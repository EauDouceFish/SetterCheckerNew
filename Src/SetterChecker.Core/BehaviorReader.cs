using System.Collections.Concurrent;
using System.Diagnostics;
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

            return methods.Select(method => ReadManagedBehavior(
                    module,
                    catalog,
                    method))
                .ToArray();
        }

        // 按一个确定的函数定义标记读取函数体或明确的无托管体边界。
        private static MethodBehavior ReadManagedBehavior(
            Cecil.ModuleDefinition module,
            MethodCatalogResult catalog,
            MethodEntry method)
        {
            Cecil.IMetadataTokenProvider? provider = module.LookupToken(
                new Cecil.MetadataToken((uint)method.MetadataToken));
            if (provider is not Cecil.MethodDefinition definition)
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

            return new ManagedBehaviorBuilder(catalog, method).Read(definition.Body);
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
            private readonly Stack<int> m_stack = new();
            private int m_currentOffset;
            private int m_currentOrder;

            // 保存当前托管文件和函数信息。
            public ManagedBehaviorBuilder(
                MethodCatalogResult catalog,
                MethodEntry method)
            {
                this.m_catalog = catalog;
                this.m_method = method;
            }

            // 沿所有实际控制流路径读取函数体并在路径汇合处合并值来源。
            public MethodBehavior Read(Cil.MethodBody body)
            {
                Mono.Cecil.Rocks.MethodBodyRocks.SimplifyMacros(body);
                Cil.Instruction[] instructions = body.Instructions.ToArray();
                IReadOnlyDictionary<int, Cil.Instruction> instructionsByOffset = instructions
                    .ToDictionary(instruction => instruction.Offset);
                Queue<int> pending = new();

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
                    RestoreStack(this.m_incomingStacks[offset]);
                    this.m_currentOffset = offset;
                    this.m_currentOrder = this.m_incomingStacks[offset].Length;
                    ReadInstruction(instruction);
                    int[] outgoingStack = this.m_stack.Reverse().ToArray();

                    foreach (int successor in ReadSuccessors(instruction, instructionsByOffset))
                    {
                        int[] successorStack = instruction.OpCode.Code is Cil.Code.Leave
                            ? Array.Empty<int>()
                            : outgoingStack;
                        AddIncomingStack(successor, successorStack, pending);
                    }
                }

                var flow = ReadManagedControlFlow(body, this.m_incomingStacks.Keys.ToHashSet());
                return new MethodBehavior(
                    this.m_method.Id,
                    MethodBodyKind.Executable,
                    this.m_values,
                    this.m_assignments.Values.OrderBy(item => item.Position).ToArray(),
                    this.m_writes.Values.OrderBy(item => item.Position).ToArray(),
                    this.m_calls.Values.OrderBy(item => item.Position).ToArray(),
                    this.m_returns.Values.OrderBy(item => item.Position).ToArray(),
                    blocks: flow.Blocks,
                    regions: flow.Regions);
            }

            // 把 Cecil 函数体转换为不依赖求值栈的控制流块和区域。
            private (IReadOnlyList<BehaviorFlowBlock> Blocks,
                IReadOnlyList<BehaviorFlowRegion> Regions) ReadManagedControlFlow(
                Cil.MethodBody body,
                IReadOnlySet<int> reachableOffsets)
            {
                Cil.Instruction[] instructions = body.Instructions.ToArray();
                IReadOnlyList<ManagedFlowRegionDraft> regionDrafts = ReadManagedFlowRegions(body);
                int exitBlockId = body.CodeSize;
                List<BehaviorFlowBlock> instructionBlocks = new(instructions.Length);

                foreach (Cil.Instruction instruction in instructions)
                {
                    instructionBlocks.Add(new BehaviorFlowBlock(
                        instruction.Offset,
                        BehaviorFlowBlockKind.Block,
                        reachableOffsets.Contains(instruction.Offset),
                        ReadManagedRegionChain(
                            instruction.Offset,
                            exitBlockId,
                            regionDrafts)[^1].Id,
                        ReadManagedFlowEdges(
                            instruction,
                            body,
                            exitBlockId,
                            regionDrafts)));
                }

                int entryTargetId = instructions.Length == 0
                    ? exitBlockId
                    : instructions[0].Offset;
                BehaviorFlowBlock entry = new(
                    -1,
                    BehaviorFlowBlockKind.Entry,
                    true,
                    regionDrafts[0].Id,
                    new[]
                    {
                        CreateManagedFlowEdge(
                            -1,
                            entryTargetId,
                            false,
                            BehaviorFlowBranchSemantics.Regular,
                            false,
                            body,
                            exitBlockId,
                            regionDrafts),
                    });
                bool exitIsReachable = instructions.Length == 0
                    || instructionBlocks.Any(block => block.IsReachable
                        && block.Successors.Any(edge => edge.TargetBlockId == exitBlockId));
                BehaviorFlowBlock exit = new(
                    exitBlockId,
                    BehaviorFlowBlockKind.Exit,
                    exitIsReachable,
                    regionDrafts[0].Id,
                    Array.Empty<BehaviorFlowEdge>());
                BehaviorFlowBlock[] blocks = instructionBlocks
                    .Prepend(entry)
                    .Append(exit)
                    .ToArray();
                BehaviorFlowRegion[] regions = regionDrafts.Select(region =>
                    new BehaviorFlowRegion(
                        region.Id,
                        region.Kind,
                        region.Parent?.Id,
                        region.Kind == BehaviorFlowRegionKind.Root
                            ? -1
                            : instructions.First(instruction => region.Contains(instruction.Offset)).Offset,
                        region.Kind == BehaviorFlowRegionKind.Root
                            ? exitBlockId
                            : instructions.Last(instruction => region.Contains(instruction.Offset)).Offset,
                        region.ExceptionType))
                    .ToArray();

                return (blocks, regions);
            }

            // 按异常处理表的真实区间建立与 Roslyn 对应的区域树。
            private IReadOnlyList<ManagedFlowRegionDraft> ReadManagedFlowRegions(
                Cil.MethodBody body)
            {
                ManagedFlowRegionDraft root = new(
                    0,
                    BehaviorFlowRegionKind.Root,
                    -1,
                    body.CodeSize + 1,
                    null,
                    null);
                List<ManagedFlowRegionDraft> regions = new() { root };
                IEnumerable<IGrouping<(int TryStart, int TryEnd, bool IsCatch),
                    Cil.ExceptionHandler>> groups = body.ExceptionHandlers.GroupBy(handler => (
                        handler.TryStart.Offset,
                        ReadManagedRegionEnd(handler.TryEnd, body.CodeSize),
                        handler.HandlerType is Cil.ExceptionHandlerType.Catch
                            or Cil.ExceptionHandlerType.Filter));

                foreach (IGrouping<(int TryStart, int TryEnd, bool IsCatch),
                             Cil.ExceptionHandler> group in groups
                             .OrderBy(item => item.Key.TryStart)
                             .ThenByDescending(item => item.Key.TryEnd)
                             .ThenByDescending(item => item.Key.IsCatch))
                {
                    Cil.ExceptionHandler[] handlers = group
                        .OrderBy(handler => handler.FilterStart?.Offset
                            ?? handler.HandlerStart.Offset)
                        .ThenBy(handler => handler.HandlerStart.Offset)
                        .ToArray();
                    ManagedFlowRegionDraft composite = new(
                        regions.Count,
                        group.Key.IsCatch
                            ? BehaviorFlowRegionKind.TryAndCatch
                            : BehaviorFlowRegionKind.TryAndFinally,
                        group.Key.TryStart,
                        handlers.Max(handler => ReadManagedRegionEnd(
                            handler.HandlerEnd,
                            body.CodeSize)),
                        null,
                        null);
                    ManagedFlowRegionDraft tryRegion = new(
                        regions.Count + 1,
                        BehaviorFlowRegionKind.Try,
                        group.Key.TryStart,
                        group.Key.TryEnd,
                        null,
                        null)
                    {
                        Parent = composite,
                    };
                    regions.Add(composite);
                    regions.Add(tryRegion);

                    foreach (Cil.ExceptionHandler handler in handlers)
                    {
                        AddManagedHandlerRegions(body, handler, composite, regions);
                    }
                }

                foreach (ManagedFlowRegionDraft composite in regions.Where(region =>
                             region.Parent == null && region != root))
                {
                    ManagedFlowRegionDraft? parent = regions.Where(region =>
                            region != composite
                            && region.Kind is BehaviorFlowRegionKind.Try
                                or BehaviorFlowRegionKind.Filter
                                or BehaviorFlowRegionKind.Catch
                                or BehaviorFlowRegionKind.Finally
                                or BehaviorFlowRegionKind.Fault
                            && region.Contains(composite))
                        .OrderBy(region => region.EndOffset - region.StartOffset)
                        .ThenBy(region => region.CreationOrder)
                        .FirstOrDefault();
                    composite.Parent = parent ?? root;
                }

                List<ManagedFlowRegionDraft> ordered = new(regions.Count);
                AppendManagedFlowRegions(root, regions, ordered);

                return ordered;
            }

            // 把一条 catch、filter、finally 或 fault 子句加入区域树。
            private void AddManagedHandlerRegions(
                Cil.MethodBody body,
                Cil.ExceptionHandler handler,
                ManagedFlowRegionDraft composite,
                ICollection<ManagedFlowRegionDraft> regions)
            {
                int handlerEnd = ReadManagedRegionEnd(handler.HandlerEnd, body.CodeSize);
                if (handler.HandlerType == Cil.ExceptionHandlerType.Filter)
                {
                    int filterStart = handler.FilterStart?.Offset
                        ?? throw new AnalysisException(
                            $"筛选异常处理器缺少入口：{this.m_method.Id}");
                    ManagedFlowRegionDraft filterAndHandler = new(
                        regions.Count,
                        BehaviorFlowRegionKind.FilterAndHandler,
                        filterStart,
                        handlerEnd,
                        null,
                        handler)
                    {
                        Parent = composite,
                    };
                    ManagedFlowRegionDraft filter = new(
                        regions.Count + 1,
                        BehaviorFlowRegionKind.Filter,
                        filterStart,
                        handler.HandlerStart.Offset,
                        null,
                        handler)
                    {
                        Parent = filterAndHandler,
                    };
                    ManagedFlowRegionDraft catchRegion = new(
                        regions.Count + 2,
                        BehaviorFlowRegionKind.Catch,
                        handler.HandlerStart.Offset,
                        handlerEnd,
                        null,
                        handler)
                    {
                        Parent = filterAndHandler,
                    };
                    regions.Add(filterAndHandler);
                    regions.Add(filter);
                    regions.Add(catchRegion);

                    return;
                }

                BehaviorFlowRegionKind kind = handler.HandlerType switch
                {
                    Cil.ExceptionHandlerType.Catch => BehaviorFlowRegionKind.Catch,
                    Cil.ExceptionHandlerType.Finally => BehaviorFlowRegionKind.Finally,
                    Cil.ExceptionHandlerType.Fault => BehaviorFlowRegionKind.Fault,
                    _ => throw new AnalysisException(
                        $"不支持的 Cecil 异常处理类型：{handler.HandlerType}"),
                };
                BehaviorTypeReference? exceptionType = handler.HandlerType
                        == Cil.ExceptionHandlerType.Catch
                    && handler.CatchType != null
                        ? ReadTypeReference(handler.CatchType)
                        : null;
                ManagedFlowRegionDraft region = new(
                    regions.Count,
                    kind,
                    handler.HandlerStart.Offset,
                    handlerEnd,
                    exceptionType,
                    handler)
                {
                    Parent = composite,
                };
                regions.Add(region);
            }

            // 按父子关系稳定分配区域编号。
            private static void AppendManagedFlowRegions(
                ManagedFlowRegionDraft current,
                IReadOnlyList<ManagedFlowRegionDraft> regions,
                ICollection<ManagedFlowRegionDraft> ordered)
            {
                current.Id = ordered.Count;
                ordered.Add(current);
                foreach (ManagedFlowRegionDraft child in regions.Where(region =>
                             region.Parent == current)
                             .OrderBy(region => region.StartOffset)
                             .ThenByDescending(region => region.EndOffset)
                             .ThenBy(region => region.CreationOrder))
                {
                    AppendManagedFlowRegions(child, regions, ordered);
                }
            }

            // 读取一条指令的明确跳转、顺序或结束边。
            private static IReadOnlyList<BehaviorFlowEdge> ReadManagedFlowEdges(
                Cil.Instruction instruction,
                Cil.MethodBody body,
                int exitBlockId,
                IReadOnlyList<ManagedFlowRegionDraft> regions)
            {
                // 使用当前指令和函数体的固定信息建立一条边。
                BehaviorFlowEdge Edge(
                    int? targetBlockId,
                    BehaviorFlowBranchSemantics semantics,
                    bool isConditional = false,
                    bool runsFinally = false)
                {
                    return CreateManagedFlowEdge(
                        instruction.Offset,
                        targetBlockId,
                        isConditional,
                        semantics,
                        runsFinally,
                        body,
                        exitBlockId,
                        regions);
                }

                if (instruction.OpCode.Code == Cil.Code.Ret)
                {
                    return new[] { Edge(exitBlockId, BehaviorFlowBranchSemantics.Return) };
                }

                if (instruction.OpCode.Code is Cil.Code.Throw or Cil.Code.Rethrow)
                {
                    return new[]
                    {
                        Edge(
                            null,
                            instruction.OpCode.Code == Cil.Code.Throw
                                ? BehaviorFlowBranchSemantics.Throw
                                : BehaviorFlowBranchSemantics.Rethrow),
                    };
                }

                if (instruction.OpCode.Code == Cil.Code.Endfinally)
                {
                    return new[]
                    {
                        Edge(
                            null,
                            BehaviorFlowBranchSemantics.StructuredExceptionHandling),
                    };
                }

                if (instruction.OpCode.Code == Cil.Code.Endfilter)
                {
                    Cil.ExceptionHandler handler = body.ExceptionHandlers.Single(item =>
                        item.HandlerType == Cil.ExceptionHandlerType.Filter
                        && item.FilterStart != null
                        && instruction.Offset >= item.FilterStart.Offset
                        && instruction.Offset < item.HandlerStart.Offset);

                    return new[]
                    {
                        Edge(
                            handler.HandlerStart.Offset,
                            BehaviorFlowBranchSemantics.Regular,
                            isConditional: true),
                        Edge(
                            null,
                            BehaviorFlowBranchSemantics.StructuredExceptionHandling),
                    };
                }

                bool isLeave = instruction.OpCode.Code == Cil.Code.Leave;
                bool isConditional = instruction.OpCode.FlowControl == Cil.FlowControl.Cond_Branch;
                IEnumerable<int?> targets = instruction.OpCode.FlowControl switch
                {
                    Cil.FlowControl.Branch => ReadBranchTargets(instruction)
                        .Select(target => (int?)target),
                    Cil.FlowControl.Cond_Branch => ReadBranchTargets(instruction)
                        .Select(target => (int?)target)
                        .Concat(instruction.Next == null
                            ? Array.Empty<int?>()
                            : new int?[] { instruction.Next.Offset }),
                    _ => instruction.Next == null
                        ? Array.Empty<int?>()
                        : new int?[] { instruction.Next.Offset },
                };

                return targets.Distinct()
                    .OrderBy(target => target)
                    .Select(target => Edge(
                        target,
                        BehaviorFlowBranchSemantics.Regular,
                        isConditional,
                        isLeave))
                    .ToArray();
            }

            // 建立一条边并计算其离开、进入和执行的 finally 区域。
            private static BehaviorFlowEdge CreateManagedFlowEdge(
                int sourceBlockId,
                int? targetBlockId,
                bool isConditional,
                BehaviorFlowBranchSemantics semantics,
                bool runsFinally,
                Cil.MethodBody body,
                int exitBlockId,
                IReadOnlyList<ManagedFlowRegionDraft> regions)
            {
                if (!targetBlockId.HasValue)
                {
                    return new BehaviorFlowEdge(
                        null,
                        isConditional,
                        semantics,
                        Array.Empty<int>(),
                        Array.Empty<int>(),
                        Array.Empty<int>());
                }

                IReadOnlyList<ManagedFlowRegionDraft> sourceRegions = ReadManagedRegionChain(
                    sourceBlockId,
                    exitBlockId,
                    regions);
                IReadOnlyList<ManagedFlowRegionDraft> targetRegions = ReadManagedRegionChain(
                    targetBlockId.Value,
                    exitBlockId,
                    regions);
                int sharedCount = 0;
                while (sharedCount < sourceRegions.Count
                    && sharedCount < targetRegions.Count
                    && sourceRegions[sharedCount] == targetRegions[sharedCount])
                {
                    sharedCount++;
                }

                int[] leaving = sourceRegions.Skip(sharedCount)
                    .Reverse()
                    .Select(region => region.Id)
                    .ToArray();
                int[] entering = targetRegions.Skip(sharedCount)
                    .Select(region => region.Id)
                    .ToArray();
                int[] finallyRegions = runsFinally
                    ? body.ExceptionHandlers.Where(handler =>
                            handler.HandlerType == Cil.ExceptionHandlerType.Finally
                            && sourceBlockId >= handler.TryStart.Offset
                            && sourceBlockId < ReadManagedRegionEnd(
                                handler.TryEnd,
                                body.CodeSize)
                            && (targetBlockId.Value < handler.TryStart.Offset
                                || targetBlockId.Value >= ReadManagedRegionEnd(
                                    handler.TryEnd,
                                    body.CodeSize)))
                        .Select(handler => regions.Single(region =>
                            region.Handler == handler
                            && region.Kind == BehaviorFlowRegionKind.Finally))
                        .OrderBy(region => ReadManagedRegionEnd(
                                region.Handler!.TryEnd,
                                body.CodeSize)
                            - region.Handler.TryStart.Offset)
                        .ThenByDescending(region => region.Handler!.TryStart.Offset)
                        .Select(region => region.Id)
                        .ToArray()
                    : Array.Empty<int>();

                return new BehaviorFlowEdge(
                    targetBlockId,
                    isConditional,
                    semantics,
                    leaving,
                    entering,
                    finallyRegions);
            }

            // 返回一个块从根到最内层的区域链。
            private static IReadOnlyList<ManagedFlowRegionDraft> ReadManagedRegionChain(
                int blockId,
                int exitBlockId,
                IReadOnlyList<ManagedFlowRegionDraft> regions)
            {
                List<ManagedFlowRegionDraft> chain = new() { regions[0] };
                if (blockId is -1 || blockId == exitBlockId)
                {
                    return chain;
                }

                while (true)
                {
                    ManagedFlowRegionDraft? child = regions.Where(region =>
                            region.Parent == chain[^1]
                            && region.Contains(blockId))
                        .OrderBy(region => region.EndOffset - region.StartOffset)
                        .ThenBy(region => region.CreationOrder)
                        .FirstOrDefault();
                    if (child == null)
                    {
                        return chain;
                    }

                    chain.Add(child);
                }
            }

            // 把可空的 Cecil 区间结束转换为函数体末尾。
            private static int ReadManagedRegionEnd(Cil.Instruction? end, int codeSize)
            {
                return end?.Offset ?? codeSize;
            }

            /// <summary>
            /// 保存托管异常区域在稳定编号前的树形信息。
            /// </summary>
            private sealed class ManagedFlowRegionDraft
            {
                // 保存一个 Cecil 异常区域的真实半开区间。
                public ManagedFlowRegionDraft(
                    int creationOrder,
                    BehaviorFlowRegionKind kind,
                    int startOffset,
                    int endOffset,
                    BehaviorTypeReference? exceptionType,
                    Cil.ExceptionHandler? handler)
                {
                    this.CreationOrder = creationOrder;
                    this.Kind = kind;
                    this.StartOffset = startOffset;
                    this.EndOffset = endOffset;
                    this.ExceptionType = exceptionType;
                    this.Handler = handler;
                }

                public int CreationOrder { get; }
                public BehaviorFlowRegionKind Kind { get; }
                public int StartOffset { get; }
                public int EndOffset { get; }
                public BehaviorTypeReference? ExceptionType { get; }
                public Cil.ExceptionHandler? Handler { get; }
                public int Id { get; set; }
                public ManagedFlowRegionDraft? Parent { get; set; }

                // 检查一条指令是否位于本区域的半开区间内。
                public bool Contains(int offset)
                {
                    return offset >= this.StartOffset && offset < this.EndOffset;
                }

                // 检查另一个区域是否完整位于本区域内。
                public bool Contains(ManagedFlowRegionDraft region)
                {
                    return region.StartOffset >= this.StartOffset
                        && region.EndOffset <= this.EndOffset;
                }
            }

            // 返回当前指令完成后可能继续执行的全部下一条指令。
            private static IReadOnlyList<int> ReadSuccessors(
                Cil.Instruction instruction,
                IReadOnlyDictionary<int, Cil.Instruction> instructions)
            {
                IEnumerable<int> successors = instruction.OpCode.FlowControl switch
                {
                    Cil.FlowControl.Branch => ReadBranchTargets(instruction),
                    Cil.FlowControl.Cond_Branch => ReadBranchTargets(instruction).Concat(
                        instruction.Next == null
                            ? Array.Empty<int>()
                            : new[] { instruction.Next.Offset }),
                    Cil.FlowControl.Return or Cil.FlowControl.Throw => Array.Empty<int>(),
                    _ => instruction.Next == null
                        ? Array.Empty<int>()
                        : new[] { instruction.Next.Offset },
                };

                return successors.Where(instructions.ContainsKey)
                    .Distinct()
                    .Order()
                    .ToArray();
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
                        new[] { firstValueId, secondValueId }
                            .Distinct()
                            .Order()
                            .ToArray())
                    {
                        Point = new BehaviorFlowPoint(offset, index),
                    });
                    this.m_mergeValueIds.Add((offset, index), mergeValueId);

                    return mergeValueId;
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

            // 用一条入口状态恢复当前正在模拟的指令栈。
            private void RestoreStack(IReadOnlyList<int> valueIds)
            {
                this.m_stack.Clear();
                foreach (int valueId in valueIds)
                {
                    this.m_stack.Push(valueId);
                }
            }

            // 读取一条会影响值栈或写入事实的基础指令。
            private void ReadInstruction(Cil.Instruction instruction)
            {
                OpCode code = instruction.OpCode;
                object? operand = instruction.Operand;
                int offset = instruction.Offset;

                if (code == OpCodes.Nop)
                {
                    return;
                }

                if (code == OpCodes.Br || code == OpCodes.Leave)
                {
                    return;
                }

                if (code == OpCodes.Brfalse || code == OpCodes.Brtrue)
                {
                    Pop(code, offset);

                    return;
                }

                if (code == OpCodes.Ret)
                {
                    this.m_returns[offset] = new BehaviorReturn(
                        this.m_stack.Count == 0 ? null : this.m_stack.Pop(),
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Ldarga)
                {
                    int argumentAddressIndex = ReadArgumentIndex(code, operand);
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Address,
                        $"argument:{argumentAddressIndex}",
                        null,
                        new[] { GetArgumentValueId(argumentAddressIndex) }));

                    return;
                }

                if (code == OpCodes.Starg)
                {
                    this.m_assignments[offset] = new BehaviorAssignment(
                        GetArgumentValueId(ReadArgumentIndex(code, operand)),
                        Pop(code, offset),
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Ldarg)
                {
                    int slotId = GetArgumentValueId(ReadArgumentIndex(code, operand));
                    this.m_stack.Push(this.m_values[slotId].Kind == BehaviorValueKind.CurrentInstance
                        ? slotId
                        : ReadSlot(slotId));

                    return;
                }

                if (code == OpCodes.Ldc_I4)
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Constant,
                        RequireOperand<int>(instruction).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        null,
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Ldc_I8)
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Constant,
                        RequireOperand<long>(instruction).ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        null,
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Ldc_R4 || code == OpCodes.Ldc_R8)
                {
                    double value = code == OpCodes.Ldc_R4
                        ? RequireOperand<float>(instruction)
                        : RequireOperand<double>(instruction);
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Constant,
                        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        null,
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Ldnull)
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Constant,
                        null,
                        null,
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Ldstr)
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Constant,
                        RequireOperand<string>(instruction),
                        null,
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Ldloc)
                {
                    this.m_stack.Push(ReadSlot(GetLocalValueId(RequireOperand<Cil.VariableDefinition>(instruction).Index)));

                    return;
                }

                if (code == OpCodes.Ldloca)
                {
                    int localIndex = RequireOperand<Cil.VariableDefinition>(instruction).Index;
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Address,
                        $"local:{localIndex}",
                        null,
                        new[] { GetLocalValueId(localIndex) }));

                    return;
                }

                if (code == OpCodes.Stloc)
                {
                    this.m_assignments[offset] = new BehaviorAssignment(
                        GetLocalValueId(RequireOperand<Cil.VariableDefinition>(instruction).Index),
                        Pop(code, offset),
                        offset)
                    {
                        Point = NextPoint(),
                    };

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
                    this.m_stack.Push(AddTypeValue(
                        BehaviorValueKind.NewArray,
                        type,
                        new[] { lengthValueId }));

                    return;
                }

                if (code == OpCodes.Ldtoken)
                {
                    if (operand is Cecil.TypeReference type)
                    {
                        this.m_stack.Push(AddTypeValue(
                            BehaviorValueKind.Type,
                            ReadTypeReference(type),
                            Array.Empty<int>()));

                        return;
                    }

                    if (operand is Cecil.MethodReference methodReference)
                    {
                        this.m_stack.Push(AddMethodValue(
                            BehaviorValueKind.Function,
                            ReadMethodReference(methodReference),
                            Array.Empty<int>()));

                        return;
                    }

                    if (operand is Cecil.FieldReference fieldReference)
                    {
                        this.m_stack.Push(AddMemberValue(
                            BehaviorValueKind.Computation,
                            ReadFieldReference(fieldReference),
                            Array.Empty<int>()));

                        return;
                    }

                    throw new AnalysisException(
                        $"托管 ldtoken 引用种类无法识别：{this.m_method.Id} @ {offset}");
                }

                if (code.Code is Cil.Code.Castclass or Cil.Code.Isinst
                    or Cil.Code.Box or Cil.Code.Unbox or Cil.Code.Unbox_Any
                    or Cil.Code.Ldobj)
                {
                    int valueId = AddTypeValue(
                        BehaviorValueKind.Conversion,
                        ReadTypeReference(RequireOperand<Cecil.TypeReference>(instruction)),
                        new[] { Pop(code, offset) });
                    this.m_values[valueId] = this.m_values[valueId] with { Reference = code.Name };
                    this.m_stack.Push(valueId);

                    return;
                }

                if (code == OpCodes.Ldfld)
                {
                    this.m_stack.Push(AddMemberValue(
                        BehaviorValueKind.FieldRead,
                        ReadFieldReference(RequireOperand<Cecil.FieldReference>(instruction)),
                        new[] { Pop(code, offset) }));

                    return;
                }

                if (code == OpCodes.Ldflda)
                {
                    this.m_stack.Push(AddMemberValue(
                        BehaviorValueKind.Address,
                        ReadFieldReference(RequireOperand<Cecil.FieldReference>(instruction)),
                        new[] { Pop(code, offset) }));

                    return;
                }

                if (code == OpCodes.Ldsfld)
                {
                    this.m_stack.Push(AddMemberValue(
                        BehaviorValueKind.FieldRead,
                        ReadFieldReference(RequireOperand<Cecil.FieldReference>(instruction)),
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Ldsflda)
                {
                    this.m_stack.Push(AddMemberValue(
                        BehaviorValueKind.Address,
                        ReadFieldReference(RequireOperand<Cecil.FieldReference>(instruction)),
                        Array.Empty<int>()));

                    return;
                }

                if (code == OpCodes.Stfld)
                {
                    int valueId = Pop(code, offset);
                    int receiverId = Pop(code, offset);
                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.Field,
                        receiverId,
                        ReadFieldReference(RequireOperand<Cecil.FieldReference>(instruction)),
                        Array.Empty<int>(),
                        valueId,
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Stsfld)
                {
                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.Field,
                        null,
                        ReadFieldReference(RequireOperand<Cecil.FieldReference>(instruction)),
                        Array.Empty<int>(),
                        Pop(code, offset),
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Ldelema)
                {
                    int indexValueId = Pop(code, offset);
                    int arrayValueId = Pop(code, offset);
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Address,
                        RequireOperand<Cecil.TypeReference>(instruction).FullName,
                        null,
                        new[] { arrayValueId, indexValueId }));

                    return;
                }

                if (IsLoadElement(code))
                {
                    int indexValueId = Pop(code, offset);
                    int arrayValueId = Pop(code, offset);
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.ArrayElementRead,
                        code.Name,
                        null,
                        new[] { arrayValueId, indexValueId }));

                    return;
                }

                if (IsStoreElement(code))
                {
                    int valueId = Pop(code, offset);
                    int indexValueId = Pop(code, offset);
                    int arrayValueId = Pop(code, offset);

                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.ArrayElement,
                        arrayValueId,
                        null,
                        new[] { indexValueId },
                        valueId,
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (IsStoreIndirect(code))
                {
                    int valueId = Pop(code, offset);
                    int addressValueId = Pop(code, offset);

                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.Indirect,
                        addressValueId,
                        null,
                        Array.Empty<int>(),
                        valueId,
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Initobj)
                {
                    int addressValueId = Pop(code, offset);
                    int defaultValueId = AddValue(
                        BehaviorValueKind.Constant,
                        $"default:{RequireOperand<Cecil.TypeReference>(instruction).FullName}",
                        null,
                        Array.Empty<int>());
                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.Indirect,
                        addressValueId,
                        null,
                        Array.Empty<int>(),
                        defaultValueId,
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Cpobj)
                {
                    int sourceAddressId = Pop(code, offset);
                    int targetAddressId = Pop(code, offset);
                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.Indirect,
                        targetAddressId,
                        null,
                        Array.Empty<int>(),
                        sourceAddressId,
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Cpblk || code == OpCodes.Initblk)
                {
                    int lengthValueId = Pop(code, offset);
                    int valueId = Pop(code, offset);
                    int targetAddressId = Pop(code, offset);
                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.Indirect,
                        targetAddressId,
                        null,
                        new[] { lengthValueId },
                        valueId,
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
                {
                    Cecil.MethodReference methodReference =
                        RequireOperand<Cecil.MethodReference>(instruction);
                    ManagedMethodReferenceInfo reference =
                        this.m_catalog.ReadManagedMethodReference(
                            methodReference,
                            this.m_method.AssemblyPath!);
                    IReadOnlyList<int> inputs = code == OpCodes.Ldvirtftn
                        ? new[] { Pop(code, offset) }
                        : Array.Empty<int>();
                    this.m_stack.Push(AddMethodValue(
                        BehaviorValueKind.Function,
                        BehaviorMethodReference.From(reference),
                        inputs));

                    return;
                }

                if (code == OpCodes.Dup)
                {
                    int valueId = Pop(code, offset);
                    this.m_stack.Push(valueId);
                    this.m_stack.Push(valueId);

                    return;
                }

                if (code == OpCodes.Pop)
                {
                    Pop(code, offset);

                    return;
                }

                if (TryReadStackOperation(code, offset))
                {
                    return;
                }

                throw new AnalysisException(
                    $"托管函数包含尚未读取的操作码：{this.m_method.Id} @ {offset} {code.Name}");
            }

            // 从 Cecil 指令取得一个类型完全确定的操作数。
            private static T RequireOperand<T>(Cil.Instruction instruction)
            {
                return RequireOperand<T>(
                    instruction.OpCode,
                    instruction.Operand,
                    instruction.Offset);
            }

            // 在操作数类型不符合操作码约定时明确停止分析。
            private static T RequireOperand<T>(OpCode code, object? operand, int offset)
            {
                return operand is T value
                    ? value
                    : throw new AnalysisException(
                        $"托管操作数类型错误：@ {offset} {code.Name}，需要 {typeof(T).Name}");
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
                    Cil.Code.Switch or Cil.Code.Throw or Cil.Code.Endfilter => (1, 0),
                    Cil.Code.Rethrow or Cil.Code.Endfinally or Cil.Code.Volatile
                        or Cil.Code.Readonly or Cil.Code.Constrained or Cil.Code.Unaligned
                        or Cil.Code.Tail => (0, 0),
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

                if (shape.Value.Push == 1)
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Computation,
                        code.Name,
                        null,
                        inputs));
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
                ManagedMethodReferenceInfo reference =
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
                BehaviorCallKind kind = ReadCallKind(code, definition);
                int? resultValueId = null;

                if (code == OpCodes.Newobj || !reference.ReturnsVoid)
                {
                    resultValueId = AddValue(
                        code == OpCodes.Newobj
                            ? BehaviorValueKind.NewObject
                            : BehaviorValueKind.CallResult,
                        code == OpCodes.Newobj
                            ? reference.Identity.DeclaringType.StableText
                            : reference.Identity.Text,
                        null,
                        arguments.Select(argument => argument.ValueId).ToArray());
                    if (code == OpCodes.Newobj)
                    {
                        this.m_values[resultValueId.Value] = this.m_values[resultValueId.Value] with
                        {
                            Type = ReadTypeReference(method.DeclaringType),
                        };
                    }

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
                    BehaviorMethodReference.From(reference),
                    receiverValueId,
                    arguments,
                    resultValueId,
                    offset)
                {
                    ConstrainedReceiverType = constrainedType,
                    Point = NextPoint(),
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
                    this.m_stack.Push(AddTypeValue(
                        BehaviorValueKind.NewArray,
                        ReadTypeReference(array),
                        arguments));

                    return;
                }

                int receiverId = Pop(code, offset);
                if (method.Name == "Set")
                {
                    this.m_writes[offset] = new BehaviorWrite(
                        BehaviorWriteKind.ArrayElement,
                        receiverId,
                        null,
                        arguments[..^1],
                        arguments[^1],
                        offset)
                    {
                        Point = NextPoint(),
                    };
                }
                else if (method.Name is "Get" or "Address")
                {
                    this.m_stack.Push(AddTypeValue(
                        method.Name == "Get"
                            ? BehaviorValueKind.ArrayElementRead
                            : BehaviorValueKind.Address,
                        ReadTypeReference(array.ElementType),
                        new[] { receiverId }.Concat(arguments).ToArray()));
                }
                else
                {
                    throw new AnalysisException(
                        $"数组固有函数无法识别：{this.m_method.Id} @ {offset} {method.FullName}");
                }
            }

            // 按真实函数定义区分委托、可重写调用和普通直接调用。
            private BehaviorCallKind ReadCallKind(
                OpCode code,
                MethodEntry definition)
            {
                if (code == OpCodes.Newobj)
                {
                    return BehaviorCallKind.ObjectCreation;
                }

                if (definition.Name == "Invoke"
                    && this.m_catalog.IsDelegateType(this.m_catalog.TypesById[definition.TypeId]))
                {
                    return BehaviorCallKind.Delegate;
                }

                return code == OpCodes.Callvirt && definition.IsVirtual
                    ? BehaviorCallKind.Virtual
                    : BehaviorCallKind.Direct;
            }

            // 把 Cecil 参数定义转换为包含当前对象槽的参数编号。
            private int ReadArgumentIndex(OpCode code, object? operand)
            {
                int index = RequireOperand<Cecil.ParameterDefinition>(
                    code,
                    operand,
                    this.m_currentOffset).Index;
                return this.m_method.IsStatic ? index : index + 1;
            }

            // 判断一条指令是否把值写入一维数组元素。
            private static bool IsStoreElement(OpCode code)
            {
                return code.Code is Cil.Code.Stelem_Any
                    or Cil.Code.Stelem_I or Cil.Code.Stelem_I1 or Cil.Code.Stelem_I2
                    or Cil.Code.Stelem_I4 or Cil.Code.Stelem_I8 or Cil.Code.Stelem_R4
                    or Cil.Code.Stelem_R8 or Cil.Code.Stelem_Ref;
            }

            // 判断一条指令是否从一维数组元素读取值。
            private static bool IsLoadElement(OpCode code)
            {
                return code.Code is Cil.Code.Ldelem_Any or Cil.Code.Ldelem_I
                    or Cil.Code.Ldelem_I1 or Cil.Code.Ldelem_I2 or Cil.Code.Ldelem_I4
                    or Cil.Code.Ldelem_I8 or Cil.Code.Ldelem_U1 or Cil.Code.Ldelem_U2
                    or Cil.Code.Ldelem_U4 or Cil.Code.Ldelem_R4 or Cil.Code.Ldelem_R8
                    or Cil.Code.Ldelem_Ref;
            }

            // 判断一条指令是否通过托管地址写入值。
            private static bool IsStoreIndirect(OpCode code)
            {
                return code.Code is Cil.Code.Stind_I or Cil.Code.Stind_I1
                    or Cil.Code.Stind_I2 or Cil.Code.Stind_I4 or Cil.Code.Stind_I8
                    or Cil.Code.Stind_R4 or Cil.Code.Stind_R8 or Cil.Code.Stind_Ref
                    or Cil.Code.Stobj;
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

            // 追加一个值来源并返回其函数内编号。
            private int AddValue(
                BehaviorValueKind kind,
                string? reference,
                int? parameterIndex,
                IReadOnlyList<int> inputValueIds)
            {
                if (this.m_instructionValueIds.TryGetValue(
                        this.m_currentOffset,
                        out int existingId))
                {
                    BehaviorValue existing = this.m_values[existingId];
                    this.m_values[existingId] = existing with
                    {
                        Kind = kind,
                        Reference = reference,
                        ParameterIndex = parameterIndex,
                        InputValueIds = inputValueIds,
                        Point = NextPoint(),
                    };

                    return existingId;
                }

                int id = this.m_values.Count;

                this.m_values.Add(new BehaviorValue(
                    id,
                    kind,
                    reference,
                    parameterIndex,
                    inputValueIds)
                {
                    Point = NextPoint(),
                });
                this.m_instructionValueIds.Add(this.m_currentOffset, id);

                return id;
            }

            // 给当前指令产生的值或行为分配唯一执行次序。
            private BehaviorFlowPoint NextPoint()
            {
                return new BehaviorFlowPoint(this.m_currentOffset, this.m_currentOrder++);
            }

            // 保留本次读取的位置，避免后续改写变量影响已经入栈的值。
            private int ReadSlot(int slotId)
            {
                return AddValue(
                    BehaviorValueKind.SlotRead,
                    this.m_values[slotId].Reference,
                    null,
                    new[] { slotId });
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

            // 追加一个带结构化字段身份的字段读取或字段地址值。
            private int AddMemberValue(
                BehaviorValueKind kind,
                BehaviorMemberReference member,
                IReadOnlyList<int> inputValueIds)
            {
                int id = AddValue(kind, member.Name, null, inputValueIds);

                this.m_values[id] = this.m_values[id] with { Member = member };

                return id;
            }

            // 追加一个带结构化函数身份的函数指针或委托绑定值。
            private int AddMethodValue(
                BehaviorValueKind kind,
                BehaviorMethodReference method,
                IReadOnlyList<int> inputValueIds)
            {
                int id = AddValue(kind, method.Name, null, inputValueIds);

                this.m_values[id] = this.m_values[id] with { Method = method };

                return id;
            }

            // 追加一个带结构化类型身份的类型或数组创建值。
            private int AddTypeValue(
                BehaviorValueKind kind,
                BehaviorTypeReference type,
                IReadOnlyList<int> inputValueIds)
            {
                int id = AddValue(kind, type.Id, null, inputValueIds);

                this.m_values[id] = this.m_values[id] with { Type = type };

                return id;
            }

            // 从字段定义或成员引用标记读取结构化字段身份。
            private BehaviorMemberReference ReadFieldReference(Cecil.FieldReference field)
            {
                ManagedFieldReferenceInfo reference =
                    this.m_catalog.ReadManagedFieldReference(
                        field,
                        this.m_method.AssemblyPath!);

                return BehaviorMemberReference.From(reference);
            }

            // 从类型定义、引用或规格标记读取结构化类型身份。
            private BehaviorTypeReference ReadTypeReference(Cecil.TypeReference type)
            {
                ManagedTypeReferenceInfo reference =
                    this.m_catalog.ReadManagedTypeReference(
                        type,
                        this.m_method.AssemblyPath!);

                return BehaviorTypeReference.From(reference);
            }

            // 从 Cecil 函数引用读取结构化调用身份。
            private BehaviorMethodReference ReadMethodReference(Cecil.MethodReference method)
            {
                return BehaviorMethodReference.From(
                    this.m_catalog.ReadManagedMethodReference(
                        method,
                        this.m_method.AssemblyPath!));
            }
        }
    }

    /// <summary>
    /// 区分有指令的函数、无函数体声明和原生边界。
    /// </summary>
    public enum MethodBodyKind
    {
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
    /// 区分控制流入口、普通块和出口。
    /// </summary>
    public enum BehaviorFlowBlockKind
    {
        /// <summary>函数控制流入口。</summary>
        Entry,
        /// <summary>包含普通行为的块。</summary>
        Block,
        /// <summary>函数控制流出口。</summary>
        Exit,
    }

    /// <summary>
    /// 区分控制流边的执行含义。
    /// </summary>
    public enum BehaviorFlowBranchSemantics
    {
        /// <summary>普通控制流转移。</summary>
        Regular,
        /// <summary>函数返回。</summary>
        Return,
        /// <summary>结构化异常处理内部转移。</summary>
        StructuredExceptionHandling,
        /// <summary>抛出异常。</summary>
        Throw,
        /// <summary>重新抛出当前异常。</summary>
        Rethrow,
    }

    /// <summary>
    /// 区分局部生命周期与异常处理区域。
    /// </summary>
    public enum BehaviorFlowRegionKind
    {
        /// <summary>完整函数区域。</summary>
        Root,
        /// <summary>try 区域。</summary>
        Try,
        /// <summary>异常筛选区域。</summary>
        Filter,
        /// <summary>catch 区域。</summary>
        Catch,
        /// <summary>异常筛选及其处理区域。</summary>
        FilterAndHandler,
        /// <summary>try 和全部 catch 的组合区域。</summary>
        TryAndCatch,
        /// <summary>finally 区域。</summary>
        Finally,
        /// <summary>try 和 finally 的组合区域。</summary>
        TryAndFinally,
        /// <summary>IL fault 区域。</summary>
        Fault,
    }

    /// <summary>
    /// 保存一条控制流转移及其经过的异常处理区域。
    /// </summary>
    public sealed record BehaviorFlowEdge(
        int? TargetBlockId,
        bool IsConditional,
        BehaviorFlowBranchSemantics Semantics,
        IReadOnlyList<int> LeavingRegionIds,
        IReadOnlyList<int> EnteringRegionIds,
        IReadOnlyList<int> FinallyRegionIds);

    /// <summary>
    /// 保存一个控制流块和它的全部后继。
    /// </summary>
    public sealed record BehaviorFlowBlock(
        int Id,
        BehaviorFlowBlockKind Kind,
        bool IsReachable,
        int? EnclosingRegionId,
        IReadOnlyList<BehaviorFlowEdge> Successors);

    /// <summary>
    /// 保存编译器已经建立的控制流区域范围。
    /// </summary>
    public sealed record BehaviorFlowRegion(
        int Id,
        BehaviorFlowRegionKind Kind,
        int? ParentRegionId,
        int FirstBlockId,
        int LastBlockId,
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
    /// 区分函数体中的静态、虚、构造、委托和函数指针调用形式。
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
        /// <summary>调用函数指针。</summary>
        FunctionPointer,
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

        /// <summary>类型或数组创建值所引用的结构化类型。</summary>
        public BehaviorTypeReference? Type { get; init; }

        /// <summary>派生值被读取或产生的执行位置；根槽可以为空。</summary>
        public BehaviorFlowPoint? Point { get; init; }

    }

    /// <summary>
    /// 保存局部槽或参数槽之间的一次赋值关系。
    /// </summary>
    public sealed record BehaviorAssignment(
        int TargetValueId,
        int ValueId,
        int Position)
    {
        /// <summary>赋值发生的执行位置。</summary>
        public required BehaviorFlowPoint Point { get; init; }
    }

    /// <summary>
    /// 保存类型的实际身份、定义身份和构造类型实参。
    /// </summary>
    public sealed record BehaviorTypeReference(
        string Id,
        string DefinitionId,
        IReadOnlyList<string> ArgumentIds,
        string? TargetAssemblyIdentity,
        string? ReferringAssemblyPath)
    {
        /// <summary>当前模块中的类型定义物理身份。</summary>
        public string? KnownTypeId { get; init; }

        internal TypeIdentityTemplate Identity { get; init; } = null!;

        internal TypeIdentityTemplate DefinitionIdentity { get; init; } = null!;

        internal IReadOnlyList<TypeIdentityTemplate> ArgumentIdentities { get; init; } =
            Array.Empty<TypeIdentityTemplate>();

        // 把函数总表使用的类型身份转换为公开行为事实。
        internal static BehaviorTypeReference From(ManagedTypeReferenceInfo reference)
        {
            return new BehaviorTypeReference(
                reference.Identity.StableText,
                reference.Definition.StableText,
                reference.Arguments.Select(type => type.StableText).ToArray(),
                reference.TargetAssemblyIdentity,
                reference.ReferringAssemblyPath)
            {
                KnownTypeId = reference.KnownTypeId,
                Identity = reference.Identity,
                DefinitionIdentity = reference.Definition,
                ArgumentIdentities = reference.Arguments,
            };
        }
    }

    /// <summary>
    /// 保存字段的声明类型、名称和字段类型，不依赖显示字符串解析。
    /// </summary>
    public sealed record BehaviorMemberReference(
        string DeclaringTypeId,
        string DeclaringTypeDefinitionId,
        IReadOnlyList<string> DeclaringTypeArgumentIds,
        string Name,
        string FieldTypeId)
    {
        /// <summary>当前模块中的字段声明类型物理身份。</summary>
        public string? KnownDeclaringTypeId { get; init; }

        /// <summary>外部字段声明类型所在程序集的完整身份。</summary>
        public string TargetAssemblyIdentity { get; init; } = string.Empty;

        /// <summary>读取字段引用的真实程序集路径。</summary>
        public string? ReferringAssemblyPath { get; init; }

        internal TypeIdentityTemplate DeclaringTypeIdentity { get; init; } = null!;

        internal TypeIdentityTemplate FieldTypeIdentity { get; init; } = null!;

        // 把函数总表使用的字段身份转换为公开行为事实。
        internal static BehaviorMemberReference From(ManagedFieldReferenceInfo reference)
        {
            return new BehaviorMemberReference(
                reference.DeclaringType.StableText,
                reference.DeclaringTypeDefinition.StableText,
                reference.DeclaringTypeArguments.Select(type => type.StableText).ToArray(),
                reference.Name,
                reference.FieldType.StableText)
            {
                KnownDeclaringTypeId = reference.KnownDeclaringTypeId,
                TargetAssemblyIdentity = reference.TargetAssemblyIdentity,
                ReferringAssemblyPath = reference.ReferringAssemblyPath,
                DeclaringTypeIdentity = reference.DeclaringType,
                FieldTypeIdentity = reference.FieldType,
            };
        }
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
        int Position)
    {
        /// <summary>写入发生的执行位置。</summary>
        public required BehaviorFlowPoint Point { get; init; }
    }

    /// <summary>
    /// 保存一个调用实参及其引用传递方式。
    /// </summary>
    public sealed record BehaviorArgument(
        int ValueId,
        CatalogRefKind RefKind);

    /// <summary>
    /// 保存调用目标的完整签名、泛型实参和实例调用形态。
    /// </summary>
    public sealed record BehaviorMethodReference(
        string DeclaringTypeId,
        string DeclaringTypeDefinitionId,
        IReadOnlyList<string> DeclaringTypeArgumentIds,
        string Name,
        int GenericArity,
        IReadOnlyList<string> ParameterTypeIds,
        string ReturnTypeId,
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

        internal MethodIdentityTemplate Identity { get; init; } = null!;

        internal IReadOnlyList<TypeIdentityTemplate> GenericArgumentIdentities { get; init; } =
            Array.Empty<TypeIdentityTemplate>();

        // 把函数总表使用的函数身份转换为公开行为事实。
        internal static BehaviorMethodReference From(ManagedMethodReferenceInfo reference)
        {
            return new BehaviorMethodReference(
                reference.Identity.DeclaringType.StableText,
                reference.DeclaringTypeDefinition.StableText,
                reference.DeclaringTypeArguments.Select(type => type.StableText).ToArray(),
                reference.Identity.Name,
                reference.Identity.GenericArity,
                reference.Identity.Parameters.Select(type => type.StableText).ToArray(),
                reference.Identity.ReturnType.StableText,
                reference.GenericArguments.Select(type => type.StableText).ToArray(),
                reference.HasInstance,
                reference.TargetAssemblyIdentity,
                reference.ReferringAssemblyPath)
            {
                ReferenceMetadataToken = reference.ReferenceMetadataToken,
                KnownDeclaringTypeId = reference.KnownDeclaringTypeId,
                KnownMetadataToken = reference.KnownMetadataToken,
                Identity = reference.Identity,
                GenericArgumentIdentities = reference.GenericArguments,
            };
        }
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
        int Position)
    {
        /// <summary>虚调用受限到的实际值类型。</summary>
        public BehaviorTypeReference? ConstrainedReceiverType { get; init; }

        /// <summary>调用发生的执行位置。</summary>
        public required BehaviorFlowPoint Point { get; init; }
    }

    /// <summary>
    /// 保存函数返回的值及其位置。
    /// </summary>
    public sealed record BehaviorReturn(
        int? ValueId,
        int Position)
    {
        /// <summary>返回或 yield 发生的执行位置。</summary>
        public required BehaviorFlowPoint Point { get; init; }
    }

    /// <summary>
    /// 保存平台调用所声明的原生库和入口名称。
    /// </summary>
    public sealed record NativeBoundary(
        string LibraryName,
        string EntryPoint);

    /// <summary>
    /// 保存一个函数的值来源、写入、调用与返回事实。
    /// </summary>
    public sealed class MethodBehavior
    {
        // 保存一组已经读取并保持顺序的函数行为事实。
        /// <summary>
        /// 建立一个函数的行为结果。
        /// </summary>
        public MethodBehavior(
            string methodId,
            MethodBodyKind bodyKind,
            IReadOnlyList<BehaviorValue> values,
            IReadOnlyList<BehaviorAssignment> assignments,
            IReadOnlyList<BehaviorWrite> writes,
            IReadOnlyList<BehaviorCall> calls,
            IReadOnlyList<BehaviorReturn> returns,
            NativeBoundary? nativeBoundary = null,
            IReadOnlyList<BehaviorFlowBlock>? blocks = null,
            IReadOnlyList<BehaviorFlowRegion>? regions = null)
        {
            this.MethodId = methodId;
            this.BodyKind = bodyKind;
            this.Values = values;
            this.Assignments = assignments;
            this.Writes = writes;
            this.Calls = calls;
            this.Returns = returns;
            this.NativeBoundary = nativeBoundary;
            this.Blocks = blocks ?? Array.Empty<BehaviorFlowBlock>();
            this.Regions = regions ?? Array.Empty<BehaviorFlowRegion>();
        }

        /// <summary>函数的唯一身份。</summary>
        public string MethodId { get; }

        /// <summary>函数体来源种类。</summary>
        public MethodBodyKind BodyKind { get; }

        /// <summary>函数体的控制流块。</summary>
        public IReadOnlyList<BehaviorFlowBlock> Blocks { get; }

        /// <summary>函数体的局部生命周期和异常处理区域。</summary>
        public IReadOnlyList<BehaviorFlowRegion> Regions { get; }

        /// <summary>按连续值编号排列的来源，可直接用值编号作为下标。</summary>
        public IReadOnlyList<BehaviorValue> Values { get; }

        /// <summary>局部槽和参数槽之间的赋值事实。</summary>
        public IReadOnlyList<BehaviorAssignment> Assignments { get; }

        /// <summary>直接存储写入事实。</summary>
        public IReadOnlyList<BehaviorWrite> Writes { get; }

        /// <summary>调用事实。</summary>
        public IReadOnlyList<BehaviorCall> Calls { get; }

        /// <summary>返回事实。</summary>
        public IReadOnlyList<BehaviorReturn> Returns { get; }

        /// <summary>平台调用函数声明的原生库和入口。</summary>
        public NativeBoundary? NativeBoundary { get; }

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
                nativeBoundary);
        }
    }

    /// <summary>
    /// 保存一批函数行为和固定顺序的查找表。
    /// </summary>
    public sealed class BehaviorReadResult
    {
        // 保存按函数身份排好顺序的行为结果。
        /// <summary>
        /// 建立一批函数的行为读取结果。
        /// </summary>
        public BehaviorReadResult(IReadOnlyList<MethodBehavior> methods, TimeSpan elapsed)
        {
            this.Methods = methods;
            this.MethodsById = methods.ToDictionary(method => method.MethodId, StringComparer.Ordinal);
            this.Elapsed = elapsed;
        }

        /// <summary>按函数身份排序的全部行为。</summary>
        public IReadOnlyList<MethodBehavior> Methods { get; }

        /// <summary>按函数身份查找行为。</summary>
        public IReadOnlyDictionary<string, MethodBehavior> MethodsById { get; }

        /// <summary>读取这一批函数行为所用的时间。</summary>
        public TimeSpan Elapsed { get; }
    }
}
