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
            using ValueSourceIndex.IntegerPathProof pathProof = resolution.ValueSources.CreatePathProof();
            HashSet<string> conditionalMethods = resolution.Behaviors.Methods.Where(ValueSourceIndex.IntegerPathProof.HasPathConditions)
                .Select(body => body.MethodId).ToHashSet(StringComparer.Ordinal);
            HashSet<int> pathConditions = instances.Where(instance => conditionalMethods.Contains(instance.MethodId)).Select(instance => instance.RootId).ToHashSet();

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
                        current.Subject = new WriteSubject(null, "修改位置之前的调用尚未证明可以正常返回", current.Subject.Witness);
                    }
                    if (current.Subject.Failure != null)
                    {
                        if (boundaries.TryGetValue(current.Instance, out EffectEvidence? previous)
                            && previous.MethodPath.Count <= current.Evidence.MethodPath.Count)
                        {
                            continue;
                        }
                        boundaries[current.Instance] = current.Evidence with { Detail = current.Subject.Failure };
                    }
                    if (current.Instance == rootId)
                    {
                        if (current.Subject.Failure == null)
                        {
                            proofs.Add(rootId, current.Evidence);
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
                            mapped = arguments.SelectMany(argument => ReadWriteSubjects(catalog, resolution, argument, rootId,
                                pathProof, current.Subject.Witness, pathConditions.Contains(rootId)));
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
                    foreach (WriteSubject subject in write.ReceiverValueId == null ? ReadStaticWriteSubjects(instance, write)
                                 : ReadWriteSubjects(catalog, resolution, new BehaviorValueReference(instance.MethodId, write.ReceiverValueId.Value, instance.Id), instance.RootId,
                                     pathProof, (new BehaviorValueReference(instance.MethodId, write.ReceiverValueId.Value, instance.Id), write.Point), pathConditions.Contains(instance.RootId)))
                    {
                        Propagate(instance.Id, subject, evidence, write.Point);
                    }
                }
            }

            // 静态写入没有接收对象，但仍必须满足写入之前的引用及转换条件。
            IEnumerable<WriteSubject> ReadStaticWriteSubjects(MethodCallInstance instance, BehaviorWrite write)
            {
                if (pathConditions.Contains(instance.RootId))
                {
                    string? failure = null;
                    try
                    {
                        var selected = pathProof.ReadSelectedOriginsAtPoint(instance.RootId, Array.Empty<ValueOrigin>(), instance.Id, write.Point, null);
                        if (!selected.Possible)
                        {
                            if (selected.Complete)
                            {
                                yield break;
                            }
                            failure = "单次经过循环未取得静态写入见证，不能据此排除其它迭代";
                        }
                    }
                    catch (AnalysisException exception)
                    {
                        failure = exception.Message;
                    }
                    if (failure != null)
                    {
                        yield return new WriteSubject(null, failure);
                        yield break;
                    }
                }
                yield return new WriteSubject(null);
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
                        Queue<(BehaviorValueReference Reference, IReadOnlySet<(BehaviorValueReference, ReturnedValuePath?)> Ancestors)> returnedObjects = new();
                        returnedObjects.Enqueue((reference, new HashSet<(BehaviorValueReference, ReturnedValuePath?)>()));
                        while (returnedObjects.TryDequeue(out var observation) && proof == null)
                        {
                            BehaviorValueReference current = observation.Reference;
                            try
                            {
                                IReadOnlyList<ValueOrigin> returnedOrigins = resolution.ValueSources.GetCallOrigins(current);
                                if (pathConditions.Contains(instanceId) && returnedOrigins.Any(origin => origin.Value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.NewArray))
                                {
                                    var selected = pathProof.ReadSelectedOriginsAtPoint(instanceId, returnedOrigins, instanceId, returned.Point, current, site.Point, normalReturn: true);
                                    if (!selected.Complete)
                                    {
                                        boundaries.TryAdd(instanceId, new EffectEvidence(new[] { root.Id }, returned.Position, "单次经过循环不能排除其它迭代返回新对象"));
                                    }
                                    returnedOrigins = selected.Origins;
                                }
                                foreach (ValueOrigin origin in returnedOrigins
                                             .OrderBy(origin => origin.Value.Kind == BehaviorValueKind.NewObject ? 0 : 1))
                                {
                                    // 同一返回位置的祖先对象已在更宽条件下读取；兄弟槽的不同观察不能合并。
                                    if (observation.Ancestors.Contains((origin.Reference, origin.ReturnPath)))
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
                                        HashSet<(BehaviorValueReference, ReturnedValuePath?)> ancestors = new(observation.Ancestors)
                                        {
                                            (origin.Reference, origin.ReturnPath),
                                        };
                                        foreach (BehaviorValueReference member in resolution.ValueSources.ReadContainerValues(current, origin, returned.Point,
                                            failure => boundaries.TryAdd(instanceId, new EffectEvidence(new[] { root.Id }, returned.Position, failure)), site.Point,
                                            pathConditions.Contains(instanceId) ? pathProof : null))
                                        {
                                            returnedObjects.Enqueue((member, ancestors));
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
            MethodCatalogResult catalog, CallTargetResolutionResult resolution, BehaviorValueReference reference, int instanceId,
            ValueSourceIndex.IntegerPathProof pathProof, (BehaviorValueReference Receiver, BehaviorFlowPoint Point)? witness, bool hasPathConditions)
        {
            Queue<BehaviorValueReference> pending = new(new[] { reference });
            HashSet<BehaviorValueReference> visited = new();
            bool complete = true;
            bool publishedSubject = false;
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
                    if (witness.HasValue && (hasPathConditions && origins.Any(origin => origin.Value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance
                            && !origin.Value.IsManagedReferenceSlot)
                        || origins.Any(origin => origin.Value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.NewArray or BehaviorValueKind.Local or BehaviorValueKind.Constant)
                            && origins.Any(origin => origin.Value.Kind is not (BehaviorValueKind.NewObject or BehaviorValueKind.NewArray or BehaviorValueKind.Local or BehaviorValueKind.Constant))))
                    {
                        var selected = pathProof.ReadSelectedOriginsAtPoint(instanceId, origins,
                            witness.Value.Receiver.InstanceId, witness.Value.Point, witness.Value.Receiver);
                        origins = selected.Origins;
                        complete &= selected.Complete;
                    }
                }
                catch (AnalysisException exception)
                {
                    origins = null;
                    failure = exception.Message;
                }
                if (origins == null)
                {
                    publishedSubject = true;
                    yield return new WriteSubject(current, failure, witness);
                    continue;
                }
                foreach (ValueOrigin origin in origins)
                {
                    switch (origin.Value.Kind)
                    {
                        case BehaviorValueKind.CurrentInstance:
                        case BehaviorValueKind.Parameter:
                            publishedSubject = true;
                            yield return new WriteSubject(origin.Reference, Witness: witness);
                            break;
                        case BehaviorValueKind.FieldRead when origin.Value.InputValueIds.Count == 0:
                        case BehaviorValueKind.Address when origin.Value.Member != null && origin.Value.InputValueIds.Count == 0:
                            publishedSubject = true;
                            yield return new WriteSubject(null, Witness: witness);
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
                            publishedSubject = true;
                            yield return new WriteSubject(origin.Reference, "被写对象来源尚未闭合", witness);
                            break;
                    }
                }
            }
            if (!complete && !publishedSubject)
            {
                yield return new WriteSubject(reference, "单次经过循环未取得旧对象写入见证，不能据此排除其它迭代", witness);
            }
        }

        private readonly record struct WriteSubject(BehaviorValueReference? Reference, string? Failure = null,
            (BehaviorValueReference Receiver, BehaviorFlowPoint Point)? Witness = null);
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
