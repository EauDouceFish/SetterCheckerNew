using System.Diagnostics;

namespace SetterChecker.Core
{
    /// <summary>
    /// 从指定根函数出发，补读实际调用的函数并保存逐调用点绑定。
    /// </summary>
    public sealed class CallTargetResolver
    {
        // 逐批读取新命中的函数，直到全部已支持的调用都接上真实声明。
        /// <summary>
        /// 找齐调用或取得行为证明；previous 必须来自相同材料和根，恢复时沿用并更新原值来源。
        /// </summary>
        public async Task<CallTargetResolutionResult> ResolveAsync(
            MaterialSet material,
            MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> roots,
            int jobs,
            CancellationToken cancellationToken = default,
            bool requireCompleteCalls = true,
            CallTargetResolutionResult? previous = null, Action<string>? progress = null,
            Action<CallTargetResolutionResult, EffectAnalysisResult>? reportProgress = null)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Dictionary<string, MethodEntry> methods = previous?.ValueSources.Definitions ?? roots.ToDictionary(method => method.Id);
            Dictionary<string, MethodBehavior> behaviors = previous?.ValueSources.Behaviors
                ?? new(StringComparer.Ordinal);
            List<ResolvedCall> calls = previous?.Calls.ToList() ?? new();
            ValueSourceIndex sources = previous?.ValueSources ?? new(catalog, methods, behaviors);
            TimeSpan initialReadingTime = previous?.Behaviors.Elapsed ?? TimeSpan.Zero;
            TimeSpan readingTime = initialReadingTime;
            IReadOnlyList<MethodCallInstance> pending = previous != null ? Array.Empty<MethodCallInstance>() : roots.Select(method => sources.GetInstance(
                method.Id,
                Enumerable.Range(0, catalog.TypesById[method.TypeId].GenericParameters.Count)
                    .Select(index => TypeIdentityTemplate.ScopedParameter(method.AssemblyPath + "|" + method.TypeId, method.Id, index).Text).ToArray(),
                Enumerable.Range(0, method.GenericArity).Select(index => TypeIdentityTemplate.ScopedParameter(
                    method.AssemblyPath + "|" + method.Id, method.Id, index, methodParameter: true).Text).ToArray())).ToArray();
            HashSet<int> discovered = sources.Instances.Select(instance => instance.Id).ToHashSet();
            List<(MethodBehavior Behavior, BehaviorCall Call, MethodCallInstance Instance)> waiting = previous?.PendingCalls
                .Select(call => (behaviors[call.CallerMethodId], call.Call, sources.GetInstance(call.CallerInstanceId))).ToList() ?? new();
            List<PendingCall> deferred = new();
            Dictionary<int, EffectEvidence> provenSetters = new();
            Dictionary<(int InstanceId, int Position), string> failures = previous?.PendingCalls.Where(call => call.Failure != null)
                .ToDictionary(call => (call.CallerInstanceId, call.Call.Position), call => call.Failure!) ?? new();
            BehaviorReader reader = new();

            // 发布同一份调用和值来源快照，尚未解析的调用独立保存。
            CallTargetResolutionResult ReadResult()
            {
                HashSet<string> activeMethods = sources.Instances.Select(instance => instance.MethodId).ToHashSet(StringComparer.Ordinal);
                return new CallTargetResolutionResult(methods.Values.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray(),
                    new BehaviorReadResult(behaviors.Values.Where(body => activeMethods.Contains(body.MethodId)).OrderBy(behavior => behavior.MethodId, StringComparer.Ordinal).ToArray(), readingTime),
                    calls.Where(call => sources.IsReachable(call.CallerInstanceId, call.Call.Point.BlockId)).OrderBy(call => call.CallerMethodId, StringComparer.Ordinal).ThenBy(call => call.Call.Point.BlockId)
                        .ThenBy(call => call.Call.Point.Order).ThenBy(call => call.CallerInstanceId).ToArray(), sources,
                    (previous?.Elapsed ?? TimeSpan.Zero) + stopwatch.Elapsed - readingTime + initialReadingTime)
                {
                    PendingCalls = deferred.Concat(waiting.Select(item => new PendingCall(item.Behavior.MethodId, item.Instance.Id, item.Call)
                    { Failure = failures.GetValueOrDefault((item.Instance.Id, item.Call.Position)) }))
                        .OrderBy(call => call.CallerMethodId, StringComparer.Ordinal).ThenBy(call => call.Call.Point.BlockId)
                        .ThenBy(call => call.Call.Point.Order).ThenBy(call => call.CallerInstanceId).ToArray(),
                };
            }

            while (true)
            {
                if (pending.Count != 0)
                {
                    MethodEntry[] unread = pending.Select(instance => methods[instance.MethodId])
                        .DistinctBy(method => method.Id).Where(method => !behaviors.ContainsKey(method.Id)).ToArray();
                    BehaviorReadResult batch = await (requireCompleteCalls
                        ? reader.ReadAsync(material, catalog, unread, jobs, cancellationToken)
                        : reader.ReadAvailableAsync(material, catalog, unread, jobs, cancellationToken)).ConfigureAwait(false);
                    foreach (MethodBehavior body in batch.Methods)
                    {
                        behaviors.Add(body.MethodId, body);
                    }
                    readingTime += batch.Elapsed;
                    foreach (MethodCallInstance instance in pending)
                    {
                        if (!instance.TypeArguments.Concat(instance.MethodArguments).Any(argument => argument.HasUnspecifiedParameter))
                        {
                            continue;
                        }
                        if (instance.ParentId != 0
                            && !SatisfiesGenericConstraints(catalog, catalog.TypesById[methods[instance.MethodId].TypeId], instance.TypeArguments, sources))
                        {
                            throw new AnalysisException($"函数所在类型的实参不符合声明约束：{instance.MethodId}");
                        }
                    }
                    waiting.AddRange(pending.SelectMany(instance => behaviors[instance.MethodId].Calls
                        .Select(call => (behaviors[instance.MethodId], call, instance))));
                }

                HashSet<int> changedRoots = sources.RefineReachability(provenSetters.Keys.ToHashSet());
                if (changedRoots.Count != 0)
                {
                    HashSet<int> invalidated = sources.ResetRoots(changedRoots);
                    waiting.RemoveAll(item => invalidated.Contains(item.Instance.Id));
                    deferred.RemoveAll(item => invalidated.Contains(item.CallerInstanceId));
                    calls.RemoveAll(call => invalidated.Contains(call.CallerInstanceId));
                    foreach (var key in failures.Keys.Where(key => invalidated.Contains(key.InstanceId)).ToArray())
                    {
                        failures.Remove(key);
                    }
                    discovered.ExceptWith(invalidated);
                    pending = sources.RootInstances.Values.Where(instance => changedRoots.Contains(instance.Id)).ToArray();
                    discovered.UnionWith(pending.Select(instance => instance.Id));
                    continue;
                }
                waiting.RemoveAll(item => !sources.IsReachable(item.Instance.Id, item.Call.Point.BlockId));
                deferred.RemoveAll(item => !sources.IsReachable(item.CallerInstanceId, item.Call.Point.BlockId));
                progress?.Invoke($"已读取 {behaviors.Count} 个函数体，已连接 {calls.Count} 个调用，待处理 {waiting.Count} 个调用。");
                if (waiting.Count == 0)
                {
                    break;
                }
                if (!requireCompleteCalls && previous == null && waiting.Count != 0)
                {
                    CallTargetResolutionResult current = ReadResult();
                    EffectAnalysisResult proofs = new EffectAnalyzer().AnalyzeAvailable(catalog, roots, current, false, cancellationToken, provenSetters);
                    reportProgress?.Invoke(current, proofs);
                    foreach (MethodEffect method in proofs.Methods.Where(method => method.Kind == MethodEffectKind.Setter))
                    {
                        provenSetters.TryAdd(sources.RootInstances[method.MethodId].Id, method.Evidence!);
                    }
                    progress?.Invoke($"已有 {provenSetters.Count} 个入口取得确定的修改证据；总入口 {roots.Count} 个。");
                    waiting.RemoveAll(item =>
                    {
                        if (!provenSetters.ContainsKey(item.Instance.RootId))
                        {
                            return false;
                        }
                        deferred.Add(new PendingCall(item.Behavior.MethodId, item.Instance.Id, item.Call)
                        { Failure = failures.GetValueOrDefault((item.Instance.Id, item.Call.Position)) });
                        return true;
                    });
                }

                List<MethodCallInstance> next = new();
                HashSet<(int InstanceId, int Position)> resolvedCalls = new();
                bool? deferDynamic = null;
                foreach (var item in waiting.OrderBy(item => item.Call.Kind is BehaviorCallKind.Virtual
                             or BehaviorCallKind.Delegate ? 1 : 0).ToArray())
                {
                    MethodBehavior behavior = item.Behavior;
                    BehaviorCall call = item.Call;
                    MethodCallInstance instance = item.Instance;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (call.Kind is BehaviorCallKind.Virtual or BehaviorCallKind.Delegate && (deferDynamic ??= next.Count != 0))
                    {
                        continue;
                    }
                    IReadOnlyList<(ResolvedMethodDefinition Definition, ResolvedCallTarget Binding)>? targets;
                    bool coversDeclaredReceivers = false;
                    bool completesWithoutTarget = false;
                    List<MethodCallInstance?> recursiveInstances = new();
                    try
                    {
                        ResolvedMethodDefinition definition = catalog.ResolveMethodDefinition(
                            call.Target, call.Kind != BehaviorCallKind.ObjectCreation);
                        definition = definition with
                        {
                            DeclaringTypeArguments = definition.DeclaringTypeArguments.Select(instance.Substitute).ToArray(),
                        };
                        call = call with
                        {
                            Target = BindReference(catalog, call.Target, definition, instance),
                            ConstrainedReceiverType = call.ConstrainedReceiverType == null
                                ? null : instance.Substitute(call.ConstrainedReceiverType),
                        };
                        if (call.Kind == BehaviorCallKind.FunctionPointer)
                        {
                            throw new AnalysisException(
                                $"调用目标尚未闭合：{behavior.MethodId}，位置 {call.Position}，"
                                + $"调用形式 {call.Kind}，目标 {definition.Method.Id}");
                        }

                        IReadOnlyList<BehaviorValueReference> receiver = call.ReceiverValueId is int valueId
                            ? new[] { new BehaviorValueReference(behavior.MethodId, valueId, InstanceId: instance.Id) }
                            : Array.Empty<BehaviorValueReference>();
                        if (call.Kind == BehaviorCallKind.ObjectCreation)
                        {
                            receiver = new[]
                            {
                                new BehaviorValueReference(behavior.MethodId, call.ResultValueId!.Value, InstanceId: instance.Id),
                            };
                        }

                        IReadOnlyList<BehaviorValueReference>[] arguments = call.Arguments.Select(argument =>
                                (IReadOnlyList<BehaviorValueReference>)new[]
                                {
                                    new BehaviorValueReference(behavior.MethodId, argument.ValueId, InstanceId: instance.Id),
                                }).ToArray();
                        bool runtimeOperation = TryResolveReflectionCall(catalog, sources, behavior, instance, call,
                            definition, receiver, arguments, out targets, out completesWithoutTarget);
                        if (runtimeOperation)
                        {
                            // 运行时查找产生值事实，反射执行仍产生普通目标和实参绑定。
                        }
                        else if (call.Kind == BehaviorCallKind.Delegate)
                        {
                            targets = TryResolveDelegateTargets(catalog, sources, receiver.Single(), arguments);
                        }
                        else
                        {
                            IReadOnlyList<ResolvedMethodDefinition> declarations;
                            if (call.Kind == BehaviorCallKind.Virtual && definition.Method.IsVirtual && !definition.Method.IsFinal)
                            {
                                IReadOnlyList<ValueOrigin> origins = (call.ConstrainedReceiverType == null ? receiver
                                    : receiver.SelectMany(value => sources.ReadAddressedValues(value, call.Point)).ToArray())
                                    .SelectMany(value => sources.GetCallOrigins(value, retainTypeChecks: true)).ToArray();
                                if (origins.Any(origin => origin.Value.Kind == BehaviorValueKind.CallResult)
                                    || sources.GetCallOrigins(receiver.Single()).Any(origin => origin.Value.Kind == BehaviorValueKind.CallResult))
                                {
                                    continue;
                                }

                                try
                                {
                                    declarations = ResolveVirtualDefinitions(catalog, definition, origins, sources,
                                        out coversDeclaredReceivers, call.ConstrainedReceiverType);
                                }
                                catch (AnalysisException exception)
                                {
                                    throw new AnalysisException($"{behavior.MethodId} @ {call.Position} => {definition.Method.Id}：{exception.Message}");
                                }
                            }
                            else
                            {
                                declarations = new[] { definition };
                            }
                            targets = declarations.Select(target => (target,
                                new ResolvedCallTarget(target.Method.Id, call.Target,
                                    call.ConstrainedReceiverType != null && !catalog.TypesById[target.Method.TypeId].IsValueType
                                        ? receiver.SelectMany(value => sources.ReadAddressedValues(value, call.Point)).ToArray() : receiver, arguments,
                                    target.DeclaringTypeArguments.Select(type => type.Text).ToArray()))).ToArray();
                        }

                        if (targets == null)
                        {
                            continue;
                        }
                        foreach (var target in targets)
                        {
                            if (!sources.TryFindRecursiveInstance(target.Binding, instance.Id, call.Point, out MethodCallInstance? recursive))
                            {
                                break;
                            }
                            recursiveInstances.Add(recursive);
                        }
                    }
                    catch (AnalysisException exception) when (!requireCompleteCalls)
                    {
                        failures[(instance.Id, item.Call.Position)] = exception.Message;
                        continue;
                    }

                    if (recursiveInstances.Count != targets.Count)
                    {
                        continue;
                    }
                    List<ResolvedCallTarget> bindings = new();
                    foreach (var (target, recursive) in targets.Zip(recursiveInstances))
                    {
                        methods.TryAdd(target.Definition.Method.Id, target.Definition.Method);
                        MethodCallInstance targetInstance = recursive ?? sources.GetInstance(target.Definition.Method.Id,
                            target.Binding.DeclaringTypeArguments, target.Binding.Reference.GenericArgumentTypeIds,
                            instance.Id, target.Binding, call.Point);
                        bindings.Add(target.Binding with { InstanceId = targetInstance.Id });
                        if (discovered.Add(targetInstance.Id))
                        {
                            next.Add(targetInstance);
                        }
                    }
                    ResolvedCall resolved = new(behavior.MethodId, call, bindings)
                    { CallerInstanceId = instance.Id, CoversDeclaredReceivers = coversDeclaredReceivers, CompletesWithoutTarget = completesWithoutTarget };
                    calls.Add(resolved);
                    sources.BindCall(resolved);
                    resolvedCalls.Add((instance.Id, item.Call.Position));
                    failures.Remove((instance.Id, item.Call.Position));
                }

                waiting.RemoveAll(item => resolvedCalls.Contains((item.Instance.Id, item.Call.Position)));
                if (next.Count == 0 && resolvedCalls.Count == 0 && waiting.Count != 0)
                {
                    if (!requireCompleteCalls)
                    {
                        break;
                    }
                    string? failure = waiting.Select(item => failures.GetValueOrDefault((item.Instance.Id, item.Call.Position)))
                        .FirstOrDefault(message => message != null);
                    if (failure != null)
                    {
                        throw new AnalysisException(failure);
                    }
                    throw new AnalysisException("调用目标来源尚未闭合：" + string.Join(Environment.NewLine,
                        waiting.Select(item => $"{item.Behavior.MethodId} @ {item.Call.Position} => {item.Call.Target.Identity.Text}"
                            + (item.Call.ReceiverValueId is int receiverId ? "；接收对象来源：" + string.Join("; ",
                                sources.GetCallOrigins(new BehaviorValueReference(item.Behavior.MethodId, receiverId,
                                    InstanceId: item.Instance.Id)).Select(origin => $"{origin.Value.Kind}:{origin.Value.Reference}")) : string.Empty))));
                }

                pending = next;
            }

            stopwatch.Stop();
            return ReadResult();
        }

        // 把运行时查得的类型和函数还原为共同值事实，执行时连接普通调用目标。
        private static bool TryResolveReflectionCall(
            MethodCatalogResult catalog,
            ValueSourceIndex sources,
            MethodBehavior body, MethodCallInstance instance, BehaviorCall call, ResolvedMethodDefinition declaration,
            IReadOnlyList<BehaviorValueReference> receiver, IReadOnlyList<IReadOnlyList<BehaviorValueReference>> arguments,
            out IReadOnlyList<(ResolvedMethodDefinition Definition, ResolvedCallTarget Binding)>? targets,
            out bool completesWithoutTarget)
        {
            targets = null;
            completesWithoutTarget = false;
            TypeEntry owner = catalog.TypesById[declaration.Method.TypeId];
            if (owner.FullName is not ("System.Type" or "System.Reflection.MethodBase" or "System.Reflection.MethodInfo"
                or "System.Reflection.FieldInfo" or "System.Reflection.PropertyInfo")
                || owner.AssemblyPath != catalog.ReadPrimitiveType("System.Object").AssemblyPath)
            {
                return false;
            }
            BehaviorValueReference resultReference = new(body.MethodId, call.ResultValueId ?? -1, instance.Id);
            if (owner.FullName == "System.Type" && declaration.Method.Name == "GetTypeFromHandle" && arguments.Count == 1)
            {
                IReadOnlyList<ValueOrigin> values = sources.GetCallOrigins(arguments[0].Single());
                if (values.Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return true;
                }
                if (values.Count == 0 || values.Any(value => value.Value.Kind != BehaviorValueKind.Type))
                {
                    throw new AnalysisException($"反射类型句柄来源尚未闭合：{body.MethodId} @ {call.Position}");
                }
                sources.BindRuntimeValue(resultReference, values);
                targets = Array.Empty<(ResolvedMethodDefinition, ResolvedCallTarget)>();
                completesWithoutTarget = true;
                return true;
            }
            if (owner.FullName == "System.Type" && declaration.Method.Name is "GetMethod" or "GetField" or "GetProperty"
                && (arguments.Count == 1 || declaration.Method.Name == "GetField" && arguments.Count == 2))
            {
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
                if (arguments.Count == 2)
                {
                    IReadOnlyList<ValueOrigin> options = sources.GetCallOrigins(arguments[1].Single());
                    if (options.Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                    {
                        return true;
                    }
                    if (options.Count != 1 || options[0].Value is not { Kind: BehaviorValueKind.Constant, Type.Id: "System.Int32" })
                    {
                        throw new AnalysisException($"反射字段查找选项尚未闭合：{body.MethodId} @ {call.Position}");
                    }
                    flags = (System.Reflection.BindingFlags)int.Parse(options[0].Value.Reference!, System.Globalization.CultureInfo.InvariantCulture);
                }
                IReadOnlyList<ValueOrigin> types = sources.GetCallOrigins(receiver.Single());
                IReadOnlyList<ValueOrigin> names = sources.GetCallOrigins(arguments[0].Single());
                if (types.Concat(names).Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return true;
                }
                if (types.Count == 0 || names.Count == 0 || types.Any(value => value.Value.Kind != BehaviorValueKind.Type)
                    || names.Any(value => value.Value.Kind != BehaviorValueKind.Constant || value.Value.Reference == null))
                {
                    throw new AnalysisException($"反射类型或函数名来源尚未闭合：{body.MethodId} @ {call.Position}");
                }
                if (types.Select(value => value.Value.Type).Distinct().Count() > 1
                    && names.Select(value => value.Value.Reference).Distinct().Count() > 1)
                {
                    throw new AnalysisException($"反射分支关联尚未闭合：{body.MethodId} @ {call.Position}");
                }
                List<ValueOrigin> members = new();
                foreach (ValueOrigin type in types)
                {
                    TypeEntry reflectedType = catalog.ResolveTypeDefinition(type.Value.Type!);
                    catalog.RequireClosedHierarchy(reflectedType);
                    if (reflectedType.GenericParameters.Count != 0)
                    {
                        throw new AnalysisException($"反射构造类型实参尚未闭合：{reflectedType.Id}");
                    }
                    foreach (ValueOrigin name in names)
                    {
                        if (declaration.Method.Name == "GetProperty")
                        {
                            var properties = catalog.ReadInheritedTypes(reflectedType, includeInterfaces: false)
                                .Prepend(new MethodCatalogResult.InheritedTypeRelation(reflectedType, Array.Empty<TypeIdentityTemplate>(), false, false, 0))
                                .OrderBy(parent => parent.Depth).SelectMany(parent => catalog.GetProperties(parent.Definition)
                                    .Where(property => property.Name == name.Value.Reference
                                        && (property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true)
                                        && (parent.Definition.Id == reflectedType.Id || (property.GetMethod ?? property.SetMethod).IsStatic == false))
                                    .Select(property => (Property: property, Owner: parent.Definition,
                                        Signature: ReadPropertySignature(catalog, property, parent))))
                                .DistinctBy(property => property.Signature).ToArray();
                            if (properties.Length != 1 || properties[0].Property.HasParameters)
                            {
                                throw new AnalysisException($"反射属性查找或索引实参尚未闭合：{reflectedType.FullName}.{name.Value.Reference}");
                            }
                            var property = properties[0];
                            members.Add(new ValueOrigin(resultReference, body.Values[resultReference.ValueId] with
                            {
                                Kind = BehaviorValueKind.Function,
                                Property = new BehaviorPropertyReference(property.Property.GetMethod == null ? null
                                        : catalog.ReadManagedMethodReference(property.Property.GetMethod, property.Owner.AssemblyPath!),
                                    property.Property.SetMethod == null ? null
                                        : catalog.ReadManagedMethodReference(property.Property.SetMethod, property.Owner.AssemblyPath!)),
                            }));
                            continue;
                        }
                        if (declaration.Method.Name == "GetField")
                        {
                            BehaviorMemberReference[] fields = catalog.ReadInheritedTypes(reflectedType, includeInterfaces: false)
                                .Where(parent => !flags.HasFlag(System.Reflection.BindingFlags.DeclaredOnly))
                                .Prepend(new MethodCatalogResult.InheritedTypeRelation(reflectedType, Array.Empty<TypeIdentityTemplate>(), false, false, 0))
                                .OrderBy(parent => parent.Depth).SelectMany(parent => catalog.GetFields(parent.Definition)
                                    .Where(field => flags.HasFlag(field.IsPublic ? System.Reflection.BindingFlags.Public : System.Reflection.BindingFlags.NonPublic)
                                        && flags.HasFlag(field.IsStatic ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance)
                                        && (parent.Depth == 0 || !field.IsPrivate && (!field.IsStatic || flags.HasFlag(System.Reflection.BindingFlags.FlattenHierarchy)))
                                        && string.Equals(field.Name, name.Value.Reference, flags.HasFlag(System.Reflection.BindingFlags.IgnoreCase)
                                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                                    .Select(field => (Depth: parent.Depth, Reference: catalog.ReadManagedFieldReference(field, parent.Definition.AssemblyPath!))))
                                .GroupBy(field => field.Depth).FirstOrDefault()?.Select(field => field.Reference).ToArray() ?? Array.Empty<BehaviorMemberReference>();
                            if (fields.Length == 0)
                            {
                                if (!flags.HasFlag(System.Reflection.BindingFlags.DeclaredOnly)
                                    && flags.HasFlag(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)
                                    && catalog.ReadInheritedTypes(reflectedType).Where(parent => parent.IsInterface)
                                        .SelectMany(parent => catalog.GetFields(parent.Definition)).Any(field => !field.IsPrivate && field.IsStatic
                                            && flags.HasFlag(field.IsPublic ? System.Reflection.BindingFlags.Public : System.Reflection.BindingFlags.NonPublic)
                                            && string.Equals(field.Name, name.Value.Reference, flags.HasFlag(System.Reflection.BindingFlags.IgnoreCase)
                                                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                                {
                                    throw new AnalysisException($"继承接口字段的运行库查找规则尚未闭合：{reflectedType.FullName}.{name.Value.Reference}");
                                }
                                members.Add(new ValueOrigin(resultReference, body.Values[resultReference.ValueId] with
                                { Kind = BehaviorValueKind.Constant, Reference = null, Type = null }));
                                continue;
                            }
                            if (fields.Length > 1)
                            {
                                throw new AnalysisException($"反射字段查找没有唯一结果：{reflectedType.FullName}.{name.Value.Reference}");
                            }
                            members.Add(new ValueOrigin(resultReference, body.Values[resultReference.ValueId] with
                            { Kind = BehaviorValueKind.Function, Member = fields[0] }));
                            continue;
                        }
                        MethodEntry[] matches = catalog.GetMethods(reflectedType)
                            .Concat(catalog.ReadInheritedTypes(reflectedType).Where(parent => !parent.IsInterface)
                                .SelectMany(parent => catalog.GetMethods(parent.Definition).Where(method => !method.IsStatic)))
                            .Where(method => method.Kind is not (CatalogMethodKind.Constructor or CatalogMethodKind.StaticConstructor)
                                && method.IsPublic && method.Name == name.Value.Reference).ToArray();
                        if (matches.Length != 1)
                        {
                            throw new AnalysisException($"反射函数查找没有唯一结果：{reflectedType.FullName}.{name.Value.Reference}");
                        }
                        members.Add(new ValueOrigin(resultReference, body.Values[resultReference.ValueId] with
                        {
                            Kind = BehaviorValueKind.Function,
                            Method = catalog.ReadMethodReference(matches[0]),
                        }));
                    }
                }
                sources.BindRuntimeValue(resultReference, members);
                targets = Array.Empty<(ResolvedMethodDefinition, ResolvedCallTarget)>();
                completesWithoutTarget = true;
                return true;
            }
            if (owner.FullName == "System.Reflection.FieldInfo"
                && (declaration.Method.Name == "GetValue" && arguments.Count == 1
                    || declaration.Method.Name == "SetValue" && arguments.Count == 2))
            {
                IReadOnlyList<ValueOrigin> members = sources.GetCallOrigins(receiver.Single());
                IReadOnlyList<ValueOrigin> objects = sources.GetCallOrigins(arguments[0].Single(), true);
                IReadOnlyList<ValueOrigin> values = arguments.Count == 2
                    ? sources.GetCallOrigins(arguments[1].Single(), true) : Array.Empty<ValueOrigin>();
                if (members.Concat(objects).Concat(values).Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return true;
                }
                if (members.Count == 0 || members.Any(value => value.Value.Member?.KnownDeclaringTypeId == null))
                {
                    throw new AnalysisException($"反射字段来源尚未闭合：{body.MethodId} @ {call.Position}");
                }
                List<ValueOrigin> reads = new();
                List<BehaviorWrite> writes = new();
                int completedMembers = 0;
                foreach (ValueOrigin member in members)
                {
                    BehaviorMemberReference reference = member.Value.Member!;
                    TypeEntry fieldOwner = catalog.TypesById[reference.KnownDeclaringTypeId!];
                    var field = catalog.GetFields(fieldOwner).Single(field => field.MetadataToken.ToInt32() == reference.ReferenceMetadataToken);
                    if (!field.IsStatic)
                    {
                        if (objects.Count > 0 && objects.All(value => value.Value.Kind == BehaviorValueKind.Constant && value.Value.Reference == null))
                        {
                            continue;
                        }
                        if (objects.Any(value => value.Value.Kind == BehaviorValueKind.Constant && value.Value.Reference == null))
                        {
                            throw new AnalysisException($"反射字段对象的成功与异常分支尚未闭合：{body.MethodId} @ {call.Position}");
                        }
                        IReadOnlyList<StaticReceiverType> objectTypes = ReadStaticReceiverTypes(catalog, objects, sources);
                        if (objectTypes.Count == 0 || objectTypes.Any(type => type.Definition.Id != fieldOwner.Id
                            && !catalog.ReadInheritedTypes(type.Definition, type.Arguments).Any(parent => parent.Definition.Id == fieldOwner.Id))
                            || members.Count > 1 && objects.Count > 1)
                        {
                            throw new AnalysisException($"反射字段对象关联尚未闭合：{body.MethodId} @ {call.Position}");
                        }
                    }
                    if (arguments.Count == 2)
                    {
                        TypeIdentityTemplate fieldType = catalog.ReadResolvedFieldType(reference);
                        if (values.Any(value => value.Value.Type is BehaviorTypeReference type && !IsAssignable(catalog, type.Identity.Text, fieldType.Text)))
                        {
                            throw new AnalysisException($"反射字段赋值转换尚未闭合：{field.FullName}");
                        }
                        if (field.IsLiteral || field.IsInitOnly)
                        {
                            throw new AnalysisException($"反射常量或只读字段写入尚未闭合：{field.FullName}");
                        }
                        writes.Add(new BehaviorWrite(BehaviorWriteKind.Field,
                            field.IsStatic ? null : call.Arguments[0].ValueId, reference, Array.Empty<int>(),
                            call.Arguments[1].ValueId, call.Position)
                        { Point = call.Point });
                    }
                    else
                    {
                        reads.Add(new ValueOrigin(resultReference, body.Values[resultReference.ValueId] with
                        {
                            Kind = BehaviorValueKind.FieldRead,
                            Member = reference,
                            InputValueIds = field.IsStatic
                            ? Array.Empty<int>() : new[] { call.Arguments[0].ValueId }
                        }));
                    }
                    completedMembers++;
                }
                if (completedMembers != 0 && completedMembers != members.Count)
                {
                    throw new AnalysisException($"反射字段对象的成功与异常分支尚未闭合：{body.MethodId} @ {call.Position}");
                }
                foreach (BehaviorWrite write in writes)
                {
                    sources.AddRuntimeWrite(instance.Id, write);
                }
                if (arguments.Count == 1)
                {
                    sources.BindRuntimeValue(resultReference, reads);
                }
                targets = Array.Empty<(ResolvedMethodDefinition, ResolvedCallTarget)>();
                completesWithoutTarget = completedMembers > 0;
                return true;
            }
            bool propertyCall = owner.FullName == "System.Reflection.PropertyInfo"
                && (declaration.Method.Name == "GetValue" && arguments.Count is 1 or 2
                    || declaration.Method.Name == "SetValue" && arguments.Count is 2 or 3);
            if (propertyCall || owner.FullName is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo"
                && declaration.Method.Name == "Invoke" && arguments.Count == 2)
            {
                IReadOnlyList<ValueOrigin> members = sources.GetCallOrigins(receiver.Single());
                if (members.Any(member => member.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return true;
                }
                if (propertyCall)
                {
                    members = members.Select(member => member with
                    {
                        Value = member.Value with
                        { Method = declaration.Method.Name == "GetValue" ? member.Value.Property?.Getter : member.Value.Property?.Setter }
                    }).ToArray();
                }
                if (members.Count == 0 || members.Any(member => member.Value.Kind != BehaviorValueKind.Function || member.Value.Method == null))
                {
                    throw new AnalysisException($"反射调用的函数来源尚未闭合：{body.MethodId} @ {call.Position}");
                }
                IReadOnlyList<ValueOrigin> objects = sources.GetCallOrigins(arguments[0].Single(), true);
                if (members.Any(member => member.Value.Method!.HasInstance)
                    && objects.Concat(sources.GetCallOrigins(arguments[0].Single())).Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return true;
                }
                if (members.Any(member => member.Value.Method!.HasInstance) && (objects.Count == 0
                    || objects.Any(value => value.Value.Kind == BehaviorValueKind.Constant && value.Value.Reference == null)
                        && objects.Any(value => value.Value.Kind != BehaviorValueKind.Constant || value.Value.Reference != null)))
                {
                    throw new AnalysisException($"反射调用对象的成功与异常分支尚未闭合：{body.MethodId} @ {call.Position}");
                }
                List<(ResolvedMethodDefinition, ResolvedCallTarget)> bindings = new();
                int resolvedMembers = 0;
                if (members.Count > 1 && objects.Count > 1
                    && members.Any(member => member.Value.Method!.HasInstance))
                {
                    throw new AnalysisException($"反射分支关联尚未闭合：{body.MethodId} @ {call.Position}");
                }
                foreach (ValueOrigin member in members)
                {
                    ResolvedMethodDefinition method = catalog.ResolveMethodDefinition(member.Value.Method!, true);
                    bool propertySetter = propertyCall && declaration.Method.Name == "SetValue";
                    IReadOnlyList<IReadOnlyList<BehaviorValueReference>>? suppliedArguments = propertyCall
                        ? arguments.Count == (propertySetter ? 3 : 2)
                            ? sources.ReadArrayElements(arguments[^1].Single(), call.Point)
                            : Array.Empty<IReadOnlyList<BehaviorValueReference>>()
                        : sources.ReadArrayElements(arguments[1].Single(), call.Point);
                    if (suppliedArguments == null)
                    {
                        return true;
                    }
                    if (propertySetter)
                    {
                        suppliedArguments = suppliedArguments.Append(arguments[1]).ToArray();
                    }
                    if (method.Method.Parameters.Count != suppliedArguments.Count || method.Method.GenericArity != 0
                        || method.Method.Parameters.Any(parameter => parameter.RefKind != CatalogRefKind.None))
                    {
                        throw new AnalysisException($"反射实参列表尚未闭合：{body.MethodId} @ {call.Position}");
                    }
                    for (int index = 0; index < suppliedArguments.Count; index++)
                    {
                        ValueOrigin[] values = suppliedArguments[index].SelectMany(argument => sources.GetCallOrigins(argument, true)).ToArray();
                        if (values.Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                        {
                            return true;
                        }
                        if (members.Select(member => member.Value.Method).Distinct().Count() > 1 && values.Length > 1)
                        {
                            throw new AnalysisException($"反射分支关联尚未闭合：{body.MethodId} @ {call.Position}");
                        }
                        TypeIdentityTemplate expected = catalog.ReadResolvedMethodSignature(method.Method.AssemblyPath!, method.Method.MetadataToken)
                            .Parameters[index].Substitute(method.DeclaringTypeArguments);
                        foreach (ValueOrigin value in values.Where(value => value.Value.Kind != BehaviorValueKind.Constant || value.Value.Reference != null))
                        {
                            IReadOnlyList<StaticReceiverType> types = value.Value.Type is BehaviorTypeReference type
                                ? new[] { new StaticReceiverType(ReadReferencedType(catalog, type), type.ArgumentIdentities) }
                                : ReadStaticReceiverTypes(catalog, new[] { value }, sources);
                            if (types.Count == 0 || types.Any(type => !IsAssignable(catalog, ConstructTypeIdentity(type.Definition, type.Arguments).Text, expected.Text)))
                            {
                                throw new AnalysisException($"反射实参转换尚未闭合：{body.MethodId} @ {call.Position} => {method.Method.Id}");
                            }
                        }
                    }
                    IReadOnlyList<ResolvedMethodDefinition> candidates = !method.Method.IsStatic
                        ? ResolveVirtualDefinitions(catalog, method, objects, sources, out _)
                        : new[] { method };
                    resolvedMembers += candidates.Count > 0 ? 1 : 0;
                    foreach (ResolvedMethodDefinition candidate in candidates)
                    {
                        bindings.Add((candidate, new ResolvedCallTarget(candidate.Method.Id, member.Value.Method!,
                            candidate.Method.IsStatic ? Array.Empty<BehaviorValueReference>() : arguments[0],
                            suppliedArguments,
                            candidate.DeclaringTypeArguments.Select(type => type.Text).ToArray())));
                    }
                }
                if (bindings.Count > 0 && resolvedMembers != members.Count)
                {
                    throw new AnalysisException($"反射调用对象的成功与异常分支尚未闭合：{body.MethodId} @ {call.Position}");
                }
                targets = bindings;
                return true;
            }
            return false;
        }

        // 用属性类型和索引参数识别隐藏关系，最派生声明由查找顺序保留。
        private static string ReadPropertySignature(MethodCatalogResult catalog, Mono.Cecil.PropertyDefinition property,
            MethodCatalogResult.InheritedTypeRelation owner)
        {
            Mono.Cecil.MethodDefinition accessor = property.GetMethod ?? property.SetMethod;
            MethodIdentityTemplate signature = catalog.ReadResolvedMethodSignature(owner.Definition.AssemblyPath!, accessor.MetadataToken.ToInt32());
            IEnumerable<TypeIdentityTemplate> types = property.GetMethod != null
                ? signature.Parameters.Prepend(signature.ReturnType)
                : signature.Parameters.SkipLast(1).Prepend(signature.Parameters.Last());
            return $"{accessor.HasThis}|{accessor.CallingConvention}|"
                + string.Concat(types.Select(type => type.Substitute(owner.TypeArguments).Text).Select(text => $"{text.Length}:{text}"));
        }

        // 从委托构造时保存的函数与对象还原调用，虚函数在绑定时选择实际实现。
        private static IReadOnlyList<(ResolvedMethodDefinition Definition, ResolvedCallTarget Binding)>? TryResolveDelegateTargets(
            MethodCatalogResult catalog,
            ValueSourceIndex sources,
            BehaviorValueReference receiver,
            IReadOnlyList<IReadOnlyList<BehaviorValueReference>> arguments)
        {
            List<(ResolvedMethodDefinition, ResolvedCallTarget)> result = new();
            foreach (ValueOrigin origin in sources.GetCallOrigins(receiver).DistinctBy(origin => (origin.Reference, origin.Value)))
            {
                if (origin.Value.Kind == BehaviorValueKind.Constant && origin.Value.Reference == null)
                {
                    continue;
                }

                if (origin.Value.Kind != BehaviorValueKind.NewObject || origin.Value.InputValueIds.Count != 2)
                {
                    return null;
                }

                BehaviorValueReference boundObject = origin.Reference with { ValueId = origin.Value.InputValueIds[0] };
                BehaviorValueReference boundFunction = origin.Reference with { ValueId = origin.Value.InputValueIds[1] };
                foreach (ValueOrigin function in sources.GetCallOrigins(boundFunction).DistinctBy(origin => (origin.Reference, origin.Value)))
                {
                    if (function.Value.Kind != BehaviorValueKind.Function || function.Value.Method == null)
                    {
                        return null;
                    }

                    ResolvedMethodDefinition definition = catalog.ResolveMethodDefinition(function.Value.Method, true);
                    MethodCallInstance instance = sources.GetInstance(function.Reference.InstanceId);
                    definition = definition with
                    {
                        DeclaringTypeArguments = definition.DeclaringTypeArguments.Select(instance.Substitute).ToArray(),
                    };
                    BehaviorMethodReference reference = BindReference(catalog, function.Value.Method, definition, instance);
                    IReadOnlyList<ResolvedMethodDefinition> targets = function.Value.InputValueIds.Count == 0
                        ? new[] { definition }
                        : ResolveVirtualDefinitions(catalog, definition,
                            sources.GetCallOrigins(function.Reference with { ValueId = function.Value.InputValueIds.Single() }, retainTypeChecks: true),
                            sources, out _);
                    foreach (ResolvedMethodDefinition target in targets)
                    {
                        IReadOnlyList<BehaviorValueReference> actualReceiver = Array.Empty<BehaviorValueReference>();
                        IReadOnlyList<IReadOnlyList<BehaviorValueReference>> actualArguments = arguments;
                        int parameterCount = target.Method.Parameters.Count;
                        if (target.Method.IsStatic && parameterCount == arguments.Count + 1)
                        {
                            actualArguments = arguments.Prepend(new[] { boundObject }).ToArray();
                        }
                        else if (!target.Method.IsStatic && parameterCount + 1 == arguments.Count)
                        {
                            actualReceiver = arguments[0];
                            actualArguments = arguments.Skip(1).ToArray();
                        }
                        else if (!target.Method.IsStatic && parameterCount == arguments.Count)
                        {
                            actualReceiver = new[] { boundObject };
                        }
                        else if (!target.Method.IsStatic || parameterCount != arguments.Count)
                        {
                            throw new AnalysisException($"委托参数与实际函数不一致：{receiver.MethodId} => {target.Method.Id}");
                        }

                        result.Add((target, new ResolvedCallTarget(target.Method.Id, reference,
                            actualReceiver, actualArguments, target.DeclaringTypeArguments.Select(type => type.Text).ToArray())));
                    }
                }
            }

            return result.OrderBy(target => target.Item1.Method.Id, StringComparer.Ordinal).ToArray();
        }

        // 按合法具体接收类型逐一选择最近实现，继承同一函数不重复产生目标。
        private static IReadOnlyList<ResolvedMethodDefinition> ResolveVirtualDefinitions(
            MethodCatalogResult catalog,
            ResolvedMethodDefinition contract,
            IReadOnlyList<ValueOrigin> receiverOrigins,
            ValueSourceIndex valueSources,
            out bool coversDeclaredReceivers,
            BehaviorTypeReference? constrainedReceiver = null)
        {
            TypeEntry declaringType = catalog.TypesById[contract.Method.TypeId];
            TypeEntry? constrainedType = constrainedReceiver == null || constrainedReceiver.Identity.Text.StartsWith('!')
                ? null : ReadReferencedType(catalog, constrainedReceiver);
            if (constrainedType?.IsValueType == false)
            {
                constrainedType = null;
            }
            ValueOrigin[] actualOrigins = receiverOrigins.SelectMany(origin => valueSources.GetCallOrigins(origin.Reference)).ToArray();
            var objectOrigins = actualOrigins.Where(origin => origin.Value.Kind != BehaviorValueKind.Constant || origin.Value.Reference != null)
                .Select(origin => (Origin: origin, Types: ReadStaticReceiverTypes(catalog, new[] { origin }, valueSources))).ToArray();
            coversDeclaredReceivers = constrainedReceiver == null && objectOrigins.Length != 0
                && objectOrigins.All(item => item.Origin.Value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance
                    or BehaviorValueKind.FieldRead or BehaviorValueKind.ArrayElementRead
                    && item.Types.Count == 1 && item.Types[0].Definition.Id == declaringType.Id
                    && item.Types[0].Arguments.SequenceEqual(contract.DeclaringTypeArguments));
            List<TypeEntry> candidates = new();
            if (constrainedType != null)
            {
                candidates.Add(constrainedType);
            }
            else if (actualOrigins.Length != 0 && objectOrigins.All(item => item.Types.Count == 1
                && (item.Origin.Value.Kind == BehaviorValueKind.NewObject || item.Types[0].Definition.IsSealed)))
            {
                candidates.AddRange(objectOrigins.Select(item => item.Types[0].Definition).DistinctBy(type => type.Id));
            }
            else
            {
                catalog.RequireClosedDispatchIndex(declaringType.IsInterface);
                candidates.AddRange(ReadReceiverTypes(catalog, declaringType));
            }

            Dictionary<string, ResolvedMethodDefinition> result = new(StringComparer.Ordinal);
            List<(ResolvedMethodDefinition Target, TypeEntry Receiver, IReadOnlyList<TypeIdentityTemplate> Arguments,
                IReadOnlyList<TypeIdentityTemplate> FreeParameters)> selections = new();
            foreach (TypeEntry candidate in candidates.Where(type => !type.IsInterface && !type.IsAbstract))
            {
                catalog.RequireClosedHierarchy(candidate, includeInterfaces: declaringType.IsInterface);
                IReadOnlyList<IReadOnlyList<TypeIdentityTemplate>> candidateArguments = constrainedType != null
                    ? new[] { constrainedReceiver!.ArgumentIdentities }
                    : receiverOrigins.SelectMany(origin => ReadReceiverArguments(
                        catalog, candidate, origin, valueSources, new())).ToArray();
                foreach (IReadOnlyList<TypeIdentityTemplate> inferredArguments in candidateArguments)
                {
                    if (inferredArguments.Count != candidate.GenericParameters.Count)
                    {
                        throw new AnalysisException($"泛型接收类型实参尚未闭合：{candidate.Id}，调用：{contract.Method.Id}；来源："
                            + string.Join("; ", receiverOrigins.Select(origin =>
                                $"{origin.Reference.MethodId} @ {origin.Value.Point}，{origin.Value.Kind}，{origin.Value.Reference}")));
                    }

                    IReadOnlyList<TypeIdentityTemplate> typeArguments = inferredArguments;
                    TypeIdentityTemplate[] parameters = Array.Empty<TypeIdentityTemplate>();
                    if (inferredArguments.Any(argument => ContainsTypeParameter(argument.Text)))
                    {
                        string scope = ValueSourceIndex.ReadReferenceKey(receiverOrigins.Select(origin => origin.Reference)
                            .OrderBy(reference => reference.MethodId, StringComparer.Ordinal).ThenBy(reference => reference.ValueId)
                            .ThenBy(reference => reference.InstanceId)) + "|" + string.Concat(inferredArguments.Select(argument => $"{argument.Text.Length}:{argument.Text}"));
                        parameters = Enumerable.Range(0, candidate.GenericParameters.Count).Select(index =>
                            TypeIdentityTemplate.ScopedParameter(candidate.AssemblyPath + "|" + candidate.Id, scope, index)).ToArray();
                        typeArguments = inferredArguments.Select(argument => argument.Substitute(parameters)).ToArray();
                        foreach (int index in Enumerable.Range(0, parameters.Length).Where(index =>
                                     inferredArguments[index].Text == $"!{index}"))
                        {
                            GenericParameterRule rule = candidate.GenericParameters[index];
                            valueSources!.AddParameterRule(parameters[index], rule with
                            { TypeConstraints = rule.TypeConstraints.Select(constraint => constraint.Substitute(typeArguments)).ToArray() });
                        }
                    }
                    if (!ReceiverMatchesType(catalog, candidate, typeArguments,
                        new StaticReceiverType(declaringType, contract.DeclaringTypeArguments)))
                    {
                        continue;
                    }
                    if (contract.Method.IsFinal)
                    {
                        selections.Add((contract, candidate, typeArguments, parameters));
                        continue;
                    }

                    MethodCatalogResult.InheritedTypeRelation[] hierarchy = catalog.ReadInheritedTypes(candidate, typeArguments, declaringType.IsInterface)
                        .Prepend(new MethodCatalogResult.InheritedTypeRelation(candidate, typeArguments, false, true, 0))
                        .ToArray();
                    MethodCatalogResult.InheritedTypeRelation[] contractRelations = hierarchy.Where(relation =>
                            relation.IsInterface && relation.Definition.Id == declaringType.Id)
                        .ToArray();
                    MethodCatalogResult.InheritedTypeRelation[] exactContractRelations = contractRelations.Where(
                            relation => relation.TypeArguments.Select(type => type.Text).SequenceEqual(
                                contract.DeclaringTypeArguments.Select(type => type.Text), StringComparer.Ordinal))
                        .ToArray();
                    ResolvedMethodDefinition[] implementationContracts = declaringType.IsInterface
                        ? (exactContractRelations.Length == 0 ? contractRelations.Where(relation =>
                                AreInterfaceArgumentsCompatible(
                                    catalog,
                                    declaringType,
                                    relation.TypeArguments,
                                    contract.DeclaringTypeArguments))
                            : exactContractRelations).Select(relation => contract with
                            {
                                DeclaringTypeArguments = relation.TypeArguments,
                            }).DistinctBy(item => string.Join(",", item.DeclaringTypeArguments.Select(type => type.Text)), StringComparer.Ordinal).ToArray()
                        : new[] { contract };
                    var interfaceOwners = !declaringType.IsInterface ? new Dictionary<ResolvedMethodDefinition, IReadOnlySet<string>>()
                        : implementationContracts.ToDictionary(item => item, item =>
                    {
                        int introductionDepth = hierarchy.Where(relation => !relation.IsInterface
                            && catalog.ReadInheritedTypes(relation.Definition, relation.TypeArguments).Any(parent =>
                                parent.CanImplementInterface && parent.Definition.Id == item.Method.TypeId
                                && TypeArgumentsMatch(parent.TypeArguments, item.DeclaringTypeArguments)))
                            .Select(relation => relation.Depth).DefaultIfEmpty(int.MaxValue).Min();
                        return (IReadOnlySet<string>)hierarchy.Where(relation => !relation.IsInterface && relation.Depth > introductionDepth)
                            .Select(relation => relation.Definition.Id).ToHashSet(StringComparer.Ordinal);
                    });
                    ResolvedMethodDefinition? selected = null;
                    foreach (var relation in hierarchy.Where(relation => !relation.IsInterface).OrderBy(relation => relation.Depth))
                    {
                        MethodEntry[] matches = catalog.GetMethods(relation.Definition).Where(method =>
                            implementationContracts.Any(implementationContract => IsImplementation(
                                catalog,
                                method,
                                relation.TypeArguments,
                                implementationContract,
                                requireMatchingReturn: declaringType.IsInterface,
                                interfaceOwners.GetValueOrDefault(implementationContract)))).ToArray();
                        if (matches.Length > 1)
                        {
                            throw new AnalysisException($"同一接收类型存在多个调用实现：{candidate.Id} => {contract.Method.Id}");
                        }

                        if (matches.Length == 1)
                        {
                            selected = new ResolvedMethodDefinition(matches[0], relation.TypeArguments);
                            break;
                        }
                    }

                    selected ??= FindDefaultInterfaceImplementation(
                        catalog,
                        candidate,
                        hierarchy,
                        implementationContracts);
                    if (selected == null || selected.Method.IsAbstract)
                    {
                        throw new AnalysisException($"具体接收类型未找到可执行实现：{candidate.Id} => {contract.Method.Id}");
                    }

                    selections.Add((selected, candidate, typeArguments, parameters));
                }
            }

            foreach (var group in selections.GroupBy(item => item.Target.Method.Id + "|"
                         + string.Join(",", item.Target.DeclaringTypeArguments.Select(type => type.Text))))
            {
                string? missingProof = null;
                foreach (var selection in group.OrderBy(item => item.FreeParameters.Count).ThenBy(item => item.Receiver.Id, StringComparer.Ordinal))
                {
                    string? failure = null;
                    bool? exists = selection.FreeParameters.Count == 0 ? true
                        : HasCandidateWitness(catalog, selection.Receiver, selection.Arguments, selection.FreeParameters, valueSources!, out failure);
                    if (exists == true && SatisfiesGenericConstraints(catalog, selection.Receiver, selection.Arguments, valueSources))
                    {
                        result.Add(group.Key, selection.Target);
                        break;
                    }
                    missingProof ??= failure;
                }
                if (!result.ContainsKey(group.Key) && missingProof != null)
                {
                    throw new AnalysisException(missingProof);
                }
            }

            return result.Values.OrderBy(target => target.Method.Id, StringComparer.Ordinal)
                .ThenBy(target => string.Join(",", target.DeclaringTypeArguments.Select(type => type.Text)), StringComparer.Ordinal)
                .ToArray();
        }

        // 共用已有继承索引列出实际候选类型，不重复扫描全部类型。
        private static IEnumerable<TypeEntry> ReadReceiverTypes(MethodCatalogResult catalog, TypeEntry declaration)
        {
            Queue<TypeEntry> pending = new(new[] { declaration });
            HashSet<string> visited = new(StringComparer.Ordinal);
            while (pending.TryDequeue(out TypeEntry? current))
            {
                if (!visited.Add(current.Id))
                {
                    continue;
                }
                yield return current;
                IReadOnlyList<TypeEntry> children = (current.IsInterface
                    ? catalog.ImplementingTypesByInterfaceId : catalog.DerivedTypesByBaseId).GetValueOrDefault(current.Id)
                    ?? Array.Empty<TypeEntry>();
                foreach (TypeEntry child in children)
                {
                    pending.Enqueue(child);
                }
            }
        }

        // 从对象、参数或转换结果读取接收对象已经确定的类型限制。
        private static IReadOnlyList<StaticReceiverType> ReadStaticReceiverTypes(
            MethodCatalogResult catalog,
            IReadOnlyList<ValueOrigin> origins,
            ValueSourceIndex valueSources)
        {
            if (origins.Count == 0
                || origins.Any(origin => origin.Value.Kind is not (
                    BehaviorValueKind.CurrentInstance or BehaviorValueKind.Parameter
                    or BehaviorValueKind.Conversion or BehaviorValueKind.NewObject or BehaviorValueKind.FieldRead)
                    && !(origin.Value.Kind == BehaviorValueKind.ArrayElementRead && origin.Value.Type != null)))
            {
                return Array.Empty<StaticReceiverType>();
            }

            List<StaticReceiverType> result = new();
            foreach (ValueOrigin origin in origins)
            {
                if (origin.Value.Kind == BehaviorValueKind.NewObject
                    || origin.Value.Kind == BehaviorValueKind.Conversion && !origin.Value.Type!.Identity.Text.StartsWith('!'))
                {
                    BehaviorTypeReference reference = origin.Value.Type!;
                    TypeEntry type = ReadReferencedType(catalog, reference);
                    result.Add(new StaticReceiverType(type, reference.ArgumentIdentities));
                    continue;
                }

                if (!valueSources.Definitions.TryGetValue(origin.Reference.MethodId, out MethodEntry? owner))
                {
                    throw new AnalysisException($"接收对象所属函数尚未登记：{origin.Reference.MethodId}");
                }

                MethodCallInstance instance = valueSources.GetInstance(origin.Reference.InstanceId);
                if (origin.Value.Kind == BehaviorValueKind.CurrentInstance)
                {
                    result.Add(new StaticReceiverType(
                        catalog.TypesById[owner.TypeId],
                        instance.TypeArguments));
                    continue;
                }

                TypeIdentityTemplate parameter = instance.Substitute(origin.Value.Kind is BehaviorValueKind.Conversion or BehaviorValueKind.ArrayElementRead
                    ? origin.Value.Type!.Identity : origin.Value.Kind == BehaviorValueKind.FieldRead
                    ? catalog.ReadResolvedFieldType(origin.Value.Member!)
                    : catalog.ReadResolvedMethodSignature(owner.AssemblyPath!, owner.MetadataToken)
                        .Parameters[origin.Value.ParameterIndex!.Value]);
                string identity = TryReadTypeSuffix(parameter.Text, out string body, out string suffix)
                    && suffix == "&" ? body : parameter.Text;
                parameter = new TypeIdentityTemplate(identity);
                if (ContainsTypeParameter(identity) || parameter.HasUnspecifiedParameter && !TryReadNamedType(identity, out _, out _))
                {
                    if (valueSources.ReadParameterRule(parameter) is not GenericParameterRule rule)
                    {
                        throw new AnalysisException($"接收对象静态类型尚未闭合：{owner.Id} => {identity}");
                    }
                    TypeIdentityTemplate[] bounds = valueSources.ReadParameterBounds(parameter)
                        .Where(bound => TryReadNamedType(bound.Text, out _, out _)).ToArray();
                    foreach (TypeIdentityTemplate bound in bounds)
                    {
                        TypeEntry boundType = ReadAssignableType(catalog, bound.Text, out TypeIdentityTemplate[] boundArguments);
                        result.Add(new StaticReceiverType(boundType, boundArguments, rule.RequiresReferenceType, rule.RequiresValueType));
                    }
                    if (bounds.Length == 0)
                    {
                        result.Add(new StaticReceiverType(catalog.ReadPrimitiveType("System.Object"),
                            Array.Empty<TypeIdentityTemplate>(), rule.RequiresReferenceType, rule.RequiresValueType));
                    }
                    continue;
                }

                TypeEntry definition = ReadAssignableType(catalog, identity, out TypeIdentityTemplate[] arguments);
                result.Add(new StaticReceiverType(definition, arguments));
            }

            return result.DistinctBy(type => type.Definition.Id + "|"
                + string.Join(",", type.Arguments.Select(argument => argument.Text))).ToArray();
        }

        // 沿每条来源路径合并转换前后的类型实参，空值没有可调用对象。
        private static IReadOnlyList<IReadOnlyList<TypeIdentityTemplate>> ReadReceiverArguments(
            MethodCatalogResult catalog,
            TypeEntry candidate,
            ValueOrigin origin,
            ValueSourceIndex sources,
            HashSet<BehaviorValueReference> path)
        {
            if (origin.Value.Kind == BehaviorValueKind.Constant && origin.Value.Reference == null
                || !path.Add(origin.Reference))
            {
                return Array.Empty<IReadOnlyList<TypeIdentityTemplate>>();
            }

            IReadOnlyList<StaticReceiverType> restrictions = ReadStaticReceiverTypes(
                catalog, new[] { origin }, sources);
            if (restrictions.Any(type => type.RequiresReferenceType && candidate.IsValueType
                || type.RequiresValueType && !candidate.IsValueType))
            {
                path.Remove(origin.Reference);
                return Array.Empty<IReadOnlyList<TypeIdentityTemplate>>();
            }
            IReadOnlyList<IReadOnlyList<TypeIdentityTemplate>> arguments;
            if (origin.Value.Kind == BehaviorValueKind.Conversion)
            {
                IReadOnlyList<IReadOnlyList<TypeIdentityTemplate>> inputs = sources.GetCallOrigins(origin.Reference with
                { ValueId = origin.Value.InputValueIds.Single() }, retainTypeChecks: true)
                    .SelectMany(input => ReadReceiverArguments(catalog, candidate, input, sources, path)).ToArray();
                arguments = inputs.SelectMany(input => input.Any(type => ContainsTypeParameter(type.Text))
                    ? restrictions.SelectMany(type => InferReceiverArguments(catalog, candidate, type.Definition, type.Arguments,
                            expandVariantBounds: path.Count == 1))
                        .Select(outer => MergeReceiverArguments(outer, input)).OfType<IReadOnlyList<TypeIdentityTemplate>>()
                    : restrictions.All(type => ReceiverMatchesType(catalog, candidate, input, type))
                        ? new[] { input } : Array.Empty<IReadOnlyList<TypeIdentityTemplate>>()).ToArray();
            }
            else
            {
                arguments = restrictions.Count == 0
                    ? new[] { Enumerable.Range(0, candidate.GenericParameters.Count)
                        .Select(index => new TypeIdentityTemplate($"!{index}")).ToArray() }
                    : origin.Value.Kind == BehaviorValueKind.NewObject
                    ? restrictions.Where(type => type.Definition.Id == candidate.Id).Select(type => type.Arguments).ToArray()
                    : restrictions.SelectMany(type => InferReceiverArguments(
                        catalog, candidate, type.Definition, type.Arguments, expandVariantBounds: path.Count == 1)).ToArray();
            }

            if (path.Count == 1)
            {
                arguments = arguments.Where(types => types.Any(type => ContainsTypeParameter(type.Text))
                    || ReceiverPathAllowsType(catalog, candidate, types, origin, sources, new())).ToArray();
            }

            path.Remove(origin.Reference);
            return arguments;
        }

        // 整条来源路径闭合后复核每次转换，避免中途未定方差被错误接受或提前拒绝。
        private static bool ReceiverPathAllowsType(
            MethodCatalogResult catalog, TypeEntry candidate, IReadOnlyList<TypeIdentityTemplate> arguments,
            ValueOrigin origin, ValueSourceIndex sources,
            HashSet<BehaviorValueReference> path)
        {
            if (origin.Value.Kind == BehaviorValueKind.Constant && origin.Value.Reference == null
                || !path.Add(origin.Reference))
            {
                return false;
            }

            bool matches = ReadStaticReceiverTypes(catalog, new[] { origin }, sources)
                .All(type => (origin.Value.Kind != BehaviorValueKind.NewObject || type.Definition.Id == candidate.Id)
                    && ReceiverMatchesType(catalog, candidate, arguments, type));
            if (matches && origin.Value.Kind == BehaviorValueKind.Conversion)
            {
                matches = sources.GetCallOrigins(origin.Reference with
                { ValueId = origin.Value.InputValueIds.Single() }, retainTypeChecks: true)
                    .Any(input => ReceiverPathAllowsType(catalog, candidate, arguments, input, sources, path));
            }

            path.Remove(origin.Reference);
            return matches;
        }

        // 用已经确定的实际类型核对转换，泛型方差不能跨越值类型边界。
        private static bool ReceiverMatchesType(
            MethodCatalogResult catalog, TypeEntry candidate,
            IReadOnlyList<TypeIdentityTemplate> arguments, StaticReceiverType restriction)
        {
            return !(restriction.RequiresReferenceType && candidate.IsValueType
                    || restriction.RequiresValueType && !candidate.IsValueType)
                && catalog.ReadInheritedTypes(candidate, arguments, restriction.Definition.IsInterface)
                .Prepend(new MethodCatalogResult.InheritedTypeRelation(candidate, arguments, false, true, 0))
                .Any(relation => relation.Definition.Id == restriction.Definition.Id
                    && AreInterfaceArgumentsCompatible(catalog, restriction.Definition,
                        relation.TypeArguments, restriction.Arguments));
        }

        // 合并同一对象在不同位置提供的实参，不允许冲突的类型组合。
        private static IReadOnlyList<TypeIdentityTemplate>? MergeReceiverArguments(
            IReadOnlyList<TypeIdentityTemplate> outer, IReadOnlyList<TypeIdentityTemplate> inner)
        {
            TypeIdentityTemplate[] result = new TypeIdentityTemplate[outer.Count];
            for (int index = 0; index < result.Length; index++)
            {
                if (outer[index].Text == $"!{index}")
                {
                    result[index] = inner[index];
                }
                else if (inner[index].Text == $"!{index}" || TypesMatch(outer[index], inner[index]))
                {
                    result[index] = outer[index];
                }
                else
                {
                    return null;
                }
            }

            return result;
        }

        /// <summary>保存接收对象声明的类型定义及当前实例中的实参。</summary>
        private sealed record StaticReceiverType(
            TypeEntry Definition,
            IReadOnlyList<TypeIdentityTemplate> Arguments,
            bool RequiresReferenceType = false,
            bool RequiresValueType = false);

        // 用本次调用的类型实参建立引用，目标函数自身的泛型参数仍按声明匹配。
        private static BehaviorMethodReference BindReference(
            MethodCatalogResult catalog,
            BehaviorMethodReference reference,
            ResolvedMethodDefinition definition,
            MethodCallInstance instance)
        {
            TypeIdentityTemplate declaringType = instance.Substitute(reference.Identity.DeclaringType);
            TypeIdentityTemplate[] genericArguments = reference.GenericArgumentTypeIds.Count == 0
                ? Array.Empty<TypeIdentityTemplate>()
                : catalog.ReadResolvedTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken,
                    methodArguments: true).Select(instance.Substitute).ToArray();
            MethodIdentityTemplate identity = new(declaringType, reference.Name, reference.Identity.GenericArity,
                definition.Method.Parameters.Select(parameter => parameter.TypeIdentity.Substitute(definition.DeclaringTypeArguments)).ToArray(),
                new TypeIdentityTemplate(definition.Method.ReturnTypeId).Substitute(definition.DeclaringTypeArguments));
            return reference with
            {
                GenericArgumentTypeIds = genericArguments.Select(type => type.Text).ToArray(),
                Identity = identity,
            };
        }

        // 用目录统一定位接收类型，保留本地定义标记和外部引用的区别。
        private static TypeEntry ReadReferencedType(
            MethodCatalogResult catalog,
            BehaviorTypeReference reference)
        {
            if (reference.Identity.Text.StartsWith('!'))
            {
                throw new AnalysisException(
                    $"接收对象的开放类型尚未闭合：{reference.Id}");
            }

            if (reference.TargetAssemblyIdentity == null && reference.KnownTypeId == null)
            {
                return ReadAssignableType(catalog, reference.Id, out _);
            }

            return catalog.ResolveTypeDefinition(reference);
        }

        // 在类没有实现接口槽时按接口继承关系选择唯一的最具体默认实现。
        private static ResolvedMethodDefinition? FindDefaultInterfaceImplementation(
            MethodCatalogResult catalog,
            TypeEntry receiver,
            IReadOnlyList<MethodCatalogResult.InheritedTypeRelation> hierarchy,
            IReadOnlyList<ResolvedMethodDefinition> contracts)
        {
            var matches = hierarchy.Where(relation => relation.IsInterface)
                .SelectMany(relation => catalog.GetMethods(relation.Definition)
                    .Where(method => !method.IsAbstract && contracts.Any(contract => IsImplementation(
                        catalog,
                        method,
                        relation.TypeArguments,
                        contract,
                        requireMatchingReturn: true)))
                    .Select(method => (Relation: relation, Method: method)))
                .ToArray();
            var mostSpecific = matches.Where(candidate => !matches.Any(other =>
                    other.Relation.Definition.Id != candidate.Relation.Definition.Id
                    && catalog.ReadInheritedTypes(other.Relation.Definition, other.Relation.TypeArguments)
                        .Any(parent => parent.IsInterface
                            && parent.Definition.Id == candidate.Relation.Definition.Id
                            && TypeArgumentsMatch(parent.TypeArguments, candidate.Relation.TypeArguments))))
                .ToArray();
            if (mostSpecific.Length > 1)
            {
                throw new AnalysisException($"接收类型存在多个最具体默认接口实现：{receiver.Id}");
            }

            return mostSpecific.Length == 0
                ? null
                : new ResolvedMethodDefinition(
                    mostSpecific[0].Method,
                    mostSpecific[0].Relation.TypeArguments);
        }

        // 用真实继承关系中的类型参数匹配调用契约，不把不同构造接口混为同一接口。
        private static IReadOnlyList<IReadOnlyList<TypeIdentityTemplate>> InferReceiverArguments(
            MethodCatalogResult catalog,
            TypeEntry candidate,
            TypeEntry declaringType,
            IReadOnlyList<TypeIdentityTemplate> arguments,
            bool expandVariantBounds = true,
            HashSet<string>? variantQueries = null)
        {
            if (candidate.Id == declaringType.Id)
            {
                return new[] { arguments };
            }

            List<IReadOnlyList<TypeIdentityTemplate>> result = new();
            MethodCatalogResult.InheritedTypeRelation[] relations = catalog.ReadInheritedTypes(candidate, includeInterfaces: declaringType.IsInterface)
                .Where(relation => relation.Definition.Id == declaringType.Id)
                .ToArray();
            MethodCatalogResult.InheritedTypeRelation[] exactRelations = relations.Where(relation =>
                    relation.TypeArguments.Select(type => type.Text).SequenceEqual(
                        arguments.Select(type => type.Text), StringComparer.Ordinal))
                .ToArray();
            foreach (var relation in exactRelations.Length == 0 ? relations : exactRelations)
            {
                List<Dictionary<int, TypeIdentityTemplate>> bindings = relation.TypeArguments.Count == arguments.Count
                    ? new() { new() } : new();
                for (int index = 0; bindings.Count != 0 && index < arguments.Count; index++)
                {
                    GenericParameterRule rule = declaringType.GenericParameters[index];
                    string template = relation.TypeArguments[index].Text;
                    if (rule.IsCovariant || rule.IsContravariant)
                    {
                        if (ContainsTypeParameter(template))
                        {
                            // 中途转换只保留开放位置，最外层确定候选后统一复核整条转换路径。
                            if (!expandVariantBounds)
                            {
                                continue;
                            }

                            IReadOnlyList<TypeIdentityTemplate> choices = ReadVariantArgumentTypes(
                                catalog, arguments[index], rule.IsCovariant, variantQueries ?? new());
                            bindings = bindings.SelectMany(binding => choices.Select(choice =>
                            {
                                Dictionary<int, TypeIdentityTemplate> expanded = new(binding);
                                return TryBindTypeArguments(template, choice.Text, expanded) ? expanded : null;
                            })).OfType<Dictionary<int, TypeIdentityTemplate>>().ToList();
                            continue;
                        }

                        bool matches = rule.IsCovariant
                            ? IsReferenceAssignable(catalog, template, arguments[index].Text)
                            : IsReferenceAssignable(catalog, arguments[index].Text, template);
                        if (!matches)
                        {
                            bindings.Clear();
                        }
                    }
                    else
                    {
                        bindings = bindings.Where(binding => TryBindTypeArguments(template, arguments[index].Text, binding)).ToList();
                    }
                }

                foreach (Dictionary<int, TypeIdentityTemplate> replacements in bindings)
                {
                    IReadOnlyList<TypeIdentityTemplate> inferred = Enumerable.Range(0, candidate.GenericParameters.Count)
                            .Select(ordinal => replacements.TryGetValue(ordinal, out TypeIdentityTemplate? value)
                                ? value : new TypeIdentityTemplate($"!{ordinal}")).ToArray();
                    result.Add(inferred);
                }
            }

            return result;
        }

        // 沿真实继承关系枚举直接类型参数的方差范围，不把开放实参猜成唯一具体类型。
        private static IReadOnlyList<TypeIdentityTemplate> ReadVariantArgumentTypes(
            MethodCatalogResult catalog, TypeIdentityTemplate bound, bool covariant, HashSet<string> activeQueries)
        {
            if (!IsReferenceType(catalog, bound.Text))
            {
                return new[] { bound };
            }

            TypeEntry definition = ReadAssignableType(catalog, bound.Text, out TypeIdentityTemplate[] arguments);
            string key = bound.Text + "|" + covariant;
            if (!activeQueries.Add(key))
            {
                throw new AnalysisException($"类型参数范围递归展开尚未闭合：{bound.Text}");
            }

            try
            {
                List<TypeIdentityTemplate> result = new() { bound };
                List<TypeIdentityTemplate[]> variants = new() { arguments };
                for (int index = 0; index < arguments.Length; index++)
                {
                    GenericParameterRule rule = definition.GenericParameters[index];
                    if (!rule.IsCovariant && !rule.IsContravariant)
                    {
                        continue;
                    }

                    IReadOnlyList<TypeIdentityTemplate> choices = ReadVariantArgumentTypes(
                        catalog, arguments[index], covariant == rule.IsCovariant, activeQueries);
                    variants = variants.SelectMany(values => choices.Select(choice =>
                    {
                        TypeIdentityTemplate[] expanded = values.ToArray();
                        expanded[index] = choice;
                        return expanded;
                    })).ToList();
                }
                if (definition.GenericParameters.Any(rule => rule.IsCovariant || rule.IsContravariant))
                {
                    result.AddRange(variants.Where(values => SatisfiesGenericConstraints(catalog, definition, values))
                        .Select(values => ConstructTypeIdentity(definition, values)));
                }

                if (covariant)
                {
                    foreach (TypeEntry child in ReadReceiverTypes(catalog, definition).Where(type => !type.IsValueType && type.Id != definition.Id))
                    {
                        foreach (IReadOnlyList<TypeIdentityTemplate> childArguments in InferReceiverArguments(
                                     catalog, child, definition, arguments, variantQueries: activeQueries))
                        {
                            if (childArguments.Any(argument => ContainsTypeParameter(argument.Text)))
                            {
                                throw new AnalysisException($"派生类型仍有开放实参：{bound.Text} => {child.Id}");
                            }
                            if (SatisfiesGenericConstraints(catalog, child, childArguments))
                            {
                                result.Add(ConstructTypeIdentity(child, childArguments));
                            }
                        }
                    }
                }
                else
                {
                    result.Add(new TypeIdentityTemplate("System.Object"));
                    string objectId = catalog.ReadPrimitiveType("System.Object").Id;
                    foreach (MethodCatalogResult.InheritedTypeRelation relation in catalog.ReadInheritedTypes(definition, arguments))
                    {
                        TypeIdentityTemplate parent = relation.Definition.Id == objectId
                            ? new TypeIdentityTemplate("System.Object") : ConstructTypeIdentity(relation.Definition, relation.TypeArguments);
                        result.AddRange(relation.Definition.GenericParameters.Any(rule => rule.IsCovariant || rule.IsContravariant)
                            ? ReadVariantArgumentTypes(catalog, parent, false, activeQueries) : new[] { parent });
                    }
                }

                return result.DistinctBy(type => type.Text).Where(type => covariant
                    ? IsReferenceAssignable(catalog, type.Text, bound.Text) : IsReferenceAssignable(catalog, bound.Text, type.Text)).ToArray();
            }
            finally
            {
                activeQueries.Remove(key);
            }
        }

        // 从真实定义与已经闭合的实参构造统一类型身份。
        private static TypeIdentityTemplate ConstructTypeIdentity(TypeEntry definition, IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            return new TypeIdentityTemplate(definition.Id + (arguments.Count == 0 ? string.Empty
                : "<" + string.Join(",", arguments.Select(argument => argument.Text)) + ">"));
        }

        // 判断构造接口的实参能否按 CLR 方差转换到调用契约。
        private static bool AreInterfaceArgumentsCompatible(
            MethodCatalogResult catalog,
            TypeEntry interfaceType,
            IReadOnlyList<TypeIdentityTemplate> implementationArguments,
            IReadOnlyList<TypeIdentityTemplate> contractArguments)
        {
            if (implementationArguments.Count != contractArguments.Count
                || interfaceType.GenericParameters.Count != contractArguments.Count)
            {
                return false;
            }

            return interfaceType.GenericParameters.Select((rule, index) => (rule, index)).All(item =>
                TypesMatch(implementationArguments[item.index], contractArguments[item.index])
                || (item.rule.IsCovariant
                    ? IsReferenceAssignable(
                        catalog,
                        implementationArguments[item.index].Text,
                        contractArguments[item.index].Text)
                    : item.rule.IsContravariant
                        ? IsReferenceAssignable(
                            catalog,
                            contractArguments[item.index].Text,
                            implementationArguments[item.index].Text)
                        : false));
        }

        // 候选类型必须有真实可构造的实参见证，不能用参数自己的约束证明它存在。
        private static bool? HasCandidateWitness(MethodCatalogResult catalog, TypeEntry candidate,
            IReadOnlyList<TypeIdentityTemplate> arguments, IReadOnlyList<TypeIdentityTemplate> parameters, ValueSourceIndex sources,
            out string? missingProof)
        {
            missingProof = null;
            TypeIdentityTemplate[] concrete = arguments.ToArray();
            HashSet<int> remaining = Enumerable.Range(0, arguments.Count).Where(index => arguments[index] == parameters[index]).ToHashSet();
            int initialCount = remaining.Count;
            while (remaining.Count != 0)
            {
                bool advanced = false;
                foreach (int index in remaining.ToArray())
                {
                    GenericParameterRule rule = candidate.GenericParameters[index];
                    TypeIdentityTemplate[] bounds = rule.TypeConstraints.Select(bound => bound.Substitute(concrete)).ToArray();
                    if (bounds.Any(bound => remaining.Any(other => other != index && bound.Text.Contains(parameters[other].Text, StringComparison.Ordinal))))
                    {
                        continue;
                    }
                    bool selfReferencing = bounds.Any(bound => bound.Text.Contains(parameters[index].Text, StringComparison.Ordinal));
                    TypeIdentityTemplate[] limitArguments = Array.Empty<TypeIdentityTemplate>();
                    TypeEntry limit = bounds.Length == 0 ? catalog.ReadPrimitiveType(rule.RequiresValueType ? "System.Int32" : "System.Object")
                        : ReadAssignableType(catalog, bounds[0].Text, out limitArguments);
                    IEnumerable<TypeEntry> choices = bounds.Length == 0 ? new[] { limit } : ReadReceiverTypes(catalog, limit);
                    bool unfinished = bounds.Length == 0 || selfReferencing;
                    bool found = false;
                    HashSet<int> pendingParameters = remaining.Where(other => other != index).ToHashSet();
                    foreach (TypeEntry choice in choices.Where(type => !rule.RequiresDefaultConstructor || !type.IsInterface && !type.IsAbstract))
                    {
                        IEnumerable<IReadOnlyList<TypeIdentityTemplate>> inferredArguments = selfReferencing
                            ? choice.GenericParameters.Count == 0 ? new[] { Array.Empty<TypeIdentityTemplate>() } : Array.Empty<TypeIdentityTemplate[]>()
                            : InferReceiverArguments(catalog, choice, limit, limitArguments);
                        foreach (IReadOnlyList<TypeIdentityTemplate> inferred in inferredArguments)
                        {
                            if (inferred.Any(argument => ContainsTypeParameter(argument.Text)))
                            {
                                unfinished = true;
                                continue;
                            }
                            TypeIdentityTemplate[] proposed = concrete.ToArray();
                            proposed[index] = ConstructTypeIdentity(choice, inferred);
                            if (SatisfiesGenericConstraints(catalog, choice, inferred, sources)
                                && SatisfiesGenericConstraints(catalog, candidate, proposed, sources, pendingParameters))
                            {
                                concrete = proposed;
                                found = true;
                                break;
                            }
                        }
                        if (found)
                        {
                            break;
                        }
                    }
                    if (!found)
                    {
                        missingProof = unfinished || remaining.Count != initialCount
                            ? $"泛型候选的合法构造尚未闭合：{candidate.Id}，参数 {index}" : null;
                        return missingProof == null ? false : null;
                    }
                    remaining.Remove(index);
                    advanced = true;
                }
                if (!advanced)
                {
                    missingProof = $"泛型候选的相互约束尚未闭合：{candidate.Id}";
                    return null;
                }
            }
            return true;
        }

        // 检查反推出的类型实参是否满足实现类声明的全部约束。
        private static bool SatisfiesGenericConstraints(
            MethodCatalogResult catalog,
            TypeEntry type,
            IReadOnlyList<TypeIdentityTemplate> arguments,
            ValueSourceIndex? sources = null,
            IReadOnlySet<int>? pendingWitnessParameters = null)
        {
            if (type.GenericParameters.Count == 0)
            {
                return true;
            }

            for (int index = 0; index < arguments.Count; index++)
            {
                if (pendingWitnessParameters?.Contains(index) == true)
                {
                    continue;
                }
                GenericParameterRule rule = type.GenericParameters[index];
                string argument = arguments[index].Text;
                if (sources?.ReadParameterRule(arguments[index]) is GenericParameterRule declared)
                {
                    TypeIdentityTemplate[] bounds = sources.ReadParameterBounds(arguments[index]).ToArray();
                    bool referenceType = bounds.Any(bound => sources.ReadParameterRule(bound)?.RequiresReferenceType == true
                        || !bound.HasUnspecifiedParameter && IsReferenceTypeConstraint(catalog, bound));
                    if (rule.RequiresValueType && referenceType || rule.RequiresReferenceType && declared.RequiresValueType)
                    {
                        return false;
                    }
                    if ((!rule.RequiresReferenceType || referenceType)
                        && (!rule.RequiresValueType || declared.RequiresValueType)
                        && (!rule.RequiresDefaultConstructor || declared.RequiresDefaultConstructor || declared.RequiresValueType)
                        && rule.TypeConstraints.Select(constraint => constraint.Substitute(arguments)).All(required =>
                            bounds.Any(bound => bound.Text == required.Text
                                || !bound.HasUnspecifiedParameter && !required.HasUnspecifiedParameter
                                    && IsAssignable(catalog, bound.Text, required.Text))))
                    {
                        continue;
                    }
                    throw new AnalysisException($"类型参数的现有约束不足以闭合调用：{type.Id}，参数 {index}");
                }
                if (arguments[index].HasUnspecifiedParameter && !TryReadNamedType(argument, out _, out _)
                    && (rule.RequiresReferenceType || rule.RequiresValueType || rule.RequiresDefaultConstructor || rule.TypeConstraints.Count != 0))
                {
                    throw new AnalysisException($"未指定类型参数参与构造约束：{type.Id}，参数 {index}");
                }
                if (rule.RequiresReferenceType && !IsReferenceType(catalog, argument)
                    || rule.RequiresValueType && !IsNonNullableValueType(catalog, argument)
                    || rule.RequiresDefaultConstructor && !HasPublicDefaultConstructor(catalog, argument)
                    || rule.TypeConstraints.Any(constraint => !IsAssignable(
                        catalog,
                        argument,
                        constraint.Substitute(arguments).Text)))
                {
                    return false;
                }
            }

            return true;
        }

        // 普通基类约束限定引用类型，CLR 的三个共同根类型不提供这个保证。
        private static bool IsReferenceTypeConstraint(MethodCatalogResult catalog, TypeIdentityTemplate bound)
        {
            TypeEntry type = ReadAssignableType(catalog, bound.Text, out _);
            return !type.IsInterface && !type.IsValueType
                && !(type.AssemblyPath == catalog.ReadPrimitiveType("System.Object").AssemblyPath
                    && type.FullName is "System.Object" or "System.ValueType" or "System.Enum");
        }

        // 判断类型身份是否包含尚未代入的类型参数。
        private static bool ContainsTypeParameter(string identity)
        {
            for (int index = 0; index < identity.Length - 1; index++)
            {
                if (identity[index] == '!' && identity[index + 1] != '!'
                    && char.IsDigit(identity[index + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        // 判断类型满足 CLR struct 约束要求的非 Nullable 值类型条件。
        private static bool IsNonNullableValueType(MethodCatalogResult catalog, string identity)
        {
            if (!IsValueType(catalog, identity))
            {
                return false;
            }

            return !ReadAssignableType(catalog, identity, out _).IsNullableValueType;
        }

        // 判断两个构造类型之间是否存在 CLR 引用转换。
        private static bool IsReferenceAssignable(
            MethodCatalogResult catalog,
            string source,
            string target)
        {
            return IsReferenceType(catalog, source)
                && IsReferenceType(catalog, target)
                && IsAssignable(catalog, source, target);
        }

        // 沿真实基类、接口和构造参数方差判断类型可赋值关系。
        private static bool IsAssignable(
            MethodCatalogResult catalog,
            string source,
            string target)
        {
            if (source == target || target == "System.Object")
            {
                return true;
            }

            bool sourceArray = TryReadTypeSuffix(source, out string sourceElement, out string sourceSuffix)
                && sourceSuffix.StartsWith('[');
            bool targetArray = TryReadTypeSuffix(target, out string targetElement, out string targetSuffix)
                && targetSuffix.StartsWith('[');
            if (sourceArray || targetArray)
            {
                return sourceArray && targetArray && sourceSuffix == targetSuffix
                    && IsReferenceAssignable(catalog, sourceElement, targetElement);
            }

            TypeEntry sourceType = ReadAssignableType(catalog, source, out TypeIdentityTemplate[] sourceArguments);
            TypeEntry targetType = ReadAssignableType(catalog, target, out TypeIdentityTemplate[] targetArguments);
            if (sourceType.LogicalId == targetType.LogicalId)
            {
                return AreInterfaceArgumentsCompatible(
                    catalog,
                    sourceType,
                    sourceArguments,
                    targetArguments);
            }

            return catalog.ReadInheritedTypes(sourceType, sourceArguments, targetType.IsInterface).Any(relation =>
                relation.Definition.Id == targetType.Id
                && AreInterfaceArgumentsCompatible(
                    catalog,
                    relation.Definition,
                    relation.TypeArguments,
                    targetArguments));
        }

        // 判断一个身份表示引用类型而不是值类型。
        internal static bool IsReferenceType(MethodCatalogResult catalog, string identity, ValueSourceIndex? sources = null)
        {
            TypeIdentityTemplate parameter = new TypeIdentityTemplate(identity);
            if (sources?.ReadParameterRule(parameter) is GenericParameterRule rule)
            {
                if (sources.ReadParameterBounds(parameter).Any(bound => sources.ReadParameterRule(bound)?.RequiresReferenceType == true
                    || TryReadNamedType(bound.Text, out _, out _) && IsReferenceTypeConstraint(catalog, bound)))
                {
                    return true;
                }
                if (rule.RequiresValueType)
                {
                    return false;
                }
                throw new AnalysisException($"类型参数的引用或值类型类别尚未闭合：{identity}");
            }
            return !IsValueType(catalog, identity);
        }

        // 从标准原始类型或真实类型定义读取值类型事实。
        private static bool IsValueType(MethodCatalogResult catalog, string identity)
        {
            if (TryReadTypeSuffix(identity, out _, out string suffix) && suffix.StartsWith('['))
            {
                return false;
            }

            return ReadAssignableType(catalog, identity, out _).IsValueType;
        }

        // 判断一个确定类型能否满足 CLR 的 public new() 约束。
        private static bool HasPublicDefaultConstructor(MethodCatalogResult catalog, string identity)
        {
            if (IsValueType(catalog, identity))
            {
                return true;
            }

            TypeEntry type = ReadAssignableType(catalog, identity, out _);
            return !type.IsAbstract && catalog.GetMethods(type).Any(method =>
                method.Kind == CatalogMethodKind.Constructor
                && method.IsPublic
                && method.Parameters.Count == 0);
        }

        // 把命名或原始类型身份统一还原成真实类型定义。
        private static TypeEntry ReadAssignableType(
            MethodCatalogResult catalog,
            string identity,
            out TypeIdentityTemplate[] arguments)
        {
            if (!TryReadNamedType(identity, out string definition, out arguments))
            {
                arguments = Array.Empty<TypeIdentityTemplate>();
                return catalog.ReadPrimitiveType(identity);
            }

            TypeEntry[] types = ReadNamedTypeDefinitions(catalog, definition);
            return types.Length == 1 ? types[0] : throw new AnalysisException($"同一类型身份存在不同定义：{definition}");
        }

        // 把命名类型身份拆成开放定义和实际类型实参。
        private static bool TryReadNamedType(
            string identity,
            out string definition,
            out TypeIdentityTemplate[] arguments)
        {
            if (TryReadConstructedType(identity, out definition, out string[] values))
            {
                arguments = values.Select(value => new TypeIdentityTemplate(value)).ToArray();
                return definition.StartsWith('A');
            }

            definition = identity;
            arguments = Array.Empty<TypeIdentityTemplate>();
            return definition.StartsWith('A');
        }

        // 按开放身份读取全部物理定义，缺失时明确停止分析。
        private static TypeEntry[] ReadNamedTypeDefinitions(
            MethodCatalogResult catalog,
            string definition)
        {
            if (catalog.TypesById.TryGetValue(definition, out TypeEntry? exact))
            {
                return new[] { exact };
            }

            TypeEntry[] types = catalog.TypesByLogicalId.GetValueOrDefault(definition)?.ToArray()
                ?? Array.Empty<TypeEntry>();
            return types.Length == 0
                ? throw new AnalysisException($"类型定义尚未闭合：{definition}")
                : types;
        }

        // 按类型身份结构递归绑定其中的 CLR 类型参数。
        private static bool TryBindTypeArguments(
            string template,
            string actual,
            IDictionary<int, TypeIdentityTemplate> replacements)
        {
            if (TryReadTypeParameter(template, out int ordinal))
            {
                if (replacements.TryGetValue(ordinal, out TypeIdentityTemplate? previous))
                {
                    return TypesMatch(previous, new TypeIdentityTemplate(actual));
                }

                replacements.Add(ordinal, new TypeIdentityTemplate(actual));
                return true;
            }

            if (TypesMatch(new TypeIdentityTemplate(template), new TypeIdentityTemplate(actual)))
            {
                return true;
            }

            if (TryReadTypeSuffix(template, out string templateBody, out string templateSuffix)
                || TryReadTypeSuffix(actual, out _, out _))
            {
                return TryReadTypeSuffix(actual, out string actualBody, out string actualSuffix)
                    && templateSuffix == actualSuffix
                    && TryBindTypeArguments(templateBody, actualBody, replacements);
            }

            if (!TryReadConstructedType(template, out string templateDefinition, out string[] templateArguments)
                || !TryReadConstructedType(actual, out string actualDefinition, out string[] actualArguments)
                || templateDefinition != actualDefinition
                || templateArguments.Length != actualArguments.Length)
            {
                return false;
            }

            return templateArguments.Zip(actualArguments).All(pair =>
                TryBindTypeArguments(pair.First, pair.Second, replacements));
        }

        // 只接受一个完整的类型泛型参数标记，不把函数泛型参数混入。
        private static bool TryReadTypeParameter(string text, out int ordinal)
        {
            ordinal = -1;
            return text.Length > 1 && text[0] == '!' && text[1] != '!'
                && int.TryParse(text.AsSpan(1), out ordinal);
        }

        // 拆出数组、引用、指针或可变参数后缀，继续匹配它包住的类型。
        private static bool TryReadTypeSuffix(string text, out string body, out string suffix)
        {
            string? found = text.EndsWith("...", StringComparison.Ordinal) ? "..."
                : text.EndsWith('&') ? "&"
                : text.EndsWith('*') ? "*"
                : null;
            int bracket = text.LastIndexOf('[');
            if (found == null && bracket >= 0 && text.EndsWith(']')
                && text.AsSpan(bracket + 1, text.Length - bracket - 2).IndexOfAnyExcept(',') < 0)
            {
                found = text[bracket..];
            }

            suffix = found ?? string.Empty;
            body = found == null ? text : text[..^found.Length];
            return found != null;
        }

        // 把一个构造泛型身份拆成定义和同层实参。
        internal static bool TryReadConstructedType(
            string text,
            out string definition,
            out string[] arguments)
        {
            int opening = text.IndexOf('<');
            if (opening <= 0 || !text.EndsWith('>'))
            {
                definition = string.Empty;
                arguments = Array.Empty<string>();
                return false;
            }

            definition = text[..opening];
            List<string> result = new();
            int start = opening + 1;
            int angleDepth = 0;
            int arrayDepth = 0;
            for (int index = start; index < text.Length - 1; index++)
            {
                angleDepth += text[index] == '<' ? 1 : text[index] == '>' ? -1 : 0;
                arrayDepth += text[index] == '[' ? 1 : text[index] == ']' ? -1 : 0;
                if (text[index] == ',' && angleDepth == 0 && arrayDepth == 0)
                {
                    result.Add(text[start..index]);
                    start = index + 1;
                }
            }

            result.Add(text[start..^1]);
            arguments = result.ToArray();
            return true;
        }

        // 沿已经证明的重写与接口实现关系找到原始调用契约。
        private static bool IsImplementation(
            MethodCatalogResult catalog,
            MethodEntry method,
            IReadOnlyList<TypeIdentityTemplate> methodTypeArguments,
            ResolvedMethodDefinition contract,
            bool requireMatchingReturn,
            IReadOnlySet<string>? inheritedInterfaceOwners = null)
        {
            bool interfaceCall = catalog.TypesById[contract.Method.TypeId].IsInterface;
            Queue<ResolvedMethodDefinition> pending = new(new[] { new ResolvedMethodDefinition(method, methodTypeArguments) });
            HashSet<string> visited = new(StringComparer.Ordinal);
            while (pending.TryDequeue(out ResolvedMethodDefinition? current))
            {
                if (!visited.Add(current.Method.Id + "|" + string.Join(",", current.DeclaringTypeArguments.Select(type => type.Text))))
                {
                    continue;
                }

                if (current.Method.Id == contract.Method.Id)
                {
                    return (!interfaceCall || TypeArgumentsMatch(current.DeclaringTypeArguments, contract.DeclaringTypeArguments))
                        && MethodSignaturesMatch(catalog, method, methodTypeArguments, contract, requireMatchingReturn);
                }

                TypeEntry owner = catalog.TypesById[current.Method.TypeId];
                IReadOnlyList<MethodCatalogResult.InheritedTypeRelation> inherited = catalog.ReadInheritedTypes(owner, current.DeclaringTypeArguments, interfaceCall);
                if (interfaceCall)
                {
                    string[] explicitMatches = catalog.ReadExplicitMethodTargets(owner).Where(pair => pair.Value.Any(target =>
                            target.Method.Id == contract.Method.Id && TypeArgumentsMatch(target.DeclaringTypeArguments
                                .Select(type => type.Substitute(current.DeclaringTypeArguments)).ToArray(), contract.DeclaringTypeArguments)))
                        .Select(pair => pair.Key).ToArray();
                    if (explicitMatches.Length != 0)
                    {
                        if (explicitMatches.Contains(current.Method.Id, StringComparer.Ordinal))
                        {
                            return MethodSignaturesMatch(catalog, current.Method, current.DeclaringTypeArguments, contract, requireMatchingReturn);
                        }

                        continue;
                    }

                    if (current.Method.IsPublic && !current.Method.IsStatic && current.Method.Name == contract.Method.Name
                        && (inheritedInterfaceOwners?.Contains(owner.Id) == true
                            || inherited.Any(relation => relation.CanImplementInterface && relation.Definition.Id == contract.Method.TypeId
                                && TypeArgumentsMatch(relation.TypeArguments, contract.DeclaringTypeArguments)))
                        && MethodSignaturesMatch(catalog, current.Method, current.DeclaringTypeArguments, contract, requireMatchingReturn))
                    {
                        return true;
                    }
                }

                foreach (ResolvedMethodDefinition target in catalog.ReadExplicitMethodTargets(owner, false)
                    .GetValueOrDefault(current.Method.Id) ?? Array.Empty<ResolvedMethodDefinition>())
                {
                    pending.Enqueue(target with
                    {
                        DeclaringTypeArguments = target.DeclaringTypeArguments.Select(type => type.Substitute(current.DeclaringTypeArguments)).ToArray(),
                    });
                }

                if (current.Method.IsVirtual && !current.Method.IsNewSlot)
                {
                    foreach (var level in inherited.Where(relation => !relation.IsInterface)
                        .GroupBy(relation => relation.Depth).OrderBy(group => group.Key))
                    {
                        ResolvedMethodDefinition[] matches = level.SelectMany(relation => catalog.GetMethods(relation.Definition)
                            .Where(target => target.IsVirtual && target.Name == current.Method.Name && target.IsStatic == current.Method.IsStatic)
                            .Select(target => new ResolvedMethodDefinition(target, relation.TypeArguments)))
                            .Where(target => MethodSignaturesMatch(catalog, current.Method, current.DeclaringTypeArguments, target, false)).ToArray();
                        foreach (ResolvedMethodDefinition target in matches)
                        {
                            pending.Enqueue(target);
                        }

                        if (matches.Length != 0)
                        {
                            break;
                        }
                    }
                }
            }

            return false;
        }

        // 比较实现与契约代入声明类型实参后的完整参数签名。
        private static bool MethodSignaturesMatch(
            MethodCatalogResult catalog,
            MethodEntry method,
            IReadOnlyList<TypeIdentityTemplate> methodTypeArguments,
            ResolvedMethodDefinition contract,
            bool requireMatchingReturn)
        {
            MethodIdentityTemplate implementation = catalog.ReadResolvedMethodSignature(method.AssemblyPath!, method.MetadataToken);
            MethodIdentityTemplate declaration = catalog.ReadResolvedMethodSignature(
                contract.Method.AssemblyPath!, contract.Method.MetadataToken);
            return method.GenericArity == contract.Method.GenericArity
                && method.Parameters.Count == contract.Method.Parameters.Count
                && method.Parameters.Select(parameter => parameter.RefKind)
                    .SequenceEqual(contract.Method.Parameters.Select(parameter => parameter.RefKind))
                && TypeArgumentsMatch(implementation.Parameters.Select(parameter => parameter.Substitute(methodTypeArguments)).ToArray(),
                    declaration.Parameters.Select(parameter => parameter.Substitute(contract.DeclaringTypeArguments)).ToArray())
                && (!requireMatchingReturn
                    || TypesMatch(implementation.ReturnType.Substitute(methodTypeArguments),
                        declaration.ReturnType.Substitute(contract.DeclaringTypeArguments)));
        }

        // 参数个数确定后逐项比较，不把未指定的类型当作必定不同。
        private static bool TypeArgumentsMatch(IReadOnlyList<TypeIdentityTemplate> left, IReadOnlyList<TypeIdentityTemplate> right)
        {
            return left.Count == right.Count && left.Zip(right).All(pair => TypesMatch(pair.First, pair.Second));
        }

        // 同一变量可确认相等；其他含未指定参数的比较必须等待实际类型。
        private static bool TypesMatch(TypeIdentityTemplate left, TypeIdentityTemplate right)
        {
            if (left.Text == right.Text)
            {
                return true;
            }
            if (left.HasUnspecifiedParameter || right.HasUnspecifiedParameter)
            {
                throw new AnalysisException($"未指定类型参数参与类型比较：{left.Text} / {right.Text}");
            }
            return false;
        }
    }

    /// <summary>保存一个值所属的函数和函数内编号，防止不同函数的编号混淆。</summary>
    public readonly record struct BehaviorValueReference(
        string MethodId, int ValueId, int InstanceId = 0);

    /// <summary>同一函数体在一组确定的类和函数泛型实参下参与调用分析。</summary>
    internal sealed record MethodCallInstance(
        int Id,
        string MethodId,
        IReadOnlyList<TypeIdentityTemplate> TypeArguments,
        IReadOnlyList<TypeIdentityTemplate> MethodArguments,
        int ParentId,
        ResolvedCallTarget? Binding,
        BehaviorFlowPoint? InvocationPoint,
        int RootId)
    {
        // 在一个使用点代入所属类和函数的类型实参。
        internal TypeIdentityTemplate Substitute(TypeIdentityTemplate type)
        {
            return type.Substitute(this.TypeArguments, this.MethodArguments);
        }

        // 保留类型使用点的完整身份，并同步更新开放定义和构造参数。
        internal BehaviorTypeReference Substitute(BehaviorTypeReference type)
        {
            TypeIdentityTemplate identity = Substitute(type.Identity);
            TypeIdentityTemplate definition = Substitute(type.DefinitionIdentity);
            TypeIdentityTemplate[] arguments = type.ArgumentIdentities.Select(Substitute).ToArray();
            if (type.DefinitionId.StartsWith('!')
                && CallTargetResolver.TryReadConstructedType(identity.Text, out string name, out string[] values))
            {
                definition = new TypeIdentityTemplate(name);
                arguments = values.Select(value => new TypeIdentityTemplate(value)).ToArray();
            }

            return new BehaviorTypeReference(identity, definition, arguments,
                type.TargetAssemblyIdentity, type.ReferringAssemblyPath, type.KnownTypeId);
        }
    }

    /// <summary>保存一个已追到来源的值及它所属的函数。</summary>
    public sealed record ValueOrigin(BehaviorValueReference Reference, BehaviorValue Value)
    {
        internal ReturnedValuePath? ReturnPath { get; init; }
    }

    // 把返回对象与真实传出它的出口绑定，供调用者观察容器内容时复用。
    internal sealed record ReturnedValuePath(int InstanceId, BehaviorFlowPoint ReturnPoint, BehaviorFlowPoint ValuePoint, ReturnedValuePath? Previous);

    /// <summary>按实际读取位置追踪变量来源，供调用解析和行为判断共同使用。</summary>
    public sealed class ValueSourceIndex
    {
        private readonly MethodCatalogResult m_catalog;
        private readonly Dictionary<string, MethodEntry> m_definitions;
        private readonly Dictionary<string, MethodBehavior> m_methods;
        private readonly Dictionary<(BehaviorValueReference Reference, bool RetainTypeChecks, bool RetainSlots), IReadOnlyList<ValueOrigin>> m_origins = new();
        private readonly Dictionary<string, ValueFlowGraph> m_flowGraphs = new(StringComparer.Ordinal);
        private readonly Dictionary<StorageLocation, BehaviorValueReference> m_defaultFieldValues = new();
        private readonly HashSet<(string Method, int Block)> m_singleAllocations = new();
        private readonly HashSet<(string Method, int Value)> m_activeAddressQueries = new();
        private readonly Dictionary<int, HashSet<int>> m_unreachableBlocks = new();
        private readonly Dictionary<int, HashSet<(int From, int To)>> m_excludedEdges = new();
        private readonly HashSet<int> m_inactiveInstances = new();
        private readonly Dictionary<BehaviorValueReference, (long Value, int Bits)?> m_integerValues = new();
        private readonly Dictionary<string, (long Value, int Bits)?> m_initializedArrayLengths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (bool Independent, ValueSourceIndex? Values)> m_initializations = new(StringComparer.Ordinal);
        private long m_integerCycles;
        private readonly Dictionary<(string MethodId, int ValueId, int InstanceId), ResolvedCall> m_resultCalls = new();
        private readonly Dictionary<BehaviorValueReference, IReadOnlyList<ValueOrigin>> m_runtimeValues = new();
        private readonly Dictionary<(BehaviorValueReference Reference, BehaviorFlowPoint Point, int Instance), BehaviorValueReference> m_addressReads = new();
        private readonly Dictionary<(BehaviorValueReference Reference, BehaviorFlowPoint Point, int Index), BehaviorValueReference> m_arrayReads = new();
        private readonly Dictionary<(BehaviorValueReference Reference, int Instance, ReturnedValuePath? Path), BehaviorValueReference> m_observedValues = new();
        private int m_nextRuntimeValueId = -1;
        private readonly Dictionary<int, List<BehaviorWrite>> m_runtimeWrites = new();
        private readonly Dictionary<int, Dictionary<BehaviorFlowPoint, ResolvedCall>> m_callsByCallerInstance = new();
        private readonly Dictionary<string, MethodCallInstance> m_instancesByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<int, MethodCallInstance> m_instances = new();
        private readonly Dictionary<string, MethodCallInstance> m_rootInstances = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Method, bool Conditions), RecursiveDependencies> m_recursiveDependencies = new();
        private readonly Dictionary<TypeIdentityTemplate, GenericParameterRule> m_parameterRules = new();
        private readonly HashSet<(BehaviorValueReference Reference, int Stop, bool Types, bool Storage)> m_activeOriginQueries = new();
        private readonly Dictionary<(BehaviorValueReference Reference, int Stop, bool Types, bool Storage), IReadOnlyList<ValueOrigin>> m_originResults = new();
        private readonly HashSet<(BehaviorValueReference Reference, int Stop, bool Types, bool Storage)> m_completedOriginQueries = new();
        private readonly HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)> m_originClosedPrefixes = new();
        private bool m_originCycle;
        private readonly HashSet<StorageEffectQuery> m_activeStorageEffects = new();
        private readonly HashSet<(int Instance, BehaviorWriteKind Kind, string? Member, bool Slot)> m_storageReadOnlyInstances = new();
        private readonly Stack<Dictionary<(BehaviorValueReference Reference, int Stop, bool Types, bool Storage), IReadOnlyList<ValueOrigin>>> m_storageOriginResults = new();
        private static readonly BehaviorValueReference s_previousStorageValue = new(string.Empty, -1);
        private static readonly BehaviorValueReference s_defaultObjectMember = new(string.Empty, -2);

        // 共用已发现的函数签名，区分普通参数槽与引用参数所指向的存储。
        internal ValueSourceIndex(
            MethodCatalogResult catalog,
            Dictionary<string, MethodEntry> definitions,
            Dictionary<string, MethodBehavior> behaviors)
        {
            this.m_catalog = catalog;
            this.m_definitions = definitions;
            this.m_methods = behaviors;
            this.m_runtimeValues.Add(s_defaultObjectMember, new[] { new ValueOrigin(s_defaultObjectMember,
                new BehaviorValue(-2, BehaviorValueKind.Constant, "default", null, Array.Empty<int>())) });
        }

        internal Dictionary<string, MethodEntry> Definitions => this.m_definitions;
        internal Dictionary<string, MethodBehavior> Behaviors => this.m_methods;

        // 为已确定需要新建的调用点保存独立环境。
        internal MethodCallInstance GetInstance(
            string methodId, IReadOnlyList<string> typeArguments, IReadOnlyList<string> methodArguments,
            int parentId = 0, ResolvedCallTarget? binding = null,
            BehaviorFlowPoint? invocationPoint = null)
        {
            string bindingKey = binding == null ? string.Empty
                : ReadReferenceKey(binding.Receiver) + "|"
                    + string.Join("|", binding.Arguments.Select(ReadReferenceKey));
            string key = methodId + "\n" + string.Join("\n", typeArguments) + "\n|\n"
                + string.Join("\n", methodArguments) + $"\nP:{parentId}\n"
                + $"C:{invocationPoint?.BlockId}:{invocationPoint?.Order}\n{bindingKey}";
            if (!this.m_instancesByKey.TryGetValue(key, out MethodCallInstance? instance))
            {
                instance = new MethodCallInstance(this.m_instances.Count + 1, methodId,
                    typeArguments.Select(value => new TypeIdentityTemplate(value)).ToArray(),
                    methodArguments.Select(value => new TypeIdentityTemplate(value)).ToArray(), parentId,
                    binding, invocationPoint, parentId == 0 ? this.m_instances.Count + 1 : this.m_instances[parentId].RootId);
                this.m_instancesByKey.Add(key, instance);
                this.m_instances.Add(instance.Id, instance);
                if (parentId == 0)
                {
                    this.m_rootInstances.Add(methodId, instance);
                    MethodEntry method = this.m_definitions[methodId];
                    IEnumerable<(TypeIdentityTemplate Parameter, GenericParameterRule Rule)> parameters = instance.TypeArguments
                        .Zip(this.m_catalog.TypesById[method.TypeId].GenericParameters, (parameter, rule) => (parameter, rule))
                        .Concat(instance.MethodArguments.Zip(method.GenericParameters, (parameter, rule) => (parameter, rule)));
                    foreach (var parameter in parameters)
                    {
                        AddParameterRule(parameter.Parameter, parameter.Rule with
                        { TypeConstraints = parameter.Rule.TypeConstraints.Select(instance.Substitute).ToArray() });
                    }
                }
            }

            this.m_inactiveInstances.Remove(instance.Id);
            return instance;
        }

        // 只在相关实参的真实来源相同后复用递归祖先；待闭合时不创建半成品实例。
        internal bool TryFindRecursiveInstance(ResolvedCallTarget binding, int parentId, BehaviorFlowPoint point, out MethodCallInstance? match)
        {
            match = null;
            HashSet<string>? inputs = null;
            for (int ancestorId = parentId; ancestorId != 0; ancestorId = this.m_instances[ancestorId].ParentId)
            {
                MethodCallInstance ancestor = this.m_instances[ancestorId];
                if (ancestor.MethodId != binding.MethodId)
                {
                    continue;
                }
                if (!ancestor.TypeArguments.Select(type => type.Text).SequenceEqual(binding.DeclaringTypeArguments)
                    || !ancestor.MethodArguments.Select(type => type.Text).SequenceEqual(binding.Reference.GenericArgumentTypeIds))
                {
                    throw new AnalysisException($"递归类型实参变化尚未闭合：{binding.MethodId}");
                }
                bool? onlyReads = HasOnlyReads(ancestor.Id);
                if (onlyReads == null)
                {
                    return false;
                }
                if (onlyReads.Value)
                {
                    match = ancestor;
                    return true;
                }
                inputs ??= ReadRecursiveInputs(binding, ancestor);
                HashSet<string>? previous = ReadRecursiveInputs(ancestor.Binding, ancestor);
                if (inputs == null || previous == null)
                {
                    return false;
                }
                if (inputs.SetEquals(previous))
                {
                    HashSet<string>? currentStorage = ReadRecursiveInputs(binding, ancestor, true, parentId, point);
                    HashSet<string>? previousStorage = ReadRecursiveInputs(ancestor.Binding, ancestor, true);
                    if (currentStorage == null || previousStorage == null)
                    {
                        return false;
                    }
                    if (currentStorage.SetEquals(previousStorage))
                    {
                        match = ancestor;
                        return true;
                    }
                }
            }
            return true;
        }

        // 只有整个确定调用闭包均无写入、分配或动态目标时，才能证明换对象不影响真实行为。
        internal bool? HasOnlyReads(int instanceId)
        {
            Queue<int> pending = new(new[] { instanceId });
            HashSet<string> visited = new(StringComparer.Ordinal);
            bool unread = false;
            while (pending.TryDequeue(out int current))
            {
                string methodId = GetInstance(current).MethodId;
                if (!visited.Add(methodId))
                {
                    continue;
                }
                if (!this.m_methods.TryGetValue(methodId, out MethodBehavior? body))
                {
                    unread = true;
                    continue;
                }
                if (body.BodyKind != MethodBodyKind.Executable || body.Writes.Count != 0
                    || this.m_runtimeWrites.GetValueOrDefault(current)?.Count > 0
                    || body.Values.Any(value => value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.NewArray
                        or BehaviorValueKind.Function or BehaviorValueKind.Address or BehaviorValueKind.Type
                        || value.Kind == BehaviorValueKind.Conversion && value.Reference == "box"))
                {
                    return false;
                }
                foreach (BehaviorCall call in body.Calls)
                {
                    if (call.Kind is not (BehaviorCallKind.Direct or BehaviorCallKind.Virtual))
                    {
                        return false;
                    }
                    MethodEntry target = this.m_catalog.ResolveMethodDefinition(call.Target, true).Method;
                    ResolvedCall? resolved = this.m_callsByCallerInstance.GetValueOrDefault(current)?
                        .GetValueOrDefault(call.Point);
                    bool virtualDispatch = call.Kind == BehaviorCallKind.Virtual && target.IsVirtual && !target.IsFinal;
                    if (resolved == null)
                    {
                        unread |= virtualDispatch || !visited.Contains(target.Id);
                    }
                    else if (virtualDispatch ? !resolved.CoversDeclaredReceivers
                        : resolved.Targets.Count != 1 || resolved.Targets[0].MethodId != target.Id)
                    {
                        return false;
                    }
                    else
                    {
                        foreach (ResolvedCallTarget binding in resolved.Targets)
                        {
                            pending.Enqueue(binding.InstanceId);
                        }
                    }
                }
            }
            return unread ? null : true;
        }

        // 从目标、存储及返回用途反向查找参数；自递归只传播已相关的参数位置。
        private RecursiveDependencies ReadRecursiveDependencies(string methodId, bool includeConditions)
        {
            if (this.m_recursiveDependencies.TryGetValue((methodId, includeConditions), out RecursiveDependencies? cached))
            {
                return cached;
            }
            MethodBehavior body = this.m_methods[methodId];
            BehaviorCall[] selfCalls = body.Calls.Where(call => call.Kind == BehaviorCallKind.Direct &&
                this.m_catalog.ResolveMethodDefinition(call.Target, true).Method.Id == methodId).ToArray();
            HashSet<int> parameters = new();
            HashSet<int> storageReads = new();
            HashSet<(int ValueId, bool Storage)> visited = new();
            Queue<(int ValueId, bool Storage)> pending = new(body.Writes.Where(write => write.ReceiverValueId.HasValue)
                .Select(write => write.ReceiverValueId!.Value)
                .Concat(body.Returns.Where(returned => returned.ValueId.HasValue).Select(returned => returned.ValueId!.Value))
                .Concat(body.Writes.SelectMany(write => write.IndexValueIds))
                .Concat(body.Blocks.Where(block => includeConditions && block.ConditionValueId.HasValue).Select(block => block.ConditionValueId!.Value))
                .Concat(body.Values.Where(value => value.Kind is BehaviorValueKind.NewArray or BehaviorValueKind.Function)
                    .SelectMany(value => value.InputValueIds))
                .Concat(body.Calls.Except(selfCalls).SelectMany(call => call.Arguments.Select(argument => argument.ValueId)
                    .Concat(call.ReceiverValueId is int receiver ? new[] { receiver } : Array.Empty<int>())))
                .Select(valueId => (valueId, true)).Concat(body.Writes.Select(write => (write.ValueId, false))));
            while (pending.TryDequeue(out var item))
            {
                if (!visited.Add(item))
                {
                    continue;
                }
                BehaviorValue value = body.Values[item.ValueId];
                if (item.Storage && value.Kind is BehaviorValueKind.FieldRead or BehaviorValueKind.ArrayElementRead or BehaviorValueKind.CallResult)
                {
                    storageReads.Add(value.Id);
                }
                if (value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance)
                {
                    int parameter = value.ParameterIndex ?? -1;
                    if (parameters.Add(parameter))
                    {
                        foreach (BehaviorCall call in selfCalls)
                        {
                            pending.Enqueue((parameter < 0 ? call.ReceiverValueId!.Value : call.Arguments[parameter].ValueId, true));
                        }
                    }
                }
                foreach (int input in value.Kind == BehaviorValueKind.SlotRead
                             ? ReadReachingValues(body, value.InputValueIds.Single(), value.Point!.Value) : value.InputValueIds)
                {
                    pending.Enqueue((input, item.Storage));
                }
            }
            RecursiveDependencies dependencies = new(parameters, storageReads);
            this.m_recursiveDependencies.Add((methodId, includeConditions), dependencies);
            return dependencies;
        }

        private sealed record RecursiveDependencies(HashSet<int> Parameters, HashSet<int> StorageReads);

        // 比较实际对象身份及常量，不把计算式或尚未确定的调用返回值当作相同状态。
        private HashSet<string>? ReadRecursiveInputs(ResolvedCallTarget? binding, MethodCallInstance instance,
            bool includeStorage = false, int? parentId = null, BehaviorFlowPoint point = default)
        {
            HashSet<string> result = new(StringComparer.Ordinal);
            MethodBehavior body = this.m_methods[instance.MethodId];
            RecursiveDependencies dependencies = ReadRecursiveDependencies(instance.MethodId, this.m_unreachableBlocks.GetValueOrDefault(instance.Id)?.Count > 0);
            List<IEnumerable<BehaviorValueReference>> inputs = dependencies.Parameters.Order().Select(parameter => binding != null
                ? parameter < 0 ? binding.Receiver : binding.Arguments[parameter]
                : body.Values.Where(value => parameter < 0 ? value.Kind == BehaviorValueKind.CurrentInstance
                    : value.Kind == BehaviorValueKind.Parameter && value.ParameterIndex == parameter)
                    .Select(value => new BehaviorValueReference(instance.MethodId, value.Id, instance.Id))).ToList();
            foreach (int readId in includeStorage ? dependencies.StorageReads.Order() : Enumerable.Empty<int>())
            {
                foreach (ValueOrigin dependency in GetRelativeOrigins(new(instance.MethodId, readId, instance.Id), 0, retainStorageReads: true))
                {
                    BehaviorValue read = dependency.Value;
                    BehaviorValueReference reference = dependency.Reference;
                    if (read.Kind is not (BehaviorValueKind.FieldRead or BehaviorValueKind.ArrayElementRead))
                    {
                        inputs.Add(new[] { reference });
                        continue;
                    }
                    IReadOnlyList<StorageLocation>? locations = ReadStorageLocations(reference, read.Member, read.InputValueIds.FirstOrDefault(-1),
                        read.Kind == BehaviorValueKind.ArrayElementRead ? read.InputValueIds.Skip(1).ToArray() : Array.Empty<int>());
                    if (locations == null)
                    {
                        return null;
                    }
                    MethodCallInstance at = parentId.HasValue ? GetInstance(parentId.Value) : instance;
                    BehaviorWriteKind kind = read.Kind == BehaviorValueKind.FieldRead ? BehaviorWriteKind.Field : BehaviorWriteKind.ArrayElement;
                    foreach (StorageLocation location in locations)
                    {
                        if (location.Receiver is BehaviorValueReference receiver)
                        {
                            foreach (ValueOrigin origin in GetCallOrigins(receiver))
                            {
                                RequireNonRecursiveAllocation(origin, instance);
                            }
                        }
                        Func<IReadOnlyList<BehaviorValueReference>> before = () => ReadStorageValuesBeforeInvocation(at, kind, read.Member, new[] { location }, reference);
                        inputs.Add(parentId.HasValue ? ReadStoredValuesAtPoint(at, kind, read.Member, new[] { location }, point, -1,
                            ReadPendingStorageValue(reference, point, at.Id), before) : before());
                    }
                }
            }
            int ambiguousParameters = 0;
            foreach (var (references, parameter) in inputs.Select((references, index) => (references, index)))
            {
                ValueOrigin[] origins = references.SelectMany(reference => GetCallOrigins(reference)).ToArray();
                if (origins.Length == 0 || origins.Any(origin => origin.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return null;
                }
                if (origins.Length > 1 && ++ambiguousParameters > 1)
                {
                    throw new AnalysisException($"递归实参的分支对应尚未闭合：{instance.MethodId}");
                }
                foreach (ValueOrigin origin in origins)
                {
                    RequireNonRecursiveAllocation(origin, instance);
                    if (origin.Value.Kind is not (BehaviorValueKind.Constant or BehaviorValueKind.NewObject or BehaviorValueKind.NewArray
                        or BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance or BehaviorValueKind.Type))
                    {
                        throw new AnalysisException($"递归实参来源尚未闭合：{instance.MethodId}；位置 {parameter}；{origin.Value.Kind}");
                    }
                    result.Add(parameter + ":" + (origin.Value.Kind == BehaviorValueKind.Constant
                        ? $"{origin.Value.Kind}:{origin.Value.Type?.Id}:{origin.Value.Reference}"
                        : origin.Value.Kind == BehaviorValueKind.Type
                            ? $"{origin.Value.Kind}:{this.m_catalog.ResolveTypeDefinition(origin.Value.Type!).Id}:{origin.Value.Type!.Identity.Text}"
                        : ReadReferenceKey(new[] { origin.Reference })));
                }
            }
            return result;
        }

        // 递归本轮产生的对象不能作为下一轮仍相同的对象或存储地址。
        private void RequireNonRecursiveAllocation(ValueOrigin origin, MethodCallInstance instance)
        {
            for (int owner = origin.Value.Kind is BehaviorValueKind.NewObject or BehaviorValueKind.NewArray
                     ? origin.Reference.InstanceId : 0; owner != 0; owner = GetInstance(owner).ParentId)
            {
                if (owner == instance.Id)
                {
                    throw new AnalysisException($"递归创建对象的存储身份尚未闭合：{instance.MethodId}");
                }
            }
        }

        // 把逐位置值引用编码成调用实例键的一部分。
        internal static string ReadReferenceKey(IEnumerable<BehaviorValueReference> references)
        {
            return string.Concat(references.Select(reference =>
            {
                string key = $"{reference.MethodId}:{reference.ValueId}:{reference.InstanceId}";

                return $"{key.Length}:{key}";
            }));
        }

        // 取得一个已登记的构造函数环境，不从相同函数的其他调用猜选。
        internal MethodCallInstance GetInstance(int instanceId)
        {
            return this.m_instances[instanceId];
        }

        // 取得当前作用域中由真实声明保证的类型参数约束，不为未知参数补规则。
        internal GenericParameterRule? ReadParameterRule(TypeIdentityTemplate parameter)
        {
            return this.m_parameterRules.GetValueOrDefault(parameter);
        }

        // 只登记新作用域内的参数声明，不改写调用者已经存在的约束。
        internal void AddParameterRule(TypeIdentityTemplate parameter, GenericParameterRule rule)
        {
            if (!this.m_parameterRules.TryAdd(parameter, rule)
                && this.m_parameterRules[parameter] is GenericParameterRule previous
                && (previous with { TypeConstraints = Array.Empty<TypeIdentityTemplate>() } != rule with { TypeConstraints = Array.Empty<TypeIdentityTemplate>() }
                    || !previous.TypeConstraints.SequenceEqual(rule.TypeConstraints)))
            {
                throw new AnalysisException($"同一类型参数的声明约束冲突：{parameter.Text}");
            }
        }

        // 受限引用类型调用读取地址中的对象，仍使用原位置的赋值快照。
        internal IReadOnlyList<BehaviorValueReference> ReadAddressedValues(BehaviorValueReference reference, BehaviorFlowPoint point)
        {
            return GetOrigins(reference).SelectMany(origin =>
            {
                MethodBehavior body = this.m_methods[origin.Reference.MethodId];
                MethodCallInstance instance = GetInstance(origin.Reference.InstanceId);
                BehaviorFlowPoint usePoint = point;
                if (origin.Value.Kind == BehaviorValueKind.Address && (origin.Value.Member != null
                    || origin.Value.InputValueIds.Count == 2 && origin.Value.Type != null))
                {
                    var key = (origin.Reference, usePoint, instance.Id);
                    if (!this.m_addressReads.TryGetValue(key, out BehaviorValueReference read))
                    {
                        read = origin.Reference with { ValueId = --this.m_nextRuntimeValueId };
                        this.m_addressReads.Add(key, read);
                    }
                    if (!this.m_runtimeValues.ContainsKey(read))
                    {
                        BindRuntimeValue(read, new[] { new ValueOrigin(read, origin.Value with
                        { Id = read.ValueId, Kind = origin.Value.Member != null ? BehaviorValueKind.FieldRead : BehaviorValueKind.ArrayElementRead, Point = usePoint }) });
                    }
                    return new[] { read };
                }
                IReadOnlyList<int> slots = origin.Value.Kind is BehaviorValueKind.Address or BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance
                    ? ReadAddressedSlots(body, origin.Reference.ValueId) : Array.Empty<int>();
                if (slots.Count == 0)
                {
                    return new[] { origin.Reference };
                }
                BehaviorValueReference unresolved = ReadPendingStorageValue(origin.Reference, usePoint);
                IReadOnlyList<StorageLocation>? locations = ReadStorageLocations(origin.Reference, null, origin.Reference.ValueId, Array.Empty<int>());
                return locations == null ? new[] { unresolved }
                    : ReadStoredValuesAtPoint(instance, BehaviorWriteKind.Indirect, null, locations, usePoint, -1, unresolved,
                        () => slots.Select(slot => origin.Reference with { ValueId = slot < 0 ? origin.Reference.ValueId : slot }).ToArray())
                        .SelectMany(value => value == origin.Reference && origin.Value.Kind == BehaviorValueKind.Parameter
                            && instance.Binding != null
                            ? instance.Binding.Arguments[origin.Value.ParameterIndex!.Value].SelectMany(input =>
                                ReadAddressedValues(input, instance.InvocationPoint!.Value))
                            : new[] { value });
            }).Distinct().ToArray();
        }

        // 按当前使用位置读取数组元素，反射实参与返回对象共用普通存储查询。
        internal IReadOnlyList<IReadOnlyList<BehaviorValueReference>>? ReadArrayElements(BehaviorValueReference reference, BehaviorFlowPoint point)
        {
            IReadOnlyList<ValueOrigin> arrays = GetCallOrigins(reference);
            if (arrays.Any(array => array.Value.Kind == BehaviorValueKind.CallResult))
            {
                return null;
            }
            if (arrays.Count == 1 && arrays[0].Value.Kind == BehaviorValueKind.Constant && arrays[0].Value.Reference == null)
            {
                return Array.Empty<IReadOnlyList<BehaviorValueReference>>();
            }
            if (arrays.Count != 1 || arrays[0].Value.Kind != BehaviorValueKind.NewArray || arrays[0].Value.InputValueIds.Count != 1)
            {
                throw new AnalysisException($"数组来源尚未闭合：{reference.MethodId} @ {point.BlockId}");
            }
            ValueOrigin array = arrays[0];
            IReadOnlyList<ValueOrigin> lengths = GetCallOrigins(array.Reference with { ValueId = array.Value.InputValueIds.Single() });
            if (lengths.Any(length => length.Value.Kind == BehaviorValueKind.CallResult))
            {
                return null;
            }
            if (lengths.Count != 1 || lengths[0].Value.Kind != BehaviorValueKind.Constant
                || !int.TryParse(lengths[0].Value.Reference, out int count) || count < 0)
            {
                throw new AnalysisException($"数组长度尚未闭合：{reference.MethodId} @ {point.BlockId}");
            }
            List<IReadOnlyList<BehaviorValueReference>> arguments = new();
            for (int index = 0; index < count; index++)
            {
                if (!this.m_arrayReads.TryGetValue((reference, point, index), out BehaviorValueReference read))
                {
                    BehaviorValueReference subscript = reference with { ValueId = --this.m_nextRuntimeValueId };
                    BindRuntimeValue(subscript, new[] { new ValueOrigin(subscript, new BehaviorValue(subscript.ValueId,
                        BehaviorValueKind.Constant, index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, Array.Empty<int>())) });
                    read = reference with { ValueId = --this.m_nextRuntimeValueId };
                    BindRuntimeValue(read, new[] { new ValueOrigin(read, new BehaviorValue(read.ValueId,
                        BehaviorValueKind.ArrayElementRead, null, null, new[] { reference.ValueId, subscript.ValueId }) { Point = point }) });
                    this.m_arrayReads.Add((reference, point, index), read);
                }
                IReadOnlyList<ValueOrigin> values = GetCallOrigins(read);
                if (values.Any(value => value.Value.Kind == BehaviorValueKind.CallResult))
                {
                    return null;
                }
                arguments.Add(new[] { read });
            }
            return arguments;
        }

        // 沿真实调用收集容器字段和数组位置，再共用存储查询读取返回时的内容。
        internal IReadOnlyList<BehaviorValueReference> ReadContainerValues(
            BehaviorValueReference reference, ValueOrigin allocated, BehaviorFlowPoint point, Action<string> recordFailure,
            BehaviorFlowPoint? throughPoint = null)
        {
            Queue<int> pending = new(new[] { reference.InstanceId });
            HashSet<int> visited = new();
            Dictionary<StorageLocation, (BehaviorMemberReference? Member, BehaviorValueReference Written)> members = new();
            while (pending.TryDequeue(out int instanceId))
            {
                MethodCallInstance instance = GetInstance(instanceId);
                if (!visited.Add(instanceId) || !this.m_methods.ContainsKey(instance.MethodId))
                {
                    continue;
                }
                foreach (BehaviorWrite write in GetWrites(instanceId).Where(write => write.Kind is BehaviorWriteKind.Field or BehaviorWriteKind.ArrayElement or BehaviorWriteKind.Indirect
                    && write.ReceiverValueId.HasValue))
                {
                    BehaviorValueReference written = new(instance.MethodId, write.ValueId, instanceId);
                    try
                    {
                        IReadOnlyList<StorageLocation>? locations = ReadStorageLocations(written, write.Member, write.ReceiverValueId!.Value, write.IndexValueIds,
                            expectedReceivers: new BehaviorValueReference?[] { allocated.Reference })
                            ?? throw new AnalysisException($"返回容器的存储写入位置尚未闭合：{instance.MethodId} @ {write.Position}");
                        foreach (StorageLocation location in locations)
                        {
                            if (location.Receiver == allocated.Reference && location.Definition != "slot")
                            {
                                members.TryAdd(location, (write.Member, written));
                            }
                        }
                    }
                    catch (AnalysisException exception)
                    {
                        recordFailure(exception.Message);
                    }
                }
                foreach (ResolvedCallTarget target in (this.m_callsByCallerInstance.GetValueOrDefault(instanceId)?.Values ?? Enumerable.Empty<ResolvedCall>())
                             .SelectMany(call => call.Targets))
                {
                    pending.Enqueue(target.InstanceId);
                }
            }
            HashSet<BehaviorValueReference> values = new();
            foreach (var item in members)
            {
                try
                {
                    values.UnionWith(ReadStoredValuesAtPoint(GetInstance(reference.InstanceId),
                        item.Key.Definition.Length == 0 ? BehaviorWriteKind.ArrayElement : BehaviorWriteKind.Field,
                        item.Value.Member, new[] { item.Key }, point, -1, ReadPendingStorageValue(item.Value.Written, point, reference.InstanceId),
                        () => new[] { s_defaultObjectMember }, throughPoint: throughPoint, returnedPath: allocated.ReturnPath)
                        .Select(value => ObserveValue(value, reference.InstanceId, allocated.ReturnPath)));
                }
                catch (AnalysisException exception)
                {
                    recordFailure(exception.Message);
                }
            }
            return values.ToArray();
        }

        // 保留值原有身份与绑定，但把后续容器内容的观察位置放到实际返回的调用者中。
        private BehaviorValueReference ObserveValue(BehaviorValueReference reference, int instanceId, ReturnedValuePath? path)
        {
            if (reference.InstanceId == 0 || reference.InstanceId == instanceId && path == null)
            {
                return reference;
            }
            if (!this.m_observedValues.TryGetValue((reference, instanceId, path), out BehaviorValueReference observed))
            {
                observed = new BehaviorValueReference(GetInstance(instanceId).MethodId, --this.m_nextRuntimeValueId, instanceId);
                this.m_observedValues.Add((reference, instanceId, path), observed);
            }
            if (!this.m_runtimeValues.ContainsKey(observed))
            {
                BindRuntimeValue(observed, ReadOrigins(reference, true, true).Select(origin =>
                    origin with { ReturnPath = MergeReturnPaths(origin.ReturnPath, path) }).ToArray());
            }
            return observed;
        }

        // 沿类型参数之间的声明约束查找所有已知上界，循环约束只访问一次。
        internal IEnumerable<TypeIdentityTemplate> ReadParameterBounds(TypeIdentityTemplate parameter)
        {
            Queue<TypeIdentityTemplate> pending = new(new[] { parameter });
            HashSet<TypeIdentityTemplate> visited = new();
            while (pending.TryDequeue(out TypeIdentityTemplate? current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }
                yield return current;
                if (ReadParameterRule(current) is GenericParameterRule rule)
                {
                    foreach (TypeIdentityTemplate bound in rule.TypeConstraints)
                    {
                        pending.Enqueue(bound);
                    }
                }
            }
        }

        // 取得作为独立分析入口登记的函数环境。
        internal IReadOnlyDictionary<string, MethodCallInstance> RootInstances => this.m_rootInstances;

        internal IEnumerable<MethodCallInstance> Instances => this.m_instances.Values.Where(instance => !this.m_inactiveInstances.Contains(instance.Id));

        // 先固定每个条件的证明状态，随后只按证据之前的执行位置检查。
        internal IReadOnlyDictionary<(int Instance, int Block), string> ReadConditionFailures(IEnumerable<MethodCallInstance> instances)
        {
            Dictionary<(int Instance, int Block), string> failures = new();
            foreach (MethodCallInstance instance in instances)
            {
                if (!this.m_methods.TryGetValue(instance.MethodId, out MethodBehavior? body))
                {
                    continue;
                }
                foreach (BehaviorFlowBlock block in body.Blocks.Where(block => block.ConditionValueId.HasValue && IsReachable(instance.Id, block.Id)))
                {
                    string? failure = ReadConditionFailure(new BehaviorValueReference(instance.MethodId, block.ConditionValueId!.Value, instance.Id));
                    if (failure != null)
                    {
                        failures.Add((instance.Id, block.Id), failure);
                    }
                }
            }
            return failures;
        }

        // 保存一个选择条件尚未闭合的具体原因，不把查询失败隐藏成纯函数。
        private string? ReadConditionFailure(BehaviorValueReference condition)
        {
            HashSet<BehaviorValueReference> needed = new(), visited = new();
            Queue<BehaviorValueReference> pending = new(new[] { condition });
            try
            {
                while (pending.TryDequeue(out BehaviorValueReference reference))
                {
                    if (!visited.Add(reference))
                    {
                        continue;
                    }
                    foreach (ValueOrigin origin in GetCallOrigins(reference, retainTypeChecks: true))
                    {
                        if (origin.Value.Kind == BehaviorValueKind.CallResult)
                        {
                            needed.Add(origin.Reference);
                        }
                        else if (origin.Value.Kind is BehaviorValueKind.Computation or BehaviorValueKind.Conversion or BehaviorValueKind.NewArray)
                        {
                            foreach (int input in origin.Value.InputValueIds)
                            {
                                pending.Enqueue(origin.Reference with { ValueId = input });
                            }
                        }
                    }
                }
                return needed.Count == 0 ? null : $"分支或对象来源仍缺少调用结果：{needed.First()}";
            }
            catch (AnalysisException exception)
            {
                return exception.Message;
            }
        }

        // 只使用前一轮独立证明的路径读条件，整批计算完才发布新路径，避免条件证明自己。
        internal HashSet<int> RefineReachability(IReadOnlySet<int>? provenSetterRoots = null)
        {
            this.m_integerValues.Clear();
            List<(int Instance, HashSet<int> Blocks, HashSet<(int From, int To)> Edges)> changes = new();
            HashSet<int> reentered = this.m_callsByCallerInstance.Values.SelectMany(calls => calls.Values)
                .SelectMany(call => call.Targets.Where(target => GetInstance(target.InstanceId).ParentId != call.CallerInstanceId
                    || GetInstance(target.InstanceId).InvocationPoint != call.Call.Point).Select(target => target.InstanceId)).ToHashSet();
            using IntegerPathProof proof = new(this, reentered);
            foreach (IGrouping<string, MethodCallInstance> group in this.Instances.Where(instance => provenSetterRoots?.Contains(instance.RootId) != true)
                         .GroupBy(instance => instance.MethodId))
            {
                if (!this.m_methods.TryGetValue(group.Key, out MethodBehavior? body)
                    || body.BodyKind != MethodBodyKind.Executable || body.ExceptionHandlers.Count != 0)
                {
                    continue;
                }
                BehaviorFlowBlock[] conditionalBlocks = body.Blocks.Where(block => block.ConditionValueId.HasValue).ToArray();
                IReadOnlyDictionary<int, BehaviorFlowBlock> blocks = ReadValueFlowGraph(body).Blocks;
                foreach (MethodCallInstance instance in group)
                {
                    Dictionary<int, bool> conditions = new();
                    foreach (BehaviorFlowBlock block in conditionalBlocks.Where(block => IsReachable(instance.Id, block.Id)))
                    {
                        try
                        {
                            if (ReadIntegerOperation(instance.Id, body.Values[block.ConditionValueId!.Value], new()) is { } condition)
                            {
                                conditions.Add(block.Id, condition.Value != 0);
                            }
                        }
                        catch (AnalysisException)
                        {
                            // 条件缺少证明时两条原始边均保留；其真实调用和失败仍由原流程检查。
                        }
                    }
                    HashSet<int> stops = (this.m_callsByCallerInstance.GetValueOrDefault(instance.Id)?.Values ?? Enumerable.Empty<ResolvedCall>())
                        .Where(call => !call.CompletesWithoutTarget && call.Targets.All(target => this.m_methods.TryGetValue(target.MethodId, out MethodBehavior? targetBody)
                            && targetBody.BodyKind == MethodBodyKind.Executable
                            && !targetBody.Returns.Any(result => IsReachable(target.InstanceId, result.Point.BlockId))))
                        .Select(call => call.Call.Point.BlockId).ToHashSet();
                    HashSet<(int From, int To)> impossible = new();
                    if (conditionalBlocks.Any(block => !conditions.ContainsKey(block.Id) && IsReachable(instance.Id, block.Id)))
                    {
                        proof.ClearQuery();
                        foreach (BehaviorFlowBlock block in conditionalBlocks.Where(block => !conditions.ContainsKey(block.Id) && IsReachable(instance.Id, block.Id)))
                        {
                            foreach (BehaviorFlowEdge edge in block.Successors.Where(edge => edge.TargetBlockId.HasValue))
                            {
                                if (proof.IsImpossibleEdge(instance.Id, block, edge.TargetBlockId!.Value))
                                {
                                    impossible.Add((block.Id, edge.TargetBlockId.Value));
                                }
                            }
                        }
                    }
                    if (conditions.Count == 0 && stops.Count == 0 && impossible.Count == 0)
                    {
                        continue;
                    }
                    HashSet<(int From, int To)> excluded = new(this.m_excludedEdges.GetValueOrDefault(instance.Id) ?? Enumerable.Empty<(int, int)>());
                    excluded.UnionWith(impossible);
                    foreach (BehaviorFlowBlock block in body.Blocks)
                    {
                        excluded.UnionWith(block.Successors.Where(edge => edge.TargetBlockId.HasValue && (stops.Contains(block.Id)
                            || block.Successors.Count > 1 && conditions.TryGetValue(block.Id, out bool taken) && taken != (edge.TargetBlockId == block.JumpTargetBlockId)))
                            .Select(edge => (block.Id, edge.TargetBlockId!.Value)));
                    }
                    HashSet<int> reachable = new();
                    Queue<int> pending = new(new[] { -1 });
                    while (pending.TryDequeue(out int id))
                    {
                        if (!IsReachable(instance.Id, id) || !reachable.Add(id))
                        {
                            continue;
                        }
                        BehaviorFlowBlock block = blocks[id];
                        foreach (BehaviorFlowEdge edge in block.Successors.Where(edge => edge.TargetBlockId.HasValue
                            && !excluded.Contains((id, edge.TargetBlockId.Value))))
                        {
                            pending.Enqueue(edge.TargetBlockId!.Value);
                        }
                    }
                    changes.Add((instance.Id, blocks.Keys.Except(reachable).ToHashSet(), excluded));
                }
            }
            HashSet<int> changedRoots = new();
            foreach (var change in changes)
            {
                if (change.Blocks.Count != (this.m_unreachableBlocks.GetValueOrDefault(change.Instance)?.Count ?? 0)
                    || change.Edges.Count != (this.m_excludedEdges.GetValueOrDefault(change.Instance)?.Count ?? 0))
                {
                    changedRoots.Add(GetInstance(change.Instance).RootId);
                }
                this.m_unreachableBlocks[change.Instance] = change.Blocks;
                this.m_excludedEdges[change.Instance] = change.Edges;
            }
            return changedRoots;
        }

        // 用现有前驱和参数绑定表达整数条件，只发布完整证明为不可能的边。
        private sealed class IntegerPathProof : IDisposable
        {
            private readonly ValueSourceIndex m_sources;
            private readonly IReadOnlySet<int> m_reentered;
            private readonly Microsoft.Z3.Context m_context = new();
            private readonly Microsoft.Z3.Solver m_solver;
            private readonly Dictionary<(int Instance, FlowNode Node), Microsoft.Z3.BoolExpr> m_prefixes = new();
            private readonly HashSet<(int Instance, FlowNode Node)> m_activePrefixes = new();
            private readonly Dictionary<BehaviorValueReference, Microsoft.Z3.BitVecExpr> m_values = new();
            private readonly HashSet<BehaviorValueReference> m_activeValues = new();
            private readonly HashSet<int> m_approvedInstances = new();
            private readonly Dictionary<Microsoft.Z3.BoolExpr, Microsoft.Z3.BoolExpr> m_simplifiedConditions = new();

            // 同一冻结批次共用公式环境，库内部保持单线程。
            public IntegerPathProof(ValueSourceIndex sources, IReadOnlySet<int> reentered)
            {
                this.m_sources = sources;
                this.m_reentered = reentered;
                this.m_solver = this.m_context.MkSolver("QF_BV");
                using Microsoft.Z3.Params parameters = this.m_context.MkParams();
                parameters.Add("threads", 1u);
                this.m_solver.Parameters = parameters;
            }

            // 下一个函数实例只复用环境，不沿用前一个实例的公式或查询结果。
            public void ClearQuery()
            {
                this.m_solver.Reset();
                this.m_prefixes.Clear();
                this.m_activePrefixes.Clear();
                this.m_values.Clear();
                this.m_activeValues.Clear();
                this.m_approvedInstances.Clear();
                this.m_simplifiedConditions.Clear();
            }

            // 不可能证明只用于排除边；有解和未能表达均不能充当修改见证。
            public bool IsImpossibleEdge(int instanceId, BehaviorFlowBlock block, int target)
            {
                try
                {
                    Microsoft.Z3.BoolExpr prefix = MergeConditions(ReadGraph(instanceId).NodesByBlockId[block.Id]
                        .Select(node => ReadPrefix(instanceId, node)));
                    Microsoft.Z3.BoolExpr formula = SimplifyCondition(JoinConditions(prefix, ReadEdge(instanceId, block, target)));
                    if (formula.IsFalse || formula.IsTrue)
                    {
                        return formula.IsFalse;
                    }
                    this.m_solver.Reset();
                    this.m_solver.Assert(formula);
                    return this.m_solver.Check() == Microsoft.Z3.Status.UNSATISFIABLE;
                }
                catch (AnalysisException)
                {
                    // 缺失关联不能用于排除原始执行边；联合修改见证仍须单独完成。
                    return false;
                }
            }

            // 合并到达同一节点的全部前驱，并联立真实父调用位置的条件。
            private Microsoft.Z3.BoolExpr ReadPrefix(int instanceId, FlowNode node)
            {
                // 仅调度原有前驱或实际父调用点，不用进程调用栈保存遍历位置。
                IReadOnlyList<(int Instance, FlowNode Node)> ReadInputs((int Instance, FlowNode Node) current)
                {
                    MethodCallInstance instance = this.m_sources.GetInstance(current.Instance);
                    ValueFlowGraph graph = ReadGraph(current.Instance);
                    if (!this.m_sources.IsReachable(current.Instance, current.Node.BlockId))
                    {
                        return Array.Empty<(int, FlowNode)>();
                    }
                    if (current.Node.BlockId == -1)
                    {
                        return instance.ParentId == 0 ? Array.Empty<(int, FlowNode)>()
                            : ReadGraph(instance.ParentId).NodesByBlockId[instance.InvocationPoint!.Value.BlockId]
                                .Select(parent => (instance.ParentId, parent)).ToArray();
                    }
                    return (graph.Predecessors.GetValueOrDefault(current.Node) ?? Array.Empty<FlowNode>())
                        .Where(parent => this.m_sources.m_excludedEdges.GetValueOrDefault(current.Instance)?.Contains((parent.BlockId, current.Node.BlockId)) != true)
                        .Select(parent => (current.Instance, parent)).ToArray();
                }

                // 所有前驱公式建立后，再按实际入边合并当前位置的条件。
                Microsoft.Z3.BoolExpr Compose((int Instance, FlowNode Node) current, IReadOnlyList<(int Instance, FlowNode Node)> inputs)
                {
                    if (!this.m_sources.IsReachable(current.Instance, current.Node.BlockId))
                    {
                        return this.m_context.MkFalse();
                    }
                    if (current.Node.BlockId == -1)
                    {
                        return this.m_sources.GetInstance(current.Instance).ParentId == 0 ? this.m_context.MkTrue()
                            : MergeConditions(inputs.Select(input => this.m_prefixes[input]));
                    }
                    ValueFlowGraph graph = ReadGraph(current.Instance);
                    return MergeConditions(inputs.Select(input => JoinConditions(this.m_prefixes[input],
                        ReadEdge(current.Instance, graph.Blocks[input.Node.BlockId], current.Node.BlockId))));
                }

                return ReadAcyclic((instanceId, node), this.m_prefixes, this.m_activePrefixes, ReadInputs, Compose);
            }

            // 前缀与赋值倒查共用显式工作栈，活动节点只用于识别未表达的循环。
            private static TResult ReadAcyclic<TKey, TResult>(TKey start, Dictionary<TKey, TResult> known, HashSet<TKey> active,
                Func<TKey, IReadOnlyList<TKey>> readInputs, Func<TKey, IReadOnlyList<TKey>, TResult> compose) where TKey : notnull
            {
                Stack<(TKey Key, IReadOnlyList<TKey>? Inputs)> pending = new(new[] { (start, (IReadOnlyList<TKey>?)null) });
                HashSet<TKey> entered = new();
                try
                {
                    while (pending.TryPop(out var current))
                    {
                        if (known.ContainsKey(current.Key))
                        {
                            continue;
                        }
                        if (current.Inputs == null)
                        {
                            if (!active.Add(current.Key))
                            {
                                throw new AnalysisException("整数路径或赋值的循环选择尚未闭合");
                            }
                            entered.Add(current.Key);
                            IReadOnlyList<TKey> inputs = readInputs(current.Key);
                            pending.Push((current.Key, inputs));
                            foreach (TKey input in inputs.Reverse())
                            {
                                pending.Push((input, null));
                            }
                        }
                        else
                        {
                            known.Add(current.Key, compose(current.Key, current.Inputs));
                            active.Remove(current.Key);
                            entered.Remove(current.Key);
                        }
                    }
                    return known[start];
                }
                finally
                {
                    active.ExceptWith(entered);
                }
            }

            // 先验证当前函数或父调用的路径种类，再读取它的实际节点。
            private ValueFlowGraph ReadGraph(int instanceId)
            {
                MethodBehavior body = this.m_sources.m_methods[this.m_sources.GetInstance(instanceId).MethodId];
                if (!this.m_approvedInstances.Contains(instanceId))
                {
                    if (body.BodyKind != MethodBodyKind.Executable || body.ExceptionHandlers.Count != 0 || this.m_reentered.Contains(instanceId))
                    {
                        throw new AnalysisException("整数路径的函数体、异常或再次调用关联尚未闭合");
                    }
                    this.m_approvedInstances.Add(instanceId);
                }
                return this.m_sources.ReadValueFlowGraph(body);
            }

            // 按实际跳转方向建立条件，缺失 switch 标签不能视为无条件。
            private Microsoft.Z3.BoolExpr ReadEdge(int instanceId, BehaviorFlowBlock block, int target)
            {
                if (block.Successors.Count <= 1)
                {
                    return this.m_context.MkTrue();
                }
                if (block.ConditionValueId is not int valueId)
                {
                    throw new AnalysisException("整数路径的分支条件尚未读取");
                }
                Microsoft.Z3.BitVecExpr value = ReadValue(new(this.m_sources.GetInstance(instanceId).MethodId, valueId, instanceId));
                Microsoft.Z3.BoolExpr condition = this.m_context.MkNot(this.m_context.MkEq(value, this.m_context.MkBV(0, value.SortSize)));
                return target == block.JumpTargetBlockId ? condition : this.m_context.MkNot(condition);
            }

            // 保留局部赋值和指令栈入边的选择，未知调用仍不生成自由变量。
            private Microsoft.Z3.BitVecExpr ReadValue(BehaviorValueReference reference)
            {
                if (this.m_values.TryGetValue(reference, out Microsoft.Z3.BitVecExpr? known))
                {
                    return known;
                }
                if (!this.m_activeValues.Add(reference))
                {
                    throw new AnalysisException("整数值的循环赋值尚未闭合");
                }
                try
                {
                    Microsoft.Z3.BitVecExpr result;
                    BehaviorValue? raw = reference.ValueId >= 0 && !this.m_sources.m_runtimeValues.ContainsKey(reference)
                        ? this.m_sources.m_methods[reference.MethodId].Values[reference.ValueId] : null;
                    if (raw?.Kind == BehaviorValueKind.SlotRead)
                    {
                        result = ReadStoredInteger(reference, raw);
                    }
                    else if (raw?.Kind == BehaviorValueKind.Merge)
                    {
                        int blockId = raw.Point!.Value.BlockId;
                        ValueFlowGraph graph = ReadGraph(reference.InstanceId);
                        result = ReadChoices(raw.IncomingValues.Where(input => this.m_sources.IsReachable(reference.InstanceId, input.PredecessorBlockId)
                            && this.m_sources.m_excludedEdges.GetValueOrDefault(reference.InstanceId)?.Contains((input.PredecessorBlockId, blockId)) != true)
                            .SelectMany(input => graph.NodesByBlockId[input.PredecessorBlockId].Select(node =>
                                (Guard: JoinConditions(ReadPrefix(reference.InstanceId, node), ReadEdge(reference.InstanceId, graph.Blocks[node.BlockId], blockId)),
                                    Value: (Func<Microsoft.Z3.BitVecExpr>)(() => ReadValue(reference with { ValueId = input.ValueId }))))));
                    }
                    else if (raw?.Kind == BehaviorValueKind.Parameter
                        && this.m_sources.GetInstance(reference.InstanceId).Binding is ResolvedCallTarget binding)
                    {
                        IReadOnlyList<BehaviorValueReference> arguments = binding.Arguments[raw.ParameterIndex!.Value];
                        if (arguments.Count != 1)
                        {
                            throw new AnalysisException("整数实参的目标选择尚未闭合");
                        }
                        result = ReadValue(arguments[0]);
                    }
                    else
                    {
                        IReadOnlyList<ValueOrigin> origins = this.m_sources.GetCallOrigins(reference, retainTypeChecks: true);
                        if (origins.Count != 1 || origins[0].ReturnPath != null)
                        {
                            throw new AnalysisException("整数值的分支或返回关联尚未闭合");
                        }
                        ValueOrigin origin = origins[0];
                        if (origin.Value.Kind == BehaviorValueKind.Computation)
                        {
                            result = ReadOperation(origin);
                        }
                        else
                        {
                            MethodCallInstance owner = this.m_sources.GetInstance(origin.Reference.InstanceId);
                            string? type = origin.Value.Kind == BehaviorValueKind.Parameter
                                ? owner.Substitute(this.m_sources.m_definitions[owner.MethodId].Parameters[origin.Value.ParameterIndex!.Value].TypeIdentity).Text
                                : origin.Value.Type?.Id;
                            uint bits = type switch
                            {
                                "System.Int32" or "System.UInt32" or "System.Boolean" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Char" => 32,
                                "System.Int64" or "System.UInt64" => 64,
                                _ => throw new AnalysisException("整数值的实际类型尚未闭合"),
                            };
                            result = origin.Value.Kind switch
                            {
                                BehaviorValueKind.Constant => this.m_context.MkBV(origin.Value.Reference!, bits),
                                BehaviorValueKind.Parameter when !origin.Value.IsManagedReferenceSlot => this.m_context.MkBVConst($"p{origin.Reference.InstanceId}_{origin.Reference.ValueId}_{bits}", bits),
                                _ => throw new AnalysisException("整数值的存储状态尚未闭合"),
                            };
                        }
                    }
                    this.m_values.Add(reference, result);
                    return result;
                }
                finally
                {
                    this.m_activeValues.Remove(reference);
                }
            }

            // 使用原倒查记录的最后写入切点，保留赋值到本次读取之间的条件。
            private Microsoft.Z3.BitVecExpr ReadStoredInteger(BehaviorValueReference reference, BehaviorValue value)
            {
                MethodCallInstance instance = this.m_sources.GetInstance(reference.InstanceId);
                ValueFlowGraph graph = ReadGraph(instance.Id);
                BehaviorValueReference slot = reference with { ValueId = value.InputValueIds.Single() };
                Dictionary<FlowSearchPoint, ReachingWrite<BehaviorValueReference>?> trace = new();
                this.m_sources.ReadStoredValuesAtPoint(instance, BehaviorWriteKind.Indirect, null,
                    new[] { new StorageLocation(slot, "slot", string.Empty, string.Empty) }, value.Point!.Value,
                    -1, reference, () => new[] { slot }, trace: trace);
                Dictionary<FlowSearchPoint, Microsoft.Z3.BitVecExpr> known = new();
                HashSet<FlowSearchPoint> active = new();

                // 在相同切点复用公式，长路径通过原工作栈逐步合并。
                Microsoft.Z3.BitVecExpr ReadAt(FlowSearchPoint current)
                {
                    // 先读取真实入边条件，不为已经排除的分支读取赋值。
                    Microsoft.Z3.BoolExpr ReadGuard(FlowSearchPoint parent, FlowSearchPoint point)
                    {
                        return JoinConditions(ReadPrefix(instance.Id, parent.Node),
                            ReadEdge(instance.Id, graph.Blocks[parent.Node.BlockId], point.Node.BlockId));
                    }

                    // 写入切点之后不再查找被覆盖的旧值。
                    IReadOnlyList<FlowSearchPoint> ReadInputs(FlowSearchPoint point)
                    {
                        return trace[point] != null || point.Node.BlockId == -1 ? Array.Empty<FlowSearchPoint>()
                            : (graph.Predecessors.GetValueOrDefault(point.Node) ?? Array.Empty<FlowNode>())
                                .Where(parent => this.m_sources.IsReachable(instance.Id, parent.BlockId)
                                    && this.m_sources.m_excludedEdges.GetValueOrDefault(instance.Id)?.Contains((parent.BlockId, point.Node.BlockId)) != true)
                                .Select(parent => new FlowSearchPoint(parent, int.MaxValue))
                                .Where(parent => !SimplifyCondition(ReadGuard(parent, point)).IsFalse).ToArray();
                    }

                    // 条件只选择当前倒查路径上仍有效的赋值。
                    Microsoft.Z3.BitVecExpr Compose(FlowSearchPoint point, IReadOnlyList<FlowSearchPoint> inputs)
                    {
                        ReachingWrite<BehaviorValueReference>? write = trace[point];
                        if (write != null)
                        {
                            if (!write.ReplacesPrevious || write.Values.Count != 1)
                            {
                                throw new AnalysisException("整数赋值的别名或子调用出口选择尚未闭合");
                            }
                            return ReadValue(write.Values[0]);
                        }
                        if (point.Node.BlockId == -1)
                        {
                            return ReadValue(slot);
                        }
                        return ReadChoices(inputs.Select(parent =>
                            (Guard: ReadGuard(parent, point),
                                Value: (Func<Microsoft.Z3.BitVecExpr>)(() => known[parent]))));
                    }
                    return ReadAcyclic(current, known, active, ReadInputs, Compose);
                }

                return ReadChoices(graph.NodesByBlockId[value.Point.Value.BlockId].Select(node =>
                    (Guard: ReadPrefix(instance.Id, node), Value: (Func<Microsoft.Z3.BitVecExpr>)(() => ReadAt(new FlowSearchPoint(node, value.Point.Value.Order))))));
            }

            // 每条真实入边选择它实际带来的值，不把不同分支的候选自由组合。
            private Microsoft.Z3.BitVecExpr ReadChoices(IEnumerable<(Microsoft.Z3.BoolExpr Guard, Func<Microsoft.Z3.BitVecExpr> Value)> choices)
            {
                Microsoft.Z3.BitVecExpr? result = null;
                foreach (var choice in choices)
                {
                    Microsoft.Z3.BoolExpr guard = SimplifyCondition(choice.Guard);
                    if (guard.IsFalse)
                    {
                        continue;
                    }
                    Microsoft.Z3.BitVecExpr value = choice.Value();
                    if (result != null && result.SortSize != value.SortSize)
                    {
                        throw new AnalysisException("整数合流的位宽不一致");
                    }
                    result = result == null ? value : (Microsoft.Z3.BitVecExpr)this.m_context.MkITE(guard, value, result);
                }
                return result ?? throw new AnalysisException("整数值没有可到达的选择分支");
            }

            // 无条件的顺序指令沿用原公式，避免给长路径逐层包裹恒真条件。
            private Microsoft.Z3.BoolExpr JoinConditions(Microsoft.Z3.BoolExpr left, Microsoft.Z3.BoolExpr right)
            {
                return left.IsFalse || right.IsTrue ? left : right.IsFalse || left.IsTrue ? right : this.m_context.MkAnd(left, right);
            }

            // 单一前驱直接共用公式，多前驱仍保留完整的可选路径。
            private Microsoft.Z3.BoolExpr MergeConditions(IEnumerable<Microsoft.Z3.BoolExpr> conditions)
            {
                Microsoft.Z3.BoolExpr[] values = conditions.Where(value => !value.IsFalse).Distinct().ToArray();
                return values.Any(value => value.IsTrue) ? this.m_context.MkTrue()
                    : values.Length == 1 ? values[0] : this.m_context.MkOr(values);
            }

            // 同一次冻结查询中，相同布尔公式只做一次化简。
            private Microsoft.Z3.BoolExpr SimplifyCondition(Microsoft.Z3.BoolExpr condition)
            {
                if (!this.m_simplifiedConditions.TryGetValue(condition, out Microsoft.Z3.BoolExpr? result))
                {
                    result = condition.IsTrue || condition.IsFalse ? condition : (Microsoft.Z3.BoolExpr)condition.Simplify();
                    this.m_simplifiedConditions.Add(condition, result);
                }
                return result;
            }

            // 位宽、有符号比较与普通溢出遵从原指令，不使用无限精度整数替代。
            private Microsoft.Z3.BitVecExpr ReadOperation(ValueOrigin origin)
            {
                string operation = origin.Value.Reference!;
                if (operation is not ("brtrue" or "brfalse" or "beq" or "ceq" or "bne.un" or "bgt" or "cgt" or "bge" or "blt" or "clt" or "ble"
                    or "bgt.un" or "cgt.un" or "bge.un" or "blt.un" or "clt.un" or "ble.un" or "add" or "sub" or "mul" or "and" or "or" or "xor"
                    or "neg" or "not" or "conv.i4" or "conv.u4" or "conv.i8" or "conv.u8"))
                {
                    throw new AnalysisException($"整数指令尚未完整表达：{operation}");
                }
                Microsoft.Z3.BitVecExpr[] inputs = origin.Value.InputValueIds.Select(id => ReadValue(origin.Reference with { ValueId = id })).ToArray();
                if (inputs.Length == 0 || inputs.Length > 2 || inputs.Length == 2 && inputs[0].SortSize != inputs[1].SortSize)
                {
                    throw new AnalysisException("整数指令的参数位宽尚未闭合");
                }
                Microsoft.Z3.BitVecExpr left = inputs[0];
                Microsoft.Z3.BitVecExpr? right = inputs.Length == 2 ? inputs[1] : null;
                Microsoft.Z3.BoolExpr? condition = operation switch
                {
                    "brtrue" => this.m_context.MkNot(this.m_context.MkEq(left, this.m_context.MkBV(0, left.SortSize))),
                    "brfalse" => this.m_context.MkEq(left, this.m_context.MkBV(0, left.SortSize)),
                    "beq" or "ceq" => this.m_context.MkEq(left, right!),
                    "bne.un" => this.m_context.MkNot(this.m_context.MkEq(left, right!)),
                    "bgt" or "cgt" => this.m_context.MkBVSGT(left, right!),
                    "bge" => this.m_context.MkBVSGE(left, right!),
                    "blt" or "clt" => this.m_context.MkBVSLT(left, right!),
                    "ble" => this.m_context.MkBVSLE(left, right!),
                    "bgt.un" or "cgt.un" => this.m_context.MkBVUGT(left, right!),
                    "bge.un" => this.m_context.MkBVUGE(left, right!),
                    "blt.un" or "clt.un" => this.m_context.MkBVULT(left, right!),
                    "ble.un" => this.m_context.MkBVULE(left, right!),
                    _ => null,
                };
                if (condition != null)
                {
                    return (Microsoft.Z3.BitVecExpr)this.m_context.MkITE(condition, this.m_context.MkBV(1, 32), this.m_context.MkBV(0, 32));
                }
                return operation switch
                {
                    "add" => this.m_context.MkBVAdd(left, right!),
                    "sub" => this.m_context.MkBVSub(left, right!),
                    "mul" => this.m_context.MkBVMul(left, right!),
                    "and" => this.m_context.MkBVAND(left, right!),
                    "or" => this.m_context.MkBVOR(left, right!),
                    "xor" => this.m_context.MkBVXOR(left, right!),
                    "neg" => this.m_context.MkBVNeg(left),
                    "not" => this.m_context.MkBVNot(left),
                    "conv.i4" or "conv.u4" => left.SortSize == 32 ? left : this.m_context.MkExtract(31, 0, left),
                    "conv.i8" or "conv.u8" => left.SortSize == 64 ? left : operation == "conv.u8"
                        ? this.m_context.MkZeroExt(32, left) : this.m_context.MkSignExt(32, left),
                    _ => throw new AnalysisException($"整数指令尚未完整表达：{operation}"),
                };
            }

            // 公式和求解状态不越过当前冻结批次的生命周期。
            public void Dispose()
            {
                this.m_solver.Dispose();
                this.m_context.Dispose();
            }
        }

        // 路径缩小时撤销受影响入口的派生事实；保留实例编号和已证路径，重新命中才激活子调用。
        internal HashSet<int> ResetRoots(HashSet<int> roots)
        {
            HashSet<int> instances = this.m_instances.Values.Where(instance => roots.Contains(instance.RootId)).Select(instance => instance.Id).ToHashSet();
            this.m_inactiveInstances.UnionWith(instances.Except(roots));
            foreach (int id in instances)
            {
                this.m_callsByCallerInstance.Remove(id);
                this.m_runtimeWrites.Remove(id);
            }
            foreach (var key in this.m_resultCalls.Keys.Where(key => instances.Contains(key.InstanceId)).ToArray())
            {
                this.m_resultCalls.Remove(key);
            }
            HashSet<BehaviorValueReference> derived = this.m_observedValues.Values.Concat(this.m_addressReads.Values).ToHashSet();
            foreach (var key in this.m_runtimeValues.Keys.Where(key => instances.Contains(key.InstanceId) && (key.ValueId >= 0 || derived.Contains(key))).ToArray())
            {
                this.m_runtimeValues.Remove(key);
            }
            this.m_storageReadOnlyInstances.RemoveWhere(key => instances.Contains(key.Instance));
            return instances;
        }

        // 前驱路径尚未闭合时不发布修改证明，防止借另一条路径替不兼容的对象来源作证。
        internal bool HasClosedPrefix(int instanceId, BehaviorFlowPoint point,
            HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)> proven,
            HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)>? active = null, bool everyPath = true,
            IReadOnlyDictionary<(int Instance, int Block), string>? conditionFailures = null)
        {
            if (ReadStaticCallInitializationFailure(instanceId, normalReturnOnly: true) != null)
            {
                return false;
            }
            var query = (instanceId, point, everyPath, conditionFailures != null);
            if (proven.Contains(query))
            {
                return true;
            }
            active ??= new();
            if (!active.Add(query))
            {
                return false;
            }
            try
            {
                MethodBehavior body = this.m_methods[GetInstance(instanceId).MethodId];
                ValueFlowGraph graph = ReadValueFlowGraph(body);
                Queue<FlowSearchPoint> pending = new((graph.NodesByBlockId.GetValueOrDefault(point.BlockId) ?? Array.Empty<FlowNode>())
                    .Select(node => new FlowSearchPoint(node, point.Order)));
                HashSet<FlowSearchPoint> visited = new();
                bool reachedEntry = false;
                while (pending.TryDequeue(out FlowSearchPoint current))
                {
                    if (!IsReachable(instanceId, current.Node.BlockId) || !visited.Add(current))
                    {
                        continue;
                    }
                    bool closed = conditionFailures?.ContainsKey((instanceId, current.Node.BlockId)) != true
                        && graph.Calls[current.Node.BlockId].Where(call => call.Point.Order < current.Order).All(call =>
                    {
                        ResolvedCall? resolved = this.m_callsByCallerInstance.GetValueOrDefault(instanceId)?.GetValueOrDefault(call.Point);
                        return HasNormalContinuation(resolved, proven, active, conditionFailures);
                    });
                    if (!closed)
                    {
                        if (everyPath)
                        {
                            return false;
                        }
                        continue;
                    }
                    if (current.Node.BlockId == -1)
                    {
                        reachedEntry = true;
                        if (!everyPath)
                        {
                            proven.Add(query);
                            return true;
                        }
                        continue;
                    }
                    foreach (FlowNode predecessor in graph.Predecessors.GetValueOrDefault(current.Node) ?? Array.Empty<FlowNode>())
                    {
                        if (this.m_excludedEdges.GetValueOrDefault(instanceId)?.Contains((predecessor.BlockId, current.Node.BlockId)) != true)
                        {
                            pending.Enqueue(new FlowSearchPoint(predecessor, int.MaxValue));
                        }
                    }
                }
                if (reachedEntry)
                {
                    proven.Add(query);
                }
                return reachedEntry;
            }
            finally
            {
                active.Remove(query);
            }
        }

        // 普通目标需要真实返回路径；反射值操作沿已有语义继续。
        private bool HasNormalContinuation(ResolvedCall? call, HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)> proven,
            HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)>? active = null,
            IReadOnlyDictionary<(int Instance, int Block), string>? conditionFailures = null)
        {
            return call != null && (call.CompletesWithoutTarget || call.Targets.Count > 0 && call.Targets.All(target => IsRuntimeDelegateCreation(call, target)
                || this.m_methods.TryGetValue(target.MethodId, out MethodBehavior? body) && body.BodyKind == MethodBodyKind.Executable
                && body.Returns.Any(returned => IsReachable(target.InstanceId, returned.Point.BlockId)
                    && HasClosedPrefix(target.InstanceId, returned.Point, proven, active, false, conditionFailures))));
        }

        // 保留汇合出口各分支最后保存返回值的位置，不能让另一个未知分支遮挡独立返回。
        internal IReadOnlyList<(BehaviorValueReference Reference, BehaviorFlowPoint Point)> ReadReturnSites(int instanceId, BehaviorReturn returned,
            HashSet<(int Instance, BehaviorFlowPoint Point, bool EveryPath, bool Conditions)> proven,
            IReadOnlyDictionary<(int Instance, int Block), string>? conditionFailures = null)
        {
            MethodBehavior body = this.m_methods[GetInstance(instanceId).MethodId];
            BehaviorValueReference reference = new(body.MethodId, returned.ValueId!.Value, instanceId);
            BehaviorValue value = body.Values[reference.ValueId];
            var original = (Reference: reference, Point: returned.Point);
            if (value.Kind != BehaviorValueKind.SlotRead || body.Values[value.InputValueIds.Single()].Kind != BehaviorValueKind.Local
                || body.Values.Any(item => item.Kind == BehaviorValueKind.Address && item.Member == null && item.InputValueIds.SequenceEqual(value.InputValueIds)))
            {
                return new[] { original };
            }
            int slotId = value.InputValueIds.Single();
            ValueFlowGraph graph = ReadValueFlowGraph(body);

            // 返回槽赋值切开不同返回分支；其后未闭合的调用不能被当作已经正常执行。
            IEnumerable<ReachingWrite<(BehaviorValueReference Reference, BehaviorFlowPoint Point)>> ReadWrites(FlowSearchPoint current)
            {
                foreach (BehaviorAssignment assignment in graph.Assignments[current.Node.BlockId]
                    .Where(item => item.TargetValueId == slotId && item.Point.Order < current.Order))
                {
                    yield return new(new[] { (new BehaviorValueReference(body.MethodId, assignment.ValueId, instanceId), assignment.Point) }, assignment.Point, true);
                }
                foreach (BehaviorCall call in graph.Calls[current.Node.BlockId].Where(call => call.Point.Order < current.Order))
                {
                    if (!HasNormalContinuation(this.m_callsByCallerInstance.GetValueOrDefault(instanceId)?.GetValueOrDefault(call.Point),
                        proven, conditionFailures: conditionFailures))
                    {
                        yield return new(Array.Empty<(BehaviorValueReference, BehaviorFlowPoint)>(), call.Point, true);
                    }
                }
            }

            return ReadReachingValues(body, returned.Point, ReadWrites, () => new[] { original }, instanceId)
                .OrderBy(site => site.Point.BlockId).ThenBy(site => site.Point.Order).ThenBy(site => site.Reference.ValueId).ToArray();
        }

        // 读取一致的整数值，保留装箱与类型检查，避免把对象误当成数值。
        private (long Value, int Bits)? ReadInteger(BehaviorValueReference reference, HashSet<BehaviorValueReference> active)
        {
            if (this.m_integerValues.TryGetValue(reference, out var known))
            {
                return known;
            }
            long cycles = this.m_integerCycles;
            IReadOnlyList<ValueOrigin> origins = GetCallOrigins(reference, retainTypeChecks: true);
            if (origins.Count == 0)
            {
                return null;
            }
            HashSet<(long Value, int Bits)?> values = new();
            foreach (ValueOrigin origin in origins)
            {
                if (!active.Add(origin.Reference))
                {
                    this.m_integerCycles++;
                    return null;
                }
                values.Add(origin.Value.Kind == BehaviorValueKind.Constant
                    ? origin.Value.Type?.Id is "System.Int32" or "System.Int64"
                        ? (long.Parse(origin.Value.Reference!, System.Globalization.CultureInfo.InvariantCulture), origin.Value.Type.Id == "System.Int32" ? 32 : 64)
                        : origin.Value.Type == null && origin.Value.Reference == null ? (0, 32) : null
                    : origin.Value.Kind == BehaviorValueKind.Computation
                        ? ReadIntegerOperation(origin.Reference.InstanceId, origin.Value, active) : null);
                active.Remove(origin.Reference);
                if (values.Count > 1 || values.Contains(null))
                {
                    break;
                }
            }
            var result = values.Count == 1 ? values.Single() : null;
            if (cycles == this.m_integerCycles)
            {
                this.m_integerValues[reference] = result;
            }
            return result;
        }

        // 按指令宽度计算已知整数，溢出遵从原指令；未支持的运算不推断结果。
        private (long Value, int Bits)? ReadIntegerOperation(int instanceId, BehaviorValue operation, HashSet<BehaviorValueReference> active)
        {
            string methodId = GetInstance(instanceId).MethodId;
            if (operation.Reference == "ldlen")
            {
                IReadOnlyList<ValueOrigin> arrays = GetCallOrigins(new BehaviorValueReference(methodId, operation.InputValueIds.Single(), instanceId), retainTypeChecks: true);
                var lengths = arrays.Select(array => ReadArrayLength(array, active)).Distinct().ToArray();
                return lengths.Length == 1 && lengths[0] is { Value: >= 0 } length ? length : null;
            }
            if (operation.Reference is not ("brtrue" or "brfalse" or "beq" or "ceq" or "bne.un"
                or "bgt" or "cgt" or "bge" or "blt" or "clt" or "ble" or "bgt.un" or "cgt.un" or "bge.un" or "blt.un" or "clt.un" or "ble.un"
                or "conv.i4" or "conv.u4" or "conv.i8" or "conv.u8" or "add" or "sub" or "mul"))
            {
                return null;
            }
            List<(long Value, int Bits)> inputs = new();
            foreach (int valueId in operation.InputValueIds)
            {
                if (ReadInteger(new BehaviorValueReference(methodId, valueId, instanceId), active) is not { } input)
                {
                    return null;
                }
                inputs.Add(input);
            }
            if (inputs.Count == 0)
            {
                return null;
            }
            (long left, int bits) = inputs[0];
            long right = inputs.Count == 2 ? inputs[1].Value : 0;
            ulong unsignedLeft = bits == 32 ? (uint)left : (ulong)left, unsignedRight = bits == 32 ? (uint)right : (ulong)right;
            bool? condition = operation.Reference switch
            {
                "brtrue" => left != 0,
                "brfalse" => left == 0,
                "beq" or "ceq" => left == right,
                "bne.un" => left != right,
                "bgt" or "cgt" => left > right,
                "bge" => left >= right,
                "blt" or "clt" => left < right,
                "ble" => left <= right,
                "bgt.un" or "cgt.un" => unsignedLeft > unsignedRight,
                "bge.un" => unsignedLeft >= unsignedRight,
                "blt.un" or "clt.un" => unsignedLeft < unsignedRight,
                "ble.un" => unsignedLeft <= unsignedRight,
                _ => null,
            };
            if (condition.HasValue)
            {
                return (condition.Value ? 1 : 0, 32);
            }
            if (operation.Reference is "conv.i4" or "conv.u4")
            {
                return (unchecked((int)left), 32);
            }
            if (operation.Reference is "conv.i8" or "conv.u8")
            {
                return (operation.Reference == "conv.u8" ? unchecked((long)unsignedLeft) : left, 64);
            }
            long? value = operation.Reference switch
            {
                "add" => unchecked(left + right),
                "sub" => unchecked(left - right),
                "mul" => unchecked(left * right),
                _ => null,
            };
            return value.HasValue ? (bits == 32 ? unchecked((int)value.Value) : value.Value, bits) : null;
        }

        // 数组长度是独立事实，绝不把共享静态数组改成当前调用新建的对象。
        private (long Value, int Bits)? ReadArrayLength(ValueOrigin array, HashSet<BehaviorValueReference> active)
        {
            if (array.Value.Kind == BehaviorValueKind.NewArray && array.Value.InputValueIds.Count == 1)
            {
                return ReadInteger(array.Reference with { ValueId = array.Value.InputValueIds.Single() }, active);
            }
            if (array.Value.Kind != BehaviorValueKind.FieldRead || array.Value.InputValueIds.Count != 0 || array.Value.Member == null)
            {
                return null;
            }
            BehaviorMemberReference member = array.Value.Member;
            MethodCallInstance caller = GetInstance(array.Reference.InstanceId);
            (TypeEntry type, TypeIdentityTemplate[] arguments) = ReadMemberType(member, caller);
            string typeKey = type.Id + "<" + string.Join(",", arguments.Select(argument => argument.Text)) + ">";
            string key = typeKey + "::" + member.Name;
            if (this.m_initializedArrayLengths.TryGetValue(key, out var known))
            {
                return known;
            }
            var field = this.m_catalog.GetFields(type).SingleOrDefault(candidate => candidate.Name == member.Name);
            (long Value, int Bits)? result = null;
            if (field is { IsPrivate: true, IsStatic: true, IsInitOnly: true }
                && ReadInitialization(type, arguments) is { Independent: true, Values: { } initial })
            {
                MethodCallInstance instance = initial.RootInstances.Values.Single();
                MethodBehavior body = initial.m_methods[instance.MethodId];
                IReadOnlyList<StorageLocation> locations = ReadStorageLocations(array.Reference, member, -1, Array.Empty<int>())!;
                var lengths = body.Returns.Where(returned => initial.IsReachable(instance.Id, returned.Point.BlockId))
                    .SelectMany(returned => initial.ReadStoredValuesAtPoint(instance, BehaviorWriteKind.Field, member, locations,
                        returned.Point, -1, s_previousStorageValue, () => new[] { s_previousStorageValue }))
                    .SelectMany(value => value == s_previousStorageValue ? new[] { ((long Value, int Bits)?)null }
                        : initial.GetCallOrigins(value, retainTypeChecks: true).Select(origin => initial.ReadArrayLength(origin, new())))
                    .Distinct().ToArray();
                result = lengths.Length == 1 && lengths[0] is { Value: >= 0 } length ? length : null;
            }
            this.m_initializedArrayLengths.Add(key, result);
            return result;
        }

        // 共用初始化事实准入，外部调用、外部读取及跨类型写入都不能冒充独立初始化。
        private (bool Independent, ValueSourceIndex? Values) ReadInitialization(TypeEntry type, IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            string key = type.Id + "<" + string.Join(",", arguments.Select(argument => argument.Text)) + ">";
            if (this.m_initializations.TryGetValue(key, out var known))
            {
                return known;
            }
            MethodEntry? initializer = this.m_catalog.GetMethods(type).SingleOrDefault(method => method.Kind == CatalogMethodKind.StaticConstructor);
            (bool Independent, ValueSourceIndex? Values) result = (initializer == null, null);
            if (initializer != null)
            {
                MethodBehavior body = this.m_catalog.ReadMethodBehavior(initializer);
                if (body.BodyKind == MethodBodyKind.Executable && body.Calls.Count == 0 && body.ExceptionHandlers.Count == 0
                    && !body.Values.Any(value => value.Kind is BehaviorValueKind.FieldRead or BehaviorValueKind.Address
                        or BehaviorValueKind.ArrayElementRead or BehaviorValueKind.Conversion)
                    && body.Writes.All(write => write.Kind == BehaviorWriteKind.Field && write.ReceiverValueId == null))
                {
                    ValueSourceIndex initial = new(this.m_catalog, new() { [initializer.Id] = initializer }, new() { [initializer.Id] = body });
                    MethodCallInstance instance = initial.GetInstance(initializer.Id, arguments.Select(argument => argument.Text).ToArray(), Array.Empty<string>());
                    BehaviorValueReference reference = new(initializer.Id, 0, instance.Id);
                    if (body.Writes.All(write => initial.ReadStorageLocations(reference, write.Member, -1, Array.Empty<int>())!
                            .All(location => location.Member.StartsWith(key + "::", StringComparison.Ordinal))))
                    {
                        initial.RefineReachability();
                        if (body.Returns.Any(returned => initial.IsReachable(instance.Id, returned.Point.BlockId))
                            && body.Values.Where(value => value.Kind == BehaviorValueKind.Computation)
                                .All(value => initial.ReadIntegerOperation(instance.Id, value, new()) != null)
                            && body.Values.Where(value => value.Kind == BehaviorValueKind.NewArray)
                                .All(value => initial.ReadArrayLength(new ValueOrigin(reference with { ValueId = value.Id }, value), new()) is { Value: >= 0 }))
                        {
                            result = (true, initial);
                        }
                    }
                }
            }
            this.m_initializations.Add(key, result);
            return result;
        }

        // 静态调用的正常返回和外部影响共用初始化事实，不把自身可变静态字段写入当作无效果。
        internal string? ReadStaticCallInitializationFailure(int instanceId, bool normalReturnOnly = false)
        {
            MethodCallInstance instance = GetInstance(instanceId);
            MethodEntry method = this.m_definitions[instance.MethodId];
            MethodCallInstance root = GetInstance(instance.RootId);
            if (!method.IsStatic || method.TypeId == this.m_definitions[root.MethodId].TypeId && instance.TypeArguments.SequenceEqual(root.TypeArguments))
            {
                return null;
            }
            TypeEntry type = this.m_catalog.TypesById[method.TypeId];
            var initialization = ReadInitialization(type, instance.TypeArguments);
            if (!initialization.Independent)
            {
                return $"静态调用的类型初始化尚未证明正常完成：{type.FullName}";
            }
            if (!normalReturnOnly && initialization.Values is ValueSourceIndex initial)
            {
                var fields = this.m_catalog.GetFields(type).ToDictionary(field => field.Name);
                if (initial.GetWrites(initial.RootInstances.Values.Single().Id).Any(write => write.ReceiverValueId == null
                    && fields[write.Member!.Name] is var field && (!field.IsPrivate || !field.IsInitOnly)))
                {
                    return $"静态调用的类型初始化写入尚未纳入调用效果：{type.FullName}";
                }
            }
            return null;
        }

        // 入口所属类型已初始化；其他类型的隐式初始化未证明时不能视为无存储影响。
        private void RequireIndependentInitialization(int instanceId)
        {
            MethodCallInstance instance = GetInstance(instanceId);
            MethodEntry method = this.m_definitions[instance.MethodId];
            RequireIndependentInitialization(this.m_catalog.TypesById[method.TypeId], instance.TypeArguments, instance.RootId);
        }

        // 静态成员访问与普通调用共用完整构造类型的初始化检查。
        private void RequireIndependentInitialization(TypeEntry type, IReadOnlyList<TypeIdentityTemplate> arguments, int rootId)
        {
            MethodCallInstance root = GetInstance(rootId);
            if (type.Id == this.m_definitions[root.MethodId].TypeId && arguments.SequenceEqual(root.TypeArguments))
            {
                return;
            }
            if (!ReadInitialization(type, arguments).Independent)
            {
                throw new AnalysisException($"前序调用的类型初始化影响尚未闭合：{type.FullName}");
            }
        }

        // 字段模板只在当前实际调用环境代入一次，初始化与存储身份共用所得类型。
        private (TypeEntry Type, TypeIdentityTemplate[] Arguments) ReadMemberType(BehaviorMemberReference member, MethodCallInstance instance)
        {
            TypeIdentityTemplate[] arguments = this.m_catalog.ReadResolvedTypeArguments(member.ReferringAssemblyPath!, member.ReferenceMetadataToken)
                .Select(instance.Substitute).ToArray();
            TypeEntry type = this.m_catalog.ResolveTypeDefinition(new BehaviorTypeReference(instance.Substitute(member.DeclaringTypeIdentity),
                new TypeIdentityTemplate(member.DeclaringTypeDefinitionId), arguments, member.TargetAssemblyIdentity,
                member.ReferringAssemblyPath, member.KnownDeclaringTypeId));
            return (type, arguments);
        }

        // 包括返回值未使用的静态字段读取，不把取成员信息本身当作初始化触发。
        private void RequireStaticFieldInitialization(int instanceId, BehaviorMemberReference member)
        {
            MethodCallInstance instance = GetInstance(instanceId);
            (TypeEntry type, TypeIdentityTemplate[] arguments) = ReadMemberType(member, instance);
            RequireIndependentInitialization(type, arguments, instance.RootId);
        }

        // 反射字段读取没有普通函数目标，但仍实际触发声明类型初始化。
        private void RequireRuntimeInitialization(ResolvedCall call)
        {
            if (call.CompletesWithoutTarget && call.Call.ResultValueId is int resultId
                && this.m_runtimeValues.TryGetValue(new(call.CallerMethodId, resultId, call.CallerInstanceId), out IReadOnlyList<ValueOrigin>? values))
            {
                foreach (ValueOrigin value in values.Where(value => value.Value.Kind == BehaviorValueKind.FieldRead && value.Value.InputValueIds.Count == 0))
                {
                    RequireStaticFieldInitialization(call.CallerInstanceId, value.Value.Member!);
                }
            }
        }

        // 方法内位置和所属调用环境使用同一份已证路径；未证明不可达的位置继续保留。
        internal bool IsReachable(int instanceId, int blockId)
        {
            return !this.m_inactiveInstances.Contains(instanceId) && this.m_unreachableBlocks.GetValueOrDefault(instanceId)?.Contains(blockId) != true;
        }

        // 将反射写入保存到当前调用实例，不混入其他实参环境。
        internal void AddRuntimeWrite(int instanceId, BehaviorWrite write)
        {
            if (!this.m_runtimeWrites.TryGetValue(instanceId, out List<BehaviorWrite>? writes))
            {
                writes = new List<BehaviorWrite>();
                this.m_runtimeWrites.Add(instanceId, writes);
            }
            writes.Add(write);
        }

        // 指令写入和运行时反射写入共用后续存储及效果流程。
        internal IEnumerable<BehaviorWrite> GetWrites(int instanceId, int? blockId = null)
        {
            string methodId = GetInstance(instanceId).MethodId;
            IEnumerable<BehaviorWrite> declared = blockId.HasValue ? this.m_flowGraphs[methodId].Writes[blockId.Value] : this.m_methods[methodId].Writes;
            return declared.Concat(this.m_runtimeWrites.TryGetValue(instanceId, out List<BehaviorWrite>? writes)
                ? writes.Where(write => !blockId.HasValue || write.Point.BlockId == blockId.Value) : Array.Empty<BehaviorWrite>())
                .Where(write => IsReachable(instanceId, write.Point.BlockId));
        }

        // 把运行时查找所得的类型或成员放回共同值来源。
        internal void BindRuntimeValue(BehaviorValueReference reference, IReadOnlyList<ValueOrigin> values)
        {
            this.m_runtimeValues.Add(reference, values);
        }

        // 把一个明确调用的返回值连接到其真实目标，供后续使用位置继续追踪。
        internal void BindCall(ResolvedCall call)
        {
            if (!this.m_callsByCallerInstance.TryGetValue(
                    call.CallerInstanceId,
                    out Dictionary<BehaviorFlowPoint, ResolvedCall>? calls))
            {
                calls = new Dictionary<BehaviorFlowPoint, ResolvedCall>();
                this.m_callsByCallerInstance.Add(call.CallerInstanceId, calls);
            }

            calls.Add(call.Call.Point, call);
            if (call.Call.ResultValueId is int resultId)
            {
                this.m_resultCalls.Add((call.CallerMethodId, resultId, call.CallerInstanceId), call);
            }
        }

        // 读取包含具体调用参数代入的值来源。
        /// <summary>沿具体调用点追踪返回值与参数，不混合其他调用的实参。</summary>
        public IReadOnlyList<ValueOrigin> GetCallOrigins(BehaviorValueReference reference, bool retainTypeChecks = false)
        {
            return GetRelativeOrigins(reference, 0, retainTypeChecks);
        }

        // 循环沿用本次查询已证明的来源，反复求值直到稳定，不跨调用发现批次缓存。
        internal IReadOnlyList<ValueOrigin> GetRelativeOrigins(BehaviorValueReference reference, int instanceId,
            bool retainTypeChecks = false, bool retainStorageReads = false)
        {
            var key = (reference, instanceId, retainTypeChecks, retainStorageReads);
            bool root = this.m_activeOriginQueries.Count == 0;
            if (this.m_activeOriginQueries.Contains(key))
            {
                this.m_originCycle = true;
                return this.m_originResults.GetValueOrDefault(key)?.Where(origin => origin.Value.Kind != BehaviorValueKind.CallResult).ToArray()
                    ?? Array.Empty<ValueOrigin>();
            }
            if (!root && !this.m_originCycle && this.m_activeStorageEffects.Count == 0 && this.m_completedOriginQueries.Contains(key))
            {
                return this.m_originResults[key];
            }
            if (!root && !this.m_originCycle && this.m_storageOriginResults.TryPeek(out var completed)
                && completed.TryGetValue(key, out IReadOnlyList<ValueOrigin>? cached))
            {
                return cached;
            }
            try
            {
                IReadOnlyList<ValueOrigin> result;
                bool changed;
                do
                {
                    var previousResults = root ? new Dictionary<(BehaviorValueReference, int, bool, bool), IReadOnlyList<ValueOrigin>>(this.m_originResults) : null;
                    if (root)
                    {
                        this.m_originCycle = false;
                        this.m_completedOriginQueries.Clear();
                        foreach (var scope in this.m_storageOriginResults)
                        {
                            scope.Clear();
                        }
                    }
                    this.m_completedOriginQueries.Remove(key);
                    bool outsideStorageEffect = this.m_activeStorageEffects.Count == 0;
                    this.m_activeOriginQueries.Add(key);
                    result = ReadCallOrigins(reference, retainTypeChecks, instanceId, retainStorageReads);
                    this.m_activeOriginQueries.Remove(key);
                    this.m_originResults[key] = result;
                    if (!this.m_originCycle && this.m_storageOriginResults.TryPeek(out var scopeResults))
                    {
                        scopeResults[key] = result;
                    }
                    if (outsideStorageEffect && !this.m_originCycle)
                    {
                        this.m_completedOriginQueries.Add(key);
                    }
                    changed = previousResults != null && (previousResults.Count != this.m_originResults.Count
                        || previousResults.Any(item => !this.m_originResults[item.Key].SequenceEqual(item.Value)));
                } while (root && this.m_originCycle && changed);
                return result;
            }
            finally
            {
                this.m_activeOriginQueries.Remove(key);
                if (root)
                {
                    this.m_originResults.Clear();
                    this.m_completedOriginQueries.Clear();
                    this.m_originClosedPrefixes.Clear();
                    foreach (var scope in this.m_storageOriginResults)
                    {
                        scope.Clear();
                    }
                }
            }
        }

        // 保留指定函数自己的参数，其余返回值仍通过同一来源流程展开。
        private IReadOnlyList<ValueOrigin> ReadCallOrigins(BehaviorValueReference reference, bool retainTypeChecks,
            int stopAtInstance = 0, bool retainStorageReads = false)
        {
            Queue<(BehaviorValueReference Reference, ReturnedValuePath? Path)> pending = new(new[] { (reference, (ReturnedValuePath?)null) });
            HashSet<(BehaviorValueReference, ReturnedValuePath?)> visited = new();
            HashSet<ValueOrigin> result = new();
            while (pending.TryDequeue(out var query))
            {
                if (!visited.Add(query))
                {
                    continue;
                }
                BehaviorValueReference current = query.Reference;

                foreach (ValueOrigin raw in ReadOrigins(current, retainTypeChecks, current.InstanceId != 0).SelectMany(origin =>
                             this.m_runtimeValues.TryGetValue(origin.Reference, out IReadOnlyList<ValueOrigin>? values)
                                 ? values : new[] { origin }))
                {
                    ValueOrigin origin = raw with { ReturnPath = MergeReturnPaths(raw.ReturnPath, query.Path) };
                    if (origin.Value.Point is BehaviorFlowPoint point && !IsReachable(origin.Reference.InstanceId, point.BlockId))
                    {
                        continue;
                    }
                    if (origin.Value.Kind == BehaviorValueKind.SlotRead)
                    {
                        BehaviorValueReference slot = origin.Reference with { ValueId = origin.Value.InputValueIds.Single() };
                        IReadOnlyList<BehaviorValueReference> values = ReadStoredValuesAtPoint(GetInstance(origin.Reference.InstanceId),
                            BehaviorWriteKind.Indirect, null, new[] { new StorageLocation(slot, "slot", string.Empty, string.Empty) },
                            origin.Value.Point!.Value, -1, origin.Reference, () => new[] { slot });
                        foreach (BehaviorValueReference stored in values)
                        {
                            if (stored == origin.Reference)
                            {
                                result.Add(origin with { Value = origin.Value with { Kind = BehaviorValueKind.CallResult } });
                            }
                            else
                            {
                                pending.Enqueue((stored, origin.ReturnPath));
                            }
                        }
                    }
                    else if (!retainStorageReads && origin.Value.Kind is BehaviorValueKind.FieldRead or BehaviorValueKind.ArrayElementRead)
                    {
                        foreach (BehaviorValueReference stored in ReadStoredValues(
                                     origin.Reference,
                                     origin.Value))
                        {
                            if (stored == origin.Reference)
                            {
                                result.Add(origin);
                            }
                            else
                            {
                                pending.Enqueue((stored, origin.ReturnPath));
                            }
                        }
                    }
                    else if (origin.Reference.InstanceId != 0
                        && origin.Reference.InstanceId != stopAtInstance
                        && origin.Value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance
                        && GetInstance(origin.Reference.InstanceId).Binding is ResolvedCallTarget binding)
                    {
                        IReadOnlyList<BehaviorValueReference> inputs =
                            origin.Value.Kind == BehaviorValueKind.Parameter
                                ? binding.Arguments[origin.Value.ParameterIndex!.Value]
                                : binding.Receiver;
                        foreach (BehaviorValueReference input in inputs)
                        {
                            pending.Enqueue((input, origin.ReturnPath));
                        }
                    }
                    else if (origin.Value.Kind == BehaviorValueKind.CallResult
                        && this.m_resultCalls.TryGetValue((origin.Reference.MethodId, origin.Reference.ValueId, origin.Reference.InstanceId), out ResolvedCall? call)
                        && call.Targets.All(target => this.m_methods.ContainsKey(target.MethodId)))
                    {
                        for (int index = 0; index < call.Targets.Count; index++)
                        {
                            ResolvedCallTarget target = call.Targets[index];
                            MethodBehavior body = this.m_methods[target.MethodId];
                            if (body.BodyKind != MethodBodyKind.Executable)
                            {
                                result.Add(origin);
                                continue;
                            }

                            RequireNonRecursiveReturn(origin.Reference.InstanceId, target.InstanceId);
                            foreach (BehaviorReturn returned in body.Returns.Where(item => item.ValueId.HasValue && IsReachable(target.InstanceId, item.Point.BlockId)))
                            {
                                if (HasClosedPrefix(target.InstanceId, returned.Point, this.m_originClosedPrefixes))
                                {
                                    foreach (var site in ReadReturnSites(target.InstanceId, returned, this.m_originClosedPrefixes))
                                    {
                                        pending.Enqueue((site.Reference, MergeReturnPaths(new ReturnedValuePath(target.InstanceId,
                                            returned.Point, site.Point, null), origin.ReturnPath)));
                                    }
                                }
                                else
                                {
                                    result.Add(origin);
                                }
                            }
                        }
                    }
                    else
                    {
                        result.Add(origin);
                    }
                }
            }

            return result.OrderBy(origin => origin.Reference.MethodId, StringComparer.Ordinal)
                .ThenBy(origin => origin.Reference.ValueId).ThenBy(origin => origin.Reference.InstanceId)
                .ThenBy(origin => origin.Value.Method?.Identity.Text, StringComparer.Ordinal).ToArray();
        }

        // 合并同一值经过的调用出口，不把矛盾分支或重复路径当成新来源。
        private static ReturnedValuePath? MergeReturnPaths(ReturnedValuePath? first, ReturnedValuePath? second)
        {
            if (first == null || second == null)
            {
                return first ?? second;
            }
            SortedDictionary<int, ReturnedValuePath> entries = new();
            foreach (ReturnedValuePath start in new[] { first, second })
            {
                for (ReturnedValuePath? item = start; item != null; item = item.Previous)
                {
                    if (entries.TryGetValue(item.InstanceId, out ReturnedValuePath? existing)
                        && (existing.ReturnPoint != item.ReturnPoint || existing.ValuePoint != item.ValuePoint))
                    {
                        throw new AnalysisException("同一对象的返回路径存在尚未闭合的分支关联");
                    }
                    entries[item.InstanceId] = item;
                }
            }
            ReturnedValuePath? result = null;
            foreach (ReturnedValuePath item in entries.Values.Reverse())
            {
                result = item with { Previous = result };
            }
            return result;
        }

        // 复用已有调用实例链识别递归返回，不另建第二套父环境。
        private void RequireNonRecursiveReturn(int callerId, int targetId)
        {
            int parentId = callerId;
            while (parentId != 0)
            {
                MethodCallInstance parent = this.m_instances[parentId];
                if (parentId == targetId)
                {
                    throw new AnalysisException($"递归返回值的调用参数尚未闭合：{parent.MethodId}");
                }

                parentId = parent.ParentId;
            }
        }

        // 读取变量当时的来源，并保留对象、参数、字段和调用结果各自的身份。
        /// <summary>取得一个值穿过赋值、分支合流与类型转换后的全部来源。</summary>
        public IReadOnlyList<ValueOrigin> GetOrigins(BehaviorValueReference reference, bool retainTypeChecks = false)
        {
            return ReadOrigins(reference, retainTypeChecks, false);
        }

        // 尚未读完的存储影响显式等待，不能冒充外部未知对象直接固定调用目标。
        private BehaviorValueReference ReadPendingStorageValue(BehaviorValueReference reference, BehaviorFlowPoint point, int? instanceId = null)
        {
            int owner = instanceId ?? reference.InstanceId;
            if (!this.m_addressReads.TryGetValue((reference, point, owner), out BehaviorValueReference pending))
            {
                pending = new BehaviorValueReference(GetInstance(owner).MethodId, --this.m_nextRuntimeValueId, owner);
                this.m_addressReads.Add((reference, point, owner), pending);
            }
            if (!this.m_runtimeValues.ContainsKey(pending))
            {
                IEnumerable<BehaviorValue> values = this.m_runtimeValues.TryGetValue(reference, out IReadOnlyList<ValueOrigin>? origins)
                    ? origins.Select(origin => origin.Value) : new[] { this.m_methods[reference.MethodId].Values[reference.ValueId] };
                BindRuntimeValue(pending, values.Select(value => new ValueOrigin(pending, value with
                { Id = pending.ValueId, Kind = BehaviorValueKind.CallResult, Point = point })).ToArray());
            }
            return pending;
        }

        // 只缓存局部事实展开；实际调用写回由使用时的存储查询处理。
        private IReadOnlyList<ValueOrigin> ReadOrigins(BehaviorValueReference reference, bool retainTypeChecks, bool retainSlots)
        {
            if (this.m_runtimeValues.TryGetValue(reference, out IReadOnlyList<ValueOrigin>? runtimeValues))
            {
                return runtimeValues;
            }
            if (this.m_origins.TryGetValue((reference, retainTypeChecks, retainSlots), out IReadOnlyList<ValueOrigin>? cached))
            {
                return cached;
            }

            Queue<BehaviorValueReference> pending = new(new[] { reference });
            HashSet<BehaviorValueReference> visited = new();
            List<ValueOrigin> result = new();
            while (pending.TryDequeue(out BehaviorValueReference current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                MethodBehavior method = this.m_methods[current.MethodId];
                BehaviorValue value = method.Values[current.ValueId];
                if (value.Type != null && current.InstanceId != 0)
                {
                    value = value with { Type = this.m_instances[current.InstanceId].Substitute(value.Type) };
                }
                IEnumerable<int>? inputs = value.Kind switch
                {
                    BehaviorValueKind.Conversion when retainTypeChecks
                        && (value.Reference is "castclass" or "isinst" or "box"
                            || value.Reference == "unbox.any" && CallTargetResolver.IsReferenceType(this.m_catalog, value.Type!.Id, this)) => null,
                    _ when value.Reference == "ldobj"
                        || (value.Kind == BehaviorValueKind.Computation
                            && value.Reference?.StartsWith("ldind.", StringComparison.Ordinal) == true) =>
                        ReadIndirectSources(method, value),
                    BehaviorValueKind.SlotRead when !retainSlots => ReadReachingValues(
                        method, value.InputValueIds.Single(), value.Point!.Value),
                    BehaviorValueKind.Conversion or BehaviorValueKind.Merge => value.InputValueIds,
                    _ => null,
                };
                if (inputs == null)
                {
                    result.Add(new ValueOrigin(current, value));
                }
                else
                {
                    foreach (int input in inputs)
                    {
                        if (input == current.ValueId)
                        {
                            result.Add(new ValueOrigin(current, value));
                        }
                        else
                        {
                            pending.Enqueue(current with { ValueId = input });
                        }
                    }
                }
            }

            ValueOrigin[] ordered = result.OrderBy(origin => origin.Reference.MethodId, StringComparer.Ordinal)
                .ThenBy(origin => origin.Reference.ValueId).ToArray();
            this.m_origins.Add((reference, retainTypeChecks, retainSlots), ordered);
            return ordered;
        }

        // 按对象和成员或下标确定读取位置，再沿执行关系寻找最后保存的值。
        private IReadOnlyList<BehaviorValueReference> ReadStoredValues(
            BehaviorValueReference reference,
            BehaviorValue value, IReadOnlyList<ValueOrigin>? selectedReceivers = null, ReturnedValuePath? returnedPath = null)
        {
            IReadOnlyList<ValueOrigin>? receivers = selectedReceivers ?? (value.InputValueIds.Count == 0 ? null
                : GetCallOrigins(reference with { ValueId = value.InputValueIds[0] }));
            if (receivers?.Count == 0)
            {
                return Array.Empty<BehaviorValueReference>();
            }
            if (selectedReceivers == null && receivers?.Any(origin => origin.ReturnPath != null) == true)
            {
                return receivers.GroupBy(origin => origin.ReturnPath).SelectMany(group =>
                    ReadStoredValues(reference, value, group.ToArray(), group.Key)).Distinct().ToArray();
            }
            IReadOnlyList<StorageLocation>? locations = ReadStorageLocations(reference, value.Member,
                value.InputValueIds.FirstOrDefault(-1),
                value.Kind == BehaviorValueKind.ArrayElementRead ? value.InputValueIds.Skip(1).ToArray() : Array.Empty<int>(), receivers);
            if (locations == null)
            {
                bool waiting = value.InputValueIds.Any(input =>
                {
                    IReadOnlyList<ValueOrigin> origins = input == value.InputValueIds[0] ? receivers!
                        : GetCallOrigins(reference with { ValueId = input });
                    return origins.Count == 0 || origins.Any(origin => origin.Value.Kind == BehaviorValueKind.CallResult);
                });
                return new[] { waiting ? ReadPendingStorageValue(reference, value.Point!.Value) : reference };
            }

            MethodCallInstance instance = GetInstance(reference.InstanceId);
            BehaviorWriteKind kind = value.Kind == BehaviorValueKind.FieldRead
                ? BehaviorWriteKind.Field
                : BehaviorWriteKind.ArrayElement;
            return ReadStoredValuesAtPoint(
                    instance,
                    kind,
                    value.Member,
                    locations,
                    value.Point!.Value,
                    value.InputValueIds.FirstOrDefault(-1),
                    ReadPendingStorageValue(reference, value.Point.Value),
                    () => ReadStorageValuesBeforeInvocation(instance, kind, value.Member, locations, reference, returnedPath), returnedPath: returnedPath)
                .Select(item => returnedPath == null ? item : ObserveValue(item, reference.InstanceId, returnedPath))
                .OrderBy(item => item.MethodId, StringComparer.Ordinal)
                .ThenBy(item => item.ValueId)
                .ThenBy(item => item.InstanceId)
                .ToArray();
        }

        // 读取本次函数调用之前同一存储的内容，逐层保留真实父调用的执行位置。
        private IReadOnlyList<BehaviorValueReference> ReadStorageValuesBeforeInvocation(
            MethodCallInstance instance,
            BehaviorWriteKind kind,
            BehaviorMemberReference? member,
            IReadOnlyList<StorageLocation> locations,
            BehaviorValueReference unresolvedRead, ReturnedValuePath? returnedPath = null)
        {
            if (instance.ParentId == 0 || !instance.InvocationPoint.HasValue)
            {
                return new[] { unresolvedRead };
            }

            MethodCallInstance parent = GetInstance(instance.ParentId);
            return ReadStoredValuesAtPoint(
                parent,
                kind,
                member,
                locations,
                instance.InvocationPoint.Value,
                -1,
                ReadPendingStorageValue(unresolvedRead, instance.InvocationPoint.Value, parent.Id),
                () => ReadStorageValuesBeforeInvocation(parent, kind, member, locations, unresolvedRead, returnedPath), returnedPath: returnedPath);
        }

        // 在一个函数实例内合并本地写入与此前已经闭合的子调用效果。
        private IReadOnlyList<BehaviorValueReference> ReadStoredValuesAtPoint(
            MethodCallInstance instance,
            BehaviorWriteKind kind,
            BehaviorMemberReference? member,
            IReadOnlyList<StorageLocation> locations,
            BehaviorFlowPoint point,
            int readReceiverId,
            BehaviorValueReference unresolvedRead,
            Func<IReadOnlyList<BehaviorValueReference>> initialValues,
            Func<ResolvedCall, ReachingWrite<BehaviorValueReference>?>? readCallWrite = null,
            BehaviorFlowPoint? throughPoint = null, ReturnedValuePath? returnedPath = null,
            Dictionary<FlowSearchPoint, ReachingWrite<BehaviorValueReference>?>? trace = null)
        {
            MethodBehavior method = this.m_methods[instance.MethodId];
            bool referenceSlotOnly = kind == BehaviorWriteKind.Indirect && locations.All(location => location.Definition == "slot"
                && location.Receiver is { ValueId: >= 0 } receiver && this.m_methods[receiver.MethodId].Values[receiver.ValueId].IsManagedReferenceSlot);
            this.m_callsByCallerInstance.TryGetValue(instance.Id, out Dictionary<BehaviorFlowPoint, ResolvedCall>? calls);

            // 逐控制流位置提供可能改变目标存储的写入。
            IEnumerable<ReachingWrite<BehaviorValueReference>> ReadWrites(FlowSearchPoint current)
            {
                ValueFlowGraph graph = this.m_flowGraphs[method.MethodId];
                foreach (BehaviorMemberReference initialized in graph.StaticReads[current.Node.BlockId].Where(value => value.Point!.Value.Order < current.Order)
                    .Select(value => value.Member!).Concat(GetWrites(instance.Id, current.Node.BlockId)
                        .Where(write => write.Kind == BehaviorWriteKind.Field && write.ReceiverValueId == null && write.Point.Order < current.Order)
                        .Select(write => write.Member!)))
                {
                    RequireStaticFieldInitialization(instance.Id, initialized);
                }
                foreach (StorageLocation location in locations.Where(location => kind == BehaviorWriteKind.Field && member != null
                             && location.Receiver?.InstanceId == instance.Id))
                {
                    BehaviorValue allocated = method.Values[location.Receiver!.Value.ValueId];
                    if (allocated.Kind == BehaviorValueKind.NewObject && allocated.Point!.Value.BlockId == current.Node.BlockId
                        && allocated.Point.Value.Order < current.Order
                        && ReadDefaultFieldValue(location, member!) is BehaviorValueReference defaultValue)
                    {
                        yield return new ReachingWrite<BehaviorValueReference>(new[] { defaultValue }, allocated.Point.Value, locations.Count == 1);
                    }
                }
                foreach (BehaviorAssignment assignment in graph.Assignments[current.Node.BlockId].Where(assignment => kind == BehaviorWriteKind.Indirect
                    && locations.Any(location => location.Definition == "slot" && location.Receiver ==
                        new BehaviorValueReference(method.MethodId, assignment.TargetValueId, instance.Id))
                    && assignment.Point.Order < current.Order))
                {
                    yield return new ReachingWrite<BehaviorValueReference>(
                        new[] { new BehaviorValueReference(method.MethodId, assignment.ValueId, instance.Id) }, assignment.Point, true);
                }
                foreach (BehaviorWrite write in GetWrites(instance.Id, current.Node.BlockId).Where(write =>
                             !referenceSlotOnly && MayWriteStorage(write, kind, member?.Name, locations.All(location => location.Definition == "slot"))
                             && write.Point.Order < current.Order))
                {
                    BehaviorValueReference written = new(
                        method.MethodId,
                        write.ValueId,
                        InstanceId: instance.Id);
                    IReadOnlyList<StorageLocation>? targets = ReadStorageLocations(
                        written,
                        write.Member,
                        write.ReceiverValueId ?? -1,
                        write.IndexValueIds, expectedReceivers: locations.Select(location => location.Receiver));
                    if (targets == null)
                    {
                        yield return new ReachingWrite<BehaviorValueReference>(
                            new[] { written, unresolvedRead },
                            write.Point,
                            false);
                    }
                    else if (targets.Any(target => locations.Any(location => StorageLocationsMatch(target, location))))
                    {
                        if (targets.Count > 1 && GetCallOrigins(written).Count > 1)
                        {
                            throw new AnalysisException(
                                $"对象与保存值的分支对应尚未闭合：{method.MethodId} @ {write.Position}");
                        }

                        yield return new ReachingWrite<BehaviorValueReference>(
                            new[] { written },
                            write.Point,
                            targets.Count == 1 && locations.Count == 1
                            || readReceiverId >= 0 && HasSameReceiverSnapshot(
                                method,
                                write.ReceiverValueId,
                                readReceiverId));
                    }
                }

                foreach (BehaviorCall call in graph.Calls[current.Node.BlockId].Where(call => !referenceSlotOnly && call.Point.Order < current.Order))
                {
                    ResolvedCall? resolved = calls?.GetValueOrDefault(call.Point);
                    if (resolved == null)
                    {
                        if (kind == BehaviorWriteKind.Indirect && locations.All(location => location.Definition == "slot")
                            && call.Arguments.All(argument => argument.RefKind == CatalogRefKind.None)
                            && (call.ReceiverValueId == null || call.ConstrainedReceiverType == null
                                && !this.m_catalog.TypesById[this.m_catalog.ResolveMethodDefinition(call.Target,
                                    call.Kind != BehaviorCallKind.ObjectCreation).Method.TypeId].IsValueType))
                        {
                            continue;
                        }
                        yield return new ReachingWrite<BehaviorValueReference>(
                            new[] { ReadPendingStorageValue(unresolvedRead, call.Point, instance.Id) },
                            call.Point,
                            false);
                        continue;
                    }

                    ReachingWrite<BehaviorValueReference>? callWrite = readCallWrite != null ? readCallWrite(resolved) : ReadStorageCallWrite(
                        resolved,
                        kind,
                        member,
                        locations,
                        unresolvedRead, returnedPath);
                    if (callWrite != null)
                    {
                        yield return callWrite;
                    }
                }
            }

            return ReadReachingValues(method, point, ReadWrites, initialValues, instance.Id, throughPoint, trace).ToArray();
        }

        // 分配时只为确定类型建立零值或空引用，构造与后续写入仍按原执行顺序覆盖它。
        private BehaviorValueReference? ReadDefaultFieldValue(StorageLocation location, BehaviorMemberReference member)
        {
            if (this.m_defaultFieldValues.TryGetValue(location, out BehaviorValueReference cached))
            {
                return cached;
            }
            string? stackType = location.ValueType switch
            {
                "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => "System.Int32",
                "System.Int64" or "System.UInt64" => "System.Int64",
                "System.Single" or "System.Double" => location.ValueType,
                _ => null,
            };
            if (stackType == null && !CallTargetResolver.IsReferenceType(this.m_catalog, location.ValueType!, this))
            {
                return null;
            }
            BehaviorValueReference reference = location.Receiver!.Value with { ValueId = --this.m_nextRuntimeValueId };
            BehaviorTypeReference? type = stackType == null ? null : new BehaviorTypeReference(new TypeIdentityTemplate(stackType),
                new TypeIdentityTemplate(stackType), Array.Empty<TypeIdentityTemplate>(), null, member.ReferringAssemblyPath, null);
            BindRuntimeValue(reference, new[] { new ValueOrigin(reference,
                new BehaviorValue(reference.ValueId, BehaviorValueKind.Constant, stackType == null ? null : "0", null, Array.Empty<int>()) { Type = type }) });
            this.m_defaultFieldValues.Add(location, reference);
            return reference;
        }

        // 把同一调用点所有运行时目标对指定存储的效果合并为一次写入。
        private ReachingWrite<BehaviorValueReference>? ReadStorageCallWrite(
            ResolvedCall call,
            BehaviorWriteKind kind,
            BehaviorMemberReference? member,
            IReadOnlyList<StorageLocation> locations,
            BehaviorValueReference unresolvedRead, ReturnedValuePath? returnedPath = null)
        {
            RequireRuntimeInitialization(call);
            HashSet<BehaviorValueReference> values = new();
            bool includesPrevious = false;
            bool hasEffect = false;
            int? selectedTarget = null;
            for (ReturnedValuePath? path = returnedPath; path != null; path = path.Previous)
            {
                if (call.Targets.Any(target => target.InstanceId == path.InstanceId))
                {
                    if (selectedTarget.HasValue && selectedTarget.Value != path.InstanceId)
                    {
                        throw new AnalysisException("同一次调用的互斥返回目标尚未闭合");
                    }
                    selectedTarget = path.InstanceId;
                }
            }
            foreach (ResolvedCallTarget target in call.Targets.Where(target => !selectedTarget.HasValue || target.InstanceId == selectedTarget))
            {
                MethodEntry definition = this.m_definitions[target.MethodId];
                if (kind == BehaviorWriteKind.Indirect && locations.All(location => location.Definition == "slot")
                    && !definition.Parameters.Select((parameter, index) => (parameter, index))
                        .Where(item => item.parameter.RefKind != CatalogRefKind.None)
                        .SelectMany(item => target.Arguments[item.index])
                        .Concat(!definition.IsStatic && this.m_catalog.TypesById[definition.TypeId].IsValueType
                            ? target.Receiver : Array.Empty<BehaviorValueReference>()).Any(argument =>
                        {
                            IReadOnlyList<StorageLocation>? passed = ReadStorageLocations(argument, null, argument.ValueId, Array.Empty<int>());
                            return passed == null || passed.Any(location => locations.Any(expected => StorageLocationsMatch(location, expected)));
                        }))
                {
                    continue;
                }
                if (!this.m_methods.TryGetValue(target.MethodId, out MethodBehavior? method))
                {
                    values.Add(unresolvedRead);
                    includesPrevious = true;
                    hasEffect = true;
                    continue;
                }

                if (method.BodyKind != MethodBodyKind.Executable)
                {
                    if (method.BodyKind == MethodBodyKind.RuntimeImplementation && IsRuntimeDelegateCreation(call, target))
                    {
                        continue;
                    }

                    throw new AnalysisException($"跨函数存储效果尚未闭合：{method.MethodId}");
                }

                hasEffect = true;
                StorageEffect effect = ReadStorageEffect(
                    target.InstanceId,
                    kind,
                    member,
                    locations,
                    unresolvedRead, returnedPath);
                values.UnionWith(effect.Values);
                includesPrevious |= effect.IncludesPrevious;
            }

            return hasEffect
                ? new ReachingWrite<BehaviorValueReference>(values.ToArray(), call.Call.Point, !includesPrevious)
                : null;
        }

        // 只把已经证明绑定函数的真实 CLR 委托构造视为不读写旧存储。
        internal bool IsRuntimeDelegateCreation(ResolvedCall call, ResolvedCallTarget target)
        {
            if (call.Call.Kind != BehaviorCallKind.ObjectCreation
                || call.Call.ResultValueId is not int resultId
                || !this.m_methods.TryGetValue(call.CallerMethodId, out MethodBehavior? caller)
                || !this.m_definitions.TryGetValue(target.MethodId, out MethodEntry? constructor)
                || constructor.Kind != CatalogMethodKind.Constructor
                || !this.m_catalog.TypesById.TryGetValue(constructor.TypeId, out TypeEntry? declaringType))
            {
                return false;
            }

            BehaviorValue created = caller.Values[resultId];

            return created.Kind == BehaviorValueKind.NewObject
                && created.InputValueIds.Any(inputId => caller.Values[inputId].Kind == BehaviorValueKind.Function)
                && this.m_catalog.IsDelegateType(declaringType);
        }

        // 计算一个实际函数实例相对调用前内容的存储效果。
        private StorageEffect ReadStorageEffect(
            int instanceId,
            BehaviorWriteKind kind,
            BehaviorMemberReference? member,
            IReadOnlyList<StorageLocation> locations,
            BehaviorValueReference unresolvedRead, ReturnedValuePath? returnedPath = null)
        {
            StorageEffectQuery query = new(instanceId, kind, ReadStorageLocationKey(locations));
            if (!this.m_activeStorageEffects.Add(query))
            {
                throw new AnalysisException($"跨函数存储效果形成尚未闭合的循环：{GetInstance(instanceId).MethodId}");
            }
            this.m_storageOriginResults.Push(new());
            try
            {
                MethodCallInstance instance = GetInstance(instanceId);
                MethodBehavior method = this.m_methods[instance.MethodId];
                RequireIndependentInitialization(instanceId);
                ReturnedValuePath? selected = returnedPath;
                while (selected != null && selected.InstanceId != instanceId)
                {
                    selected = selected.Previous;
                }
                if (method.BodyKind != MethodBodyKind.Executable || method.Returns.Count == 0)
                {
                    throw new AnalysisException($"跨函数存储效果尚未闭合：{method.MethodId}");
                }
                if (HasNoStorageWrites(instanceId, kind, member?.Name, locations.All(location => location.Definition == "slot")))
                {
                    return new StorageEffect(Array.Empty<BehaviorValueReference>(), true);
                }

                HashSet<BehaviorValueReference> values = new();
                Dictionary<BehaviorFlowPoint, ReachingWrite<BehaviorValueReference>?> callWrites = new();
                // 各出口共用同一次子调用查询，存储输入和活动祖先在本次计算内保持不变。
                ReachingWrite<BehaviorValueReference>? ReadCallWrite(ResolvedCall call)
                {
                    bool reusable = this.m_activeOriginQueries.Count != 0 && !this.m_originCycle;
                    if (reusable && callWrites.TryGetValue(call.Call.Point, out ReachingWrite<BehaviorValueReference>? cached))
                    {
                        return cached;
                    }
                    ReachingWrite<BehaviorValueReference>? result = ReadStorageCallWrite(call, kind, member, locations, unresolvedRead, returnedPath);
                    if (reusable && this.m_activeOriginQueries.Count != 0 && !this.m_originCycle)
                    {
                        callWrites[call.Call.Point] = result;
                    }
                    return result;
                }
                foreach (BehaviorReturn returned in method.Returns.Where(returned => IsReachable(instanceId, returned.Point.BlockId)
                    && (selected == null || selected.ReturnPoint == returned.Point)))
                {
                    values.UnionWith(ReadStoredValuesAtPoint(
                        instance,
                        kind,
                        member,
                        locations,
                        returned.Point,
                        -1,
                        unresolvedRead,
                        () => new[] { s_previousStorageValue }, ReadCallWrite, selected?.ValuePoint, returnedPath));
                }

                bool includesPrevious = values.Remove(s_previousStorageValue);
                return new StorageEffect(values.ToArray(), includesPrevious);
            }
            finally
            {
                this.m_activeStorageEffects.Remove(query);
                this.m_storageOriginResults.Pop();
            }
        }

        // 倒查与无关写入证明共用筛选，未知间接地址始终保留。
        private static bool MayWriteStorage(BehaviorWrite write, BehaviorWriteKind kind, string? memberName, bool localSlotOnly)
        {
            return (write.Kind == kind || write.Kind == BehaviorWriteKind.Indirect || kind == BehaviorWriteKind.Indirect && !localSlotOnly)
                && (memberName == null || write.Member?.Name == memberName || write.Kind == BehaviorWriteKind.Indirect);
        }

        // 仅整个已绑定调用闭包均不修改该类存储且有正常出口时复用正证。
        private bool HasNoStorageWrites(int instanceId, BehaviorWriteKind kind, string? memberName, bool localSlotOnly)
        {
            Queue<int> pending = new(new[] { instanceId });
            HashSet<int> visited = new();
            while (pending.TryDequeue(out int current))
            {
                if (this.m_storageReadOnlyInstances.Contains((current, kind, memberName, localSlotOnly)) || !visited.Add(current))
                {
                    continue;
                }
                RequireIndependentInitialization(current);
                if (!this.m_methods.TryGetValue(GetInstance(current).MethodId, out MethodBehavior? body)
                    || body.BodyKind != MethodBodyKind.Executable || body.Returns.Count == 0)
                {
                    return false;
                }
                foreach (BehaviorMemberReference member in ReadValueFlowGraph(body).StaticReads.SelectMany(group => group)
                    .Where(value => IsReachable(current, value.Point!.Value.BlockId)).Select(value => value.Member!)
                    .Concat(GetWrites(current).Where(write => write.Kind == BehaviorWriteKind.Field && write.ReceiverValueId == null).Select(write => write.Member!)))
                {
                    RequireStaticFieldInitialization(current, member);
                }
                if (GetWrites(current).Any(write => MayWriteStorage(write, kind, memberName, localSlotOnly))
                    || kind == BehaviorWriteKind.Field && body.Values.Any(value => value.Kind == BehaviorValueKind.NewObject)
                    || body.Calls.Any(call => IsReachable(current, call.Point.BlockId)
                        && this.m_callsByCallerInstance.GetValueOrDefault(current)?.ContainsKey(call.Point) != true))
                {
                    return false;
                }
                foreach (ResolvedCall call in (this.m_callsByCallerInstance.GetValueOrDefault(current)?.Values ?? Enumerable.Empty<ResolvedCall>())
                             .Where(call => IsReachable(current, call.Call.Point.BlockId)))
                {
                    RequireRuntimeInitialization(call);
                    foreach (ResolvedCallTarget target in call.Targets)
                    {
                        pending.Enqueue(target.InstanceId);
                    }
                }
            }
            this.m_storageReadOnlyInstances.UnionWith(visited.Select(current => (current, kind, memberName, localSlotOnly)));
            return true;
        }

        // 为活动效果查询生成稳定键，避免跨函数存储循环导致无限递归。
        private static string ReadStorageLocationKey(IReadOnlyList<StorageLocation> locations)
        {
            return string.Join("|", locations.OrderBy(location => location.Member, StringComparer.Ordinal)
                .ThenBy(location => location.Indices, StringComparer.Ordinal)
                .ThenBy(location => location.Receiver?.MethodId, StringComparer.Ordinal)
                .ThenBy(location => location.Receiver?.ValueId)
                .ThenBy(location => location.Receiver?.InstanceId)
                .Select(location => $"{location.Receiver}:{location.Member}:{location.Indices}"));
        }

        // 保存确定对象的存储身份；尚未知道对象或下标时保留原始读取等待闭合。
        private IReadOnlyList<StorageLocation>? ReadStorageLocations(
            BehaviorValueReference reference, BehaviorMemberReference? member, int receiverId, IReadOnlyList<int> indices,
            IReadOnlyList<ValueOrigin>? receivers = null, IEnumerable<BehaviorValueReference?>? expectedReceivers = null)
        {
            string memberId = string.Empty;
            string definitionId = string.Empty;
            string? valueType = member == null ? null : GetInstance(reference.InstanceId).Substitute(this.m_catalog.ReadResolvedFieldType(member)).Text;
            if (member != null)
            {
                MethodCallInstance instance = GetInstance(reference.InstanceId);
                (TypeEntry declaringType, TypeIdentityTemplate[] arguments) = ReadMemberType(member, instance);
                if (receiverId != -1 && declaringType.IsExplicitLayout)
                {
                    throw new AnalysisException($"显式布局字段的重叠关系尚未闭合：{declaringType.Id}::{member.Name}");
                }
                memberId = declaringType.Id + "<" + string.Join(",", arguments.Select(type => type.Text))
                    + ">::" + member.Name;
                definitionId = declaringType.Id + "::" + member.Name;
            }
            if (receiverId == -1)
            {
                return new[] { new StorageLocation(null, definitionId, memberId, string.Empty, valueType) };
            }

            receivers ??= GetCallOrigins(reference with { ValueId = receiverId });
            if (receivers.Count == 0)
            {
                return null;
            }
            if (member == null && indices.Count == 0)
            {
                List<StorageLocation> slots = new();
                foreach (ValueOrigin receiver in receivers)
                {
                    if (receiver.Value.Kind is not (BehaviorValueKind.Address or BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance))
                    {
                        return null;
                    }
                    if (receiver.Value.Kind == BehaviorValueKind.Address && (receiver.Value.Member != null
                        || receiver.Value.InputValueIds.Count == 2 && receiver.Value.Type != null))
                    {
                        IReadOnlyList<StorageLocation>? addressed = ReadStorageLocations(receiver.Reference, receiver.Value.Member,
                            receiver.Value.InputValueIds.FirstOrDefault(-1), receiver.Value.InputValueIds.Skip(1).ToArray(),
                            expectedReceivers: receivers.Count == 1 ? expectedReceivers : null);
                        if (addressed == null)
                        {
                            return null;
                        }
                        slots.AddRange(addressed);
                        continue;
                    }
                    int[] targets = ReadAddressedSlots(this.m_methods[receiver.Reference.MethodId], receiver.Reference.ValueId).ToArray();
                    if (targets.Length == 0)
                    {
                        return null;
                    }
                    slots.AddRange(targets.Select(slot => new StorageLocation(receiver.Reference with { ValueId = slot },
                        "slot", string.Empty, string.Empty)));
                }
                return slots.Distinct().ToArray();
            }
            if (receivers.Any(origin => origin.Value.Kind is not (BehaviorValueKind.NewObject or BehaviorValueKind.NewArray)))
            {
                return null;
            }
            if (expectedReceivers != null && receivers.All(origin => !expectedReceivers.Contains(origin.Reference)))
            {
                return Array.Empty<StorageLocation>();
            }

            if (new TypeIdentityTemplate(memberId).HasUnspecifiedParameter)
            {
                throw new AnalysisException($"未指定类型参数参与对象存储身份：{reference.MethodId}，字段 {member!.Name}");
            }

            foreach (ValueOrigin receiver in receivers.Where(receiver => !this.m_singleAllocations.Contains((receiver.Reference.MethodId, receiver.Value.Point!.Value.BlockId))))
            {
                MethodBehavior body = this.m_methods[receiver.Reference.MethodId];
                int allocationBlock = receiver.Value.Point!.Value.BlockId;
                ValueFlowGraph graph = ReadValueFlowGraph(body);
                if (!graph.NodesByBlockId.TryGetValue(allocationBlock, out FlowNode[]? allocations))
                {
                    throw new AnalysisException($"对象分配位置没有可达控制流：{body.MethodId} @ {allocationBlock}");
                }
                Queue<FlowNode> pending = new(allocations.SelectMany(node => graph.Predecessors.GetValueOrDefault(node) ?? Array.Empty<FlowNode>()));
                HashSet<FlowNode> visited = new();
                while (pending.TryDequeue(out FlowNode node))
                {
                    if (node.BlockId == allocationBlock)
                    {
                        throw new AnalysisException($"循环创建对象的存储身份尚未闭合：{body.MethodId}，值 {receiver.Reference.ValueId}");
                    }

                    if (visited.Add(node))
                    {
                        foreach (FlowNode predecessor in graph.Predecessors.GetValueOrDefault(node) ?? Array.Empty<FlowNode>())
                        {
                            pending.Enqueue(predecessor);
                        }
                    }
                }
                this.m_singleAllocations.Add((body.MethodId, allocationBlock));
            }

            List<string> indexValues = new();
            foreach (int index in indices)
            {
                IReadOnlyList<ValueOrigin> origins = GetCallOrigins(reference with { ValueId = index });
                if (origins.Count != 1 || origins[0].Value.Kind != BehaviorValueKind.Constant)
                {
                    return null;
                }

                indexValues.Add(origins[0].Value.Reference!);
            }

            return receivers.Select(origin => new StorageLocation(origin.Reference, definitionId, memberId,
                string.Join(",", indexValues), valueType)).Distinct().ToArray();
        }

        // 比较同一字段的实际存储，不能把尚未指定的类型参数猜成互不相等。
        private static bool StorageLocationsMatch(StorageLocation left, StorageLocation right)
        {
            if (left.Receiver != right.Receiver || left.Definition != right.Definition || left.Indices != right.Indices)
            {
                return false;
            }
            if (left.Member == right.Member)
            {
                return true;
            }
            if (new TypeIdentityTemplate(left.Member).HasUnspecifiedParameter
                || new TypeIdentityTemplate(right.Member).HasUnspecifiedParameter)
            {
                throw new AnalysisException($"泛型存储的重合关系尚未闭合：{left.Member}，{right.Member}");
            }
            return false;
        }

        private readonly record struct StorageLocation(BehaviorValueReference? Receiver, string Definition, string Member, string Indices, string? ValueType = null);

        private readonly record struct StorageEffect(
            IReadOnlyList<BehaviorValueReference> Values,
            bool IncludesPrevious);

        private readonly record struct StorageEffectQuery(
            int InstanceId,
            BehaviorWriteKind Kind,
            string Locations);

        private sealed record ReachingWrite<T>(
            IReadOnlyList<T> Values,
            BehaviorFlowPoint Point,
            bool ReplacesPrevious)
            where T : notnull;

        // 同一未重新赋值的接收变量在写入和读取处必然指向同一对象。
        private bool HasSameReceiverSnapshot(MethodBehavior method, int? writtenId, int readId)
        {
            if (!writtenId.HasValue || readId < 0)
            {
                return false;
            }

            BehaviorValue written = method.Values[writtenId.Value];
            BehaviorValue read = method.Values[readId];
            return written.Kind == BehaviorValueKind.SlotRead && read.Kind == BehaviorValueKind.SlotRead
                && written.InputValueIds.Single() == read.InputValueIds.Single()
                && ReadReachingValues(method, written.InputValueIds.Single(), written.Point!.Value)
                    .SequenceEqual(ReadReachingValues(method, read.InputValueIds.Single(), read.Point!.Value));
        }

        // 读取引用所指存储的当前内容，未被改写的外部内容仍保留本次读取身份。
        private IReadOnlyList<int>? ReadIndirectSources(MethodBehavior method, BehaviorValue value)
        {
            IReadOnlyList<int> slots = ReadAddressedSlots(method, value.InputValueIds.Single());
            return slots.Count == 0 ? null : slots.SelectMany(slot =>
                ReadReachingValues(method, slot, value.Point!.Value, value.Id)).Distinct().ToArray();
        }

        // 从使用位置倒查每条路径最后一次赋值，不混入已经被覆盖的历史值。
        private IReadOnlyList<int> ReadReachingValues(
            MethodBehavior method, int slotId, BehaviorFlowPoint point, int? initialValueId = null)
        {
            // 只读取当前倒查位置之前的写入，引用槽解析不会反过来使用未来赋值。
            IEnumerable<ReachingWrite<int>> ReadWrites(FlowSearchPoint current)
            {
                ValueFlowGraph graph = this.m_flowGraphs[method.MethodId];
                foreach (BehaviorAssignment assignment in graph.Assignments[current.Node.BlockId].Where(item => item.TargetValueId == slotId
                             && item.Point.Order < current.Order))
                {
                    yield return new ReachingWrite<int>(
                        new[] { assignment.ValueId },
                        assignment.Point,
                        true);
                }

                foreach (BehaviorWrite write in graph.Writes[current.Node.BlockId].Where(item => (slotId < 0 || !method.Values[slotId].IsManagedReferenceSlot)
                             && item.Kind == BehaviorWriteKind.Indirect
                             && item.Point.Order < current.Order))
                {
                    IReadOnlyList<int> targets = ReadAddressedSlots(method, write.ReceiverValueId!.Value);
                    if (targets.Contains(slotId))
                    {
                        yield return new ReachingWrite<int>(
                            new[] { write.ValueId },
                            write.Point,
                            targets.Count == 1);
                    }
                }
            }

            return ReadReachingValues(
                    method,
                    point,
                    ReadWrites,
                    () => new[] { initialValueId ?? slotId })
                .Order()
                .ToArray();
        }

        // 局部槽、字段和数组共用倒查；只有到达入口仍未覆盖，才读取调用前的值。
        private HashSet<T> ReadReachingValues<T>(
            MethodBehavior method, BehaviorFlowPoint point,
            Func<FlowSearchPoint, IEnumerable<ReachingWrite<T>>> readWrites,
            Func<IReadOnlyList<T>> initialValues, int instanceId = 0, BehaviorFlowPoint? throughPoint = null,
            Dictionary<FlowSearchPoint, ReachingWrite<T>?>? trace = null)
            where T : notnull
        {
            ValueFlowGraph graph = ReadValueFlowGraph(method);
            FlowNode[] startNodes = graph.NodesByBlockId.TryGetValue(
                    point.BlockId,
                    out FlowNode[]? reachableNodes)
                ? reachableNodes
                : throw new AnalysisException(
                    $"值使用位置没有可达控制流：{method.MethodId} @ {point.BlockId}:{point.Order}");
            HashSet<FlowNode>? following = null;
            if (throughPoint.HasValue && throughPoint.Value != point)
            {
                BehaviorFlowPoint required = throughPoint.Value;
                int returnSlot = graph.Assignments[required.BlockId].Single(assignment => assignment.Point == required).TargetValueId;
                following = new();
                Queue<FlowNode> forward = new(graph.NodesByBlockId[required.BlockId]);
                while (forward.TryDequeue(out FlowNode node))
                {
                    if (!IsReachable(instanceId, node.BlockId) || !following.Add(node))
                    {
                        continue;
                    }
                    foreach (FlowNode next in graph.Successors[node].Where(next =>
                        this.m_excludedEdges.GetValueOrDefault(instanceId)?.Contains((node.BlockId, next.BlockId)) != true
                        && !graph.Assignments[next.BlockId].Any(assignment => assignment.TargetValueId == returnSlot)))
                    {
                        forward.Enqueue(next);
                    }
                }
            }
            Queue<(FlowSearchPoint Search, bool Through)> pending = new(startNodes.Select(node =>
                (new FlowSearchPoint(node, point.Order), following == null)));
            HashSet<(FlowSearchPoint, bool)> visited = new();
            HashSet<T> result = new();
            while (pending.TryDequeue(out var search))
            {
                FlowSearchPoint current = search.Search;
                bool through = search.Through || current.Node.BlockId == throughPoint?.BlockId;
                if (!IsReachable(instanceId, current.Node.BlockId) || !through && !following!.Contains(current.Node)
                    || !visited.Add((current, through)))
                {
                    continue;
                }

                ReachingWrite<T>? write = readWrites(current).MaxBy(item => item.Point.Order);
                trace?.Add(current, write);
                if (write != null)
                {
                    foreach (T value in write.Values)
                    {
                        result.Add(value);
                    }
                    if (!write.ReplacesPrevious)
                    {
                        pending.Enqueue((new FlowSearchPoint(current.Node, write.Point.Order), through));
                    }
                }
                else if (current.Node.BlockId == -1)
                {
                    result.UnionWith(initialValues());
                }
                else if (graph.Predecessors.TryGetValue(
                             current.Node,
                             out IReadOnlyList<FlowNode>? incoming))
                {
                    foreach (FlowNode predecessor in incoming.Where(predecessor =>
                        this.m_excludedEdges.GetValueOrDefault(instanceId)?.Contains((predecessor.BlockId, current.Node.BlockId)) != true))
                    {
                        pending.Enqueue((new FlowSearchPoint(predecessor, int.MaxValue), through));
                    }
                }
            }

            if (result.Count == 0)
            {
                throw new AnalysisException(
                    $"值来源没有可到达的赋值：{method.MethodId} @ {point.BlockId}:{point.Order}");
            }

            return result;
        }

        // 沿引用局部变量找到间接写入真正指向的参数或局部槽。
        private IReadOnlyList<int> ReadAddressedSlots(MethodBehavior method, int valueId)
        {
            var query = (method.MethodId, valueId);
            if (!this.m_activeAddressQueries.Add(query))
            {
                throw new AnalysisException($"地址来源循环尚未闭合：{method.MethodId}，值 {valueId}");
            }
            try
            {
                return ReadAddressedSlotsCore(method, valueId);
            }
            finally
            {
                this.m_activeAddressQueries.Remove(query);
            }
        }

        // 托管引用槽和所指数据分开追踪，原生指针不得套用托管引用的不别名规则。
        private IReadOnlyList<int> ReadAddressedSlotsCore(MethodBehavior method, int valueId)
        {
            Queue<int> pending = new(new[] { valueId });
            HashSet<int> visited = new();
            HashSet<int> result = new();
            while (pending.TryDequeue(out int current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                BehaviorValue value = method.Values[current];
                if (value.Kind is BehaviorValueKind.Parameter or BehaviorValueKind.CurrentInstance && value.IsManagedReferenceSlot)
                {
                    result.Add(-value.Id - 1);
                }
                else if (value.Kind == BehaviorValueKind.Address
                    && value.Member == null
                    && value.InputValueIds.Count == 1
                    && method.Values[value.InputValueIds[0]].Kind is BehaviorValueKind.Local
                        or BehaviorValueKind.Parameter
                        or BehaviorValueKind.CurrentInstance)
                {
                    result.Add(value.InputValueIds[0]);
                }
                else if (value.Kind == BehaviorValueKind.SlotRead)
                {
                    foreach (int reaching in ReadReachingValues(
                                 method,
                                 value.InputValueIds.Single(),
                                 value.Point!.Value))
                    {
                        pending.Enqueue(reaching);
                    }
                }
                else if (value.Kind is BehaviorValueKind.Conversion or BehaviorValueKind.Merge)
                {
                    foreach (int input in value.InputValueIds)
                    {
                        pending.Enqueue(input);
                    }
                }
            }

            return result.Order().ToArray();
        }

        // 把 leave 携带的 finally 执行顺序展开为不串线的前驱图。
        private static ValueFlowGraph BuildValueFlowGraph(MethodBehavior method)
        {
            IReadOnlyDictionary<int, BehaviorFlowBlock> blocks = method.Blocks.ToDictionary(
                block => block.Id);
            Dictionary<FlowNode, HashSet<FlowNode>> predecessors = new();
            List<int[]> continuations = new() { Array.Empty<int>() };
            Dictionary<string, int> continuationIds = new(StringComparer.Ordinal) { [string.Empty] = 0 };
            HashSet<FlowNode> nodes = new() { new FlowNode(-1, 0) };
            Queue<FlowNode> pending = new(nodes);

            // 记录一条已经展开的前驱边。
            void AddEdge(FlowNode from, FlowNode to)
            {
                if (!predecessors.TryGetValue(to, out HashSet<FlowNode>? incoming))
                {
                    incoming = new HashSet<FlowNode>();
                    predecessors.Add(to, incoming);
                }

                incoming.Add(from);
                if (nodes.Add(to))
                {
                    pending.Enqueue(to);
                }
            }

            // 复用内容相同的 finally 后续队列编号。
            int GetContinuationId(IReadOnlyList<int> targets)
            {
                string key = string.Join(",", targets);
                if (!continuationIds.TryGetValue(key, out int id))
                {
                    id = continuations.Count;
                    continuations.Add(targets.ToArray());
                    continuationIds.Add(key, id);
                }
                return id;
            }

            while (pending.TryDequeue(out FlowNode current))
            {
                BehaviorFlowBlock block = blocks[current.BlockId];
                int[] continuation = continuations[current.ContinuationId];
                foreach (BehaviorFlowEdge edge in block.Successors)
                {
                    FlowNode? next = null;
                    if (edge.FinallyBlockIds.Count != 0)
                    {
                        int targetBlockId = edge.TargetBlockId
                            ?? throw new AnalysisException(
                                $"finally 执行边缺少后继：{method.MethodId} @ {block.Id}");
                        int[] nextContinuation = edge.FinallyBlockIds.Skip(1)
                            .Append(targetBlockId)
                            .Concat(continuation)
                            .ToArray();
                        next = new FlowNode(
                            edge.FinallyBlockIds[0],
                            GetContinuationId(nextContinuation));
                    }
                    else if (edge.TargetBlockId.HasValue)
                    {
                        next = new FlowNode(edge.TargetBlockId.Value, current.ContinuationId);
                    }
                    else if (edge.Semantics == BehaviorFlowBranchSemantics.StructuredExceptionHandling
                        && continuation.Length != 0)
                    {
                        next = new FlowNode(
                            continuation[0],
                            GetContinuationId(continuation.Skip(1).ToArray()));
                    }

                    if (next.HasValue)
                    {
                        AddEdge(current, next.Value);
                    }
                }
            }

            IReadOnlyDictionary<FlowNode, IReadOnlyList<FlowNode>> orderedPredecessors = predecessors
                .ToDictionary(
                    item => item.Key,
                    item => (IReadOnlyList<FlowNode>)item.Value.OrderBy(node => node.BlockId)
                        .ThenBy(node => node.ContinuationId)
                        .ToArray());
            IReadOnlyDictionary<int, FlowNode[]> nodesByBlockId = nodes.GroupBy(node => node.BlockId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(node => node.ContinuationId).ToArray());

            return new ValueFlowGraph(blocks, orderedPredecessors, nodesByBlockId,
                method.Assignments.ToLookup(item => item.Point.BlockId), method.Writes.ToLookup(item => item.Point.BlockId),
                method.Calls.ToLookup(item => item.Point.BlockId), method.Values.Where(value => value.Kind is BehaviorValueKind.FieldRead or BehaviorValueKind.Address
                    && value.Member != null && value.InputValueIds.Count == 0).ToLookup(value => value.Point!.Value.BlockId));
        }

        // 同一函数的 finally 展开图供值倒查与分配次数检查共同复用。
        private ValueFlowGraph ReadValueFlowGraph(MethodBehavior method)
        {
            if (!this.m_flowGraphs.TryGetValue(method.MethodId, out ValueFlowGraph? graph))
            {
                graph = BuildValueFlowGraph(method);
                this.m_flowGraphs.Add(method.MethodId, graph);
            }
            return graph;
        }

        private readonly record struct FlowNode(int BlockId, int ContinuationId);

        private readonly record struct FlowSearchPoint(FlowNode Node, int Order);

        private sealed record ValueFlowGraph(
            IReadOnlyDictionary<int, BehaviorFlowBlock> Blocks,
            IReadOnlyDictionary<FlowNode, IReadOnlyList<FlowNode>> Predecessors,
            IReadOnlyDictionary<int, FlowNode[]> NodesByBlockId,
            ILookup<int, BehaviorAssignment> Assignments,
            ILookup<int, BehaviorWrite> Writes,
            ILookup<int, BehaviorCall> Calls,
            ILookup<int, BehaviorValue> StaticReads)
        {
            private ILookup<FlowNode, FlowNode>? m_successors;

            /// <summary>按需建立不随调用环境变化的正向边索引，保留 finally 的独立续接身份。</summary>
            public ILookup<FlowNode, FlowNode> Successors => this.m_successors ??= this.Predecessors
                .SelectMany(item => item.Value.Select(from => (From: from, To: item.Key))).ToLookup(edge => edge.From, edge => edge.To);
        }
    }

    /// <summary>保存真实调用函数，以及映射到该函数的接收对象和逐位置实参。</summary>
    public sealed record ResolvedCallTarget(
        string MethodId,
        BehaviorMethodReference Reference,
        IReadOnlyList<BehaviorValueReference> Receiver,
        IReadOnlyList<IReadOnlyList<BehaviorValueReference>> Arguments,
        IReadOnlyList<string> DeclaringTypeArguments)
    {
        /// <summary>被调函数在本组具体类型实参下的分析环境。</summary>
        public int InstanceId { get; init; }
    }

    /// <summary>保存一个调用点和它所有合法目标的绑定。</summary>
    public sealed record ResolvedCall(
        string CallerMethodId,
        BehaviorCall Call,
        IReadOnlyList<ResolvedCallTarget> Targets)
    {
        /// <summary>本调用所属函数的具体类型实参环境。</summary>
        public int CallerInstanceId { get; init; }

        /// <summary>目标覆盖声明接收类型的全部合法对象，而非某次具体对象的较小范围。</summary>
        internal bool CoversDeclaredReceivers { get; init; }

        /// <summary>反射值或字段操作已按语义处理，可无普通目标继续；空接收对象调用不具备此事实。</summary>
        internal bool CompletesWithoutTarget { get; init; }
    }

    /// <summary>暂未展开的真实调用，后续告警分析仍须证明它是否经过缺少原因的豁免函数。</summary>
    public sealed record PendingCall(string CallerMethodId, int CallerInstanceId, BehaviorCall Call)
    {
        /// <summary>保留实际尝试解析时的失败证据，不把未查清调用解释为空目标。</summary>
        public string? Failure { get; init; }
    }

    /// <summary>保存闭合调用涉及的函数、行为、调用绑定和本模块耗时。</summary>
    public sealed record CallTargetResolutionResult(
        IReadOnlyList<MethodEntry> Methods,
        BehaviorReadResult Behaviors,
        IReadOnlyList<ResolvedCall> Calls,
        ValueSourceIndex ValueSources,
        TimeSpan Elapsed)
    {
        /// <summary>已有 Setter 证明也不代表这些调用不存在或已经闭合。</summary>
        public IReadOnlyList<PendingCall> PendingCalls { get; init; } = Array.Empty<PendingCall>();
    }
}
