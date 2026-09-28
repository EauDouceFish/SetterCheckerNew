using System.Diagnostics;
using System.Numerics;

namespace SetterChecker.Core
{
    /// <summary>按固定调用关系计算每个函数"写到了谁"的摘要，再得出真实行为。</summary>
    public sealed class EffectAnalyzer
    {
        // 解析完成后按强连通分量从被调用方到调用方计算摘要。
        /// <summary>返回请求函数的真实行为；不能证明的函数进入失败，不猜成 Getter。</summary>
        public EffectAnalysisResult Analyze(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots,
            CallTargetResolutionResult resolution, CancellationToken cancellationToken = default)
        {
            using var timing = resolution.ValueSources.Timing.Measure(AnalysisTiming.Part.Effects);
            Stopwatch watch = Stopwatch.StartNew();
            EffectAnalysisResult result = new SummaryEngine(catalog, resolution, cancellationToken).Run(roots);
            return result with { Elapsed = watch.Elapsed };
        }

        /// <summary>解析期间只记录能确定的写入槽位，用于安全剪枝；最终结论仍由摘要计算。</summary>
        internal sealed class TopTracker
        {
            private readonly MethodCatalogResult m_catalog;
            private readonly ValueSourceIndex m_values;
            private readonly Dictionary<string, TopCause> m_top = new(StringComparer.Ordinal);
            private readonly Dictionary<string, ulong> m_slots = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<(string Caller, ResolvedCallTarget Target, int Position)>> m_bindings = new(StringComparer.Ordinal);
            private readonly Queue<string> m_pending = new();
            private readonly HashSet<string> m_queued = new(StringComparer.Ordinal);
            private readonly List<string> m_settled = new();

            // 共用解析器的函数目录和值来源索引。
            internal TopTracker(MethodCatalogResult catalog, ValueSourceIndex values)
            {
                this.m_catalog = catalog;
                this.m_values = values;
            }

            internal Action<IReadOnlyList<string>>? Settled { get; set; }

            // 已证明写入静态数据或调用了这类函数，其余调用不再影响任何调用者的结论。
            internal bool IsTop(string method) => this.m_top.ContainsKey(method);

            // 读取 TOP 的首个原因，供摘要补全被剪枝函数的证据。
            internal TopCause? ReadCause(string method) => this.m_top.GetValueOrDefault(method);

            // 自身已有确定写入（当前对象、参数或静态数据），函数本身的结论已是 Setter。
            internal bool IsDecided(string method) => this.m_top.ContainsKey(method) || this.m_slots.GetValueOrDefault(method) != 0;

            // 直接写入：静态数据成为 TOP，当前对象或参数记入已确定槽位。
            internal void ReadDirect(MethodBehavior body, IEnumerable<BehaviorWrite>? reflectionWrites = null)
            {
                using var timing = this.m_values.Timing.Measure(AnalysisTiming.Part.Effects);
                if (IsTop(body.MethodId))
                {
                    return;
                }
                foreach (BehaviorWrite write in body.Writes.Concat(reflectionWrites ?? Enumerable.Empty<BehaviorWrite>()))
                {
                    if (write.IsLazyInitialization)
                    {
                        continue;
                    }
                    var (isStatic, slots) = write.ReceiverValueId is int receiver ? ClassifyRoot(body.MethodId, receiver) : (true, 0UL);
                    if (isStatic)
                    {
                        AddTop(body.MethodId, new TopCause(null, write.Point.BlockId, write.Member?.Name ?? write.Kind.ToString()));
                        break;
                    }
                    AddSlots(body.MethodId, slots);
                }
                Propagate();
            }

            // 登记调用关系，并把目标已确定的槽位换算到本调用的实参。
            internal void Bind(ResolvedCall call)
            {
                using var timing = this.m_values.Timing.Measure(AnalysisTiming.Part.Effects);
                string caller = call.CallerMethodId;
                if (call.ValuesOnly || call.RuntimeRule?.Operation == RuntimeOperation.Diagnostics)
                {
                    return;
                }
                if (call.RuntimeRule?.Operation == RuntimeOperation.WriteCollection && call.Call.ReceiverValueId is int receiver)
                {
                    var (isStatic, slots) = ClassifyRoot(caller, receiver);
                    if (isStatic)
                    {
                        AddTop(caller, new TopCause(null, call.Call.Point.BlockId, call.RuntimeRule.Description));
                    }
                    AddSlots(caller, slots);
                }
                if (call.InvokesUnboundParameter)
                {
                    AddTop(caller, new TopCause(null, call.Call.Point.BlockId, UnboundCallbackDetail));
                }
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    if (IsStandardCollectionCtor(this.m_catalog, target))
                    {
                        continue;
                    }
                    if (!this.m_bindings.TryGetValue(target.MethodId, out var bindings))
                    {
                        this.m_bindings.Add(target.MethodId, bindings = new());
                    }
                    bindings.Add((caller, target, call.Call.Point.BlockId));
                    MapBinding(caller, target, call.Call.Point.BlockId);
                }
                Propagate();
            }

            // 目标 TOP 使调用者 TOP；目标写入的槽位若对应调用者已有对象，调用者随之确定。
            private void MapBinding(string caller, ResolvedCallTarget target, int position)
            {
                if (IsTop(caller))
                {
                    return;
                }
                if (IsTop(target.MethodId))
                {
                    AddTop(caller, new TopCause(target.MethodId, position, string.Empty));
                    return;
                }
                ulong written = this.m_slots.GetValueOrDefault(target.MethodId);
                while (written != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(written);
                    written &= written - 1;
                    IReadOnlyList<BehaviorValueReference>? actual = slot == 0 ? target.Receiver
                        : slot - 1 < target.Arguments.Count ? target.Arguments[slot - 1] : null;
                    foreach (BehaviorValueReference value in actual ?? Array.Empty<BehaviorValueReference>())
                    {
                        if (value.MethodId != caller)
                        {
                            AddTop(caller, new TopCause(target.MethodId, position, string.Empty));
                            return;
                        }
                        if (value.ValueId < 0)
                        {
                            continue;
                        }
                        var (isStatic, slots) = ClassifyRoot(caller, value.ValueId);
                        if (isStatic)
                        {
                            AddTop(caller, new TopCause(target.MethodId, position, string.Empty));
                            return;
                        }
                        AddSlots(caller, slots);
                    }
                }
            }

            // 按摘要同样的规则在函数内回溯写入目标；调用结果与新对象不据此下结论。
            private (bool Static, ulong Slots) ClassifyRoot(string method, int value)
            {
                ulong slots = 0;
                Queue<(BehaviorValueReference Reference, bool Deep)> pending = new(new[] { (new BehaviorValueReference(method, value), false) });
                HashSet<(BehaviorValueReference Reference, bool Deep)> visited = new();
                while (pending.TryDequeue(out var item))
                {
                    if (!visited.Add(item))
                    {
                        continue;
                    }
                    foreach (ValueOrigin origin in this.m_values.ReadLocalOrigins(item.Reference, method, observe: false))
                    {
                        if (origin.Reference.MethodId != method)
                        {
                            continue;
                        }
                        BehaviorValue source = origin.Value;
                        BehaviorValueReference Input() => origin.Reference with { ValueId = source.InputValueIds[0] };
                        switch (source.Kind)
                        {
                            case BehaviorValueKind.CurrentInstance:
                                slots |= 1UL;
                                break;
                            case BehaviorValueKind.Parameter:
                                if (source.ParameterIndex is int parameter && parameter >= 0 && parameter < MaxParameterSlot)
                                {
                                    slots |= 1UL << (parameter + 1);
                                }
                                break;
                            case BehaviorValueKind.FieldRead:
                            case BehaviorValueKind.Address:
                                if (source.Member != null && source.InputValueIds.Count == 0)
                                {
                                    return (true, slots);
                                }
                                if (source.InputValueIds.Count != 0)
                                {
                                    pending.Enqueue((Input(), item.Deep || source.Kind == BehaviorValueKind.FieldRead && source.Member?.IsReferenceStorage != false));
                                }
                                break;
                            case BehaviorValueKind.ArrayElementRead:
                                if (source.InputValueIds.Count != 0)
                                {
                                    pending.Enqueue((Input(), true));
                                }
                                break;
                            case BehaviorValueKind.ValueCopy:
                                if (item.Deep && source.InputValueIds.Count != 0)
                                {
                                    pending.Enqueue((Input(), true));
                                }
                                break;
                        }
                    }
                }
                return (false, slots);
            }

            // 已确定槽位只增加；新增后通知调用者重新换算。
            private void AddSlots(string method, ulong slots)
            {
                ulong old = this.m_slots.GetValueOrDefault(method);
                if ((old | slots) == old || IsTop(method))
                {
                    return;
                }
                this.m_slots[method] = old | slots;
                if (old == 0)
                {
                    this.m_settled.Add(method);
                }
                if (this.m_queued.Add(method))
                {
                    this.m_pending.Enqueue(method);
                }
            }

            // TOP 只从无到有，重复原因不覆盖。
            private void AddTop(string method, TopCause cause)
            {
                if (this.m_top.TryAdd(method, cause))
                {
                    this.m_settled.Add(method);
                    if (this.m_queued.Add(method))
                    {
                        this.m_pending.Enqueue(method);
                    }
                }
            }

            // 新增事实只通知已登记的直接调用者，与登记先后无关。
            private void Propagate()
            {
                while (this.m_pending.TryDequeue(out string? method))
                {
                    this.m_queued.Remove(method);
                    if (!this.m_bindings.TryGetValue(method, out var bindings))
                    {
                        continue;
                    }
                    foreach (var (caller, target, position) in bindings.ToArray())
                    {
                        MapBinding(caller, target, position);
                    }
                }
                if (this.m_settled.Count != 0)
                {
                    string[] settled = this.m_settled.ToArray();
                    this.m_settled.Clear();
                    this.Settled?.Invoke(settled);
                }
            }
        }

        internal const string UnboundCallbackDetail = "调用未固定目标的委托参数，合法回调允许修改状态";

        // 标准 List/Dictionary 构造只初始化新容器，与原有规则一致不展开其内部实现。
        private static bool IsStandardCollectionCtor(MethodCatalogResult catalog, ResolvedCallTarget target)
        {
            if (target.Reference.Name != ".ctor")
            {
                return false;
            }
            // 只认 List/Dictionary 本身；嵌套的 Enumerator、KeyCollection 等构造会保存原集合，必须照常分析。
            string id = target.Reference.DeclaringTypeDefinitionId;
            if (catalog.TypesById.TryGetValue(id, out TypeEntry? type))
            {
                return !type.FullName.Contains('+')
                    && (type.FullName is "System.Collections.Generic.List`1" or "System.Collections.Generic.Dictionary`2"
                        || type.FullName.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal)
                        || type.FullName.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal));
            }
            return System.Text.RegularExpressions.Regex.IsMatch(id, @":System\.Collections\.Generic\.(List`1|Dictionary`2)(\||$)");
        }
        private const int StaticBit = 128;
        private const int UnknownBit = 129;
        private const int ReturnSlot = 63;
        private const int MaxParameterSlot = 62;

        /// <summary>解析完成后按调用图计算摘要，每个函数只保存一份，不按调用路径复制。</summary>
        private sealed class SummaryEngine
        {
            private readonly MethodCatalogResult m_catalog;
            private readonly CallTargetResolutionResult m_resolution;
            private readonly ValueSourceIndex m_values;
            private readonly TopTracker m_top;
            private readonly CancellationToken m_cancellation;
            private readonly IReadOnlyDictionary<string, MethodBehavior> m_bodies;
            private readonly Dictionary<(string Method, BehaviorFlowPoint Point), ResolvedCall> m_calls = new();
            private readonly Dictionary<(string Method, BehaviorFlowPoint Point), PendingCall> m_pending = new();
            private readonly Dictionary<string, MethodSummary> m_summaries = new(StringComparer.Ordinal);
            private int m_updates;
            private int m_ignoredLazyWrites;
            private int m_ignoredDiagnosticCalls;

            // 固定调用与待定调用按位置建表。
            internal SummaryEngine(MethodCatalogResult catalog, CallTargetResolutionResult resolution, CancellationToken cancellation)
            {
                this.m_catalog = catalog;
                this.m_resolution = resolution;
                this.m_values = resolution.ValueSources;
                this.m_top = resolution.Top;
                this.m_cancellation = cancellation;
                this.m_bodies = resolution.Behaviors.MethodsById;
                foreach (ResolvedCall call in resolution.Calls)
                {
                    this.m_calls[(call.CallerMethodId, call.Call.Point)] = call;
                }
                foreach (PendingCall call in resolution.PendingCalls)
                {
                    this.m_pending[(call.CallerMethodId, call.Call.Point)] = call;
                }
            }

            // 求出全部摘要后只为请求函数整理结论和证据。
            internal EffectAnalysisResult Run(IReadOnlyList<MethodEntry> roots)
            {
                SortedSet<string> nodes = new(this.m_bodies.Keys, StringComparer.Ordinal);
                Dictionary<string, SortedSet<string>> edges = new(StringComparer.Ordinal);
                foreach (ResolvedCall call in this.m_resolution.Calls)
                {
                    nodes.Add(call.CallerMethodId);
                    if (!edges.TryGetValue(call.CallerMethodId, out SortedSet<string>? targets))
                    {
                        targets = new(StringComparer.Ordinal);
                        edges.Add(call.CallerMethodId, targets);
                    }
                    foreach (ResolvedCallTarget target in call.Targets)
                    {
                        targets.Add(target.MethodId);
                        nodes.Add(target.MethodId);
                    }
                }
                foreach (MethodEntry root in roots)
                {
                    nodes.Add(root.Id);
                }
                foreach (string[] component in ReadComponents(nodes, edges))
                {
                    this.m_cancellation.ThrowIfCancellationRequested();
                    bool recursive = component.Length > 1 || CallsItself(component[0], edges);
                    while (true)
                    {
                        bool changed = false;
                        foreach (string method in component)
                        {
                            MethodSummary summary = Summarize(method);
                            this.m_updates++;
                            if (!this.m_summaries.TryGetValue(method, out MethodSummary? previous) || !previous.SameAs(summary))
                            {
                                changed = true;
                            }
                            this.m_summaries[method] = summary;
                        }
                        if (!changed || !recursive)
                        {
                            break;
                        }
                    }
                }

                List<MethodEffect> results = new();
                Dictionary<string, EffectEvidence> failures = new(StringComparer.Ordinal);
                Dictionary<string, IReadOnlyList<int>> hidden = new(StringComparer.Ordinal);
                foreach (MethodEntry root in roots.OrderBy(method => method.Id, StringComparer.Ordinal))
                {
                    MethodSummary summary = this.m_summaries[root.Id];
                    if (summary.IsSetter)
                    {
                        results.Add(new(root.Id, MethodEffectKind.Setter, ReadEvidence(root.Id, summary.EvidenceBit)));
                    }
                    else if (this.m_resolution.Failure != null)
                    {
                        failures.Add(root.Id, new(new[] { root.Id }, -1, this.m_resolution.Failure));
                    }
                    else if (summary.Unknown != null)
                    {
                        failures.Add(root.Id, ReadEvidence(root.Id, UnknownBit));
                    }
                    else
                    {
                        results.Add(new(root.Id, MethodEffectKind.Getter, null));
                    }
                    if (this.m_bodies.TryGetValue(root.Id, out MethodBehavior? body) && body.HiddenPlayerCodeLines.Count != 0)
                    {
                        hidden.Add(root.Id, body.HiddenPlayerCodeLines);
                    }
                }
                return new EffectAnalysisResult(results, TimeSpan.Zero)
                {
                    Failures = failures,
                    SummaryUpdates = this.m_updates,
                    IgnoredLazyWrites = this.m_ignoredLazyWrites,
                    IgnoredDiagnosticCalls = this.m_ignoredDiagnosticCalls,
                    HiddenPlayerCode = hidden,
                };
            }

            // 单节点分量只有自调用时才需要反复求解。
            private static bool CallsItself(string method, Dictionary<string, SortedSet<string>> edges)
                => edges.TryGetValue(method, out SortedSet<string>? targets) && targets.Contains(method);

            // 迭代版 Tarjan：起点与邻接均按身份排序，分量按被调用方优先输出。
            private static List<string[]> ReadComponents(SortedSet<string> nodes, Dictionary<string, SortedSet<string>> edges)
            {
                Dictionary<string, int> index = new(StringComparer.Ordinal);
                Dictionary<string, int> low = new(StringComparer.Ordinal);
                HashSet<string> onStack = new(StringComparer.Ordinal);
                Stack<string> stack = new();
                List<string[]> components = new();
                int next = 0;
                foreach (string start in nodes)
                {
                    if (index.ContainsKey(start))
                    {
                        continue;
                    }
                    Stack<(string Node, IEnumerator<string> Targets)> work = new();
                    index[start] = low[start] = next++;
                    stack.Push(start);
                    onStack.Add(start);
                    work.Push((start, (edges.GetValueOrDefault(start) ?? new SortedSet<string>()).GetEnumerator()));
                    while (work.Count != 0)
                    {
                        var (node, targets) = work.Peek();
                        if (targets.MoveNext())
                        {
                            string target = targets.Current;
                            if (!index.ContainsKey(target))
                            {
                                index[target] = low[target] = next++;
                                stack.Push(target);
                                onStack.Add(target);
                                work.Push((target, (edges.GetValueOrDefault(target) ?? new SortedSet<string>()).GetEnumerator()));
                            }
                            else if (onStack.Contains(target))
                            {
                                low[node] = Math.Min(low[node], index[target]);
                            }
                            continue;
                        }
                        work.Pop();
                        if (work.Count != 0)
                        {
                            string parent = work.Peek().Node;
                            low[parent] = Math.Min(low[parent], low[node]);
                        }
                        if (low[node] == index[node])
                        {
                            List<string> component = new();
                            string member;
                            do
                            {
                                member = stack.Pop();
                                onStack.Remove(member);
                                component.Add(member);
                            }
                            while (member != node);
                            component.Sort(StringComparer.Ordinal);
                            components.Add(component.ToArray());
                        }
                    }
                }
                return components;
            }

            // 同一分量尚未求出的函数按空摘要参与迭代。
            private MethodSummary ReadSummary(string method) => this.m_summaries.GetValueOrDefault(method) ?? MethodSummary.Empty;

            // 从函数自身写入、调用和返回计算一份完整摘要。
            private MethodSummary Summarize(string method)
            {
                MethodSummary summary = new();
                if (!this.m_bodies.TryGetValue(method, out MethodBehavior? body))
                {
                    summary.SetUnknown(new SummaryCause(new BehaviorFlowPoint(-1, 0), null, -1, "函数体尚未读取"), "函数体尚未读取");
                    summary.Return.Unknown = "函数体尚未读取";
                    ApplyTop(method, summary);
                    return summary;
                }
                string? failure = body.Failure ?? (body.BodyKind != MethodBodyKind.Executable
                    ? body.NativeBoundary?.ToString() ?? $"没有可读取的托管函数体：{body.BodyKind}" : null);
                if (failure != null)
                {
                    summary.SetUnknown(new SummaryCause(new BehaviorFlowPoint(-1, 0), null, -1, failure), failure);
                    summary.Return.Unknown = failure;
                    ApplyTop(method, summary);
                    return summary;
                }

                MethodState state = new(method);
                foreach (BehaviorWrite write in body.Writes.Concat(this.m_values.ReadReflectionWrites(method)))
                {
                    if (write.IsLazyInitialization)
                    {
                        this.m_ignoredLazyWrites++;
                        continue;
                    }
                    IReadOnlyList<EffectRoot> targets = write.ReceiverValueId is int receiver
                        ? ReadRoots(state, receiver, false) : new[] { EffectRoot.Static };
                    SummaryCause cause = new(write.Point, null, -1, write.Member?.Name ?? write.Kind.ToString());
                    foreach (EffectRoot target in targets)
                    {
                        state.Effects.Add((target, false, cause));
                    }
                    // 值类型字段保存的是拷贝，不会成为被写对象的别名。
                    IReadOnlyList<EffectRoot> values = write.Member?.IsReferenceStorage == false
                        ? Array.Empty<EffectRoot>() : ReadRoots(state, write.ValueId, false);
                    Store(state, targets, values);
                    if (write.Kind == BehaviorWriteKind.Indirect)
                    {
                        foreach (EffectRoot target in targets.Where(target => target.Kind == EffectRootKind.Parameter && !target.Deep))
                        {
                            if (!state.Outs.TryGetValue(target.Slot - 1, out List<EffectRoot>? outs))
                            {
                                state.Outs.Add(target.Slot - 1, outs = new());
                            }
                            outs.AddRange(values);
                        }
                    }
                }
                foreach (BehaviorCall call in body.Calls)
                {
                    ReadCall(state, call);
                }
                foreach (BehaviorReturn returned in body.Returns.Where(item => item.ValueId.HasValue))
                {
                    state.Returns.AddRange(ReadRoots(state, returned.ValueId!.Value, false));
                }

                foreach (var (root, deep, cause) in state.Effects)
                {
                    Apply(state, summary, root, deep, cause, new HashSet<int>());
                }
                foreach (var (slot, values) in state.Stores)
                {
                    summary.AddInto(slot, SlotsOf(state, values));
                }
                summary.Return = ReadValueSummary(state, state.Returns, out ulong intoReturn);
                summary.AddInto(ReturnSlot, intoReturn);
                foreach (var (index, values) in state.Outs.OrderBy(pair => pair.Key))
                {
                    ValueSummary written = ReadValueSummary(state, values, out ulong alias);
                    written.DeepSlots |= alias & ~(1UL << ReturnSlot);
                    written.Static |= (alias & (1UL << ReturnSlot)) != 0;
                    (summary.Outs ??= new()).Add(index, written);
                }
                ApplyTop(method, summary);
                return summary;
            }

            // 解析器已证明的 TOP 必须保留，被剪枝的调用不能让函数变成 Getter。
            private void ApplyTop(string method, MethodSummary summary)
            {
                if (!summary.Static && this.m_top.ReadCause(method) is TopCause cause)
                {
                    summary.SetBit(StaticBit, new SummaryCause(new BehaviorFlowPoint(cause.Position, 0), cause.Callee,
                        cause.Callee == null ? -1 : StaticBit, cause.Detail));
                }
            }

            // 一次调用：运行时规则、未绑定回调或把被调函数摘要换算到本函数的实参。
            private void ReadCall(MethodState state, BehaviorCall call)
            {
                string method = state.Method;
                this.m_pending.TryGetValue((method, call.Point), out PendingCall? pending);
                if (!this.m_calls.TryGetValue((method, call.Point), out ResolvedCall? resolved))
                {
                    if (pending != null)
                    {
                        state.Effects.Add((EffectRoot.UnknownOf(pending.Failure ?? "调用目标尚未确定"), false, new(call.Point, null, -1, pending.Failure ?? "调用目标尚未确定")));
                    }
                    else if (!this.m_top.IsTop(method))
                    {
                        state.Effects.Add((EffectRoot.UnknownOf("调用目标尚未确定"), false, new(call.Point, null, -1, "调用目标尚未确定")));
                    }
                    return;
                }
                string? failure = pending?.Failure ?? resolved.Failure;
                if (failure != null)
                {
                    state.Effects.Add((EffectRoot.UnknownOf(failure), false, new(call.Point, null, -1, failure)));
                }
                if (resolved.RuntimeRule is RuntimeOperationRule rule)
                {
                    ReadRuntimeCall(state, call, rule);
                    if (resolved.Targets.Count == 0)
                    {
                        return;
                    }
                }
                if (resolved.InvokesUnboundParameter)
                {
                    state.Effects.Add((EffectRoot.Static, false, new(call.Point, null, -1, UnboundCallbackDetail)));
                }
                if (resolved.ValuesOnly)
                {
                    return;
                }
                foreach (ResolvedCallTarget target in resolved.Targets)
                {
                    if (IsStandardCollectionConstructor(target))
                    {
                        // 标准集合构造不修改已有对象，但复制构造会把实参中的对象引用存进新集合。
                        Store(state, ReadActualRoots(state, target, 0, false),
                            call.Arguments.Where(argument => !IsValueTypeValue(state.Method, argument.ValueId))
                                .SelectMany(argument => ReadRoots(state, argument.ValueId, false)).ToArray());
                        continue;
                    }
                    MethodSummary callee = ReadSummary(target.MethodId);
                    if (callee.Static)
                    {
                        state.Effects.Add((EffectRoot.Static, false, new(call.Point, target.MethodId, StaticBit, string.Empty)));
                    }
                    if (callee.Unknown != null)
                    {
                        state.Effects.Add((EffectRoot.UnknownOf(callee.Unknown), false, new(call.Point, target.MethodId, UnknownBit, string.Empty)));
                    }
                    foreach (int slot in Bits(callee.Shallow))
                    {
                        MapSlot(state, call, target, slot, false, new(call.Point, target.MethodId, slot, string.Empty));
                    }
                    foreach (int slot in Bits(callee.Deep))
                    {
                        MapSlot(state, call, target, slot, true, new(call.Point, target.MethodId, 64 + slot, string.Empty));
                    }
                    if (callee.Into == null)
                    {
                        continue;
                    }
                    foreach (var (destination, sources) in callee.Into.OrderBy(pair => pair.Key))
                    {
                        List<EffectRoot> values = new();
                        foreach (int source in Bits(sources))
                        {
                            if (source == ReturnSlot)
                            {
                                values.Add(EffectRoot.Static);
                                continue;
                            }
                            values.AddRange(ReadActualRoots(state, target, source, false));
                        }
                        if (destination == ReturnSlot)
                        {
                            if (call.ResultValueId is int result)
                            {
                                state.AddFreshSources(result, values);
                            }
                            continue;
                        }
                        Store(state, ReadActualRoots(state, target, destination, false), values);
                    }
                }
            }

            // 运行时规则只描述集合写入和 out 写出；诊断调用不计效果。
            private void ReadRuntimeCall(MethodState state, BehaviorCall call, RuntimeOperationRule rule)
            {
                SummaryCause cause = new(call.Point, null, -1, rule.Description);
                if (rule.Operation == RuntimeOperation.Diagnostics)
                {
                    this.m_ignoredDiagnosticCalls++;
                    return;
                }
                if (rule.Operation == RuntimeOperation.WriteCollection)
                {
                    IReadOnlyList<EffectRoot> receiver = call.ReceiverValueId is int value
                        ? ReadRoots(state, value, false) : new[] { EffectRoot.UnknownOf("集合写入没有接收对象") };
                    foreach (EffectRoot root in receiver)
                    {
                        state.Effects.Add((root, false, cause));
                    }
                    Store(state, receiver, call.Arguments.Where(argument => !IsValueTypeValue(state.Method, argument.ValueId))
                        .SelectMany(argument => ReadRoots(state, argument.ValueId, false)).ToArray());
                    return;
                }
                if (rule.Operation is not (RuntimeOperation.ReadCollection or RuntimeOperation.ConvertValue))
                {
                    return;
                }
                foreach (BehaviorArgument argument in call.Arguments.Where(argument => argument.RefKind == Microsoft.CodeAnalysis.RefKind.Out))
                {
                    foreach (EffectRoot root in ReadRoots(state, argument.ValueId, false))
                    {
                        state.Effects.Add((root, false, cause));
                    }
                }
            }

            // 值类型的值只能是拷贝，不会成为别名；类型未知时按引用处理。
            private bool IsValueTypeValue(string method, int valueId)
            {
                if (!this.m_bodies.TryGetValue(method, out MethodBehavior? body) || valueId < 0 || valueId >= body.Values.Count
                    || body.Values[valueId].Type is not BehaviorTypeReference type || type.Id.StartsWith('!'))
                {
                    return false;
                }
                try
                {
                    return this.m_catalog.ResolveTypeDefinition(type).IsValueType;
                }
                catch (AnalysisException)
                {
                    return false;
                }
            }

            // 被调函数写入的槽位对应到本调用的实参；实参属于其他函数时按静态处理。
            private void MapSlot(MethodState state, BehaviorCall call, ResolvedCallTarget target, int slot, bool deep, SummaryCause cause)
            {
                foreach (EffectRoot root in ReadActualRoots(state, target, slot, false))
                {
                    state.Effects.Add((root, deep, cause));
                }
            }

            // 读取目标函数某个槽位对应实参的根。
            private IReadOnlyList<EffectRoot> ReadActualRoots(MethodState state, ResolvedCallTarget target, int slot, bool deep)
            {
                IReadOnlyList<BehaviorValueReference>? actual = slot == 0 ? target.Receiver
                    : slot - 1 < target.Arguments.Count ? target.Arguments[slot - 1] : null;
                if (actual == null)
                {
                    return new[] { EffectRoot.UnknownOf("调用实参与目标参数不对应") };
                }
                List<EffectRoot> roots = new();
                foreach (BehaviorValueReference value in actual)
                {
                    if (value.MethodId != state.Method)
                    {
                        roots.Add(EffectRoot.Foreign);
                        continue;
                    }
                    if (value.ValueId < 0)
                    {
                        roots.Add(EffectRoot.UnknownOf("实参来源尚未确定"));
                        continue;
                    }
                    roots.AddRange(ReadRoots(state, value.ValueId, deep));
                }
                return roots;
            }

            // 存储关系：写进新对象的值成为其别名，写进当前对象或参数的值记入摘要。
            private static void Store(MethodState state, IEnumerable<EffectRoot> targets, IReadOnlyList<EffectRoot> values)
            {
                if (values.Count == 0)
                {
                    return;
                }
                foreach (EffectRoot target in targets)
                {
                    if (target.Kind == EffectRootKind.Fresh)
                    {
                        state.AddFreshSources(target.Site, values);
                    }
                    else if (target.Kind is EffectRootKind.Receiver or EffectRootKind.Parameter)
                    {
                        state.Stores.Add((target.Slot, values));
                    }
                }
            }

            // 把一个根上的写入落到摘要；经由新对象的深写传到它的别名根。
            private void Apply(MethodState state, MethodSummary summary, EffectRoot root, bool deepWrite, SummaryCause cause, HashSet<int> visiting)
            {
                bool deep = deepWrite || root.Deep;
                switch (root.Kind)
                {
                    case EffectRootKind.Receiver:
                    case EffectRootKind.Parameter:
                        summary.SetBit(deep ? 64 + root.Slot : root.Slot, cause);
                        break;
                    case EffectRootKind.Static:
                    case EffectRootKind.Foreign:
                        summary.SetBit(StaticBit, cause);
                        break;
                    case EffectRootKind.Fresh:
                        if (deep && visiting.Add(root.Site))
                        {
                            foreach (EffectRoot alias in ReadAliasRoots(state, root.Site))
                            {
                                Apply(state, summary, alias, true, cause, visiting);
                            }
                        }
                        break;
                    case EffectRootKind.Unknown:
                        string reason = root.Reason ?? cause.Detail;
                        summary.SetUnknown(cause.CalleeId == null ? cause with { Detail = reason } : cause, reason);
                        break;
                }
            }

            // 新对象中可能存着的本函数已有对象；嵌套新对象递归展开。
            private IReadOnlyList<EffectRoot> ReadAliasRoots(MethodState state, int site)
            {
                if (state.Aliases.TryGetValue(site, out List<EffectRoot>? known))
                {
                    return known;
                }
                List<EffectRoot> result = new();
                state.Aliases.Add(site, result);
                HashSet<EffectRoot> seen = new();
                Stack<int> pending = new(new[] { site });
                HashSet<int> sites = new();
                while (pending.TryPop(out int current))
                {
                    if (!sites.Add(current) || !state.FreshSources.TryGetValue(current, out List<EffectRoot>? sources))
                    {
                        continue;
                    }
                    foreach (EffectRoot source in sources.ToArray())
                    {
                        if (source.Kind == EffectRootKind.Fresh)
                        {
                            pending.Push(source.Site);
                        }
                        else if (source.Kind != EffectRootKind.Local && seen.Add(source with { Deep = true }))
                        {
                            result.Add(source with { Deep = true });
                        }
                    }
                }
                return result;
            }

            // 返回值或 out 写出值的来源说明；新对象的别名槽位单独返回。
            private ValueSummary ReadValueSummary(MethodState state, IEnumerable<EffectRoot> roots, out ulong alias)
            {
                ValueSummary summary = new();
                alias = 0;
                foreach (EffectRoot root in roots)
                {
                    switch (root.Kind)
                    {
                        case EffectRootKind.Receiver:
                        case EffectRootKind.Parameter:
                            if (root.Deep)
                            {
                                summary.DeepSlots |= 1UL << root.Slot;
                            }
                            else
                            {
                                summary.Slots |= 1UL << root.Slot;
                            }
                            break;
                        case EffectRootKind.Static:
                            summary.Static = true;
                            break;
                        case EffectRootKind.Fresh:
                            summary.Fresh = true;
                            alias |= SlotsOf(state, ReadAliasRoots(state, root.Site));
                            break;
                        case EffectRootKind.Foreign:
                            summary.Unknown ??= "返回值来自其他函数的对象";
                            break;
                        case EffectRootKind.Unknown:
                            summary.Unknown ??= root.Reason ?? "返回值来源尚未确定";
                            break;
                    }
                }
                return summary;
            }

            // 根集合换算为槽位掩码；静态、外部或未知来源记为第 63 位。
            private ulong SlotsOf(MethodState state, IEnumerable<EffectRoot> roots)
            {
                ulong mask = 0;
                foreach (EffectRoot root in roots)
                {
                    mask |= root.Kind switch
                    {
                        EffectRootKind.Receiver or EffectRootKind.Parameter => 1UL << root.Slot,
                        EffectRootKind.Static or EffectRootKind.Foreign or EffectRootKind.Unknown => 1UL << ReturnSlot,
                        EffectRootKind.Fresh => SlotsOf(state, ReadAliasRoots(state, root.Site)),
                        _ => 0,
                    };
                }
                return mask;
            }

            // 值在本函数内的最终来源；调用结果用被调函数的返回摘要换算。
            private IReadOnlyList<EffectRoot> ReadRoots(MethodState state, int valueId, bool deep)
            {
                if (state.Roots.TryGetValue((valueId, deep), out List<EffectRoot>? known))
                {
                    return known;
                }
                List<EffectRoot> result = new();
                state.Roots.Add((valueId, deep), result);
                HashSet<EffectRoot> seen = new();
                void Add(EffectRoot root)
                {
                    if (seen.Add(root))
                    {
                        result.Add(root);
                    }
                }
                Queue<(BehaviorValueReference Reference, bool Deep)> pending = new();
                HashSet<(BehaviorValueReference Reference, bool Deep)> visited = new();
                pending.Enqueue((new BehaviorValueReference(state.Method, valueId), deep));
                while (pending.TryDequeue(out var item))
                {
                    if (!visited.Add(item))
                    {
                        continue;
                    }
                    bool d = item.Deep;
                    foreach (ValueOrigin origin in this.m_values.ReadLocalOrigins(item.Reference, state.Method, observe: false))
                    {
                        BehaviorValue value = origin.Value;
                        if (origin.Reference.MethodId != state.Method)
                        {
                            if (value.Kind is not (BehaviorValueKind.Local or BehaviorValueKind.Constant))
                            {
                                Add(EffectRoot.Foreign);
                            }
                            continue;
                        }
                        BehaviorValueReference Input(int index) => origin.Reference with { ValueId = value.InputValueIds[index] };
                        switch (value.Kind)
                        {
                            case BehaviorValueKind.CurrentInstance:
                                Add(new(EffectRootKind.Receiver, 0, 0, d, null));
                                break;
                            case BehaviorValueKind.Parameter:
                                if (value.ParameterIndex is int parameter && parameter >= 0 && parameter < MaxParameterSlot)
                                {
                                    Add(new(EffectRootKind.Parameter, parameter + 1, 0, d, null));
                                }
                                else
                                {
                                    Add(EffectRoot.UnknownOf("参数超出摘要可表示范围"));
                                }
                                break;
                            case BehaviorValueKind.Local:
                            case BehaviorValueKind.Constant:
                            case BehaviorValueKind.Computation:
                            case BehaviorValueKind.Type:
                            case BehaviorValueKind.Function:
                            case BehaviorValueKind.StackAllocation:
                                break;
                            case BehaviorValueKind.NewObject:
                            case BehaviorValueKind.NewArray:
                            case BehaviorValueKind.Iterator:
                                Add(new(EffectRootKind.Fresh, 0, origin.Reference.ValueId, d, null));
                                break;
                            case BehaviorValueKind.ShallowCopy:
                                Add(new(EffectRootKind.Fresh, 0, origin.Reference.ValueId, d, null));
                                if (!state.CopySources.Contains(origin.Reference.ValueId))
                                {
                                    state.CopySources.Add(origin.Reference.ValueId);
                                    List<EffectRoot> copied = new();
                                    foreach (BehaviorValueReference source in origin.BoundReceiver
                                        ?? value.InputValueIds.Select(input => origin.Reference with { ValueId = input }).ToArray())
                                    {
                                        copied.AddRange(source.MethodId != state.Method ? new[] { EffectRoot.Foreign }
                                            : source.ValueId < 0 ? new[] { EffectRoot.UnknownOf("浅复制来源尚未确定") }
                                            : ReadRoots(state, source.ValueId, true));
                                    }
                                    state.AddFreshSources(origin.Reference.ValueId, copied);
                                }
                                break;
                            case BehaviorValueKind.FieldRead:
                                if (value.InputValueIds.Count == 0)
                                {
                                    Add(value.Member != null ? EffectRoot.Static : EffectRoot.UnknownOf(value.Reference ?? "写入对象来源尚未确定"));
                                }
                                else
                                {
                                    pending.Enqueue((Input(0), d || value.Member?.IsReferenceStorage != false));
                                }
                                break;
                            case BehaviorValueKind.ArrayElementRead:
                                if (value.InputValueIds.Count == 0)
                                {
                                    Add(EffectRoot.UnknownOf("写入对象来源尚未确定"));
                                }
                                else
                                {
                                    pending.Enqueue((Input(0), true));
                                }
                                break;
                            case BehaviorValueKind.Address:
                                if (value.Member != null && value.InputValueIds.Count == 0)
                                {
                                    Add(EffectRoot.Static);
                                }
                                else if (value.InputValueIds.Count != 0)
                                {
                                    pending.Enqueue((Input(0), d));
                                    // 经局部变量地址做深写时，要追到赋给该变量的值（例如结构体枚举器中保存的原集合）。
                                    if (d && value.Member == null && this.m_bodies.TryGetValue(origin.Reference.MethodId, out MethodBehavior? owner))
                                    {
                                        foreach (BehaviorAssignment assignment in owner.Assignments.Where(item => item.TargetValueId == value.InputValueIds[0]))
                                        {
                                            pending.Enqueue((origin.Reference with { ValueId = assignment.ValueId }, true));
                                        }
                                    }
                                }
                                else if (d)
                                {
                                    Add(EffectRoot.UnknownOf("写入对象来源尚未确定"));
                                }
                                break;
                            case BehaviorValueKind.ValueCopy:
                                if (d)
                                {
                                    foreach (BehaviorValueReference source in origin.BoundReceiver
                                        ?? value.InputValueIds.Select(input => origin.Reference with { ValueId = input }).ToArray())
                                    {
                                        pending.Enqueue((source, true));
                                    }
                                }
                                break;
                            case BehaviorValueKind.CallResult:
                                foreach (var mapped in ReadCallResult(state, origin, d))
                                {
                                    if (mapped.Reference is BehaviorValueReference next)
                                    {
                                        pending.Enqueue((next, mapped.Deep));
                                    }
                                    else
                                    {
                                        Add(mapped.Root!.Value);
                                    }
                                }
                                break;
                            default:
                                Add(EffectRoot.UnknownOf("写入对象来源尚未确定"));
                                break;
                        }
                    }
                }
                return result;
            }

            // 调用结果按被调函数的返回（或 out 写出）摘要对应到本调用的实参。
            private IEnumerable<(BehaviorValueReference? Reference, bool Deep, EffectRoot? Root)> ReadCallResult(MethodState state, ValueOrigin origin, bool deep)
            {
                if (!this.m_values.TryReadValueCall(origin.Reference, out BehaviorCall? call))
                {
                    yield return (null, false, EffectRoot.UnknownOf("调用结果来源尚未确定"));
                    yield break;
                }
                this.m_pending.TryGetValue((state.Method, call!.Point), out PendingCall? pending);
                if (!this.m_calls.TryGetValue((state.Method, call.Point), out ResolvedCall? resolved))
                {
                    yield return (null, false, EffectRoot.UnknownOf(pending?.Failure ?? "调用目标尚未确定"));
                    yield break;
                }
                string? failure = pending?.Failure ?? resolved.Failure;
                if (failure != null)
                {
                    yield return (null, false, EffectRoot.UnknownOf(failure));
                }
                if (resolved.RuntimeRule != null || resolved.InvokesUnboundParameter && resolved.Targets.Count == 0)
                {
                    if (resolved.InvokesUnboundParameter)
                    {
                        yield return (null, false, EffectRoot.UnknownOf("未固定回调的返回值来源尚未确定"));
                    }
                    yield break;
                }
                int? output = origin.Value.ParameterIndex;
                foreach (ResolvedCallTarget target in resolved.Targets)
                {
                    MethodSummary callee = ReadSummary(target.MethodId);
                    ValueSummary? returned = output is int index
                        ? callee.Outs != null && callee.Outs.TryGetValue(index, out ValueSummary written) ? written : null
                        : callee.Return;
                    if (returned is not ValueSummary value)
                    {
                        yield return (null, false, EffectRoot.UnknownOf("out 参数写出来源尚未确定"));
                        continue;
                    }
                    if (value.Unknown != null)
                    {
                        yield return (null, false, EffectRoot.UnknownOf(value.Unknown));
                    }
                    if (value.Static)
                    {
                        yield return (null, false, EffectRoot.Static);
                    }
                    if (value.Fresh)
                    {
                        yield return (null, false, new EffectRoot(EffectRootKind.Fresh, 0, origin.Reference.ValueId, deep, null));
                    }
                    foreach (var (mask, forceDeep) in new[] { (value.Slots, false), (value.DeepSlots, true) })
                    {
                        foreach (int slot in Bits(mask))
                        {
                            IReadOnlyList<BehaviorValueReference>? actual = slot == 0 ? target.Receiver
                                : slot - 1 < target.Arguments.Count ? target.Arguments[slot - 1] : null;
                            if (actual == null)
                            {
                                yield return (null, false, EffectRoot.UnknownOf("调用实参与目标参数不对应"));
                                continue;
                            }
                            foreach (BehaviorValueReference input in actual)
                            {
                                yield return input.MethodId != state.Method ? (null, false, EffectRoot.Foreign)
                                    : input.ValueId < 0 ? (null, false, EffectRoot.UnknownOf("实参来源尚未确定"))
                                    : (input, deep || forceDeep, null);
                            }
                        }
                    }
                }
            }

            // 标准 List/Dictionary 构造只初始化新容器。
            private bool IsStandardCollectionConstructor(ResolvedCallTarget target) => IsStandardCollectionCtor(this.m_catalog, target);

            // 沿首个原因还原一条调用证据链。
            private EffectEvidence ReadEvidence(string method, int bit)
            {
                List<string> path = new();
                HashSet<(string Method, int Bit)> visited = new();
                string current = method;
                int currentBit = bit;
                while (true)
                {
                    path.Add(current);
                    SummaryCause? cause = this.m_summaries.GetValueOrDefault(current)?.Causes[currentBit];
                    if (cause == null)
                    {
                        return new(path, -1, string.Empty);
                    }
                    if (cause.CalleeId == null || cause.CalleeBit < 0 || !visited.Add((cause.CalleeId, cause.CalleeBit))
                        || !this.m_summaries.ContainsKey(cause.CalleeId))
                    {
                        return new(path, cause.Point.BlockId, cause.Detail);
                    }
                    current = cause.CalleeId;
                    currentBit = cause.CalleeBit;
                }
            }

            // 枚举掩码中的置位。
            private static IEnumerable<int> Bits(ulong mask)
            {
                while (mask != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    yield return bit;
                    mask &= mask - 1;
                }
            }
        }

        /// <summary>一次摘要计算内的临时状态，只属于当前函数。</summary>
        private sealed class MethodState(string method)
        {
            internal string Method { get; } = method;
            internal Dictionary<(int Value, bool Deep), List<EffectRoot>> Roots { get; } = new();
            internal List<(EffectRoot Root, bool Deep, SummaryCause Cause)> Effects { get; } = new();
            internal List<(int Slot, IReadOnlyList<EffectRoot> Values)> Stores { get; } = new();
            internal Dictionary<int, List<EffectRoot>> FreshSources { get; } = new();
            internal Dictionary<int, List<EffectRoot>> Aliases { get; } = new();
            internal HashSet<int> CopySources { get; } = new();
            internal List<EffectRoot> Returns { get; } = new();
            internal Dictionary<int, List<EffectRoot>> Outs { get; } = new();

            // 新对象的别名来源只增加，已求出的别名缓存随之作废。
            internal void AddFreshSources(int site, IEnumerable<EffectRoot> values)
            {
                if (!this.FreshSources.TryGetValue(site, out List<EffectRoot>? sources))
                {
                    this.FreshSources.Add(site, sources = new());
                }
                sources.AddRange(values);
                this.Aliases.Clear();
            }
        }

        /// <summary>解析期 TOP 的首个原因。</summary>
        internal sealed record TopCause(string? Callee, int Position, string Detail);

        /// <summary>值在本函数内的最终来源。</summary>
        private readonly record struct EffectRoot(EffectRootKind Kind, int Slot, int Site, bool Deep, string? Reason)
        {
            internal static EffectRoot Static => new(EffectRootKind.Static, 0, 0, false, null);
            internal static EffectRoot Foreign => new(EffectRootKind.Foreign, 0, 0, false, null);
            internal static EffectRoot UnknownOf(string reason) => new(EffectRootKind.Unknown, 0, 0, false, reason);
        }

        private enum EffectRootKind { Local, Receiver, Parameter, Static, Fresh, Foreign, Unknown }

        /// <summary>摘要某一位的首个原因：直接写入、调用某函数或未确定。</summary>
        private sealed record SummaryCause(BehaviorFlowPoint Point, string? CalleeId, int CalleeBit, string Detail)
        {
            // 位置在前的原因优先，同位置按被调函数身份。
            internal bool Precedes(SummaryCause other)
            {
                int compare = this.Point.BlockId.CompareTo(other.Point.BlockId);
                if (compare == 0)
                {
                    compare = this.Point.Order.CompareTo(other.Point.Order);
                }
                if (compare == 0)
                {
                    compare = StringComparer.Ordinal.Compare(this.CalleeId ?? string.Empty, other.CalleeId ?? string.Empty);
                }
                if (compare == 0)
                {
                    compare = this.CalleeBit.CompareTo(other.CalleeBit);
                }
                return compare < 0;
            }
        }

        /// <summary>返回值或 out 写出值的来源。</summary>
        private struct ValueSummary
        {
            internal ulong Slots;
            internal ulong DeepSlots;
            internal bool Fresh;
            internal bool Static;
            internal string? Unknown;

            // 比较来源类别，原因文本不参与收敛判断。
            internal readonly bool SameAs(ValueSummary other) => this.Slots == other.Slots && this.DeepSlots == other.DeepSlots
                && this.Fresh == other.Fresh && this.Static == other.Static && (this.Unknown == null) == (other.Unknown == null);
        }

        /// <summary>一个函数的效果摘要：浅写与深写的槽位、静态写入、未知原因、存储关系、返回和 out 来源。</summary>
        private sealed class MethodSummary
        {
            internal static readonly MethodSummary Empty = new();
            internal ulong Shallow;
            internal ulong Deep;
            internal bool Static;
            internal string? Unknown;
            internal Dictionary<int, ulong>? Into;
            internal ValueSummary Return;
            internal Dictionary<int, ValueSummary>? Outs;
            internal readonly SummaryCause?[] Causes = new SummaryCause?[130];

            internal bool IsSetter => this.Static || this.Shallow != 0 || this.Deep != 0;

            // 证据优先取静态写入，其次最低的浅写、深写槽位。
            internal int EvidenceBit => this.Static ? StaticBit
                : this.Shallow != 0 ? BitOperations.TrailingZeroCount(this.Shallow)
                : 64 + BitOperations.TrailingZeroCount(this.Deep);

            // 置位并保留位置最前的原因。
            internal void SetBit(int bit, SummaryCause cause)
            {
                if (bit == StaticBit)
                {
                    this.Static = true;
                }
                else if (bit >= 64)
                {
                    this.Deep |= 1UL << (bit - 64);
                }
                else
                {
                    this.Shallow |= 1UL << bit;
                }
                if (this.Causes[bit] is not SummaryCause old || cause.Precedes(old))
                {
                    this.Causes[bit] = cause;
                }
            }

            // 未确定原因只保留位置最前的一条，文本为最终来源处的原因。
            internal void SetUnknown(SummaryCause cause, string reason)
            {
                if (this.Causes[UnknownBit] is not SummaryCause old || cause.Precedes(old))
                {
                    this.Causes[UnknownBit] = cause;
                    this.Unknown = reason;
                }
            }

            // 合并存储关系掩码。
            internal void AddInto(int slot, ulong sources)
            {
                if (sources == 0)
                {
                    return;
                }
                this.Into ??= new();
                this.Into[slot] = this.Into.GetValueOrDefault(slot) | sources;
            }

            // 收敛只比较会影响调用者的内容。
            internal bool SameAs(MethodSummary other)
            {
                if (this.Shallow != other.Shallow || this.Deep != other.Deep || this.Static != other.Static
                    || (this.Unknown == null) != (other.Unknown == null) || !this.Return.SameAs(other.Return))
                {
                    return false;
                }
                if ((this.Into?.Count ?? 0) != (other.Into?.Count ?? 0) || (this.Outs?.Count ?? 0) != (other.Outs?.Count ?? 0))
                {
                    return false;
                }
                foreach (var (slot, mask) in this.Into ?? new())
                {
                    if (other.Into!.GetValueOrDefault(slot) != mask)
                    {
                        return false;
                    }
                }
                foreach (var (index, value) in this.Outs ?? new())
                {
                    if (!other.Outs!.TryGetValue(index, out ValueSummary written) || !written.SameAs(value))
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }

    /// <summary>函数修改能力，不是日志标签决定。</summary>
    public enum MethodEffectKind
    {
        /// <summary>没有外部修改，必要调用均已确定。</summary>
        Getter,
        /// <summary>具有修改外部对象的能力。</summary>
        Setter,
    }

    /// <summary>交付时生成的一条修改证据及位置。</summary>
    public sealed record EffectEvidence(IReadOnlyList<string> MethodPath, int Position, string Detail);

    /// <summary>函数的真实判断及证据。</summary>
    public sealed record MethodEffect(string MethodId, MethodEffectKind Kind, EffectEvidence? Evidence);

    /// <summary>真实行为的判断结果、失败和规则统计。</summary>
    public sealed record EffectAnalysisResult(IReadOnlyList<MethodEffect> Methods, TimeSpan Elapsed)
    {
        /// <summary>真实行为尚未确定的函数。</summary>
        public IReadOnlyDictionary<string, EffectEvidence> Failures { get; init; } = new Dictionary<string, EffectEvidence>();
        /// <summary>摘要计算次数。</summary>
        public int SummaryUpdates { get; init; }
        /// <summary>按懒加载规则忽略的写入次数。</summary>
        public int IgnoredLazyWrites { get; init; }
        /// <summary>按诊断边界忽略的调用次数。</summary>
        public int IgnoredDiagnosticCalls { get; init; }
        /// <summary>被编辑器宏隐藏了真机代码的函数及行号。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<int>> HiddenPlayerCode { get; init; } = new Dictionary<string, IReadOnlyList<int>>();
    }
}
