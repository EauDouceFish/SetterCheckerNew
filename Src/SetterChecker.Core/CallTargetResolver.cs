using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SetterChecker.Core
{
    /// <summary>在固定函数目录上连接调用，不按入口或路径复制函数。</summary>
    public sealed class CallTargetResolver
    {
        private static readonly ConditionalWeakTable<ResolvedCallTarget, TargetDescription> s_targetDescriptions = new();
        private static readonly ConditionalWeakTable<ResolvedCallTarget, string> s_targetIdentities = new();

        // 每份函数体只读一次，固定关系只在新增事实时通知使用方。
        /// <summary>连接共享调用关系；Setter 立即向上通知，Getter 在关系闭合后确认。</summary>
        public async Task<CallTargetResolutionResult> ResolveAsync(MaterialSet material, MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> roots, int jobs, CancellationToken cancellationToken = default,
            bool requireCompleteCalls = true, Action<string>? progress = null, bool useReflectionBaseline = false)
        {
            if (jobs <= 0)
            {
                throw new AnalysisException("工作数量必须是正整数。");
            }

            Stopwatch watch = Stopwatch.StartNew();
            Dictionary<string, MethodEntry> methods = new(StringComparer.Ordinal);
            Dictionary<string, MethodBehavior> bodies = new(StringComparer.Ordinal);
            ValueSourceIndex sources = new(catalog, methods, bodies, cancellationToken) { UseReflectionBaseline = useReflectionBaseline };
            EffectAnalyzer.TopTracker effects = new(catalog, sources);
            Dictionary<(string Method, BehaviorFlowPoint Point), ResolvedCall> calls = new();
            Dictionary<(string Method, BehaviorFlowPoint Point), PendingCall> failures = new();
            DispatchIndex dispatch = new();
            RegistrationIndex registrations = new(material, catalog, sources.Timing, jobs, cancellationToken);
            Queue<string> ready = new();
            HashSet<string> queued = new(StringComparer.Ordinal);
            HashSet<string> unread = new(StringComparer.Ordinal);
            HashSet<string> deferred = new(StringComparer.Ordinal);
            Queue<string> deferredReady = new();
            Queue<string> revisit = new();
            HashSet<string> revisitSet = new(StringComparer.Ordinal);
            HashSet<string> behaviorNeeded = new(StringComparer.Ordinal);
            HashSet<string> rootIds = roots.Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> liveBehavior = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> behaviorEdges = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> behaviorCallers = new(StringComparer.Ordinal);
            Dictionary<string, string> demandParent = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> demandChildren = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> registrationTargets = new(StringComparer.Ordinal);
            Queue<(IEnumerator<(MethodEntry Owner, MethodEntry? Target)> Items, MethodEntry? Required, BehaviorMemberReference? Member)> registrationRequests = new();
            List<(IEnumerator<(MethodEntry Owner, MethodEntry? Target)> Items, MethodEntry? Required, BehaviorMemberReference? Member)> pausedRegistrations = new();
            TimeSpan readingTime = TimeSpan.Zero;
            long lastProgress = 0;
            long demandUpdates = 0;
            long demandVisits = 0;
            long scheduleRequests = 0;
            long scheduledMethods = 0;
            long processedMethods = 0;
            long processedCalls = 0;
            long skippedCalls = 0;
            long deferredCalls = 0;
            long resolvedCalls = 0;
            long changedCalls = 0;
            long resumedMethods = 0;
            string? interrupted = null;
            // 查询只查登记状态和结果，不搜索任何上下游关系。
            bool NeedsBehavior(string method) => behaviorNeeded.Contains(method) && IsLive(method);

            // 尚未确定的入口，或被仍在展开的函数需要完整摘要的函数保持活跃；TOP 函数的其余调用不影响任何结论。
            bool IsLive(string method) => !effects.IsTop(method)
                && (rootIds.Contains(method) && !effects.IsDecided(method) || liveBehavior.Contains(method));

            // 首次激活时只记一个有效的直接调用者，不复制函数或分析环境。
            void ActivateBehavior(string caller, string method)
            {
                Queue<(string Caller, string Method)> pending = new(new[] { (caller, method) });
                while (pending.TryDequeue(out var next))
                {
                    demandVisits++;
                    if (effects.IsTop(next.Method) || !liveBehavior.Add(next.Method))
                    {
                        continue;
                    }
                    demandParent.Add(next.Method, next.Caller);
                    if (bodies.ContainsKey(next.Method))
                    {
                        Schedule(next.Method);
                    }
                    if (!demandChildren.TryGetValue(next.Caller, out HashSet<string>? children))
                    {
                        children = new(StringComparer.Ordinal);
                        demandChildren.Add(next.Caller, children);
                    }
                    children.Add(next.Method);
                    if (behaviorEdges.TryGetValue(next.Method, out HashSet<string>? targets))
                    {
                        foreach (string target in targets)
                        {
                            pending.Enqueue((next.Method, target));
                        }
                    }
                }
            }

            // 先清除受影响分支，再从有效调用者恢复共享函数，避免循环互相保留失效需求。
            effects.Settled = settled =>
            {
                HashSet<string> affected = new(StringComparer.Ordinal);
                Stack<string> pending = new(settled.Where(method => demandParent.ContainsKey(method) || demandChildren.ContainsKey(method)));
                while (pending.TryPop(out string? method))
                {
                    if (!affected.Add(method))
                    {
                        continue;
                    }
                    liveBehavior.Remove(method);
                    if (demandParent.Remove(method, out string? parent) && demandChildren.TryGetValue(parent, out var siblings))
                    {
                        siblings.Remove(method);
                    }
                    if (demandChildren.Remove(method, out var children))
                    {
                        foreach (string child in children)
                        {
                            pending.Push(child);
                        }
                    }
                }
                foreach (string method in affected)
                {
                    if (IsLive(method) || effects.IsTop(method) || !behaviorCallers.TryGetValue(method, out var callers))
                    {
                        continue;
                    }
                    foreach (string caller in callers)
                    {
                        demandVisits++;
                        if (IsLive(caller))
                        {
                            ActivateBehavior(caller, method);
                            break;
                        }
                    }
                }
                demandUpdates += affected.Count != 0 ? 1 : 0;
                demandVisits += affected.Count;
                sources.InvalidateValueReaders();
            };
            sources.IsBehaviorNeeded = NeedsBehavior;

            // 多个入口共用同一函数事实和编号。
            void Include(MethodEntry method, bool behavior)
            {
                sources.Include(method);
                bool activated = behavior && behaviorNeeded.Add(method.Id);
                if (!bodies.ContainsKey(method.Id))
                {
                    activated |= unread.Add(method.Id);
                }
                if (activated)
                {
                    Schedule(method.Id);
                }
            }

            // 通知只进入一次队列；读取过的函数不再读取语法或 IL。
            void Schedule(string method)
            {
                scheduleRequests++;
                if (queued.Add(method))
                {
                    ready.Enqueue(method);
                    scheduledMethods++;
                }
            }

            // 普通调用优先复用已知 Setter，参数和返回信息独立保留。
            void Connect(ResolvedCall call)
            {
                var key = (call.CallerMethodId, call.Call.Point);
                calls[key] = call;
                if (!call.ValuesOnly)
                {
                    if (!behaviorEdges.TryGetValue(call.CallerMethodId, out HashSet<string>? targets))
                    {
                        targets = new(StringComparer.Ordinal);
                        behaviorEdges.Add(call.CallerMethodId, targets);
                    }
                    foreach (ResolvedCallTarget target in call.Targets)
                    {
                        if (!targets.Add(target.MethodId))
                        {
                            continue;
                        }
                        sources.InvalidateValueReaders();
                        if (!behaviorCallers.TryGetValue(target.MethodId, out HashSet<string>? callers))
                        {
                            callers = new(StringComparer.Ordinal);
                            behaviorCallers.Add(target.MethodId, callers);
                        }
                        callers.Add(call.CallerMethodId);
                        if (NeedsBehavior(call.CallerMethodId))
                        {
                            ActivateBehavior(call.CallerMethodId, target.MethodId);
                        }
                    }
                }
                sources.Bind(call);
                effects.Bind(call);
                if (call.Failure == null)
                {
                    failures.Remove(key);
                }
                else
                {
                    failures[key] = new(call.CallerMethodId, call.Call) { Failure = call.Failure };
                }
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    bool behavior = !call.ValuesOnly && NeedsBehavior(call.CallerMethodId);
                    if (behavior || sources.NeedsCall(call.CallerMethodId, call.Call.Point))
                    {
                        Include(methods[target.MethodId], behavior);
                    }
                }
            }

            foreach (MethodEntry root in roots)
            {
                Include(root, true);
            }

            try
            {
                while (unread.Count != 0 || ready.Count != 0 || registrationRequests.Count != 0 || deferred.Count != 0
                    || pausedRegistrations.Count != 0 || revisit.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 需求可能被后续调用重新激活；暂停不等于候选已经找全。
                    if (unread.Count == 0 && ready.Count == 0 && deferred.Count == 0 && registrationRequests.Count == 0 && revisit.Count == 0)
                    {
                        int retained = 0;
                        for (int index = 0; index < pausedRegistrations.Count; index++)
                        {
                            var paused = pausedRegistrations[index];
                            if (sources.NeedsRegistration(paused.Required, paused.Member))
                            {
                                registrationRequests.Enqueue(paused);
                            }
                            else
                            {
                                pausedRegistrations[retained++] = paused;
                            }
                        }
                        pausedRegistrations.RemoveRange(retained, pausedRegistrations.Count - retained);
                        if (registrationRequests.Count == 0)
                        {
                            break;
                        }
                    }
                    if (watch.ElapsedMilliseconds - lastProgress >= 5000)
                    {
                        progress?.Invoke($"已读取 {bodies.Count} 个不同函数，固定调用 {calls.Count} 个；耗时 {watch.Elapsed.TotalSeconds:F1} 秒。");
                        lastProgress = watch.ElapsedMilliseconds;
                    }
                    if (unread.Count != 0 && (bodies.Count == 0 || ready.Count == 0))
                    {
                        MethodEntry[] batch = unread.Order(StringComparer.Ordinal).Select(id => methods[id]).ToArray();
                        unread.Clear();
                        BehaviorReadResult read;
                        using (sources.Timing.Measure(AnalysisTiming.Part.Bodies))
                        {
                            read = await new BehaviorReader().ReadAvailableAsync(material, catalog, batch, jobs, cancellationToken).ConfigureAwait(false);
                        }
                        readingTime += read.Elapsed;
                        foreach (MethodBehavior body in read.Methods)
                        {
                            bodies.Add(body.MethodId, body);
                            sources.ReadBody(body);
                            effects.ReadDirect(body);
                            Schedule(body.MethodId);
                        }
                    }
                    if (!ready.TryDequeue(out string? current))
                    {
                        if (deferred.Count != 0)
                        {
                            // 一次唤醒一个等待函数；它请求的新函数体先读完，不让整批函数反复空转。
                            string method = deferredReady.Dequeue();
                            deferred.Remove(method);
                            Schedule(method);
                            resumedMethods++;
                            continue;
                        }
                        if (registrationRequests.Count == 0)
                        {
                            // 依赖变化引起的重算放到新函数与注册候选之后，一批变化只重算一次。
                            while (revisit.TryDequeue(out string? again))
                            {
                                revisitSet.Remove(again);
                                Schedule(again);
                            }
                            continue;
                        }
                        var request = registrationRequests.Peek();
                        if (!sources.NeedsRegistration(request.Required, request.Member))
                        {
                            pausedRegistrations.Add(registrationRequests.Dequeue());
                            sources.Timing.Count("注册需求暂停次数");
                            continue;
                        }
                        bool complete = false;
                        // 一批候选交给主分析处理后，再决定是否继续读取注册位置。
                        for (int count = 0; count < 64; count++)
                        {
                            if (!request.Items.MoveNext())
                            {
                                complete = true;
                                break;
                            }
                            var item = request.Items.Current;
                            Include(item.Owner, false);
                            if (item.Target != null)
                            {
                                if (!registrationTargets.TryGetValue(item.Owner.Id, out HashSet<string>? targets))
                                {
                                    targets = new(StringComparer.Ordinal);
                                    registrationTargets.Add(item.Owner.Id, targets);
                                }
                                targets.Add(item.Target.Id);
                                sources.RequestRegistrationCall(item.Owner.Id, item.Target);
                            }
                        }
                        if (complete)
                        {
                            request.Items.Dispose();
                            registrationRequests.Dequeue();
                            if (request.Required != null)
                            {
                                sources.CompleteRegistration(request.Required.Id);
                            }
                        }
                    }
                    else
                    {
                        processedMethods++;
                        queued.Remove(current);
                        if (!bodies.ContainsKey(current))
                        {
                            continue;
                        }
                        bool active = NeedsBehavior(current) || sources.NeedsValues(current) || registrationTargets.ContainsKey(current);
                        foreach (BehaviorCall call in active ? sources.ReadPendingCalls(current) : Enumerable.Empty<BehaviorCall>())
                        {
                            processedCalls++;
                            var key = (current, call.Point);
                            bool valueNeeded = sources.NeedsCall(current, call.Point);
                            bool registration = registrationTargets.TryGetValue(current, out HashSet<string>? targets)
                                && targets.Contains(catalog.ResolveMethodDefinition(call.Target, true).Method.Id);
                            if (!NeedsBehavior(current) && !valueNeeded && !registration)
                            {
                                // 暂停需求不等于删除调用；后续重新激活时仍需处理。
                                sources.DeferCall(current, call.Point);
                                skippedCalls++;
                                continue;
                            }
                            if (unread.Count != 0 && call.Kind is BehaviorCallKind.Virtual or BehaviorCallKind.Delegate)
                            {
                                sources.DeferCall(current, call.Point);
                                deferredCalls++;
                                if (deferred.Add(current))
                                {
                                    deferredReady.Enqueue(current);
                                }
                                continue;
                            }
                            using var inputs = sources.CaptureInputs();
                            sources.ObserveCallDemand(current, call.Point);
                            try
                            {
                                if (calls.TryGetValue(key, out ResolvedCall? known) && known.IsMetadataBinding)
                                {
                                    foreach (ResolvedCallTarget target in known.Targets)
                                    {
                                        if (!bodies.ContainsKey(target.MethodId))
                                        {
                                            Include(methods[target.MethodId], NeedsBehavior(current));
                                        }
                                    }
                                    continue;
                                }
                                ResolvedCall resolved = await ResolveCall(catalog, sources, methods[current], call, dispatch,
                                    stopAfterTarget: valueNeeded || registration ? null : target =>
                                    {
                                        effects.Bind(new(current, call, new[] { target }));
                                        return ValueTask.FromResult(effects.IsTop(current));
                                    }, registrationBinding: registration && !NeedsBehavior(current) && !valueNeeded,
                                    registrationTargets: registration ? targets : null).ConfigureAwait(false);
                                resolvedCalls++;
                                if (known == null || known.Failure != resolved.Failure
                                    || known.InvokesUnboundParameter != resolved.InvokesUnboundParameter
                                    || !known.Targets.Select(TargetIdentity).SequenceEqual(resolved.Targets.Select(TargetIdentity))
                                    || !known.Writes.SequenceEqual(resolved.Writes)
                                    || known.CompletesWithoutTarget != resolved.CompletesWithoutTarget)
                                {
                                    changedCalls++;
                                    Connect(resolved);
                                }
                            }
                            catch (AnalysisException exception)
                            {
                                failures[key] = new(current, call) { Failure = exception.Message };
                            }
                            finally
                            {
                                sources.SaveCallInputs(current, call.Point, inputs.Inputs);
                            }
                        }
                        if (sources.NeedsValues(current))
                        {
                            sources.UpdateReturns(current);
                        }
                        if (NeedsBehavior(current))
                        {
                            effects.ReadDirect(bodies[current], sources.ReadReflectionWrites(current));
                        }
                    }
                    foreach (string reader in sources.TakeChangedReaders())
                    {
                        if (methods.ContainsKey(reader) && !bodies.ContainsKey(reader))
                        {
                            unread.Add(reader);
                            Schedule(reader);
                        }
                        else if (!queued.Contains(reader) && revisitSet.Add(reader))
                        {
                            revisit.Enqueue(reader);
                        }
                    }
                    foreach (BehaviorMemberReference member in sources.TakeRegistrationMembers())
                    {
                        registrationRequests.Enqueue((registrations.FindMemberWriters(member).GetEnumerator(), null, member));
                    }
                    foreach (var registered in sources.TakeRegistrationMethods())
                    {
                        registrationRequests.Enqueue((registrations.FindCallers(registered).GetEnumerator(), registered, null));
                    }
                }
            }
            catch (AnalysisException exception) when (!requireCompleteCalls)
            {
                interrupted = exception.Message;
            }
            finally
            {
                foreach (var request in registrationRequests)
                {
                    request.Items.Dispose();
                }
                foreach (var request in pausedRegistrations)
                {
                    request.Items.Dispose();
                }
            }

            sources.CompleteReturns();
            foreach (MethodBehavior body in bodies.Values.Where(body => !effects.IsTop(body.MethodId)))
            {
                effects.ReadDirect(body, sources.ReadReflectionWrites(body.MethodId));
            }

            CallTargetResolutionResult result = new(methods.Values.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray(),
                new(bodies.Values.OrderBy(body => body.MethodId, StringComparer.Ordinal).ToArray(), readingTime),
                calls.Values.OrderBy(call => call.CallerMethodId, StringComparer.Ordinal)
                    .ThenBy(call => call.Call.Point.BlockId).ThenBy(call => call.Call.Point.Order).ToArray(), sources, watch.Elapsed)
            {
                Top = effects,
                Failure = interrupted,
                PendingCalls = failures.Values.Where(call => NeedsBehavior(call.CallerMethodId) || sources.NeedsValues(call.CallerMethodId))
                    .OrderBy(call => call.CallerMethodId, StringComparer.Ordinal).ThenBy(call => call.Call.Point.BlockId).ToArray(),
            };
            int libraryModels = bodies.Values.Count(body => body.BodyKind == MethodBodyKind.LibraryModel);
            sources.Timing.Count("不同函数体读取次数", bodies.Count - libraryModels);
            sources.Timing.Count("外部库模型函数数", libraryModels);
            sources.Timing.Count("外部库回调目标数", calls.Values.Sum(call => call.Targets.Count(target => target.IsCallback)));
            sources.Timing.Count("行为需求状态更新次数", demandUpdates);
            sources.Timing.Count("行为需求登记及更新访问次数", demandVisits);
            sources.Timing.Count("函数调度请求次数", scheduleRequests);
            sources.Timing.Count("函数实际入队次数", scheduledMethods);
            sources.Timing.Count("函数实际处理次数", processedMethods);
            sources.Timing.Count("调用位置处理次数", processedCalls);
            sources.Timing.Count("无需求调用跳过次数", skippedCalls);
            sources.Timing.Count("等待函数体调用次数", deferredCalls);
            sources.Timing.Count("调用目标实际解析次数", resolvedCalls);
            sources.Timing.Count("调用关系实际更新次数", changedCalls);
            sources.Timing.Count("等待结束后函数入队次数", resumedMethods);
            progress?.Invoke($"函数体读取 {bodies.Count - libraryModels} 次，外部库模型 {libraryModels} 个；固定调用 {calls.Count} 个；未确定目标 {result.PendingCalls.Count} 个。");
            if (requireCompleteCalls && result.PendingCalls.FirstOrDefault(call => !effects.IsTop(call.CallerMethodId)) is PendingCall failure)
            {
                throw new AnalysisException(failure.Failure!);
            }
            return result;
        }

        // 各种调用都生成同一种接收对象和参数对应关系。
        private static async ValueTask<ResolvedCall> ResolveCall(MethodCatalogResult catalog, ValueSourceIndex sources, MethodEntry caller,
            BehaviorCall call, DispatchIndex dispatch,
            Func<ResolvedCallTarget, ValueTask<bool>>? stopAfterTarget = null, bool registrationBinding = false,
            IReadOnlySet<string>? registrationTargets = null)
        {
            using var timing = sources.Timing.Measure(AnalysisTiming.Part.Targets);
            ResolvedMethodDefinition declaration;
            using (sources.Timing.Measure(AnalysisTiming.Part.Declarations))
            {
                declaration = catalog.ResolveMethodDefinition(call.Target, true);
            }
            MethodEntry method = declaration.Method;
            BehaviorValueReference Reference(int value) => new(caller.Id, value, sources.GetMethodOrdinal(caller.Id));
            int? receiverValue = call.ReceiverValueId ?? (call.Kind == BehaviorCallKind.ObjectCreation ? call.ResultValueId : null);
            IReadOnlyList<BehaviorValueReference> receiver = receiverValue is int receiverId ? new[] { Reference(receiverId) } : Array.Empty<BehaviorValueReference>();
            IReadOnlyList<IReadOnlyList<BehaviorValueReference>> arguments = call.Arguments.Select(argument => (IReadOnlyList<BehaviorValueReference>)new[] { Reference(argument.ValueId) }).ToArray();
            List<ResolvedCallTarget> targets = new();
            List<BehaviorWrite> writes = new();
            bool completesWithoutTarget = false;
            bool dynamicBinding = false;
            bool delegateInvocation = false;
            bool invokesUnboundParameter = false;
            bool valuesOnly = false;
            string? failure = null;
            bool stopped = false;
            List<ResolvedCallTarget> library = new();
            RuntimeOperationRule? runtimeRule = RuntimeOperations.Find(catalog, method);
            if (runtimeRule != null)
            {
                dynamicBinding = true;
                completesWithoutTarget = true;
                if (runtimeRule.Operation == RuntimeOperation.ShallowCopy)
                {
                    ValueOrigin[] originals = receiver.SelectMany(value => sources.ReadLocalOrigins(value)).ToArray();
                    if (originals.Length == 0 || originals.Any(origin => origin.Value.Type == null
                        || origin.Value.Type.Id.EndsWith(']') || origin.Value.Type.Id == "System.Object"
                        || origin.Value.Kind is BehaviorValueKind.CallResult or BehaviorValueKind.NewArray))
                    {
                        failure = "浅复制的对象类型尚未确定，或数组复制来源尚未支持";
                    }
                    else
                    {
                        sources.SetResult(call, caller.Id, originals.Select(origin =>
                            Result(BehaviorValueKind.ShallowCopy, null, origin.Value.Type) with { BoundReceiver = receiver }).ToArray());
                    }
                }
                else if (runtimeRule.Operation == RuntimeOperation.CreateObject && call.ResultValueId is int objectValue)
                {
                    TypeEntry objectType = catalog.TypesById[method.TypeId];
                    sources.SetResult(call, caller.Id, new[]
                    {
                        Result(BehaviorValueKind.NewObject, null,
                            TypeReference(objectType, declaration.DeclaringTypeArguments))
                    });
                }
                else if (runtimeRule.Operation == RuntimeOperation.CreateObject)
                {
                    // 普通构造函数没有返回值；只需截断其新对象初始化路径。
                }
                else if (runtimeRule.Operation == RuntimeOperation.ReadCollection
                    && method.Name == "TryGetValue" && method.Parameters.Count == 2
                    && method.Parameters[1].RefKind == RefKind.Out)
                {
                    int outputIndex = 1;
                    BehaviorValue value = new(call.Arguments[outputIndex].ValueId, BehaviorValueKind.FieldRead,
                        "集合元素", null, receiverValue is int input ? new[] { input } : Array.Empty<int>())
                    {
                        Type = catalog.ReadMethodReturnType(method),
                    };
                    sources.SetOutResult(call, caller.Id, outputIndex, value);
                    sources.SetResult(call, caller.Id, new[] { Result(BehaviorValueKind.Computation, runtimeRule.Operation.ToString()) });
                }
                else if (runtimeRule.Operation == RuntimeOperation.ReadCollection
                    && method.Name is "get_Item" or "get_Keys" or "get_Values" or "get_Default"
                    || runtimeRule.Operation == RuntimeOperation.WriteCollection && call.ResultValueId is int)
                {
                    ValueOrigin result = Result(BehaviorValueKind.FieldRead, runtimeRule.Operation.ToString(),
                        catalog.ReadMethodReturnType(method));
                    if (receiverValue is int input)
                    {
                        result = result with { Value = result.Value with { InputValueIds = new[] { input } } };
                    }
                    sources.SetResult(call, caller.Id, new[] { result });
                }
                else if (call.ResultValueId is int)
                {
                    sources.SetResult(call, caller.Id, new[] { Result(BehaviorValueKind.Computation, runtimeRule.Operation.ToString()) });
                }
                if (runtimeRule.Operation == RuntimeOperation.ConvertValue && method.Name is "Format" or "Concat" or "Join"
                    && catalog.TypesById[method.TypeId].FullName == "System.String")
                {
                    await ReadFormattedArguments().ConfigureAwait(false);
                }
            }
            else if (await ReadIterator().ConfigureAwait(false) || await ReadReflection().ConfigureAwait(false))
            {
                dynamicBinding = true;
                completesWithoutTarget = targets.Count == 0;
            }
            else if (call.Kind == BehaviorCallKind.Delegate || method.Kind == MethodKind.DelegateInvoke)
            {
                delegateInvocation = true;
                if (receiver.Count == 0)
                {
                    throw new AnalysisException($"委托调用没有接收对象：{caller.Id}");
                }

                bool foundOrigin = false;
                foreach (ValueOrigin origin in sources.GetOrigins(receiver[0]))
                {
                    foundOrigin = true;
                    if (origin.Value.Kind == BehaviorValueKind.Constant && origin.Value.Reference is "null" or "default")
                    {
                        continue;
                    }

                    if (origin.Value.Kind != BehaviorValueKind.Function || origin.Value.Method == null)
                    {
                        if (origin.Value.Kind == BehaviorValueKind.Parameter && origin.Reference.MethodId == caller.Id)
                        {
                            invokesUnboundParameter = true;
                            continue;
                        }
                        failure = $"委托绑定来源尚未闭合：{caller.Id} @ {call.Point.BlockId}；{origin.Value.Kind}：{origin.Value.Reference}";
                        continue;
                    }

                    ResolvedMethodDefinition target = catalog.ResolveMethodDefinition(origin.Value.Method, true);
                    IReadOnlyList<BehaviorValueReference> actualReceiver = origin.BoundReceiver
                        ?? origin.Value.InputValueIds.Select(value => origin.Reference with { ValueId = value }).ToArray();
                    foreach (ResolvedMethodDefinition implementation in origin.Value.UsesVirtualDispatch ? VirtualTargets(target, actualReceiver) : new[] { target })
                    {
                        if (await AddTarget(Bind(implementation, actualReceiver, arguments) with { DelegateDeclarationId = target.Method.Id }).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    if (stopped)
                    {
                        break;
                    }
                }

                if (targets.Count == 0)
                {
                    if (!foundOrigin)
                    {
                        failure = "委托的来源尚未取得";
                    }
                    completesWithoutTarget = failure == null;
                }
            }
            else if (call.Kind == BehaviorCallKind.ObjectCreation && catalog.IsDelegateType(catalog.TypesById[method.TypeId]))
            {
                dynamicBinding = true;
                ValueOrigin[] functions = call.Arguments.SelectMany(argument => sources.ReadLocalOrigins(Reference(argument.ValueId)))
                    .Where(origin => origin.Value.Kind == BehaviorValueKind.Function)
                    .Select(origin => origin with { BoundReceiver = arguments[0] }).ToArray();
                if (functions.Length == 0)
                {
                    throw new AnalysisException("委托构造的函数地址尚未确定");
                }
                sources.SetResult(call, caller.Id, functions);
                completesWithoutTarget = true;
            }
            else if (call.Kind == BehaviorCallKind.Virtual && (method.IsVirtual || method.IsAbstract))
            {
                IEnumerable<ResolvedMethodDefinition> candidates = VirtualTargets(declaration, receiver);
                // constrained. 前缀已给出实际值类型：只取声明在该类型上的实现，找不到时保留全部候选。
                if (call.ConstrainedReceiverType is BehaviorTypeReference constrainedType && !constrainedType.Id.StartsWith('!'))
                {
                    TypeEntry? exact = null;
                    try
                    {
                        exact = catalog.ResolveTypeDefinition(constrainedType);
                    }
                    catch (AnalysisException)
                    {
                    }
                    ResolvedMethodDefinition[] all = candidates.ToArray();
                    ResolvedMethodDefinition[] own = exact == null ? Array.Empty<ResolvedMethodDefinition>()
                        : all.Where(candidate => candidate.Method.TypeId == exact.Id).ToArray();
                    candidates = own.Length != 0 ? own : all;
                }
                foreach (ResolvedMethodDefinition target in candidates)
                {
                    if (await AddTarget(Bind(target, receiver, arguments)).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                if (targets.Count == 0)
                {
                    throw new AnalysisException($"接口或重写没有合法实现：{method.Id}");
                }
            }
            else
            {
                targets.Add(Bind(declaration, receiver, arguments));
            }
            foreach (ResolvedCallTarget libraryTarget in registrationBinding ? Array.Empty<ResolvedCallTarget>() : library.ToArray())
            {
                dynamicBinding = true;
                if (stopped || await ReadLibraryCallbacks(libraryTarget).ConfigureAwait(false))
                {
                    break;
                }
            }

            return new ResolvedCall(caller.Id, call, targets.Count <= 1 ? targets.ToArray()
                : targets.DistinctBy(TargetIdentity).OrderBy(TargetIdentity, StringComparer.Ordinal).ToArray())
            {
                IsMetadataBinding = !dynamicBinding && call.Kind is BehaviorCallKind.Direct or BehaviorCallKind.ObjectCreation,
                CompletesWithoutTarget = completesWithoutTarget,
                Writes = writes,
                Failure = stopped ? null : failure,
                IsDelegateInvocation = delegateInvocation,
                InvokesUnboundParameter = invokesUnboundParameter,
                ValuesOnly = valuesOnly,
                RuntimeRule = runtimeRule,
            };

            // string 格式化会调用对象实参的 ToString 或 IFormattable.ToString；只连接业务代码中的实现，标准库实现按纯转换处理。
            async ValueTask ReadFormattedArguments()
            {
                TypeEntry objectType = catalog.ReadPrimitiveType("System.Object");
                List<ResolvedMethodDefinition> declarations = new();
                foreach (TypeEntry? type in new TypeEntry?[] { objectType, catalog.ReadNamedRuntimeType("System.IFormattable", caller) })
                {
                    MethodEntry? entry = type == null ? null : catalog.GetMethods(type).FirstOrDefault(candidate => candidate.Name == "ToString" && !candidate.IsStatic
                        && (type == objectType ? candidate.Parameters.Count == 0 : candidate.Parameters.Count == 2));
                    if (entry != null)
                    {
                        declarations.Add(catalog.ResolveMethodDefinition(catalog.ReadMethodReference(entry), true));
                    }
                }
                for (int index = 0; index < method.Parameters.Count && index < arguments.Count; index++)
                {
                    string parameterType = method.Parameters[index].TypeId;
                    IReadOnlyList<IReadOnlyList<BehaviorValueReference>> values;
                    if (parameterType == "System.Object")
                    {
                        values = new[] { arguments[index] };
                    }
                    else if (parameterType == "System.Object[]" && arguments[index].Count == 1)
                    {
                        try
                        {
                            values = sources.ReadArrayArguments(arguments[index][0], sources.ReadArrayLength(arguments[index][0]));
                        }
                        catch (AnalysisException)
                        {
                            failure = "格式化参数数组来源尚未确定";
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                    foreach (IReadOnlyList<BehaviorValueReference> value in values)
                    {
                        foreach (ResolvedMethodDefinition declaration2 in declarations)
                        {
                            foreach (ResolvedMethodDefinition implementation in VirtualTargets(declaration2, value))
                            {
                                // 只连接有源码的业务类型实现；标准库和外部工具 DLL（如 Bee）的 ToString 不属于战斗逻辑。
                                if (implementation.Method.SourceSymbol == null || implementation.Method.AssemblyPath == objectType.AssemblyPath
                                    || RuntimeOperations.Find(catalog, implementation.Method) != null)
                                {
                                    continue;
                                }
                                IReadOnlyList<IReadOnlyList<BehaviorValueReference>> parameters = implementation.Method.Parameters
                                    .Select(_ => (IReadOnlyList<BehaviorValueReference>)Array.Empty<BehaviorValueReference>()).ToArray();
                                sources.Include(implementation.Method);
                                if (await AddTarget(new ResolvedCallTarget(implementation.Method.Id,
                                    catalog.ReadMethodReference(implementation.Method, implementation.DeclaringTypeArguments, implementation.MethodTypeArguments),
                                    value, parameters, implementation.DeclaringTypeArguments.Select(type => type.Text).ToArray())).ConfigureAwait(false))
                                {
                                    return;
                                }
                            }
                        }
                    }
                }
            }

            // 枚举对象保存延迟函数及原实参；只有 MoveNext 执行该函数体。
            async ValueTask<bool> ReadIterator()
            {
                if (receiver.Count == 0 || method.Parameters.Count != 0 || method.Name is not ("GetEnumerator" or "MoveNext" or "get_Current" or "Dispose" or "Reset"))
                {
                    return false;
                }
                TypeEntry contract = catalog.TypesById[method.TypeId];
                if (contract.AssemblyPath != catalog.ReadPrimitiveType("System.Object").AssemblyPath
                    || contract.FullName.Split('<')[0] is not ("System.Collections.IEnumerable" or "System.Collections.IEnumerator"
                        or "System.Collections.Generic.IEnumerable" or "System.Collections.Generic.IEnumerator" or "System.IDisposable"))
                {
                    return false;
                }
                ValueOrigin[] origins = sources.GetOrigins(receiver[0], resolveRuntimeTypes: false).ToArray();
                if (!origins.Any(origin => origin.Value.Kind == BehaviorValueKind.Iterator))
                {
                    if (origins.Length == 0 || origins.Any(origin => origin.Value.Kind == BehaviorValueKind.CallResult))
                    {
                        failure = "枚举对象来源尚未确定";
                        return true;
                    }
                    return false;
                }
                foreach (ValueOrigin origin in origins)
                {
                    if (IsNull(origin))
                    {
                        continue;
                    }
                    if (origin.Value.Kind != BehaviorValueKind.Iterator || origin.Value.Method == null)
                    {
                        failure = "枚举对象仍含未确定的来源";
                        continue;
                    }
                    ResolvedMethodDefinition body = catalog.ResolveMethodDefinition(origin.Value.Method, false);
                    if (method.Name == "Dispose")
                    {
                        if (body.Method.SourceSymbol!.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax().DescendantNodes()
                            .Any(node => node is FinallyClauseSyntax or UsingStatementSyntax or CommonForEachStatementSyntax
                                || node is LocalDeclarationStatementSyntax local && local.UsingKeyword.RawKind != 0)))
                        {
                            failure = "迭代器释放时的 finally 行为尚未单独连接";
                        }
                        continue;
                    }
                    if (method.Name is "GetEnumerator" or "Reset")
                    {
                        continue;
                    }
                    valuesOnly = method.Name == "get_Current";
                    IReadOnlyList<BehaviorValueReference> capturedReceiver = origin.BoundReceiver
                        ?? (body.Method.IsStatic ? Array.Empty<BehaviorValueReference>() : new[] { origin.Reference with { ValueId = origin.Value.InputValueIds[0] } });
                    IReadOnlyList<IReadOnlyList<BehaviorValueReference>> capturedArguments = origin.IteratorArguments
                        ?? origin.Value.InputValueIds.Skip(body.Method.IsStatic ? 0 : 1)
                            .Select(value => (IReadOnlyList<BehaviorValueReference>)new[] { origin.Reference with { ValueId = value } }).ToArray();
                    ResolvedCallTarget target = Bind(body, capturedReceiver, capturedArguments);
                    if (valuesOnly)
                    {
                        targets.Add(target);
                    }
                    else if (await AddTarget(target).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                if (method.Name == "GetEnumerator")
                {
                    sources.SetResult(call, caller.Id, origins);
                }
                else if (method.Name == "MoveNext")
                {
                    sources.SetResult(call, caller.Id, new[] { Result(BehaviorValueKind.Computation, "枚举是否继续") });
                }
                return true;
            }

            // 只识别实际核心库的反射操作；业务包装函数仍按普通调用分析。
            async ValueTask<bool> ReadReflection()
            {
                using var reflectionTiming = sources.Timing.Measure(AnalysisTiming.Part.Reflection);
                TypeEntry owner = catalog.TypesById[method.TypeId];
                if (owner.AssemblyPath != catalog.ReadPrimitiveType("System.Object").AssemblyPath)
                {
                    return false;
                }
                string typeName = owner.FullName;
                if (sources.UseReflectionBaseline && (typeName == "System.Type"
                    && method.Name is "GetField" or "GetFields" or "GetProperty" or "GetProperties" or "GetMethod" or "GetMethods"
                        or "GetConstructor" or "GetConstructors" or "GetMember" or "GetMembers" or "MakeGenericType" or "get_BaseType"
                        or "IsAssignableFrom" or "IsSubclassOf" or "IsInstanceOfType"
                    || typeName is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo"
                        && method.Name == "Invoke"
                    || typeName is "System.Reflection.FieldInfo" or "System.Reflection.PropertyInfo" && method.Name is "GetValue" or "SetValue"
                    || typeName == "System.Activator" && method.Name == "CreateInstance"))
                {
                    failure = ValueSourceIndex.ReflectionBaselineFailure;
                    return true;
                }
                if (!sources.NeedsCall(caller.Id, call.Point) && typeName == "System.Type"
                    && method.Name is "GetField" or "GetFields" or "GetProperty" or "GetProperties"
                        or "GetMethod" or "GetMethods" or "GetConstructor" or "GetConstructors" or "GetMember" or "GetMembers")
                {
                    ValueOrigin[] metadata = sources.GetOrigins(receiver.Single(), resolveRuntimeTypes: false).ToArray();
                    if (metadata.Length != 0 && metadata.All(origin => origin.Value.Kind == BehaviorValueKind.Type || IsNull(origin)))
                    {
                        return true;
                    }
                }
                if (typeName == "System.Array" && method.Name == "CreateInstance" && method.Parameters.Count == 2
                    && method.Parameters[1].TypeId == "System.Int32")
                {
                    if (!sources.NeedsCall(caller.Id, call.Point))
                    {
                        return true;
                    }
                    List<ValueOrigin> arrays = new();
                    foreach (ValueOrigin element in sources.GetOrigins(arguments[0][0]))
                    {
                        if (element.Value.Kind != BehaviorValueKind.Type || element.Value.Type == null)
                        {
                            failure = "动态数组的元素类型尚未确定";
                            continue;
                        }
                        BehaviorTypeReference type = element.Value.Type with { Identity = new(element.Value.Type.Id + "[]") };
                        ValueOrigin array = Result(BehaviorValueKind.NewArray, null, type);
                        arrays.Add(array with { Value = array.Value with { InputValueIds = new[] { call.Arguments[1].ValueId } } });
                    }
                    sources.SetResult(call, caller.Id, arrays);
                    return true;
                }
                if (typeName == "System.Array" && method.Name == "SetValue" && method.Parameters.Count == 2
                    && method.Parameters[1].TypeId == "System.Int32")
                {
                    writes.Add(new(BehaviorWriteKind.ArrayElement, call.ReceiverValueId, null,
                        new[] { call.Arguments[1].ValueId }, call.Arguments[0].ValueId, call.Point));
                    return true;
                }
                if (typeName == "System.Array" && method.Name == "GetValue" && method.Parameters.Count == 1
                    && method.Parameters[0].TypeId == "System.Int32")
                {
                    if (!sources.NeedsCall(caller.Id, call.Point))
                    {
                        return true;
                    }
                    ValueOrigin read = Result(BehaviorValueKind.ArrayElementRead, null);
                    read = read with { Value = read.Value with { InputValueIds = new[] { call.ReceiverValueId!.Value, call.Arguments[0].ValueId } } };
                    sources.SetResult(call, caller.Id, new[] { read });
                    return true;
                }
                if (typeName == "System.Delegate" && method.Name == "Combine" && method.Parameters.Count == 2)
                {
                    if (!sources.NeedsCall(caller.Id, call.Point))
                    {
                        return true;
                    }
                    ValueOrigin combined = Result(BehaviorValueKind.Merge, "合并委托候选");
                    sources.SetResult(call, caller.Id, new[] { combined with
                    {
                        Value = combined.Value with { InputValueIds = call.Arguments.Select(argument => argument.ValueId).ToArray() },
                    } });
                    return true;
                }
                if (typeName == "System.Object" && method.Name == "GetType" && arguments.Count == 0)
                {
                    sources.SetResult(call, caller.Id, new[] { Result(BehaviorValueKind.Type, null) with { BoundReceiver = receiver } });
                    return true;
                }
                if (typeName == "System.Type" && method.Name == "GetTypeFromHandle" && arguments.Count == 1)
                {
                    sources.SetResult(call, caller.Id, sources.GetOrigins(arguments[0][0]));
                    return true;
                }
                if (typeName == "System.Type" && method.Name == "MakeGenericType")
                {
                    var parameters = sources.ReadArrayArguments(arguments[0][0], sources.ReadArrayLength(arguments[0][0]));
                    List<TypeIdentityTemplate[]> choices = new() { Array.Empty<TypeIdentityTemplate>() };
                    foreach (var parameter in parameters)
                    {
                        TypeIdentityTemplate[] types = parameter.SelectMany(value => sources.GetOrigins(value))
                            .Select(origin => origin.Value.Kind == BehaviorValueKind.Type && origin.Value.Type != null
                                ? catalog.ReadResolvedTypeIdentity(origin.Value.Type)
                                : throw new AnalysisException("动态泛型的类型参数尚未确定")).Distinct().ToArray();
                        choices = choices.SelectMany(previous => types.Select(type => previous.Append(type).ToArray())).ToList();
                    }
                    List<ValueOrigin> constructed = new();
                    foreach (ValueOrigin origin in sources.GetOrigins(receiver.Single()))
                    {
                        if (origin.Value.Kind != BehaviorValueKind.Type || origin.Value.Type == null)
                        {
                            throw new AnalysisException($"动态泛型的类型定义尚未确定：{origin.Value.Kind}，{origin.Value.Reference}");
                        }
                        TypeEntry definition = catalog.ResolveTypeDefinition(origin.Value.Type);
                        if (definition.GenericParameters.Count != parameters.Count || definition.GenericParameters.Count == 0)
                        {
                            throw new AnalysisException("动态泛型的类型参数数量不匹配");
                        }
                        if (definition.GenericParameters.Any(rule => rule.RequiresReferenceType || rule.RequiresValueType
                            || rule.RequiresDefaultConstructor || rule.TypeConstraints.Count != 0))
                        {
                            throw new AnalysisException("动态泛型的类型约束尚未验证");
                        }
                        constructed.AddRange(choices.Select(types => Result(BehaviorValueKind.Type, null, TypeReference(definition, types))));
                    }
                    sources.SetResult(call, caller.Id, constructed);
                    return true;
                }
                if (typeName == "System.Type" && method.Name == "GetType" && method.IsStatic
                    && method.Parameters.Count != 0 && method.Parameters[0].TypeId == "System.String")
                {
                    if (arguments.Count > 2)
                    {
                        throw new AnalysisException("类型名查询的大小写选项或自定义解析函数尚未连接");
                    }
                    List<ValueOrigin> types = new();
                    foreach (ValueOrigin name in sources.GetOrigins(arguments[0][0]))
                    {
                        if (name.Value.Kind != BehaviorValueKind.Constant || name.Value.Reference == null)
                        {
                            failure = "反射类型名称尚未确定";
                            continue;
                        }
                        TypeEntry? selected = catalog.ReadNamedRuntimeType(name.Value.Reference, caller);
                        types.Add(selected == null ? Result(BehaviorValueKind.Constant, "null")
                            : Result(BehaviorValueKind.Type, null, TypeReference(selected, Array.Empty<TypeIdentityTemplate>())));
                    }
                    sources.SetResult(call, caller.Id, types);
                    return true;
                }
                if (typeName is "System.Type" or "System.Reflection.MemberInfo" or "System.Reflection.MethodInfo" or "System.Reflection.FieldInfo" or "System.Reflection.PropertyInfo"
                    && method.Name is "op_Equality" or "op_Inequality")
                {
                    bool readResult = sources.NeedsCall(caller.Id, call.Point);
                    if (catalog.HasOnlyValueInstructions(method))
                    {
                        return false;
                    }
                    ValueOrigin[][] operands = arguments.Select(values => values.SelectMany(value => sources.GetOrigins(value,
                        resolveRuntimeTypes: readResult)).ToArray()).ToArray();
                    if (!readResult)
                    {
                        if (operands.Any(values => values.Length == 0 || values.Any(origin => origin.Value.Kind != BehaviorValueKind.Type && MetadataIdentity(origin) == null)))
                        {
                            failure = "元数据比较的操作数尚未确定";
                        }
                        return true;
                    }
                    List<ValueOrigin> comparisons = new();
                    foreach (ValueOrigin left in operands[0])
                    {
                        foreach (ValueOrigin right in operands[1])
                        {
                            string? leftIdentity = MetadataIdentity(left);
                            string? rightIdentity = MetadataIdentity(right);
                            if (leftIdentity == null || rightIdentity == null)
                            {
                                failure = "元数据比较的操作数尚未确定";
                                continue;
                            }
                            bool equal = leftIdentity == rightIdentity;
                            comparisons.Add(Result(BehaviorValueKind.Constant, (method.Name == "op_Equality" ? equal : !equal).ToString()));
                        }
                    }
                    sources.SetResult(call, caller.Id, comparisons);
                    return true;
                }
                if (typeName == "System.Type" && method.Name == "get_BaseType")
                {
                    List<ValueOrigin> parents = new();
                    foreach (ValueOrigin origin in sources.GetOrigins(receiver.Single()))
                    {
                        if (IsNull(origin))
                        {
                            continue;
                        }
                        if (origin.Value.Kind != BehaviorValueKind.Type || origin.Value.Type == null)
                        {
                            failure = "父类查询的类型尚未确定";
                            continue;
                        }
                        MethodCatalogResult.InheritedTypeRelation? parent = catalog.ReadInheritedTypes(catalog.ResolveTypeDefinition(origin.Value.Type),
                            catalog.ReadResolvedTypeArguments(origin.Value.Type), includeInterfaces: false).SingleOrDefault(relation => relation.Depth == 1);
                        parents.Add(parent == null ? Result(BehaviorValueKind.Constant, "null")
                            : Result(BehaviorValueKind.Type, null, TypeReference(parent.Definition, parent.TypeArguments)));
                    }
                    sources.SetResult(call, caller.Id, parents);
                    return true;
                }
                if (typeName == "System.Activator" && method.Name == "CreateInstance"
                    && (method.GenericArity == 1 && arguments.Count == 0 || method.GenericArity == 0
                        && arguments.Count >= 1 && method.Parameters[0].TypeId.EndsWith("System.Type", StringComparison.Ordinal)))
                {
                    int arrayIndex = method.Parameters.ToList().FindIndex(parameter => parameter.TypeId == "System.Object[]");
                    int count = arrayIndex < 0 ? 0 : sources.ReadArrayLength(arguments[arrayIndex][0]);
                    IReadOnlyList<IReadOnlyList<BehaviorValueReference>> supplied = arrayIndex < 0 ? Array.Empty<IReadOnlyList<BehaviorValueReference>>()
                        : sources.ReadArrayArguments(arguments[arrayIndex][0], count);
                    List<ValueOrigin> created = new();
                    IEnumerable<ValueOrigin> requested = method.GenericArity == 0 ? sources.GetOrigins(arguments[0][0]) : GenericCreationType();
                    foreach (ValueOrigin origin in requested)
                    {
                        if (origin.Value.Kind != BehaviorValueKind.Type || origin.Value.Type == null)
                        {
                            throw new AnalysisException("反射创建的类型尚未确定");
                        }
                        if (origin.Value.Type.Id.EndsWith(']') || origin.Value.Type.Id.EndsWith('*') || origin.Value.Type.Id.StartsWith('!'))
                        {
                            failure = "反射创建需要确定的非数组对象类型";
                            continue;
                        }
                        TypeEntry definition = catalog.ResolveTypeDefinition(origin.Value.Type);
                        MethodEntry[] constructors = catalog.GetMethods(definition).Where(candidate => candidate.Kind == MethodKind.Constructor
                            && candidate.IsPublic && candidate.Parameters.Count == count).ToArray();
                        if (constructors.Length != 1)
                        {
                            throw new AnalysisException($"反射构造函数尚未唯一匹配：{definition.FullName}，参数 {count} 个");
                        }
                        created.Add(new ValueOrigin(Reference(call.ResultValueId!.Value), new BehaviorValue(call.ResultValueId.Value,
                            BehaviorValueKind.NewObject, null, null, Array.Empty<int>())
                        { Type = origin.Value.Type }));
                        if (await AddTarget(Bind(new ResolvedMethodDefinition(constructors[0], catalog.ReadResolvedTypeArguments(origin.Value.Type)),
                            new[] { Reference(call.ResultValueId!.Value) }, supplied)).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    if (failure != null || created.Count == 0)
                    {
                        failure ??= "反射创建的对象类型尚未确定";
                        created.Add(Result(BehaviorValueKind.CallResult, failure));
                    }
                    sources.SetResult(call, caller.Id, created);
                    return true;
                }
                if (typeName == "System.Type" && method.Name is "GetMethod" or "GetMethods" or "GetField" or "GetFields" or "GetProperty" or "GetProperties"
                    or "GetMember" or "GetMembers" or "GetConstructor" or "GetConstructors")
                {
                    int typesIndex = method.Parameters.ToList().FindIndex(parameter => parameter.TypeId.EndsWith("System.Type[]", StringComparison.Ordinal));
                    string[]? requestedTypes = null;
                    if (typesIndex >= 0)
                    {
                        requestedTypes = sources.ReadArrayArguments(arguments[typesIndex][0], sources.ReadArrayLength(arguments[typesIndex][0]))
                            .Select(values =>
                            {
                                string[] choices = values.SelectMany(value => sources.GetOrigins(value)).Select(origin => origin.Value.Kind == BehaviorValueKind.Type && origin.Value.Type != null
                                    ? catalog.ReadResolvedTypeIdentity(origin.Value.Type).Text
                                    : throw new AnalysisException("反射参数类型尚未确定")).Distinct().ToArray();
                                return choices.Length == 1 ? choices[0] : throw new AnalysisException("反射参数类型存在多个选择，尚未保留其对应关系");
                            }).ToArray();
                    }
                    string[]? names = null;
                    if (method.Parameters.Count != 0 && method.Parameters[0].TypeId == "System.String")
                    {
                        ValueOrigin[] inputs = sources.GetOrigins(arguments[0][0]).ToArray();
                        if (inputs.Length == 0 || inputs.Any(origin => origin.Value.Kind != BehaviorValueKind.Constant))
                        {
                            failure = "反射成员名称尚未确定";
                        }
                        names = inputs.Where(origin => origin.Value.Kind == BehaviorValueKind.Constant)
                            .Select(origin => origin.Value.Reference ?? string.Empty).Distinct().ToArray();
                        if (names.Length == 0)
                        {
                            return true;
                        }
                    }
                    int flags = 4 | 8 | 16;
                    int flagIndex = method.Parameters.ToList().FindIndex(parameter => parameter.TypeId.EndsWith("System.Reflection.BindingFlags", StringComparison.Ordinal));
                    if (flagIndex >= 0)
                    {
                        ValueOrigin[] constants = sources.GetOrigins(arguments[flagIndex][0]).ToArray();
                        if (constants.Length != 1 || !int.TryParse(constants[0].Value.Reference, out flags))
                        {
                            throw new AnalysisException("反射成员查找选项尚未确定");
                        }
                    }
                    List<ValueOrigin> found = new();
                    System.Reflection.MemberTypes memberKind = method.Name switch
                    {
                        "GetField" or "GetFields" => System.Reflection.MemberTypes.Field,
                        "GetProperty" or "GetProperties" => System.Reflection.MemberTypes.Property,
                        "GetMethod" or "GetMethods" => System.Reflection.MemberTypes.Method,
                        "GetConstructor" or "GetConstructors" => System.Reflection.MemberTypes.Constructor,
                        _ => System.Reflection.MemberTypes.All,
                    };
                    string selection = method.Name + ":" + flags + ":" + (requestedTypes == null ? "*" : string.Join('\0', requestedTypes));
                    StringComparer nameComparer = (flags & 1) == 0 ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                    HashSet<string>? requestedNames = names?.ToHashSet(nameComparer);
                    HashSet<string> queriedTypes = new(StringComparer.Ordinal);
                    foreach (ValueOrigin origin in sources.GetOrigins(receiver.Single()))
                    {
                        if (IsNull(origin))
                        {
                            continue;
                        }
                        if (origin.Value.Kind != BehaviorValueKind.Type || origin.Value.Type == null)
                        {
                            failure = "反射所属类型尚未确定";
                            continue;
                        }
                        TypeEntry definition = catalog.ResolveTypeDefinition(origin.Value.Type);
                        var typeArguments = catalog.ReadResolvedTypeArguments(origin.Value.Type);
                        string typeKey = selection + ":" + definition.Id + ":" + string.Join(',', typeArguments.Select(argument => argument.Text));
                        if (!queriedTypes.Add(typeKey))
                        {
                            continue;
                        }
                        IEnumerable<MethodCatalogResult.InheritedTypeRelation> hierarchy = new[]
                        {
                            new MethodCatalogResult.InheritedTypeRelation(definition, typeArguments, definition.IsInterface, true, 0),
                        };
                        if ((flags & 2) == 0 && method.Name is not ("GetConstructor" or "GetConstructors"))
                        {
                            hierarchy = hierarchy.Concat(catalog.ReadInheritedTypes(definition, typeArguments, includeInterfaces: false));
                        }
                        MethodCatalogResult.InheritedTypeRelation[] types = hierarchy.ToArray();
                        // 先按该类型真正存在的名字求交，不逐个展开上万个不可能命中的名字。
                        string?[] candidateNames = requestedNames == null ? new string?[] { null }
                            : types.SelectMany(relation => catalog.ReadReflectionMembers(relation.Definition, memberKind))
                                .Select(member => member.Name).Where(requestedNames.Contains).Distinct(nameComparer).ToArray();
                        if (requestedNames != null && candidateNames.Length != requestedNames.Count)
                        {
                            if (requestedTypes != null)
                            {
                                failure ??= "反射查询没有精确匹配的参数签名，需继续解析绑定转换";
                            }
                            else
                            {
                                found.Add(Result(BehaviorValueKind.Constant, "null"));
                            }
                        }
                        foreach (string? name in candidateNames)
                        {
                            string[]? selectedNames = name == null ? null : new[] { name };
                            string queryKey = typeKey + ":" + (name == null ? "*" : name.Length + ":" + name);
                            if (sources.ReflectionQueries.TryGetValue((Reference(call.ResultValueId!.Value), queryKey), out var cachedQuery))
                            {
                                found.AddRange(cachedQuery.Origins);
                                failure ??= cachedQuery.Failure;
                                continue;
                            }
                            int before = found.Count;
                            string? queryFailure = null;
                            HashSet<string> methodSignatures = new(StringComparer.Ordinal);
                            foreach (var relation in types)
                            {
                                TypeEntry declaring = relation.Definition;
                                foreach (MethodCatalogResult.ReflectionMember member in catalog.ReadReflectionMembers(declaring, memberKind, selectedNames, (flags & 1) != 0))
                                {
                                    if (declaring.Id != definition.Id && (member.IsPrivate || member.IsStatic && (flags & 64) == 0)
                                        || name != null && !string.Equals(name, member.Name, (flags & 1) == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                                        || (flags & (member.IsStatic ? 8 : 4)) == 0 || (flags & (member.IsPublic ? 16 : 32)) == 0
                                        || method.Name is "GetMethod" or "GetMethods" && (member.Method == null || member.Name is ".ctor" or ".cctor")
                                        || method.Name is "GetConstructor" or "GetConstructors" && (member.Method == null || member.Name != ".ctor")
                                        || method.Name is "GetField" or "GetFields" && member.Field == null
                                        || method.Name is "GetProperty" or "GetProperties" && member.Property == null)
                                    {
                                        continue;
                                    }
                                    if (member.Method != null)
                                    {
                                        ResolvedMethodDefinition target = catalog.ResolveMethodDefinition(member.Method, true);
                                        MethodIdentityTemplate signature = catalog.ReadMethodSignature(target.Method)
                                            .Instantiate(new(declaring.Id), relation.TypeArguments);
                                        string signatureKey = target.Method.Name + ":" + target.Method.IsStatic + ":" + target.Method.GenericArity
                                            + ":" + string.Join(",", signature.Parameters.Select(parameter => parameter.Text));
                                        if (!methodSignatures.Add(signatureKey)
                                            || requestedTypes != null && !signature.Parameters.Select(parameter => parameter.Text).SequenceEqual(requestedTypes))
                                        {
                                            continue;
                                        }
                                    }
                                    // 方法、属性和字段使用同一层声明的实际类型参数，继承时不借用子类参数。
                                    BehaviorMethodReference? BindMember(BehaviorMethodReference? reference) => reference == null ? null
                                        : catalog.ReadMethodReference(catalog.ResolveMethodDefinition(reference, true).Method, relation.TypeArguments);
                                    found.Add(new ValueOrigin(Reference(call.ResultValueId!.Value), new BehaviorValue(call.ResultValueId.Value,
                                        member.Method != null ? BehaviorValueKind.Function : BehaviorValueKind.Constant, member.Name, null, Array.Empty<int>())
                                    {
                                        Method = BindMember(member.Method),
                                        Member = member.Field == null ? null : member.Field with
                                        {
                                            DeclaringTypeIdentity = ConstructTypeIdentity(declaring, relation.TypeArguments),
                                        },
                                        Property = member.Property == null ? null : new BehaviorPropertyReference(
                                            BindMember(member.Property.Getter), BindMember(member.Property.Setter)),
                                        Type = origin.Value.Type,
                                    }));
                                }
                            }
                            if (found.Count == before)
                            {
                                if (requestedTypes != null)
                                {
                                    queryFailure = "反射查询没有精确匹配的参数签名，需继续解析绑定转换";
                                    failure ??= queryFailure;
                                }
                                else
                                {
                                    found.Add(Result(BehaviorValueKind.Constant, "null"));
                                }
                            }
                            sources.ReflectionQueries[(Reference(call.ResultValueId!.Value), queryKey)] =
                                (found.Skip(before).ToArray(), queryFailure);
                        }
                    }
                    sources.SetResult(call, caller.Id, found);
                    return true;
                }
                if (typeName is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo"
                    && method.Name == "Invoke")
                {
                    IReadOnlyList<ValueOrigin> invoked = sources.GetOrigins(receiver.Single());
                    if (invoked.Count == 0)
                    {
                        failure = "反射调用目标尚未确定";
                    }
                    foreach (ValueOrigin origin in invoked)
                    {
                        if (IsNull(origin))
                        {
                            continue;
                        }
                        if (origin.Value.Method == null)
                        {
                            failure = $"反射调用目标尚未确定：{origin.Value.Kind} {origin.Value.Reference}";
                            continue;
                        }
                        ResolvedMethodDefinition target = catalog.ResolveMethodDefinition(origin.Value.Method, true);
                        IReadOnlyList<IReadOnlyList<BehaviorValueReference>> supplied = sources.ReadArrayArguments(arguments.Last()[0], target.Method.Parameters.Count);
                        if (typeName == "System.Reflection.ConstructorInfo" && target.Method.Kind == MethodKind.Constructor)
                        {
                            sources.SetResult(call, caller.Id, new[] { Result(BehaviorValueKind.NewObject, null, origin.Value.Type) });
                            await ConnectRuntimeTarget(target, new[] { Reference(call.ResultValueId!.Value) }, supplied).ConfigureAwait(false);
                        }
                        else
                        {
                            await ConnectRuntimeTarget(target, arguments[0], supplied).ConfigureAwait(false);
                        }
                        if (stopped)
                        {
                            break;
                        }
                    }
                    return true;
                }
                if (typeName == "System.Reflection.FieldInfo" && method.Name is "SetValue" or "GetValue")
                {
                    List<ValueOrigin> read = new();
                    IReadOnlyList<ValueOrigin> fields = sources.GetOrigins(receiver.Single());
                    if (fields.Count == 0)
                    {
                        failure = "反射字段尚未确定";
                    }
                    foreach (ValueOrigin origin in fields)
                    {
                        if (IsNull(origin))
                        {
                            continue;
                        }
                        if (origin.Value.Member == null)
                        {
                            throw new AnalysisException("反射字段尚未确定");
                        }
                        int? destination = call.Arguments[0].ValueId;
                        if (origin.Value.Member.IsStatic == null)
                        {
                            throw new AnalysisException("反射字段的静态声明尚未读取");
                        }
                        if (origin.Value.Member.IsStatic == true)
                        {
                            destination = null;
                        }
                        if (method.Name == "SetValue")
                        {
                            writes.Add(new BehaviorWrite(BehaviorWriteKind.Field, destination, origin.Value.Member, Array.Empty<int>(), call.Arguments[1].ValueId, call.Point));
                        }
                        else
                        {
                            read.Add(new ValueOrigin(Reference(call.ResultValueId!.Value), new BehaviorValue(call.ResultValueId.Value,
                                BehaviorValueKind.FieldRead, origin.Value.Member.Name, null, destination == null ? Array.Empty<int>() : new[] { destination.Value })
                            { Member = origin.Value.Member }));
                        }
                    }
                    if (method.Name == "GetValue")
                    {
                        sources.SetResult(call, caller.Id, read);
                    }
                    return true;
                }
                if (typeName == "System.Reflection.PropertyInfo" && method.Name is "GetValue" or "SetValue")
                {
                    IReadOnlyList<ValueOrigin> properties = sources.GetOrigins(receiver.Single());
                    if (properties.Count == 0)
                    {
                        failure = "反射属性尚未确定";
                    }
                    foreach (ValueOrigin origin in properties)
                    {
                        if (IsNull(origin))
                        {
                            continue;
                        }
                        BehaviorMethodReference? accessor = method.Name == "GetValue" ? origin.Value.Property?.Getter : origin.Value.Property?.Setter;
                        if (accessor == null)
                        {
                            throw new AnalysisException("反射属性访问函数尚未确定");
                        }
                        ResolvedMethodDefinition target = catalog.ResolveMethodDefinition(accessor, true);
                        int indexCount = target.Method.Parameters.Count - (method.Name == "SetValue" ? 1 : 0);
                        IReadOnlyList<IReadOnlyList<BehaviorValueReference>> supplied = indexCount == 0 ? Array.Empty<IReadOnlyList<BehaviorValueReference>>()
                            : sources.ReadArrayArguments(arguments.Last()[0], indexCount);
                        if (method.Name == "SetValue")
                        {
                            supplied = supplied.Append(arguments[1]).ToArray();
                        }
                        await ConnectRuntimeTarget(target, arguments[0], supplied).ConfigureAwait(false);
                        if (stopped)
                        {
                            break;
                        }
                    }
                    return true;
                }
                return false;
            }

            // 泛型创建和传 Type 的创建共用构造函数分析，不展开运行库内部的泛型创建机制。
            IEnumerable<ValueOrigin> GenericCreationType()
            {
                TypeIdentityTemplate identity = call.Target.ReferenceMetadataToken == 0
                    ? new(call.Target.GenericArgumentTypeIds.Single())
                    : catalog.ReadResolvedTypeArguments(call.Target.ReferringAssemblyPath!, call.Target.ReferenceMetadataToken, methodArguments: true).Single();
                foreach (TypeIdentityTemplate concrete in sources.ReadGenericTypes(caller.Id, identity))
                {
                    TryReadConstructedType(concrete.Text, out string definition, out string[] parameters);
                    BehaviorTypeReference type = new(concrete, new(definition), parameters.Select(parameter => new TypeIdentityTemplate(parameter)).ToArray(),
                        null, caller.AssemblyPath, catalog.TypesById.ContainsKey(definition) ? definition : null);
                    yield return Result(BehaviorValueKind.Type, null, type);
                }
            }

            // 反射函数和属性访问使用同一个接收对象规则及重写列表。
            async ValueTask ConnectRuntimeTarget(ResolvedMethodDefinition declared, IReadOnlyList<BehaviorValueReference> actualReceiver,
                IReadOnlyList<IReadOnlyList<BehaviorValueReference>> supplied)
            {
                IEnumerable<ResolvedMethodDefinition> implementations = declared.Method.IsVirtual || declared.Method.IsAbstract
                    ? VirtualTargets(declared, actualReceiver) : new[] { declared };
                bool found = false;
                foreach (ResolvedMethodDefinition target in implementations)
                {
                    found = true;
                    if (await AddTarget(Bind(target, target.Method.IsStatic ? Array.Empty<BehaviorValueReference>() : actualReceiver, supplied)).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                if (!found)
                {
                    failure = $"反射调用没有找到合法实现：{declared.Method.Id}";
                }
            }

            // 逐个提交合法候选；已确定真实与追踪修改时不再枚举其余目标。
            async ValueTask<bool> AddTarget(ResolvedCallTarget target)
            {
                targets.Add(target);
                stopped = stopAfterTarget != null && await stopAfterTarget(target).ConfigureAwait(false);
                return stopped;
            }

            // 普通调用和反射调用共用虚函数候选表，不能把反射得到的父类声明当成最终实现。
            IEnumerable<ResolvedMethodDefinition> VirtualTargets(ResolvedMethodDefinition declared, IReadOnlyList<BehaviorValueReference> actualReceiver)
            {
                // 仅为登记实参查合法重写，不为注册位置展开工厂对象；普通行为分析仍检查实际接收对象。
                if (registrationBinding)
                {
                    return ReadCandidates(Array.Empty<ValueOrigin>())
                        .Where(target => registrationTargets == null || registrationTargets.Contains(target.Method.Id));
                }
                ValueOrigin[] origins = actualReceiver.SelectMany(value => sources.ReadFixedReceiver(value) is ValueOrigin fixedReceiver
                    ? new[] { fixedReceiver } : sources.ReadLocalOrigins(value)).ToArray();
                if (origins.Any(origin => origin.Value.Kind == BehaviorValueKind.CallResult && origin.Value.Type == null))
                {
                    failure = "调用接收对象的返回来源尚未确定";
                    origins = origins.Where(origin => origin.Value.Kind != BehaviorValueKind.CallResult || origin.Value.Type != null).ToArray();
                }
                if (origins.Length == 0)
                {
                    return Array.Empty<ResolvedMethodDefinition>();
                }
                if (origins.Any(origin => origin.Value.Type == null || origin.Value.Type.Id.StartsWith('!')))
                {
                    return ReadCandidates(origins);
                }
                return origins.DistinctBy(origin => (origin.Value.Kind == BehaviorValueKind.NewObject, origin.Value.Type!.Id))
                    .SelectMany(origin => ReadCandidates(new[] { origin }));

                // 每个接收类型独立复用实现列表，新增类型不使旧类型重新匹配。
                IEnumerable<ResolvedMethodDefinition> ReadCandidates(ValueOrigin[] receivers)
                {
                    string receiverRange = receivers.Length == 0 || receivers.Any(origin => origin.Value.Type == null || origin.Value.Type.Id.StartsWith('!'))
                        ? "*" : string.Join(',', receivers.Select(origin => (origin.Value.Kind == BehaviorValueKind.NewObject ? "exact:" : "derived:")
                            + origin.Value.Type!.Id).Distinct().Order());
                    string key = declared.Method.Id + "|" + string.Join(',', declared.DeclaringTypeArguments.Select(argument => argument.Text)) + "|"
                        + string.Join(',', declared.MethodTypeArguments.Select(argument => argument.Text)) + "|"
                        + receiverRange;
                    if (!dispatch.Ranges.TryGetValue(key, out TargetCandidates? implementations))
                    {
                        implementations = new TargetCandidates(ReadImplementations(catalog, declared, receivers, sources.Timing, dispatch));
                        dispatch.Ranges.Add(key, implementations);
                    }
                    return sources.Timing.MeasureEnumeration(implementations.Read(), AnalysisTiming.Part.Dispatch);
                }
            }

            // 运行库查询的结果仍存入原调用位置，不创建额外函数或调用环境。
            ValueOrigin Result(BehaviorValueKind kind, string? reference, BehaviorTypeReference? type = null)
            {
                return new ValueOrigin(Reference(call.ResultValueId!.Value), new BehaviorValue(call.ResultValueId.Value, kind, reference, null, Array.Empty<int>()) { Type = type });
            }

            // 只比较已确定的运行时类型或成员身份，不能把用户实现的元数据子类当作纯比较。
            string? MetadataIdentity(ValueOrigin origin)
            {
                if (IsNull(origin))
                {
                    return "null";
                }
                if (origin.Value.Kind == BehaviorValueKind.Type && origin.Value.Type != null)
                {
                    return "type:" + catalog.ReadResolvedTypeIdentity(origin.Value.Type).Text;
                }
                if (origin.Value.Method != null)
                {
                    return "method:" + catalog.ResolveMethodDefinition(origin.Value.Method, true).Method.Id;
                }
                if (origin.Value.Member != null)
                {
                    return "field:" + origin.Value.Member.DeclaringTypeIdentity.Text + ":" + origin.Value.Member.Name;
                }
                BehaviorMethodReference? accessor = origin.Value.Property?.Getter ?? origin.Value.Property?.Setter;
                return accessor == null ? null : "property:" + catalog.ResolveMethodDefinition(accessor, true).Method.Id;
            }

            // 只记录实参编号，不创建被调函数的执行环境。
            ResolvedCallTarget Bind(ResolvedMethodDefinition target, IReadOnlyList<BehaviorValueReference> actualReceiver,
                IReadOnlyList<IReadOnlyList<BehaviorValueReference>> actualArguments)
            {
                using var bindingTiming = sources.Timing.Measure(AnalysisTiming.Part.Bindings);
                if (delegateInvocation)
                {
                    if (target.Method.IsStatic)
                    {
                        if (target.Method.Parameters.Count == actualArguments.Count + 1 && actualReceiver.Count != 0)
                        {
                            actualArguments = actualArguments.Prepend(actualReceiver).ToArray();
                        }
                        actualReceiver = Array.Empty<BehaviorValueReference>();
                    }
                    else if (target.Method.Parameters.Count + 1 == actualArguments.Count)
                    {
                        actualReceiver = actualArguments[0];
                        actualArguments = actualArguments.Skip(1).ToArray();
                    }
                }
                if (actualArguments.Count != target.Method.Parameters.Count)
                {
                    throw new AnalysisException($"调用参数与目标声明不一致：{caller.Id} => {target.Method.Id}；"
                        + $"实参 {actualArguments.Count}，形参 {target.Method.Parameters.Count}");
                }
                sources.Include(target.Method);
                ResolvedCallTarget bound = new(target.Method.Id, catalog.ReadMethodReference(target.Method, target.DeclaringTypeArguments, target.MethodTypeArguments), actualReceiver,
                    actualArguments, target.DeclaringTypeArguments.Select(type => type.Text).ToArray())
                { MethodTypeArguments = target.MethodTypeArguments.Select(type => type.Text).ToArray() };
                if (catalog.IsLibraryMethod(target.Method))
                {
                    library.Add(bound);
                }
                return bound;
            }

            // V3 设计 3.2 节：外部库函数只能经传入的委托，或经接收对象与实参中源码类型对外部库虚成员的重写回调业务代码。
            async ValueTask<bool> ReadLibraryCallbacks(ResolvedCallTarget libraryTarget)
            {
                using var callbackTiming = sources.Timing.Measure(AnalysisTiming.Part.Dispatch);
                BehaviorValueReference[] contents = libraryTarget.Receiver.Concat(libraryTarget.Arguments.SelectMany(values => values)).Distinct().ToArray();
                MethodEntry libraryMethod = sources.ReadMethod(libraryTarget.MethodId);
                TypeEntry libraryOwner = catalog.TypesById[libraryMethod.TypeId];
                HashSet<string> receiverVisible = new(s_frameworkProtocols, StringComparer.Ordinal) { RuntimeOperations.ReadMetadataName(libraryOwner.LogicalId) };
                receiverVisible.UnionWith(catalog.ReadInheritedTypes(libraryOwner).Select(relation => RuntimeOperations.ReadMetadataName(relation.Definition.LogicalId)));
                // 库函数只能经它看得到的静态类型调用虚成员：接收对象按声明类型及其祖先，实参按形参声明类型，另加框架通用协议。
                IEnumerable<(BehaviorValueReference Value, IReadOnlySet<string> Visible)> visibleValues = libraryTarget.Receiver
                    .Select(value => (value, (IReadOnlySet<string>)receiverVisible))
                    .Concat(libraryTarget.Arguments.SelectMany((values, index) => values.Select(value => (value, (IReadOnlySet<string>)new HashSet<string>(s_frameworkProtocols, StringComparer.Ordinal)
                    {
                        RuntimeOperations.ReadMetadataName(index < libraryMethod.Parameters.Count ? libraryMethod.Parameters[index].TypeId : "System.Object"),
                    }))));
                foreach (var (value, visible) in visibleValues)
                {
                    ValueOrigin[] local = (sources.ReadFixedReceiver(value) is ValueOrigin fixedValue ? new[] { fixedValue }
                        : sources.ReadLocalOrigins(value)).ToArray();
                    if (local.Any(origin => origin.Value.Kind == BehaviorValueKind.Function || IsDelegateValue(origin)))
                    {
                        foreach (ValueOrigin origin in sources.GetOrigins(value))
                        {
                            if (IsNull(origin))
                            {
                                continue;
                            }
                            if (origin.Value.Kind != BehaviorValueKind.Function || origin.Value.Method == null)
                            {
                                if (origin.Value.Kind == BehaviorValueKind.Parameter && origin.Reference.MethodId == caller.Id)
                                {
                                    invokesUnboundParameter = true;
                                }
                                else
                                {
                                    failure = $"库函数的委托实参来源尚未闭合：{caller.Id} @ {call.Point.BlockId}；{origin.Value.Kind}：{origin.Value.Reference}";
                                }
                                continue;
                            }
                            ResolvedMethodDefinition delegateTarget = catalog.ResolveMethodDefinition(origin.Value.Method, true);
                            IReadOnlyList<BehaviorValueReference> bound = origin.BoundReceiver
                                ?? origin.Value.InputValueIds.Select(input => origin.Reference with { ValueId = input }).ToArray();
                            foreach (ResolvedMethodDefinition implementation in origin.Value.UsesVirtualDispatch ? VirtualTargets(delegateTarget, bound) : new[] { delegateTarget })
                            {
                                bool isStatic = implementation.Method.IsStatic;
                                if (await AddTarget(Callback(implementation, isStatic ? Array.Empty<BehaviorValueReference>() : bound,
                                    isStatic && bound.Count != 0 ? bound : null, contents)).ConfigureAwait(false))
                                {
                                    return true;
                                }
                            }
                        }
                        continue;
                    }
                    foreach (ValueOrigin origin in local)
                    {
                        if (IsNull(origin) || origin.Value.Kind == BehaviorValueKind.Function)
                        {
                            continue;
                        }
                        if (origin.Value.Kind == BehaviorValueKind.Iterator && origin.Value.Method != null)
                        {
                            ResolvedMethodDefinition body = catalog.ResolveMethodDefinition(origin.Value.Method, false);
                            IReadOnlyList<BehaviorValueReference> capturedReceiver = origin.BoundReceiver
                                ?? (body.Method.IsStatic ? Array.Empty<BehaviorValueReference>() : new[] { origin.Reference with { ValueId = origin.Value.InputValueIds[0] } });
                            IReadOnlyList<IReadOnlyList<BehaviorValueReference>> capturedArguments = origin.IteratorArguments
                                ?? origin.Value.InputValueIds.Skip(body.Method.IsStatic ? 0 : 1)
                                    .Select(input => (IReadOnlyList<BehaviorValueReference>)new[] { origin.Reference with { ValueId = input } }).ToArray();
                            if (await AddTarget(Bind(body, capturedReceiver, capturedArguments) with { IsCallback = true }).ConfigureAwait(false))
                            {
                                return true;
                            }
                            continue;
                        }
                        foreach (ResolvedMethodDefinition implementation in ReadFrameworkCallbacks(catalog, origin, visible, dispatch))
                        {
                            if (await AddTarget(Callback(implementation, new[] { value }, null, contents)).ConfigureAwait(false))
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            // 值的已知类型为委托类型；开放泛型或尚未载入的类型不据此判断。
            bool IsDelegateValue(ValueOrigin origin)
            {
                if (origin.Value.Type is not BehaviorTypeReference type || type.Id.StartsWith('!') || type.Id.EndsWith(']'))
                {
                    return false;
                }
                try
                {
                    return catalog.IsDelegateType(catalog.ResolveTypeDefinition(type));
                }
                catch (AnalysisException)
                {
                    return false;
                }
            }

            // 回调以库函数接收对象和实参所含内容为实参；静态目标可带委托绑定的首个实参。
            ResolvedCallTarget Callback(ResolvedMethodDefinition target, IReadOnlyList<BehaviorValueReference> actualReceiver,
                IReadOnlyList<BehaviorValueReference>? firstArgument, IReadOnlyList<BehaviorValueReference> contents)
            {
                sources.Include(target.Method);
                IReadOnlyList<BehaviorValueReference>[] actualArguments = target.Method.Parameters
                    .Select((_, index) => index == 0 && firstArgument != null ? firstArgument : contents).ToArray();
                return new ResolvedCallTarget(target.Method.Id, catalog.ReadMethodReference(target.Method, target.DeclaringTypeArguments, target.MethodTypeArguments),
                    actualReceiver, actualArguments, target.DeclaringTypeArguments.Select(type => type.Text).ToArray())
                {
                    MethodTypeArguments = target.MethodTypeArguments.Select(type => type.Text).ToArray(),
                    IsCallback = true,
                };
            }
        }

        // 值的实际类型范围（新对象只取自身，其余含全部派生类型，以及它们的源码祖先）中，重写或实现外部库虚成员的源码函数。
        private static IReadOnlyList<ResolvedMethodDefinition> ReadFrameworkCallbacks(MethodCatalogResult catalog, ValueOrigin origin, IReadOnlySet<string> visible,
            DispatchIndex dispatch)
        {
            BehaviorTypeReference? type = origin.Value.Type;
            bool known = type != null && !type.Id.StartsWith('!') && !type.Id.EndsWith(']') && !type.Id.EndsWith('*') && !type.Id.EndsWith('&');
            TypeEntry definition = known ? catalog.ResolveTypeDefinition(type!) : catalog.ReadPrimitiveType("System.Object");
            IReadOnlyList<TypeIdentityTemplate> arguments = known ? catalog.ReadResolvedTypeArguments(type!) : Array.Empty<TypeIdentityTemplate>();
            bool exact = known && origin.Value.Kind == BehaviorValueKind.NewObject;
            string key = (exact ? "exact:" : "derived:") + definition.Id + "|" + string.Join(',', arguments.Select(argument => argument.Text))
                + "|" + string.Join(',', visible.Order(StringComparer.Ordinal));
            if (dispatch.Callbacks.TryGetValue(key, out IReadOnlyList<ResolvedMethodDefinition>? cached))
            {
                return cached;
            }
            List<(TypeEntry Type, IReadOnlyList<TypeIdentityTemplate> Arguments)> range = new() { (definition, arguments) };
            range.AddRange(catalog.ReadInheritedTypes(definition, arguments, includeInterfaces: false)
                .Select(relation => (relation.Definition, (IReadOnlyList<TypeIdentityTemplate>)relation.TypeArguments)));
            if (!exact)
            {
                HashSet<string> visited = new(StringComparer.Ordinal) { definition.Id };
                Queue<TypeEntry> pending = new(new[] { definition });
                while (pending.TryDequeue(out TypeEntry? current))
                {
                    foreach (TypeEntry child in catalog.DerivedTypesByBaseId.GetValueOrDefault(current.Id, Array.Empty<TypeEntry>())
                        .Concat(catalog.ImplementingTypesByInterfaceId.GetValueOrDefault(current.Id, Array.Empty<TypeEntry>())))
                    {
                        if (visited.Add(child.Id))
                        {
                            range.Add((child, Placeholders(child.GenericParameters.Count)));
                            pending.Enqueue(child);
                        }
                    }
                }
            }
            List<ResolvedMethodDefinition> callbacks = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (var (candidate, candidateArguments) in range.Where(item => item.Type.SourceSymbol != null && item.Type.IsCandidate))
            {
                foreach (var (symbol, contracts) in ReadFrameworkOverrides(catalog, candidate, dispatch))
                {
                    if (!contracts.Overlaps(visible))
                    {
                        continue;
                    }
                    MethodEntry method = catalog.ReadSourceDeclaration(symbol, candidate.AssemblyPath!);
                    if (seen.Add(method.Id + "|" + string.Join(',', candidateArguments.Select(argument => argument.Text))))
                    {
                        callbacks.Add(new ResolvedMethodDefinition(method, candidateArguments) { MethodTypeArguments = Placeholders(method.GenericArity) });
                    }
                }
            }
            dispatch.Callbacks.Add(key, callbacks);
            return callbacks;
        }

        // 源码类型中重写外部库虚函数或实现外部库接口成员的函数，每个类型只计算一次。
        private static IReadOnlyList<(IMethodSymbol Member, IReadOnlySet<string> Contracts)> ReadFrameworkOverrides(MethodCatalogResult catalog, TypeEntry type, DispatchIndex dispatch)
        {
            if (dispatch.FrameworkOverrides.TryGetValue(type.Id, out IReadOnlyList<(IMethodSymbol, IReadOnlySet<string>)>? known))
            {
                return known;
            }
            INamedTypeSymbol symbol = type.SourceSymbol!;
            Dictionary<IMethodSymbol, HashSet<string>> result = new(SymbolEqualityComparer.Default);
            void Add(IMethodSymbol member, string contract)
            {
                IMethodSymbol key = member.OriginalDefinition;
                if (!result.TryGetValue(key, out HashSet<string>? contracts))
                {
                    result.Add(key, contracts = new(StringComparer.Ordinal));
                }
                contracts.Add(contract);
            }
            foreach (IMethodSymbol member in symbol.GetMembers().OfType<IMethodSymbol>().Where(member => member.IsOverride))
            {
                for (IMethodSymbol? overridden = member.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                {
                    if (catalog.IsLibraryAssembly(overridden.ContainingAssembly.Name))
                    {
                        Add(member, MetadataName(overridden.ContainingType));
                    }
                }
            }
            foreach (INamedTypeSymbol contract in symbol.AllInterfaces.Where(contract => catalog.IsLibraryAssembly(contract.ContainingAssembly.Name)))
            {
                foreach (IMethodSymbol member in contract.GetMembers().OfType<IMethodSymbol>())
                {
                    if (symbol.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation
                        && implementation.Locations.Any(location => location.IsInSource))
                    {
                        Add(implementation, MetadataName(contract));
                    }
                }
            }
            (IMethodSymbol, IReadOnlySet<string>)[] ordered = result.OrderBy(pair => pair.Key.ToDisplayString(), StringComparer.Ordinal)
                .Select(pair => (pair.Key, (IReadOnlySet<string>)pair.Value)).ToArray();
            dispatch.FrameworkOverrides.Add(type.Id, ordered);
            return ordered;
        }

        // 外部库类型的元数据全名（命名空间加元数据名，嵌套类型用 + 连接），与类型身份中的名称一致。
        private static string MetadataName(INamedTypeSymbol type)
        {
            INamedTypeSymbol definition = type.OriginalDefinition;
            string name = definition.MetadataName;
            for (INamedTypeSymbol? outer = definition.ContainingType; outer != null; outer = outer.ContainingType)
            {
                name = outer.MetadataName + "+" + name;
            }
            return definition.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() + "." + name : name;
        }

        // 框架代码对任意对象都可能经由这些通用协议调用业务实现（比较、相等、格式化、枚举、释放、集合访问）。
        private static readonly HashSet<string> s_frameworkProtocols = new(StringComparer.Ordinal)
        {
            "System.Object", "System.IComparable", "System.IComparable`1", "System.IEquatable`1", "System.IFormattable", "System.IConvertible",
            "System.ICloneable", "System.IDisposable", "System.Collections.IEnumerable", "System.Collections.IEnumerator",
            "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IEnumerator`1", "System.Collections.ICollection",
            "System.Collections.IList", "System.Collections.IDictionary", "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IList`1", "System.Collections.Generic.IDictionary`2", "System.Collections.Generic.IReadOnlyCollection`1",
            "System.Collections.Generic.IReadOnlyList`1", "System.Collections.IComparer", "System.Collections.Generic.IComparer`1",
            "System.Collections.IEqualityComparer", "System.Collections.Generic.IEqualityComparer`1",
        };

        /// <summary>候选范围和单个类型的匹配结果分开复用，不保存调用路径。</summary>
        private sealed class DispatchIndex
        {
            internal Dictionary<string, IReadOnlyList<ResolvedMethodDefinition>> Callbacks { get; } = new(StringComparer.Ordinal);
            internal Dictionary<string, IReadOnlyList<(IMethodSymbol, IReadOnlySet<string>)>> FrameworkOverrides { get; } = new(StringComparer.Ordinal);
            internal Dictionary<string, TargetCandidates> Ranges { get; } = new(StringComparer.Ordinal);
            internal Dictionary<(string Method, TemplateList TypeArguments, TemplateList MethodArguments, string Starts), TargetCandidates> Traversals { get; } = new();
            internal Dictionary<(string Method, TemplateList TypeArguments, TemplateList MethodArguments, string Type, TemplateList Arguments), ResolvedMethodDefinition?> Implementations { get; } = new();
            internal Dictionary<(string Owner, TemplateList OwnerArguments, string Type, TemplateList Arguments), (MethodCatalogResult.InheritedTypeRelation[] Types, int IntroductionDepth)> Hierarchies { get; } = new();
        }

        /// <summary>按文本逐项比较的泛型实参列表，作字典键时不拼接字符串。</summary>
        private readonly record struct TemplateList(IReadOnlyList<TypeIdentityTemplate> Items)
        {
            // 个数相同且每项文本相同才视为同一组实参。
            public bool Equals(TemplateList other) => this.Items.SequenceEqual(other.Items);

            // 与逐项文本比较一致的哈希。
            public override int GetHashCode()
            {
                HashCode hash = new();
                for (int index = 0; index < this.Items.Count; index++)
                {
                    hash.Add(this.Items[index].Text, StringComparer.Ordinal);
                }
                return hash.ToHashCode();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TypeIdentityTemplate[]> s_placeholders = new();

        // 开放泛型类型的占位实参 !0、!1…… 按个数共用同一数组。
        private static TypeIdentityTemplate[] Placeholders(int count) => s_placeholders.GetOrAdd(count,
            static count => Enumerable.Range(0, count).Select(index => new TypeIdentityTemplate("!" + index)).ToArray());

        /// <summary>共用已找到的实现；提前停止后保留枚举位置，其他调用可以继续查。</summary>
        private sealed class TargetCandidates
        {
            private readonly List<ResolvedMethodDefinition> m_found = new();
            private IEnumerator<ResolvedMethodDefinition>? m_remaining;
            private AnalysisException? m_failure;

            // 保存声明的候选枚举，不提前读取全部实现。
            public TargetCandidates(IEnumerable<ResolvedMethodDefinition> candidates)
            {
                this.m_remaining = candidates.GetEnumerator();
            }

            // 先复用已有结果，只有调用者需要时才继续匹配下一个实现。
            public IEnumerable<ResolvedMethodDefinition> Read()
            {
                for (int index = 0; ; index++)
                {
                    if (index == this.m_found.Count)
                    {
                        if (this.m_failure != null)
                        {
                            throw this.m_failure;
                        }
                        if (this.m_remaining == null)
                        {
                            yield break;
                        }
                        IEnumerator<ResolvedMethodDefinition> remaining = this.m_remaining;
                        try
                        {
                            if (remaining.MoveNext())
                            {
                                this.m_found.Add(remaining.Current);
                            }
                            else
                            {
                                remaining.Dispose();
                                this.m_remaining = null;
                            }
                        }
                        catch (AnalysisException exception)
                        {
                            this.m_failure = exception;
                            remaining.Dispose();
                            this.m_remaining = null;
                            throw;
                        }
                        if (this.m_remaining == null)
                        {
                            yield break;
                        }
                    }
                    yield return this.m_found[index];
                }
            }
        }

        // 空目标没有可执行的函数体，不与暂未确定的目标混为一谈。
        private static bool IsNull(ValueOrigin origin)
        {
            return origin.Value.Kind == BehaviorValueKind.Constant && origin.Value.Reference is "null" or "default";
        }

        // 从已建立的继承列表找具体类型，一种声明类型只计算一次候选范围。
        internal static IReadOnlyList<BehaviorTypeReference> ReadRuntimeTypes(MethodCatalogResult catalog, TypeEntry declared,
            IReadOnlyList<TypeIdentityTemplate> declaredArguments)
        {
            Queue<TypeEntry> pending = new(new[] { declared });
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<BehaviorTypeReference> result = new();
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> derived = catalog.DerivedTypesByBaseId;
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> implementations = catalog.ImplementingTypesByInterfaceId;
            while (pending.TryDequeue(out TypeEntry? type))
            {
                if (!visited.Add(type.Id))
                {
                    continue;
                }
                foreach (TypeEntry child in (derived.GetValueOrDefault(type.Id) ?? Array.Empty<TypeEntry>())
                    .Concat(implementations.GetValueOrDefault(type.Id) ?? Array.Empty<TypeEntry>()))
                {
                    pending.Enqueue(child);
                }
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }
                IReadOnlyList<TypeIdentityTemplate> arguments = type.Id == declared.Id ? declaredArguments
                    : Placeholders(type.GenericParameters.Count);
                MethodCatalogResult.InheritedTypeRelation? relation = type.Id == declared.Id ? null
                    : catalog.ReadInheritedTypes(type, arguments).FirstOrDefault(parent => parent.Definition.Id == declared.Id);
                if (relation != null)
                {
                    Dictionary<int, TypeIdentityTemplate> substitutions = new();
                    if (relation.TypeArguments.Count != declaredArguments.Count || !relation.TypeArguments.Zip(declaredArguments)
                        .All(pair => MatchType(pair.First.Text, pair.Second.Text, substitutions)))
                    {
                        continue;
                    }
                    arguments = arguments.Select((argument, index) => substitutions.GetValueOrDefault(index) ?? argument).ToArray();
                }
                result.Add(TypeReference(type, arguments));
            }
            return result;
        }

        // 已确定的类型及泛型实参组成稳定引用，不依赖调用路径。
        private static BehaviorTypeReference TypeReference(TypeEntry type, IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            return new BehaviorTypeReference(new(type.LogicalId + (arguments.Count == 0 ? string.Empty
                : "<" + string.Join(",", arguments.Select(argument => argument.Text)) + ">")), new(type.LogicalId),
                arguments, null, type.AssemblyPath, type.Id);
        }

        // 同一函数绑定不同对象仍是不同合法候选，但不复制函数体或分析环境。
        internal static string TargetIdentity(ResolvedCallTarget target) => s_targetIdentities.GetValue(target, static target => target.MethodId + "|"
            + string.Join(',', target.DeclaringTypeArguments) + "|" + string.Join(',', target.MethodTypeArguments) + "|"
            + string.Join(',', target.Receiver.Select(value => value.MethodOrdinal + ":" + value.ValueId)) + "|"
            + string.Join(';', target.Arguments.Select(argument => string.Join(',', argument.Select(value => value.MethodOrdinal + ":" + value.ValueId))))
            + "|" + target.DelegateDeclarationId + (target.IsCallback ? "|callback" : string.Empty));

        internal sealed record TargetDescription(IReadOnlyList<TypeIdentityTemplate> TypeArguments, IReadOnlyList<TypeIdentityTemplate> MethodArguments);

        // 一个固定目标的泛型模板只整理一次，来源换算直接复用。
        internal static TargetDescription ReadTargetDescription(ResolvedCallTarget target)
        {
            return s_targetDescriptions.GetValue(target, static target => new(
                target.DeclaringTypeArguments.Select(argument => new TypeIdentityTemplate(argument)).ToArray(),
                target.MethodTypeArguments.Select(argument => new TypeIdentityTemplate(argument)).ToArray()));
        }

        // 接收类型明确时查该类型，否则使用声明对应的实现列表；同一签名只匹配一次。
        private static IEnumerable<ResolvedMethodDefinition> ReadImplementations(MethodCatalogResult catalog, ResolvedMethodDefinition declaration,
            IReadOnlyList<ValueOrigin> receivers, AnalysisTiming timing, DispatchIndex dispatch)
        {
            TypeEntry owner = catalog.TypesById[declaration.Method.TypeId];
            MethodIdentityTemplate targetSignature = catalog.ReadMethodSignature(declaration.Method)
                .Instantiate(new(owner.Id), declaration.DeclaringTypeArguments);
            List<(TypeEntry Type, IReadOnlyList<TypeIdentityTemplate> Arguments, bool Expand)> bounds = new();
            bool allKnown = receivers.Count != 0;
            foreach (ValueOrigin origin in receivers)
            {
                if (origin.Value.Type == null || origin.Value.Type.Id.StartsWith('!'))
                {
                    allKnown = false;
                    continue;
                }
                TypeEntry type = catalog.ResolveTypeDefinition(origin.Value.Type);
                bool expand = origin.Value.Kind != BehaviorValueKind.NewObject && !type.IsSealed && !type.IsValueType;
                bounds.Add((type, catalog.ReadResolvedTypeArguments(origin.Value.Type), expand));
            }
            if (!allKnown)
            {
                bounds = [(owner, declaration.DeclaringTypeArguments, true)];
            }
            // 不同接收范围解析出相同起点时（如 Object 与未知类型）共用同一遍历，结果序列不变。
            var key = (declaration.Method.Id, new TemplateList(declaration.DeclaringTypeArguments), new TemplateList(declaration.MethodTypeArguments),
                string.Concat(bounds.Select(bound => $"{bound.Type.Id.Length}:{bound.Type.Id}{bound.Arguments.Count}:"
                    + string.Concat(bound.Arguments.Select(argument => $"{argument.Text.Length}:{argument.Text}")) + (bound.Expand ? '+' : '-'))));
            if (!dispatch.Traversals.TryGetValue(key, out TargetCandidates? traversal))
            {
                traversal = new TargetCandidates(Traverse(catalog, declaration, owner, targetSignature, bounds, timing, dispatch));
                dispatch.Traversals.Add(key, traversal);
            }
            foreach (ResolvedMethodDefinition implementation in traversal.Read())
            {
                yield return implementation;
            }
        }

        // 从起点类型沿子类型展开，逐个匹配实现；同一构造声明与具体类型只匹配一次。
        private static IEnumerable<ResolvedMethodDefinition> Traverse(MethodCatalogResult catalog, ResolvedMethodDefinition declaration, TypeEntry owner,
            MethodIdentityTemplate targetSignature, List<(TypeEntry Type, IReadOnlyList<TypeIdentityTemplate> Arguments, bool Expand)> starts,
            AnalysisTiming timing, DispatchIndex dispatch)
        {
            TemplateList ownerArguments = new(declaration.DeclaringTypeArguments);
            Queue<(TypeEntry Type, IReadOnlyList<TypeIdentityTemplate> Arguments, bool Expand, ResolvedMethodDefinition? Inherited)> pending = new(
                starts.Select(bound => (bound.Type, bound.Arguments, bound.Expand, (ResolvedMethodDefinition?)null)));
            HashSet<(string Type, TemplateList Arguments, bool Expand)> visited = new();
            HashSet<(string Method, TemplateList Arguments)> result = new();
            bool expandAny = pending.Any(candidate => candidate.Expand);
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> children;
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> implementations;
            using (timing.Measure(AnalysisTiming.Part.HierarchyIndex))
            {
                children = expandAny ? catalog.DerivedTypesByBaseId : new Dictionary<string, IReadOnlyList<TypeEntry>>();
                implementations = expandAny && owner.IsInterface ? catalog.ImplementingTypesByInterfaceId : new Dictionary<string, IReadOnlyList<TypeEntry>>();
            }
            while (pending.TryDequeue(out var candidate))
            {
                timing.Count("接口候选类型访问");
                TypeEntry type = candidate.Type;
                if (!visited.Add((type.Id, new(candidate.Arguments), candidate.Expand)))
                {
                    continue;
                }

                ResolvedMethodDefinition? selected = null;
                if (!owner.IsInterface && declaration.DeclaringTypeArguments.Count == 0
                    && candidate.Inherited is { DeclaringTypeArguments.Count: 0 } inherited
                    && !catalog.HasDeclaredOverride(type, declaration.Method.Name))
                {
                    selected = inherited;
                    timing.Count("虚函数直接继承已有实现");
                }
                else if (!type.IsInterface && !type.IsAbstract)
                {
                    selected = ResolveImplementation(type, candidate.Arguments);
                }
                else if (type.Id == owner.Id && !owner.IsInterface && !declaration.Method.IsAbstract
                    && declaration.DeclaringTypeArguments.Count == 0)
                {
                    selected = declaration;
                }

                if (candidate.Expand)
                {
                    foreach (TypeEntry child in (children.GetValueOrDefault(type.Id) ?? Array.Empty<TypeEntry>())
                        .Concat(implementations.GetValueOrDefault(type.Id) ?? Array.Empty<TypeEntry>()))
                    {
                        pending.Enqueue((child, Placeholders(child.GenericParameters.Count), true, selected));
                    }
                }
                if (type.IsInterface || type.IsAbstract)
                {
                    continue;
                }
                if (selected != null && result.Add((selected.Method.Id, new(selected.DeclaringTypeArguments))))
                {
                    yield return selected;
                }
            }

            // 子类与基类共用匹配结果，不按每个具体子类重复扫描继承链。
            ResolvedMethodDefinition? ResolveImplementation(TypeEntry type, IReadOnlyList<TypeIdentityTemplate> arguments)
            {
                var implementationKey = (declaration.Method.Id, ownerArguments, new TemplateList(declaration.MethodTypeArguments), type.Id, new TemplateList(arguments));
                if (!dispatch.Implementations.TryGetValue(implementationKey, out ResolvedMethodDefinition? selected))
                {
                    timing.Count("接口具体类型实际匹配");
                    selected = MatchImplementation(type, arguments);
                    dispatch.Implementations.Add(implementationKey, selected);
                }
                return selected;
            }

            // 同一完整构造声明与具体类型只匹配一次，范围变化不重复比较继承和签名。
            ResolvedMethodDefinition? MatchImplementation(TypeEntry type, IReadOnlyList<TypeIdentityTemplate> arguments)
            {
                using var matchingTiming = timing.Measure(AnalysisTiming.Part.ImplementationMatch);
                if (!owner.IsInterface && type.Id != owner.Id && !catalog.HasDeclaredOverride(type, declaration.Method.Name))
                {
                    var parent = catalog.ReadInheritedTypes(type, arguments, includeInterfaces: false, directOnly: true).FirstOrDefault();
                    if (parent != null)
                    {
                        return ResolveImplementation(parent.Definition, parent.TypeArguments);
                    }
                }
                var hierarchyKey = (owner.Id, ownerArguments, type.Id, new TemplateList(arguments));
                if (!dispatch.Hierarchies.TryGetValue(hierarchyKey, out var binding))
                {
                    binding = BindHierarchy(type, arguments);
                    dispatch.Hierarchies.Add(hierarchyKey, binding);
                    timing.Count("接口类型关系实际匹配");
                }
                if (binding.Types.Length == 0)
                {
                    return null;
                }
                if (owner.IsInterface && type.SourceSymbol != null)
                {
                    using var memberTiming = timing.Measure(AnalysisTiming.Part.ImplementationMembers);
                    return catalog.ReadSourceInterfaceImplementation(type, declaration, binding.Types[0].TypeArguments);
                }
                ResolvedMethodDefinition? selected = null;
                foreach (var relation in binding.Types)
                {
                    using var memberTiming = timing.Measure(AnalysisTiming.Part.ImplementationMembers);
                    MethodEntry? match = catalog.ReadExplicitImplementations(relation.Definition, declaration.Method)
                        .FirstOrDefault(item => item.Target.DeclaringTypeArguments.Select(argument => argument.Substitute(relation.TypeArguments).Text)
                            .SequenceEqual(declaration.DeclaringTypeArguments.Select(argument => argument.Text))).Method;
                    match ??= catalog.ReadMethodCandidates(relation.Definition, declaration.Method.Name,
                        declaration.Method.GenericArity, targetSignature.Parameters.Count, false).FirstOrDefault(method =>
                    {
                        timing.Count("接口候选签名比较");
                        if (method.IsStatic || method.IsAbstract || method.Name != declaration.Method.Name || method.GenericArity != declaration.Method.GenericArity
                            || owner.IsInterface && !method.IsPublic || !owner.IsInterface && method.Id != declaration.Method.Id && (!method.IsVirtual || method.IsNewSlot))
                        {
                            return false;
                        }
                        if (owner.IsInterface && relation.Depth < binding.IntroductionDepth && (!method.IsVirtual || method.IsNewSlot))
                        {
                            return false;
                        }
                        MethodIdentityTemplate signature = catalog.ReadMethodSignature(method).Instantiate(new(relation.Definition.Id), relation.TypeArguments);
                        return signature.Parameters.Select(parameter => parameter.Text).SequenceEqual(targetSignature.Parameters.Select(parameter => parameter.Text))
                            && (!owner.IsInterface || signature.ReturnType == targetSignature.ReturnType);
                    });
                    if (match != null)
                    {
                        selected = new ResolvedMethodDefinition(match, relation.TypeArguments) { MethodTypeArguments = declaration.MethodTypeArguments };
                        break;
                    }
                }
                if (selected == null && !declaration.Method.IsAbstract)
                {
                    selected = declaration;
                }
                if (selected == null)
                {
                    throw new AnalysisException($"尚不能确定继承实现：{type.FullName} => {declaration.Method.Id}");
                }
                return selected;
            }

            // 同一构造接口与具体类型的继承对应不随接口方法变化，只建立一次。
            (MethodCatalogResult.InheritedTypeRelation[] Types, int IntroductionDepth) BindHierarchy(TypeEntry type, IReadOnlyList<TypeIdentityTemplate> arguments)
            {
                using var hierarchyTiming = timing.Measure(AnalysisTiming.Part.ImplementationHierarchy);
                var hierarchy = catalog.ReadInheritedTypes(type, arguments, owner.IsInterface)
                    .Prepend(new MethodCatalogResult.InheritedTypeRelation(type, arguments, false, true, 0)).ToArray();
                Dictionary<int, TypeIdentityTemplate> substitutions = new();
                bool related = false;
                foreach (var relationship in hierarchy.Where(relation => relation.Definition.Id == owner.Id))
                {
                    substitutions.Clear();
                    if (relationship.TypeArguments.Count == declaration.DeclaringTypeArguments.Count
                        && relationship.TypeArguments.Zip(declaration.DeclaringTypeArguments).All(pair => MatchType(pair.First.Text, pair.Second.Text, substitutions)))
                    {
                        related = true;
                        break;
                    }
                }
                if (!related)
                {
                    return (Array.Empty<MethodCatalogResult.InheritedTypeRelation>(), 0);
                }
                TypeIdentityTemplate[] bound = arguments.Select((argument, index) => substitutions.GetValueOrDefault(index) ?? argument).ToArray();
                if (!new TemplateList(bound).Equals(new TemplateList(arguments)))
                {
                    arguments = bound;
                    hierarchy = catalog.ReadInheritedTypes(type, arguments, owner.IsInterface)
                        .Prepend(new MethodCatalogResult.InheritedTypeRelation(type, arguments, false, true, 0)).ToArray();
                }
                int introductionDepth = !owner.IsInterface ? 0 : hierarchy.Where(relation => !relation.IsInterface
                    && catalog.ReadInheritedTypes(relation.Definition, relation.TypeArguments).Any(parent => parent.CanImplementInterface
                        && parent.Definition.Id == owner.Id && parent.TypeArguments.SequenceEqual(declaration.DeclaringTypeArguments)))
                    .Select(relation => relation.Depth).DefaultIfEmpty(0).Min();
                return (hierarchy.Where(relation => !relation.IsInterface).OrderBy(relation => relation.Depth).ToArray(), introductionDepth);
            }
        }

        // 泛型声明的参数位置与接口实际类型逐项对应，不创建调用环境。
        internal static bool MatchType(string template, string actual, Dictionary<int, TypeIdentityTemplate> substitutions)
        {
            if (template == actual)
            {
                return true;
            }
            int prefix = template.StartsWith("!!", StringComparison.Ordinal) ? 2 : 1;
            if (template.StartsWith('!') && int.TryParse(template.AsSpan(prefix), out int index))
            {
                index = prefix == 2 ? ~index : index;
                if (substitutions.TryGetValue(index, out TypeIdentityTemplate? previous))
                {
                    return previous.Text == actual;
                }
                substitutions.Add(index, new TypeIdentityTemplate(actual));
                return true;
            }
            if (template.EndsWith(']') && actual.EndsWith(']'))
            {
                int leftArray = template.LastIndexOf('[');
                int rightArray = actual.LastIndexOf('[');
                return leftArray >= 0 && rightArray >= 0 && template[leftArray..] == actual[rightArray..]
                    && MatchType(template[..leftArray], actual[..rightArray], substitutions);
            }
            if (TryReadConstructedType(template, out string left, out string[] leftArguments)
                && TryReadConstructedType(actual, out string right, out string[] rightArguments)
                && left == right && leftArguments.Length == rightArguments.Length)
            {
                return leftArguments.Zip(rightArguments).All(pair => MatchType(pair.First, pair.Second, substitutions));
            }
            return false;
        }

        // 根据类型声明和实参构造统一身份。
        internal static TypeIdentityTemplate ConstructTypeIdentity(TypeEntry definition, IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            return new TypeIdentityTemplate(definition.Id + (arguments.Count == 0 ? string.Empty : "<" + string.Join(',', arguments.Select(type => type.Text)) + ">"));
        }

        // 拆开泛型身份，保留嵌套类型和数组内的逗号。
        internal static bool TryReadConstructedType(string text, out string name, out string[] arguments)
        {
            int definitionEnd = ReadNamedIdentityEnd(text, 0);
            int start = text.IndexOf('<', definitionEnd);
            name = start < 0 ? text : text[..start];
            arguments = Array.Empty<string>();
            if (start < 0 || !text.EndsWith('>'))
            {
                return false;
            }

            List<string> values = new();
            int depth = 0;
            int beginning = start + 1;
            for (int index = beginning; index < text.Length - 1; index++)
            {
                int namedEnd = ReadNamedIdentityEnd(text, index);
                if (namedEnd > index)
                {
                    index = namedEnd - 1;
                    continue;
                }
                depth += text[index] is '<' or '[' ? 1 : text[index] is '>' or ']' ? -1 : 0;
                if (text[index] == ',' && depth == 0)
                {
                    values.Add(text[beginning..index]);
                    beginning = index + 1;
                }
            }

            values.Add(text[beginning..^1]);
            arguments = values.ToArray();
            return true;
        }

        // 类型名和文件路径已有长度编码，跳过其中编译器生成名称的尖括号。
        private static int ReadNamedIdentityEnd(string text, int start)
        {
            if (start >= text.Length || text[start] != 'A' || start + 1 >= text.Length || !char.IsAsciiDigit(text[start + 1])
                || start != 0 && text[start - 1] is not ('<' or ','))
            {
                return start;
            }
            int separator = text.IndexOf(':', start + 1);
            int position = separator + 1 + int.Parse(text.AsSpan(start + 1, separator - start - 1));
            separator = text.IndexOf(':', position + 1);
            position = separator + 1 + int.Parse(text.AsSpan(position + 1, separator - position - 1));
            if (text.AsSpan(position).StartsWith("|P", StringComparison.Ordinal))
            {
                separator = text.IndexOf(':', position + 2);
                position = separator + 1 + int.Parse(text.AsSpan(position + 2, separator - position - 2));
            }
            return position;
        }
    }

    /// <summary>按实际字段和注册函数查源码使用位置，只读取命中的函数事实。</summary>
    internal sealed class RegistrationIndex(MaterialSet material, MethodCatalogResult catalog, AnalysisTiming timing, int jobs, CancellationToken cancellation)
    {
        private Dictionary<string, List<RegistrationLocation>>? m_writes;
        private readonly Dictionary<string, List<RegistrationLocation>> m_calls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RegistrationLocation>> m_receivers = new(StringComparer.Ordinal);
        private readonly Dictionary<RegistrationLocation, SyntaxNode> m_nodes = new();
        private readonly Dictionary<(SemanticModel Model, SyntaxNode Node), ISymbol?> m_symbols = new();
        private readonly Dictionary<(string Name, bool Calls), RegistrationLocations> m_boundLocations = new();
        private (SourceAssemblyMaterial Assembly, SyntaxTree Tree)[] m_files = Array.Empty<(SourceAssemblyMaterial, SyntaxTree)>();

        private enum RegistrationKind : byte { Write, Call, Receiver }
        private readonly record struct RegistrationLocation(int File, TextSpan Span, int Kind);
        private readonly record struct RegistrationEntry(string Name, TextSpan Span, ushort Kind, RegistrationKind Index);

        // 全工程只保留位置；被实际查询的位置才还原语法对象。
        private (SourceAssemblyMaterial Assembly, SyntaxNode Node) ReadLocation(RegistrationLocation location)
        {
            if (!this.m_nodes.TryGetValue(location, out SyntaxNode? node))
            {
                node = this.m_files[location.File].Tree.GetRoot(cancellation).FindNode(location.Span, getInnermostNodeForTie: true);
                while (node.RawKind != location.Kind || node.Span != location.Span)
                {
                    node = node.Parent ?? throw new AnalysisException("注册位置不能还原为原语法节点。");
                }
                this.m_nodes.Add(location, node);
            }
            return (this.m_files[location.File].Assembly, node);
        }

        /// <summary>直接访问可能登记目标的语法，不为每个语法节点建立遍历迭代器。</summary>
        private sealed class RegistrationScanner : CSharpSyntaxWalker
        {
            internal List<RegistrationEntry> Entries { get; } = new();

            // 保存赋值位置，左侧的实际身份在查询时确认。
            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                Add(node.Left, false);
                base.VisitAssignmentExpression(node);
            }

            // 字段或局部变量的初始值同样是登记位置。
            public override void VisitVariableDeclarator(VariableDeclaratorSyntax node)
            {
                if (node.Initializer != null)
                {
                    Add(node, false);
                }
                base.VisitVariableDeclarator(node);
            }

            // 同时保留调用位置和接收字段的位置。
            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                Add(node.Expression, true, (node.Expression as MemberAccessExpressionSyntax)?.Expression);
                base.VisitInvocationExpression(node);
            }

            // 显式构造按语法类型名初筛，仍由编译器确认真实类型。
            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                Add(node, true);
                base.VisitObjectCreationExpression(node);
            }

            // 隐式构造保留在公共构造列表中。
            public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
            {
                Add(node, true);
                base.VisitImplicitObjectCreationExpression(node);
            }

            // 保留 this 和 base 构造调用。
            public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
            {
                Add(node, true);
                base.VisitConstructorInitializer(node);
            }

            // 只保存名称与跨度，不让整个文件的语法对象因索引而常驻。
            private void Add(SyntaxNode destination, bool call, ExpressionSyntax? receiver = null)
            {
                string? name = destination switch
                {
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
                    ObjectCreationExpressionSyntax creation => ".ctor:" + (creation.Type switch
                    {
                        SimpleNameSyntax simple => simple.Identifier.ValueText,
                        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                        _ => string.Empty,
                    }),
                    ImplicitObjectCreationExpressionSyntax or ConstructorInitializerSyntax => ".ctor",
                    _ => null,
                };
                if (name == null)
                {
                    return;
                }
                if (name == ".ctor:")
                {
                    name = ".ctor";
                }
                string? receiverName = receiver switch
                {
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null,
                };
                this.Entries.Add(new(name, destination.Span, checked((ushort)destination.RawKind), call ? RegistrationKind.Call : RegistrationKind.Write));
                if (receiverName != null)
                {
                    this.Entries.Add(new(receiverName, receiver!.Span, checked((ushort)receiver.RawKind), RegistrationKind.Receiver));
                }
            }
        }

        // 相同源码位置只绑定一次，字段名称相同的多次查询共用编译器结果。
        private ISymbol? ReadSymbol(SemanticModel model, SyntaxNode node)
        {
            if (!this.m_symbols.TryGetValue((model, node), out ISymbol? symbol))
            {
                timing.Count("注册位置编译器绑定");
                symbol = node is VariableDeclaratorSyntax ? model.GetDeclaredSymbol(node, cancellation)
                    : model.GetSymbolInfo(node, cancellation).Symbol;
                this.m_symbols.Add((model, node), symbol);
            }
            return symbol;
        }

        // 名称只用于筛选语法位置，随后必须核对编译器给出的真实声明。
        private void Build()
        {
            if (this.m_writes != null)
            {
                return;
            }
            using var registrationTiming = timing.Measure(AnalysisTiming.Part.RegistrationSyntax);
            this.m_writes = new(StringComparer.Ordinal);
            var files = this.m_files = material.SourceAssemblies.Where(assembly => assembly.IsCandidateSource).SelectMany(assembly => assembly.Compilation.SyntaxTrees
                .Select(tree => (Assembly: assembly, Tree: tree))).ToArray();
            var entries = new List<RegistrationEntry>[files.Length];
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = cancellation }, fileIndex =>
            {
                RegistrationScanner scanner = new();
                scanner.Visit(files[fileIndex].Tree.GetRoot(cancellation));
                entries[fileIndex] = scanner.Entries;
            });
            for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                foreach (var entry in entries[fileIndex])
                {
                    var index = entry.Index switch
                    {
                        RegistrationKind.Call => this.m_calls,
                        RegistrationKind.Receiver => this.m_receivers,
                        _ => this.m_writes,
                    };
                    if (!index.TryGetValue(entry.Name, out var locations))
                    {
                        locations = new();
                        index.Add(entry.Name, locations);
                    }
                    locations.Add(new(fileIndex, entry.Span, entry.Kind));
                }
            }
            timing.Count("注册扫描文件数", files.Length);
            timing.Count("注册语法候选数", entries.Sum(items => (long)items.Count));
        }

        // 字段赋值的所属函数提供值，不能把其其他调用当作使用方的调用。
        internal IEnumerable<(MethodEntry Owner, MethodEntry? Target)> FindMemberWriters(BehaviorMemberReference member)
        {
            foreach (var location in ReadLocations(member.Name, false).Read(member.DeclaringTypeDefinitionId))
            {
                SemanticModel model = catalog.ReadSourceModel(location.Assembly.Compilation, location.Node.SyntaxTree);
                foreach (MethodEntry method in ReadOwners(model, location.Node, location.Assembly.AssemblyPath))
                {
                    MethodEntry? target = location.Node.Parent is MemberAccessExpressionSyntax access
                        && access.Expression == location.Node && access.Parent is InvocationExpressionSyntax invocation
                        && ReadSymbol(model, invocation) is IMethodSymbol called
                            ? catalog.ReadSourceDeclaration(called, location.Assembly.AssemblyPath) : null;
                    yield return (method, target);
                }
            }
        }

        // 已请求的注册参数只查该函数的实际调用点，不查全工程的初始化顺序。
        internal IEnumerable<(MethodEntry Owner, MethodEntry? Target)> FindCallers(MethodEntry method)
        {
            if (method.SourceSymbol is { AssociatedSymbol: null } source)
            {
                List<IMethodSymbol> declarations = new();
                for (IMethodSymbol? parent = source.OverriddenMethod; parent != null; parent = parent.OverriddenMethod)
                {
                    declarations.Add(parent);
                }
                declarations.AddRange(source.ContainingType.AllInterfaces.SelectMany(type => type.GetMembers(source.Name).OfType<IMethodSymbol>())
                    .Where(slot => source.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                        && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, source.OriginalDefinition)));
                foreach (IMethodSymbol declaration in declarations.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
                {
                    MethodEntry declared = catalog.ReadSourceDeclaration(declaration, method.AssemblyPath!);
                    foreach (var location in ReadLocations(declared.Name, true).Read(declared.Id))
                    {
                        SemanticModel model = catalog.ReadSourceModel(location.Assembly.Compilation, location.Node.SyntaxTree);
                        foreach (MethodEntry owner in ReadOwners(model, location.Node, location.Assembly.AssemblyPath))
                        {
                            yield return (owner, declared);
                        }
                    }
                }
            }
            IPropertySymbol? property = method.SourceSymbol?.AssociatedSymbol as IPropertySymbol;
            string name = property?.Name ?? (method.Kind == MethodKind.Constructor ? ".ctor:" + ReadConstructorTypeName(method) : method.Name);
            string identity = property == null ? method.Id : MethodCatalog.SourceNamedTypeId(property.ContainingType);
            foreach (var location in ReadLocations(name, property == null).Read(identity))
            {
                SemanticModel model = catalog.ReadSourceModel(location.Assembly.Compilation, location.Node.SyntaxTree);
                if (property != null && (ReadSymbol(model, location.Node) is not IPropertySymbol assigned
                    || assigned.SetMethod == null || catalog.ReadSourceDeclaration(assigned.SetMethod, location.Assembly.AssemblyPath).Id != method.Id))
                {
                    continue;
                }
                foreach (MethodEntry owner in ReadOwners(model, location.Node, location.Assembly.AssemblyPath))
                {
                    yield return (owner, method);
                }
            }
        }

        // 构造位置按语法类型名初筛；DLL 构造函数没有源码符号时取目录中的简单类型名。
        private string ReadConstructorTypeName(MethodEntry method)
        {
            if (method.SourceSymbol != null)
            {
                return method.SourceSymbol.ContainingType.Name;
            }
            string name = catalog.TypesById[method.TypeId].FullName;
            int generic = name.IndexOfAny(new[] { '<', '`' });
            name = generic < 0 ? name : name[..generic];
            return name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1)..];
        }

        // 同名位置只绑定并分类一次，查询仍按需推进，不预先读取外围函数体。
        private RegistrationLocations ReadLocations(string name, bool calls)
        {
            Build();
            if (!this.m_boundLocations.TryGetValue((name, calls), out RegistrationLocations? locations))
            {
                locations = new RegistrationLocations(timing.MeasureEnumeration(BindLocations(name, calls), AnalysisTiming.Part.Registrations).GetEnumerator());
                this.m_boundLocations.Add((name, calls), locations);
            }
            return locations;
        }

        // 将语法名称还原成真实声明，保留原来的文件和出现顺序。
        private IEnumerable<(string Identity, SourceAssemblyMaterial Assembly, SyntaxNode Node)> BindLocations(string name, bool calls)
        {
            if (calls && name.StartsWith(".ctor:", StringComparison.Ordinal))
            {
                IndexImplicitConstructors();
            }
            RegistrationLocation[] locations = (calls
                ? (this.m_calls.GetValueOrDefault(name) ?? Enumerable.Empty<RegistrationLocation>())
                : (this.m_writes!.GetValueOrDefault(name) ?? Enumerable.Empty<RegistrationLocation>())
                    .Concat(this.m_receivers.GetValueOrDefault(name) ?? Enumerable.Empty<RegistrationLocation>())).ToArray();
            // 只绑定当前批次，后续位置随候选查询继续推进。
            const int BindingBatchSize = 64;
            for (int index = 0; index < locations.Length; index++)
            {
                if (index % BindingBatchSize == 0)
                {
                    PrepareSymbols(locations[index..Math.Min(index + BindingBatchSize, locations.Length)].Select(ReadLocation).ToArray());
                }
                var location = ReadLocation(locations[index]);
                SemanticModel model = catalog.ReadSourceModel(location.Assembly.Compilation, location.Node.SyntaxTree);
                ISymbol? symbol = ReadSymbol(model, location.Node);
                if (calls && symbol is IMethodSymbol method)
                {
                    if (name.StartsWith(".ctor:", StringComparison.Ordinal) && method.ContainingType.Name != name[6..])
                    {
                        continue;
                    }
                    yield return (catalog.ReadSourceDeclaration(method, location.Assembly.AssemblyPath).Id, location.Assembly, location.Node);
                }
                else if (!calls && symbol is IFieldSymbol field)
                {
                    yield return (MethodCatalog.SourceNamedTypeId(field.ContainingType), location.Assembly, location.Node);
                }
                else if (!calls && symbol is IPropertySymbol property)
                {
                    yield return (MethodCatalog.SourceNamedTypeId(property.ContainingType), location.Assembly, location.Node);
                }
            }
        }

        // 公共构造位置只确认一次，按真实类型并入列表，保留显式构造在前的原顺序。
        private void IndexImplicitConstructors()
        {
            if (!this.m_calls.Remove(".ctor", out var locations))
            {
                return;
            }

            PrepareSymbols(locations.Select(ReadLocation).ToArray());
            foreach (RegistrationLocation stored in locations)
            {
                var location = ReadLocation(stored);
                SemanticModel model = catalog.ReadSourceModel(location.Assembly.Compilation, location.Node.SyntaxTree);
                if (ReadSymbol(model, location.Node) is not IMethodSymbol method)
                {
                    continue;
                }
                string name = ".ctor:" + method.ContainingType.Name;
                if (!this.m_calls.TryGetValue(name, out var matches))
                {
                    matches = new();
                    this.m_calls.Add(name, matches);
                }
                matches.Add(stored);
            }
        }

        // 独立文件并行确认符号，完成后按原顺序合并，不并发修改注册索引。
        private void PrepareSymbols((SourceAssemblyMaterial Assembly, SyntaxNode Node)[] locations)
        {
            var groups = locations.Distinct().GroupBy(location => (location.Assembly.Compilation, location.Node.SyntaxTree)).ToArray();
            var results = new (SemanticModel Model, SyntaxNode Node, ISymbol? Symbol)[groups.Length][];
            Parallel.For(0, groups.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = cancellation }, index =>
            {
                SemanticModel model = catalog.ReadSourceModel(groups[index].Key.Compilation, groups[index].Key.SyntaxTree);
                results[index] = groups[index].Where(location => !this.m_symbols.ContainsKey((model, location.Node)))
                    .Select(location => (model, location.Node, location.Node is VariableDeclaratorSyntax
                        ? model.GetDeclaredSymbol(location.Node, cancellation) : model.GetSymbolInfo(location.Node, cancellation).Symbol)).ToArray();
            });
            foreach (var result in results.SelectMany(result => result))
            {
                this.m_symbols.Add((result.Model, result.Node), result.Symbol);
                timing.Count("注册位置编译器绑定");
            }
        }

        /// <summary>一个同名列表只扫描一次，各声明复用自己的位置；提前停止后保留扫描位置。</summary>
        private sealed class RegistrationLocations(IEnumerator<(string Identity, SourceAssemblyMaterial Assembly, SyntaxNode Node)> remaining)
        {
            private readonly Dictionary<string, List<(SourceAssemblyMaterial Assembly, SyntaxNode Node)>> m_locations = new(StringComparer.Ordinal);
            private bool m_complete;

            // 优先读取已分类位置，只有需要更多候选时才继续绑定源码。
            internal IEnumerable<(SourceAssemblyMaterial Assembly, SyntaxNode Node)> Read(string identity)
            {
                if (!this.m_locations.TryGetValue(identity, out var locations))
                {
                    locations = new();
                    this.m_locations.Add(identity, locations);
                }
                int index = 0;
                while (true)
                {
                    if (index < locations.Count)
                    {
                        yield return locations[index++];
                    }
                    else if (this.m_complete)
                    {
                        yield break;
                    }
                    else if (remaining.MoveNext())
                    {
                        var next = remaining.Current;
                        if (!this.m_locations.TryGetValue(next.Identity, out var matches))
                        {
                            matches = new();
                            this.m_locations.Add(next.Identity, matches);
                        }
                        matches.Add((next.Assembly, next.Node));
                    }
                    else
                    {
                        remaining.Dispose();
                        this.m_complete = true;
                    }
                }
            }
        }

        // 普通函数和字段初始化均回到原函数目录，不创建额外的执行环境。
        private IEnumerable<MethodEntry> ReadOwners(SemanticModel model, SyntaxNode node, string path)
        {
            ISymbol? owner = model.GetEnclosingSymbol(node.SpanStart, cancellation);
            if (owner is IMethodSymbol method)
            {
                yield return catalog.ReadSourceDeclaration(method, path);
            }
            else if (owner is IFieldSymbol field)
            {
                TypeEntry type = catalog.ResolveTypeDefinition(catalog.ReadSourceTypeReference(field.ContainingType, path));
                foreach (MethodEntry constructor in catalog.GetMethods(type).Where(candidate => candidate.Kind
                    == (field.IsStatic ? MethodKind.StaticConstructor : MethodKind.Constructor)))
                {
                    yield return constructor;
                }
            }
        }
    }

    /// <summary>一个值只属于一个函数，编号不包含调用路径。</summary>
    public readonly record struct BehaviorValueReference(string MethodId, int ValueId, int MethodOrdinal = 0)
    {
        // 正式分析使用固定函数编号，避免反复计算长函数签名的哈希。
        /// <summary>保留完整身份比较，仅简化字典定位计算。</summary>
        public override int GetHashCode() => MethodOrdinal == 0 ? HashCode.Combine(MethodId, ValueId) : HashCode.Combine(MethodOrdinal, ValueId);
    }

    /// <summary>函数说明中的原始值，不保存父调用环境。</summary>
    public sealed record ValueOrigin(BehaviorValueReference Reference, BehaviorValue Value)
    {
        /// <summary>委托绑定的对象，与函数地址分开保存。</summary>
        public IReadOnlyList<BehaviorValueReference>? BoundReceiver { get; init; }

        /// <summary>延迟枚举体捕获的固定实参，不保存调用路径。</summary>
        public IReadOnlyList<IReadOnlyList<BehaviorValueReference>>? IteratorArguments { get; init; }

    }

    /// <summary>索引原始赋值和固定实参，只在读取指定值时查询，不展开对象图。</summary>
    public sealed class ValueSourceIndex
    {
        private readonly MethodCatalogResult m_catalog;
        private readonly Dictionary<string, MethodEntry> m_definitions;
        private readonly Dictionary<string, MethodBehavior> m_bodies;
        private readonly CancellationToken m_cancellationToken;
        private readonly Dictionary<string, int> m_functions = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Method, BehaviorFlowPoint Point), ResolvedCall> m_calls = new();
        private readonly Dictionary<string, Dictionary<BehaviorFlowPoint, ResolvedCall>> m_callsByMethod = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Method, BehaviorFlowPoint Point), BehaviorCall> m_bodyCalls = new();
        private readonly Dictionary<BehaviorValueReference, BehaviorCall> m_valueCalls = new();
        private readonly Dictionary<(string Method, BehaviorFlowPoint Point, int Index), BehaviorValueReference> m_outResults = new();
        private readonly Dictionary<BehaviorValueReference, IReadOnlyList<ValueOrigin>> m_results = new();
        private readonly Dictionary<(string Method, bool Tracking), ValueOrigin[]> m_returns = new();
        private readonly Dictionary<string, Dictionary<(string Caller, BehaviorFlowPoint Point), ResolvedCallTarget[]>> m_incoming = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Method, BehaviorWrite Write)>> m_writes = new(StringComparer.Ordinal);
        private readonly Dictionary<ISymbol, List<BehaviorValueReference>> m_sourceSlots = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, HashSet<BehaviorFlowPoint>> m_pendingCalls = new(StringComparer.Ordinal);
        private readonly HashSet<(string Method, BehaviorFlowPoint Point)> m_neededCalls = new();
        private readonly Dictionary<string, HashSet<string>> m_valueUsers = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Method, BehaviorFlowPoint Point), HashSet<string>> m_callUsers = new();
        private readonly Dictionary<string, bool> m_liveValues = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_changedReaders = new(StringComparer.Ordinal);
        private readonly Dictionary<SourceKey, HashSet<(string Method, BehaviorFlowPoint? Call)>> m_readers = new();
        private readonly List<HashSet<SourceKey>> m_captures = new();
        private readonly Dictionary<string, BehaviorMemberReference> m_registrationMembers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodEntry> m_registrationMethods = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_requestedMembers = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_requestedMethods = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_completedRegistrations = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Method, int Slot, int Block), BehaviorAssignment[]> m_slotWrites = new();
        private readonly Dictionary<string, Dictionary<int, List<int>>> m_predecessors = new(StringComparer.Ordinal);
        private readonly Dictionary<BehaviorValueReference, int[]> m_reaching = new();
        private readonly Dictionary<OriginKey, BehaviorValueReference> m_stored = new();
        private readonly Dictionary<(BehaviorTypeReference Source, BehaviorTypeReference Target), BehaviorTypeReference> m_convertedTypes = new();
        private readonly Dictionary<(string Type, string Arguments), (int Types, IReadOnlyList<BehaviorTypeReference>? Values, string? Failure)> m_runtimeTypes = new();
        private int m_nextStored = -1;

        // 共用已读取的原始事实，不再建立逐函数的对象状态副本。
        internal ValueSourceIndex(MethodCatalogResult catalog, Dictionary<string, MethodEntry> definitions,
            Dictionary<string, MethodBehavior> bodies, CancellationToken cancellationToken)
        {
            this.m_catalog = catalog;
            this.m_definitions = definitions;
            this.m_bodies = bodies;
            this.m_cancellationToken = cancellationToken;
        }

        internal Dictionary<string, MethodBehavior> Behaviors => this.m_bodies;
        internal AnalysisTiming Timing { get; } = new();
        internal bool UseReflectionBaseline { get; init; }
        internal const string ReflectionBaselineFailure = "反射目标未展开：日志决定采用当前源码人工标签，真实行为未证明";
        internal Func<string, bool> IsBehaviorNeeded { get; set; } = null!;
        internal Dictionary<(BehaviorValueReference Call, string Selection), (IReadOnlyList<ValueOrigin> Origins, string? Failure)> ReflectionQueries { get; } = new();
        /// <summary>本轮真正查询的原始值节点数量。</summary>
        public long OriginReadCount { get; private set; }

        internal readonly record struct SourceKey(string Kind, string Owner, int Value = 0, string Member = "");

        // 仅登记查询所依赖的输入；没有全局版本或整表失效。
        internal CaptureScope CaptureInputs() => new(this);

        internal readonly struct CaptureScope : IDisposable
        {
            private readonly ValueSourceIndex m_owner;
            internal HashSet<SourceKey> Inputs { get; }

            // 收集当前调用读取过的固定关系。
            internal CaptureScope(ValueSourceIndex owner)
            {
                this.m_owner = owner;
                this.Inputs = new();
                owner.m_captures.Add(this.Inputs);
            }

            // 结束本次查询收集，不保存计算环境。
            public void Dispose() => this.m_owner.m_captures.RemoveAt(this.m_owner.m_captures.Count - 1);
        }

        // 首次登记函数并分配唯一编号。
        internal void Include(MethodEntry method)
        {
            if (this.m_definitions.TryAdd(method.Id, method))
            {
                this.m_functions.Add(method.Id, this.m_functions.Count + 1);
            }
        }

        // 所有调用者共用原函数编号。
        internal int GetMethodOrdinal(string method) => this.m_functions[method];

        // 读取已登记函数的目录记录。
        internal MethodEntry ReadMethod(string method) => this.m_definitions[method];

        // 统一引用编号，防止同一个原始值重复进入字典。
        private BehaviorValueReference Normalize(BehaviorValueReference value) => value with { MethodOrdinal = GetMethodOrdinal(value.MethodId) };

        // 原始事实仅建表一次；不提前计算返回值、对象成员或数组内容。
        internal void ReadBody(MethodBehavior body)
        {
            this.m_pendingCalls.Add(body.MethodId, body.Calls.Select(call => call.Point).ToHashSet());
            Dictionary<int, List<int>> predecessors = new();
            foreach (BehaviorFlowBlock block in body.Blocks)
            {
                for (int index = 0; index < block.Successors.Count; index++)
                {
                    if (block.Successors[index].TargetBlockId is int target)
                    {
                        if (!predecessors.TryGetValue(target, out List<int>? sources))
                        {
                            predecessors.Add(target, sources = new());
                        }
                        sources.Add(block.Id);
                    }
                }
            }
            this.m_predecessors.Add(body.MethodId, predecessors);
            List<BehaviorAssignment> assignments = new(body.Assignments);
            foreach (BehaviorCall call in body.Calls)
            {
                for (int index = 0; index < call.Arguments.Count; index++)
                {
                    BehaviorArgument argument = call.Arguments[index];
                    BehaviorValue address = body.Values[argument.ValueId];
                    if (argument.RefKind != RefKind.Out || address.Kind != BehaviorValueKind.Address
                        || address.Member != null || address.InputValueIds.Count != 1
                        || body.Values[address.InputValueIds[0]].Kind != BehaviorValueKind.Local)
                    {
                        continue;
                    }
                    // out 完成后替换局部槽的旧值；实际内容仍读取被调用函数的写入。
                    BehaviorValueReference output = Normalize(new(body.MethodId, this.m_nextStored--));
                    this.m_results.Add(output, new[] { new ValueOrigin(output,
                        new(output.ValueId, BehaviorValueKind.CallResult, "out", index, Array.Empty<int>())) });
                    this.m_valueCalls.Add(output, call);
                    this.m_outResults[(body.MethodId, call.Point, index)] = output;
                    assignments.Add(new(address.InputValueIds[0], output.ValueId, call.Point));
                }
            }
            BehaviorAssignment[] sorted = assignments.OrderBy(item => item.TargetValueId).ThenBy(item => item.Point.BlockId).ThenBy(item => item.Point.Order).ToArray();
            for (int start = 0, end = 0; start < sorted.Length; start = end)
            {
                while (end < sorted.Length && sorted[end].TargetValueId == sorted[start].TargetValueId && sorted[end].Point.BlockId == sorted[start].Point.BlockId)
                {
                    end++;
                }
                this.m_slotWrites.Add((body.MethodId, sorted[start].TargetValueId, sorted[start].Point.BlockId), sorted[start..end]);
            }
            foreach (BehaviorValue value in body.Values)
            {
                if (value.SourceSymbol != null)
                {
                    if (!this.m_sourceSlots.TryGetValue(value.SourceSymbol, out List<BehaviorValueReference>? slots))
                    {
                        slots = new();
                        this.m_sourceSlots.Add(value.SourceSymbol, slots);
                    }
                    slots.Add(Normalize(new(body.MethodId, value.Id)));
                }
            }
            foreach (BehaviorCall call in body.Calls)
            {
                this.m_bodyCalls.Add((body.MethodId, call.Point), call);
                if (call.ResultValueId is int result)
                {
                    this.m_valueCalls.Add(Normalize(new(body.MethodId, result)), call);
                }
            }
            RegisterWrites(body.MethodId, body.Writes);
            Notify(new("body", body.MethodId));
        }

        // 字段只按真实身份收集写入位置，不组装每个调用位置的成员状态。
        private void RegisterWrites(string method, IEnumerable<BehaviorWrite> writes)
        {
            foreach (BehaviorWrite write in writes.Where(write => write.Member != null))
            {
                string key = MemberKey(write.Member!);
                if (!this.m_writes.TryGetValue(key, out var entries))
                {
                    entries = new();
                    this.m_writes.Add(key, entries);
                }
                if (!entries.Contains((method, write)))
                {
                    entries.Add((method, write));
                    Notify(new("member", key));
                }
            }
        }

        // 新增固定绑定只通知实际读取过它的位置。
        internal void Bind(ResolvedCall call)
        {
            var key = (call.CallerMethodId, call.Call.Point);
            this.m_calls[key] = call;
            if (!this.m_callsByMethod.TryGetValue(call.CallerMethodId, out var localCalls))
            {
                localCalls = new();
                this.m_callsByMethod.Add(call.CallerMethodId, localCalls);
            }
            localCalls[call.Call.Point] = call;
            foreach (var group in call.Targets.GroupBy(target => target.MethodId))
            {
                if (!this.m_incoming.TryGetValue(group.Key, out var incoming))
                {
                    incoming = new();
                    this.m_incoming.Add(group.Key, incoming);
                }
                incoming[key] = group.ToArray();
                Notify(new("incoming", group.Key));
            }
            RegisterWrites(call.CallerMethodId, call.Writes);
            Notify(CallKey(call.CallerMethodId, call.Call.Point));
        }

        // 返回独立调用位置的待处理列表，普通已绑定调用不重复解析。
        internal IEnumerable<BehaviorCall> ReadPendingCalls(string method)
        {
            if (!this.m_pendingCalls.TryGetValue(method, out HashSet<BehaviorFlowPoint>? pending) || pending.Count == 0)
            {
                yield break;
            }
            BehaviorFlowPoint[] points = pending.ToArray();
            pending.Clear();
            Array.Sort(points, static (left, right) => left.BlockId != right.BlockId
                ? left.BlockId.CompareTo(right.BlockId) : left.Order.CompareTo(right.Order));
            foreach (BehaviorFlowPoint point in points)
            {
                yield return this.m_bodyCalls[(method, point)];
            }
        }

        // 先取得已知普通调用的行为，再处理需要查询目标的调用。
        internal void DeferCall(string method, BehaviorFlowPoint point) => this.m_pendingCalls[method].Add(point);

        // 保存实际读取依赖，后续变化直接定位到调用位置。
        internal void SaveCallInputs(string method, BehaviorFlowPoint point, IReadOnlySet<SourceKey> inputs)
        {
            foreach (SourceKey input in inputs)
            {
                Subscribe(input, (method, point));
            }
        }

        // 需求是否变化只涉及对应调用，不触发全图搜索。
        internal void ObserveCallDemand(string method, BehaviorFlowPoint point) => Observe(CallKey(method, point), method);

        // 登记某条固定关系的读取者。
        private void Subscribe(SourceKey key, (string Method, BehaviorFlowPoint? Call) reader)
        {
            if (!this.m_readers.TryGetValue(key, out var readers))
            {
                readers = new();
                this.m_readers.Add(key, readers);
            }
            readers.Add(reader);
        }

        // 记录查询读到的事实来源，不保存所有对象来源的展开结果。
        private void Observe(SourceKey key, string reader)
        {
            if (this.m_captures.Count == 0)
            {
                Subscribe(key, (reader, null));
            }
            else
            {
                foreach (var inputs in this.m_captures)
                {
                    inputs.Add(key);
                }
            }
        }

        // 一次变化只通知登记过的使用方。
        private void Notify(SourceKey key)
        {
            if (!this.m_readers.TryGetValue(key, out var readers))
            {
                return;
            }
            foreach (var reader in readers)
            {
                this.m_changedReaders.Add(reader.Method);
                if (reader.Call is BehaviorFlowPoint point && this.m_pendingCalls.TryGetValue(reader.Method, out var pending))
                {
                    pending.Add(point);
                }
            }
        }

        // 调用身份仅由原函数和原位置组成。
        private static SourceKey CallKey(string method, BehaviorFlowPoint point) => new("call", method, point.BlockId, point.Order.ToString());

        // 请求缺失函数体，只触发一次读取。
        private bool RequireBody(string method, Query query)
        {
            AddValueUser(method, query.Reader);
            Observe(new("body", method), query.Reader);
            if (this.m_bodies.ContainsKey(method))
            {
                return true;
            }
            this.m_changedReaders.Add(method);
            return false;
        }

        // 返回或回调目标确实被读取时才要求对应调用。
        private void RequireCall(string method, BehaviorCall call, Query query)
        {
            AddValueUser(method, query.Reader);
            var key = (method, call.Point);
            if (!this.m_callUsers.TryGetValue(key, out HashSet<string>? users))
            {
                users = new(StringComparer.Ordinal);
                this.m_callUsers.Add(key, users);
            }
            users.Add(query.Reader);
            Observe(CallKey(method, call.Point), query.Reader);
            if (this.m_neededCalls.Add((method, call.Point)))
            {
                this.m_pendingCalls[method].Add(call.Point);
                this.m_changedReaders.Add(method);
            }
        }

        // 只处理仍有活跃使用者的返回来源。
        internal bool NeedsValues(string method) => (this.m_valueUsers.ContainsKey(method)
            || this.m_returns.ContainsKey((method, false)) || this.m_returns.ContainsKey((method, true)))
            && HasValueReader(method);

        // 查找指定调用的结果是否真正被使用。
        internal bool NeedsCall(string method, BehaviorFlowPoint point) => this.m_callUsers.TryGetValue((method, point), out var users)
            && users.Any(HasValueReader);

        // 值需求连接到真正使用它的函数，已不需要的查询不再调度。
        private void AddValueUser(string method, string reader)
        {
            if (method == reader)
            {
                return;
            }
            if (!this.m_valueUsers.TryGetValue(method, out HashSet<string>? users))
            {
                users = new(StringComparer.Ordinal);
                this.m_valueUsers.Add(method, users);
            }
            if (users.Add(reader))
            {
                this.m_liveValues.Clear();
            }
        }

        // 只有使用关系或 Setter 状态改变才重新检查活跃需求。
        internal void InvalidateValueReaders() => this.m_liveValues.Clear();

        // 仅沿固定需求关系找活跃使用者，循环不会让无使用者的需求永久存活。
        private bool HasValueReader(string method)
        {
            if (this.IsBehaviorNeeded(method))
            {
                return true;
            }
            if (this.m_liveValues.TryGetValue(method, out bool known))
            {
                return known;
            }
            Stack<string> pending = new(new[] { method });
            HashSet<string> visited = new(StringComparer.Ordinal);
            while (pending.TryPop(out string? current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }
                bool cached = this.m_liveValues.TryGetValue(current, out bool active);
                if (this.IsBehaviorNeeded(current) || cached && active)
                {
                    this.m_liveValues[method] = true;
                    return true;
                }
                if (cached)
                {
                    // 已证明没有活跃使用者的分支无需重复搜索；关系变化仍按原规则失效。
                    continue;
                }
                if (this.m_valueUsers.TryGetValue(current, out var readers))
                {
                    foreach (string reader in readers)
                    {
                        pending.Push(reader);
                    }
                }
            }
            foreach (string inactive in visited)
            {
                this.m_liveValues[inactive] = false;
            }
            return false;
        }

        // 返回具体受影响的函数，不全量补扫。
        internal string[] TakeChangedReaders()
        {
            string[] changed = this.m_changedReaders.ToArray();
            this.m_changedReaders.Clear();
            return changed;
        }

        // 只索取指定字段的登记位置。
        private void RequestMember(BehaviorMemberReference member, Query query)
        {
            string key = MemberKey(member);
            Observe(new("member", key), query.Reader);
            if (this.m_requestedMembers.Add(key))
            {
                this.m_registrationMembers.Add(key, member);
            }
        }

        // 只索取指定函数的实参登记位置。
        private void RequestIncoming(string method, Query query)
        {
            AddValueUser(method, query.Reader);
            Observe(new("incoming", method), query.Reader);
            Observe(new("registration", method), query.Reader);
            if (this.m_requestedMethods.Add(method))
            {
                this.m_registrationMethods.Add(method, this.m_definitions[method]);
            }
        }

        // 沿现有使用关系确认需求，Setter 仍有返回或写回读取者时继续查全注册。
        internal bool NeedsRegistration(MethodEntry? method, BehaviorMemberReference? member)
        {
            if (method != null)
            {
                return IsBehaviorNeeded(method.Id) || HasValueReader(method.Id);
            }
            return this.m_readers.TryGetValue(new("member", MemberKey(member!)), out var readers)
                && readers.Any(reader => HasValueReader(reader.Method));
        }

        // 登记完成后重新判断尚未闭合的相关查询。
        internal void CompleteRegistration(string method)
        {
            if (this.m_completedRegistrations.Add(method))
            {
                Notify(new("registration", method));
            }
        }

        // 读取一批明确字段请求。
        internal BehaviorMemberReference[] TakeRegistrationMembers()
        {
            BehaviorMemberReference[] result = this.m_registrationMembers.Values.ToArray();
            this.m_registrationMembers.Clear();
            return result;
        }

        // 读取一批明确函数请求。
        internal MethodEntry[] TakeRegistrationMethods()
        {
            MethodEntry[] result = this.m_registrationMethods.Values.ToArray();
            this.m_registrationMethods.Clear();
            return result;
        }

        // 外围函数只连接需要的注册调用，不分析它的其他业务调用。
        internal void RequestRegistrationCall(string method, MethodEntry target)
        {
            AddValueUser(method, target.Id);
            if (this.m_bodies.TryGetValue(method, out MethodBehavior? body))
            {
                foreach (BehaviorCall call in body.Calls.Where(call => this.m_catalog.ResolveMethodDefinition(call.Target, true).Method.Id == target.Id))
                {
                    this.m_pendingCalls[method].Add(call.Point);
                }
            }
            this.m_changedReaders.Add(method);
        }

        // 字段以所属程序集类型及成员身份区分，泛型实参另在绑定处核对。
        internal static string MemberKey(BehaviorMemberReference member) => member.DeclaringTypeDefinitionId + "::" + member.Name;

        // 找到产生某个调用结果（含 out 结果）的调用位置。
        internal bool TryReadValueCall(BehaviorValueReference value, out BehaviorCall? call)
            => this.m_valueCalls.TryGetValue(Normalize(value), out call);

        // 反射写入沿用普通写入判断。
        internal IEnumerable<BehaviorWrite> ReadReflectionWrites(string method) => this.m_callsByMethod.TryGetValue(method, out var calls)
            ? calls.Values.SelectMany(call => call.Writes) : Enumerable.Empty<BehaviorWrite>();

        // 语义操作仅保存其实际结果，不创建字段和对象的调用代理。
        internal void SetResult(BehaviorCall call, string method, IEnumerable<ValueOrigin> values)
        {
            if (call.ResultValueId is not int value)
            {
                return;
            }
            BehaviorValueReference reference = Normalize(new(method, value));
            ValueOrigin[] result = Distinct(values);
            if (this.m_results.TryGetValue(reference, out var old) && old.Select(Key).SequenceEqual(result.Select(Key)))
            {
                return;
            }
            this.m_results[reference] = result;
            Notify(CallKey(method, call.Point));
        }

        // 标准库 TryGetValue/TryParse 的局部 out 结果直接登记，避免展开库函数的异常或解析实现。
        internal void SetOutResult(BehaviorCall call, string method, int index, BehaviorValue value)
        {
            if (!this.m_outResults.TryGetValue((method, call.Point, index), out BehaviorValueReference output))
            {
                return;
            }
            ValueOrigin[] result = { new ValueOrigin(output, value with { Id = output.ValueId }) };
            if (this.m_results.TryGetValue(output, out var old) && old.Select(Key).SequenceEqual(result.Select(Key)))
            {
                return;
            }
            this.m_results[output] = result;
            Notify(CallKey(method, call.Point));
        }

        // 在固定关系上从空集合归并返回类别，避免递归关系保留早期的临时未知。
        internal void CompleteReturns()
        {
            var keys = this.m_returns.Keys.ToArray();
            string[] methods = keys.Select(key => key.Method).Distinct(StringComparer.Ordinal).Where(this.m_bodies.ContainsKey).ToArray();
            foreach (var key in keys)
            {
                this.m_returns[key] = Array.Empty<ValueOrigin>();
            }
            Queue<string> pending = new(methods);
            HashSet<string> queued = new(methods, StringComparer.Ordinal);
            while (pending.TryDequeue(out string? method))
            {
                queued.Remove(method);
                UpdateReturns(method, buildingSummary: true);
                foreach (string reader in TakeChangedReaders())
                {
                    if ((this.m_returns.ContainsKey((reader, false)) || this.m_returns.ContainsKey((reader, true)))
                        && this.m_bodies.ContainsKey(reader) && queued.Add(reader))
                    {
                        pending.Enqueue(reader);
                    }
                }
            }
        }

        // 每个函数只保存返回对象的类别与创建类型，不保存返回位置和泛型成员明细。
        internal void UpdateReturns(string method, bool buildingSummary = false)
        {
            UpdateReturn(method, false, buildingSummary);
            UpdateReturn(method, true, buildingSummary);
        }

        // 真实来源保持不变，追踪来源在可信 NLT 调用处停止传递创建要求。
        private void UpdateReturn(string method, bool tracking, bool buildingSummary)
        {
            var key = (method, tracking);
            if (!this.m_returns.ContainsKey(key))
            {
                return;
            }
            using var timing = this.Timing.Measure(AnalysisTiming.Part.Origins);
            BehaviorValueReference summary = Normalize(new(method, 0));
            ValueOrigin[] values = Distinct(this.m_bodies[method].Returns.Where(returned => returned.ValueId.HasValue)
                .SelectMany(returned => ReadReturnedOrigins(new(method, returned.ValueId!.Value), buildingSummary, tracking))
                .Select(origin => new ValueOrigin(summary, origin.Value.Kind switch
                {
                    BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance => new(0, origin.Value.Kind, null,
                        origin.Value.ParameterIndex, Array.Empty<int>()),
                    BehaviorValueKind.Constant when origin.Value.Reference == "null" => new(0,
                        BehaviorValueKind.Constant, "null", null, Array.Empty<int>()),
                    BehaviorValueKind.NewObject or BehaviorValueKind.ShallowCopy => new(0, BehaviorValueKind.NewObject, null,
                        null, Array.Empty<int>()) { Type = origin.Value.Type == null ? null : origin.Value.Type with
                        {
                            Identity = origin.Value.Type.DefinitionIdentity,
                            ArgumentIdentities = Array.Empty<TypeIdentityTemplate>(),
                            ReferenceMetadataToken = 0,
                        }, AllocationConstraint = origin.Value.AllocationConstraint },
                    BehaviorValueKind.CallResult => new(0, BehaviorValueKind.CallResult,
                        origin.Value.Reference == ReflectionBaselineFailure ? ReflectionBaselineFailure : null, null, Array.Empty<int>()),
                    _ => new(0, BehaviorValueKind.Constant, "existing-return", null, Array.Empty<int>()),
                })));
            if (!this.m_returns.TryGetValue(key, out var previous) || !previous.Select(Key).ToHashSet().SetEquals(values.Select(Key)))
            {
                this.m_returns[key] = values;
                Notify(new("return", method));
            }
        }

        // 普通返回与 out 写回共用已读取的函数事实，不按函数名识别容器。
        private IEnumerable<BehaviorValueReference> ReadCallValues(string method, int? output)
        {
            MethodBehavior body = this.m_bodies[method];
            if (!output.HasValue)
            {
                return body.Returns.Where(value => value.ValueId.HasValue).Select(value => new BehaviorValueReference(method, value.ValueId!.Value));
            }
            return body.Writes.Where(write => write.Kind == BehaviorWriteKind.Indirect && write.ReceiverValueId.HasValue
                && ReadLocalOrigins(new(method, write.ReceiverValueId.Value)).Any(origin =>
                    origin.Value.Kind == BehaviorValueKind.Parameter && origin.Value.ParameterIndex == output))
                .Select(write => new BehaviorValueReference(method, write.ValueId));
        }

        // 仅沿已固定的调用传递返回类别和参数，不为返回判断重新展开外围调用。
        internal IEnumerable<ValueOrigin> ReadReturnedOrigins(BehaviorValueReference reference, bool buildingSummary = false, bool tracking = false)
        {
            Query query = new(reference.MethodId);
            Queue<BehaviorValueReference> pending = new(new[] { Normalize(reference) });
            HashSet<BehaviorValueReference> visited = new();
            while (pending.TryDequeue(out BehaviorValueReference current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }
                foreach (ValueOrigin origin in ReadLocalOrigins(current, query.Reader))
                {
                    if (origin.Value.Kind != BehaviorValueKind.CallResult
                        || !this.m_valueCalls.TryGetValue(origin.Reference, out BehaviorCall? call))
                    {
                        yield return origin;
                        continue;
                    }
                    Observe(CallKey(origin.Reference.MethodId, call.Point), query.Reader);
                    if (origin.Value.ParameterIndex.HasValue)
                    {
                        yield return origin;
                        continue;
                    }
                    if (tracking && call.Kind == BehaviorCallKind.Direct
                        && this.m_catalog.ResolveMethodDefinition(call.Target, true).Method.HasNoLogTrackExemption)
                    {
                        yield return origin with { Value = new(0, BehaviorValueKind.Constant, "trusted-return", null, Array.Empty<int>()) };
                        continue;
                    }
                    this.m_calls.TryGetValue((origin.Reference.MethodId, call.Point), out ResolvedCall? resolved);
                    // 反射短路的返回保留人工基线原因，不再索取其对象来源。
                    if (resolved?.Failure == ReflectionBaselineFailure)
                    {
                        yield return origin with { Value = origin.Value with { Reference = ReflectionBaselineFailure } };
                        continue;
                    }
                    if (resolved == null || resolved.Targets.Count == 0)
                    {
                        yield return origin;
                        continue;
                    }
                    if (resolved.Failure != null)
                    {
                        yield return origin;
                    }
                    foreach (ResolvedCallTarget target in resolved.Targets.Where(target => !target.IsCallback))
                    {
                        if (tracking && this.m_definitions[target.MethodId].HasNoLogTrackExemption)
                        {
                            yield return origin with { Value = new(0, BehaviorValueKind.Constant, "trusted-return", null, Array.Empty<int>()) };
                            continue;
                        }
                        Observe(new("return", target.MethodId), query.Reader);
                        AddValueUser(target.MethodId, query.Reader);
                        Observe(new("body", target.MethodId), query.Reader);
                        bool loaded = this.m_bodies.ContainsKey(target.MethodId);
                        var key = (target.MethodId, tracking);
                        if (this.m_returns.TryAdd(key, Array.Empty<ValueOrigin>()))
                        {
                            if (loaded)
                            {
                                this.m_changedReaders.Add(target.MethodId);
                            }
                        }
                        if (!loaded)
                        {
                            yield return origin;
                            continue;
                        }
                        ValueOrigin[] returned = this.m_returns[key];
                        if (this.m_bodies[target.MethodId].Failure != null
                            || returned.Length == 0 && (!buildingSummary
                                || this.m_bodies[target.MethodId].BodyKind != MethodBodyKind.Executable))
                        {
                            yield return origin;
                        }
                        foreach (ValueOrigin value in returned)
                        {
                            if (value.Value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance)
                            {
                                var inputs = value.Value.Kind == BehaviorValueKind.Parameter
                                    ? target.Arguments[value.Value.ParameterIndex!.Value] : target.Receiver;
                                foreach (BehaviorValueReference input in inputs)
                                {
                                    pending.Enqueue(Normalize(input));
                                }
                            }
                            else if (value.Value.Kind == BehaviorValueKind.CallResult)
                            {
                                yield return origin with { Value = origin.Value with { Reference = value.Value.Reference } };
                            }
                            else
                            {
                                var description = CallTargetResolver.ReadTargetDescription(target);
                                yield return value.Value.Type == null ? value : value with { Value = value.Value with
                                {
                                    Type = this.m_catalog.SubstituteType(value.Value.Type, description.TypeArguments, description.MethodArguments),
                                } };
                            }
                        }
                    }
                }
            }
        }

        // 写入判断只需要函数内对象身份，不为普通参数枚举所有调用者。
        internal IEnumerable<ValueOrigin> ReadLocalOrigins(BehaviorValueReference reference, string? readerMethod = null, bool observe = true)
        {
            // 多数值没有输入，队列和去重表在第一次出现输入时才建立。
            Queue<(BehaviorValueReference Reference, BehaviorTypeReference? Cast)>? pending = null;
            HashSet<(BehaviorValueReference Reference, BehaviorTypeReference? Cast)>? visited = null;
            (BehaviorValueReference Reference, BehaviorTypeReference? Cast) item = (Normalize(reference), null);
            while (true)
            {
                this.OriginReadCount++;
                if (observe)
                {
                    Observe(new("body", item.Reference.MethodId), readerMethod ?? reference.MethodId);
                }
                IReadOnlyList<ValueOrigin>? results = this.m_results.GetValueOrDefault(item.Reference);
                if (observe && this.m_valueCalls.TryGetValue(item.Reference, out BehaviorCall? call))
                {
                    Observe(CallKey(item.Reference.MethodId, call.Point), readerMethod ?? reference.MethodId);
                }
                for (int index = 0; index < (results?.Count ?? 1); index++)
                {
                    BehaviorValueReference source = results?[index].Reference ?? item.Reference;
                    BehaviorValue value = results?[index].Value ?? this.m_bodies[source.MethodId].Values[source.ValueId];
                    IEnumerable<int>? inputs = value.Kind == BehaviorValueKind.SlotRead ? ReadReachingValues(source, value)
                        : value.Kind is BehaviorValueKind.Merge or BehaviorValueKind.Conversion ? value.InputValueIds : null;
                    List<BehaviorValueReference>? captures = inputs == null && value.Kind == BehaviorValueKind.CapturedVariable
                        && value.SourceSymbol != null ? this.m_sourceSlots.GetValueOrDefault(value.SourceSymbol) : null;
                    if (inputs == null && captures == null)
                    {
                        ValueOrigin? converted = ConvertOrigin(results?[index] ?? new ValueOrigin(source, value), item.Cast);
                        if (converted != null)
                        {
                            yield return converted;
                        }
                        continue;
                    }
                    if (pending == null)
                    {
                        pending = new();
                        visited = new() { item };
                    }
                    if (inputs != null)
                    {
                        foreach (int input in inputs)
                        {
                            pending.Enqueue((source with { ValueId = input }, value.Kind == BehaviorValueKind.Conversion ? value.Type ?? item.Cast : item.Cast));
                        }
                        continue;
                    }
                    foreach (BehaviorValueReference capture in captures!)
                    {
                        if (capture != source)
                        {
                            pending.Enqueue((capture, item.Cast));
                        }
                    }
                }
                do
                {
                    if (pending == null || !pending.TryDequeue(out item))
                    {
                        yield break;
                    }
                }
                while (!visited!.Add(item));
            }
        }

        // 类型已明确的形参可直接用于接口范围查询。
        internal ValueOrigin? ReadFixedReceiver(BehaviorValueReference reference)
        {
            if (reference.ValueId < 0)
            {
                return null;
            }
            BehaviorValue value = this.m_bodies[reference.MethodId].Values[reference.ValueId];
            return value.Kind is BehaviorValueKind.CurrentInstance or BehaviorValueKind.Parameter && value.Type != null
                && !value.Type.Id.Contains('!') ? new(Normalize(reference), value) : null;
        }

        // 委托和反射需要具体目标时，才连接字段登记与调用实参。
        /// <summary>查找固定赋值、字段登记和实参中的候选；未知来源保留原值。</summary>
        public IReadOnlyList<ValueOrigin> GetOrigins(BehaviorValueReference reference, bool resolveRuntimeTypes = true)
        {
            using var timing = this.Timing.Measure(AnalysisTiming.Part.Origins);
            Query query = new(reference.MethodId) { RuntimeTypes = resolveRuntimeTypes };
            return Distinct(ReadTargets(Normalize(reference), query));
        }

        // 目标沿原始赋值和固定调用查表，每个节点只入队一次，不组合祖先调用环境。
        private IEnumerable<ValueOrigin> ReadTargets(BehaviorValueReference reference, Query query)
        {
            Queue<(BehaviorValueReference Reference, ResolvedCallTarget? Binding, bool Element)> pending = new();
            HashSet<(BehaviorValueReference Reference, ResolvedCallTarget? Binding, bool Element)> visited = new();
            void Enqueue(BehaviorValueReference value, ResolvedCallTarget? binding = null, bool element = false)
            {
                var item = (Normalize(value), binding, element);
                if (visited.Add(item))
                {
                    pending.Enqueue(item);
                }
            }
            Enqueue(reference);
            while (pending.TryDequeue(out var item))
            {
                this.m_cancellationToken.ThrowIfCancellationRequested();
                if (!RequireBody(item.Reference.MethodId, query))
                {
                    yield return Unknown(item.Reference, "函数体尚未读取");
                    continue;
                }
                foreach (ValueOrigin local in ReadLocalOrigins(item.Reference, query.Reader))
                {
                    ValueOrigin origin = local;
                    BehaviorValue value = origin.Value;
                    BehaviorValueReference slot = Normalize(origin.Reference);
                    if (value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance)
                    {
                        bool supplied = false;
                        if (item.Binding?.MethodId == slot.MethodId)
                        {
                            var inputs = value.Kind == BehaviorValueKind.Parameter
                                ? item.Binding.Arguments[value.ParameterIndex!.Value] : item.Binding.Receiver;
                            foreach (BehaviorValueReference input in inputs)
                            {
                                supplied = true;
                                Enqueue(input, element: item.Element);
                            }
                        }
                        else if (value.Kind == BehaviorValueKind.Parameter)
                        {
                            RequestIncoming(slot.MethodId, query);
                            foreach (var incoming in Incoming(slot.MethodId))
                            {
                                foreach (BehaviorValueReference input in incoming.Target.Arguments[value.ParameterIndex!.Value])
                                {
                                    supplied = true;
                                    Enqueue(input, element: item.Element);
                                }
                            }
                        }
                        if (!supplied)
                        {
                            yield return origin;
                        }
                        continue;
                    }
                    if (value.Kind == BehaviorValueKind.CallResult)
                    {
                        if (this.m_valueCalls.TryGetValue(slot, out BehaviorCall? call))
                        {
                            RequireCall(slot.MethodId, call, query);
                            if (this.m_calls.TryGetValue((slot.MethodId, call.Point), out ResolvedCall? resolved)
                                && resolved.Targets.Count != 0)
                            {
                                foreach (ResolvedCallTarget target in resolved.Targets.Where(target => !target.IsCallback))
                                {
                                    if (!RequireBody(target.MethodId, query))
                                    {
                                        yield return origin;
                                        continue;
                                    }
                                    MethodBehavior body = this.m_bodies[target.MethodId];
                                    if (body.BodyKind != MethodBodyKind.Executable)
                                    {
                                        yield return origin;
                                        continue;
                                    }
                                    BehaviorValueReference[] outputs = ReadCallValues(target.MethodId, value.ParameterIndex).ToArray();
                                    if (value.ParameterIndex.HasValue && outputs.Length == 0)
                                    {
                                        yield return origin;
                                    }
                                    foreach (BehaviorValueReference returned in outputs)
                                    {
                                        Enqueue(returned, target, item.Element);
                                    }
                                }
                                if (resolved.Failure != null)
                                {
                                    yield return origin;
                                }
                                continue;
                            }
                        }
                        yield return origin;
                        continue;
                    }
                    if (value.Kind == BehaviorValueKind.FieldRead && value.Member != null)
                    {
                        RequestMember(value.Member, query);
                        if (this.m_writes.TryGetValue(MemberKey(value.Member), out var writes))
                        {
                            foreach (var write in writes)
                            {
                                Enqueue(new(write.Method, write.Write.ValueId), element: item.Element);
                            }
                        }
                        else
                        {
                            yield return origin;
                        }
                        continue;
                    }
                    if (value.Kind == BehaviorValueKind.ArrayElementRead)
                    {
                        Enqueue(slot with { ValueId = value.InputValueIds[0] }, item.Binding, true);
                        continue;
                    }
                    if (item.Element && value.Kind == BehaviorValueKind.NewArray)
                    {
                        foreach (BehaviorWrite write in this.m_bodies[slot.MethodId].Writes.Where(write => write.Kind == BehaviorWriteKind.ArrayElement))
                        {
                            if (write.ReceiverValueId is int receiver && ReadLocalOrigins(slot with { ValueId = receiver })
                                .Any(array => array.Reference == origin.Reference))
                            {
                                Enqueue(slot with { ValueId = write.ValueId }, item.Binding);
                            }
                        }
                        continue;
                    }
                    if (item.Binding != null && value.Type != null)
                    {
                        var description = CallTargetResolver.ReadTargetDescription(item.Binding);
                        origin = origin with { Value = value with { Type = this.m_catalog.SubstituteType(value.Type,
                            description.TypeArguments, description.MethodArguments) } };
                    }
                    if (value.Kind == BehaviorValueKind.Type && value.Type == null && origin.BoundReceiver != null && query.RuntimeTypes)
                    {
                        foreach (BehaviorValueReference receiver in origin.BoundReceiver)
                        {
                            foreach (ValueOrigin instance in ReadLocalOrigins(receiver))
                            {
                                if (instance.Value.Type == null)
                                {
                                    yield return origin;
                                    continue;
                                }
                                TypeEntry definition = this.m_catalog.ResolveTypeDefinition(instance.Value.Type);
                                foreach (BehaviorTypeReference type in ReadRuntimeTypes(definition,
                                    this.m_catalog.ReadResolvedTypeArguments(instance.Value.Type)))
                                {
                                    yield return origin with { Value = origin.Value with { Type = type }, BoundReceiver = null };
                                }
                            }
                        }
                        continue;
                    }
                    yield return origin;
                }
            }
        }

        // 同一类型范围只计算一次；目录新增类型后重算，原有失败仍明确返回。
        private IReadOnlyList<BehaviorTypeReference> ReadRuntimeTypes(TypeEntry declared, IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            var key = (declared.Id, string.Join(',', arguments.Select(argument => argument.Text)));
            if (!this.m_runtimeTypes.TryGetValue(key, out var result) || result.Types != this.m_catalog.Types.Count)
            {
                try
                {
                    var values = CallTargetResolver.ReadRuntimeTypes(this.m_catalog, declared, arguments);
                    result = (this.m_catalog.Types.Count, values, null);
                }
                catch (AnalysisException exception)
                {
                    result = (this.m_catalog.Types.Count, null, exception.Message);
                }
                this.m_runtimeTypes[key] = result;
            }
            return result.Values ?? throw new AnalysisException(result.Failure!);
        }

        // 返回当前方法的固定来向调用及其类型实参。
        private IEnumerable<(ResolvedCallTarget Target, BehaviorValueReference CallSite)> Incoming(string method)
        {
            if (this.m_incoming.TryGetValue(method, out var incoming))
            {
                foreach (var entry in incoming.ToArray())
                {
                    ResolvedCall call = this.m_calls[entry.Key];
                    foreach (ResolvedCallTarget target in entry.Value)
                    {
                        yield return (target, Normalize(new(call.CallerMethodId, call.Call.ResultValueId ?? 0)));
                    }
                }
            }
        }

        // 泛型类型只沿现存构造调用替换，保留不同调用的实参。
        internal IEnumerable<TypeIdentityTemplate> ReadGenericTypes(string method, TypeIdentityTemplate identity,
            HashSet<(string Method, string Type)>? visited = null)
        {
            if (!identity.Text.Contains('!'))
            {
                yield return identity;
                yield break;
            }
            visited ??= new();
            if (!visited.Add((method, identity.Text)))
            {
                yield break;
            }
            Query query = new(method);
            RequestIncoming(method, query);
            foreach (var incoming in Incoming(method))
            {
                var description = CallTargetResolver.ReadTargetDescription(incoming.Target);
                TypeIdentityTemplate concrete = identity.Substitute(description.TypeArguments, description.MethodArguments);
                foreach (TypeIdentityTemplate value in ReadGenericTypes(incoming.CallSite.MethodId, concrete, visited))
                {
                    yield return value;
                }
            }
        }

        // 反射参数数组只读取指定下标，不配对所有数组写入。
        internal IReadOnlyList<IReadOnlyList<BehaviorValueReference>> ReadArrayArguments(BehaviorValueReference array, int count)
        {
            return Enumerable.Range(0, count).Select(index => (IReadOnlyList<BehaviorValueReference>)
                ReadArrayValues(array, index)
                    .Select(StoreOrigin).Distinct().ToArray()).ToArray();
        }

        // 数组元素只检查创建位置上的明确写入，不构造数组与调用路径的组合。
        private IEnumerable<ValueOrigin> ReadArrayValues(BehaviorValueReference array, int? index)
        {
            foreach (ValueOrigin origin in GetOrigins(array))
            {
                if (origin.Value.Member != null || origin.Value.Method != null || origin.Value.Property != null)
                {
                    yield return origin;
                    continue;
                }
                if (origin.Value.Kind != BehaviorValueKind.NewArray)
                {
                    yield return Unknown(array, "数组创建位置尚未确定");
                    continue;
                }
                bool found = false;
                foreach (BehaviorWrite write in this.m_bodies[origin.Reference.MethodId].Writes.Where(write => write.Kind == BehaviorWriteKind.ArrayElement))
                {
                    if (write.ReceiverValueId is not int receiver || !ReadLocalOrigins(origin.Reference with { ValueId = receiver })
                        .Any(candidate => candidate.Reference == origin.Reference))
                    {
                        continue;
                    }
                    if (index.HasValue && write.IndexValueIds.Count == 1)
                    {
                        ValueOrigin[] indices = ReadLocalOrigins(origin.Reference with { ValueId = write.IndexValueIds[0] }).ToArray();
                        if (indices.Length == 1 && int.TryParse(indices[0].Value.Reference, out int actual) && actual != index.Value)
                        {
                            continue;
                        }
                    }
                    found = true;
                    foreach (ValueOrigin value in GetOrigins(origin.Reference with { ValueId = write.ValueId }))
                    {
                        yield return value;
                    }
                }
                if (!found)
                {
                    yield return Unknown(array, "数组元素没有明确写入来源");
                }
            }
        }

        // 数组长度来自当前创建表达式，未知长度明确报错。
        internal int ReadArrayLength(BehaviorValueReference array)
        {
            ValueOrigin[] origins = GetOrigins(array).ToArray();
            if (origins.Length == 1 && origins[0].Value.Kind == BehaviorValueKind.Constant && origins[0].Value.Reference is "null" or "default")
            {
                return 0;
            }
            if (origins.Length == 1 && origins[0].Value.Kind == BehaviorValueKind.NewArray && origins[0].Value.InputValueIds.Count == 1)
            {
                ValueOrigin[] length = GetOrigins(origins[0].Reference with { ValueId = origins[0].Value.InputValueIds[0] }).ToArray();
                if (length.Length == 1 && int.TryParse(length[0].Value.Reference, out int result))
                {
                    return result;
                }
            }
            throw new AnalysisException("反射实参数组长度尚未确定");
        }

        // 只为语义操作生成的具体值保留引用，不创建字段映射代理。
        private BehaviorValueReference StoreOrigin(ValueOrigin origin)
        {
            OriginKey key = Key(origin);
            if (!this.m_stored.TryGetValue(key, out BehaviorValueReference reference))
            {
                reference = Normalize(origin.Reference) with { ValueId = this.m_nextStored-- };
                this.m_stored.Add(key, reference);
                this.m_results.Add(reference, new[] { origin });
            }
            return reference;
        }

        // 候选去重只使用实际身份和固定绑定，不包含调用路径。
        private static OriginKey Key(ValueOrigin origin) => new(origin.Reference, origin.Value.Kind, origin.Value.Reference ?? string.Empty,
            origin.Value.ParameterIndex, origin.Value.Type?.Id ?? string.Empty, origin.Value.Method?.Identity.ToString() ?? string.Empty,
            origin.Value.Member?.DeclaringTypeIdentity.Text ?? string.Empty, origin.Value.Member?.Name ?? string.Empty,
            string.Join(',', origin.BoundReceiver ?? Array.Empty<BehaviorValueReference>()),
            string.Join(';', origin.IteratorArguments?.Select(arguments => string.Join(',', arguments)) ?? Array.Empty<string>()),
            origin.Value.AllocationConstraint?.Id);

        // 直接比较原有身份字段，避免为每次去重拼接完整函数身份字符串。
        private readonly record struct OriginKey(BehaviorValueReference Reference, BehaviorValueKind Kind, string Value,
            int? Parameter, string Type, string Method, string Owner, string Member, string Receiver, string IteratorArguments,
            string? AllocationConstraint);

        // 同一个候选不在列表中重复保存。
        private static ValueOrigin[] Distinct(IEnumerable<ValueOrigin> values) => values.DistinctBy(Key).ToArray();

        // 不存在可靠来源时明确保留未知，不猜成 Getter。
        private static ValueOrigin Unknown(BehaviorValueReference reference, string reason) => new(reference,
            new(reference.ValueId, BehaviorValueKind.CallResult, reason, null, Array.Empty<int>()));

        /// <summary>查询只登记读取者和需要的信息，不保存调用路径。</summary>
        private sealed class Query(string reader)
        {
            internal string Reader { get; } = reader;
            internal bool RuntimeTypes { get; init; } = true;
        }

        // 委托构造不执行其业务目标。
        internal bool IsRuntimeDelegateCreation(ResolvedCall call, ResolvedCallTarget target) => call.Call.Kind == BehaviorCallKind.ObjectCreation
            && this.m_catalog.IsDelegateType(this.m_catalog.TypesById[this.m_definitions[target.MethodId].TypeId]);

        // 转换保持对象身份，只收紧已知接收类型；向父类转换不能抹掉原有具体类型。
        private ValueOrigin? ConvertOrigin(ValueOrigin origin, BehaviorTypeReference? target)
        {
            if (target == null || target.DefinitionIdentity.Text.StartsWith('!')
                || origin.Value.Kind is not (BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance
                    or BehaviorValueKind.FieldRead or BehaviorValueKind.CallResult or BehaviorValueKind.Local or BehaviorValueKind.ShallowCopy
                    or BehaviorValueKind.NewObject or BehaviorValueKind.Computation))
            {
                return origin;
            }
            BehaviorTypeReference? source = origin.Value.Type;
            BehaviorTypeReference narrowed = target;
            if (source != null && !source.DefinitionIdentity.Text.StartsWith('!'))
            {
                if (!this.m_convertedTypes.TryGetValue((source, target), out narrowed!))
                {
                    TypeEntry sourceType = this.m_catalog.ResolveTypeDefinition(source);
                    TypeEntry targetType = this.m_catalog.ResolveTypeDefinition(target);
                    if (origin.Value.Kind != BehaviorValueKind.Computation
                        && !source.Id.Contains('!') && !target.Id.Contains('!')
                        && !source.Id.EndsWith(']') && !target.Id.EndsWith(']') && !source.Id.EndsWith('*') && !target.Id.EndsWith('*')
                        && !sourceType.IsEnum && !targetType.IsEnum && sourceType.FullName != "System.Nullable`1"
                        && targetType.FullName != "System.Nullable`1" && (sourceType.IsSealed || sourceType.IsValueType)
                        && sourceType.Id != targetType.Id && !this.m_catalog.ReadInheritedTypes(sourceType, this.m_catalog.ReadResolvedTypeArguments(source))
                            .Any(parent => parent.Definition.Id == targetType.Id))
                    {
                        if (!targetType.IsValueType && !sourceType.IsValueType
                            && origin.Value.Kind is not (BehaviorValueKind.NewObject or BehaviorValueKind.CurrentInstance))
                        {
                            return new ValueOrigin(origin.Reference, new BehaviorValue(origin.Value.Id,
                                BehaviorValueKind.Constant, "null", null, Array.Empty<int>()) { Type = target });
                        }
                        return null;
                    }
                    bool sourceIsNarrower = sourceType.Id == targetType.Id
                        || this.m_catalog.ReadInheritedTypes(sourceType, this.m_catalog.ReadResolvedTypeArguments(source))
                            .Any(parent => parent.Definition.Id == targetType.Id);
                    narrowed = sourceIsNarrower ? source : target;
                    this.m_convertedTypes.Add((source, target), narrowed);
                }
            }
            return source == narrowed ? origin : origin with { Value = origin.Value with { Type = narrowed } };
        }

        // 为局部读取找最近赋值，循环回边保留为有限候选。
        private int[] ReadReachingValues(BehaviorValueReference reference, BehaviorValue value)
        {
            if (this.m_reaching.TryGetValue(reference, out int[]? known))
            {
                return known;
            }

            int slot = value.InputValueIds.Single();
            Stack<BehaviorFlowPoint> pending = new();
            pending.Push(value.Point!.Value);
            HashSet<BehaviorFlowPoint> visited = new();
            HashSet<int> result = new();
            while (pending.TryPop(out BehaviorFlowPoint point))
            {
                if (!visited.Add(point))
                {
                    continue;
                }

                BehaviorAssignment? assignment = null;
                foreach (BehaviorAssignment write in this.m_slotWrites.GetValueOrDefault((reference.MethodId, slot, point.BlockId)) ?? [])
                {
                    assignment = write.Point.Order < point.Order ? write : assignment;
                }
                if (assignment != null)
                {
                    result.Add(assignment.ValueId);
                    continue;
                }

                if (!this.m_predecessors[reference.MethodId].TryGetValue(point.BlockId, out List<int>? predecessors))
                {
                    result.Add(slot);
                    continue;
                }
                foreach (int predecessor in predecessors)
                {
                    pending.Push(new BehaviorFlowPoint(predecessor, int.MaxValue));
                }
            }

            known = result.Order().ToArray();
            this.m_reaching.Add(reference, known);
            return known;
        }


    }


    /// <summary>固定调用目标与原实参的对应关系。</summary>
    public sealed record ResolvedCallTarget(string MethodId, BehaviorMethodReference Reference,
        IReadOnlyList<BehaviorValueReference> Receiver, IReadOnlyList<IReadOnlyList<BehaviorValueReference>> Arguments,
        IReadOnlyList<string> DeclaringTypeArguments)
    {
        /// <summary>调用点的方法泛型实参，不与声明类型参数混用。</summary>
        public IReadOnlyList<string> MethodTypeArguments { get; init; } = Array.Empty<string>();

        /// <summary>委托最初绑定的声明，用于把实际重写的修改对应回同一委托来源。</summary>
        public string? DelegateDeclarationId { get; init; }

        /// <summary>外部库函数可能执行的业务回调：实参是库函数接收对象和实参所含的内容，返回值不是调用结果。</summary>
        public bool IsCallback { get; init; }
    }

    /// <summary>一个调用位置及其全部合法目标，不按上游入口重复保存。</summary>
    public sealed record ResolvedCall(string CallerMethodId, BehaviorCall Call, IReadOnlyList<ResolvedCallTarget> Targets)
    {
        /// <summary>本次调用使用的运行时基础操作说明及依据。</summary>
        public RuntimeOperationRule? RuntimeRule { get; init; }
        /// <summary>已知目标之外仍有未确定分支时保留原因，不丢弃已有目标。</summary>
        public string? Failure { get; init; }
        /// <summary>反射确定的存储写入，与普通成员写入使用同一事实格式。</summary>
        public IReadOnlyList<BehaviorWrite> Writes { get; init; } = Array.Empty<BehaviorWrite>();
        internal bool CompletesWithoutTarget { get; init; }
        internal bool IsMetadataBinding { get; init; }
        internal bool IsDelegateInvocation { get; init; }
        /// <summary>实际执行未固定目标的委托参数；合法输入允许传入修改状态的回调。</summary>
        internal bool InvokesUnboundParameter { get; init; }
        internal bool ValuesOnly { get; init; }
    }

    /// <summary>没有确定目标的调用位置及具体原因。</summary>
    public sealed record PendingCall(string CallerMethodId, BehaviorCall Call)
    {
        /// <summary>未确定目标的具体原因。</summary>
        public string? Failure { get; init; }
    }

    /// <summary>一轮调用分析的函数、事实、固定关系和耗时。</summary>
    public sealed record CallTargetResolutionResult(IReadOnlyList<MethodEntry> Methods, BehaviorReadResult Behaviors,
        IReadOnlyList<ResolvedCall> Calls, ValueSourceIndex ValueSources, TimeSpan Elapsed)
    {
        internal EffectAnalyzer.TopTracker Top { get; init; } = null!;
        /// <summary>整轮连接中断的原因；已有 Setter 证据保留，其余行为不能据此下结论。</summary>
        public string? Failure { get; init; }
        /// <summary>尚未闭合的固定调用位置。</summary>
        public IReadOnlyList<PendingCall> PendingCalls { get; init; } = Array.Empty<PendingCall>();
    }
}
