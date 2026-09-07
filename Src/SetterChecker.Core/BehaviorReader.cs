using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
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
            IReadOnlyDictionary<string, SourceAssemblyMaterial> sourceAssemblies =
                material.SourceAssemblies.ToDictionary(
                    assembly => assembly.Name,
                    StringComparer.Ordinal);
            Dictionary<ISymbol, string> sourceMethodIds = new(SymbolEqualityComparer.Default);
            foreach (MethodEntry method in catalog.Methods.Where(method => method.SourceSymbol != null))
            {
                sourceMethodIds.Add(method.SourceSymbol!.OriginalDefinition, method.Id);
            }

            long sourcePartitionCount = (long)jobs * 4;
            BehaviorWorkItem[] workItems = methods
                .Where(method => method.SourceSymbol != null)
                .GroupBy(method => method.AssemblyName, StringComparer.Ordinal)
                .SelectMany(group =>
                {
                    MethodEntry[] assemblyMethods = group.ToArray();
                    int batchSize = (int)Math.Max(
                        1,
                        (assemblyMethods.Length + sourcePartitionCount - 1)
                            / sourcePartitionCount);

                    return assemblyMethods.Chunk(batchSize).Select(batch => new BehaviorWorkItem(
                        sourceAssemblies[group.Key],
                        null,
                        batch));
                })
                .Concat(methods
                    .Where(method => method.SourceSymbol == null)
                    .GroupBy(
                        method => method.AssemblyPath
                            ?? throw new AnalysisException($"托管函数缺少真实文件：{method.Id}"),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => new BehaviorWorkItem(null, group.Key, group.ToArray())))
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
                    parts.Add(workItem.SourceAssembly != null
                        ? ReadSourceBehaviors(
                            workItem.SourceAssembly,
                            catalog,
                            sourceMethodIds,
                            workItem.Methods,
                            token)
                        : ReadManagedBehaviors(
                            workItem.AssemblyPath!,
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

        // 使用 Roslyn 当前编译内容直接读取这一批源码函数。
        private static IReadOnlyList<MethodBehavior> ReadSourceBehaviors(
            SourceAssemblyMaterial assembly,
            MethodCatalogResult catalog,
            IReadOnlyDictionary<ISymbol, string> sourceMethodIds,
            IReadOnlyList<MethodEntry> methods,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return methods.Select(method => ReadSourceBehavior(
                    assembly.Compilation,
                    catalog,
                    sourceMethodIds,
                    method,
                    cancellationToken))
                .ToArray();
        }

        // 为源码读取错误补充当前函数身份，确保真实项目问题可以直接定位。
        private static MethodBehavior ReadSourceBehavior(
            CSharpCompilation compilation,
            MethodCatalogResult catalog,
            IReadOnlyDictionary<ISymbol, string> sourceMethodIds,
            MethodEntry method,
            CancellationToken cancellationToken)
        {
            try
            {
                return new SourceBehaviorBuilder(
                    compilation,
                    catalog,
                    sourceMethodIds,
                    method,
                    cancellationToken).Read();
            }
            catch (AnalysisException exception)
            {
                throw new AnalysisException($"读取源码函数失败：{method.Id} => {exception.Message}");
            }
        }

        // 一次打开真实文件并读取这一批托管函数。
        private static IReadOnlyList<MethodBehavior> ReadManagedBehaviors(
            string assemblyPath,
            MethodCatalogResult catalog,
            IReadOnlyList<MethodEntry> methods)
        {
            using ManagedAssemblyResolver resolver = new(catalog, assemblyPath);
            Cecil.ModuleDefinition module = resolver.ReadAssembly(assemblyPath).MainModule;

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

        /// <summary>
        /// 让 Cecil 依赖读取复用函数总表的精确文件定位，并在本批结束时释放文件。
        /// </summary>
        private sealed class ManagedAssemblyResolver : Cecil.IAssemblyResolver
        {
            private readonly MethodCatalogResult m_catalog;
            private readonly string m_referringAssemblyPath;
            private readonly Dictionary<string, Cecil.AssemblyDefinition> m_assemblies;
            private readonly bool m_ownsAssemblies;

            // 为当前文件保存引用上下文，嵌套读取共享本批已打开的程序集。
            public ManagedAssemblyResolver(
                MethodCatalogResult catalog,
                string referringAssemblyPath,
                Dictionary<string, Cecil.AssemblyDefinition>? assemblies = null)
            {
                this.m_catalog = catalog;
                this.m_referringAssemblyPath = referringAssemblyPath;
                this.m_ownsAssemblies = assemblies == null;
                this.m_assemblies = assemblies ?? new(StringComparer.OrdinalIgnoreCase);
            }

            // 按引用的完整身份选择唯一真实文件。
            public Cecil.AssemblyDefinition Resolve(Cecil.AssemblyNameReference name)
            {
                return ReadAssembly(this.m_catalog.ResolveAssemblyPath(
                    new System.Reflection.AssemblyName(name.FullName),
                    this.m_referringAssemblyPath));
            }

            // Cecil 的读取参数不会改变本工具统一的文件与身份约束。
            public Cecil.AssemblyDefinition Resolve(
                Cecil.AssemblyNameReference name,
                Cecil.ReaderParameters parameters)
            {
                return Resolve(name);
            }

            // 每个精确路径只打开一次，并把新文件本身设为其依赖的引用上下文。
            public Cecil.AssemblyDefinition ReadAssembly(string path)
            {
                if (!this.m_assemblies.TryGetValue(path, out Cecil.AssemblyDefinition? assembly))
                {
                    assembly = Cecil.AssemblyDefinition.ReadAssembly(
                        path,
                        new Cecil.ReaderParameters
                        {
                            AssemblyResolver = new ManagedAssemblyResolver(
                                this.m_catalog,
                                path,
                                this.m_assemblies),
                            InMemory = true,
                            ReadingMode = Cecil.ReadingMode.Deferred,
                        });
                    this.m_assemblies.Add(path, assembly);
                }

                return assembly;
            }

            // 只有本批读取的拥有者负责释放共享的所有程序集。
            public void Dispose()
            {
                if (this.m_ownsAssemblies)
                {
                    foreach (Cecil.AssemblyDefinition assembly in this.m_assemblies.Values)
                    {
                        assembly.Dispose();
                    }
                }
            }
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
        /// 保存一次共享源码编译或托管文件读取所处理的函数批次。
        /// </summary>
        private sealed record BehaviorWorkItem(
            SourceAssemblyMaterial? SourceAssembly,
            string? AssemblyPath,
            IReadOnlyList<MethodEntry> Methods);

        /// <summary>
        /// 使用 Roslyn 操作树把一个源码函数转换为统一行为事实。
        /// </summary>
        private sealed class SourceBehaviorBuilder : OperationWalker
        {
            private readonly CSharpCompilation m_compilation;
            private readonly MethodCatalogResult m_catalog;
            private readonly IReadOnlyDictionary<ISymbol, string> m_sourceMethodIds;
            private readonly MethodEntry m_method;
            private readonly CancellationToken m_cancellationToken;
            private readonly Dictionary<IOperation, int> m_valueIds = new(
                ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IParameterSymbol, int> m_parameterValueIds = new(
                SymbolEqualityComparer.Default);
            private readonly Dictionary<ILocalSymbol, int> m_localValueIds = new(
                SymbolEqualityComparer.Default);
            private readonly Dictionary<ISymbol, int> m_capturedValueIds = new(
                SymbolEqualityComparer.Default);
            private readonly Dictionary<CaptureId, int> m_captureValueIds = new();
            private readonly Dictionary<CaptureId, IOperation> m_captureOperations = new();
            private readonly List<BehaviorValue> m_values = new();
            private readonly List<BehaviorAssignment> m_assignments = new();
            private readonly List<BehaviorWrite> m_writes = new();
            private readonly List<BehaviorCall> m_calls = new();
            private readonly List<BehaviorReturn> m_returns = new();
            private readonly List<BehaviorFlowBlock> m_blocks = new();
            private readonly List<BehaviorFlowRegion> m_regions = new();
            private readonly HashSet<IOperation> m_recordedCalls = new(
                ReferenceEqualityComparer.Instance);
            private readonly HashSet<IOperation> m_writeTargets = new(
                ReferenceEqualityComparer.Instance);
            private readonly HashSet<IOperation> m_recordedInitializers = new(
                ReferenceEqualityComparer.Instance);
            private readonly Stack<int> m_implicitReceivers = new();
            private readonly Stack<int> m_conditionalReceivers = new();
            private int? m_currentInstanceValueId;
            private int? m_capturedInstanceValueId;
            private int m_currentBlockId;
            private int m_nextOrder;
            private bool m_hasInitializerBlock;

            // 保存当前源码编译、函数总表和正在读取的函数。
            public SourceBehaviorBuilder(
                CSharpCompilation compilation,
                MethodCatalogResult catalog,
                IReadOnlyDictionary<ISymbol, string> sourceMethodIds,
                MethodEntry method,
                CancellationToken cancellationToken)
            {
                this.m_compilation = compilation;
                this.m_catalog = catalog;
                this.m_sourceMethodIds = sourceMethodIds;
                this.m_method = method;
                this.m_cancellationToken = cancellationToken;
            }

            // 读取函数声明、明确原生入口或编译器生成的简单访问器。
            public MethodBehavior Read()
            {
                IMethodSymbol symbol = this.m_method.SourceSymbol
                    ?? throw new AnalysisException($"源码函数缺少 Roslyn 符号：{this.m_method.Id}");
                if (symbol.IsAbstract)
                {
                    return MethodBehavior.Empty(this.m_method.Id, MethodBodyKind.Declaration);
                }

                System.Reflection.MethodImplAttributes implementation =
                    symbol.MethodImplementationFlags;
                if ((implementation & System.Reflection.MethodImplAttributes.CodeTypeMask)
                        == System.Reflection.MethodImplAttributes.Runtime
                    || (implementation & System.Reflection.MethodImplAttributes.InternalCall) != 0)
                {
                    return MethodBehavior.Empty(
                        this.m_method.Id,
                        MethodBodyKind.RuntimeImplementation);
                }

                DllImportData? import = symbol.GetDllImportData();
                if (import != null)
                {
                    return MethodBehavior.Empty(
                        this.m_method.Id,
                        MethodBodyKind.PlatformInvocation,
                        new NativeBoundary(
                            import.ModuleName
                                ?? throw new AnalysisException(
                                    $"平台调用缺少原生库名称：{this.m_method.Id}"),
                            import.EntryPointName ?? symbol.Name));
                }

                if (symbol.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
                {
                    BeginBlock(-1);
                    ReadInitializers(symbol);
                    this.m_hasInitializerBlock = this.m_nextOrder > 0;
                }

                IOperation? operation = ReadRootOperation(symbol);
                if (operation == null)
                {
                    ControlFlowGraph? expressionFlow = ReadExpressionBodyFlow(symbol);
                    if (expressionFlow != null)
                    {
                        ReadControlFlow(expressionFlow);
                    }
                    else
                    {
                        if (!this.m_hasInitializerBlock)
                        {
                            BeginBlock(1);
                        }

                        ReadCompilerProvidedBody(symbol);
                        BuildCompilerProvidedFlow();
                    }
                }
                else if (operation is ILocalFunctionOperation localFunction)
                {
                    ReadControlFlow(CreateNestedControlFlow(localFunction.Symbol));
                }
                else if (operation is IAnonymousFunctionOperation anonymousFunction)
                {
                    ReadControlFlow(CreateNestedControlFlow(anonymousFunction.Symbol));
                }
                else if (operation is IMethodBodyOperation methodBody)
                {
                    ReadControlFlow(ControlFlowGraph.Create(
                        methodBody,
                        this.m_cancellationToken));
                }
                else if (operation is IConstructorBodyOperation constructorBody)
                {
                    ReadControlFlow(ControlFlowGraph.Create(
                        constructorBody,
                        this.m_cancellationToken));
                }
                else
                {
                    throw new AnalysisException(
                        $"源码函数包含未知控制流根：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart} {operation.Kind}");
                }

                return new MethodBehavior(
                    this.m_method.Id,
                    MethodBodyKind.Executable,
                    this.m_values,
                    this.m_assignments.OrderBy(item => item.Position).ToArray(),
                    this.m_writes.OrderBy(item => item.Position).ToArray(),
                    this.m_calls.OrderBy(item => item.Position).ToArray(),
                    this.m_returns.OrderBy(item => item.Position).ToArray(),
                    executionKind: symbol.IsIterator
                        ? BehaviorExecutionKind.IteratorBody
                        : BehaviorExecutionKind.Immediate,
                    blocks: this.m_blocks.OrderBy(item => item.Id).ToArray(),
                    regions: this.m_regions.OrderBy(item => item.Id).ToArray());
            }

            // 为自动访问器或隐式静态构造函数建立最小真实控制流。
            private void BuildCompilerProvidedFlow()
            {
                if (this.m_hasInitializerBlock)
                {
                    this.m_blocks.Add(new BehaviorFlowBlock(
                        -2,
                        BehaviorFlowBlockKind.Entry,
                        true,
                        0,
                        new[] { RegularEdge(-1) }));
                    this.m_blocks.Add(new BehaviorFlowBlock(
                        -1,
                        BehaviorFlowBlockKind.Block,
                        true,
                        0,
                        new[] { RegularEdge(0) }));
                    this.m_blocks.Add(new BehaviorFlowBlock(
                        0,
                        BehaviorFlowBlockKind.Exit,
                        true,
                        0,
                        Array.Empty<BehaviorFlowEdge>()));
                    this.m_regions.Add(new BehaviorFlowRegion(
                        0,
                        BehaviorFlowRegionKind.Root,
                        null,
                        -2,
                        0,
                        null));
                    return;
                }

                this.m_blocks.Add(new BehaviorFlowBlock(
                    0,
                    BehaviorFlowBlockKind.Entry,
                    true,
                    0,
                    new[] { RegularEdge(1) }));
                this.m_blocks.Add(new BehaviorFlowBlock(
                    1,
                    BehaviorFlowBlockKind.Block,
                    true,
                    0,
                    new[] { RegularEdge(2) }));
                this.m_blocks.Add(new BehaviorFlowBlock(
                    2,
                    BehaviorFlowBlockKind.Exit,
                    true,
                    0,
                    Array.Empty<BehaviorFlowEdge>()));
                this.m_regions.Add(new BehaviorFlowRegion(
                    0,
                    BehaviorFlowRegionKind.Root,
                    null,
                    0,
                    2,
                    null));
            }

            // 建立没有异常区域变化的普通控制流边。
            private static BehaviorFlowEdge RegularEdge(int targetBlockId)
            {
                return new BehaviorFlowEdge(
                    targetBlockId,
                    false,
                    BehaviorFlowBranchSemantics.Regular,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>());
            }

            // 从包含函数的控制流图取得局部函数或匿名函数自己的控制流图。
            private ControlFlowGraph CreateNestedControlFlow(IMethodSymbol symbol)
            {
                IMethodSymbol containing = symbol.ContainingSymbol as IMethodSymbol
                    ?? throw new AnalysisException(
                        $"内部函数缺少包含函数：{this.m_method.Id}");
                ControlFlowGraph parent = CreateSymbolControlFlow(containing);
                if (symbol.MethodKind == MethodKind.LocalFunction)
                {
                    return parent.GetLocalFunctionControlFlowGraphInScope(
                        symbol,
                        this.m_cancellationToken);
                }

                IFlowAnonymousFunctionOperation flow = EnumerateGraphOperations(parent)
                    .OfType<IFlowAnonymousFunctionOperation>()
                    .Single(candidate => SymbolEqualityComparer.Default.Equals(
                        candidate.Symbol,
                        symbol));
                return parent.GetAnonymousFunctionControlFlowGraphInScope(
                    flow,
                    this.m_cancellationToken);
            }

            // 为普通函数或内部函数建立可继续向内查找的控制流图。
            private ControlFlowGraph CreateSymbolControlFlow(IMethodSymbol symbol)
            {
                IOperation operation = ReadRootOperation(symbol)
                    ?? throw new AnalysisException(
                        $"源码函数没有控制流根：{this.m_method.Id}");
                return operation switch
                {
                    IMethodBodyOperation methodBody => ControlFlowGraph.Create(
                        methodBody,
                        this.m_cancellationToken),
                    IConstructorBodyOperation constructorBody => ControlFlowGraph.Create(
                        constructorBody,
                        this.m_cancellationToken),
                    ILocalFunctionOperation local => CreateNestedControlFlow(local.Symbol),
                    IAnonymousFunctionOperation anonymous =>
                        CreateNestedControlFlow(anonymous.Symbol),
                    _ => throw new AnalysisException(
                        $"源码函数无法建立控制流：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart} {operation.Kind}"),
                };
            }

            // 列出控制流图中的顶层操作和分支值以定位匿名函数节点。
            private static IEnumerable<IOperation> EnumerateGraphOperations(ControlFlowGraph graph)
            {
                foreach (BasicBlock block in graph.Blocks)
                {
                    foreach (IOperation operation in block.Operations)
                    {
                        yield return operation;
                        foreach (IOperation descendant in EnumerateOperations(operation))
                        {
                            yield return descendant;
                        }
                    }

                    if (block.BranchValue != null)
                    {
                        yield return block.BranchValue;
                        foreach (IOperation descendant in EnumerateOperations(block.BranchValue))
                        {
                            yield return descendant;
                        }
                    }
                }
            }

            // 按基本块顺序读取编译器已经展开的源码行为。
            private void ReadControlFlow(ControlFlowGraph graph)
            {
                BuildSourceFlow(graph);
                foreach (BasicBlock block in graph.Blocks)
                {
                    if (!block.IsReachable)
                    {
                        continue;
                    }

                    BeginBlock(block.Ordinal);
                    foreach (IOperation operation in block.Operations)
                    {
                        Visit(operation);
                    }

                    if (block.BranchValue == null)
                    {
                        continue;
                    }

                    bool returns = block.FallThroughSuccessor?.Semantics
                            == ControlFlowBranchSemantics.Return
                        || block.ConditionalSuccessor?.Semantics
                            == ControlFlowBranchSemantics.Return;
                    int valueId = returns
                        && (this.m_method.SourceSymbol?.ReturnsByRef == true
                            || this.m_method.SourceSymbol?.ReturnsByRefReadonly == true)
                            ? ReadAddress(block.BranchValue)
                            : ReadValue(block.BranchValue);
                    if (returns)
                    {
                        this.m_returns.Add(CreateReturn(
                                                    valueId,
                                                    block.BranchValue.Syntax.SpanStart));
                    }
                }
            }

            // 直接复制 Roslyn 的控制流块、区域和异常边关系。
            private void BuildSourceFlow(ControlFlowGraph graph)
            {
                Dictionary<ControlFlowRegion, int> regionIds = new(
                    ReferenceEqualityComparer.Instance);
                AddSourceRegion(graph.Root, null, regionIds);
                if (this.m_hasInitializerBlock)
                {
                    int rootRegionId = regionIds[graph.Root];
                    this.m_blocks.Add(new BehaviorFlowBlock(
                        -2,
                        BehaviorFlowBlockKind.Entry,
                        true,
                        rootRegionId,
                        new[] { RegularEdge(-1) }));
                    this.m_blocks.Add(new BehaviorFlowBlock(
                        -1,
                        BehaviorFlowBlockKind.Block,
                        true,
                        rootRegionId,
                        new[] { RegularEdge(graph.Blocks[0].Ordinal) }));
                }

                foreach (BasicBlock block in graph.Blocks)
                {
                    List<BehaviorFlowEdge> successors = new();
                    if (block.FallThroughSuccessor != null)
                    {
                        successors.Add(ReadSourceEdge(
                            block.FallThroughSuccessor,
                            regionIds));
                    }

                    if (block.ConditionalSuccessor != null)
                    {
                        successors.Add(ReadSourceEdge(
                            block.ConditionalSuccessor,
                            regionIds));
                    }

                    this.m_blocks.Add(new BehaviorFlowBlock(
                        block.Ordinal,
                        block.Kind switch
                        {
                            BasicBlockKind.Entry when this.m_hasInitializerBlock =>
                                BehaviorFlowBlockKind.Block,
                            BasicBlockKind.Entry => BehaviorFlowBlockKind.Entry,
                            BasicBlockKind.Block => BehaviorFlowBlockKind.Block,
                            BasicBlockKind.Exit => BehaviorFlowBlockKind.Exit,
                            _ => throw new AnalysisException(
                                $"源码函数包含未知控制流块：{this.m_method.Id} "
                                    + block.Kind.ToString()),
                        },
                        block.IsReachable,
                        regionIds[block.EnclosingRegion],
                        successors));
                }
            }

            // 递归复制一个 Roslyn 控制流区域及其子区域。
            private void AddSourceRegion(
                ControlFlowRegion region,
                int? parentRegionId,
                Dictionary<ControlFlowRegion, int> regionIds)
            {
                int regionId = regionIds.Count;
                regionIds.Add(region, regionId);
                this.m_regions.Add(new BehaviorFlowRegion(
                    regionId,
                    ReadSourceRegionKind(region.Kind),
                    parentRegionId,
                    parentRegionId == null && this.m_hasInitializerBlock
                        ? -2
                        : region.FirstBlockOrdinal,
                    region.LastBlockOrdinal,
                    region.ExceptionType == null
                        ? null
                        : ReadTypeReference(region.ExceptionType)));
                foreach (ControlFlowRegion nested in region.NestedRegions)
                {
                    AddSourceRegion(nested, regionId, regionIds);
                }
            }

            // 复制一条 Roslyn 分支和它经过的异常处理区域。
            private static BehaviorFlowEdge ReadSourceEdge(
                ControlFlowBranch branch,
                IReadOnlyDictionary<ControlFlowRegion, int> regionIds)
            {
                return new BehaviorFlowEdge(
                    branch.Destination?.Ordinal,
                    branch.IsConditionalSuccessor,
                    branch.Semantics switch
                    {
                        ControlFlowBranchSemantics.None =>
                            BehaviorFlowBranchSemantics.None,
                        ControlFlowBranchSemantics.Regular =>
                            BehaviorFlowBranchSemantics.Regular,
                        ControlFlowBranchSemantics.Return =>
                            BehaviorFlowBranchSemantics.Return,
                        ControlFlowBranchSemantics.StructuredExceptionHandling =>
                            BehaviorFlowBranchSemantics.StructuredExceptionHandling,
                        ControlFlowBranchSemantics.ProgramTermination =>
                            BehaviorFlowBranchSemantics.ProgramTermination,
                        ControlFlowBranchSemantics.Throw =>
                            BehaviorFlowBranchSemantics.Throw,
                        ControlFlowBranchSemantics.Rethrow =>
                            BehaviorFlowBranchSemantics.Rethrow,
                        ControlFlowBranchSemantics.Error =>
                            BehaviorFlowBranchSemantics.Error,
                        _ => throw new AnalysisException(
                            $"未知 Roslyn 控制流分支：{branch.Semantics}"),
                    },
                    branch.LeavingRegions.Select(region => regionIds[region]).ToArray(),
                    branch.EnteringRegions.Select(region => regionIds[region]).ToArray(),
                    branch.FinallyRegions.Select(region => regionIds[region]).ToArray());
            }

            // 将当前 Roslyn 区域种类映射到公开控制流事实。
            private static BehaviorFlowRegionKind ReadSourceRegionKind(
                ControlFlowRegionKind kind)
            {
                return kind switch
                {
                    ControlFlowRegionKind.Root => BehaviorFlowRegionKind.Root,
                    ControlFlowRegionKind.LocalLifetime => BehaviorFlowRegionKind.LocalLifetime,
                    ControlFlowRegionKind.Try => BehaviorFlowRegionKind.Try,
                    ControlFlowRegionKind.Filter => BehaviorFlowRegionKind.Filter,
                    ControlFlowRegionKind.Catch => BehaviorFlowRegionKind.Catch,
                    ControlFlowRegionKind.FilterAndHandler =>
                        BehaviorFlowRegionKind.FilterAndHandler,
                    ControlFlowRegionKind.TryAndCatch => BehaviorFlowRegionKind.TryAndCatch,
                    ControlFlowRegionKind.Finally => BehaviorFlowRegionKind.Finally,
                    ControlFlowRegionKind.TryAndFinally => BehaviorFlowRegionKind.TryAndFinally,
                    ControlFlowRegionKind.StaticLocalInitializer =>
                        BehaviorFlowRegionKind.StaticLocalInitializer,
                    ControlFlowRegionKind.ErroneousBody => BehaviorFlowRegionKind.ErroneousBody,
                    _ => throw new AnalysisException($"未知 Roslyn 控制流区域：{kind}"),
                };
            }

            // 建立带有当前执行位置的槽赋值事实。
            private BehaviorAssignment CreateAssignment(
                int targetValueId,
                int valueId,
                int position)
            {
                return new BehaviorAssignment(targetValueId, valueId, position)
                {
                    Point = NextPoint(),
                };
            }

            // 建立带有当前执行位置的写入事实。
            private BehaviorWrite CreateWrite(
                BehaviorWriteKind kind,
                int? receiverValueId,
                BehaviorMemberReference? member,
                IReadOnlyList<int> indexValueIds,
                int valueId,
                int position)
            {
                return new BehaviorWrite(
                    kind,
                    receiverValueId,
                    member,
                    indexValueIds,
                    valueId,
                    position)
                {
                    Point = NextPoint(),
                };
            }

            // 建立带有当前执行位置的调用事实。
            private BehaviorCall CreateCall(
                BehaviorCallKind kind,
                BehaviorMethodReference target,
                int? receiverValueId,
                IReadOnlyList<BehaviorArgument> arguments,
                int? resultValueId,
                int position,
                BehaviorTypeReference? constrainedReceiverType = null)
            {
                return new BehaviorCall(
                    kind,
                    target,
                    receiverValueId,
                    arguments,
                    resultValueId,
                    position)
                {
                    ConstrainedReceiverType = constrainedReceiverType,
                    Point = NextPoint(),
                };
            }

            // 建立带有当前执行位置的返回事实。
            private BehaviorReturn CreateReturn(int? valueId, int position)
            {
                return new BehaviorReturn(valueId, position)
                {
                    Point = NextPoint(),
                };
            }

            // 切换到一个控制流块并从块首重新编号事实。
            private void BeginBlock(int blockId)
            {
                this.m_currentBlockId = blockId;
                this.m_nextOrder = 0;
            }

            // 返回当前控制流块中的下一个严格执行位置。
            private BehaviorFlowPoint NextPoint()
            {
                return new BehaviorFlowPoint(
                    this.m_currentBlockId,
                    this.m_nextOrder++);
            }

            // 把实际会由当前构造函数执行的字段和自动属性初始化器记为写入。
            private void ReadInitializers(IMethodSymbol constructor)
            {
                bool delegatesToAnotherConstructor = constructor.DeclaringSyntaxReferences
                    .Select(reference => reference.GetSyntax(this.m_cancellationToken))
                    .OfType<ConstructorDeclarationSyntax>()
                    .Any(declaration => declaration.Initializer?.IsKind(
                        SyntaxKind.ThisConstructorInitializer) == true);
                if (delegatesToAnotherConstructor)
                {
                    return;
                }

                Dictionary<SyntaxTree, int> treeOrder = this.m_compilation.SyntaxTrees
                    .Select((tree, index) => (tree, index))
                    .ToDictionary(item => item.tree, item => item.index);
                List<(ISymbol Member, EqualsValueClauseSyntax Initializer, string Description)>
                    initializers = new();
                foreach (ISymbol member in constructor.ContainingType.GetMembers())
                {
                    if (member is IFieldSymbol field
                        && field.IsStatic == constructor.IsStatic)
                    {
                        foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
                        {
                            if (reference.GetSyntax(this.m_cancellationToken)
                                    is VariableDeclaratorSyntax { Initializer: { } initializer })
                            {
                                initializers.Add((field, initializer, "字段"));
                            }
                        }
                    }
                    else if (member is IPropertySymbol property
                        && property.IsStatic == constructor.IsStatic)
                    {
                        foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
                        {
                            if (reference.GetSyntax(this.m_cancellationToken)
                                    is PropertyDeclarationSyntax { Initializer: { } initializer })
                            {
                                initializers.Add((property, initializer, "属性"));
                            }
                        }
                    }
                }

                foreach ((ISymbol member, EqualsValueClauseSyntax initializer, string description)
                         in initializers.OrderBy(item => treeOrder[item.Initializer.SyntaxTree])
                             .ThenBy(item => item.Initializer.SpanStart))
                {
                    IOperation value = ReadInitializerValue(initializer, description);
                    int valueId = ReadValue(value);
                    ReadInitializerEffects(value, valueId);
                    BehaviorMemberReference storage = member switch
                    {
                        IFieldSymbol field => ReadFieldReference(field),
                        IPropertySymbol property => BehaviorMemberReference.From(
                            this.m_catalog.ReadSourcePropertyStorageReference(
                                property,
                                this.m_method.AssemblyName)),
                        _ => throw new AnalysisException(
                            $"未知初始化成员：{this.m_method.Id} @ {initializer.SpanStart}"),
                    };
                    this.m_writes.Add(CreateWrite(
                        BehaviorWriteKind.Field,
                        constructor.IsStatic ? null : GetCurrentInstanceValueId(),
                        storage,
                        Array.Empty<int>(),
                        valueId,
                        initializer.SpanStart));
                }
            }

            // 从完整初始化语句读取值，兼容编译器不为感叹号节点单独建立操作树的情况。
            private IOperation ReadInitializerValue(
                EqualsValueClauseSyntax initializer,
                string description)
            {
                SemanticModel model = this.m_compilation.GetSemanticModel(initializer.SyntaxTree);
                IOperation? operation = model.GetOperation(initializer, this.m_cancellationToken);
                IOperation? value = operation switch
                {
                    IFieldInitializerOperation field => field.Value,
                    IPropertyInitializerOperation property => property.Value,
                    IVariableInitializerOperation variable => variable.Value,
                    _ => model.GetOperation(initializer.Value, this.m_cancellationToken),
                };

                return value ?? throw new AnalysisException(
                    $"{description}初始化器没有操作树：{this.m_method.Id} "
                        + $"@ {initializer.SpanStart}");
            }

            // 找到函数声明或表达式体对应的 Roslyn 操作树根。
            private IOperation? ReadRootOperation(IMethodSymbol symbol)
            {
                foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
                {
                    SyntaxNode syntax = reference.GetSyntax(this.m_cancellationToken);
                    SemanticModel model = this.m_compilation.GetSemanticModel(syntax.SyntaxTree);
                    IOperation? operation = model.GetOperation(syntax, this.m_cancellationToken);
                    IOperation? root = FindControlFlowRoot(operation);
                    if (root != null)
                    {
                        return root;
                    }

                    SyntaxNode? body = syntax switch
                    {
                        BaseMethodDeclarationSyntax method =>
                            (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
                        AccessorDeclarationSyntax accessor =>
                            accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody?.Expression,
                        LocalFunctionStatementSyntax local =>
                            local.Body ?? (SyntaxNode?)local.ExpressionBody?.Expression,
                        AnonymousFunctionExpressionSyntax anonymous => anonymous.Body,
                        PropertyDeclarationSyntax property => property.ExpressionBody?.Expression,
                        IndexerDeclarationSyntax indexer => indexer.ExpressionBody?.Expression,
                        _ => null,
                    };
                    if (body != null)
                    {
                        operation = model.GetOperation(body, this.m_cancellationToken);
                        root = FindControlFlowRoot(operation);
                        if (root != null)
                        {
                            return root;
                        }
                    }
                }

                return null;
            }

            // 让 Roslyn 为表达式体属性或索引器建立真实分支图。
            private ControlFlowGraph? ReadExpressionBodyFlow(IMethodSymbol symbol)
            {
                foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
                {
                    SyntaxNode syntax = reference.GetSyntax(this.m_cancellationToken);
                    ArrowExpressionClauseSyntax? expressionBody = syntax switch
                    {
                        ArrowExpressionClauseSyntax arrow => arrow,
                        BaseMethodDeclarationSyntax method => method.ExpressionBody,
                        AccessorDeclarationSyntax accessor => accessor.ExpressionBody,
                        LocalFunctionStatementSyntax local => local.ExpressionBody,
                        PropertyDeclarationSyntax property => property.ExpressionBody,
                        IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
                        _ => null,
                    };
                    if (expressionBody == null)
                    {
                        continue;
                    }

                    SemanticModel model = this.m_compilation.GetSemanticModel(
                        expressionBody.SyntaxTree);
                    return ControlFlowGraph.Create(
                        expressionBody,
                        model,
                        this.m_cancellationToken);
                }

                return null;
            }

            // 从声明或表达式节点向上找到 Roslyn 提供的真实函数体根。
            private static IOperation? FindControlFlowRoot(IOperation? operation)
            {
                for (IOperation? current = operation; current != null; current = current.Parent)
                {
                    if (current is IMethodBodyOperation
                        or IConstructorBodyOperation
                        or ILocalFunctionOperation
                        or IAnonymousFunctionOperation)
                    {
                        return current;
                    }
                }

                return null;
            }

            // 读取自动属性、自动事件和没有源码语句的编译器函数体。
            private void ReadCompilerProvidedBody(IMethodSymbol symbol)
            {
                if (symbol.MethodKind == MethodKind.Constructor
                    && symbol.IsImplicitlyDeclared
                    && symbol.ContainingType.TypeKind == TypeKind.Class)
                {
                    INamedTypeSymbol baseType = symbol.ContainingType.BaseType
                        ?? throw new AnalysisException(
                            $"隐式实例构造函数缺少基类：{this.m_method.Id}");
                    IMethodSymbol baseConstructor = baseType.InstanceConstructors.Single(
                        constructor => constructor.Parameters.Length == 0);
                    this.m_calls.Add(CreateCall(
                        BehaviorCallKind.Direct,
                        ReadMethodReference(baseConstructor),
                        GetCurrentInstanceValueId(),
                        Array.Empty<BehaviorArgument>(),
                        null,
                        symbol.ContainingType.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0));
                    return;
                }

                if (symbol.AssociatedSymbol is IPropertySymbol property)
                {
                    IFieldSymbol? field = symbol.ContainingType.GetMembers()
                        .OfType<IFieldSymbol>()
                        .SingleOrDefault(candidate => SymbolEqualityComparer.Default.Equals(
                            candidate.AssociatedSymbol,
                            property));
                    if (field == null)
                    {
                        throw new AnalysisException($"属性访问器没有源码函数体：{this.m_method.Id}");
                    }

                    int position = symbol.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0;
                    BehaviorMemberReference member = ReadFieldReference(field);
                    int? receiverValueId = field.IsStatic ? null : GetCurrentInstanceValueId();
                    if (symbol.MethodKind == MethodKind.PropertyGet)
                    {
                        int valueId = AddMemberValue(
                            BehaviorValueKind.FieldRead,
                            member,
                            receiverValueId == null
                                ? Array.Empty<int>()
                                : new[] { receiverValueId.Value });
                        this.m_returns.Add(CreateReturn(valueId, position));
                    }
                    else
                    {
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.Field,
                            receiverValueId,
                            member,
                            Array.Empty<int>(),
                            AddValue(
                                BehaviorValueKind.SlotRead,
                                symbol.Parameters[0].Name,
                                null,
                                new[] { GetParameterValueId(symbol.Parameters[0]) }),
                            position));
                    }

                    return;
                }

                if (symbol.AssociatedSymbol is IEventSymbol eventSymbol)
                {
                    int position = symbol.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0;
                    BehaviorMemberReference member = ReadEventReference(eventSymbol);
                    int? receiverValueId = eventSymbol.IsStatic
                        ? null
                        : GetCurrentInstanceValueId();
                    int oldValueId = AddMemberValue(
                        BehaviorValueKind.FieldRead,
                        member,
                        receiverValueId == null
                            ? Array.Empty<int>()
                            : new[] { receiverValueId.Value });
                    int newValueId = AddValue(
                        BehaviorValueKind.SlotRead,
                        symbol.Parameters[0].Name,
                        null,
                        new[] { GetParameterValueId(symbol.Parameters[0]) });
                    string operationName = symbol.MethodKind switch
                    {
                        MethodKind.EventAdd => "Combine",
                        MethodKind.EventRemove => "Remove",
                        _ => throw new AnalysisException(
                            $"事件访问器种类未知：{this.m_method.Id} {symbol.MethodKind}"),
                    };
                    INamedTypeSymbol delegateType = this.m_compilation.GetTypeByMetadataName(
                            "System.Delegate")
                        ?? throw new AnalysisException("当前源码编译缺少 System.Delegate。");
                    IMethodSymbol operation = delegateType.GetMembers(operationName)
                        .OfType<IMethodSymbol>()
                        .Single(method => method.IsStatic
                            && method.Parameters.Length == 2
                            && method.Parameters.All(parameter =>
                                SymbolEqualityComparer.Default.Equals(
                                    parameter.Type,
                                    delegateType)));
                    int resultValueId = AddValue(
                        BehaviorValueKind.CallResult,
                        operation.MetadataName,
                        null,
                        new[] { oldValueId, newValueId });
                    this.m_calls.Add(CreateCall(
                        BehaviorCallKind.Direct,
                        ReadMethodReference(operation),
                        null,
                        new[]
                        {
                            new BehaviorArgument(oldValueId, CatalogRefKind.None),
                            new BehaviorArgument(newValueId, CatalogRefKind.None),
                        },
                        resultValueId,
                        position));
                    this.m_writes.Add(CreateWrite(
                        BehaviorWriteKind.Field,
                        receiverValueId,
                        member,
                        Array.Empty<int>(),
                        resultValueId,
                        position));

                    return;
                }

                if (symbol.IsExtern)
                {
                    throw new AnalysisException(
                        $"源码 extern 函数不是已支持的平台调用：{this.m_method.Id}");
                }

                if (symbol.MethodKind == MethodKind.StaticConstructor
                    && symbol.IsImplicitlyDeclared
                    && this.m_hasInitializerBlock)
                {
                    return;
                }

                if (symbol.IsImplicitlyDeclared)
                {
                    throw new AnalysisException(
                        $"未支持的编译器隐式函数体：{this.m_method.Id} {symbol.MethodKind}");
                }

                throw new AnalysisException($"源码函数没有可读取的函数体：{this.m_method.Id}");
            }

            // 记录普通赋值产生的局部关系或外部写入。
            public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
            {
                ReadSimpleAssignmentValue(operation);
                base.VisitSimpleAssignment(operation);
            }

            // 记录空值赋值真正执行时产生的写入和表达式结果。
            public override void VisitCoalesceAssignment(ICoalesceAssignmentOperation operation)
            {
                if (this.m_valueIds.ContainsKey(operation))
                {
                    return;
                }

                int currentValueId = ReadValue(operation.Target);
                int assignedValueId = ReadValue(operation.Value);
                this.m_writeTargets.Add(operation.Target);
                RecordWrite(operation.Target, assignedValueId, operation.Syntax.SpanStart);
                AddOperationValue(
                    operation,
                    BehaviorValueKind.Merge,
                    "coalesce-assignment",
                    new[] { currentValueId, assignedValueId });
            }

            // 将解构赋值中的每个目标和值按位置一一展开。
            public override void VisitDeconstructionAssignment(
                IDeconstructionAssignmentOperation operation)
            {
                RecordDeconstruction(
                    operation.Target,
                    operation.Value,
                    operation.Syntax.SpanStart);
            }

            // 记录复合赋值的最终写入位置。
            public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
            {
                ReadCompoundAssignmentValue(operation);
                base.VisitCompoundAssignment(operation);
            }

            // 记录递增和递减表达式的最终写入位置。
            public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
            {
                ReadIncrementValue(operation);
                base.VisitIncrementOrDecrement(operation);
            }

            // 记录局部变量初始化的值来源。
            public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
            {
                if (operation.Initializer != null)
                {
                    this.m_assignments.Add(CreateAssignment(
                                            GetLocalValueId(operation.Symbol),
                                            ReadValue(operation.Initializer.Value),
                                            operation.Syntax.SpanStart));
                }

                base.VisitVariableDeclarator(operation);
            }

            // 记录控制流图临时槽与其真实值来源。
            public override void VisitFlowCapture(IFlowCaptureOperation operation)
            {
                this.m_captureOperations[operation.Id] = operation.Value;
                int captureValueId = GetCaptureValueId(operation.Id);
                this.m_assignments.Add(CreateAssignment(
                    captureValueId,
                    ReadValue(operation.Value),
                    operation.Syntax.SpanStart));
            }

            // 记录普通函数、接口函数、虚函数或委托调用。
            public override void VisitInvocation(IInvocationOperation operation)
            {
                RecordInvocation(operation);
                base.VisitInvocation(operation);
            }

            // 记录构造函数调用和新对象值。
            public override void VisitObjectCreation(IObjectCreationOperation operation)
            {
                int createdValueId = RecordObjectCreation(operation);
                ReadInitializerEffects(operation, createdValueId);
            }

            // 记录泛型 new T() 及其初始化器的值来源。
            public override void VisitTypeParameterObjectCreation(
                ITypeParameterObjectCreationOperation operation)
            {
                int createdValueId = ReadTypeParameterObjectCreation(operation);
                ReadInitializerEffects(operation, createdValueId);
            }

            // 读取对象或集合初始化器对刚创建对象产生的成员行为。
            private void ReadInitializerEffects(
                IOperation value,
                int createdValueId)
            {
                while (value is IConversionOperation conversion)
                {
                    value = conversion.Operand;
                }

                IObjectOrCollectionInitializerOperation? initializer = value switch
                {
                    IObjectCreationOperation creation => creation.Initializer,
                    ITypeParameterObjectCreationOperation creation => creation.Initializer,
                    _ => null,
                };
                if (initializer == null
                    || !this.m_recordedInitializers.Add(initializer))
                {
                    return;
                }

                this.m_implicitReceivers.Push(createdValueId);
                Visit(initializer);
                this.m_implicitReceivers.Pop();
            }

            // 记录数组创建及初始化器中的每一个元素写入。
            public override void VisitArrayCreation(IArrayCreationOperation operation)
            {
                ReadArrayCreation(operation, operation);
            }

            // 把事件订阅或退订转换为对应事件访问器调用。
            public override void VisitEventAssignment(IEventAssignmentOperation operation)
            {
                IEventReferenceOperation eventReference = operation.EventReference
                    as IEventReferenceOperation
                    ?? throw new AnalysisException(
                        $"事件赋值缺少事件引用：{this.m_method.Id} @ {operation.Syntax.SpanStart}");
                IEventSymbol eventSymbol = eventReference.Event;
                IMethodSymbol accessor = (operation.Adds
                        ? eventSymbol.AddMethod
                        : eventSymbol.RemoveMethod)
                    ?? throw new AnalysisException(
                        $"事件缺少访问器：{this.m_method.Id} @ {operation.Syntax.SpanStart}");
                this.m_calls.Add(CreateCall(
                                    ReadCallKind(accessor),
                                    ReadMethodReference(accessor),
                                    accessor.IsStatic ? null : ReadValue(eventReference.Instance!),
                                    new[]
                                    {
                        new BehaviorArgument(
                            ReadValue(operation.HandlerValue),
                            CatalogRefKind.None),
                                    },
                                    null,
                                    operation.Syntax.SpanStart));
                base.VisitEventAssignment(operation);
            }

            // 将条件访问中的占位接收者连接回问号左侧的真实对象。
            public override void VisitConditionalAccess(IConditionalAccessOperation operation)
            {
                if (operation.Type?.SpecialType == SpecialType.System_Void)
                {
                    int receiverValueId = ReadValue(operation.Operation);
                    this.m_conditionalReceivers.Push(receiverValueId);
                    Visit(operation.WhenNotNull);
                    this.m_conditionalReceivers.Pop();

                    return;
                }

                ReadConditionalAccessValue(operation);
            }

            // 保证每次属性读取都产生一次 getter 调用事实。
            public override void VisitPropertyReference(IPropertyReferenceOperation operation)
            {
                if (!this.m_writeTargets.Contains(operation))
                {
                    ReadPropertyValue(operation, operation);
                }

                base.VisitPropertyReference(operation);
            }

            // 记录用户自定义二元运算符调用。
            public override void VisitBinaryOperator(IBinaryOperation operation)
            {
                ReadBinaryValue(operation);
            }

            // 记录用户自定义一元运算符调用。
            public override void VisitUnaryOperator(IUnaryOperation operation)
            {
                ReadUnaryValue(operation);
            }

            // 记录条件语句中的模式输入和声明变量来源。
            public override void VisitIsPattern(IIsPatternOperation operation)
            {
                ReadPatternValue(operation);
            }

            // 记录返回值与函数出口的关系。
            public override void VisitReturn(IReturnOperation operation)
            {
                if (operation.ReturnedValue is IThrowOperation throwOperation)
                {
                    VisitThrow(throwOperation);

                    return;
                }

                this.m_returns.Add(CreateReturn(
                    operation.ReturnedValue == null ? null : ReadValue(operation.ReturnedValue),
                    operation.Syntax.SpanStart));
                base.VisitReturn(operation);
            }

            // 外层函数只保留局部函数定义或绑定，不读取其独立函数体。
            public override void VisitLocalFunction(ILocalFunctionOperation operation)
            {
            }

            // 外层函数只保留匿名函数绑定，不读取其独立函数体。
            public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
            {
            }

            // 只遍历已经明确支持的源码容器和表达式，未知节点立即报告位置。
            public override void DefaultVisit(IOperation operation)
            {
                if (!IsSupportedSourceOperation(operation))
                {
                    throw new AnalysisException(
                        $"源码函数包含尚未支持的操作：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart} {operation.Kind}");
                }

                base.DefaultVisit(operation);
            }

            // 明确列出当前行为读取器已经理解或只负责承载子节点的操作。
            private static bool IsSupportedSourceOperation(IOperation operation)
            {
                return operation is IBlockOperation
                    or IExpressionStatementOperation
                    or IVariableDeclarationGroupOperation
                    or IVariableDeclarationOperation
                    or IVariableDeclaratorOperation
                    or IVariableInitializerOperation
                    or IArgumentOperation
                    or IFlowCaptureOperation
                    or IFlowCaptureReferenceOperation
                    or IFlowAnonymousFunctionOperation
                    or IUsingDeclarationOperation
                    or IUsingOperation
                    or ILockOperation
                    or IForEachLoopOperation
                    or IForLoopOperation
                    or IWhileLoopOperation
                    or IBranchOperation
                    or ILabeledOperation
                    or IEmptyOperation
                    or IReturnOperation
                    or IThrowOperation
                    or ITryOperation
                    or ICatchClauseOperation
                    or IObjectOrCollectionInitializerOperation
                    or IMemberInitializerOperation
                    or IArrayInitializerOperation
                    or IInterpolationOperation
                    or IInterpolatedStringTextOperation
                    or IInstanceReferenceOperation
                    or IConditionalAccessInstanceOperation
                    or IParameterReferenceOperation
                    or ILocalReferenceOperation
                    or ICaughtExceptionOperation
                    or ILiteralOperation
                    or IFieldReferenceOperation
                    or IEventReferenceOperation
                    or IArrayElementReferenceOperation
                    or IAddressOfOperation
                    or IConversionOperation
                    or IBinaryOperation
                    or IUnaryOperation
                    or IInvocationOperation
                    or IObjectCreationOperation
                    or ITypeParameterObjectCreationOperation
                    or IArrayCreationOperation
                    or ITypeOfOperation
                    or IMethodReferenceOperation
                    or IDelegateCreationOperation
                    or IDefaultValueOperation
                    or IPropertyReferenceOperation
                    or IParenthesizedOperation
                    or ICoalesceOperation
                    or ICoalesceAssignmentOperation
                    or ISimpleAssignmentOperation
                    or IDeconstructionAssignmentOperation
                    or ICompoundAssignmentOperation
                    or IIncrementOrDecrementOperation
                    or IConditionalAccessOperation
                    or IConditionalOperation
                    or IIsTypeOperation
                    or IIsPatternOperation
                    or IInterpolatedStringOperation
                    or ITupleOperation
                    or IEventAssignmentOperation
                    or IDeclarationExpressionOperation
                    or IDiscardOperation
                    or ILocalFunctionOperation
                    or IAnonymousFunctionOperation
                    || operation.Kind == OperationKind.IsNull
                    || IsPointerIndirection(operation);
            }

            // 将赋值目标归为局部关系、字段、数组、属性或引用位置。
            private void RecordWrite(IOperation target, int valueId, int position)
            {
                switch (target)
                {
                    case ILocalReferenceOperation local:
                        this.m_assignments.Add(CreateAssignment(
                            GetLocalValueId(local.Local),
                            valueId,
                            position));
                        break;
                    case IFlowCaptureReferenceOperation capture:
                        IOperation captured = this.m_captureOperations[capture.Id];
                        if (captured is IFieldReferenceOperation
                            or IPropertyReferenceOperation
                            or IArrayElementReferenceOperation)
                        {
                            RecordWrite(captured, valueId, position);
                        }
                        else
                        {
                            this.m_assignments.Add(CreateAssignment(
                                GetCaptureValueId(capture.Id),
                                valueId,
                                position));
                        }

                        break;
                    case IParameterReferenceOperation parameter:
                        if (parameter.Parameter.RefKind is RefKind.Ref or RefKind.Out)
                        {
                            this.m_writes.Add(CreateWrite(
                                BehaviorWriteKind.Indirect,
                                GetParameterValueId(parameter.Parameter),
                                null,
                                Array.Empty<int>(),
                                valueId,
                                position));
                        }
                        else
                        {
                            this.m_assignments.Add(CreateAssignment(
                                GetParameterValueId(parameter.Parameter),
                                valueId,
                                position));
                        }

                        break;
                    case IInstanceReferenceOperation:
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.Indirect,
                            GetCurrentInstanceValueId(),
                            null,
                            Array.Empty<int>(),
                            valueId,
                            position));
                        break;
                    case IFieldReferenceOperation field:
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.Field,
                            field.Field.IsStatic ? null : ReadValue(field.Instance!),
                            ReadFieldReference(field.Field),
                            Array.Empty<int>(),
                            valueId,
                            position));
                        break;
                    case IArrayElementReferenceOperation element:
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.ArrayElement,
                            ReadValue(element.ArrayReference),
                            null,
                            element.Indices.Select(ReadValue).ToArray(),
                            valueId,
                            position));
                        break;
                    case var pointer when IsPointerIndirection(pointer):
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.Indirect,
                            ReadValue(pointer.ChildOperations.Single()),
                            null,
                            Array.Empty<int>(),
                            valueId,
                            position));
                        break;
                    case IPropertyReferenceOperation property:
                        RecordPropertyWrite(property, valueId, position);
                        break;
                    case IEventReferenceOperation eventReference:
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.Field,
                            eventReference.Event.IsStatic
                                ? null
                                : ReadValue(eventReference.Instance!),
                            ReadEventReference(eventReference.Event),
                            Array.Empty<int>(),
                            valueId,
                            position));
                        break;
                    case IDiscardOperation:
                        break;
                    default:
                        throw new AnalysisException(
                            $"源码函数包含尚未读取的赋值目标：{this.m_method.Id} "
                                + $"@ {position} {target.Kind}");
                }
            }

            // 按元组位置递归展开解构目标和值。
            private void RecordDeconstruction(IOperation target, IOperation value, int position)
            {
                IOperation unwrappedValue = value is IConversionOperation conversion
                    ? conversion.Operand
                    : value;
                if (target is ITupleOperation targetTuple
                    && unwrappedValue is ITupleOperation valueTuple)
                {
                    if (targetTuple.Elements.Length != valueTuple.Elements.Length)
                    {
                        throw new AnalysisException(
                            $"解构目标和值数量不同：{this.m_method.Id} @ {position}");
                    }

                    for (int index = 0; index < targetTuple.Elements.Length; index++)
                    {
                        RecordDeconstruction(
                            targetTuple.Elements[index],
                            valueTuple.Elements[index],
                            position);
                    }

                    return;
                }

                this.m_writeTargets.Add(target);
                RecordWrite(target, ReadValue(value), position);
            }

            // 把属性赋值转换为属性写入函数的普通调用事实。
            private void RecordPropertyWrite(
                IPropertyReferenceOperation property,
                int valueId,
                int position)
            {
                IMethodSymbol? setter = property.Property.SetMethod;
                if (setter == null)
                {
                    this.m_writes.Add(CreateWrite(
                        BehaviorWriteKind.Field,
                        property.Property.IsStatic ? null : ReadValue(property.Instance!),
                        BehaviorMemberReference.From(
                            this.m_catalog.ReadSourcePropertyStorageReference(
                                property.Property,
                                this.m_method.AssemblyName)),
                        Array.Empty<int>(),
                        valueId,
                        position));

                    return;
                }

                BehaviorArgument[] arguments = property.Arguments
                    .Select(argument => new BehaviorArgument(
                        ReadValue(argument.Value),
                        ReadRefKind(argument.Parameter?.RefKind ?? RefKind.None)))
                    .Append(new BehaviorArgument(valueId, CatalogRefKind.None))
                    .ToArray();
                this.m_calls.Add(CreateCall(
                    ReadCallKind(setter),
                    ReadMethodReference(setter),
                    setter.IsStatic ? null : ReadValue(property.Instance!),
                    arguments,
                    null,
                    position));
            }

            // 记录一次源码调用并返回非 void 调用的结果值编号。
            private int? RecordInvocation(IInvocationOperation operation)
            {
                if (this.m_recordedCalls.Contains(operation))
                {
                    return this.m_valueIds.GetValueOrDefault(operation);
                }

                if (TryReadArrayForEachCompilerCall(operation, out int? arrayResultValueId))
                {
                    this.m_recordedCalls.Add(operation);
                    return arrayResultValueId;
                }

                IMethodSymbol target = operation.TargetMethod;
                bool isReducedExtension = target.ReducedFrom != null;
                List<BehaviorArgument> arguments = new();
                int? receiverValueId = null;
                if (isReducedExtension)
                {
                    arguments.Add(new BehaviorArgument(
                        ReadValue(operation.Instance!),
                        ReadRefKind(target.ReducedFrom!.Parameters[0].RefKind)));
                }
                else if (!target.IsStatic)
                {
                    receiverValueId = operation.Instance == null
                        ? GetCurrentInstanceValueId()
                        : ReadValue(operation.Instance);
                }

                arguments.AddRange(operation.Arguments
                    .OrderBy(argument => argument.Parameter?.Ordinal ?? int.MaxValue)
                    .Select(argument => new BehaviorArgument(
                        argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out or RefKind.In
                            ? ReadAddress(argument.Value)
                            : ReadValue(argument.Value),
                        ReadRefKind(argument.Parameter?.RefKind ?? RefKind.None))));
                int? resultValueId = target.ReturnsVoid
                    ? null
                    : AddOperationValue(
                        operation,
                        BehaviorValueKind.CallResult,
                        target.MetadataName,
                        arguments.Select(argument => argument.ValueId).ToArray());
                this.m_calls.Add(CreateCall(
                    ReadCallKind(operation),
                    ReadMethodReference(target),
                    receiverValueId,
                    arguments,
                    resultValueId,
                    operation.Syntax.SpanStart,
                    ReadConstrainedReceiverType(operation)));
                this.m_recordedCalls.Add(operation);

                return resultValueId;
            }

            // 读取会生成 constrained 调用前缀的实际值类型接收者。
            private BehaviorTypeReference? ReadConstrainedReceiverType(
                IInvocationOperation operation)
            {
                ITypeSymbol? type = operation.ConstrainedToType;
                if (type == null && !operation.IsImplicit)
                {
                    return null;
                }

                IOperation? receiver = operation.Instance;
                while (type == null && receiver != null)
                {
                    if (receiver is IConversionOperation conversion && conversion.IsImplicit)
                    {
                        receiver = conversion.Operand;
                        continue;
                    }

                    if (receiver is IFlowCaptureReferenceOperation capture
                        && this.m_captureOperations.TryGetValue(capture.Id, out IOperation? captured))
                    {
                        receiver = captured;
                        continue;
                    }

                    type = receiver.Type;
                    break;
                }

                if (!operation.IsVirtual
                    || type == null
                    || (!type.IsValueType && type.TypeKind != TypeKind.TypeParameter))
                {
                    return null;
                }

                return ReadTypeReference(type);
            }

            // 把控制流图为数组 foreach 伪造的枚举器调用还原成数组读取。
            private bool TryReadArrayForEachCompilerCall(
                IInvocationOperation operation,
                out int? resultValueId)
            {
                if (!IsArrayForEachCompilerOperation(operation))
                {
                    resultValueId = null;
                    return false;
                }

                List<int> inputs = operation.Arguments
                    .Select(argument => ReadValue(argument.Value))
                    .ToList();
                if (operation.Instance != null)
                {
                    inputs.Insert(0, ReadValue(operation.Instance));
                }
                resultValueId = operation.TargetMethod.ReturnsVoid
                    ? null
                    : AddOperationValue(
                        operation,
                        operation.TargetMethod.MethodKind == MethodKind.PropertyGet
                            ? BehaviorValueKind.ArrayElementRead
                            : BehaviorValueKind.Computation,
                        $"array-foreach:{operation.TargetMethod.MetadataName}",
                        inputs);
                return true;
            }

            // 判断隐式操作是否来自数组 foreach 的抽象枚举器模型。
            private bool IsArrayForEachCompilerOperation(IOperation operation)
            {
                CommonForEachStatementSyntax? loop = operation.IsImplicit
                    ? operation.Syntax.FirstAncestorOrSelf<CommonForEachStatementSyntax>()
                    : null;
                return loop != null
                    && this.m_compilation.GetSemanticModel(loop.SyntaxTree)
                        .GetTypeInfo(loop.Expression, this.m_cancellationToken).Type
                        is IArrayTypeSymbol;
            }

            // 按调用节点的真实分派方式区分直接、可重写和委托调用。
            private static BehaviorCallKind ReadCallKind(IInvocationOperation operation)
            {
                return operation.TargetMethod.MethodKind == MethodKind.DelegateInvoke
                    ? BehaviorCallKind.Delegate
                    : operation.IsVirtual
                        ? BehaviorCallKind.Virtual
                        : BehaviorCallKind.Direct;
            }

            // 根据源码函数符号区分普通、可重写、接口和委托调用。
            private static BehaviorCallKind ReadCallKind(IMethodSymbol target)
            {
                if (target.MethodKind == MethodKind.DelegateInvoke)
                {
                    return BehaviorCallKind.Delegate;
                }

                return target.IsVirtual
                    || target.IsAbstract
                    || target.ContainingType.TypeKind == TypeKind.Interface
                        ? BehaviorCallKind.Virtual
                        : BehaviorCallKind.Direct;
            }

            // 将用户自定义运算符记录成普通函数调用并返回调用结果。
            private int RecordOperator(
                IOperation operation,
                IMethodSymbol target,
                IReadOnlyList<int> inputValueIds)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                int resultValueId = AddOperationValue(
                    operation,
                    BehaviorValueKind.CallResult,
                    target.MetadataName,
                    inputValueIds);
                this.m_calls.Add(CreateCall(
                    BehaviorCallKind.Direct,
                    ReadMethodReference(target),
                    null,
                    inputValueIds.Select(valueId => new BehaviorArgument(
                        valueId,
                        CatalogRefKind.None)).ToArray(),
                    resultValueId,
                    operation.Syntax.SpanStart));

                return resultValueId;
            }

            // 记录一次对象创建及其构造函数调用。
            private int RecordObjectCreation(IObjectCreationOperation operation)
            {
                if (this.m_recordedCalls.Contains(operation))
                {
                    return this.m_valueIds[operation];
                }

                IMethodSymbol constructor = operation.Constructor
                    ?? throw new AnalysisException(
                        $"源码对象创建没有构造函数：{this.m_method.Id} @ {operation.Syntax.SpanStart}");
                BehaviorArgument[] arguments = operation.Arguments
                    .OrderBy(argument => argument.Parameter?.Ordinal ?? int.MaxValue)
                    .Select(argument => new BehaviorArgument(
                        argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out or RefKind.In
                            ? ReadAddress(argument.Value)
                            : ReadValue(argument.Value),
                        ReadRefKind(argument.Parameter?.RefKind ?? RefKind.None)))
                    .ToArray();
                int resultValueId = AddOperationValue(
                    operation,
                    BehaviorValueKind.NewObject,
                    operation.Type?.ToDisplayString(),
                    arguments.Select(argument => argument.ValueId).ToArray());
                this.m_values[resultValueId] = this.m_values[resultValueId] with
                {
                    Type = operation.Type == null ? null : ReadTypeReference(operation.Type),
                };
                this.m_calls.Add(CreateCall(
                    BehaviorCallKind.ObjectCreation,
                    ReadMethodReference(constructor),
                    null,
                    arguments,
                    resultValueId,
                    operation.Syntax.SpanStart));
                this.m_recordedCalls.Add(operation);

                return resultValueId;
            }

            // 追加一个没有独立 Roslyn 调用节点的编译器隐含调用。
            private int? RecordSyntheticCall(
                IMethodSymbol target,
                int? receiverValueId,
                IReadOnlyList<BehaviorArgument> arguments,
                int position)
            {
                int? resultValueId = target.ReturnsVoid
                    ? null
                    : AddValue(
                        BehaviorValueKind.CallResult,
                        target.MetadataName,
                        null,
                        arguments.Select(argument => argument.ValueId).ToArray());
                this.m_calls.Add(CreateCall(
                    ReadCallKind(target),
                    ReadMethodReference(target),
                    target.IsStatic ? null : receiverValueId,
                    arguments,
                    resultValueId,
                    position));

                return resultValueId;
            }

            // 读取 ref/out/in 实参所指向的准确位置。
            private int ReadAddress(IOperation operation)
            {
                if (operation is IDeclarationExpressionOperation declaration)
                {
                    return ReadAddress(declaration.Expression);
                }

                if (operation is ILocalReferenceOperation local)
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.Address,
                        local.Local.Name,
                        new[] { GetLocalValueId(local.Local) });
                }

                if (operation is IParameterReferenceOperation parameter)
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.Address,
                        parameter.Parameter.Name,
                        new[] { GetParameterValueId(parameter.Parameter) });
                }

                if (operation is IInstanceReferenceOperation)
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.Address,
                        "this",
                        new[] { GetCurrentInstanceValueId() });
                }

                if (operation is IDiscardOperation)
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.Address,
                        "discard",
                        Array.Empty<int>());
                }

                if (operation is IFieldReferenceOperation field)
                {
                    int id = AddOperationValue(
                        operation,
                        BehaviorValueKind.Address,
                        field.Field.MetadataName,
                        field.Field.IsStatic
                            ? Array.Empty<int>()
                            : new[] { ReadValue(field.Instance!) });
                    this.m_values[id] = this.m_values[id] with
                    {
                        Member = ReadFieldReference(field.Field),
                    };

                    return id;
                }

                if (operation is IArrayElementReferenceOperation element)
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.Address,
                        "array-element",
                        element.Indices.Prepend(element.ArrayReference).Select(ReadValue).ToArray());
                }

                if (operation is IConversionOperation conversion)
                {
                    return ReadAddress(conversion.Operand);
                }

                throw new AnalysisException(
                    $"源码函数包含尚未读取的引用实参：{this.m_method.Id} "
                        + $"@ {operation.Syntax.SpanStart} {operation.Kind}");
            }

            // 将一个源码表达式转换为可被后续模块追踪的值来源。
            private int ReadValue(IOperation operation)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existing))
                {
                    return existing;
                }

                return operation switch
                {
                    IInstanceReferenceOperation instance => ReadInstanceValue(instance),
                    IConditionalAccessInstanceOperation =>
                        this.m_conditionalReceivers.TryPeek(out int conditionalReceiver)
                            ? conditionalReceiver
                            : throw new AnalysisException(
                                $"条件访问接收者不在条件访问中：{this.m_method.Id} "
                                    + $"@ {operation.Syntax.SpanStart}"),
                    IParameterReferenceOperation parameter =>
                        ReadSlotValue(
                            operation,
                            GetParameterValueId(parameter.Parameter),
                            parameter.Parameter.Name),
                    ILocalReferenceOperation local => ReadSlotValue(
                        operation,
                        GetLocalValueId(local.Local),
                        local.Local.Name),
                    IFlowCaptureReferenceOperation capture => ReadSlotValue(
                        operation,
                        GetCaptureValueId(capture.Id),
                        $"capture:{capture.Id.GetHashCode().ToString()}"),
                    ICaughtExceptionOperation => AddOperationValue(
                        operation,
                        BehaviorValueKind.Exception,
                        operation.Type?.ToDisplayString(),
                        Array.Empty<int>()),
                    ILiteralOperation literal => AddOperationValue(
                        operation,
                        BehaviorValueKind.Constant,
                        literal.ConstantValue.HasValue
                            ? Convert.ToString(
                                literal.ConstantValue.Value,
                                System.Globalization.CultureInfo.InvariantCulture)
                            : null,
                        Array.Empty<int>()),
                    IFieldReferenceOperation field => ReadFieldValue(operation, field),
                    IEventReferenceOperation eventReference =>
                        ReadEventValue(operation, eventReference),
                    IArrayElementReferenceOperation element => AddOperationValue(
                        operation,
                        BehaviorValueKind.ArrayElementRead,
                        null,
                        element.Indices.Prepend(element.ArrayReference).Select(ReadValue).ToArray()),
                    IAddressOfOperation address => ReadAddress(address.Reference),
                    var pointer when IsPointerIndirection(pointer) => AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        "pointer-read",
                        new[] { ReadValue(pointer.ChildOperations.Single()) }),
                    IConversionOperation conversion => ReadConversionValue(operation, conversion),
                    IBinaryOperation binary => ReadBinaryValue(binary),
                    IUnaryOperation unary => ReadUnaryValue(unary),
                    IInvocationOperation invocation => RecordInvocation(invocation)
                        ?? throw new AnalysisException(
                            $"void 调用不能作为值使用：{this.m_method.Id} @ {operation.Syntax.SpanStart}"),
                    IObjectCreationOperation creation => RecordObjectCreation(creation),
                    ITypeParameterObjectCreationOperation creation =>
                        ReadTypeParameterObjectCreation(creation),
                    IArrayCreationOperation array => ReadArrayCreation(operation, array),
                    ITypeOfOperation typeOf => ReadTypeValue(operation, typeOf.TypeOperand),
                    IMethodReferenceOperation method => ReadMethodValue(operation, method),
                    IDelegateCreationOperation delegateCreation =>
                        RecordDelegateCreation(delegateCreation),
                    IDefaultValueOperation defaultValue => AddOperationValue(
                        operation,
                        BehaviorValueKind.Constant,
                        $"default:{defaultValue.Type?.ToDisplayString()}",
                        Array.Empty<int>()),
                    IThrowOperation throwOperation => ReadThrowValue(operation, throwOperation),
                    IPropertyReferenceOperation property => ReadPropertyValue(operation, property),
                    IParenthesizedOperation parenthesized => ReadValue(parenthesized.Operand),
                    ICoalesceOperation coalesce => ReadCoalesceValue(operation, coalesce),
                    ICoalesceAssignmentOperation assignment =>
                        ReadCoalesceAssignmentValue(assignment),
                    ISimpleAssignmentOperation assignment =>
                        ReadSimpleAssignmentValue(assignment),
                    ICompoundAssignmentOperation assignment =>
                        ReadCompoundAssignmentValue(assignment),
                    IIncrementOrDecrementOperation increment =>
                        ReadIncrementValue(increment),
                    IConditionalAccessOperation conditionalAccess =>
                        ReadConditionalAccessValue(conditionalAccess),
                    IConditionalOperation conditional => AddOperationValue(
                        operation,
                        BehaviorValueKind.Merge,
                        null,
                        new[]
                        {
                            ReadValue(conditional.WhenTrue),
                                ReadValue(conditional.WhenFalse
                                ?? throw new AnalysisException(
                                    $"条件值缺少第二条分支：{this.m_method.Id} "
                                        + $"@ {operation.Syntax.SpanStart}")),
                        }),
                    IIsTypeOperation isType => AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        $"is:{isType.TypeOperand.ToDisplayString()}",
                        new[] { ReadValue(isType.ValueOperand) }),
                    var isNull when isNull.Kind == OperationKind.IsNull => AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        "is-null",
                        isNull.ChildOperations.Select(ReadValue).ToArray()),
                    IIsPatternOperation isPattern => ReadPatternValue(isPattern),
                    IInterpolatedStringOperation interpolated =>
                        ReadInterpolatedStringValue(interpolated),
                    ITupleOperation tuple => ReadTupleValue(tuple),
                    _ => throw new AnalysisException(
                        $"源码函数包含尚未读取的值：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart} {operation.Kind}"),
                };
            }

            // 记录 new T() 的泛型类型身份，交给后续按实际类型闭合构造函数。
            private int ReadTypeParameterObjectCreation(
                ITypeParameterObjectCreationOperation operation)
            {
                ITypeSymbol type = operation.Type
                    ?? throw new AnalysisException(
                        $"泛型对象创建缺少类型：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart}");
                int valueId = AddOperationValue(
                    operation,
                    BehaviorValueKind.NewObject,
                    type.ToDisplayString(),
                    Array.Empty<int>());
                this.m_values[valueId] = this.m_values[valueId] with
                {
                    Type = ReadTypeReference(type),
                };

                return valueId;
            }

            // 将元组的每个元素合并成带结构化类型的值来源。
            private int ReadTupleValue(ITupleOperation operation)
            {
                int valueId = AddOperationValue(
                    operation,
                    BehaviorValueKind.Computation,
                    "tuple",
                    operation.Elements.Select(ReadValue).ToArray());
                if (operation.Type != null)
                {
                    this.m_values[valueId] = this.m_values[valueId] with
                    {
                        Type = ReadTypeReference(operation.Type),
                    };
                }

                return valueId;
            }

            // 将插值字符串的文本、表达式、对齐和格式来源合并为结果值。
            private int ReadInterpolatedStringValue(IInterpolatedStringOperation operation)
            {
                List<int> inputs = new();
                foreach (IInterpolatedStringContentOperation part in operation.Parts)
                {
                    if (part is IInterpolatedStringTextOperation text)
                    {
                        inputs.Add(ReadValue(text.Text));
                        continue;
                    }

                    if (part is not IInterpolationOperation interpolation)
                    {
                        throw new AnalysisException(
                            $"源码插值字符串包含未知部分：{this.m_method.Id} "
                                + $"@ {part.Syntax.SpanStart} {part.Kind}");
                    }

                    inputs.Add(ReadValue(interpolation.Expression));
                    if (interpolation.Alignment != null)
                    {
                        inputs.Add(ReadValue(interpolation.Alignment));
                    }

                    if (interpolation.FormatString != null)
                    {
                        inputs.Add(ReadValue(interpolation.FormatString));
                    }
                }

                return AddOperationValue(
                    operation,
                    BehaviorValueKind.Computation,
                    "interpolated-string",
                    inputs);
            }

            // 为控制流图临时槽建立可复用的局部值。
            private int GetCaptureValueId(CaptureId captureId)
            {
                if (this.m_captureValueIds.TryGetValue(captureId, out int valueId))
                {
                    return valueId;
                }

                valueId = AddRootValue(
                    BehaviorValueKind.Local,
                    $"capture:{captureId.GetHashCode().ToString()}",
                    null);
                this.m_captureValueIds.Add(captureId, valueId);

                return valueId;
            }

            // 记录简单赋值并将右侧值作为整个表达式的结果。
            private int ReadSimpleAssignmentValue(ISimpleAssignmentOperation operation)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                int valueId = ReadValue(operation.Value);
                this.m_writeTargets.Add(operation.Target);
                RecordWrite(operation.Target, valueId, operation.Syntax.SpanStart);
                this.m_valueIds.Add(operation, valueId);

                return valueId;
            }

            // 记录复合赋值并返回运算后的值。
            private int ReadCompoundAssignmentValue(ICompoundAssignmentOperation operation)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                int[] inputs = { ReadValue(operation.Target), ReadValue(operation.Value) };
                int valueId = operation.OperatorMethod == null
                    ? AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        operation.OperatorKind.ToString(),
                        inputs)
                    : RecordOperator(operation, operation.OperatorMethod, inputs);
                this.m_writeTargets.Add(operation.Target);
                RecordWrite(operation.Target, valueId, operation.Syntax.SpanStart);

                return valueId;
            }

            // 记录递增或递减并返回表达式值。
            private int ReadIncrementValue(IIncrementOrDecrementOperation operation)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                int[] inputs = { ReadValue(operation.Target) };
                int valueId = operation.OperatorMethod == null
                    ? AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        operation.Kind.ToString(),
                        inputs)
                    : RecordOperator(operation, operation.OperatorMethod, inputs);
                this.m_writeTargets.Add(operation.Target);
                RecordWrite(operation.Target, valueId, operation.Syntax.SpanStart);

                return valueId;
            }

            // 读取类型、常量和组合模式，并保留声明变量的值来源。
            private int ReadPatternValue(IIsPatternOperation operation)
            {
                int inputValueId = ReadValue(operation.Value);
                List<int> inputs = new() { inputValueId };

                ReadPattern(operation.Pattern, inputValueId, inputs);

                return AddOperationValue(
                    operation,
                    BehaviorValueKind.Computation,
                    $"pattern:{operation.Pattern.Kind}",
                    inputs);
            }

            // 递归读取一种明确的模式写法。
            private void ReadPattern(IPatternOperation pattern, int inputValueId, ICollection<int> inputs)
            {
                switch (pattern)
                {
                    case IDeclarationPatternOperation declaration:
                        if (declaration.DeclaredSymbol is ILocalSymbol local)
                        {
                            this.m_assignments.Add(CreateAssignment(
                                GetLocalValueId(local),
                                inputValueId,
                                declaration.Syntax.SpanStart));
                        }

                        break;
                    case ITypePatternOperation:
                        break;
                    case IConstantPatternOperation constant:
                        inputs.Add(ReadValue(constant.Value));
                        break;
                    case IRelationalPatternOperation relational:
                        inputs.Add(ReadValue(relational.Value));
                        break;
                    case INegatedPatternOperation negated:
                        ReadPattern(negated.Pattern, inputValueId, inputs);
                        break;
                    case IBinaryPatternOperation binary:
                        ReadPattern(binary.LeftPattern, inputValueId, inputs);
                        ReadPattern(binary.RightPattern, inputValueId, inputs);
                        break;
                    case IRecursivePatternOperation recursive:
                        ReadRecursivePattern(recursive, inputValueId, inputs);
                        break;
                    default:
                        throw new AnalysisException(
                            $"源码函数包含尚未读取的模式：{this.m_method.Id} "
                                + $"@ {pattern.Syntax.SpanStart} {pattern.Kind}");
                }
            }

            // 读取递归模式声明、属性 getter 和位置解构调用。
            private void ReadRecursivePattern(
                IRecursivePatternOperation pattern,
                int inputValueId,
                ICollection<int> inputs)
            {
                if (pattern.DeclaredSymbol is ILocalSymbol local)
                {
                    this.m_assignments.Add(CreateAssignment(
                        GetLocalValueId(local),
                        inputValueId,
                        pattern.Syntax.SpanStart));
                }

                foreach (IPropertySubpatternOperation property in pattern.PropertySubpatterns)
                {
                    this.m_implicitReceivers.Push(inputValueId);
                    int memberValueId = ReadValue(property.Member);
                    this.m_implicitReceivers.Pop();
                    inputs.Add(memberValueId);
                    ReadPattern(property.Pattern, memberValueId, inputs);
                }

                IMethodSymbol? deconstruct = pattern.DeconstructSymbol as IMethodSymbol;
                if (deconstruct == null && pattern.DeconstructionSubpatterns.Length > 0)
                {
                    throw new AnalysisException(
                        $"位置模式没有解构函数：{this.m_method.Id} "
                            + $"@ {pattern.Syntax.SpanStart}");
                }

                if (deconstruct != null)
                {
                    int[] outputValueIds = pattern.DeconstructionSubpatterns
                        .Select((_, index) => AddRootValue(
                            BehaviorValueKind.Local,
                            $"pattern-output:{index.ToString()}",
                            null))
                        .ToArray();
                    RecordSyntheticCall(
                        deconstruct,
                        inputValueId,
                        outputValueIds.Select(valueId => new BehaviorArgument(
                            AddValue(
                                BehaviorValueKind.Address,
                                "pattern-output",
                                null,
                                new[] { valueId }),
                            CatalogRefKind.Out)).ToArray(),
                        pattern.Syntax.SpanStart);
                    for (int index = 0; index < outputValueIds.Length; index++)
                    {
                        inputs.Add(outputValueIds[index]);
                        ReadPattern(
                            pattern.DeconstructionSubpatterns[index],
                            outputValueIds[index],
                            inputs);
                    }
                }
            }

            // 区分当前对象、对象初始化器接收者和条件访问接收者。
            private int ReadInstanceValue(IInstanceReferenceOperation operation)
            {
                if (operation.ReferenceKind == InstanceReferenceKind.ImplicitReceiver
                    && this.m_implicitReceivers.TryPeek(out int receiver))
                {
                    return receiver;
                }

                if (this.m_method.Kind is CatalogMethodKind.AnonymousFunction
                    or CatalogMethodKind.LocalFunction)
                {
                    this.m_capturedInstanceValueId ??= AddRootValue(
                        BehaviorValueKind.Captured,
                        $"this:{this.m_method.TypeId}",
                        null);
                    return this.m_capturedInstanceValueId.Value;
                }

                return GetCurrentInstanceValueId();
            }

            // 读取可能由用户函数实现的类型转换。
            private int ReadConversionValue(
                IOperation operation,
                IConversionOperation conversion)
            {
                int operandValueId = ReadValue(conversion.Operand);
                int valueId = conversion.OperatorMethod == null
                    ? AddOperationValue(
                        operation,
                        BehaviorValueKind.Conversion,
                        conversion.Type?.ToDisplayString(),
                        new[] { operandValueId })
                    : RecordOperator(
                        operation,
                        conversion.OperatorMethod,
                        new[] { operandValueId });
                if (conversion.Type != null)
                {
                    this.m_values[valueId] = this.m_values[valueId] with
                    {
                        Type = ReadTypeReference(conversion.Type),
                    };
                }

                return valueId;
            }

            // 读取二元计算或其用户自定义运算符调用。
            private int ReadBinaryValue(IBinaryOperation operation)
            {
                int[] inputs = { ReadValue(operation.LeftOperand), ReadValue(operation.RightOperand) };

                return operation.OperatorMethod == null
                    ? AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        operation.OperatorKind.ToString(),
                        inputs)
                    : RecordOperator(operation, operation.OperatorMethod, inputs);
            }

            // 读取一元计算或其用户自定义运算符调用。
            private int ReadUnaryValue(IUnaryOperation operation)
            {
                int[] inputs = { ReadValue(operation.Operand) };

                return operation.OperatorMethod == null
                    ? AddOperationValue(
                        operation,
                        BehaviorValueKind.Computation,
                        operation.OperatorKind.ToString(),
                        inputs)
                    : RecordOperator(operation, operation.OperatorMethod, inputs);
            }

            // 返回空值赋值在保留旧值和新值后的汇合结果。
            private int ReadCoalesceAssignmentValue(ICoalesceAssignmentOperation operation)
            {
                VisitCoalesceAssignment(operation);

                return this.m_valueIds[operation];
            }

            // 将条件访问占位接收者连回问号左侧对象并返回非空分支结果。
            private int ReadConditionalAccessValue(IConditionalAccessOperation operation)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                int receiverValueId = ReadValue(operation.Operation);
                this.m_conditionalReceivers.Push(receiverValueId);
                int resultValueId = ReadValue(operation.WhenNotNull);
                this.m_conditionalReceivers.Pop();

                return AddOperationValue(
                    operation,
                    BehaviorValueKind.Merge,
                    "conditional-access",
                    new[] { resultValueId });
            }

            // 读取空值合并表达式并排除不会产生值的 throw 分支。
            private int ReadCoalesceValue(IOperation operation, ICoalesceOperation coalesce)
            {
                List<int> inputs = new() { ReadValue(coalesce.Value) };
                IOperation whenNull = coalesce.WhenNull;
                while (whenNull is IConversionOperation conversion)
                {
                    whenNull = conversion.Operand;
                }

                if (whenNull is IThrowOperation throwOperation)
                {
                    VisitThrow(throwOperation);
                }
                else
                {
                    inputs.Add(ReadValue(coalesce.WhenNull));
                }

                return AddOperationValue(
                    operation,
                    BehaviorValueKind.Merge,
                    "coalesce",
                    inputs);
            }

            // 记录 throw 表达式中的异常来源并明确标记该分支不会产生返回值。
            private int ReadThrowValue(IOperation operation, IThrowOperation throwOperation)
            {
                int[] inputs = throwOperation.Exception == null
                    ? Array.Empty<int>()
                    : new[] { ReadValue(throwOperation.Exception) };

                return AddOperationValue(
                    operation,
                    BehaviorValueKind.NoReturn,
                    "throw",
                    inputs);
            }

            // 记录源码方法组或匿名函数到委托对象的绑定事实。
            private int RecordDelegateCreation(IDelegateCreationOperation operation)
            {
                if (this.m_recordedCalls.Contains(operation))
                {
                    return this.m_valueIds[operation];
                }

                INamedTypeSymbol delegateType = operation.Type as INamedTypeSymbol
                    ?? throw new AnalysisException(
                        $"委托创建缺少委托类型：{this.m_method.Id} @ {operation.Syntax.SpanStart}");
                IMethodSymbol constructor = delegateType.InstanceConstructors.Single(method =>
                    method.Parameters.Length == 2);
                int functionValueId;
                int receiverValueId;
                if (operation.Target is IMethodReferenceOperation method)
                {
                    functionValueId = ReadMethodValue(method, method);
                    receiverValueId = method.Instance == null
                        ? AddValue(
                            BehaviorValueKind.Constant,
                            null,
                            null,
                            Array.Empty<int>())
                        : ReadValue(method.Instance);
                }
                else if (operation.Target is IAnonymousFunctionOperation anonymous)
                {
                    BehaviorCaptureBinding[] captures = ReadFunctionCaptureSources(
                        anonymous.Symbol,
                        anonymous.Body);
                    functionValueId = AddOperationValue(
                        anonymous,
                        BehaviorValueKind.Function,
                        anonymous.Symbol.MetadataName,
                        captures.Select(capture => capture.ValueId).ToArray());
                    this.m_values[functionValueId] = this.m_values[functionValueId] with
                    {
                        Captures = captures,
                        Method = ReadMethodReference(anonymous.Symbol),
                    };
                    receiverValueId = AddValue(
                        BehaviorValueKind.Constant,
                        null,
                        null,
                        Array.Empty<int>());
                }
                else if (operation.Target is IFlowAnonymousFunctionOperation flowAnonymous)
                {
                    BehaviorCaptureBinding[] captures = ReadAnonymousCaptureSources(flowAnonymous);
                    functionValueId = AddOperationValue(
                        flowAnonymous,
                        BehaviorValueKind.Function,
                        flowAnonymous.Symbol.MetadataName,
                        captures.Select(capture => capture.ValueId).ToArray());
                    this.m_values[functionValueId] = this.m_values[functionValueId] with
                    {
                        Captures = captures,
                        Method = ReadMethodReference(flowAnonymous.Symbol),
                    };
                    receiverValueId = AddValue(
                        BehaviorValueKind.Constant,
                        null,
                        null,
                        Array.Empty<int>());
                }
                else
                {
                    throw new AnalysisException(
                        $"委托创建目标不是函数：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart} {operation.Target.Kind}");
                }

                int resultValueId = AddOperationValue(
                    operation,
                    BehaviorValueKind.NewObject,
                    delegateType.ToDisplayString(),
                    new[] { receiverValueId, functionValueId });
                this.m_values[resultValueId] = this.m_values[resultValueId] with
                {
                    Type = ReadTypeReference(delegateType),
                };
                this.m_calls.Add(CreateCall(
                    BehaviorCallKind.ObjectCreation,
                    ReadMethodReference(constructor),
                    null,
                    new[]
                    {
                        new BehaviorArgument(receiverValueId, CatalogRefKind.None),
                        new BehaviorArgument(functionValueId, CatalogRefKind.None),
                    },
                    resultValueId,
                    operation.Syntax.SpanStart));
                this.m_recordedCalls.Add(operation);

                return resultValueId;
            }

            // 按稳定捕获键收集控制流匿名函数引用的外层值。
            private BehaviorCaptureBinding[] ReadAnonymousCaptureSources(
                IFlowAnonymousFunctionOperation operation)
            {
                SemanticModel model = this.m_compilation.GetSemanticModel(operation.Syntax.SyntaxTree);
                IAnonymousFunctionOperation anonymous = model.GetOperation(
                        operation.Syntax,
                        this.m_cancellationToken) as IAnonymousFunctionOperation
                    ?? throw new AnalysisException(
                        $"控制流匿名函数没有原始操作树：{this.m_method.Id} "
                            + $"@ {operation.Syntax.SpanStart}");
                return ReadFunctionCaptureSources(anonymous.Symbol, anonymous.Body);
            }

            // 按稳定键收集内部函数引用的所有外层值。
            private BehaviorCaptureBinding[] ReadFunctionCaptureSources(
                IMethodSymbol function,
                IOperation body)
            {
                Dictionary<string, int> captures = new(StringComparer.Ordinal);
                foreach (IOperation descendant in EnumerateOperations(body))
                {
                    if (descendant is IParameterReferenceOperation parameter
                        && !SymbolEqualityComparer.Default.Equals(
                            parameter.Parameter.ContainingSymbol,
                            function))
                    {
                        captures.TryAdd(
                            ReadCaptureKey(parameter.Parameter),
                            GetParameterValueId(parameter.Parameter));
                    }
                    else if (descendant is ILocalReferenceOperation local
                        && !SymbolEqualityComparer.Default.Equals(
                            local.Local.ContainingSymbol,
                            function))
                    {
                        captures.TryAdd(
                            ReadCaptureKey(local.Local),
                            GetLocalValueId(local.Local));
                    }
                    else if (descendant is IInstanceReferenceOperation instance
                        && instance.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance)
                    {
                        captures.TryAdd(
                            $"this:{this.m_method.TypeId}",
                            GetCurrentInstanceValueId());
                    }
                }

                return captures.OrderBy(item => item.Key)
                    .Select(item => new BehaviorCaptureBinding(item.Key, item.Value))
                    .ToArray();
            }

            // 递归列出一个操作树，忽略其中再次嵌套的函数体。
            private static IEnumerable<IOperation> EnumerateOperations(IOperation root)
            {
                foreach (IOperation child in root.ChildOperations)
                {
                    yield return child;
                    if (child is IAnonymousFunctionOperation or ILocalFunctionOperation)
                    {
                        continue;
                    }

                    foreach (IOperation descendant in EnumerateOperations(child))
                    {
                        yield return descendant;
                    }
                }
            }

            // 读取字段值并附加结构化字段身份。
            private int ReadFieldValue(IOperation operation, IFieldReferenceOperation field)
            {
                if (field.Field.HasConstantValue)
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.Constant,
                        Convert.ToString(
                            field.Field.ConstantValue,
                            System.Globalization.CultureInfo.InvariantCulture),
                        Array.Empty<int>());
                }

                int id = AddOperationValue(
                    operation,
                    BehaviorValueKind.FieldRead,
                    field.Field.MetadataName,
                    field.Field.IsStatic
                        ? Array.Empty<int>()
                        : new[] { ReadValue(field.Instance!) });
                this.m_values[id] = this.m_values[id] with
                {
                    Member = ReadFieldReference(field.Field),
                };

                return id;
            }

            // 判断 Roslyn 尚未公开专用接口的指针解引用操作。
            private static bool IsPointerIndirection(IOperation operation)
            {
                return operation.Kind == OperationKind.None
                    && operation.Syntax is PrefixUnaryExpressionSyntax prefix
                    && prefix.IsKind(SyntaxKind.PointerIndirectionExpression);
            }

            // 读取字段式事件值并附加结构化事件存储身份。
            private int ReadEventValue(
                IOperation operation,
                IEventReferenceOperation eventReference)
            {
                int id = AddOperationValue(
                    operation,
                    BehaviorValueKind.FieldRead,
                    eventReference.Event.MetadataName,
                    eventReference.Event.IsStatic
                        ? Array.Empty<int>()
                        : new[] { ReadValue(eventReference.Instance!) });
                this.m_values[id] = this.m_values[id] with
                {
                    Member = ReadEventReference(eventReference.Event),
                };

                return id;
            }

            // 追加一个没有独立操作树节点但带结构化字段身份的值。
            private int AddMemberValue(
                BehaviorValueKind kind,
                BehaviorMemberReference member,
                IReadOnlyList<int> inputValueIds)
            {
                int id = AddValue(kind, member.Name, null, inputValueIds);
                this.m_values[id] = this.m_values[id] with { Member = member };

                return id;
            }

            // 读取数组创建值和全部维度来源。
            private int ReadArrayCreation(IOperation operation, IArrayCreationOperation array)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                int id = AddOperationValue(
                    operation,
                    BehaviorValueKind.NewArray,
                    array.Type?.ToDisplayString(),
                    array.DimensionSizes.Select(ReadValue).ToArray());
                this.m_values[id] = this.m_values[id] with
                {
                    Type = array.Type == null ? null : ReadTypeReference(array.Type),
                };
                if (array.Initializer != null
                    && this.m_recordedInitializers.Add(array.Initializer))
                {
                    RecordArrayInitializer(id, array.Initializer, Array.Empty<int>());
                }

                return id;
            }

            // 递归记录矩形或交错数组初始化器写入的每个下标和值。
            private void RecordArrayInitializer(
                int arrayValueId,
                IArrayInitializerOperation initializer,
                IReadOnlyList<int> parentIndexes)
            {
                for (int index = 0; index < initializer.ElementValues.Length; index++)
                {
                    IOperation element = initializer.ElementValues[index];
                    int indexValueId = AddValue(
                        BehaviorValueKind.Constant,
                        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        null,
                        Array.Empty<int>());
                    int[] indexes = parentIndexes.Append(indexValueId).ToArray();
                    if (element is IArrayInitializerOperation nested)
                    {
                        RecordArrayInitializer(arrayValueId, nested, indexes);
                    }
                    else
                    {
                        this.m_writes.Add(CreateWrite(
                            BehaviorWriteKind.ArrayElement,
                            arrayValueId,
                            null,
                            indexes,
                            ReadValue(element),
                            element.Syntax.SpanStart));
                    }
                }
            }

            // 读取 typeof 表达式中的结构化类型。
            private int ReadTypeValue(IOperation operation, ITypeSymbol type)
            {
                int id = AddOperationValue(
                    operation,
                    BehaviorValueKind.Type,
                    type.ToDisplayString(),
                    Array.Empty<int>());
                this.m_values[id] = this.m_values[id] with
                {
                    Type = ReadTypeReference(type),
                };

                return id;
            }

            // 读取方法组绑定的结构化函数和接收对象。
            private int ReadMethodValue(IOperation operation, IMethodReferenceOperation method)
            {
                List<int> inputValueIds = new();
                BehaviorCaptureBinding[] captures = Array.Empty<BehaviorCaptureBinding>();
                if (method.Instance != null)
                {
                    inputValueIds.Add(ReadValue(method.Instance));
                }

                if (method.Method.MethodKind == MethodKind.LocalFunction)
                {
                    captures = ReadLocalFunctionCaptureSources(method.Method);
                    inputValueIds.AddRange(captures.Select(capture => capture.ValueId));
                }

                int id = AddOperationValue(
                    operation,
                    BehaviorValueKind.Function,
                    method.Method.MetadataName,
                    inputValueIds);
                this.m_values[id] = this.m_values[id] with
                {
                    Captures = captures,
                    Method = ReadMethodReference(method.Method),
                };

                return id;
            }

            // 读取属性 getter 调用及其结果值。
            private int ReadPropertyValue(IOperation operation, IPropertyReferenceOperation property)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    return existingId;
                }

                if (IsArrayForEachCompilerOperation(operation))
                {
                    return AddOperationValue(
                        operation,
                        BehaviorValueKind.ArrayElementRead,
                        "foreach-element",
                        property.Instance == null
                            ? Array.Empty<int>()
                            : new[] { ReadValue(property.Instance) });
                }

                IMethodSymbol getter = property.Property.GetMethod
                    ?? throw new AnalysisException(
                        $"源码属性没有读取函数：{this.m_method.Id} @ {operation.Syntax.SpanStart}");
                BehaviorArgument[] arguments = property.Arguments
                    .Select(argument => new BehaviorArgument(
                        ReadValue(argument.Value),
                        ReadRefKind(argument.Parameter?.RefKind ?? RefKind.None)))
                    .ToArray();
                int resultValueId = AddOperationValue(
                    operation,
                    BehaviorValueKind.CallResult,
                    getter.MetadataName,
                    arguments.Select(argument => argument.ValueId).ToArray());
                this.m_calls.Add(CreateCall(
                    ReadCallKind(getter),
                    ReadMethodReference(getter),
                    getter.IsStatic ? null : ReadValue(property.Instance!),
                    arguments,
                    resultValueId,
                    operation.Syntax.SpanStart));

                return resultValueId;
            }

            // 建立一个操作树节点对应的稳定值来源。
            private int AddOperationValue(
                IOperation operation,
                BehaviorValueKind kind,
                string? reference,
                IReadOnlyList<int> inputValueIds)
            {
                if (this.m_valueIds.TryGetValue(operation, out int existingId))
                {
                    this.m_values[existingId] = this.m_values[existingId] with
                    {
                        Kind = kind,
                        Reference = reference,
                        InputValueIds = inputValueIds,
                    };

                    return existingId;
                }

                int id = this.m_values.Count;
                this.m_values.Add(new BehaviorValue(
                    id,
                    kind,
                    reference,
                    null,
                    inputValueIds)
                {
                    Point = NextPoint(),
                });
                this.m_valueIds.Add(operation, id);

                return id;
            }

            // 追加当前执行位置产生且不依赖具体操作树节点的值来源。
            private int AddValue(
                BehaviorValueKind kind,
                string? reference,
                int? parameterIndex,
                IReadOnlyList<int> inputValueIds)
            {
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

                return id;
            }

            // 追加没有执行位置的稳定根槽。
            private int AddRootValue(
                BehaviorValueKind kind,
                string? reference,
                int? parameterIndex)
            {
                int id = this.m_values.Count;
                this.m_values.Add(new BehaviorValue(
                    id,
                    kind,
                    reference,
                    parameterIndex,
                    Array.Empty<int>()));

                return id;
            }

            // 返回当前对象在本函数中的唯一值来源。
            private int GetCurrentInstanceValueId()
            {
                this.m_currentInstanceValueId ??= AddRootValue(
                    BehaviorValueKind.CurrentInstance,
                    null,
                    null);

                return this.m_currentInstanceValueId.Value;
            }

            // 返回一个函数参数在本函数中的唯一值来源。
            private int GetParameterValueId(IParameterSymbol parameter)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        parameter.ContainingSymbol,
                        this.m_method.SourceSymbol))
                {
                    return GetCapturedValueId(parameter);
                }

                if (!this.m_parameterValueIds.TryGetValue(parameter, out int id))
                {
                    id = AddRootValue(
                        BehaviorValueKind.Parameter,
                        parameter.Name,
                        parameter.Ordinal);
                    this.m_parameterValueIds.Add(parameter, id);
                }

                return id;
            }

            // 返回一个局部变量在本函数中的唯一值来源。
            private int GetLocalValueId(ILocalSymbol local)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        local.ContainingSymbol,
                        this.m_method.SourceSymbol))
                {
                    return GetCapturedValueId(local);
                }

                if (!this.m_localValueIds.TryGetValue(local, out int id))
                {
                    id = AddRootValue(
                        BehaviorValueKind.Local,
                        local.Name,
                        null);
                    this.m_localValueIds.Add(local, id);
                }

                return id;
            }

            // 读取局部函数绑定时由外层函数提供的捕获值。
            private BehaviorCaptureBinding[] ReadLocalFunctionCaptureSources(IMethodSymbol method)
            {
                SyntaxNode syntax = method.DeclaringSyntaxReferences.Single()
                    .GetSyntax(this.m_cancellationToken);
                SemanticModel model = this.m_compilation.GetSemanticModel(syntax.SyntaxTree);
                ILocalFunctionOperation localFunction = model.GetOperation(
                        syntax,
                        this.m_cancellationToken) as ILocalFunctionOperation
                    ?? throw new AnalysisException(
                        $"局部函数没有操作树：{this.m_method.Id} @ {syntax.SpanStart}");

                return ReadFunctionCaptureSources(
                    localFunction.Symbol,
                    localFunction.Body
                        ?? throw new AnalysisException(
                            $"局部函数没有函数体：{this.m_method.Id} @ {syntax.SpanStart}"));
            }

            // 返回匿名函数或局部函数捕获的外层值来源。
            private int GetCapturedValueId(ISymbol symbol)
            {
                if (!this.m_capturedValueIds.TryGetValue(symbol, out int id))
                {
                    id = AddRootValue(
                        BehaviorValueKind.Captured,
                        ReadCaptureKey(symbol),
                        null);
                    this.m_capturedValueIds.Add(symbol, id);
                }

                return id;
            }

            // 建立绑定处与内部函数体共同使用的稳定捕获键。
            private string ReadCaptureKey(ISymbol symbol)
            {
                if (symbol is IParameterSymbol parameter
                    && this.m_sourceMethodIds.TryGetValue(
                        parameter.ContainingSymbol.OriginalDefinition,
                        out string? methodId))
                {
                    return $"{methodId}:parameter:{parameter.Ordinal.ToString()}";
                }

                FileLinePositionSpan location = symbol.Locations.Single(item => item.IsInSource)
                    .GetLineSpan();
                return $"{Path.GetFullPath(location.Path)}:{location.StartLinePosition.Line.ToString()}:"
                    + location.StartLinePosition.Character.ToString();
            }

            // 读取源码函数引用并转换为公开结构。
            private BehaviorMethodReference ReadMethodReference(IMethodSymbol method)
            {
                try
                {
                    IMethodSymbol definition = (method.ReducedFrom ?? method).OriginalDefinition;
                    this.m_sourceMethodIds.TryGetValue(definition, out string? knownMethodId);
                    return BehaviorMethodReference.From(this.m_catalog.ReadSourceMethodReference(
                        method,
                        this.m_method.AssemblyName)) with
                    {
                        KnownMethodId = knownMethodId,
                    };
                }
                catch (AnalysisException exception)
                {
                    throw new AnalysisException(
                        $"读取源码调用引用失败：{method.ToDisplayString()} => {exception.Message}");
                }
            }

            // 读取源码字段引用并转换为公开结构。
            private BehaviorMemberReference ReadFieldReference(IFieldSymbol field)
            {
                return BehaviorMemberReference.From(this.m_catalog.ReadSourceFieldReference(
                    field,
                    this.m_method.AssemblyName));
            }

            // 读取源码字段式事件引用并转换为公开结构。
            private BehaviorMemberReference ReadEventReference(IEventSymbol eventSymbol)
            {
                return BehaviorMemberReference.From(this.m_catalog.ReadSourceEventReference(
                    eventSymbol,
                    this.m_method.AssemblyName));
            }

            // 读取源码类型引用并转换为公开结构。
            private BehaviorTypeReference ReadTypeReference(ITypeSymbol type)
            {
                try
                {
                    return BehaviorTypeReference.From(this.m_catalog.ReadSourceTypeReference(
                        type,
                        this.m_method.AssemblyName));
                }
                catch (AnalysisException exception)
                {
                    throw new AnalysisException(
                        $"读取源码类型引用失败：{type.Kind} {type.ToDisplayString()} "
                            + $"=> {exception.Message}");
                }
            }

            // 把 Roslyn 参数传递方式转换为函数总表使用的枚举。
            private static CatalogRefKind ReadRefKind(RefKind refKind)
            {
                return refKind switch
                {
                    RefKind.Ref => CatalogRefKind.Ref,
                    RefKind.Out => CatalogRefKind.Out,
                    RefKind.In or RefKind.RefReadOnlyParameter => CatalogRefKind.In,
                    _ => CatalogRefKind.None,
                };
            }

            // 为参数、局部值或控制流临时槽的本次读取建立独立快照。
            private int ReadSlotValue(IOperation operation, int slotValueId, string reference)
            {
                return AddOperationValue(
                    operation,
                    BehaviorValueKind.SlotRead,
                    reference,
                    new[] { slotValueId });
            }

        }

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
                            or Cil.Code.Leave_S
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

                bool isLeave = instruction.OpCode.Code is Cil.Code.Leave or Cil.Code.Leave_S;
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

                if (code == OpCodes.Br
                    || code == OpCodes.Br_S
                    || code == OpCodes.Leave
                    || code == OpCodes.Leave_S)
                {
                    return;
                }

                if (code == OpCodes.Brfalse
                    || code == OpCodes.Brfalse_S
                    || code == OpCodes.Brtrue
                    || code == OpCodes.Brtrue_S)
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

                if (TryReadArgumentAddress(code, operand, out int argumentAddressIndex))
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Address,
                        $"argument:{argumentAddressIndex}",
                        null,
                        new[] { GetArgumentValueId(argumentAddressIndex) }));

                    return;
                }

                if (TryWriteArgument(code, operand, out int writtenArgumentIndex))
                {
                    this.m_assignments[offset] = new BehaviorAssignment(
                        GetArgumentValueId(writtenArgumentIndex),
                        Pop(code, offset),
                        offset)
                    {
                        Point = NextPoint(),
                    };

                    return;
                }

                if (TryReadArgument(code, operand, out int argumentIndex))
                {
                    int slotId = GetArgumentValueId(argumentIndex);
                    this.m_stack.Push(this.m_values[slotId].Kind == BehaviorValueKind.CurrentInstance
                        ? slotId
                        : ReadSlot(slotId));

                    return;
                }

                if (TryReadIntegerConstant(code, operand, out int constant))
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Constant,
                        constant.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

                if (TryReadLocal(code, operand, out int localIndex))
                {
                    this.m_stack.Push(ReadSlot(GetLocalValueId(localIndex)));

                    return;
                }

                if (TryReadLocalAddress(code, operand, out localIndex))
                {
                    this.m_stack.Push(AddValue(
                        BehaviorValueKind.Address,
                        $"local:{localIndex}",
                        null,
                        new[] { GetLocalValueId(localIndex) }));

                    return;
                }

                if (TryWriteLocal(code, operand, out localIndex))
                {
                    this.m_assignments[offset] = new BehaviorAssignment(
                        GetLocalValueId(localIndex),
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
                    this.m_stack.Push(AddFieldRead(
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
                    this.m_stack.Push(AddFieldRead(
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
                    Cil.Code.Beq or Cil.Code.Beq_S or Cil.Code.Bne_Un or Cil.Code.Bne_Un_S
                        or Cil.Code.Bge or Cil.Code.Bge_S or Cil.Code.Bge_Un or Cil.Code.Bge_Un_S
                        or Cil.Code.Bgt or Cil.Code.Bgt_S or Cil.Code.Bgt_Un or Cil.Code.Bgt_Un_S
                        or Cil.Code.Ble or Cil.Code.Ble_S or Cil.Code.Ble_Un or Cil.Code.Ble_Un_S
                        or Cil.Code.Blt or Cil.Code.Blt_S or Cil.Code.Blt_Un or Cil.Code.Blt_Un_S => (2, 0),
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
                Cecil.MethodDefinition definition = method.Resolve()
                    ?? throw new AnalysisException(
                        $"托管调用目标无法解析：{this.m_method.Id} @ {offset} => {reference.Identity.Text}");
                BehaviorArgument[] arguments = new BehaviorArgument[reference.Identity.Parameters.Count];

                for (int index = arguments.Length - 1; index >= 0; index--)
                {
                    arguments[index] = new BehaviorArgument(
                        Pop(code, offset),
                        MethodCatalog.ReadManagedRefKind(definition.Parameters[index]));
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
            private static BehaviorCallKind ReadCallKind(
                OpCode code,
                Cecil.MethodDefinition definition)
            {
                if (code == OpCodes.Newobj)
                {
                    return BehaviorCallKind.ObjectCreation;
                }

                if (definition.Name == "Invoke" && IsDelegateType(definition.DeclaringType))
                {
                    return BehaviorCallKind.Delegate;
                }

                return code == OpCodes.Callvirt && definition.IsVirtual
                    ? BehaviorCallKind.Virtual
                    : BehaviorCallKind.Direct;
            }

            // 沿类型定义基类确认 Invoke 是否属于真实委托类型。
            private static bool IsDelegateType(Cecil.TypeDefinition type)
            {
                Cecil.TypeDefinition? current = type;
                while (current != null)
                {
                    if (current.FullName == "System.MulticastDelegate")
                    {
                        return true;
                    }

                    if (current.BaseType == null)
                    {
                        return false;
                    }

                    current = current.BaseType.Resolve()
                        ?? throw new AnalysisException(
                            $"委托基类无法解析：{current.BaseType.FullName}");
                }

                return false;
            }

            // 识别所有短格式和长格式的参数读取指令。
            private bool TryReadArgument(OpCode code, object? operand, out int argumentIndex)
            {
                argumentIndex = code.Code switch
                {
                    Cil.Code.Ldarg_0 => 0,
                    Cil.Code.Ldarg_1 => 1,
                    Cil.Code.Ldarg_2 => 2,
                    Cil.Code.Ldarg_3 => 3,
                    Cil.Code.Ldarg or Cil.Code.Ldarg_S => ReadArgumentIndex(code, operand),
                    _ => -1,
                };
                return argumentIndex >= 0;
            }

            // 识别参数地址读取并返回包含当前对象的真实参数槽。
            private bool TryReadArgumentAddress(
                OpCode code,
                object? operand,
                out int argumentIndex)
            {
                argumentIndex = code.Code is Cil.Code.Ldarga or Cil.Code.Ldarga_S
                    ? ReadArgumentIndex(code, operand)
                    : -1;
                return argumentIndex >= 0;
            }

            // 识别参数槽写入并返回包含当前对象的真实参数槽。
            private bool TryWriteArgument(
                OpCode code,
                object? operand,
                out int argumentIndex)
            {
                argumentIndex = code.Code is Cil.Code.Starg or Cil.Code.Starg_S
                    ? ReadArgumentIndex(code, operand)
                    : -1;
                return argumentIndex >= 0;
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

            // 识别所有短格式和长格式的局部变量读取指令。
            private static bool TryReadLocal(OpCode code, object? operand, out int localIndex)
            {
                localIndex = code.Code switch
                {
                    Cil.Code.Ldloc_0 => 0,
                    Cil.Code.Ldloc_1 => 1,
                    Cil.Code.Ldloc_2 => 2,
                    Cil.Code.Ldloc_3 => 3,
                    Cil.Code.Ldloc or Cil.Code.Ldloc_S => ReadLocalIndex(code, operand),
                    _ => -1,
                };
                return localIndex >= 0;
            }

            // 识别局部变量地址读取并返回 Cecil 已解析的槽编号。
            private static bool TryReadLocalAddress(
                OpCode code,
                object? operand,
                out int localIndex)
            {
                localIndex = code.Code is Cil.Code.Ldloca or Cil.Code.Ldloca_S
                    ? ReadLocalIndex(code, operand)
                    : -1;
                return localIndex >= 0;
            }

            // 识别所有短格式和长格式的局部变量写入指令。
            private static bool TryWriteLocal(OpCode code, object? operand, out int localIndex)
            {
                localIndex = code.Code switch
                {
                    Cil.Code.Stloc_0 => 0,
                    Cil.Code.Stloc_1 => 1,
                    Cil.Code.Stloc_2 => 2,
                    Cil.Code.Stloc_3 => 3,
                    Cil.Code.Stloc or Cil.Code.Stloc_S => ReadLocalIndex(code, operand),
                    _ => -1,
                };
                return localIndex >= 0;
            }

            // 返回 Cecil 已解析的局部变量槽编号。
            private static int ReadLocalIndex(OpCode code, object? operand)
            {
                return RequireOperand<Cil.VariableDefinition>(code, operand, -1).Index;
            }

            // 识别整型常量的短格式与普通格式。
            private static bool TryReadIntegerConstant(
                OpCode code,
                object? operand,
                out int value)
            {
                value = code.Code switch
                {
                    >= Cil.Code.Ldc_I4_0 and <= Cil.Code.Ldc_I4_8 =>
                        code.Code - Cil.Code.Ldc_I4_0,
                    Cil.Code.Ldc_I4_M1 => -1,
                    Cil.Code.Ldc_I4 => RequireOperand<int>(code, operand, -1),
                    Cil.Code.Ldc_I4_S => RequireOperand<sbyte>(code, operand, -1),
                    _ => 0,
                };
                return code.Code is >= Cil.Code.Ldc_I4_M1 and <= Cil.Code.Ldc_I4;
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

            // 追加一个带结构化字段身份的读取值。
            private int AddFieldRead(
                BehaviorMemberReference member,
                IReadOnlyList<int> inputValueIds)
            {
                int id = AddValue(BehaviorValueKind.FieldRead, member.Name, null, inputValueIds);
                this.m_values[id] = this.m_values[id] with { Member = member };
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
                    this.m_catalog.ReadManagedFieldReference(field);

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
    /// 区分调用时立即执行的函数体和枚举时才执行的迭代器函数体。
    /// </summary>
    public enum BehaviorExecutionKind
    {
        /// <summary>普通调用会立即执行函数体。</summary>
        Immediate,
        /// <summary>调用只创建迭代器，函数体事实要到枚举时才执行。</summary>
        IteratorBody,
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
        /// <summary>没有可执行转移。</summary>
        None,
        /// <summary>普通控制流转移。</summary>
        Regular,
        /// <summary>函数返回。</summary>
        Return,
        /// <summary>结构化异常处理内部转移。</summary>
        StructuredExceptionHandling,
        /// <summary>程序终止。</summary>
        ProgramTermination,
        /// <summary>抛出异常。</summary>
        Throw,
        /// <summary>重新抛出当前异常。</summary>
        Rethrow,
        /// <summary>编译器报告的错误控制流。</summary>
        Error,
    }

    /// <summary>
    /// 区分局部生命周期与异常处理区域。
    /// </summary>
    public enum BehaviorFlowRegionKind
    {
        /// <summary>完整函数区域。</summary>
        Root,
        /// <summary>局部变量和临时值生命周期区域。</summary>
        LocalLifetime,
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
        /// <summary>静态局部变量初始化区域。</summary>
        StaticLocalInitializer,
        /// <summary>编译错误函数体区域。</summary>
        ErroneousBody,
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
        /// <summary>匿名函数或局部函数捕获的外层值。</summary>
        Captured,
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
        /// <summary>throw 等明确不会产生普通结果的表达式分支。</summary>
        NoReturn,
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

        /// <summary>函数值捕获键与绑定处稳定槽的明确对应关系。</summary>
        public IReadOnlyList<BehaviorCaptureBinding> Captures { get; init; } =
            Array.Empty<BehaviorCaptureBinding>();
    }

    /// <summary>
    /// 保存一个内部函数捕获键在绑定处对应的稳定槽。
    /// </summary>
    public sealed record BehaviorCaptureBinding(
        string Key,
        int ValueId);

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
        /// <summary>源码总表中已经精确命中的物理函数身份。</summary>
        public string? KnownMethodId { get; init; }

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
            BehaviorExecutionKind executionKind = BehaviorExecutionKind.Immediate,
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
            this.ExecutionKind = executionKind;
            this.Blocks = blocks ?? Array.Empty<BehaviorFlowBlock>();
            this.Regions = regions ?? Array.Empty<BehaviorFlowRegion>();
        }

        /// <summary>函数的唯一身份。</summary>
        public string MethodId { get; }

        /// <summary>函数体来源种类。</summary>
        public MethodBodyKind BodyKind { get; }

        /// <summary>函数体事实会在调用时还是枚举时执行。</summary>
        public BehaviorExecutionKind ExecutionKind { get; }

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
