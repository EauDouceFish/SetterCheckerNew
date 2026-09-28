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

        /// <summary>解析期间只记录能确定的写入槽位，用于安全剪枝；归属规则与摘要一致，最终结论仍由摘要计算。</summary>
        internal sealed class TopTracker
        {
            private readonly MethodCatalogResult m_catalog;
            private readonly ValueSourceIndex m_values;
            private readonly Dictionary<string, TopCause> m_top = new(StringComparer.Ordinal);
            private readonly Dictionary<string, (ulong Open, ulong Combat)> m_slots = new(StringComparer.Ordinal);
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

            // 已证明写入战斗静态数据或调用了这类函数，其余调用不再影响任何调用者的结论。
            internal bool IsTop(string method) => this.m_top.ContainsKey(method);

            // 读取 TOP 的首个原因，供摘要补全被剪枝函数的证据。
            internal TopCause? ReadCause(string method) => this.m_top.GetValueOrDefault(method);

            // 自身已有确定写入（当前对象、参数或战斗静态数据），函数本身的结论已是 Setter。
            internal bool IsDecided(string method) => this.m_top.ContainsKey(method)
                || this.m_slots.GetValueOrDefault(method) is var slots && (slots.Open | slots.Combat) != 0;

            // 直接写入：战斗静态数据成为 TOP，当前对象或参数记入已确定槽位；外部库函数按通用模型。
            internal void ReadDirect(MethodBehavior body, IEnumerable<BehaviorWrite>? reflectionWrites = null)
            {
                using var timing = this.m_values.Timing.Measure(AnalysisTiming.Part.Effects);
                if (IsTop(body.MethodId))
                {
                    return;
                }
                if (body.BodyKind == MethodBodyKind.LibraryModel)
                {
                    AddSlots(body.MethodId, RuntimeOperations.ReadLibraryEffect(this.m_catalog, this.m_values.ReadMethod(body.MethodId)).Written, 0);
                    Propagate();
                    return;
                }
                foreach (BehaviorWrite write in body.Writes.Concat(reflectionWrites ?? Enumerable.Empty<BehaviorWrite>()))
                {
                    if (write.IsLazyInitialization)
                    {
                        continue;
                    }
                    var (owner, pending) = ReadWriteOwner(this.m_catalog, write);
                    var (isTop, open, combat) = write.ReceiverValueId is int receiver ? ClassifyRoot(body.MethodId, receiver, false, owner, pending)
                        : (ReadStaticOwner(this.m_catalog, Owner.Open, write.Member) == Owner.Combat, 0UL, 0UL);
                    if (isTop)
                    {
                        AddTop(body.MethodId, new TopCause(null, write.Point.BlockId, write.Member?.Name ?? write.Kind.ToString()));
                        break;
                    }
                    AddSlots(body.MethodId, open, combat);
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
                    var (isTop, open, combat) = ClassifyRoot(caller, receiver, false, Owner.Open, false);
                    if (isTop)
                    {
                        AddTop(caller, new TopCause(null, call.Call.Point.BlockId, call.RuntimeRule.Description));
                    }
                    AddSlots(caller, open, combat);
                }
                if (call.InvokesUnboundParameter)
                {
                    AddTop(caller, new TopCause(null, call.Call.Point.BlockId, UnboundCallbackDetail));
                }
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    if (!this.m_bindings.TryGetValue(target.MethodId, out var bindings))
                    {
                        this.m_bindings.Add(target.MethodId, bindings = new());
                    }
                    bindings.Add((caller, target, call.Call.Point.BlockId));
                    MapBinding(caller, target, call.Call.Point.BlockId);
                }
                Propagate();
            }

            // 目标 TOP 使调用者 TOP；目标写入的槽位按同一归属规则对应到调用者已有对象。
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
                var (targetOpen, targetCombat) = this.m_slots.GetValueOrDefault(target.MethodId);
                foreach (var (mask, owner) in new[] { (targetOpen, Owner.Open), (targetCombat, Owner.Combat) })
                {
                    ulong written = mask;
                    while (written != 0)
                    {
                        int slot = BitOperations.TrailingZeroCount(written);
                        written &= written - 1;
                        IReadOnlyList<BehaviorValueReference>? actual = slot == 0 ? target.Receiver
                            : slot - 1 < target.Arguments.Count ? target.Arguments[slot - 1] : null;
                        bool deep = owner == Owner.Combat || target.IsCallback && slot > 0;
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
                            var (isTop, open, combat) = ClassifyRoot(caller, value.ValueId, deep, owner, false);
                            if (isTop)
                            {
                                AddTop(caller, new TopCause(target.MethodId, position, string.Empty));
                                return;
                            }
                            AddSlots(caller, open, combat);
                        }
                    }
                }
            }

            // 按摘要同样的归属规则在函数内回溯写入目标；调用结果、新对象与未知来源不据此下结论。
            private (bool Top, ulong Open, ulong Combat) ClassifyRoot(string method, int value, bool deep, Owner owner, bool pendingOwner)
            {
                ulong open = 0;
                ulong combat = 0;
                Queue<Walk> pending = new();
                pending.Enqueue(new Walk(new BehaviorValueReference(method, value), deep, owner, pendingOwner));
                HashSet<Walk> visited = new();
                while (pending.TryDequeue(out Walk item))
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
                        Walk walk = item;
                        BehaviorValueReference input = source.InputValueIds.Count == 0 ? default : origin.Reference with { ValueId = source.InputValueIds[0] };
                        int slot = -1;
                        switch (source.Kind)
                        {
                            case BehaviorValueKind.CurrentInstance:
                                slot = 0;
                                break;
                            case BehaviorValueKind.Parameter:
                                if (source.ParameterIndex is int parameter && parameter >= 0 && parameter < MaxParameterSlot)
                                {
                                    slot = parameter + 1;
                                }
                                break;
                            case BehaviorValueKind.FieldRead:
                            case BehaviorValueKind.Address:
                                if (source.InputValueIds.Count == 0)
                                {
                                    if (source.Member != null && Settle(ref walk)
                                        && ReadStaticOwner(this.m_catalog, walk.Owner, source.Member) == Owner.Combat)
                                    {
                                        return (true, open, combat);
                                    }
                                }
                                else if (Step(this.m_catalog, ref walk, source))
                                {
                                    pending.Enqueue(walk with { Reference = input });
                                }
                                break;
                            case BehaviorValueKind.ArrayElementRead:
                                if (source.InputValueIds.Count != 0 && Settle(ref walk))
                                {
                                    pending.Enqueue(walk with { Reference = input, Deep = true });
                                }
                                break;
                            case BehaviorValueKind.ValueCopy:
                                if (walk.Deep && source.InputValueIds.Count != 0 && Settle(ref walk))
                                {
                                    pending.Enqueue(walk with { Reference = input });
                                }
                                break;
                        }
                        if (slot >= 0 && Settle(ref walk))
                        {
                            if (walk.Owner == Owner.Combat)
                            {
                                combat |= 1UL << slot;
                            }
                            else
                            {
                                open |= 1UL << slot;
                            }
                        }
                    }
                }
                return (false, open, combat);
            }

            // 已确定槽位只增加；新增后通知调用者重新换算。
            private void AddSlots(string method, ulong open, ulong combat)
            {
                var old = this.m_slots.GetValueOrDefault(method);
                if ((old.Open | open) == old.Open && (old.Combat | combat) == old.Combat || IsTop(method))
                {
                    return;
                }
                this.m_slots[method] = (old.Open | open, old.Combat | combat);
                if ((old.Open | old.Combat) == 0)
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
                    foreach (var (caller, target, position) in bindings)
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

        // V3 设计 2.2 节：归属字段在战斗程序集中声明时是战斗状态；声明位置不明时按战斗状态处理。
        private static Owner ClassifyField(MethodCatalogResult catalog, BehaviorMemberReference member)
        {
            string? assembly = member.TargetAssemblyIdentity.Length != 0 ? member.TargetAssemblyIdentity.Split(',')[0]
                : catalog.TypesById.TryGetValue(member.DeclaringTypeDefinitionId, out TypeEntry? type) ? type.AssemblyName : null;
            return assembly == null || catalog.CombatAssemblies.Contains(assembly) ? Owner.Combat : Owner.Other;
        }

        // 结构体成员不改变归属；声明类型不明时按结构体成员处理，交由外层存储判定。
        private static bool IsValueMember(MethodCatalogResult catalog, BehaviorMemberReference member)
            => member.SourceSymbol?.ContainingType?.IsValueType
                ?? (catalog.TypesById.TryGetValue(member.DeclaringTypeDefinitionId, out TypeEntry? type) ? type.IsValueType : true);

        // 字段写入：引用类型对象的字段本身决定归属，并待查其容器是否为本函数新对象；结构体成员与元素写入归属未定。
        private static (Owner Owner, bool Pending) ReadWriteOwner(MethodCatalogResult catalog, BehaviorWrite write)
            => write.Kind == BehaviorWriteKind.Field && write.Member != null && !IsValueMember(catalog, write.Member)
                ? (ClassifyField(catalog, write.Member), true) : (Owner.Open, false);

        // 静态存储就是归属字段；未定归属时由它决定，没有字段身份时按战斗状态处理。
        private static Owner ReadStaticOwner(MethodCatalogResult catalog, Owner owner, BehaviorMemberReference? member)
            => owner != Owner.Open ? owner : member == null ? Owner.Combat : ClassifyField(catalog, member);

        // 当前值不是本函数新对象：待查的归属就此确定，非战斗归属的路径不再计入。
        private static bool Settle(ref Walk walk)
        {
            if (walk.Pending)
            {
                if (walk.Owner == Owner.Other)
                {
                    return false;
                }
                walk = walk with { Pending = false };
            }
            return true;
        }

        // 回溯经过字段读取或字段地址：归属未定时，首个引用字段或引用类型对象上的字段决定归属；局部与参数地址保持原存储。
        private static bool Step(MethodCatalogResult catalog, ref Walk walk, BehaviorValue source)
        {
            if (source.Kind == BehaviorValueKind.Address && source.Member == null)
            {
                return true;
            }
            if (!Settle(ref walk))
            {
                return false;
            }
            BehaviorMemberReference? member = source.Member;
            bool reference = source.Kind == BehaviorValueKind.FieldRead && member?.IsReferenceStorage != false;
            if (walk.Owner == Owner.Open && member != null && (reference || !IsValueMember(catalog, member)))
            {
                walk = walk with { Owner = ClassifyField(catalog, member), Pending = true };
            }
            if (reference)
            {
                walk = walk with { Deep = true };
            }
            return true;
        }

        private const int StaticBit = 128;
        private const int UnknownBit = 129;
        private const int CombatBit = 130;
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
                if (body.BodyKind == MethodBodyKind.LibraryModel)
                {
                    return ReadLibrarySummary(method);
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
                    var (owner, pendingOwner) = ReadWriteOwner(this.m_catalog, write);
                    IReadOnlyList<EffectRoot> targets = write.ReceiverValueId is int receiver ? ReadRoots(state, receiver, false, owner, pendingOwner)
                        : ReadStaticOwner(this.m_catalog, Owner.Open, write.Member) == Owner.Combat ? new[] { EffectRoot.Static(Owner.Combat) }
                        : Array.Empty<EffectRoot>();
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
                    written.StaticCombat |= (alias & (1UL << ReturnSlot)) != 0;
                    (summary.Outs ??= new()).Add(index, written);
                }
                ApplyTop(method, summary);
                return summary;
            }

            // V3 设计 3.2 节：外部库函数按签名建模，返回值可能是新对象、库内全局对象或接收对象与实参所含的任一对象。
            private MethodSummary ReadLibrarySummary(string method)
            {
                MethodEntry entry = this.m_values.ReadMethod(method);
                LibraryEffect effect = RuntimeOperations.ReadLibraryEffect(this.m_catalog, entry);
                MethodSummary summary = new();
                foreach (int slot in Bits(effect.Written))
                {
                    summary.SetBit(slot, new SummaryCause(new BehaviorFlowPoint(-1, 0), null, -1, effect.Reasons[slot]));
                }
                summary.AddInto(0, effect.StoredIntoReceiver);
                ulong sources = entry.IsStatic ? 0 : 1UL;
                for (int index = 0; index < entry.Parameters.Count && index < MaxParameterSlot; index++)
                {
                    sources |= 1UL << (index + 1);
                }
                summary.Return = new ValueSummary { Fresh = true, Static = entry.IsStatic, DeepSlots = sources };
                for (int index = 0; index < entry.Parameters.Count && index < MaxParameterSlot; index++)
                {
                    if (entry.Parameters[index].RefKind is Microsoft.CodeAnalysis.RefKind.Ref or Microsoft.CodeAnalysis.RefKind.Out)
                    {
                        (summary.Outs ??= new()).Add(index, summary.Return);
                    }
                }
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

            // 一次调用：运行时规则、未绑定回调或把被调函数摘要换算到本函数的实参；回调的实参是库函数实参所含的内容。
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
                    state.Effects.Add((EffectRoot.Static(Owner.Combat), false, new(call.Point, null, -1, UnboundCallbackDetail)));
                }
                if (resolved.ValuesOnly)
                {
                    return;
                }
                foreach (ResolvedCallTarget target in resolved.Targets)
                {
                    MethodSummary callee = ReadSummary(target.MethodId);
                    if (callee.Static)
                    {
                        state.Effects.Add((EffectRoot.Static(Owner.Combat), false, new(call.Point, target.MethodId, StaticBit, string.Empty)));
                    }
                    if (callee.Unknown != null)
                    {
                        state.Effects.Add((EffectRoot.UnknownOf(callee.Unknown), false, new(call.Point, target.MethodId, UnknownBit, string.Empty)));
                    }
                    foreach (int slot in Bits(callee.Shallow))
                    {
                        MapSlot(state, target, slot, target.IsCallback && slot > 0, Owner.Open, false, new(call.Point, target.MethodId, slot, string.Empty));
                    }
                    foreach (int slot in Bits(callee.Deep))
                    {
                        MapSlot(state, target, slot, true, Owner.Open, true, new(call.Point, target.MethodId, 64 + slot, string.Empty));
                    }
                    foreach (int slot in Bits(callee.Combat))
                    {
                        MapSlot(state, target, slot, true, Owner.Combat, true, new(call.Point, target.MethodId, CombatBit + slot, string.Empty));
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
                                values.Add(EffectRoot.Static(Owner.Combat));
                                continue;
                            }
                            values.AddRange(ReadActualRoots(state, target, source, target.IsCallback && source > 0, Owner.Open));
                        }
                        if (destination == ReturnSlot)
                        {
                            if (call.ResultValueId is int result && !target.IsCallback)
                            {
                                state.AddFreshSources(result, values);
                            }
                            continue;
                        }
                        Store(state, ReadActualRoots(state, target, destination, target.IsCallback && destination > 0, Owner.Open), values);
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

            // 被调函数写入的槽位按已定或未定归属对应到本调用的实参；实参属于其他函数时按静态处理。
            private void MapSlot(MethodState state, ResolvedCallTarget target, int slot, bool deep, Owner owner, bool deepWrite, SummaryCause cause)
            {
                foreach (EffectRoot root in ReadActualRoots(state, target, slot, deep, owner))
                {
                    state.Effects.Add((root, deepWrite, cause));
                }
            }

            // 读取目标函数某个槽位对应实参的根。
            private IReadOnlyList<EffectRoot> ReadActualRoots(MethodState state, ResolvedCallTarget target, int slot, bool deep, Owner owner)
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
                        roots.Add(EffectRoot.ForeignOf(owner));
                        continue;
                    }
                    if (value.ValueId < 0)
                    {
                        roots.Add(EffectRoot.UnknownOf("实参来源尚未确定"));
                        continue;
                    }
                    roots.AddRange(ReadRoots(state, value.ValueId, deep, owner));
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

            // 把一个根上的写入落到摘要；经由新对象的深写传到它的别名根，路径已定的战斗归属覆盖别名自身的未定归属。
            private void Apply(MethodState state, MethodSummary summary, EffectRoot root, bool deepWrite, SummaryCause cause, HashSet<int> visiting)
            {
                bool deep = deepWrite || root.Deep;
                switch (root.Kind)
                {
                    case EffectRootKind.Receiver:
                    case EffectRootKind.Parameter:
                        summary.SetBit(root.Owner == Owner.Combat ? CombatBit + root.Slot : deep ? 64 + root.Slot : root.Slot, cause);
                        break;
                    case EffectRootKind.Static:
                        if (root.Owner == Owner.Combat)
                        {
                            summary.SetBit(StaticBit, cause);
                        }
                        break;
                    case EffectRootKind.Foreign:
                        summary.SetBit(StaticBit, cause);
                        break;
                    case EffectRootKind.Fresh:
                        if (deep && visiting.Add(root.Site))
                        {
                            foreach (EffectRoot alias in ReadAliasRoots(state, root.Site))
                            {
                                Apply(state, summary, root.Owner == Owner.Combat ? alias with { Owner = Owner.Combat } : alias, true, cause, visiting);
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

            // 返回值或 out 写出值的来源说明；新对象的别名槽位单独返回，经新对象成员取得的值按别名自身记入。
            private ValueSummary ReadValueSummary(MethodState state, IEnumerable<EffectRoot> roots, out ulong alias)
            {
                ValueSummary summary = new();
                ulong aliases = 0;
                HashSet<int> visiting = new();
                void Fold(EffectRoot root)
                {
                    switch (root.Kind)
                    {
                        case EffectRootKind.Receiver:
                        case EffectRootKind.Parameter:
                            if (root.Owner == Owner.Combat)
                            {
                                summary.CombatSlots |= 1UL << root.Slot;
                            }
                            else if (root.Deep)
                            {
                                summary.DeepSlots |= 1UL << root.Slot;
                            }
                            else
                            {
                                summary.Slots |= 1UL << root.Slot;
                            }
                            break;
                        case EffectRootKind.Static:
                            if (root.Owner == Owner.Combat)
                            {
                                summary.StaticCombat = true;
                            }
                            else
                            {
                                summary.Static = true;
                            }
                            break;
                        case EffectRootKind.Fresh:
                            summary.Fresh = true;
                            aliases |= SlotsOf(state, ReadAliasRoots(state, root.Site));
                            if (root.Deep && visiting.Add(root.Site))
                            {
                                foreach (EffectRoot inner in ReadAliasRoots(state, root.Site))
                                {
                                    Fold(root.Owner == Owner.Combat ? inner with { Owner = Owner.Combat } : inner);
                                }
                            }
                            break;
                        case EffectRootKind.Foreign:
                            summary.Unknown ??= "返回值来自其他函数的对象";
                            break;
                        case EffectRootKind.Unknown:
                            summary.Unknown ??= root.Reason ?? "返回值来源尚未确定";
                            break;
                    }
                }
                foreach (EffectRoot root in roots)
                {
                    Fold(root);
                }
                alias = aliases;
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

            // 值的全部输入，与原值位于同一函数。
            private static BehaviorValueReference[] ReadInputs(ValueOrigin origin)
                => origin.Value.InputValueIds.Select(input => origin.Reference with { ValueId = input }).ToArray();

            // 值在本函数内的最终来源及归属；调用结果用被调函数的返回摘要换算。
            private IReadOnlyList<EffectRoot> ReadRoots(MethodState state, int valueId, bool deep, Owner owner = Owner.Open, bool pendingOwner = false)
            {
                if (state.Roots.TryGetValue((valueId, deep, owner, pendingOwner), out List<EffectRoot>? known))
                {
                    return known;
                }
                List<EffectRoot> result = new();
                state.Roots.Add((valueId, deep, owner, pendingOwner), result);
                HashSet<EffectRoot> seen = new();
                void Add(EffectRoot root)
                {
                    if (seen.Add(root))
                    {
                        result.Add(root);
                    }
                }
                Queue<Walk> pending = new();
                HashSet<Walk> visited = new();
                pending.Enqueue(new Walk(new BehaviorValueReference(state.Method, valueId), deep, owner, pendingOwner));
                while (pending.TryDequeue(out Walk item))
                {
                    if (!visited.Add(item))
                    {
                        continue;
                    }
                    foreach (ValueOrigin origin in this.m_values.ReadLocalOrigins(item.Reference, state.Method, observe: false))
                    {
                        BehaviorValue value = origin.Value;
                        Walk walk = item;
                        if (origin.Reference.MethodId != state.Method)
                        {
                            if (value.Kind is not (BehaviorValueKind.Local or BehaviorValueKind.Constant) && Settle(ref walk))
                            {
                                Add(EffectRoot.ForeignOf(walk.Owner));
                            }
                            continue;
                        }
                        BehaviorValueReference firstInput = value.InputValueIds.Count == 0 ? default : origin.Reference with { ValueId = value.InputValueIds[0] };
                        switch (value.Kind)
                        {
                            case BehaviorValueKind.CurrentInstance:
                                if (Settle(ref walk))
                                {
                                    Add(new(EffectRootKind.Receiver, 0, 0, walk.Deep, walk.Owner, null));
                                }
                                break;
                            case BehaviorValueKind.Parameter:
                                if (value.ParameterIndex is not int parameter || parameter < 0 || parameter >= MaxParameterSlot)
                                {
                                    Add(EffectRoot.UnknownOf("参数超出摘要可表示范围"));
                                }
                                else if (Settle(ref walk))
                                {
                                    Add(new(EffectRootKind.Parameter, parameter + 1, 0, walk.Deep, walk.Owner, null));
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
                                Add(EffectRoot.FreshOf(origin.Reference.ValueId, walk));
                                break;
                            case BehaviorValueKind.ShallowCopy:
                                Add(EffectRoot.FreshOf(origin.Reference.ValueId, walk));
                                if (!state.CopySources.Contains(origin.Reference.ValueId))
                                {
                                    state.CopySources.Add(origin.Reference.ValueId);
                                    List<EffectRoot> copied = new();
                                    foreach (BehaviorValueReference source in origin.BoundReceiver
                                        ?? ReadInputs(origin))
                                    {
                                        copied.AddRange(source.MethodId != state.Method ? new[] { EffectRoot.ForeignOf(Owner.Open) }
                                            : source.ValueId < 0 ? new[] { EffectRoot.UnknownOf("浅复制来源尚未确定") }
                                            : ReadRoots(state, source.ValueId, true));
                                    }
                                    state.AddFreshSources(origin.Reference.ValueId, copied);
                                }
                                break;
                            case BehaviorValueKind.FieldRead:
                                if (value.InputValueIds.Count == 0)
                                {
                                    if (!Settle(ref walk))
                                    {
                                        break;
                                    }
                                    if (value.Member == null)
                                    {
                                        Add(EffectRoot.UnknownOf(value.Reference ?? "写入对象来源尚未确定"));
                                    }
                                    else if (ReadStaticOwner(this.m_catalog, walk.Owner, value.Member) == Owner.Combat)
                                    {
                                        Add(EffectRoot.Static(Owner.Combat));
                                    }
                                }
                                else if (Step(this.m_catalog, ref walk, value))
                                {
                                    pending.Enqueue(walk with { Reference = firstInput });
                                }
                                break;
                            case BehaviorValueKind.ArrayElementRead:
                                if (!Settle(ref walk))
                                {
                                    break;
                                }
                                if (value.InputValueIds.Count == 0)
                                {
                                    Add(EffectRoot.UnknownOf("写入对象来源尚未确定"));
                                }
                                else
                                {
                                    pending.Enqueue(walk with { Reference = firstInput, Deep = true });
                                }
                                break;
                            case BehaviorValueKind.Address:
                                if (value.Member != null && value.InputValueIds.Count == 0)
                                {
                                    if (Settle(ref walk) && ReadStaticOwner(this.m_catalog, walk.Owner, value.Member) == Owner.Combat)
                                    {
                                        Add(EffectRoot.Static(Owner.Combat));
                                    }
                                }
                                else if (value.InputValueIds.Count != 0)
                                {
                                    if (!Step(this.m_catalog, ref walk, value))
                                    {
                                        break;
                                    }
                                    pending.Enqueue(walk with { Reference = firstInput });
                                    // 经局部变量地址做深写时，要追到赋给该变量的值（例如结构体枚举器中保存的原集合）。
                                    if (walk.Deep && value.Member == null && this.m_bodies.TryGetValue(origin.Reference.MethodId, out MethodBehavior? body))
                                    {
                                        foreach (BehaviorAssignment assignment in body.Assignments)
                                        {
                                            if (assignment.TargetValueId == firstInput.ValueId)
                                            {
                                                pending.Enqueue(walk with { Reference = origin.Reference with { ValueId = assignment.ValueId } });
                                            }
                                        }
                                    }
                                }
                                else if (walk.Deep && Settle(ref walk))
                                {
                                    Add(EffectRoot.UnknownOf("写入对象来源尚未确定"));
                                }
                                break;
                            case BehaviorValueKind.ValueCopy:
                                if (walk.Deep && Settle(ref walk))
                                {
                                    foreach (BehaviorValueReference source in origin.BoundReceiver
                                        ?? ReadInputs(origin))
                                    {
                                        pending.Enqueue(walk with { Reference = source });
                                    }
                                }
                                break;
                            case BehaviorValueKind.CallResult:
                                foreach (var mapped in ReadCallResult(state, origin, walk))
                                {
                                    if (mapped.Next is Walk next)
                                    {
                                        pending.Enqueue(next);
                                    }
                                    else
                                    {
                                        Add(mapped.Root!.Value);
                                    }
                                }
                                break;
                            default:
                                if (Settle(ref walk))
                                {
                                    Add(EffectRoot.UnknownOf("写入对象来源尚未确定"));
                                }
                                break;
                        }
                    }
                }
                return result;
            }

            // 调用结果按被调函数的返回（或 out 写出）摘要对应到本调用的实参；回调的返回值不是调用结果。
            private IEnumerable<(Walk? Next, EffectRoot? Root)> ReadCallResult(MethodState state, ValueOrigin origin, Walk walk)
            {
                if (!this.m_values.TryReadValueCall(origin.Reference, out BehaviorCall? call))
                {
                    yield return (null, EffectRoot.UnknownOf("调用结果来源尚未确定"));
                    yield break;
                }
                this.m_pending.TryGetValue((state.Method, call!.Point), out PendingCall? pending);
                if (!this.m_calls.TryGetValue((state.Method, call.Point), out ResolvedCall? resolved))
                {
                    yield return (null, EffectRoot.UnknownOf(pending?.Failure ?? "调用目标尚未确定"));
                    yield break;
                }
                string? failure = pending?.Failure ?? resolved.Failure;
                if (failure != null)
                {
                    yield return (null, EffectRoot.UnknownOf(failure));
                }
                if (resolved.RuntimeRule != null || resolved.InvokesUnboundParameter && resolved.Targets.All(target => target.IsCallback))
                {
                    if (resolved.InvokesUnboundParameter)
                    {
                        yield return (null, EffectRoot.UnknownOf("未固定回调的返回值来源尚未确定"));
                    }
                    yield break;
                }
                Walk settled = walk;
                bool live = Settle(ref settled);
                int? output = origin.Value.ParameterIndex;
                foreach (ResolvedCallTarget target in resolved.Targets.Where(target => !target.IsCallback))
                {
                    MethodSummary callee = ReadSummary(target.MethodId);
                    ValueSummary? returned = output is int index
                        ? callee.Outs != null && callee.Outs.TryGetValue(index, out ValueSummary written) ? written : null
                        : callee.Return;
                    if (returned is not ValueSummary value)
                    {
                        yield return (null, EffectRoot.UnknownOf("out 参数写出来源尚未确定"));
                        continue;
                    }
                    if (value.Unknown != null)
                    {
                        yield return (null, EffectRoot.UnknownOf(value.Unknown));
                    }
                    if (value.Static && live)
                    {
                        yield return (null, EffectRoot.Static(settled.Owner));
                    }
                    if (value.StaticCombat && live)
                    {
                        yield return (null, EffectRoot.Static(settled.Owner == Owner.Open ? Owner.Combat : settled.Owner));
                    }
                    if (value.Fresh)
                    {
                        yield return (null, EffectRoot.FreshOf(origin.Reference.ValueId, walk));
                    }
                    foreach (var (mask, kind) in new[] { (value.Slots, 0), (value.DeepSlots, 1), (value.CombatSlots, 2) })
                    {
                        foreach (int slot in Bits(mask))
                        {
                            IReadOnlyList<BehaviorValueReference>? actual = slot == 0 ? target.Receiver
                                : slot - 1 < target.Arguments.Count ? target.Arguments[slot - 1] : null;
                            if (actual == null)
                            {
                                yield return (null, EffectRoot.UnknownOf("调用实参与目标参数不对应"));
                                continue;
                            }
                            foreach (BehaviorValueReference input in actual)
                            {
                                if (input.MethodId != state.Method)
                                {
                                    if (live)
                                    {
                                        yield return (null, EffectRoot.ForeignOf(settled.Owner));
                                    }
                                }
                                else if (input.ValueId < 0)
                                {
                                    yield return (null, EffectRoot.UnknownOf("实参来源尚未确定"));
                                }
                                else if (kind == 0)
                                {
                                    yield return (walk with { Reference = input }, null);
                                }
                                else if (live)
                                {
                                    yield return (settled with
                                    {
                                        Reference = input,
                                        Deep = true,
                                        Owner = kind == 2 && settled.Owner == Owner.Open ? Owner.Combat : settled.Owner,
                                    }, null);
                                }
                            }
                        }
                    }
                }
            }

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
            internal Dictionary<(int Value, bool Deep, Owner Owner, bool Pending), List<EffectRoot>> Roots { get; } = new();
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

        /// <summary>被写存储的归属：未定（交给调用方）、战斗状态、非战斗状态（V3 设计 2.2 节）。</summary>
        private enum Owner : byte { Open, Combat, Other }

        /// <summary>回溯中的一个值：是否已经过引用成员、当前归属，以及归属字段的容器是否仍待确认不是本函数新对象。</summary>
        private readonly record struct Walk(BehaviorValueReference Reference, bool Deep, Owner Owner, bool Pending);

        /// <summary>值在本函数内的最终来源及写入归属。</summary>
        private readonly record struct EffectRoot(EffectRootKind Kind, int Slot, int Site, bool Deep, Owner Owner, string? Reason)
        {
            internal static EffectRoot Static(Owner owner) => new(EffectRootKind.Static, 0, 0, false, owner, null);
            internal static EffectRoot ForeignOf(Owner owner) => new(EffectRootKind.Foreign, 0, 0, false, owner, null);
            internal static EffectRoot UnknownOf(string reason) => new(EffectRootKind.Unknown, 0, 0, false, Owner.Open, reason);

            // 归属字段就在这个新对象上时，改由新对象保存的别名决定归属。
            internal static EffectRoot FreshOf(int site, Walk walk) => new(EffectRootKind.Fresh, 0, site, walk.Deep,
                walk.Pending ? Owner.Open : walk.Owner, null);
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

        /// <summary>返回值或 out 写出值的来源：槽位对象本身、槽位所含对象（归属未定或已定为战斗）、新对象、库内全局对象、战斗静态数据。</summary>
        private struct ValueSummary
        {
            internal ulong Slots;
            internal ulong DeepSlots;
            internal ulong CombatSlots;
            internal bool Fresh;
            internal bool Static;
            internal bool StaticCombat;
            internal string? Unknown;

            // 比较来源类别，原因文本不参与收敛判断。
            internal readonly bool SameAs(ValueSummary other) => this.Slots == other.Slots && this.DeepSlots == other.DeepSlots
                && this.CombatSlots == other.CombatSlots && this.Fresh == other.Fresh && this.Static == other.Static
                && this.StaticCombat == other.StaticCombat && (this.Unknown == null) == (other.Unknown == null);
        }

        /// <summary>一个函数的效果摘要：归属未定的浅写与深写、已定为战斗状态的写入槽位、战斗静态写入、未知原因、存储关系、返回和 out 来源。</summary>
        private sealed class MethodSummary
        {
            internal static readonly MethodSummary Empty = new();
            internal ulong Shallow;
            internal ulong Deep;
            internal ulong Combat;
            internal bool Static;
            internal string? Unknown;
            internal Dictionary<int, ulong>? Into;
            internal ValueSummary Return;
            internal Dictionary<int, ValueSummary>? Outs;
            internal readonly SummaryCause?[] Causes = new SummaryCause?[CombatBit + 64];

            internal bool IsSetter => this.Static || (this.Shallow | this.Deep | this.Combat) != 0;

            // 证据优先取静态写入，其次最低的战斗、浅写、深写槽位。
            internal int EvidenceBit => this.Static ? StaticBit
                : this.Combat != 0 ? CombatBit + BitOperations.TrailingZeroCount(this.Combat)
                : this.Shallow != 0 ? BitOperations.TrailingZeroCount(this.Shallow)
                : 64 + BitOperations.TrailingZeroCount(this.Deep);

            // 置位并保留位置最前的原因。
            internal void SetBit(int bit, SummaryCause cause)
            {
                if (bit == StaticBit)
                {
                    this.Static = true;
                }
                else if (bit >= CombatBit)
                {
                    this.Combat |= 1UL << (bit - CombatBit);
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
                if (this.Shallow != other.Shallow || this.Deep != other.Deep || this.Combat != other.Combat || this.Static != other.Static
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
