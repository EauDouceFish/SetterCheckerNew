using System.Diagnostics;

namespace SetterChecker.Core
{
    /// <summary>沿已有调用与值来源证明根函数是否修改外部对象。</summary>
    public sealed class EffectAnalyzer
    {
        // 保存被修改的对象来源，按每条调用边映射到实际参数并迭代至稳定。
        /// <summary>返回全部根函数的真实行为；缺少必要证明时明确失败。</summary>
        public EffectAnalysisResult Analyze(
            MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> roots,
            CallTargetResolutionResult resolution,
            CancellationToken cancellationToken = default)
        {
            return AnalyzeAvailable(catalog, roots, resolution, true, cancellationToken);
        }

        // 共用写入传播处理完整或正在补读的图，未证明的根绝不加入确定结果。
        internal EffectAnalysisResult AnalyzeAvailable(
            MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots,
            CallTargetResolutionResult resolution, bool requireCompleteProof, CancellationToken cancellationToken,
            IReadOnlyDictionary<int, EffectEvidence>? frozenSetters = null)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            MethodCallInstance[] instances = resolution.ValueSources.Instances.Where(instance => frozenSetters?.ContainsKey(instance.RootId) != true).ToArray();
            Dictionary<int, EffectEvidence> proofs = frozenSetters == null ? new() : new(frozenSetters);
            Dictionary<int, EffectEvidence> boundaries = new();
            HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)> closedPrefixes = new();
            ILookup<int, (ResolvedCall Call, ResolvedCallTarget Target)> callers = resolution.Calls
                .SelectMany(call => call.Targets.Select(target => (Call: call, Target: target))).ToLookup(item => item.Target.InstanceId);
            IReadOnlyDictionary<(int Instance, int Block), string> unsettled = resolution.ValueSources.ReadConditionFailures(instances);
            HashSet<int> unsettledRoots = unsettled.Keys.Select(key => resolution.ValueSources.GetInstance(key.Instance).RootId).ToHashSet();

            // 每处证据沿真实调用边独立传播，保留递归回边及逐参数失败隔离。
            void Propagate(int instanceId, WriteSubject subject, EffectEvidence evidence, BehaviorFlowPoint? point = null)
            {
                int rootId = resolution.ValueSources.GetInstance(instanceId).RootId;
                Queue<(int Instance, WriteSubject Subject, EffectEvidence Evidence, BehaviorFlowPoint? Point)> pending = new(new[] { (instanceId, subject, evidence, point) });
                HashSet<(int, WriteSubject, BehaviorFlowPoint?)> visited = new();
                while (!proofs.ContainsKey(rootId) && pending.TryDequeue(out var current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add((current.Instance, current.Subject, current.Point)))
                    {
                        continue;
                    }
                    if (current.Subject.Failure == null && current.Point.HasValue
                        && !resolution.ValueSources.HasClosedPrefix(current.Instance, current.Point.Value, closedPrefixes, conditionFailures: unsettled))
                    {
                        current.Subject = new WriteSubject(null, "修改位置之前的调用尚未证明可以正常返回");
                    }
                    if (current.Instance == rootId)
                    {
                        if (current.Subject.Failure == null)
                        {
                            proofs.Add(rootId, current.Evidence);
                        }
                        else if (!boundaries.TryGetValue(rootId, out EffectEvidence? previous) || current.Evidence.MethodPath.Count < previous.MethodPath.Count)
                        {
                            boundaries[rootId] = current.Evidence with { Detail = current.Subject.Failure };
                        }
                        continue;
                    }
                    foreach (var item in callers[current.Instance])
                    {
                        IEnumerable<WriteSubject> mapped = new[] { current.Subject };
                        if (current.Subject.Failure == null && current.Subject.Reference is BehaviorValueReference reference)
                        {
                            BehaviorValue value = resolution.Behaviors.MethodsById[reference.MethodId].Values[reference.ValueId];
                            IEnumerable<BehaviorValueReference> arguments = reference.InstanceId == current.Instance
                                ? value.Kind == BehaviorValueKind.Parameter ? item.Target.Arguments[value.ParameterIndex!.Value] : item.Target.Receiver
                                : new[] { reference };
                            mapped = arguments.SelectMany(argument => ReadWriteSubjects(catalog, resolution, argument, item.Call.CallerInstanceId));
                        }
                        foreach (WriteSubject mappedSubject in mapped)
                        {
                            pending.Enqueue((item.Call.CallerInstanceId, mappedSubject,
                                current.Evidence with { MethodPath = current.Evidence.MethodPath.Prepend(item.Call.CallerMethodId).ToArray() }, item.Call.Call.Point));
                        }
                    }
                }
            }

            foreach (MethodCallInstance instance in instances)
            {
                if (resolution.ValueSources.ReadStaticCallInitializationFailure(instance.Id) is string initializationFailure)
                {
                    Propagate(instance.Id, new WriteSubject(null, initializationFailure), new EffectEvidence(new[] { instance.MethodId }, -1, initializationFailure));
                }
                if (!resolution.Behaviors.MethodsById.TryGetValue(instance.MethodId, out MethodBehavior? body)
                    || body.BodyKind != MethodBodyKind.Executable
                        && !(body.BodyKind == MethodBodyKind.RuntimeImplementation && callers[instance.Id].Any() && callers[instance.Id].All(item =>
                            resolution.ValueSources.IsRuntimeDelegateCreation(item.Call, item.Target))))
                {
                    Propagate(instance.Id, new WriteSubject(null, body?.Failure ?? body?.NativeBoundary?.ToString() ?? body?.BodyKind.ToString() ?? "等待读取函数体"),
                        new EffectEvidence(new[] { instance.MethodId }, -1, string.Empty));
                }
            }
            foreach (PendingCall call in resolution.PendingCalls.Where(call => !proofs.ContainsKey(resolution.ValueSources.GetInstance(call.CallerInstanceId).RootId)))
            {
                Propagate(call.CallerInstanceId, new WriteSubject(null, call.Failure ?? "等待确定调用目标：" + call.Call.Target.Identity.Text),
                    new EffectEvidence(new[] { call.CallerMethodId }, call.Call.Position, string.Empty));
            }
            foreach (var failure in unsettled)
            {
                MethodCallInstance instance = resolution.ValueSources.GetInstance(failure.Key.Instance);
                Propagate(instance.Id, new WriteSubject(null, failure.Value),
                    new EffectEvidence(new[] { instance.MethodId }, failure.Key.Block, failure.Value));
            }
            foreach (MethodCallInstance instance in instances)
            {
                resolution.Behaviors.MethodsById.TryGetValue(instance.MethodId, out MethodBehavior? body);
                foreach (BehaviorWrite write in (body == null ? Enumerable.Empty<BehaviorWrite>() : resolution.ValueSources.GetWrites(instance.Id).OrderBy(write =>
                             write.ReceiverValueId is int receiver && body.Values[receiver].Kind is not (BehaviorValueKind.CurrentInstance or BehaviorValueKind.Parameter) ? 1 : 0))
                         .TakeWhile(_ => !proofs.ContainsKey(instance.RootId)))
                {
                    EffectEvidence evidence = new(new[] { instance.MethodId }, write.Position, write.Member?.Name ?? write.Kind.ToString());
                    foreach (WriteSubject subject in write.ReceiverValueId == null ? new[] { new WriteSubject(null) }
                                 : ReadWriteSubjects(catalog, resolution, new BehaviorValueReference(instance.MethodId, write.ReceiverValueId.Value, instance.Id), instance.Id))
                    {
                        Propagate(instance.Id, subject, evidence, write.Point);
                    }
                }
            }

            HashSet<string?> businessAssemblies = roots.Select(method => method.AssemblyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<MethodEffect> results = new();
            Dictionary<string, EffectEvidence> failures = new(StringComparer.Ordinal);
            foreach (MethodEntry root in roots.OrderBy(method => method.Id, StringComparer.Ordinal))
            {
                int instanceId = resolution.ValueSources.RootInstances[root.Id].Id;
                EffectEvidence? proof = proofs.GetValueOrDefault(instanceId);
                bool? readOnlyClosure = null;
                foreach (BehaviorReturn returned in resolution.Behaviors.MethodsById[root.Id].Returns.Where(value => value.ValueId.HasValue && proof == null
                             && !unsettledRoots.Contains(instanceId)
                             && resolution.ValueSources.IsReachable(instanceId, value.Point.BlockId)
                             && !(readOnlyClosure ??= resolution.ValueSources.HasOnlyReads(instanceId) == true)))
                {
                    IReadOnlyList<(BehaviorValueReference Reference, BehaviorFlowPoint Point)> sites;
                    try
                    {
                        sites = resolution.ValueSources.ReadReturnSites(instanceId, returned, closedPrefixes, unsettled);
                    }
                    catch (AnalysisException exception)
                    {
                        boundaries.TryAdd(instanceId, new EffectEvidence(new[] { root.Id }, returned.Position, exception.Message));
                        continue;
                    }
                    foreach (var site in sites.TakeWhile(_ => proof == null))
                    {
                        if (!resolution.ValueSources.HasClosedPrefix(instanceId, site.Point, closedPrefixes, conditionFailures: unsettled))
                        {
                            boundaries.TryAdd(instanceId, new EffectEvidence(new[] { root.Id }, returned.Position, "返回位置之前的调用尚未证明可以正常返回"));
                            continue;
                        }
                        BehaviorValueReference reference = site.Reference;
                        Queue<BehaviorValueReference> returnedObjects = new(new[] { reference });
                        HashSet<(BehaviorValueReference, ReturnedValuePath?)> visitedObjects = new();
                        while (returnedObjects.TryDequeue(out BehaviorValueReference current) && proof == null)
                        {
                            try
                            {
                                foreach (ValueOrigin origin in resolution.ValueSources.GetCallOrigins(current)
                                             .OrderBy(origin => origin.Value.Kind == BehaviorValueKind.NewObject ? 0 : 1))
                                {
                                    if (!visitedObjects.Add((origin.Reference, origin.ReturnPath)))
                                    {
                                        continue;
                                    }
                                    if (origin.Value.Kind == BehaviorValueKind.NewObject && origin.Value.Type != null
                                        && catalog.ResolveTypeDefinition(origin.Value.Type) is TypeEntry type
                                        && !type.IsCompilerGenerated && businessAssemblies.Contains(type.AssemblyPath))
                                    {
                                        proof = new EffectEvidence(new[] { root.Id }, returned.Position, "返回新建业务对象或包含它的容器");
                                        break;
                                    }
                                    else if (origin.Value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.NewArray)
                                    {
                                        foreach (BehaviorValueReference member in resolution.ValueSources.ReadContainerValues(current, origin, returned.Point,
                                            failure => boundaries.TryAdd(instanceId, new EffectEvidence(new[] { root.Id }, returned.Position, failure)), site.Point))
                                        {
                                            returnedObjects.Enqueue(member);
                                        }
                                    }
                                    else if (origin.Value.Kind == BehaviorValueKind.CallResult)
                                    {
                                        throw new AnalysisException($"返回对象来源尚未闭合：{root.Id} @ {returned.Position}");
                                    }
                                }
                            }
                            catch (AnalysisException exception)
                            {
                                boundaries.TryAdd(instanceId, new EffectEvidence(new[] { root.Id }, returned.Position, exception.Message));
                            }
                        }
                    }
                }
                if (proof == null && boundaries.TryGetValue(instanceId, out EffectEvidence? boundary))
                {
                    failures.Add(root.Id, boundary);
                    if (requireCompleteProof)
                    {
                        throw new AnalysisException($"函数真实行为缺少实现证明：{root.Id}；{boundary.Detail}；调用过程："
                            + string.Join(" -> ", boundary.MethodPath));
                    }
                    continue;
                }
                results.Add(new MethodEffect(root.Id, proof == null ? MethodEffectKind.Getter : MethodEffectKind.Setter, proof));
            }

            return new EffectAnalysisResult(results, stopwatch.Elapsed) { Failures = failures };
        }

        // 将写入目标还原为当前函数的参数、接收对象或静态存储。
        private static IEnumerable<WriteSubject> ReadWriteSubjects(
            MethodCatalogResult catalog, CallTargetResolutionResult resolution, BehaviorValueReference reference, int instanceId)
        {
            Queue<BehaviorValueReference> pending = new(new[] { reference });
            HashSet<BehaviorValueReference> visited = new();
            while (pending.TryDequeue(out BehaviorValueReference current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }
                IReadOnlyList<ValueOrigin>? origins = null;
                string? failure = null;
                try
                {
                    origins = resolution.ValueSources.GetRelativeOrigins(current, instanceId, retainTypeChecks: true);
                }
                catch (AnalysisException exception)
                {
                    failure = exception.Message;
                }
                if (origins == null)
                {
                    yield return new WriteSubject(current, failure);
                    continue;
                }
                foreach (ValueOrigin origin in origins)
                {
                    switch (origin.Value.Kind)
                    {
                        case BehaviorValueKind.CurrentInstance:
                        case BehaviorValueKind.Parameter:
                            yield return new WriteSubject(origin.Reference);
                            break;
                        case BehaviorValueKind.FieldRead when origin.Value.InputValueIds.Count == 0:
                        case BehaviorValueKind.Address when origin.Value.Member != null && origin.Value.InputValueIds.Count == 0:
                            yield return new WriteSubject(null);
                            break;
                        case BehaviorValueKind.Address when origin.Value.Reference?.StartsWith("argument:", StringComparison.Ordinal) == true
                            || origin.Value.Reference?.StartsWith("local:", StringComparison.Ordinal) == true:
                            break;
                        case BehaviorValueKind.Conversion when origin.Value.Reference == "box"
                            && !CallTargetResolver.IsReferenceType(catalog, origin.Value.Type!.Id, resolution.ValueSources):
                            break;
                        case BehaviorValueKind.Conversion:
                        case BehaviorValueKind.FieldRead:
                        case BehaviorValueKind.ArrayElementRead:
                        case BehaviorValueKind.Address:
                            foreach (int input in origin.Value.InputValueIds.Take(1))
                            {
                                pending.Enqueue(origin.Reference with { ValueId = input });
                            }
                            break;
                        case BehaviorValueKind.NewObject:
                        case BehaviorValueKind.NewArray:
                        case BehaviorValueKind.Local:
                        case BehaviorValueKind.Constant:
                            break;
                        default:
                            yield return new WriteSubject(origin.Reference, "被写对象来源尚未闭合");
                            break;
                    }
                }
            }
        }

        private readonly record struct WriteSubject(BehaviorValueReference? Reference, string? Failure = null);
    }

    /// <summary>函数自身的真实行为，与日志豁免无关。</summary>
    public enum MethodEffectKind
    {
        /// <summary>全部必要实现均无外部修改。</summary>
        Getter,
        /// <summary>存在已经证明的外部修改或业务对象传出。</summary>
        Setter,
    }

    /// <summary>保存已经证明、可以复核的调用过程和写入位置。</summary>
    public sealed record EffectEvidence(IReadOnlyList<string> MethodPath, int Position, string Detail);

    /// <summary>保存一个根函数的真实判断及 Setter 证据。</summary>
    public sealed record MethodEffect(string MethodId, MethodEffectKind Kind, EffectEvidence? Evidence);

    /// <summary>保存行为结论和本模块耗时。</summary>
    public sealed record EffectAnalysisResult(IReadOnlyList<MethodEffect> Methods, TimeSpan Elapsed)
    {
        /// <summary>尚未证明的根函数及其原始失败过程。</summary>
        public IReadOnlyDictionary<string, EffectEvidence> Failures { get; init; } = new Dictionary<string, EffectEvidence>();
    }
}
