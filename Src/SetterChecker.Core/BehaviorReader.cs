using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;
using OpCode = Mono.Cecil.Cil.OpCode;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace SetterChecker.Core
{
    /// <summary>
    /// 把源码函数和真实托管函数体读取为统一的行为事实。
    /// </summary>
    public sealed class BehaviorReader
    {
        // 并行读取指定函数，并按函数身份固定结果顺序。
        /// <summary>
        /// 读取一批已经进入函数总表的源码或托管函数。
        /// </summary>
        public async Task<BehaviorReadResult> ReadAsync(
            MaterialSet material,
            MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> methods,
            int jobs,
            CancellationToken cancellationToken = default)
        {
            BehaviorReadResult result = await ReadAvailableAsync(material, catalog, methods, jobs, cancellationToken).ConfigureAwait(false);
            string? failure = result.Methods.Select(method => method.Failure).FirstOrDefault(message => message != null);
            return failure == null ? result : throw new AnalysisException(failure);
        }

        // 逐方法保存读取失败，不丢弃同批成功事实；调用方必须继续保留失败状态。
        internal async Task<BehaviorReadResult> ReadAvailableAsync(MaterialSet material, MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> methods, int jobs, CancellationToken cancellationToken)
        {
            if (jobs <= 0)
            {
                throw new AnalysisException("工作数量必须是正整数。");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyDictionary<string, byte[]> sourceImages = material.SourceAssemblies.ToDictionary(
                assembly => Path.GetFullPath(assembly.AssemblyPath),
                assembly => assembly.AssemblyImage,
                StringComparer.OrdinalIgnoreCase);
            long partitionCount = (long)jobs * 4;
            BehaviorWorkItem[] workItems = methods
                .GroupBy(
                    method => method.AssemblyPath
                        ?? throw new AnalysisException($"函数缺少实际程序集：{method.Id}"),
                    StringComparer.OrdinalIgnoreCase)
                .SelectMany(group =>
                {
                    MethodEntry[] assemblyMethods = group.ToArray();
                    int batchSize = (int)Math.Max(
                        1,
                        (assemblyMethods.Length + partitionCount - 1) / partitionCount);

                    return assemblyMethods.Chunk(batchSize).Select(batch => new BehaviorWorkItem(
                        group.Key,
                        batch));
                })
                .ToArray();
            ConcurrentBag<IReadOnlyList<MethodBehavior>> parts = new();

            await Parallel.ForEachAsync(
                workItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (workItem, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    parts.Add(ReadManagedBehaviors(
                        workItem.AssemblyPath,
                        sourceImages,
                        catalog,
                        workItem.Methods));

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            MethodBehavior[] ordered = parts
                .SelectMany(part => part)
                .OrderBy(behavior => behavior.MethodId, StringComparer.Ordinal)
                .ToArray();

            stopwatch.Stop();

            return new BehaviorReadResult(ordered, stopwatch.Elapsed);
        }

        // 一次打开内存编译结果或真实托管文件并读取一批函数。
        private static IReadOnlyList<MethodBehavior> ReadManagedBehaviors(
            string assemblyPath,
            IReadOnlyDictionary<string, byte[]> sourceImages,
            MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> methods)
        {
            string fullPath = Path.GetFullPath(assemblyPath);
            using Cecil.ModuleDefinition module = sourceImages.TryGetValue(fullPath, out byte[]? image)
                ? MethodCatalog.OpenModule(image)
                : MethodCatalog.OpenModule(fullPath);

            if (methods.Any(method => !string.Equals(
                    method.AssemblyName,
                    module.Assembly.Name.Name,
                    StringComparison.Ordinal)))
            {
                throw new AnalysisException($"托管函数与真实程序集不一致：{assemblyPath}");
            }

            return methods.Select(method =>
            {
                try
                {
                    return ReadManagedBehavior(module, catalog, method);
                }
                catch (AnalysisException exception)
                {
                    return MethodBehavior.Empty(method.Id, MethodBodyKind.ReadFailure) with { Failure = exception.Message };
                }
            }).ToArray();
        }

        // 按一个确定的函数定义标记读取函数体或明确的无托管体边界。
        internal static MethodBehavior ReadManagedBehavior(
            Cecil.ModuleDefinition module,
            MethodCatalogResult catalog,
            MethodEntry method)
        {
            if (module.LookupToken(method.MetadataToken) is not Cecil.MethodDefinition definition)
            {
                throw new AnalysisException($"托管函数标记不是函数定义：{method.Id}");
            }

            if (definition.IsAbstract)
            {
                return MethodBehavior.Empty(method.Id, MethodBodyKind.Declaration);
            }

            if (definition.IsPInvokeImpl)
            {
                Cecil.PInvokeInfo import = definition.PInvokeInfo
                    ?? throw new AnalysisException($"平台调用缺少入口信息：{method.Id}");

                return MethodBehavior.Empty(
                    method.Id,
                    MethodBodyKind.PlatformInvocation,
                    new NativeBoundary(
                        import.Module.Name,
                        import.EntryPoint));
            }

            if (definition.IsInternalCall || definition.IsRuntime)
            {
                return MethodBehavior.Empty(method.Id, MethodBodyKind.RuntimeImplementation);
            }

            if (!definition.HasBody)
            {
                throw new AnalysisException($"托管函数没有函数体也没有原生标记：{method.Id}");
            }

            return new ManagedBehaviorBuilder(catalog, method, module.TypeSystem).Read(definition.Body);
        }

        /// <summary>
        /// 保存一次程序集内容读取所处理的函数批次。
        /// </summary>
        private sealed record BehaviorWorkItem(
            string AssemblyPath,
            IReadOnlyList<MethodEntry> Methods);

        /// <summary>
        /// 读取托管指令栈并建立本函数的事实集合。
        /// </summary>
        private sealed class ManagedBehaviorBuilder
        {
            private readonly MethodCatalogResult m_catalog;
            private readonly MethodEntry m_method;
            private readonly Cecil.TypeSystem m_typeSystem;
            private readonly List<BehaviorValue> m_values = new();
            private readonly Dictionary<int, BehaviorAssignment> m_assignments = new();
            private readonly Dictionary<int, BehaviorWrite> m_writes = new();
            private readonly Dictionary<int, BehaviorCall> m_calls = new();
            private readonly Dictionary<int, BehaviorReturn> m_returns = new();
            private readonly Dictionary<int, int> m_argumentValueIds = new();
            private readonly Dictionary<int, int> m_localValueIds = new();
            private readonly Dictionary<int, int> m_instructionValueIds = new();
            private readonly Dictionary<(int Offset, int Index), int> m_mergeValueIds = new();
            private readonly Dictionary<int, int[]> m_incomingStacks = new();
            private readonly Dictionary<int, int[]> m_outgoingStacks = new();
            private readonly Dictionary<Cecil.TypeReference, BehaviorTypeReference> m_typeReferences = new();
            private readonly Stack<int> m_stack = new();
            private int m_currentOffset;
            private int m_currentOrder;
            private BehaviorTypeReference? m_currentOperandType;

            // 保存当前托管文件和函数信息。
            public ManagedBehaviorBuilder(
                MethodCatalogResult catalog,
                MethodEntry method,
                Cecil.TypeSystem typeSystem)
            {
                this.m_catalog = catalog;
                this.m_method = method;
                this.m_typeSystem = typeSystem;
            }

            // 沿所有实际控制流路径读取函数体并在路径汇合处合并值来源。
            public MethodBehavior Read(Cil.MethodBody body)
            {
                Mono.Cecil.Rocks.MethodBodyRocks.SimplifyMacros(body);
                Cil.Instruction[] instructions = body.Instructions.ToArray();
                IReadOnlyDictionary<int, Cil.Instruction> instructionsByOffset = instructions
                    .ToDictionary(instruction => instruction.Offset);
                Queue<int> pending = new();
                foreach (Cil.VariableDefinition variable in body.Variables)
                {
                    Cecil.TypeReference type = variable.VariableType;
                    while (type is Cecil.PinnedType or Cecil.IModifierType)
                    {
                        type = ((Cecil.TypeSpecification)type).ElementType;
                    }
                    int slot = GetLocalValueId(variable.Index);
                    this.m_values[slot] = this.m_values[slot] with { IsManagedReferenceSlot = type.IsByReference, Type = ReadTypeReference(type) };
                }

                if (instructions.Length == 0)
                {
                    return MethodBehavior.Empty(this.m_method.Id, MethodBodyKind.Executable);
                }

                AddIncomingStack(instructions[0].Offset, Array.Empty<int>(), pending);
                foreach (Cil.ExceptionHandler handler in body.ExceptionHandlers)
                {
                    int[] handlerStack = handler.HandlerType is Cil.ExceptionHandlerType.Catch
                        or Cil.ExceptionHandlerType.Filter
                        ? new[] { AddRootValue(BehaviorValueKind.Exception, "exception") }
                        : Array.Empty<int>();
                    if (handlerStack.Length != 0)
                    {
                        int exceptionId = handlerStack[0];
                        this.m_values[exceptionId] = this.m_values[exceptionId] with
                        {
                            Point = new BehaviorFlowPoint(handler.HandlerStart.Offset, 0),
                            Type = handler.CatchType == null ? null : ReadTypeReference(handler.CatchType),
                        };
                    }
                    AddIncomingStack(handler.HandlerStart.Offset, handlerStack, pending);
                    if (handler.HandlerType == Cil.ExceptionHandlerType.Filter)
                    {
                        AddIncomingStack(
                            handler.FilterStart?.Offset
                                ?? throw new AnalysisException(
                                    $"筛选异常处理器缺少入口：{this.m_method.Id}"),
                            handlerStack,
                            pending);
                    }
                }

                while (pending.TryDequeue(out int offset))
                {
                    Cil.Instruction instruction = instructionsByOffset[offset];
                    // 用本条指令的入口状态恢复求值栈。
                    this.m_stack.Clear();
                    foreach (int valueId in this.m_incomingStacks[offset])
                    {
                        this.m_stack.Push(valueId);
                    }
                    this.m_currentOffset = offset;
                    this.m_currentOrder = this.m_incomingStacks[offset].Length;
                    ReadInstruction(instruction);
                    int[] outgoingStack = this.m_stack.Reverse().ToArray();
                    this.m_outgoingStacks[offset] = outgoingStack;

                    foreach (int successor in ReadSuccessors(instruction, instructionsByOffset))
                    {
                        int[] successorStack = instruction.OpCode.Code is Cil.Code.Leave
                            ? Array.Empty<int>()
                            : outgoingStack;
                        AddIncomingStack(successor, successorStack, pending);
                    }
                }

                var flow = ReadManagedControlFlow(body, this.m_incomingStacks.Keys.ToHashSet());
                ILookup<int, int> incoming = flow.Blocks.Where(block => block.IsReachable)
                    .SelectMany(block => block.Successors.Where(edge => edge.TargetBlockId.HasValue)
                        .Select(edge => (Target: edge.TargetBlockId!.Value, From: block.Id)))
                    .ToLookup(edge => edge.Target, edge => edge.From);
                foreach (var merge in this.m_mergeValueIds)
                {
                    this.m_values[merge.Value] = this.m_values[merge.Value] with
                    {
                        IncomingValues = incoming[merge.Key.Offset].Where(this.m_outgoingStacks.ContainsKey)
                            .Select(from => new BehaviorMergeInput(from, this.m_outgoingStacks[from][merge.Key.Index])).ToArray(),
                    };
                }
                return new MethodBehavior(
                    this.m_method.Id,
                    MethodBodyKind.Executable,
                    this.m_values,
                    this.m_assignments.Values.OrderBy(item => item.Point.BlockId).ToArray(),
                    this.m_writes.Values.OrderBy(item => item.Point.BlockId).ToArray(),
                    this.m_calls.Values.OrderBy(item => item.Point.BlockId).ToArray(),
                    this.m_returns.Values.OrderBy(item => item.Point.BlockId).ToArray(),
                    flow.Blocks,
                    flow.Handlers);
            }

            // 按托管指令建立控制流块，并保留原始异常处理表。
            private (IReadOnlyList<BehaviorFlowBlock> Blocks,
                IReadOnlyList<BehaviorExceptionHandler> Handlers) ReadManagedControlFlow(
                Cil.MethodBody body,
                IReadOnlySet<int> reachableOffsets)
            {
                BehaviorExceptionHandler[] handlers = body.ExceptionHandlers.Select(handler =>
                    new BehaviorExceptionHandler(
                        handler.HandlerType is Cil.ExceptionHandlerType.Catch or Cil.ExceptionHandlerType.Filter
                            or Cil.ExceptionHandlerType.Finally or Cil.ExceptionHandlerType.Fault ? handler.HandlerType
                            : throw new AnalysisException($"不支持的 Cecil 异常处理类型：{handler.HandlerType}"),
                        handler.TryStart.Offset,
                        handler.TryEnd?.Offset ?? body.CodeSize,
                        handler.HandlerStart.Offset,
                        handler.HandlerEnd?.Offset ?? body.CodeSize,
                        handler.FilterStart?.Offset,
                        handler.CatchType == null ? null : ReadTypeReference(handler.CatchType))).ToArray();
                BehaviorFlowBlock[] instructionBlocks = body.Instructions.Select(instruction =>
                    new BehaviorFlowBlock(instruction.Offset,
                        reachableOffsets.Contains(instruction.Offset),
                        ReadManagedFlowEdges(instruction, body.CodeSize, handlers),
                        instruction.OpCode.FlowControl == Cil.FlowControl.Cond_Branch && instruction.Operand is Cil.Instruction or Cil.Instruction[]
                            && this.m_instructionValueIds.TryGetValue(instruction.Offset, out int condition) ? condition : null,
                        instruction.Operand is Cil.Instruction target ? target.Offset
                            : instruction.OpCode == OpCodes.Switch ? instruction.Next?.Offset : null,
                        instruction.Operand is Cil.Instruction[] targets ? targets.Select(target => target.Offset).ToArray() : null)).ToArray();
                int entryTarget = body.Instructions.Count == 0 ? body.CodeSize : body.Instructions[0].Offset;
                BehaviorFlowBlock entry = new(-1, true,
                    new[] { new BehaviorFlowEdge(entryTarget, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.Regular, Array.Empty<int>()) });
                bool exitReachable = instructionBlocks.Length == 0 || instructionBlocks.Any(block =>
                    block.IsReachable && block.Successors.Any(edge => edge.TargetBlockId == body.CodeSize));
                BehaviorFlowBlock exit = new(body.CodeSize, exitReachable, Array.Empty<BehaviorFlowEdge>());

                return (instructionBlocks.Prepend(entry).Append(exit).ToArray(), handlers);
            }

            // 直接保存指令跳转和离开 try 时必须执行的 finally 入口，不另建区域树。
            private static IReadOnlyList<BehaviorFlowEdge> ReadManagedFlowEdges(
                Cil.Instruction instruction,
                int exitBlockId,
                IReadOnlyList<BehaviorExceptionHandler> handlers)
            {
                // 按真实半开区间计算离开路径，嵌套 finally 从内到外执行。
                BehaviorFlowEdge Edge(int? target, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics semantics,
                    bool runsFinally = false)
                {
                    int[] finalizers = runsFinally && target.HasValue
                        ? handlers.Where(handler => handler.Kind == Mono.Cecil.Cil.ExceptionHandlerType.Finally
                            && instruction.Offset >= handler.TryStartBlockId && instruction.Offset < handler.TryEndBlockId
                            && (target.Value < handler.TryStartBlockId || target.Value >= handler.TryEndBlockId))
                            .OrderBy(handler => handler.TryEndBlockId - handler.TryStartBlockId)
                            .ThenByDescending(handler => handler.TryStartBlockId)
                            .Select(handler => handler.HandlerStartBlockId).ToArray()
                        : Array.Empty<int>();
                    return new BehaviorFlowEdge(target, semantics, finalizers);
                }

                if (instruction.OpCode.Code == Cil.Code.Ret)
                {
                    return new[] { Edge(exitBlockId, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.Return) };
                }
                if (instruction.OpCode.Code is Cil.Code.Throw or Cil.Code.Rethrow)
                {
                    return new[] { Edge(null, instruction.OpCode.Code == Cil.Code.Throw
                        ? Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.Throw : Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.Rethrow) };
                }
                if (instruction.OpCode.Code == Cil.Code.Endfinally)
                {
                    return new[] { Edge(null, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.StructuredExceptionHandling) };
                }
                if (instruction.OpCode.Code == Cil.Code.Endfilter)
                {
                    BehaviorExceptionHandler handler = handlers.Single(item => item.Kind == Mono.Cecil.Cil.ExceptionHandlerType.Filter
                        && instruction.Offset >= item.FilterStartBlockId && instruction.Offset < item.HandlerStartBlockId);
                    return new[]
                    {
                        Edge(handler.HandlerStartBlockId, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.Regular),
                        Edge(null, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.StructuredExceptionHandling),
                    };
                }

                IEnumerable<int> targets = instruction.OpCode.FlowControl switch
                {
                    Cil.FlowControl.Branch => ReadBranchTargets(instruction),
                    Cil.FlowControl.Cond_Branch => ReadBranchTargets(instruction).Concat(instruction.Next == null
                        ? Array.Empty<int>() : new[] { instruction.Next.Offset }),
                    _ => instruction.Next == null ? Array.Empty<int>() : new[] { instruction.Next.Offset },
                };
                return targets.Distinct().Order().Select(target => Edge(target,
                    Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics.Regular, instruction.OpCode.Code == Cil.Code.Leave)).ToArray();
            }

            // 返回当前指令完成后可能继续执行的全部下一条指令。
            private static IReadOnlyList<int> ReadSuccessors(
                Cil.Instruction instruction,
                IReadOnlyDictionary<int, Cil.Instruction> instructions)
            {
                // 异常入口由编译器给定的独立栈初始化；普通路径共用已保存的跳转事实。
                return instruction.OpCode.FlowControl is Cil.FlowControl.Return or Cil.FlowControl.Throw
                    ? Array.Empty<int>()
                    : ReadManagedFlowEdges(instruction, int.MaxValue, Array.Empty<BehaviorExceptionHandler>())
                        .Where(edge => edge.TargetBlockId.HasValue && instructions.ContainsKey(edge.TargetBlockId.Value))
                        .Select(edge => edge.TargetBlockId!.Value).ToArray();
            }

            // 读取 Cecil 已经解析完成的单目标或多目标跳转位置。
            private static IReadOnlyList<int> ReadBranchTargets(Cil.Instruction instruction)
            {
                return instruction.Operand switch
                {
                    Cil.Instruction target => new[] { target.Offset },
                    Cil.Instruction[] targets => targets.Select(target => target.Offset).ToArray(),
                    _ => throw new AnalysisException(
                        $"跳转指令缺少 Cecil 目标：{instruction.Offset} {instruction.OpCode.Name}"),
                };
            }

            // 合并一条新执行路径到目标指令的入口栈并在状态改变时继续处理。
            private void AddIncomingStack(int offset, int[] candidate, Queue<int> pending)
            {
                if (!this.m_incomingStacks.TryGetValue(offset, out int[]? current))
                {
                    this.m_incomingStacks.Add(offset, candidate.ToArray());
                    pending.Enqueue(offset);

                    return;
                }

                if (current.Length != candidate.Length)
                {
                    throw new AnalysisException(
                        $"托管函数在控制流汇合处的指令栈深度不一致：{this.m_method.Id} @ {offset}");
                }

                bool changed = false;
                int[] merged = new int[current.Length];
                for (int index = 0; index < current.Length; index++)
                {
                    merged[index] = current[index] == candidate[index]
                        ? current[index]
                        : GetMergeValueId(offset, index, current[index], candidate[index]);
                    changed |= merged[index] != current[index];
                }

                if (changed)
                {
                    this.m_incomingStacks[offset] = merged;
                    pending.Enqueue(offset);
                }
            }

            // 建立或扩展一个固定控制流位置的值来源汇合节点。
            private int GetMergeValueId(int offset, int index, int firstValueId, int secondValueId)
            {
                if (!this.m_mergeValueIds.TryGetValue((offset, index), out int mergeValueId))
                {
                    mergeValueId = this.m_values.Count;
                    this.m_values.Add(new BehaviorValue(
                        mergeValueId,
                        BehaviorValueKind.Merge,
                        null,
                        null,
                        Array.Empty<int>())
                    {
                        Point = new BehaviorFlowPoint(offset, index),
                    });
                    this.m_mergeValueIds.Add((offset, index), mergeValueId);
                }

                BehaviorValue merge = this.m_values[mergeValueId];
                int[] inputs = merge.InputValueIds
                    .Append(firstValueId)
                    .Append(secondValueId)
                    .Where(valueId => valueId != mergeValueId)
                    .Distinct()
                    .Order()
                    .ToArray();
                this.m_values[mergeValueId] = merge with { InputValueIds = inputs };

                return mergeValueId;
            }

            // 读取一条会影响值栈或写入事实的基础指令。
            private void ReadInstruction(Cil.Instruction instruction)
            {
                OpCode code = instruction.OpCode;
                object? operand = instruction.Operand;
                int offset = instruction.Offset;
                this.m_currentOperandType = operand is Cecil.TypeReference operandType ? ReadTypeReference(operandType) : null;

                if (code == OpCodes.Ret)
                {
                    this.m_returns[offset] = new BehaviorReturn(
                        this.m_stack.Count == 0 ? null : this.m_stack.Pop(),
                        NextPoint());

                    return;
                }

                if (code.Code is Cil.Code.Ldarg or Cil.Code.Ldarga or Cil.Code.Starg
                    or Cil.Code.Ldloc or Cil.Code.Ldloca or Cil.Code.Stloc)
                {
                    bool argument = code.Code is Cil.Code.Ldarg or Cil.Code.Ldarga or Cil.Code.Starg;
                    int index = argument ? RequireOperand<Cecil.ParameterDefinition>(instruction).Index + (this.m_method.IsStatic ? 0 : 1)
                        : RequireOperand<Cil.VariableDefinition>(instruction).Index;
                    int slotId = argument ? GetArgumentValueId(index) : GetLocalValueId(index);
                    if (code.Code is Cil.Code.Starg or Cil.Code.Stloc)
                    {
                        this.m_assignments[offset] = new BehaviorAssignment(slotId, Pop(code, offset), NextPoint());
                    }
                    else if (code.Code is Cil.Code.Ldarga or Cil.Code.Ldloca)
                    {
                        if (this.m_values[slotId].IsManagedReferenceSlot)
                        {
                            throw new AnalysisException($"托管引用不能再次取托管地址：{this.m_method.Id} @ {offset}");
                        }
                        this.m_stack.Push(AddValue(BehaviorValueKind.Address,
                            $"{(argument ? "argument" : "local")}:{index}", new[] { slotId }));
                    }
                    else
                    {
                        // 每次加载独立保留位置，不让后续赋值改变已入栈的值。
                        this.m_stack.Push(AddValue(BehaviorValueKind.SlotRead, this.m_values[slotId].Reference, new[] { slotId }));
                    }
                    return;
                }

                System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
                (Cecil.TypeReference? Type, string? Text)? constant = code.Code switch
                {
                    Cil.Code.Ldc_I4 => (this.m_typeSystem.Int32, RequireOperand<int>(instruction).ToString(culture)),
                    Cil.Code.Ldc_I8 => (this.m_typeSystem.Int64, RequireOperand<long>(instruction).ToString(culture)),
                    Cil.Code.Ldc_R4 => (this.m_typeSystem.Single, RequireOperand<float>(instruction).ToString("R", culture)),
                    Cil.Code.Ldc_R8 => (this.m_typeSystem.Double, RequireOperand<double>(instruction).ToString("R", culture)),
                    Cil.Code.Ldstr => (this.m_typeSystem.String, RequireOperand<string>(instruction)),
                    Cil.Code.Ldnull => (null, null),
                    _ => null,
                };
                if (constant.HasValue)
                {
                    int valueId = AddValue(BehaviorValueKind.Constant, constant.Value.Text, Array.Empty<int>(),
                        type: constant.Value.Type == null ? null : ReadTypeReference(constant.Value.Type));
                    this.m_stack.Push(valueId);
                    return;
                }

                if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                {
                    Cecil.MethodReference target = RequireOperand<Cecil.MethodReference>(instruction);
                    if (target.DeclaringType is Cecil.ArrayType array)
                    {
                        ReadArrayCall(code, target, array, offset);
                    }
                    else
                    {
                        ReadCall(instruction, target);
                    }

                    return;
                }

                if (code == OpCodes.Newarr)
                {
                    BehaviorTypeReference type = ReadTypeReference(
                        new Cecil.ArrayType(RequireOperand<Cecil.TypeReference>(instruction)));
                    int lengthValueId = Pop(code, offset);
                    this.m_stack.Push(AddValue(BehaviorValueKind.NewArray, null, new[] { lengthValueId }, type: type));

                    return;
                }

                if (code == OpCodes.Ldtoken)
                {
                    if (operand is Cecil.TypeReference type)
                    {
                        this.m_stack.Push(AddValue(BehaviorValueKind.Type, null, Array.Empty<int>(), type: ReadTypeReference(type)));

                        return;
                    }

                    if (operand is Cecil.MethodReference methodReference)
                    {
                        this.m_stack.Push(AddValue(BehaviorValueKind.Function, null, Array.Empty<int>(),
                            method: this.m_catalog.ReadManagedMethodReference(methodReference, this.m_method.AssemblyPath!)));

                        return;
                    }

                    if (operand is Cecil.FieldReference fieldReference)
                    {
                        this.m_stack.Push(AddValue(BehaviorValueKind.Computation, null, Array.Empty<int>(),
                            member: this.m_catalog.ReadManagedFieldReference(fieldReference, this.m_method.AssemblyPath!)));

                        return;
                    }

                    throw new AnalysisException(
                        $"托管 ldtoken 引用种类无法识别：{this.m_method.Id} @ {offset}");
                }

                if (code.Code is Cil.Code.Castclass or Cil.Code.Isinst
                    or Cil.Code.Box or Cil.Code.Unbox or Cil.Code.Unbox_Any
                    or Cil.Code.Ldobj)
                {
                    int valueId = AddValue(BehaviorValueKind.Conversion, code.Name, new[] { Pop(code, offset) },
                        type: ReadTypeReference(RequireOperand<Cecil.TypeReference>(instruction)));
                    this.m_stack.Push(valueId);

                    return;
                }

                if (code.Code is Cil.Code.Ldfld or Cil.Code.Ldflda or Cil.Code.Ldsfld or Cil.Code.Ldsflda)
                {
                    this.m_stack.Push(AddValue(
                        code.Code is Cil.Code.Ldflda or Cil.Code.Ldsflda ? BehaviorValueKind.Address : BehaviorValueKind.FieldRead,
                        null, code.Code is Cil.Code.Ldsfld or Cil.Code.Ldsflda ? Array.Empty<int>() : new[] { Pop(code, offset) },
                        member: this.m_catalog.ReadManagedFieldReference(RequireOperand<Cecil.FieldReference>(instruction), this.m_method.AssemblyPath!)));

                    return;
                }

                if (code == OpCodes.Stfld || code == OpCodes.Stsfld)
                {
                    int valueId = Pop(code, offset);
                    int? receiverId = code == OpCodes.Stfld ? Pop(code, offset) : null;
                    RecordWrite(BehaviorWriteKind.Field, receiverId,
                        this.m_catalog.ReadManagedFieldReference(RequireOperand<Cecil.FieldReference>(instruction), this.m_method.AssemblyPath!), Array.Empty<int>(), valueId, offset);

                    return;
                }

                if (code == OpCodes.Ldelema)
                {
                    int indexValueId = Pop(code, offset);
                    int arrayValueId = Pop(code, offset);
                    this.m_stack.Push(AddValue(BehaviorValueKind.Address, null, new[] { arrayValueId, indexValueId },
                        type: ReadTypeReference(RequireOperand<Cecil.TypeReference>(instruction))));

                    return;
                }

                // 判断一条指令是否从一维数组元素读取值。
                if (code.Code is Cil.Code.Ldelem_Any or Cil.Code.Ldelem_I
                    or Cil.Code.Ldelem_I1 or Cil.Code.Ldelem_I2 or Cil.Code.Ldelem_I4
                    or Cil.Code.Ldelem_I8 or Cil.Code.Ldelem_U1 or Cil.Code.Ldelem_U2
                    or Cil.Code.Ldelem_U4 or Cil.Code.Ldelem_R4 or Cil.Code.Ldelem_R8
                    or Cil.Code.Ldelem_Ref)
                {
                    int indexValueId = Pop(code, offset);
                    int arrayValueId = Pop(code, offset);
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.ArrayElementRead,
                        code.Name,
                        new[] { arrayValueId, indexValueId },
                        type: operand is Cecil.TypeReference elementType ? ReadTypeReference(elementType) : null));

                    return;
                }

                // 判断一条指令是否把值写入一维数组元素。
                if (code.Code is Cil.Code.Stelem_Any
                    or Cil.Code.Stelem_I or Cil.Code.Stelem_I1 or Cil.Code.Stelem_I2
                    or Cil.Code.Stelem_I4 or Cil.Code.Stelem_I8 or Cil.Code.Stelem_R4
                    or Cil.Code.Stelem_R8 or Cil.Code.Stelem_Ref)
                {
                    int valueId = Pop(code, offset);
                    int indexValueId = Pop(code, offset);
                    int arrayValueId = Pop(code, offset);

                    RecordWrite(BehaviorWriteKind.ArrayElement, arrayValueId, null,
                        new[] { indexValueId }, valueId, offset);

                    return;
                }

                // 判断一条指令是否通过托管地址写入值。
                if (code.Code is Cil.Code.Stind_I or Cil.Code.Stind_I1
                    or Cil.Code.Stind_I2 or Cil.Code.Stind_I4 or Cil.Code.Stind_I8
                    or Cil.Code.Stind_R4 or Cil.Code.Stind_R8 or Cil.Code.Stind_Ref
                    or Cil.Code.Stobj or Cil.Code.Cpobj)
                {
                    int valueId = Pop(code, offset);
                    int addressValueId = Pop(code, offset);

                    RecordWrite(BehaviorWriteKind.Indirect, addressValueId, null,
                        Array.Empty<int>(), valueId, offset);

                    return;
                }

                if (code == OpCodes.Initobj)
                {
                    int addressValueId = Pop(code, offset);
                    int defaultValueId = AddValue(
                        BehaviorValueKind.Constant,
                        $"default:{RequireOperand<Cecil.TypeReference>(instruction).FullName}",
                        Array.Empty<int>());
                    RecordWrite(BehaviorWriteKind.Indirect, addressValueId, null,
                        Array.Empty<int>(), defaultValueId, offset);

                    return;
                }

                if (code == OpCodes.Cpblk || code == OpCodes.Initblk)
                {
                    int lengthValueId = Pop(code, offset);
                    int valueId = Pop(code, offset);
                    int targetAddressId = Pop(code, offset);
                    RecordWrite(BehaviorWriteKind.Indirect, targetAddressId, null,
                        new[] { lengthValueId }, valueId, offset);

                    return;
                }

                if (code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
                {
                    Cecil.MethodReference methodReference =
                        RequireOperand<Cecil.MethodReference>(instruction);
                    BehaviorMethodReference reference =
                        this.m_catalog.ReadManagedMethodReference(
                            methodReference,
                            this.m_method.AssemblyPath!);
                    IReadOnlyList<int> inputs = code == OpCodes.Ldvirtftn
                        ? new[] { Pop(code, offset) }
                        : Array.Empty<int>();
                    this.m_stack.Push(AddValue(BehaviorValueKind.Function, null, inputs, method: reference));

                    return;
                }

                if (code == OpCodes.Dup)
                {
                    int valueId = Pop(code, offset);
                    this.m_stack.Push(valueId);
                    this.m_stack.Push(valueId);

                    return;
                }

                if (TryReadStackOperation(code, offset))
                {
                    return;
                }

                throw new AnalysisException(
                    $"托管函数包含尚未读取的操作码：{this.m_method.Id} @ {offset} {code.Name}");
            }

            // 用统一的执行位置登记字段、数组和地址写入。
            private void RecordWrite(BehaviorWriteKind kind, int? receiver, BehaviorMemberReference? member,
                IReadOnlyList<int> indices, int value, int offset)
            {
                this.m_writes[offset] = new BehaviorWrite(kind, receiver, member, indices, value, NextPoint())
                {
                    OperandType = this.m_currentOperandType,
                };
            }

            // 从 Cecil 指令取得一个类型完全确定的操作数。
            private static T RequireOperand<T>(Cil.Instruction instruction)
            {
                return instruction.Operand is T value
                    ? value
                    : throw new AnalysisException(
                        $"托管操作数类型错误：@ {instruction.Offset} {instruction.OpCode.Name}，需要 {typeof(T).Name}");
            }

            // 只读取已明确支持的纯运算、转换、间接读取和控制指令。
            private bool TryReadStackOperation(OpCode code, int offset)
            {
                (int Pop, int Push)? shape = code.Code switch
                {
                    Cil.Code.Add or Cil.Code.Add_Ovf or Cil.Code.Add_Ovf_Un
                        or Cil.Code.Sub or Cil.Code.Sub_Ovf or Cil.Code.Sub_Ovf_Un
                        or Cil.Code.Mul or Cil.Code.Mul_Ovf or Cil.Code.Mul_Ovf_Un
                        or Cil.Code.Div or Cil.Code.Div_Un or Cil.Code.Rem or Cil.Code.Rem_Un
                        or Cil.Code.And or Cil.Code.Or or Cil.Code.Xor
                        or Cil.Code.Shl or Cil.Code.Shr or Cil.Code.Shr_Un
                        or Cil.Code.Ceq or Cil.Code.Cgt or Cil.Code.Cgt_Un
                        or Cil.Code.Clt or Cil.Code.Clt_Un => (2, 1),
                    Cil.Code.Neg or Cil.Code.Not or Cil.Code.Ckfinite or Cil.Code.Ldlen
                        or Cil.Code.Conv_I or Cil.Code.Conv_I1 or Cil.Code.Conv_I2
                        or Cil.Code.Conv_I4 or Cil.Code.Conv_I8 or Cil.Code.Conv_U
                        or Cil.Code.Conv_U1 or Cil.Code.Conv_U2 or Cil.Code.Conv_U4 or Cil.Code.Conv_U8
                        or Cil.Code.Conv_R4 or Cil.Code.Conv_R8 or Cil.Code.Conv_R_Un
                        or Cil.Code.Conv_Ovf_I or Cil.Code.Conv_Ovf_I_Un
                        or Cil.Code.Conv_Ovf_I1 or Cil.Code.Conv_Ovf_I1_Un
                        or Cil.Code.Conv_Ovf_I2 or Cil.Code.Conv_Ovf_I2_Un
                        or Cil.Code.Conv_Ovf_I4 or Cil.Code.Conv_Ovf_I4_Un
                        or Cil.Code.Conv_Ovf_I8 or Cil.Code.Conv_Ovf_I8_Un
                        or Cil.Code.Conv_Ovf_U or Cil.Code.Conv_Ovf_U_Un
                        or Cil.Code.Conv_Ovf_U1 or Cil.Code.Conv_Ovf_U1_Un
                        or Cil.Code.Conv_Ovf_U2 or Cil.Code.Conv_Ovf_U2_Un
                        or Cil.Code.Conv_Ovf_U4 or Cil.Code.Conv_Ovf_U4_Un
                        or Cil.Code.Conv_Ovf_U8 or Cil.Code.Conv_Ovf_U8_Un
                        or Cil.Code.Ldind_I or Cil.Code.Ldind_I1 or Cil.Code.Ldind_I2
                        or Cil.Code.Ldind_I4 or Cil.Code.Ldind_I8 or Cil.Code.Ldind_R4
                        or Cil.Code.Ldind_R8 or Cil.Code.Ldind_Ref or Cil.Code.Ldind_U1
                        or Cil.Code.Ldind_U2 or Cil.Code.Ldind_U4 => (1, 1),
                    Cil.Code.Beq or Cil.Code.Bne_Un or Cil.Code.Bge or Cil.Code.Bge_Un
                        or Cil.Code.Bgt or Cil.Code.Bgt_Un or Cil.Code.Ble or Cil.Code.Ble_Un
                        or Cil.Code.Blt or Cil.Code.Blt_Un => (2, 0),
                    Cil.Code.Brtrue or Cil.Code.Brfalse or Cil.Code.Pop
                        or Cil.Code.Switch or Cil.Code.Throw or Cil.Code.Endfilter => (1, 0),
                    Cil.Code.Rethrow or Cil.Code.Endfinally or Cil.Code.Volatile
                        or Cil.Code.Readonly or Cil.Code.Constrained or Cil.Code.Unaligned
                        or Cil.Code.Tail or Cil.Code.Nop or Cil.Code.Br or Cil.Code.Leave => (0, 0),
                    Cil.Code.Sizeof => (0, 1),
                    _ => null,
                };
                if (shape == null)
                {
                    return false;
                }

                int[] inputs = new int[shape.Value.Pop];
                for (int index = inputs.Length - 1; index >= 0; index--)
                {
                    inputs[index] = Pop(code, offset);
                }

                if (shape.Value.Push == 1 || code.FlowControl == Cil.FlowControl.Cond_Branch
                    || code.Code is Cil.Code.Throw or Cil.Code.Rethrow or Cil.Code.Endfilter)
                {
                    int valueId = AddValue(
                        BehaviorValueKind.Computation,
                        code.Name,
                        inputs);
                    if (shape.Value.Push == 1)
                    {
                        this.m_stack.Push(valueId);
                    }
                }

                return true;
            }

            // 读取普通调用、虚调用或构造函数调用的接收对象、参数和结果。
            private void ReadCall(
                Cil.Instruction instruction,
                Cecil.MethodReference method)
            {
                OpCode code = instruction.OpCode;
                int offset = instruction.Offset;
                BehaviorMethodReference reference =
                    this.m_catalog.ReadManagedMethodReference(
                        method,
                        this.m_method.AssemblyPath!);
                MethodEntry definition = this.m_catalog.ResolveMethodDefinition(
                    reference,
                    searchInherited: code != OpCodes.Newobj).Method;
                BehaviorArgument[] arguments = new BehaviorArgument[reference.Identity.Parameters.Count];

                for (int index = arguments.Length - 1; index >= 0; index--)
                {
                    arguments[index] = new BehaviorArgument(
                        Pop(code, offset),
                        definition.Parameters[index].RefKind);
                }

                int? receiverValueId = reference.HasInstance && code != OpCodes.Newobj
                    ? Pop(code, offset)
                    : null;
                BehaviorCallKind kind = code.Code switch
                {
                    Cil.Code.Newobj => BehaviorCallKind.ObjectCreation,
                    _ when definition.Name == "Invoke" && this.m_catalog.IsDelegateType(this.m_catalog.TypesById[definition.TypeId]) => BehaviorCallKind.Delegate,
                    Cil.Code.Callvirt when definition.IsVirtual => BehaviorCallKind.Virtual,
                    _ => BehaviorCallKind.Direct,
                };
                int? resultValueId = null;

                if (code == OpCodes.Newobj || reference.Identity.ReturnType.Text != "System.Void")
                {
                    resultValueId = AddValue(
                        code == OpCodes.Newobj
                            ? BehaviorValueKind.NewObject
                            : BehaviorValueKind.CallResult,
                        code == OpCodes.Newobj
                            ? reference.Identity.DeclaringType.Text
                            : reference.Identity.Text,
                        arguments.Select(argument => argument.ValueId).ToArray(),
                        type: code == OpCodes.Newobj ? ReadTypeReference(method.DeclaringType) : null);

                    this.m_stack.Push(resultValueId.Value);
                }

                BehaviorTypeReference? constrainedType = null;
                for (Cil.Instruction? prefix = instruction.Previous;
                     prefix?.OpCode.OpCodeType == Cil.OpCodeType.Prefix;
                     prefix = prefix.Previous)
                {
                    if (prefix.OpCode == OpCodes.Constrained)
                    {
                        constrainedType = ReadTypeReference(
                            RequireOperand<Cecil.TypeReference>(prefix));
                    }
                }

                this.m_calls[offset] = new BehaviorCall(
                    kind,
                    reference,
                    receiverValueId,
                    arguments,
                    resultValueId,
                    NextPoint())
                {
                    ConstrainedReceiverType = constrainedType,
                };
            }

            // 把 CLR 数组固有函数还原为数组创建、读取、写入和元素地址。
            private void ReadArrayCall(
                OpCode code,
                Cecil.MethodReference method,
                Cecil.ArrayType array,
                int offset)
            {
                int[] arguments = new int[method.Parameters.Count];
                for (int index = arguments.Length - 1; index >= 0; index--)
                {
                    arguments[index] = Pop(code, offset);
                }

                if (code == OpCodes.Newobj && method.Name == ".ctor")
                {
                    this.m_stack.Push(AddValue(BehaviorValueKind.NewArray, null, arguments, type: ReadTypeReference(array)));

                    return;
                }

                int receiverId = Pop(code, offset);
                if (method.Name == "Set")
                {
                    RecordWrite(BehaviorWriteKind.ArrayElement, receiverId, null,
                        arguments[..^1], arguments[^1], offset);
                }
                else if (method.Name is "Get" or "Address")
                {
                    this.m_stack.Push(AddValue(
                        method.Name == "Get"
                            ? BehaviorValueKind.ArrayElementRead
                            : BehaviorValueKind.Address,
                        null, new[] { receiverId }.Concat(arguments).ToArray(), type: ReadTypeReference(array.ElementType)));
                }
                else
                {
                    throw new AnalysisException(
                        $"数组固有函数无法识别：{this.m_method.Id} @ {offset} {method.FullName}");
                }
            }

            // 为当前对象或参数槽建立可复用的根值编号。
            private int GetArgumentValueId(int argumentIndex)
            {
                if (this.m_argumentValueIds.TryGetValue(argumentIndex, out int valueId))
                {
                    return valueId;
                }

                int parameterIndex = this.m_method.IsStatic ? argumentIndex : argumentIndex - 1;
                if (!this.m_method.IsStatic && argumentIndex == 0)
                {
                    valueId = AddRootValue(BehaviorValueKind.CurrentInstance, "this");
                    this.m_values[valueId] = this.m_values[valueId] with
                    {
                        IsManagedReferenceSlot = this.m_catalog.TypesById[this.m_method.TypeId].IsValueType,
                    };
                }
                else
                {
                    if ((uint)parameterIndex >= (uint)this.m_method.Parameters.Count)
                    {
                        throw new AnalysisException(
                            $"托管参数槽超出函数签名：{this.m_method.Id} #{argumentIndex}");
                    }

                    ParameterEntry parameter = this.m_method.Parameters[parameterIndex];
                    valueId = AddRootValue(BehaviorValueKind.Parameter, parameter.Name);
                    this.m_values[valueId] = this.m_values[valueId] with
                    {
                        ParameterIndex = parameterIndex,
                        IsManagedReferenceSlot = parameter.RefKind != RefKind.None,
                    };
                }

                this.m_argumentValueIds.Add(argumentIndex, valueId);
                return valueId;
            }

            // 为一个局部变量槽建立可复用的值来源编号。
            private int GetLocalValueId(int localIndex)
            {
                return this.m_localValueIds.TryGetValue(localIndex, out int valueId)
                    ? valueId
                    : this.m_localValueIds[localIndex] = AddRootValue(
                        BehaviorValueKind.Local,
                        $"local{localIndex}");
            }

            // 从函数指令栈读取一个已经建立的值来源。
            private int Pop(OpCode code, int offset)
            {
                return this.m_stack.Count > 0
                    ? this.m_stack.Pop()
                    : throw new AnalysisException(
                        $"托管函数指令栈不足：{this.m_method.Id} @ {offset} {code.Name}");
            }

            // 一次保存值和类型、函数或字段事实，同一指令重算时保留编号。
            private int AddValue(
                BehaviorValueKind kind,
                string? reference,
                IReadOnlyList<int> inputValueIds,
                BehaviorTypeReference? type = null, BehaviorMethodReference? method = null, BehaviorMemberReference? member = null)
            {
                int id = this.m_instructionValueIds.GetValueOrDefault(this.m_currentOffset, this.m_values.Count);
                BehaviorValue value = new(id, kind, reference ?? type?.Id ?? method?.Name ?? member?.Name, null, inputValueIds)
                {
                    OperandType = this.m_currentOperandType,
                    Type = type,
                    Method = method,
                    Member = member,
                    Point = NextPoint(),
                };
                if (id == this.m_values.Count)
                {
                    this.m_values.Add(value);
                    this.m_instructionValueIds.Add(this.m_currentOffset, id);
                }
                else
                {
                    this.m_values[id] = value;
                }
                return id;
            }

            // 给当前指令产生的值或行为分配唯一执行次序。
            private BehaviorFlowPoint NextPoint()
            {
                return new BehaviorFlowPoint(this.m_currentOffset, this.m_currentOrder++);
            }

            // 追加一个不属于普通指令结果的入口值。
            private int AddRootValue(BehaviorValueKind kind, string reference)
            {
                int id = this.m_values.Count;
                this.m_values.Add(new BehaviorValue(
                    id,
                    kind,
                    reference,
                    null,
                    Array.Empty<int>()));

                return id;
            }

            // 从类型定义、引用或规格标记读取结构化类型身份。
            private BehaviorTypeReference ReadTypeReference(Cecil.TypeReference type)
            {
                if (!this.m_typeReferences.TryGetValue(type, out BehaviorTypeReference? reference))
                {
                    reference = this.m_catalog.ReadManagedTypeReference(type, this.m_method.AssemblyPath!);
                    this.m_typeReferences.Add(type, reference);
                }

                return reference;
            }

        }
    }

    /// <summary>
    /// 区分有指令的函数、无函数体声明和原生边界。
    /// </summary>
    public enum MethodBodyKind
    {
        /// <summary>函数体读取失败，不能把空事实当作没有修改。</summary>
        ReadFailure,
        /// <summary>函数具有可读取的托管行为。</summary>
        Executable,
        /// <summary>接口或抽象函数只声明契约。</summary>
        Declaration,
        /// <summary>函数行为由明确的原生库入口实现。</summary>
        PlatformInvocation,
        /// <summary>函数行为由运行时内部实现。</summary>
        RuntimeImplementation,
    }

    /// <summary>
    /// 保存行为事实所在的控制流块和块内执行次序。
    /// </summary>
    public readonly record struct BehaviorFlowPoint(
        int BlockId,
        int Order);

    /// <summary>
    /// 保存控制流转移及必须执行的 finally 入口。
    /// </summary>
    public sealed record BehaviorFlowEdge(
        int? TargetBlockId,
        Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowBranchSemantics Semantics,
        IReadOnlyList<int> FinallyBlockIds);

    /// <summary>
    /// 保存一个控制流块和它的全部后继。
    /// </summary>
    public sealed record BehaviorFlowBlock(
        int Id,
        bool IsReachable,
        IReadOnlyList<BehaviorFlowEdge> Successors,
        int? ConditionValueId = null, int? JumpTargetBlockId = null,
        IReadOnlyList<int>? SwitchTargetBlockIds = null);

    /// <summary>
    /// 按元数据顺序保存异常处理子句；所有范围均为包含起点、不含终点的指令区间。
    /// </summary>
    public sealed record BehaviorExceptionHandler(
        Mono.Cecil.Cil.ExceptionHandlerType Kind,
        int TryStartBlockId,
        int TryEndBlockId,
        int HandlerStartBlockId,
        int HandlerEndBlockId,
        int? FilterStartBlockId,
        BehaviorTypeReference? ExceptionType);

    /// <summary>
    /// 区分后续数据流需要追踪的值来源。
    /// </summary>
    public enum BehaviorValueKind
    {
        /// <summary>当前对象。</summary>
        CurrentInstance,
        /// <summary>函数参数。</summary>
        Parameter,
        /// <summary>局部变量槽。</summary>
        Local,
        /// <summary>编译期常量。</summary>
        Constant,
        /// <summary>字段读取结果。</summary>
        FieldRead,
        /// <summary>数组元素读取结果。</summary>
        ArrayElementRead,
        /// <summary>显式或隐式类型转换结果。</summary>
        Conversion,
        /// <summary>新建对象。</summary>
        NewObject,
        /// <summary>新建数组。</summary>
        NewArray,
        /// <summary>函数调用返回值。</summary>
        CallResult,
        /// <summary>typeof 表达式中的类型。</summary>
        Type,
        /// <summary>函数或委托绑定目标。</summary>
        Function,
        /// <summary>参数、局部变量、字段或数组元素的可写地址。</summary>
        Address,
        /// <summary>多条控制流路径在同一位置汇合后的可能值。</summary>
        Merge,
        /// <summary>异常处理入口收到的异常对象。</summary>
        Exception,
        /// <summary>由明确托管操作码计算出的值。</summary>
        Computation,
        /// <summary>在一个执行位置读取参数、局部值、捕获值或控制流临时槽。</summary>
        SlotRead,
    }

    /// <summary>
    /// 区分函数体中发生的直接存储写入。
    /// </summary>
    public enum BehaviorWriteKind
    {
        /// <summary>字段写入。</summary>
        Field,
        /// <summary>数组元素写入。</summary>
        ArrayElement,
        /// <summary>通过 ref、out 或地址进行的间接写入。</summary>
        Indirect,
    }

    /// <summary>
    /// 区分函数体中已读取的静态、虚、构造和委托调用形式。
    /// </summary>
    public enum BehaviorCallKind
    {
        /// <summary>目标在调用点已经静态确定。</summary>
        Direct,
        /// <summary>目标允许按运行时类型重写。</summary>
        Virtual,
        /// <summary>构造新对象。</summary>
        ObjectCreation,
        /// <summary>调用委托的 Invoke。</summary>
        Delegate,
    }

    /// <summary>
    /// 保存函数内一个值的来源和输入关系。
    /// </summary>
    public sealed record BehaviorValue(
        int Id,
        BehaviorValueKind Kind,
        string? Reference,
        int? ParameterIndex,
        IReadOnlyList<int> InputValueIds)
    {
        /// <summary>字段读取值所引用的结构化字段。</summary>
        public BehaviorMemberReference? Member { get; init; }

        /// <summary>函数或委托值所引用的结构化函数。</summary>
        public BehaviorMethodReference? Method { get; init; }

        /// <summary>反射属性保存真实读写函数，不把属性名称当作行为结论。</summary>
        public BehaviorPropertyReference? Property { get; init; }

        /// <summary>该值已知的结构化类型。</summary>
        public BehaviorTypeReference? Type { get; init; }

        /// <summary>原指令的类型操作数，与 sizeof 等指令的结果类型分开保存。</summary>
        public BehaviorTypeReference? OperandType { get; init; }

        /// <summary>该槽保存托管引用本身；间接写入修改其所指数据，不改变引用槽。</summary>
        public bool IsManagedReferenceSlot { get; init; }

        /// <summary>派生值被读取或产生的执行位置；根槽可以为空。</summary>
        public BehaviorFlowPoint? Point { get; init; }

        /// <summary>指令栈汇合时每条真实入边携带的值，保留分支与值的对应。</summary>
        public IReadOnlyList<BehaviorMergeInput> IncomingValues { get; init; } = Array.Empty<BehaviorMergeInput>();
    }

    /// <summary>记录一个前驱指令向汇合位置传入的值。</summary>
    public sealed record BehaviorMergeInput(int PredecessorBlockId, int ValueId);

    /// <summary>保存一个反射属性的真实访问函数。</summary>
    public sealed record BehaviorPropertyReference(BehaviorMethodReference? Getter, BehaviorMethodReference? Setter);

    /// <summary>
    /// 保存局部槽或参数槽之间的一次赋值关系。
    /// </summary>
    public sealed record BehaviorAssignment(
        int TargetValueId,
        int ValueId,
        BehaviorFlowPoint Point);

    /// <summary>
    /// 保存类型的实际身份、定义身份和构造类型实参。
    /// </summary>
    public sealed record BehaviorTypeReference(
        TypeIdentityTemplate Identity,
        TypeIdentityTemplate DefinitionIdentity,
        IReadOnlyList<TypeIdentityTemplate> ArgumentIdentities,
        string? TargetAssemblyIdentity,
        string? ReferringAssemblyPath,
        string? KnownTypeId)
    {
        /// <summary>直接展示同一类型身份，不另存字符串副本。</summary>
        public string Id => this.Identity.Text;

        /// <summary>直接展示类型开放定义身份。</summary>
        public string DefinitionId => this.DefinitionIdentity.Text;
    }

    /// <summary>
    /// 保存字段的声明身份和元数据位置，字段类型由目录统一查询。
    /// </summary>
    public sealed record BehaviorMemberReference(
        TypeIdentityTemplate DeclaringTypeIdentity,
        string DeclaringTypeDefinitionId,
        string Name)
    {
        /// <summary>当前函数内容中字段引用的原始标记，用于还原完整类型来源。</summary>
        public int ReferenceMetadataToken { get; init; }

        /// <summary>当前模块中的字段声明类型物理身份。</summary>
        public string? KnownDeclaringTypeId { get; init; }

        /// <summary>外部字段声明类型所在程序集的完整身份。</summary>
        public string TargetAssemblyIdentity { get; init; } = string.Empty;

        /// <summary>读取字段引用的真实程序集路径。</summary>
        public string? ReferringAssemblyPath { get; init; }

    }

    /// <summary>
    /// 保存字段、数组元素或引用位置的一次写入事实。
    /// </summary>
    public sealed record BehaviorWrite(
        BehaviorWriteKind Kind,
        int? ReceiverValueId,
        BehaviorMemberReference? Member,
        IReadOnlyList<int> IndexValueIds,
        int ValueId,
        BehaviorFlowPoint Point)
    {
        /// <summary>原写入指令的类型操作数；合成反射写入不伪造此项。</summary>
        public BehaviorTypeReference? OperandType { get; init; }

        // 动态选出的写入仍须满足原成员选择，不将多个候选同时覆盖。
        internal (BehaviorValueReference Input, ValueOrigin Selected)? Selection { get; init; }
    }

    /// <summary>
    /// 保存一个调用实参及其引用传递方式。
    /// </summary>
    public sealed record BehaviorArgument(
        int ValueId,
        RefKind RefKind);

    /// <summary>
    /// 保存调用目标的完整签名、泛型实参和实例调用形态。
    /// </summary>
    public sealed record BehaviorMethodReference(
        MethodIdentityTemplate Identity,
        string DeclaringTypeDefinitionId,
        IReadOnlyList<string> GenericArgumentTypeIds,
        bool HasInstance,
        string TargetAssemblyIdentity,
        string? ReferringAssemblyPath)
    {
        /// <summary>调用在原始模块内使用的函数引用标记。</summary>
        public int ReferenceMetadataToken { get; init; }

        /// <summary>当前模块中的函数声明类型物理身份。</summary>
        public string? KnownDeclaringTypeId { get; init; }

        /// <summary>当前模块中的函数定义标记。</summary>
        public int? KnownMetadataToken { get; init; }

        /// <summary>直接展示函数身份中的名称。</summary>
        public string Name => this.Identity.Name;
    }

    /// <summary>
    /// 保存一次调用的静态引用、接收对象、实参和结果值。
    /// </summary>
    public sealed record BehaviorCall(
        BehaviorCallKind Kind,
        BehaviorMethodReference Target,
        int? ReceiverValueId,
        IReadOnlyList<BehaviorArgument> Arguments,
        int? ResultValueId,
        BehaviorFlowPoint Point)
    {
        /// <summary>虚调用受限到的实际值类型。</summary>
        public BehaviorTypeReference? ConstrainedReceiverType { get; init; }
    }

    /// <summary>
    /// 保存函数返回的值及其位置。
    /// </summary>
    public sealed record BehaviorReturn(
        int? ValueId,
        BehaviorFlowPoint Point);

    /// <summary>
    /// 保存平台调用所声明的原生库和入口名称。
    /// </summary>
    public sealed record NativeBoundary(
        string LibraryName,
        string EntryPoint);

    /// <summary>
    /// 保存一个函数的值来源、写入、调用与返回事实。
    /// </summary>
    /// <param name="MethodId">函数的唯一身份。</param>
    /// <param name="BodyKind">函数体来源。</param>
    /// <param name="Values">按连续值编号排列的来源。</param>
    /// <param name="Assignments">局部槽和参数槽赋值。</param>
    /// <param name="Writes">直接存储写入。</param>
    /// <param name="Calls">函数调用。</param>
    /// <param name="Returns">函数返回。</param>
    /// <param name="Blocks">控制流块。</param>
    /// <param name="ExceptionHandlers">完整异常处理表。</param>
    /// <param name="NativeBoundary">原生库和入口。</param>
    public sealed record MethodBehavior(
        string MethodId,
        MethodBodyKind BodyKind,
        IReadOnlyList<BehaviorValue> Values,
        IReadOnlyList<BehaviorAssignment> Assignments,
        IReadOnlyList<BehaviorWrite> Writes,
        IReadOnlyList<BehaviorCall> Calls,
        IReadOnlyList<BehaviorReturn> Returns,
        IReadOnlyList<BehaviorFlowBlock> Blocks,
        IReadOnlyList<BehaviorExceptionHandler> ExceptionHandlers,
        NativeBoundary? NativeBoundary = null)
    {
        /// <summary>当前函数体尚未读出的原始分析失败。</summary>
        public string? Failure { get; init; }

        // 建立没有可执行行为的声明或原生函数结果。
        internal static MethodBehavior Empty(
            string methodId,
            MethodBodyKind bodyKind,
            NativeBoundary? nativeBoundary = null)
        {
            return new MethodBehavior(
                methodId,
                bodyKind,
                Array.Empty<BehaviorValue>(),
                Array.Empty<BehaviorAssignment>(),
                Array.Empty<BehaviorWrite>(),
                Array.Empty<BehaviorCall>(),
                Array.Empty<BehaviorReturn>(),
                Array.Empty<BehaviorFlowBlock>(),
                Array.Empty<BehaviorExceptionHandler>(),
                nativeBoundary);
        }
    }

    /// <summary>
    /// 保存一批函数行为和固定顺序的查找表。
    /// </summary>
    /// <param name="Methods">按函数身份排列的全部行为。</param>
    /// <param name="Elapsed">读取本批行为的耗时。</param>
    public sealed record BehaviorReadResult(IReadOnlyList<MethodBehavior> Methods, TimeSpan Elapsed)
    {
        /// <summary>按函数身份查找行为。</summary>
        public IReadOnlyDictionary<string, MethodBehavior> MethodsById { get; } =
            Methods.ToDictionary(method => method.MethodId, StringComparer.Ordinal);
    }
}
