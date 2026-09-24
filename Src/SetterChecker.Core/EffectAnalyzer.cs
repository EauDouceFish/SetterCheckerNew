using System.Diagnostics;

namespace SetterChecker.Core
{
    /// <summary>每个函数保存一份修改说明，只传递新增说明。</summary>
    public sealed class EffectAnalyzer
    {
        // 真实判断和标签生效后的修改共用固定关系，分别保存结果。
        /// <summary>返回请求函数的行为，不能判断时明确报错。</summary>
        public EffectAnalysisResult Analyze(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots,
            CallTargetResolutionResult resolution, CancellationToken cancellationToken = default)
        {
            // 真实行为只计算一轮；未闭合项作为失败返回，不再重复计算标签生效后的第二轮。
            return AnalyzeAvailable(catalog, roots, resolution, false, cancellationToken);
        }

        // 判定已经在连接调用时传递，这里只整理请求函数的结果。
        internal EffectAnalysisResult AnalyzeAvailable(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots,
            CallTargetResolutionResult resolution, bool requireCompleteProof, CancellationToken cancellationToken)
        {
            using var timing = resolution.ValueSources.Timing.Measure(AnalysisTiming.Part.Effects);
            Stopwatch watch = Stopwatch.StartNew();
            EffectAnalysisResult result = resolution.Effects.ReadResults(roots, resolution.PendingCalls, cancellationToken, resolution.Failure);
            if (requireCompleteProof && result.Failures.FirstOrDefault() is var failure && failure.Key != null)
            {
                throw new AnalysisException($"函数行为尚未确定：{failure.Key}；{failure.Value.Detail}");
            }
            return result with { Elapsed = watch.Elapsed };
        }

        /// <summary>每个函数只保存真实修改、追踪修改各一条证据，新增结论只传一次。</summary>
        internal sealed class FunctionEffects
        {
            private readonly MethodCatalogResult m_catalog;
            private readonly Dictionary<string, MethodEntry> m_methods;
            private readonly ValueSourceIndex m_values;
            private readonly HashSet<string?> m_businessAssemblies;
            private HashSet<string>? m_businessReturnTypes;
            private readonly Dictionary<string, bool> m_businessReturnQueries = new(StringComparer.Ordinal);
            private readonly Dictionary<(string Method, bool Tracking), ModificationEvidence> m_setters = new();
            private readonly HashSet<(string Method, bool Tracking)> m_newObjectReturns = new();
            private readonly Dictionary<string, Dictionary<string, (int Position, bool NewObjectConstruction)>> m_callers = new(StringComparer.Ordinal);
            private readonly Dictionary<(string Method, bool Tracking), EffectEvidence> m_unknownWrites = new();
            private readonly Queue<(string Method, bool Tracking)> m_pending = new();
            private readonly List<string> m_settled = new();

            // 修改说明和调用连接共用当前函数表。
            internal FunctionEffects(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots,
                Dictionary<string, MethodEntry> methods, ValueSourceIndex values)
            {
                this.m_catalog = catalog;
                this.m_methods = methods;
                this.m_values = values;
                this.m_businessAssemblies = roots.Select(method => method.AssemblyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            // 真实行为与追踪行为都已确定后，不再为该函数继续查找修改。
            internal bool IsSettled(string method) => this.m_setters.ContainsKey((method, false))
                && (this.m_methods[method].HasNoLogTrackExemption || this.m_setters.ContainsKey((method, true)));

            // 直接写入只需找到第一条确定证据，未知对象来源仍明确保留。
            internal void ReadDirect(MethodBehavior body, IEnumerable<BehaviorWrite>? reflectionWrites = null, bool includeReturns = true)
            {
                using var timing = this.m_values.Timing.Measure(AnalysisTiming.Part.Effects);
                if (IsSettled(body.MethodId))
                {
                    return;
                }
                this.m_unknownWrites.Remove((body.MethodId, false));
                this.m_unknownWrites.Remove((body.MethodId, true));
                foreach (BehaviorWrite write in body.Writes.Concat(reflectionWrites ?? Enumerable.Empty<BehaviorWrite>()))
                {
                    if (write.IsLazyInitialization)
                    {
                        continue;
                    }
                    if (IsStandardDictionaryTryGetValue(body, write))
                    {
                        continue;
                    }
                    IEnumerable<WriteSubjectKind> subjects = write.ReceiverValueId is int receiver
                        ? ReadSubjects(this.m_values, new BehaviorValueReference(body.MethodId, receiver))
                        : new[] { WriteSubjectKind.Static };
                    foreach (WriteSubjectKind subject in subjects)
                    {
                        if (subject != WriteSubjectKind.Unknown)
                        {
                            Seed(body.MethodId, write.Point.BlockId, write.Member?.Name ?? write.Kind.ToString());
                            return;
                        }
                        EffectEvidence evidence = new(new[] { body.MethodId }, write.Point.BlockId, "写入对象来源尚未确定");
                        this.m_unknownWrites[(body.MethodId, false)] = evidence;
                        this.m_unknownWrites[(body.MethodId, true)] = evidence;
                    }
                }
                if (includeReturns)
                {
                    ReadReturns(body, false);
                    ReadReturns(body, true);
                }
            }

            // 分别检查真实创建与需要追踪的创建，可信 NLT 不抹掉写入所需的真实对象来源。
            private void ReadReturns(MethodBehavior body, bool tracking)
            {
                var key = (body.MethodId, tracking);
                if (this.m_setters.ContainsKey(key) || tracking && this.m_methods[body.MethodId].HasNoLogTrackExemption
                    || !CanReturnBusinessObject(this.m_methods[body.MethodId]))
                {
                    return;
                }
                foreach (BehaviorReturn returned in body.Returns.Where(item => item.ValueId.HasValue))
                {
                    foreach (ValueOrigin origin in this.m_values.ReadReturnedOrigins(new BehaviorValueReference(body.MethodId, returned.ValueId!.Value), tracking: tracking))
                    {
                        BehaviorTypeReference? createdType = origin.Value.Type?.Id.StartsWith('!') == true
                            ? origin.Value.AllocationConstraint : origin.Value.Type;
                        // 返回新建对象或浅复制外壳本身不修改已有战斗状态；对象内部若写入了外部对象，已由 ReadDirect 的写入对象来源单独捕获。
                        if (origin.Value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.ShallowCopy)
                        {
                            continue;
                        }
                        if ((origin.Value.Kind == BehaviorValueKind.CallResult
                                || origin.Value.Kind == BehaviorValueKind.NewObject && createdType == null)
                            && (origin.Value.Reference != ValueSourceIndex.ReflectionBaselineFailure
                                || !this.m_unknownWrites.TryGetValue(key, out EffectEvidence? unknown)
                                || unknown.Detail == ValueSourceIndex.ReflectionBaselineFailure))
                        {
                            this.m_unknownWrites[key] = new(new[] { body.MethodId }, returned.Point.BlockId,
                                origin.Value.Reference == ValueSourceIndex.ReflectionBaselineFailure
                                    ? ValueSourceIndex.ReflectionBaselineFailure : "返回值可能是新建业务对象，固定返回说明尚未闭合");
                        }
                    }
                }
            }

            // 返回类型不可能承载业务对象时，不为判断创建行为计算它的全部返回内容。
            private bool CanReturnBusinessObject(MethodEntry method)
            {
                if (this.m_businessReturnQueries.TryGetValue(method.Id, out bool known))
                {
                    return known;
                }
                BehaviorTypeReference reference = this.m_catalog.ReadMethodReturnType(method);
                if (reference.DefinitionIdentity.Text.StartsWith('!'))
                {
                    return true;
                }
                TypeEntry type = this.m_catalog.ResolveTypeDefinition(reference);
                // 枚举返回的是值，不因声明在业务程序集中就追踪新建业务对象。
                if (type.IsEnum)
                {
                    this.m_businessReturnQueries.Add(method.Id, false);
                    return false;
                }
                bool possible = this.m_businessAssemblies.Contains(type.AssemblyPath) && !type.IsCompilerGenerated;
                if (!possible && !type.IsSealed && !type.IsValueType)
                {
                    if (this.m_businessReturnTypes == null)
                    {
                        this.m_businessReturnTypes = new(StringComparer.Ordinal);
                        foreach (TypeEntry business in this.m_catalog.Types.Where(candidate => !candidate.IsCompilerGenerated
                            && this.m_businessAssemblies.Contains(candidate.AssemblyPath)))
                        {
                            this.m_businessReturnTypes.Add(business.Id);
                            foreach (var parent in this.m_catalog.ReadInheritedTypes(business))
                            {
                                this.m_businessReturnTypes.Add(parent.Definition.Id);
                            }
                        }
                    }
                    possible = this.m_businessReturnTypes.Contains(type.Id);
                }
                this.m_businessReturnQueries.Add(method.Id, possible);
                return possible;
            }

            // 一个调用位置找到 Setter 后即可传递结论，不按实际参数复制行为。
            internal void Bind(ResolvedCall call)
            {
                using var timing = this.m_values.Timing.Measure(AnalysisTiming.Part.Effects);
                if (call.ValuesOnly)
                {
                    return;
                }
                if (call.RuntimeRule?.Operation == RuntimeOperation.WriteCollection)
                {
                    Seed(call.CallerMethodId, call.Call.Point.BlockId, call.RuntimeRule.Description);
                }
                if (call.RuntimeRule?.Operation == RuntimeOperation.Diagnostics)
                {
                    return;
                }
                if (call.InvokesUnboundParameter)
                {
                    Seed(call.CallerMethodId, call.Call.Point.BlockId, "调用未固定目标的委托参数，合法回调允许修改状态");
                }
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    if (target.Reference.Name == ".ctor" && IsStandardCollectionType(target.Reference.DeclaringTypeDefinitionId))
                    {
                        continue;
                    }
                    if (!this.m_callers.TryGetValue(target.MethodId, out Dictionary<string, (int Position, bool NewObjectConstruction)>? callers))
                    {
                        callers = new(StringComparer.Ordinal);
                        this.m_callers.Add(target.MethodId, callers);
                    }
                    callers.TryAdd(call.CallerMethodId,
                        (call.Call.Point.BlockId, IsNewObjectConstruction(target)));
                    foreach (bool tracking in new[] { false, true })
                    {
                        if (this.m_setters.ContainsKey((target.MethodId, tracking))
                            && !IsNewObjectReturn(target.MethodId, tracking)
                            && !IsNewObjectConstruction(target))
                        {
                            Add(call.CallerMethodId, tracking, new(target.MethodId, call.Call.Point.BlockId, string.Empty));
                        }
                    }
                    Propagate();
                    if (IsSettled(call.CallerMethodId))
                    {
                        break;
                    }
                }
            }

            // 返回新对象本身不修改调用者可见状态，不能把它传播成上层 Setter。
            private bool IsNewObjectReturn(string method, bool tracking)
            {
                return this.m_newObjectReturns.Contains((method, tracking));
            }

            // 构造函数只初始化调用点刚创建的对象，不把初始化写入传播给调用者。
            private bool IsNewObjectConstruction(ResolvedCallTarget target)
            {
                if (target.Reference.Name != ".ctor")
                {
                    return false;
                }
                if (!this.m_catalog.TypesById.TryGetValue(target.Reference.DeclaringTypeDefinitionId, out TypeEntry? declaringType)
                    || !declaringType.IsValueType)
                {
                    return false;
                }
                foreach (BehaviorValueReference receiver in target.Receiver)
                {
                    if (!this.m_values.Behaviors.TryGetValue(receiver.MethodId, out MethodBehavior? body)
                        || receiver.ValueId < 0 || receiver.ValueId >= body.Values.Count)
                    {
                        continue;
                    }
                    BehaviorValue value = body.Values[receiver.ValueId];
                    if (value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.NewArray or BehaviorValueKind.ShallowCopy)
                    {
                        return true;
                    }
                    if (value.Kind == BehaviorValueKind.CallResult
                        && body.Calls.Any(call => call.ResultValueId == receiver.ValueId
                            && call.Kind == BehaviorCallKind.ObjectCreation))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Dictionary.TryGetValue 只把结果写入 out 参数，不修改字典或其调用者可见状态。
            private bool IsStandardDictionaryTryGetValue(MethodBehavior body, BehaviorWrite write)
            {
                if (write.Kind != BehaviorWriteKind.Indirect || write.Member != null
                    || !IsStandardDictionaryTryGetValue(body.MethodId))
                {
                    return false;
                }
                return true;
            }

            private bool IsStandardDictionaryTryGetValue(string methodId)
            {
                if (!this.m_methods.TryGetValue(methodId, out MethodEntry? method)
                    || method.Name != "TryGetValue" || method.Parameters.Count != 2
                    || method.Parameters[1].RefKind != Microsoft.CodeAnalysis.RefKind.Out
                    || !this.m_catalog.TypesById.TryGetValue(method.TypeId, out TypeEntry? owner))
                {
                    return false;
                }
                return owner.FullName == "System.Collections.Generic.Dictionary`2"
                    || owner.FullName.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal);
            }

            // 标准 List/Dictionary 构造函数只初始化刚创建的容器，不修改外部业务对象。
            private bool IsStandardCollectionType(string typeId)
            {
                if (typeId.Contains("System.Collections.Generic.List`1", StringComparison.Ordinal)
                    || typeId.Contains("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal))
                {
                    return true;
                }
                if (!this.m_catalog.TypesById.TryGetValue(typeId, out TypeEntry? type))
                {
                    return false;
                }
                return type.FullName is "System.Collections.Generic.List`1" or "System.Collections.Generic.Dictionary`2"
                    || type.FullName.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal)
                    || type.FullName.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal);
            }

            // 直接修改同时产生真实证据和受标签约束的追踪证据。
            private void Seed(string method, int position, string detail)
            {
                ModificationEvidence evidence = new(null, position, detail);
                Add(method, false, evidence);
                Add(method, true, evidence);
                Propagate();
            }

            // Setter 只从未确定变成确定；可信标签只阻断追踪传播。
            private void Add(string method, bool tracking, ModificationEvidence evidence, bool newObjectReturn = false)
            {
                if (tracking && this.m_methods[method].HasNoLogTrackExemption)
                {
                    return;
                }
                if (!newObjectReturn)
                {
                    if (this.m_newObjectReturns.Remove((method, tracking))
                        && this.m_setters.ContainsKey((method, tracking)))
                    {
                        this.m_pending.Enqueue((method, tracking));
                    }
                }
                if (this.m_setters.TryAdd((method, tracking), evidence))
                {
                    if (newObjectReturn)
                    {
                        this.m_newObjectReturns.Add((method, tracking));
                    }
                    this.m_pending.Enqueue((method, tracking));
                    if (IsSettled(method))
                    {
                        this.m_settled.Add(method);
                    }
                }
            }

            internal Action<IReadOnlyList<string>>? Settled { get; set; }

            // 每次新增结论只通知已登记的直接调用者。
            private void Propagate()
            {
                while (this.m_pending.TryDequeue(out var fact))
                {
                    if (!this.m_callers.TryGetValue(fact.Method,
                        out Dictionary<string, (int Position, bool NewObjectConstruction)>? callers))
                    {
                        continue;
                    }
                    foreach (var caller in callers)
                    {
                        if (caller.Value.NewObjectConstruction || IsNewObjectReturn(fact.Method, fact.Tracking))
                        {
                            continue;
                        }
                        Add(caller.Key, fact.Tracking, new(fact.Method, caller.Value.Position, string.Empty));
                    }
                }
                if (this.m_settled.Count != 0)
                {
                    this.Settled?.Invoke(this.m_settled);
                    this.m_settled.Clear();
                }
            }

            // 已确定 Setter 的其他未知调用不影响结论；未确定函数不得借剪枝变成 Getter。
            internal EffectAnalysisResult ReadResults(IReadOnlyList<MethodEntry> roots, IReadOnlyList<PendingCall> pendingCalls,
                CancellationToken cancellationToken, string? interrupted = null)
            {
                Dictionary<(string Method, bool Tracking), ModificationEvidence> unknown = new();
                Queue<(string Method, bool Tracking)> queue = new();
                // 只为还没有确定修改的函数保留未知证据。
                void AddUnknown(string method, bool tracking, ModificationEvidence evidence)
                {
                    if (this.m_setters.ContainsKey((method, tracking)) || tracking && this.m_methods[method].HasNoLogTrackExemption)
                    {
                        return;
                    }
                    if (unknown.TryAdd((method, tracking), evidence))
                    {
                        queue.Enqueue((method, tracking));
                    }
                }
                if (interrupted != null)
                {
                    foreach (string method in this.m_methods.Keys)
                    {
                        AddUnknown(method, false, new(null, -1, interrupted));
                        AddUnknown(method, true, new(null, -1, interrupted));
                    }
                }
                foreach (MethodBehavior body in this.m_values.Behaviors.Values)
                {
                    string? failure = body.Failure ?? (body.BodyKind != MethodBodyKind.Executable
                        ? body.NativeBoundary?.ToString() ?? $"没有可读取的托管函数体：{body.BodyKind}" : null);
                    if (failure != null)
                    {
                        AddUnknown(body.MethodId, false, new(null, -1, failure));
                        AddUnknown(body.MethodId, true, new(null, -1, failure));
                    }
                }
                foreach (var pair in this.m_unknownWrites)
                {
                    AddUnknown(pair.Key.Method, pair.Key.Tracking, new(null, pair.Value.Position, pair.Value.Detail));
                }
                foreach (PendingCall call in pendingCalls)
                {
                    ModificationEvidence evidence = new(null, call.Call.Point.BlockId, call.Failure ?? "调用目标尚未确定");
                    AddUnknown(call.CallerMethodId, false, evidence);
                    AddUnknown(call.CallerMethodId, true, evidence);
                }
                while (queue.TryDequeue(out var fact))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (this.m_callers.TryGetValue(fact.Method,
                        out Dictionary<string, (int Position, bool NewObjectConstruction)>? callers))
                    {
                        foreach (var caller in callers)
                        {
                            AddUnknown(caller.Key, fact.Tracking, new(fact.Method, caller.Value.Position, string.Empty));
                        }
                    }
                }
                Dictionary<string, EffectEvidence> failures = new(StringComparer.Ordinal);
                IReadOnlyList<MethodEffect> actual = Results(false, failures);
                return new EffectAnalysisResult(actual, TimeSpan.Zero)
                {
                    Failures = failures,
                    SummaryUpdates = this.m_setters.Count + unknown.Count,
                };

                // 只在输出时沿单条证据链接还原调用过程。
                EffectEvidence Evidence(string method, bool tracking, Dictionary<(string Method, bool Tracking), ModificationEvidence> table)
                {
                    List<string> path = new();
                    ModificationEvidence evidence;
                    do
                    {
                        path.Add(method);
                        evidence = table[(method, tracking)];
                        method = evidence.Callee!;
                    }
                    while (method != null);
                    return new(path, evidence.Position, evidence.Detail);
                }

                // 真实结果与标签生效后的结果分别输出，不互相覆盖。
                IReadOnlyList<MethodEffect> Results(bool tracking, Dictionary<string, EffectEvidence> destination)
                {
                    List<MethodEffect> results = new();
                    foreach (MethodEntry root in roots.OrderBy(method => method.Id, StringComparer.Ordinal))
                    {
                        if (this.m_setters.ContainsKey((root.Id, tracking)) && !IsNewObjectReturn(root.Id, tracking))
                        {
                            results.Add(new(root.Id, MethodEffectKind.Setter, Evidence(root.Id, tracking, this.m_setters)));
                        }
                        else if (unknown.ContainsKey((root.Id, tracking)))
                        {
                            destination.Add(root.Id, Evidence(root.Id, tracking, unknown));
                        }
                        else
                        {
                            results.Add(new(root.Id, MethodEffectKind.Getter, null));
                        }
                    }
                    return results;
                }
            }
        }

        // 把写入值追到本函数的当前对象、参数或静态数据，临时新对象不算外部修改。
        private static IEnumerable<WriteSubjectKind> ReadSubjects(ValueSourceIndex values, BehaviorValueReference reference)
        {
            Queue<(BehaviorValueReference Reference, MemberAccess? Members)> pending = new(new[] { (reference, (MemberAccess?)null) });
            HashSet<(BehaviorValueReference Reference, MemberAccess? Members)> visited = new();
            while (pending.TryDequeue(out var item))
            {
                if (!visited.Add(item))
                {
                    continue;
                }

                IReadOnlyList<ValueOrigin> origins = values.ReadWriteOrigins(item.Reference, reference.MethodId).ToArray();
                if (origins.Count == 0)
                {
                    yield return WriteSubjectKind.Unknown;
                }
                foreach (ValueOrigin origin in origins)
                {
                    switch (origin.Value.Kind)
                    {
                        case BehaviorValueKind.ValueCopy:
                            bool? referenceMember = HasReferenceMember(item.Members);
                            if (referenceMember == true)
                            {
                                foreach (BehaviorValueReference input in origin.BoundReceiver
                                    ?? origin.Value.InputValueIds.Select(value => origin.Reference with { ValueId = value }).ToArray())
                                {
                                    pending.Enqueue((input, item.Members));
                                }
                            }
                            else if (referenceMember == null)
                            {
                                yield return WriteSubjectKind.Unknown;
                            }
                            break;
                        case BehaviorValueKind.CurrentInstance:
                            yield return WriteSubjectKind.Receiver;
                            break;
                        case BehaviorValueKind.Parameter:
                            yield return WriteSubjectKind.Parameter;
                            break;
                        case BehaviorValueKind.FieldRead when origin.Value.InputValueIds.Count == 0:
                        case BehaviorValueKind.Address when origin.Value.Member != null && origin.Value.InputValueIds.Count == 0:
                            yield return WriteSubjectKind.Static;
                            break;
                        case BehaviorValueKind.Address when origin.Value.Reference?.StartsWith("local:", StringComparison.Ordinal) == true
                            || origin.Value.Reference?.StartsWith("argument:", StringComparison.Ordinal) == true:
                            if (item.Members != null)
                            {
                                foreach (BehaviorValueReference value in values.ReadMemberValues(origin, item.Members.Member))
                                {
                                    pending.Enqueue((value, item.Members.Next));
                                }
                            }
                            break;
                        case BehaviorValueKind.Address:
                        case BehaviorValueKind.FieldRead:
                        case BehaviorValueKind.ArrayElementRead:
                            MemberAccess? access = item.Members;
                            if (origin.Value.Member != null)
                            {
                                if (ContainsMember(access, origin.Reference))
                                {
                                    yield return WriteSubjectKind.Unknown;
                                    break;
                                }
                                access = new MemberAccess(origin.Value.Member, origin.Reference, access);
                            }
                            foreach (BehaviorValueReference input in origin.BoundReceiver
                                ?? origin.Value.InputValueIds.Take(1).Select(value => origin.Reference with { ValueId = value }).ToArray())
                            {
                                pending.Enqueue((input, access));
                            }
                            break;
                        case BehaviorValueKind.NewObject:
                        case BehaviorValueKind.ShallowCopy:
                            if (item.Members != null)
                            {
                                if (origin.IsAllocationSummary)
                                {
                                    yield return WriteSubjectKind.Unknown;
                                    break;
                                }
                                foreach (BehaviorValueReference value in values.ReadMemberValues(origin, item.Members.Member))
                                {
                                    pending.Enqueue((value, item.Members.Next));
                                }
                            }
                            break;
                        case BehaviorValueKind.NewArray:
                        case BehaviorValueKind.Local:
                        case BehaviorValueKind.Constant:
                            break;
                        default:
                            yield return WriteSubjectKind.Unknown;
                            break;
                    }
                }
            }
        }

        // 值副本内只有沿引用成员继续写入才会影响副本外的数据。
        private static bool? HasReferenceMember(MemberAccess? access)
        {
            bool? result = false;
            for (; access != null; access = access.Next)
            {
                if (access.Member.IsReferenceStorage == true)
                {
                    return true;
                }
                if (access.Member.IsReferenceStorage == null)
                {
                    result = null;
                }
            }
            return result;
        }

        // 递归成员关系不能无限增长；发现循环时保留明确的待分析项。
        private static bool ContainsMember(MemberAccess? access, BehaviorValueReference reference)
        {
            for (; access != null; access = access.Next)
            {
                if (access.Reference == reference)
                {
                    return true;
                }
            }
            return false;
        }

        private enum WriteSubjectKind { Receiver, Parameter, Static, Unknown }
        private sealed record MemberAccess(BehaviorMemberReference Member, BehaviorValueReference Reference, MemberAccess? Next);
        private sealed record ModificationEvidence(string? Callee, int Position, string Detail);
    }

    /// <summary>函数修改能力，不是日志标签决定。</summary>
    public enum MethodEffectKind
    {
        /// <summary>没有外部修改，必要调用均已确定。</summary>
        Getter,
        /// <summary>具有修改外部对象或传出业务对象的能力。</summary>
        Setter,
    }

    /// <summary>交付时生成的一条修改证据及位置。</summary>
    public sealed record EffectEvidence(IReadOnlyList<string> MethodPath, int Position, string Detail);

    /// <summary>函数的真实判断及证据。</summary>
    public sealed record MethodEffect(string MethodId, MethodEffectKind Kind, EffectEvidence? Evidence);

    /// <summary>真实行为和标签生效后行为使用各自的说明。</summary>
    public sealed record EffectAnalysisResult(IReadOnlyList<MethodEffect> Methods, TimeSpan Elapsed)
    {
        /// <summary>真实行为尚未确定的函数。</summary>
        public IReadOnlyDictionary<string, EffectEvidence> Failures { get; init; } = new Dictionary<string, EffectEvidence>();
        /// <summary>本轮实际新增的函数修改说明数量。</summary>
        public int SummaryUpdates { get; init; }
    }
}
