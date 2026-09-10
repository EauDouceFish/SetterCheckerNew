using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil.Rocks;
using Cecil = Mono.Cecil;

namespace SetterChecker.Core
{
    /// <summary>
    /// 从 Roslyn 源码和 Cecil 托管程序集建立统一的函数、类型与继承总表。
    /// </summary>
    public sealed class MethodCatalog
    {
        private static readonly SymbolDisplayFormat s_typeDisplayFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

        // 并行读取源码和托管文件并建立固定顺序的总表。
        /// <summary>
        /// 建立后续分析唯一使用的函数和类型总表。
        /// </summary>
        public async Task<MethodCatalogResult> BuildAsync(
            MaterialSet material,
            int jobs,
            CancellationToken cancellationToken = default)
        {
            if (jobs <= 0)
            {
                throw new AnalysisException("工作数量必须是正整数。");
            }

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string[] managedPaths = material.ExternalAssemblies
                .SelectMany(assembly => assembly.ImplementationPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] assemblyLookupPaths = material.AssemblyLookupPaths
                .Concat(material.UsesUnityLegacyBinding ? Array.Empty<string>() : managedPaths)
                .Except(material.AnalyzerPaths, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SourceCatalogContext[] sourceContexts = material.SourceAssemblies
                .Select(assembly => new SourceCatalogContext(
                    assembly,
                    assembly.SourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    assembly.ReportSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            ConcurrentBag<ManagedAssemblyPart> managedParts = new();
            ConcurrentBag<TypeEntry> allTypes = new();
            ConcurrentBag<IReadOnlyList<MethodEntry>> sourceMethodParts = new();
            CatalogAssemblyWorkItem[] assemblyWorkItems = managedPaths
                .Select(path => new CatalogAssemblyWorkItem(path, null))
                .Concat(sourceContexts.Select(context => new CatalogAssemblyWorkItem(
                    context.Material.AssemblyPath,
                    context)))
                .ToArray();

            await Parallel.ForEachAsync(
                assemblyWorkItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (workItem, _) =>
                {
                    SourceCatalogContext? context = workItem.SourceContext;
                    using Cecil.ModuleDefinition module = context == null ? OpenModule(workItem.Path) : OpenModule(context.Material.AssemblyImage);
                    ManagedAssemblyPart part = ReadManagedTypes(Path.GetFullPath(workItem.Path), module);
                    // 仅把当前用户源码声明配回元数据，生成器产物仍保留在托管目录。
                    Dictionary<string, INamedTypeSymbol>? sourceTypes = context == null ? null : context.Material.Compilation
                        .GetSymbolsWithName(_ => true, SymbolFilter.Type, cancellationToken).OfType<INamedTypeSymbol>()
                        .Where(type => type.Locations.Any(location => location.IsInSource
                            && location.SourceTree != null && context.DeclaredPaths.Contains(location.SourceTree.FilePath)))
                        .ToDictionary(ReadDocumentationId, StringComparer.Ordinal);
                    managedParts.Add(part);
                    foreach (TypeEntry metadata in part.Types)
                    {
                        if (sourceTypes != null && sourceTypes.Remove(metadata.DocumentationId, out INamedTypeSymbol? symbol))
                        {
                            TypeEntry sourceType = CreateSourceType(symbol, metadata);
                            allTypes.Add(sourceType);
                            sourceMethodParts.Add(ReadSourceTypeMethods(context!, symbol, sourceType, ReadManagedTypeMethods(metadata, module)));
                        }
                        else
                        {
                            allTypes.Add(metadata);
                        }
                    }
                    if (sourceTypes?.Count > 0)
                    {
                        throw new AnalysisException("源码类型没有元数据定义：" + string.Join(", ", sourceTypes.Keys.Order(StringComparer.Ordinal)));
                    }

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            ManagedAssemblyPart[] allManagedParts = managedParts
                .OrderBy(part => part.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            MethodEntry[] sourceMethods = sourceMethodParts
                .SelectMany(methods => methods)
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
            TypeEntry[] orderedTypes = allTypes.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();

            RequireUniqueIdentities(sourceMethods.Select(method => method.Id), "函数");
            RequireUniqueIdentities(orderedTypes.Select(type => type.Id), "类型");

            return new MethodCatalogResult(
                sourceMethods,
                orderedTypes,
                allManagedParts,
                sourceContexts,
                assemblyLookupPaths,
                material.UsesUnityLegacyBinding,
                material.ExplicitRuntimeAssemblyPaths,
                material.AssemblyRedirects,
                stopwatch,
                jobs);
        }

        // 比较程序集名称、版本、区域和公钥标记。
        internal static bool MatchesAssemblyIdentity(
            System.Reflection.AssemblyName actual,
            System.Reflection.AssemblyName expected)
        {
            return Equals(actual.Version, expected.Version)
                && MaterialLoader.SameAssemblyNameCultureAndToken(expected, actual);
        }

        // 延迟读取一个真实托管文件，避免仅建目录时解析无关特性依赖。
        internal static Cecil.ModuleDefinition OpenModule(string path)
        {
            return Cecil.ModuleDefinition.ReadModule(
                Path.GetFullPath(path),
                new Cecil.ReaderParameters
                {
                    InMemory = true,
                    ReadingMode = Cecil.ReadingMode.Deferred,
                });
        }

        // 从当前编译生成的内存 PE 读取 Cecil 模块。
        internal static Cecil.ModuleDefinition OpenModule(byte[] image)
        {
            return Cecil.ModuleDefinition.ReadModule(
                new MemoryStream(image, writable: false),
                new Cecil.ReaderParameters
                {
                    InMemory = true,
                    ReadingMode = Cecil.ReadingMode.Deferred,
                });
        }

        // 拒绝函数或类型共享同一个物理身份，并保留具体类别和冲突值。
        private static void RequireUniqueIdentities(IEnumerable<string> identities, string kind)
        {
            IGrouping<string, string>? duplicate = identities
                .GroupBy(identity => identity, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Skip(1).Any());
            if (duplicate != null)
            {
                throw new AnalysisException($"{kind}身份不唯一：{duplicate.Key}");
            }
        }

        // 读取标准成员编号，并保留显式接口函数实际写入元数据的名称。
        private static string ReadDocumentationId(ISymbol symbol)
        {
            string id = symbol.GetDocumentationCommentId()
                ?? throw new AnalysisException($"源码声明没有文档成员编号：{symbol}");
            if (symbol is not IMethodSymbol method || method.ExplicitInterfaceImplementations.IsEmpty)
            {
                return id;
            }

            int nameStart = ReadDocumentationId(method.ContainingType).Length + 1;
            int parameterStart = id.IndexOf('(', nameStart);
            string suffix = parameterStart < 0 ? string.Empty : id[parameterStart..];
            string name = method.MetadataName.Replace('.', '#').Replace('<', '{').Replace('>', '}');
            string arity = method.Arity == 0 ? string.Empty : $"``{method.Arity}";

            return id[..nameStart] + name + arity + suffix;
        }

        // 把源码位置和标签附加到 Cecil 已读取的类型事实。
        private static TypeEntry CreateSourceType(INamedTypeSymbol type, TypeEntry metadata)
        {
            INamedTypeSymbol definition = type.OriginalDefinition;

            return metadata with
            {
                Id = metadata.LogicalId,
                FullName = definition.ToDisplayString(s_typeDisplayFormat),
                SourceSymbol = definition,
            };
        }

        // 读取一个源码类型直接声明的全部函数。
        private static IReadOnlyList<MethodEntry> ReadSourceTypeMethods(SourceCatalogContext context,
            INamedTypeSymbol symbol, TypeEntry sourceType, IReadOnlyList<MethodEntry> metadataMethods)
        {
            Dictionary<string, IMethodSymbol> sourceMethods = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Select(method => (method.ReducedFrom ?? method.PartialImplementationPart ?? method).OriginalDefinition)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .ToDictionary(ReadDocumentationId, StringComparer.Ordinal);
            List<MethodEntry> result = new(metadataMethods.Count);
            foreach (MethodEntry metadata in metadataMethods)
            {
                if (sourceMethods.Remove(metadata.DocumentationId, out IMethodSymbol? source))
                {
                    result.Add(CreateSourceMethod(source, sourceType, context, metadata));
                }
                else
                {
                    result.Add(metadata with
                    {
                        TypeId = sourceType.Id,
                        TypeName = sourceType.FullName,
                    });
                }
            }

            IMethodSymbol? missing = sourceMethods.Values.FirstOrDefault(method =>
                !method.IsImplicitlyDeclared);
            if (missing != null)
            {
                throw new AnalysisException(
                    $"源码函数没有元数据定义：{ReadDocumentationId(missing)}；同名元数据："
                    + string.Join("; ", metadataMethods.Where(method => method.Name == missing.MetadataName)
                        .Select(method => method.DocumentationId)));
            }

            return result.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray();
        }

        // 把源码显示和标签信息附加到 Cecil 已读取的函数事实。
        private static MethodEntry CreateSourceMethod(
            IMethodSymbol definition,
            TypeEntry type,
            SourceCatalogContext context,
            MethodEntry metadata)
        {
            Location? location = definition.Locations.FirstOrDefault(item => item.IsInSource);
            string? sourcePath = location?.SourceTree?.FilePath;
            int line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1;
            if (metadata.Parameters.Count != definition.Parameters.Length)
            {
                throw new AnalysisException(
                    $"源码函数与元数据参数数量不一致：{metadata.LogicalId}");
            }

            CatalogMethodKind kind = definition.MethodKind == MethodKind.Destructor
                ? CatalogMethodKind.Destructor
                : metadata.Kind;

            return metadata with
            {
                Id = metadata.LogicalId,
                TypeId = type.Id,
                TypeName = type.FullName,
                Kind = kind,
                SourcePath = sourcePath,
                Line = line,
                IsReportable = IsReportable(
                    definition,
                    kind,
                    sourcePath,
                    context),
                SourceSymbol = definition,
            };
        }

        // 判断源码函数是否属于最终标签统计范围。
        private static bool IsReportable(
            IMethodSymbol method,
            CatalogMethodKind kind,
            string? sourcePath,
            SourceCatalogContext context)
        {
            bool allowedKind = kind is CatalogMethodKind.Ordinary
                or CatalogMethodKind.PropertySetter
                or CatalogMethodKind.EventAdder
                or CatalogMethodKind.EventRemover
                or CatalogMethodKind.Operator
                or CatalogMethodKind.Conversion;
            string namespaceName = method.ContainingNamespace.ToDisplayString();

            return context.Material.IsReportAssembly
                && sourcePath != null
                && context.ReportablePaths.Contains(sourcePath)
                && allowedKind
                && !method.IsAbstract
                && !method.IsExtern
                && !method.IsImplicitlyDeclared
                && namespaceName != "Tss"
                && namespaceName != "UnityEngine"
                && !namespaceName.StartsWith("UnityEngine.", StringComparison.Ordinal)
                && method.ContainingType.TypeKind is TypeKind.Class or TypeKind.Struct
                && method.MetadataName != "getInstance"
                && !method.MetadataName.StartsWith("BaseProxy_", StringComparison.Ordinal)
                && !HasCompilerGeneratedAttribute(method)
                && !HasCompilerGeneratedAttribute(method.ContainingType);
        }

        // 检查一个源码符号是否由编译器生成。
        private static bool HasCompilerGeneratedAttribute(ISymbol symbol)
        {
            return symbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() ==
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        // 生成边界明确的程序集加类型身份。
        internal static string NamedTypeId(string assemblyName, string metadataName)
        {
            assemblyName = assemblyName.ToUpperInvariant();
            return $"A{assemblyName.Length}:{assemblyName}T{metadataName.Length}:{metadataName}";
        }

        /// <summary>保存读取源码函数时需要的编译上下文。</summary>
        internal sealed record SourceCatalogContext(
            SourceAssemblyMaterial Material,
            IReadOnlySet<string> DeclaredPaths,
            IReadOnlySet<string> ReportablePaths);

        /// <summary>保存一个外部文件或源码内存 PE 目录读取任务。</summary>
        private sealed record CatalogAssemblyWorkItem(
            string Path,
            SourceCatalogContext? SourceContext);

        // 共用真实文件或当前源码内存映像的类型读取与模块释放流程。
        internal static ManagedAssemblyPart ReadManagedTypes(
            string path, byte[]? image = null)
        {
            string fullPath = Path.GetFullPath(path);
            using Cecil.ModuleDefinition module = image == null ? OpenModule(fullPath) : OpenModule(image);

            return ReadManagedTypes(fullPath, module);
        }

        // 把已打开模块转换为统一类型目录。
        private static ManagedAssemblyPart ReadManagedTypes(
            string fullPath,
            Cecil.ModuleDefinition module)
        {
            string assemblyName = module.Assembly.Name.Name;
            TypeEntry[] types = module.GetAllTypes()
                .Where(type => type.Name != "<Module>" || !string.IsNullOrEmpty(type.Namespace))
                .Select(type => CreateManagedType(type, fullPath, assemblyName))
                .ToArray();
            ForwardedTypeEntry[] forwarders = module.ExportedTypes
                .Where(type => ReadForwardedRoot(type).IsForwarder)
                .Select(type => CreateForwarder(
                    type,
                    fullPath,
                    assemblyName,
                    module.Assembly.Name.FullName))
                .ToArray();

            return new ManagedAssemblyPart(
                fullPath,
                types,
                forwarders,
                module.AssemblyReferences.Select(reference => reference.FullName).Order(StringComparer.Ordinal).ToArray());
        }

        // 把一个 Cecil 类型定义转换为统一类型记录。
        private static TypeEntry CreateManagedType(
            Cecil.TypeDefinition type,
            string path,
            string assemblyName)
        {
            string logicalId = ManagedNamedTypeDefinitionId(type);
            string id = PhysicalDefinitionId(logicalId, path);

            return new TypeEntry(
                id,
                logicalId,
                assemblyName,
                type.Module.Assembly.Name.FullName,
                RemoveGenericArity(type.Name),
                DisplayManagedTypeDefinition(type),
                type.BaseType == null ? null : CreateManagedRelation(type.BaseType),
                type.Interfaces
                    .Select(item => CreateManagedRelation(item.InterfaceType))
                    .DistinctBy(item => item.TypeId, StringComparer.Ordinal)
                    .OrderBy(item => item.TypeId, StringComparer.Ordinal)
                    .ToArray(),
                type.IsInterface,
                type.IsAbstract,
                null,
                path,
                type.MetadataToken.ToInt32())
            {
                DocumentationId = DocCommentId.GetDocCommentId(type),
                IsValueType = type.IsValueType,
                IsEnum = type.IsEnum,
                IsExplicitLayout = type.IsExplicitLayout,
                IsSealed = type.IsSealed,
                IsCompilerGenerated = type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName
                    == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
                IsNullableValueType = type.Namespace == "System" && type.Name == "Nullable`1"
                    && (type.Module.TypeSystem.CoreLibrary is Cecil.ModuleDefinition coreModule
                        && coreModule == type.Module
                        || type.Module.TypeSystem.CoreLibrary is Cecil.AssemblyNameReference coreLibrary
                        && coreLibrary.FullName == type.Module.Assembly.Name.FullName),
                GenericParameters = ReadGenericParameters(type.GenericParameters),
            };
        }

        // 类和函数的类型参数共用同一份真实约束读取。
        private static IReadOnlyList<GenericParameterRule> ReadGenericParameters(
            IEnumerable<Cecil.GenericParameter> parameters)
        {
            return parameters.Select(parameter => new GenericParameterRule(
                parameter.IsCovariant, parameter.IsContravariant, parameter.HasReferenceTypeConstraint,
                parameter.HasNotNullableValueTypeConstraint, parameter.HasDefaultConstructorConstraint,
                parameter.Constraints.Select(constraint => ManagedTypeIdentity(constraint.ConstraintType)).ToArray())).ToArray();
        }

        // 把一个 Cecil 基类或接口保存为结构化关系。
        private static TypeRelationEntry CreateManagedRelation(
            Cecil.TypeReference type)
        {
            TypeIdentityTemplate identity = ManagedTypeIdentity(type);
            TypeIdentityTemplate[] arguments = ReadManagedTypeArguments(type).ToArray();

            return new TypeRelationEntry(
                ManagedNamedTypeDefinitionId(type.GetElementType()),
                identity.Text,
                ReadManagedAssemblyName(type.GetElementType(), fullName: true),
                arguments.Select(argument => argument.Text).ToArray())
            {
                FullName = type.GetElementType().FullName,
                ReferenceMetadataToken = type.MetadataToken.ToInt32(),
                KnownMetadataToken = type.GetElementType() is Cecil.TypeDefinition definition
                    ? definition.MetadataToken.ToInt32()
                    : null,
            };
        }

        // 把 Cecil 类型转交声明转换为目标类型别名。
        private static ForwardedTypeEntry CreateForwarder(
            Cecil.ExportedType type,
            string path,
            string facadeAssemblyName,
            string facadeAssemblyIdentity)
        {
            Cecil.IMetadataScope scope = ReadForwardedRoot(type).Scope;
            if (scope is not Cecil.AssemblyNameReference assembly)
            {
                throw new AnalysisException($"类型转交目标不是程序集：{type.FullName}");
            }

            return new ForwardedTypeEntry(
                NamedTypeId(facadeAssemblyName, type.FullName.Replace('/', '+')),
                facadeAssemblyIdentity,
                NamedTypeId(
                    assembly.Name,
                    type.FullName.Replace('/', '+')),
                assembly.FullName,
                path);
        }

        // 沿嵌套转交记录找到持有转交标记和程序集范围的最外层类型。
        private static Cecil.ExportedType ReadForwardedRoot(Cecil.ExportedType type)
        {
            Cecil.ExportedType root = type;
            while (root.DeclaringType != null)
            {
                root = root.DeclaringType;
            }

            return root;
        }

        // 初始材料与后续加载共用同一转交链求解，不重复实现终点判断。
        internal static Dictionary<ForwardedTypeKey, TypeEntry> ResolveForwardedTargets(
            IReadOnlyList<TypeEntry> types, IReadOnlyList<ForwardedTypeEntry> forwarders)
        {
            Dictionary<ForwardedTypeKey, TypeEntry[]> definitions = types
                .GroupBy(type => CreateForwardedTypeKey(type.LogicalId, type.AssemblyIdentity))
                .ToDictionary(group => group.Key, group => group.ToArray());
            Dictionary<ForwardedTypeKey, ForwardedTypeEntry[]> edges = IndexForwarders(forwarders);
            Dictionary<ForwardedTypeKey, TypeEntry?> resolved = new();
            foreach (ForwardedTypeEntry forwarder in edges.OrderBy(item => item.Key.TypeId, StringComparer.Ordinal)
                         .ThenBy(item => item.Key.AssemblyIdentity, StringComparer.Ordinal).Select(item => item.Value[0]))
            {
                ResolveForwardedTarget(forwarder.AliasTypeId, forwarder.AliasAssemblyIdentity, forwarder.AssemblyPath,
                    (typeId, identity, _) =>
                    {
                        ForwardedTypeKey key = CreateForwardedTypeKey(typeId, identity);
                        return new ForwardedTypeNode(definitions.GetValueOrDefault(key) ?? Array.Empty<TypeEntry>(),
                            edges.GetValueOrDefault(key) ?? Array.Empty<ForwardedTypeEntry>());
                    }, resolved, new HashSet<ForwardedTypeKey>(), false);
            }

            return resolved.Where(item => edges.ContainsKey(item.Key) && item.Value != null)
                .ToDictionary(item => item.Key, item => item.Value!);
        }

        // 按完整身份预建转交边，查询时不再扫描全部程序集。
        internal static Dictionary<ForwardedTypeKey, ForwardedTypeEntry[]> IndexForwarders(IEnumerable<ForwardedTypeEntry> forwarders)
        {
            return forwarders.GroupBy(forwarder => CreateForwardedTypeKey(forwarder.AliasTypeId, forwarder.AliasAssemblyIdentity))
                .ToDictionary(group => group.Key, group => group.OrderBy(forwarder => forwarder.TargetTypeId, StringComparer.Ordinal)
                    .ThenBy(forwarder => forwarder.TargetAssemblyIdentity, StringComparer.Ordinal)
                    .ThenBy(forwarder => forwarder.AssemblyPath, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        // 沿真实转交记录选择唯一定义，节点读取者只负责提供已经校验的材料。
        internal static TypeEntry? ResolveForwardedTarget(
            string typeId, string identity, string? referringPath,
            Func<string, string, string?, ForwardedTypeNode> readNode,
            IDictionary<ForwardedTypeKey, TypeEntry?> resolved,
            ISet<ForwardedTypeKey> visiting, bool requireTargets)
        {
            ForwardedTypeKey key = CreateForwardedTypeKey(typeId, identity);
            if (resolved.TryGetValue(key, out TypeEntry? known))
            {
                return known;
            }
            if (!visiting.Add(key))
            {
                throw new AnalysisException($"类型转交形成循环：{typeId} @ {identity}");
            }

            ForwardedTypeNode node = readNode(typeId, identity, referringPath);
            bool sameFileNameOverride = node.Definitions.Length == 1 && node.Forwarders.Length > 0
                && node.Forwarders.All(forwarder => string.Equals(forwarder.AssemblyPath,
                    node.Definitions[0].AssemblyPath, StringComparison.OrdinalIgnoreCase));
            if (node.Definitions.Length > 0 && node.Forwarders.Length > 0 && !sameFileNameOverride)
            {
                throw new AnalysisException($"类型身份同时存在定义和转交：{typeId} @ {identity}");
            }
            if (node.Definitions.Length > 1)
            {
                throw new AnalysisException($"类型转交目标不唯一：{typeId} @ {identity}；引用文件：{referringPath}；候选："
                    + string.Join("; ", node.Definitions.Select(type => type.AssemblyPath)));
            }

            TypeEntry? result = node.Definitions.SingleOrDefault();
            if (node.Forwarders.Length > 0)
            {
                TypeEntry?[] targets = node.Forwarders.DistinctBy(forwarder =>
                        CreateForwardedTypeKey(forwarder.TargetTypeId, forwarder.TargetAssemblyIdentity))
                    .Select(forwarder => ResolveForwardedTarget(forwarder.TargetTypeId, forwarder.TargetAssemblyIdentity,
                        forwarder.AssemblyPath, readNode, resolved, visiting, requireTargets)).ToArray();
                if (targets.Any(target => target == null))
                {
                    if (requireTargets)
                    {
                        throw new AnalysisException($"类型转交目标没有定义：{typeId} @ {identity} => "
                            + string.Join("; ", node.Forwarders.Select(forwarder => forwarder.TargetTypeId + " @ " + forwarder.TargetAssemblyIdentity)));
                    }
                    result = null;
                }
                else
                {
                    TypeEntry[] unique = targets.Cast<TypeEntry>().DistinctBy(type => type.Id, StringComparer.Ordinal).ToArray();
                    if (unique.Length != 1)
                    {
                        throw new AnalysisException($"参考类型身份对应多个真实类型：{typeId} @ {identity} => "
                            + string.Join("; ", unique.Select(type => type.Id)));
                    }
                    result = unique[0];
                }
            }
            visiting.Remove(key);
            resolved[key] = result;
            return result;
        }

        // 把类型身份与完整程序集身份组合成转交节点。
        internal static ForwardedTypeKey CreateForwardedTypeKey(
            string typeId,
            string assemblyIdentity)
        {
            System.Reflection.AssemblyName identity = new(assemblyIdentity);
            string key = $"{identity.Name?.ToUpperInvariant()}|{identity.Version}|"
                + $"{(identity.CultureName ?? string.Empty).ToUpperInvariant()}|"
                + Convert.ToHexString(identity.GetPublicKeyToken() ?? Array.Empty<byte>());

            return new ForwardedTypeKey(typeId, key);
        }

        // 读取一个 Cecil 类型直接声明的全部函数。
        internal static IReadOnlyList<MethodEntry> ReadManagedTypeMethods(
            TypeEntry type,
            Cecil.ModuleDefinition module)
        {
            Cecil.IMetadataTokenProvider provider = module.LookupToken(type.MetadataToken)
                ?? throw new AnalysisException($"托管类型标记不存在：{type.Id}");
            if (provider is not Cecil.TypeDefinition definition)
            {
                throw new AnalysisException($"托管类型标记不是类型定义：{type.Id}");
            }

            return definition.Methods.Select(method => CreateManagedMethod(
                    method,
                    type,
                    method switch
                    {
                        { IsGetter: true } => CatalogMethodKind.PropertyGetter,
                        { IsSetter: true } => CatalogMethodKind.PropertySetter,
                        { IsAddOn: true } => CatalogMethodKind.EventAdder,
                        { IsRemoveOn: true } => CatalogMethodKind.EventRemover,
                        _ => ReadManagedOrdinaryKind(method),
                    }))
                .ToArray();
        }

        // 按 CLR 函数名识别构造、转换和运算符。
        private static CatalogMethodKind ReadManagedOrdinaryKind(Cecil.MethodDefinition method)
        {
            if (method.IsConstructor)
            {
                return method.IsStatic
                    ? CatalogMethodKind.StaticConstructor
                    : CatalogMethodKind.Constructor;
            }

            if (method.Name is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit")
            {
                return CatalogMethodKind.Conversion;
            }

            return method.Name.StartsWith("op_", StringComparison.Ordinal)
                ? CatalogMethodKind.Operator
                : CatalogMethodKind.Ordinary;
        }

        // 把一个 Cecil 函数定义转换为统一函数记录。
        private static MethodEntry CreateManagedMethod(
            Cecil.MethodDefinition method,
            TypeEntry type,
            CatalogMethodKind kind)
        {
            MethodIdentityTemplate identity = ManagedMethodDefinitionIdentity(method);
            ParameterEntry[] parameters = method.Parameters.Select((parameter, index) =>
                new ParameterEntry(parameter.Name, identity.Parameters[index].Text, ReadManagedRefKind(parameter))
                { TypeIdentity = identity.Parameters[index] }).ToArray();
            string id = $"{PhysicalDefinitionId(identity.Text, type.AssemblyPath!)}|M{method.MetadataToken.ToInt32()}";

            return new MethodEntry(
                id,
                identity.Text,
                type.AssemblyName,
                type.Id,
                type.FullName,
                method.Name,
                identity.ReturnType.Text,
                parameters,
                kind,
                null,
                0,
                false,
                method.IsAbstract,
                method.IsPublic,
                method.IsStatic,
                method.IsVirtual,
                method.IsNewSlot,
                method.GenericParameters.Count,
                null,
                type.AssemblyPath,
                method.MetadataToken.ToInt32())
            {
                DocumentationId = ReadManagedDocumentationId(method),
                GenericParameters = ReadGenericParameters(method.GenericParameters),
                IsFinal = method.IsFinal,
                HasNoLogTrackExemption = method.CustomAttributes.Concat(method.DeclaringType.CustomAttributes)
                    .Any(attribute => attribute.AttributeType.FullName == "KH.NoLogTrackAttribute")
                    && !method.CustomAttributes.Concat(method.DeclaringType.CustomAttributes)
                        .Any(attribute => attribute.AttributeType.FullName == "KH.LogTrackAttribute"),
            };
        }

        // 文档身份不含编译器修饰符，实际函数签名仍保留原始元数据。
        private static string ReadManagedDocumentationId(Cecil.MethodDefinition method)
        {
            Cecil.MethodDefinition documented = new(method.Name, method.Attributes,
                ReadDocumentationType(method.ReturnType))
            {
                DeclaringType = method.DeclaringType,
            };
            foreach (Cecil.GenericParameter parameter in method.GenericParameters)
            {
                documented.GenericParameters.Add(new Cecil.GenericParameter(parameter.Name, documented));
            }

            foreach (Cecil.ParameterDefinition parameter in method.Parameters)
            {
                documented.Parameters.Add(new Cecil.ParameterDefinition(ReadDocumentationType(parameter.ParameterType)));
            }

            return DocCommentId.GetDocCommentId(documented);
        }

        // 仅为文档名称复制带修饰的类型结构，不修改读取到的程序集。
        private static Cecil.TypeReference ReadDocumentationType(Cecil.TypeReference type)
        {
            switch (type)
            {
                case Cecil.IModifierType modifier:
                    return ReadDocumentationType(modifier.ElementType);
                case Cecil.ByReferenceType reference:
                    return new Cecil.ByReferenceType(ReadDocumentationType(reference.ElementType));
                case Cecil.PointerType pointer:
                    return new Cecil.PointerType(ReadDocumentationType(pointer.ElementType));
                case Cecil.ArrayType array:
                    Cecil.ArrayType copiedArray = new(ReadDocumentationType(array.ElementType), array.Rank);
                    for (int index = 0; index < array.Dimensions.Count; index++)
                    {
                        copiedArray.Dimensions[index] = array.Dimensions[index];
                    }

                    return copiedArray;
                case Cecil.GenericInstanceType generic:
                    Cecil.GenericInstanceType copiedGeneric = new(ReadDocumentationType(generic.ElementType));
                    foreach (Cecil.TypeReference argument in generic.GenericArguments)
                    {
                        copiedGeneric.GenericArguments.Add(ReadDocumentationType(argument));
                    }

                    return copiedGeneric;
                default:
                    if (type is Cecil.TypeSpecification or Cecil.GenericParameter
                        || type.DeclaringType == null && !type.Name.Contains('`'))
                    {
                        return type;
                    }

                    Cecil.TypeReference named = new(type.Namespace, type.Name, type.Module, type.Scope, type.IsValueType)
                    {
                        DeclaringType = type.DeclaringType == null ? null : ReadDocumentationType(type.DeclaringType),
                    };
                    int separator = type.Name.LastIndexOf('`');
                    int arity = separator < 0 ? 0 : int.Parse(type.Name.AsSpan(separator + 1), System.Globalization.CultureInfo.InvariantCulture);
                    for (int index = 0; index < arity; index++)
                    {
                        named.GenericParameters.Add(new Cecil.GenericParameter(named));
                    }

                    return named;
            }
        }

        // 读取 Cecil 参数的引用传递方式。
        internal static CatalogRefKind ReadManagedRefKind(Cecil.ParameterDefinition parameter)
        {
            Cecil.TypeReference type = parameter.ParameterType;
            while (type is Cecil.IModifierType modifier)
            {
                type = modifier.ElementType;
            }

            if (type is not Cecil.ByReferenceType)
            {
                return CatalogRefKind.None;
            }

            if (parameter.IsOut)
            {
                return CatalogRefKind.Out;
            }

            return parameter.IsIn ? CatalogRefKind.In : CatalogRefKind.Ref;
        }

        // 建立一个 Cecil 函数引用的实际身份。
        internal static MethodIdentityTemplate ManagedMethodIdentity(
            Cecil.MethodReference method)
        {
            return ManagedMethodDefinitionIdentity(method).Instantiate(
                ManagedTypeIdentity(method.DeclaringType),
                ReadManagedTypeArguments(method.DeclaringType).ToArray());
        }

        // 建立 Cecil 函数引用所指向的开放声明身份。
        internal static MethodIdentityTemplate ManagedMethodDefinitionIdentity(
            Cecil.MethodReference method,
            Func<Cecil.TypeReference, string>? namedTypeId = null)
        {
            Cecil.MethodReference element = method.GetElementMethod();

            return new MethodIdentityTemplate(
                new TypeIdentityTemplate(namedTypeId == null
                    ? ManagedNamedTypeDefinitionId(element.DeclaringType.GetElementType())
                    : namedTypeId(element.DeclaringType.GetElementType())),
                element.Name,
                element.GenericParameters.Count,
                element.Parameters.Select(parameter =>
                    ManagedTypeIdentity(parameter.ParameterType, namedTypeId)).ToArray(),
                ManagedTypeIdentity(element.ReturnType, namedTypeId));
        }

        // 建立 Cecil 类型定义或构造类型的确定身份。
        internal static TypeIdentityTemplate ManagedTypeIdentity(
            Cecil.TypeReference type,
            Func<Cecil.TypeReference, string>? namedTypeId = null)
        {
            if (type.IsPrimitive || type.MetadataType is Cecil.MetadataType.Void or Cecil.MetadataType.String or Cecil.MetadataType.Object)
            {
                return new TypeIdentityTemplate(type.FullName);
            }

            return type switch
            {
                Cecil.GenericParameter parameter when parameter.Type == Cecil.GenericParameterType.Method =>
                    new TypeIdentityTemplate($"!!{parameter.Position}"),
                Cecil.GenericParameter parameter =>
                    new TypeIdentityTemplate($"!{parameter.Position}"),
                Cecil.GenericInstanceType instance => new TypeIdentityTemplate(
                    (namedTypeId == null ? ManagedNamedTypeDefinitionId(instance.ElementType) : namedTypeId(instance.ElementType))
                    + $"<{string.Join(',', instance.GenericArguments.Select(argument => ManagedTypeIdentity(argument, namedTypeId).Text))}>"),
                Cecil.ArrayType array => new TypeIdentityTemplate(ManagedTypeIdentity(array.ElementType, namedTypeId).Text + $"[{new string(',', array.Rank - 1)}]"),
                Cecil.ByReferenceType reference => new TypeIdentityTemplate(ManagedTypeIdentity(reference.ElementType, namedTypeId).Text + "&"),
                Cecil.PointerType pointer => new TypeIdentityTemplate(ManagedTypeIdentity(pointer.ElementType, namedTypeId).Text + "*"),
                Cecil.PinnedType pinned => ManagedTypeIdentity(pinned.ElementType, namedTypeId),
                Cecil.OptionalModifierType optional => ManagedTypeIdentity(optional.ElementType, namedTypeId),
                Cecil.RequiredModifierType required => ManagedTypeIdentity(required.ElementType, namedTypeId),
                Cecil.FunctionPointerType function => new TypeIdentityTemplate(
                    DisplayManagedTypeDefinition(function)),
                Cecil.SentinelType sentinel => new TypeIdentityTemplate(ManagedTypeIdentity(sentinel.ElementType, namedTypeId).Text + "..."),
                _ => new TypeIdentityTemplate(namedTypeId == null
                    ? ManagedNamedTypeDefinitionId(type) : namedTypeId(type)),
            };
        }

        // 读取 Cecil 构造类型的全部实际类型参数。
        internal static IEnumerable<TypeIdentityTemplate> ReadManagedTypeArguments(
            Cecil.TypeReference type,
            Func<Cecil.TypeReference, string>? namedTypeId = null)
        {
            return type is Cecil.GenericInstanceType instance
                ? instance.GenericArguments.Select(argument => ManagedTypeIdentity(argument, namedTypeId)).ToArray()
                : Array.Empty<TypeIdentityTemplate>();
        }

        // 建立 Cecil 命名类型定义的跨文件身份。
        internal static string ManagedNamedTypeDefinitionId(
            Cecil.TypeReference type)
        {
            Cecil.TypeReference element = type.GetElementType();
            string assemblyName = ReadManagedAssemblyName(element);

            return NamedTypeId(assemblyName, element.FullName.Replace('/', '+'));
        }

        // 沿 Cecil 嵌套类型找到类型所属程序集。
        internal static string ReadManagedAssemblyName(Cecil.TypeReference type, bool fullName = false)
        {
            Cecil.TypeReference root = type;
            while (root.DeclaringType != null)
            {
                root = root.DeclaringType;
            }

            return root.Scope switch
            {
                Cecil.AssemblyNameReference assembly => fullName ? assembly.FullName : assembly.Name,
                Cecil.ModuleDefinition module => fullName ? module.Assembly.Name.FullName : module.Assembly.Name.Name,
                Cecil.ModuleReference module when !fullName => module.Name,
                _ => throw new AnalysisException(fullName ? $"托管类型没有程序集身份：{type.FullName}"
                    : $"托管类型没有明确的程序集范围：{type.FullName}"),
            };
        }

        // 生成用户可读的 Cecil 类型定义名称。
        private static string DisplayManagedTypeDefinition(Cecil.TypeReference type)
        {
            string ownName = RemoveGenericArity(type.Name);
            string arguments = type.HasGenericParameters
                ? $"<{string.Join(',', type.GenericParameters.Select(parameter => parameter.Name))}>"
                : string.Empty;
            if (type.DeclaringType != null)
            {
                return $"{DisplayManagedTypeDefinition(type.DeclaringType)}.{ownName}{arguments}";
            }

            return string.IsNullOrEmpty(type.Namespace)
                ? ownName + arguments
                : $"{type.Namespace}.{ownName}{arguments}";
        }

        // 去掉 Cecil 类型简单名称末尾的合法泛型元数。
        private static string RemoveGenericArity(string name)
        {
            int separator = name.LastIndexOf('`');
            return separator >= 0
                && int.TryParse(name[(separator + 1)..], out _)
                    ? name[..separator]
                    : name;
        }

        // 为类型和函数共用物理文件的身份部分。
        private static string PhysicalDefinitionId(string logicalId, string path)
        {
            string fullPath = Path.GetFullPath(path);

            return $"{logicalId}|P{fullPath.Length}:{fullPath}";
        }

        /// <summary>保存一次 Cecil 托管文件读取结果。</summary>
        internal sealed record ManagedAssemblyPart(
            string Path,
            IReadOnlyList<TypeEntry> Types,
            IReadOnlyList<ForwardedTypeEntry> Forwarders,
            IReadOnlyList<string> AssemblyReferences);

        /// <summary>保存一个参考类型身份到真实类型身份的转交。</summary>
        internal sealed record ForwardedTypeEntry(
            string AliasTypeId,
            string AliasAssemblyIdentity,
            string TargetTypeId,
            string TargetAssemblyIdentity,
            string AssemblyPath);

        /// <summary>一个转交节点的真实定义与转交边。</summary>
        internal sealed record ForwardedTypeNode(TypeEntry[] Definitions, ForwardedTypeEntry[] Forwarders);

        /// <summary>保存转交链中的类型身份和完整程序集身份。</summary>
        internal readonly record struct ForwardedTypeKey(
            string TypeId,
            string AssemblyIdentity);
    }

    /// <summary>
    /// 保存一个已经确定的类型身份；Cecil 和 Roslyn 负责解析，当前对象只负责传递。
    /// </summary>
    public sealed record TypeIdentityTemplate(string Text)
    {
        internal bool HasUnspecifiedParameter => this.Text.Contains("!{", StringComparison.Ordinal);

        // 保留参数的声明、位置和使用环境，编码后不会被当成另一层的泛型位置再次替换。
        internal static TypeIdentityTemplate ScopedParameter(string declaration, string scope, int ordinal, bool methodParameter = false)
        {
            string identity = $"{declaration.Length}:{declaration}|{scope.Length}:{scope}|{methodParameter}:{ordinal}";
            return new TypeIdentityTemplate("!{" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity)) + "}");
        }

        // 分别用当前类和函数的实参替换各自的泛型参数。
        internal TypeIdentityTemplate Substitute(
            IReadOnlyList<TypeIdentityTemplate> arguments,
            IReadOnlyList<TypeIdentityTemplate>? methodArguments = null)
        {
            if ((arguments.Count == 0 && (methodArguments == null || methodArguments.Count == 0))
                || !this.Text.Contains('!'))
            {
                return this;
            }

            return new TypeIdentityTemplate(System.Text.RegularExpressions.Regex.Replace(this.Text, @"(!{1,2})(\d+)", match =>
            {
                int position = int.Parse(match.Groups[2].Value);
                IReadOnlyList<TypeIdentityTemplate>? actual = match.Groups[1].Value.Length == 2 ? methodArguments : arguments;
                return actual != null && position < actual.Count ? actual[position].Text : match.Value;
            }));
        }
    }

    /// <summary>
    /// 保存函数声明类型、名称、泛型元数、参数和返回类型的唯一身份。
    /// </summary>
    public sealed record MethodIdentityTemplate(
        TypeIdentityTemplate DeclaringType,
        string Name,
        int GenericArity,
        IReadOnlyList<TypeIdentityTemplate> Parameters,
        TypeIdentityTemplate ReturnType)
    {
        internal string Text
        {
            get
            {
                string arity = this.GenericArity == 0 ? string.Empty : $"``{this.GenericArity}";
                string parameters = string.Concat(this.Parameters.Select(parameter =>
                    $"{parameter.Text.Length}:{parameter.Text}"));

                return $"{this.DeclaringType.Text}::{this.Name.Length}:{this.Name}{arity}"
                    + $"({this.Parameters.Count}:{parameters})"
                    + $"->{this.ReturnType.Text.Length}:{this.ReturnType.Text}";
            }
        }

        // 使用实际声明类型和类型实参建立构造函数身份。
        internal MethodIdentityTemplate Instantiate(
            TypeIdentityTemplate declaringType,
            IReadOnlyList<TypeIdentityTemplate> typeArguments)
        {
            return this with
            {
                DeclaringType = declaringType,
                Parameters = this.Parameters.Select(parameter =>
                    parameter.Substitute(typeArguments)).ToArray(),
                ReturnType = this.ReturnType.Substitute(typeArguments),
            };
        }
    }

    /// <summary>保存一个函数声明及其实际声明类型参数。</summary>
    internal sealed record ResolvedMethodDefinition(
        MethodEntry Method,
        IReadOnlyList<TypeIdentityTemplate> DeclaringTypeArguments)
    {
        /// <summary>虚调用中实际选择此实现的接收类型，直接调用不需要此限制。</summary>
        internal IReadOnlyList<TypeIdentityTemplate>? DispatchTypes { get; init; }

        /// <summary>无法完整表达的接收类型不能缩成空集合。</summary>
        internal string? DispatchFailure { get; init; }
    }

    /// <summary>表示函数在源码或托管文件中的种类。</summary>
    public enum CatalogMethodKind
    {
        /// <summary>普通命名函数。</summary>
        Ordinary,
        /// <summary>对象构造函数。</summary>
        Constructor,
        /// <summary>静态构造函数。</summary>
        StaticConstructor,
        /// <summary>属性读取函数。</summary>
        PropertyGetter,
        /// <summary>属性写入函数。</summary>
        PropertySetter,
        /// <summary>事件添加函数。</summary>
        EventAdder,
        /// <summary>事件移除函数。</summary>
        EventRemover,
        /// <summary>运算符函数。</summary>
        Operator,
        /// <summary>类型转换函数。</summary>
        Conversion,
        /// <summary>局部函数。</summary>
        LocalFunction,
        /// <summary>匿名函数。</summary>
        AnonymousFunction,
        /// <summary>析构函数。</summary>
        Destructor,
        /// <summary>其他编译器函数。</summary>
        Other,
    }

    /// <summary>表示函数参数的传递方式。</summary>
    public enum CatalogRefKind
    {
        /// <summary>按值传递。</summary>
        None,
        /// <summary>可读写引用传递。</summary>
        Ref,
        /// <summary>输出引用传递。</summary>
        Out,
        /// <summary>只读引用传递。</summary>
        In,
    }

    /// <summary>保存一个函数参数的名称、类型和传递方式。</summary>
    public sealed record ParameterEntry(
        string Name,
        string TypeId,
        CatalogRefKind RefKind)
    {
        internal TypeIdentityTemplate TypeIdentity { get; init; } =
            new TypeIdentityTemplate(TypeId);
    }

    /// <summary>保存基类或接口定义及其实际泛型参数。</summary>
    public sealed record TypeRelationEntry(
        string DefinitionId,
        string TypeId,
        string AssemblyIdentity,
        IReadOnlyList<string> TypeArguments)
    {
        internal string FullName { get; init; } = string.Empty;

        internal int ReferenceMetadataToken { get; init; }

        internal int? KnownMetadataToken { get; init; }
    }

    /// <summary>保存 CLR 类型参数的方差和构造约束。</summary>
    internal sealed record GenericParameterRule(
        bool IsCovariant,
        bool IsContravariant,
        bool RequiresReferenceType,
        bool RequiresValueType,
        bool RequiresDefaultConstructor,
        IReadOnlyList<TypeIdentityTemplate> TypeConstraints);

    /// <summary>保存一个类型及其直接继承关系。</summary>
    public sealed record TypeEntry(
        string Id,
        string LogicalId,
        string AssemblyName,
        string AssemblyIdentity,
        string Name,
        string FullName,
        TypeRelationEntry? BaseType,
        IReadOnlyList<TypeRelationEntry> Interfaces,
        bool IsInterface,
        bool IsAbstract,
        INamedTypeSymbol? SourceSymbol,
        string? AssemblyPath,
        int MetadataToken)
    {
        internal string DocumentationId { get; init; } = string.Empty;

        internal bool IsValueType { get; init; }

        internal bool IsEnum { get; init; }

        internal bool IsExplicitLayout { get; init; }

        internal bool IsSealed { get; init; }

        internal bool IsCompilerGenerated { get; init; }

        internal bool IsNullableValueType { get; init; }

        internal IReadOnlyList<GenericParameterRule> GenericParameters { get; init; } =
            Array.Empty<GenericParameterRule>();
    }

    /// <summary>保存一个函数的稳定身份、来源、参数、标签和统计属性。</summary>
    public sealed record MethodEntry(
        string Id,
        string LogicalId,
        string AssemblyName,
        string TypeId,
        string TypeName,
        string Name,
        string ReturnTypeId,
        IReadOnlyList<ParameterEntry> Parameters,
        CatalogMethodKind Kind,
        string? SourcePath,
        int Line,
        bool IsReportable,
        bool IsAbstract,
        bool IsPublic,
        bool IsStatic,
        bool IsVirtual,
        bool IsNewSlot,
        int GenericArity,
        IMethodSymbol? SourceSymbol,
        string? AssemblyPath,
        int MetadataToken)
    {
        internal string DocumentationId { get; init; } = string.Empty;

        internal bool IsFinal { get; init; }

        internal bool HasNoLogTrackExemption { get; init; }

        internal IReadOnlyList<GenericParameterRule> GenericParameters { get; init; } = Array.Empty<GenericParameterRule>();
    }

    /// <summary>
    /// 保存函数总表和后续模块直接使用的查找索引。
    /// </summary>
    public sealed class MethodCatalogResult
    {
        private readonly ConcurrentDictionary<string, IReadOnlyList<MethodEntry>>
            m_managedMethodsByTypeId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Path, int Token), MethodIdentityTemplate>
            m_resolvedSignatures = new();
        private readonly ConcurrentDictionary<(string Path, int Token, bool MethodArguments), IReadOnlyList<TypeIdentityTemplate>>
            m_resolvedTypeArguments = new();
        private readonly ConcurrentDictionary<(string Path, int Token), TypeIdentityTemplate>
            m_resolvedFieldTypes = new();
        private readonly ConcurrentDictionary<string, TypeEntry> m_primitiveTypes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ResolvedMethodDefinition[]>>
            m_explicitTargetsByTypeId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Type, string Arguments, bool Interfaces), IReadOnlyList<InheritedTypeRelation>>
            m_inheritedTypes = new();
        private readonly Dictionary<string, Cecil.ModuleDefinition> m_modulesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object m_moduleLock = new();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> m_lookupPathsByAssemblyName;
        private readonly bool m_usesUnityLegacyBinding;
        private readonly IReadOnlyDictionary<string, AssemblyRedirectTarget> m_assemblyRedirects;
        private readonly Dictionary<string, MethodCatalog.ManagedAssemblyPart>
            m_loadedPartsByPath = new(StringComparer.OrdinalIgnoreCase);
        private bool m_dispatchAssembliesClosed;
        private readonly object m_loadedTypeLock = new();
        private readonly Dictionary<bool, int> m_dispatchGenerations = new();
        private int m_typeGeneration;
        private int m_semanticGeneration;

        private TypeEntry[] m_types;
        private IReadOnlyDictionary<string, TypeEntry> m_typesById;
        private IReadOnlyDictionary<string, TypeEntry> m_typesByManagedLocation;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_typesByLogicalId;
        private bool m_logicalAliasesChanged;
        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> m_assemblyCandidates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ResolvedMethodDefinition> m_methodDefinitions = new(StringComparer.Ordinal);
        private Dictionary<MethodCatalog.ForwardedTypeKey, TypeEntry> m_forwardedTargets;
        private Dictionary<MethodCatalog.ForwardedTypeKey, MethodCatalog.ForwardedTypeEntry[]> m_forwardersByAlias;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>>? m_derivedTypesByBaseId;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>>? m_implementingTypesByInterfaceId;
        private readonly IReadOnlyDictionary<string, MethodCatalog.SourceCatalogContext>
            m_sourceContextsByAssembly;
        private readonly IReadOnlyDictionary<string, byte[]> m_sourceImagesByPath;

        // 保存排好顺序的函数、类型和关系索引。
        internal MethodCatalogResult(
            IReadOnlyList<MethodEntry> methods,
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<MethodCatalog.ManagedAssemblyPart> managedParts,
            IReadOnlyList<MethodCatalog.SourceCatalogContext> sourceContexts,
            IReadOnlyList<string> assemblyLookupPaths,
            bool usesUnityLegacyBinding,
            IReadOnlySet<string> explicitRuntimeAssemblyPaths,
            IReadOnlyDictionary<string, string> assemblyRedirects,
            System.Diagnostics.Stopwatch stopwatch,
            int jobs)
        {
            this.m_usesUnityLegacyBinding = usesUnityLegacyBinding;
            this.m_lookupPathsByAssemblyName = MaterialLoader.IndexAssemblyPaths(assemblyLookupPaths,
                usesUnityLegacyBinding ? explicitRuntimeAssemblyPaths : null);
            this.m_assemblyRedirects = assemblyRedirects.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    string path = Path.GetFullPath(pair.Value);

                    return new AssemblyRedirectTarget(
                        System.Reflection.AssemblyName.GetAssemblyName(path),
                        path);
                },
                StringComparer.OrdinalIgnoreCase);
            this.m_sourceContextsByAssembly = sourceContexts.ToDictionary(
                context => context.Material.Name,
                StringComparer.OrdinalIgnoreCase);
            this.m_sourceImagesByPath = sourceContexts.ToDictionary(
                context => Path.GetFullPath(context.Material.AssemblyPath),
                context => context.Material.AssemblyImage,
                StringComparer.OrdinalIgnoreCase);
            foreach (MethodCatalog.ManagedAssemblyPart part in managedParts)
            {
                this.m_loadedPartsByPath.Add(part.Path, part);
            }

            this.m_types = types.ToArray();
            this.m_forwardedTargets = usesUnityLegacyBinding ? new() : MethodCatalog.ResolveForwardedTargets(
                types,
                managedParts.SelectMany(part => part.Forwarders).ToArray());
            this.m_forwardersByAlias = MethodCatalog.IndexForwarders(managedParts.SelectMany(part => part.Forwarders));
            IReadOnlyDictionary<string, TypeEntry> typesById = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByTypeId = null!;
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> typesByLogicalId = null!;
            ParallelOptions options = new() { MaxDegreeOfParallelism = jobs };

            Parallel.Invoke(
                options,
                () => typesById = types.ToDictionary(type => type.Id, StringComparer.Ordinal),
                () => methodsByTypeId = methods.GroupBy(method => method.TypeId, StringComparer.Ordinal).ToDictionary(
                    group => group.Key, group => (IReadOnlyList<MethodEntry>)group.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
                () => typesByLogicalId = IndexLogicalTypes(types));

            this.m_typesById = typesById;
            this.m_typesByManagedLocation = IndexManagedTypeLocations(types);
            this.MethodsByTypeId = methodsByTypeId;
            this.m_typesByLogicalId = typesByLogicalId;
            this.Methods = methods;
            stopwatch.Stop();
            this.Elapsed = stopwatch.Elapsed;
        }

        // 按物理文件和TypeDef标记建立源码回贴后仍稳定的类型索引。
        private static IReadOnlyDictionary<string, TypeEntry> IndexManagedTypeLocations(
            IEnumerable<TypeEntry> types)
        {
            return types.Where(type => type.AssemblyPath != null && type.MetadataToken > 0)
                .ToDictionary(
                    type => ManagedTypeLocation(type.AssemblyPath!, type.MetadataToken),
                    StringComparer.OrdinalIgnoreCase);
        }

        // 生成一个物理文件内TypeDef标记的稳定查找键。
        private static string ManagedTypeLocation(string path, int metadataToken)
        {
            return $"{Path.GetFullPath(path)}|{metadataToken}";
        }

        /// <summary>启动时建立的全部源码函数。</summary>
        public IReadOnlyList<MethodEntry> Methods { get; }

        /// <summary>类型或转交别名新增时，使依赖这些事实的本次路径检查重新执行。</summary>
        internal int SemanticGeneration => this.m_semanticGeneration;

        /// <summary>全部源码和托管类型。</summary>
        public IReadOnlyList<TypeEntry> Types
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    return this.m_types;
                }
            }
        }

        /// <summary>按物理身份查找类型。</summary>
        public IReadOnlyDictionary<string, TypeEntry> TypesById
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    return this.m_typesById;
                }
            }
        }

        /// <summary>按真实身份或转交身份查找类型。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> TypesByLogicalId
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    if (this.m_logicalAliasesChanged)
                    {
                        this.m_typesByLogicalId = IndexLogicalTypes(this.m_types);
                        this.m_logicalAliasesChanged = false;
                    }
                    return this.m_typesByLogicalId;
                }
            }
        }

        // 通过源码编译所用核心库把原始类型身份还原成唯一真实类型。
        internal TypeEntry ReadPrimitiveType(string identity)
        {
            return this.m_primitiveTypes.GetOrAdd(identity, _ =>
            {
                TypeEntry[] meanings = this.m_sourceContextsByAssembly.Values.Select(context =>
                {
                    INamedTypeSymbol? type = context.Material.Compilation.GetTypeByMetadataName(identity);
                    if (type == null || type.SpecialType == SpecialType.None)
                    {
                        throw new AnalysisException($"源码编译未定义原始类型：{context.Material.Name} => {identity}");
                    }

                    string name = type.ContainingAssembly.Identity.Name;
                    IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(
                        MethodCatalog.NamedTypeId(name, identity),
                        type.ContainingAssembly.Identity.GetDisplayName(), context.Material.AssemblyPath, loadMissing: true);
                    return definitions.Count == 1 ? definitions[0]
                        : throw new AnalysisException($"原始类型定义不唯一：{context.Material.Name} => {identity}，命中 {definitions.Count} 个定义");
                }).DistinctBy(type => type.Id, StringComparer.Ordinal).ToArray();

                return meanings.Length == 1 ? meanings[0]
                    : throw new AnalysisException($"原始类型存在不同运行定义：{identity} => {string.Join("; ", meanings.Select(type => type.Id))}");
            });
        }

        /// <summary>按声明类型查找源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByTypeId { get; }

        /// <summary>按可能的物理基类身份查找直接派生候选；执行前仍须核实唯一关系。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> DerivedTypesByBaseId
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    RequireClosedDispatchIndex(includeInterfaces: false);
                    return this.m_derivedTypesByBaseId ??= IndexMany(
                        this.m_types.Where(type => type.BaseType != null),
                        type => ResolveRelationDefinitionIds(type.BaseType!, type), type => type.Id);
                }
            }
        }

        /// <summary>按可能的物理接口身份查找直接声明实现的候选；间接关系沿两表遍历。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> ImplementingTypesByInterfaceId
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    RequireClosedDispatchIndex(includeInterfaces: true);
                    return this.m_implementingTypesByInterfaceId ??= IndexMany(this.m_types,
                        type => type.Interfaces.SelectMany(relation => ResolveRelationDefinitionIds(relation, type)), type => type.Id);
                }
            }
        }

        /// <summary>建立总表所用的时间。</summary>
        public TimeSpan Elapsed { get; }

        // 读取源码类型函数，或按需读取并缓存托管类型函数。
        /// <summary>
        /// 获取指定类型直接声明的全部函数。
        /// </summary>
        public IReadOnlyList<MethodEntry> GetMethods(TypeEntry type)
        {
            return type.SourceSymbol != null
                ? this.MethodsByTypeId.GetValueOrDefault(type.Id) ?? Array.Empty<MethodEntry>()
                : this.m_managedMethodsByTypeId.GetOrAdd(type.Id, _ => ReadManagedMethodDeclarations(type));
        }

        // 按行为事实中的完整类型引用定位唯一声明。
        internal TypeEntry ResolveTypeDefinition(BehaviorTypeReference reference)
        {
            if (reference.KnownTypeId != null)
            {
                return this.TypesById.TryGetValue(reference.KnownTypeId, out TypeEntry? known)
                    ? known
                    : throw new AnalysisException($"本地类型定义尚未载入：{reference.KnownTypeId}");
            }

            if (reference.Identity.Text.StartsWith('!'))
            {
                throw new AnalysisException($"开放类型引用尚未闭合：{reference.Id}");
            }

            if (reference.TargetAssemblyIdentity == null)
            {
                return ReadPrimitiveType(reference.Id);
            }

            IReadOnlyList<TypeEntry> matches = FindTypeDefinitions(
                reference.DefinitionId,
                reference.TargetAssemblyIdentity,
                reference.ReferringAssemblyPath,
                loadMissing: true);

            return matches.Count == 1
                ? matches[0]
                : throw new AnalysisException(
                    $"类型引用定义不唯一：{reference.Id}，命中 {matches.Count} 个定义");
        }

        // 依据当前模块真实核心库确认类型是否直接继承系统委托基类。
        internal bool IsDelegateType(TypeEntry type)
        {
            if (type.BaseType == null || type.AssemblyPath == null)
            {
                return false;
            }

            if (type.BaseType.FullName != "System.MulticastDelegate")
            {
                return false;
            }

            Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath);
            Cecil.TypeReference expected = new("System", "MulticastDelegate", module, module.TypeSystem.CoreLibrary);
            TypeEntry coreBase = ResolveTypeDefinition(ReadManagedTypeReference(expected, type.AssemblyPath));
            TypeEntry[] actualBases = FindTypeDefinitions(
                type.BaseType,
                type,
                loadMissing: true).ToArray();
            if (actualBases.Length != 1)
            {
                throw new AnalysisException($"委托基类没有唯一运行时定义：{type.Id}");
            }

            return actualBases[0].Id == coreBase.Id;
        }

        // 同一轮内共用已经精确匹配的声明，不重复查找相同引用。
        internal ResolvedMethodDefinition ResolveMethodDefinition(
            BehaviorMethodReference reference,
            bool searchInherited)
        {
            string key = $"{reference.TargetAssemblyIdentity}|{reference.ReferringAssemblyPath}|{reference.Identity.Text}"
                + $"|{reference.ReferenceMetadataToken}|{reference.KnownDeclaringTypeId}|{reference.KnownMetadataToken}|{searchInherited}";
            return this.m_dispatchAssembliesClosed
                ? this.m_methodDefinitions.GetOrAdd(key, _ => ReadMethodDefinition(reference, searchInherited))
                : ReadMethodDefinition(reference, searchInherited);
        }

        // 按调用点的完整类型和函数签名找到唯一声明。
        private ResolvedMethodDefinition ReadMethodDefinition(BehaviorMethodReference reference, bool searchInherited)
        {
            IReadOnlyList<TypeIdentityTemplate> arguments = ReadResolvedTypeArguments(
                reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken);
            TypeEntry[] declaringTypes = reference.KnownDeclaringTypeId == null
                ? FindTypeDefinitions(reference.DeclaringTypeDefinitionId, reference.TargetAssemblyIdentity,
                    reference.ReferringAssemblyPath, loadMissing: true).ToArray()
                : new[]
                {
                    this.TypesById.TryGetValue(reference.KnownDeclaringTypeId, out TypeEntry? known)
                        ? known
                        : throw new AnalysisException(
                            $"本地函数声明类型尚未载入：{reference.KnownDeclaringTypeId}"),
                };
            ResolvedMethodDefinition[] matches = reference.KnownMetadataToken is int metadataToken
                ? declaringTypes.SelectMany(type => GetMethods(type).Where(method =>
                        method.MetadataToken == metadataToken)
                    .Select(method => new ResolvedMethodDefinition(
                        method,
                        arguments))).ToArray()
                : declaringTypes.SelectMany(type =>
                        FindMatchingMethods(type, reference, arguments))
                    .ToArray();
            if (matches.Length == 0
                && searchInherited
                && reference.KnownMetadataToken == null)
            {
                matches = declaringTypes.SelectMany(type =>
                        FindInheritedMethods(type, reference))
                    .ToArray();
            }

            if (matches.Length == 1)
            {
                return matches[0];
            }

            throw new AnalysisException(matches.Length == 0
                ? $"调用目标不存在：{reference.Identity.Text}"
                : $"调用目标不唯一：{reference.Identity.Text} => "
                    + string.Join("; ", matches.Select(match => match.Method.Id)));
        }

        // 把材料已证明的参考身份归一到唯一实际文件和身份。
        private AssemblyRedirectTarget NormalizeAssemblyReference(
            System.Reflection.AssemblyName identity)
        {
            string key = identity.FullName
                ?? throw new AnalysisException("程序集引用没有完整身份。");

            return this.m_assemblyRedirects.GetValueOrDefault(key)
                ?? new AssemblyRedirectTarget(identity, null);
        }

        // 按最近继承层级寻找函数的实际声明。
        private IReadOnlyList<ResolvedMethodDefinition> FindInheritedMethods(
            TypeEntry type,
            BehaviorMethodReference reference)
        {
            bool searchInterfaces = type.IsInterface;
            foreach (IGrouping<int, InheritedTypeRelation> level in ReadInheritedTypes(
                         type,
                         ReadResolvedTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken),
                         includeInterfaces: searchInterfaces)
                     .Where(relation =>
                         relation.IsInterface == searchInterfaces).GroupBy(item => item.Depth)
                     .OrderBy(group => group.Key))
            {
                ResolvedMethodDefinition[] matches = level.SelectMany(relation =>
                        FindMatchingMethods(
                            relation.Definition,
                            reference,
                            relation.TypeArguments,
                            relation.TypeArguments))
                    .ToArray();
                if (matches.Length > 0)
                {
                    return matches;
                }
            }

            return Array.Empty<ResolvedMethodDefinition>();
        }

        // 在一个声明类型中精确比较函数的构造签名。
        private IReadOnlyList<ResolvedMethodDefinition> FindMatchingMethods(
            TypeEntry type,
            BehaviorMethodReference reference,
            IReadOnlyList<TypeIdentityTemplate> typeArguments,
            IReadOnlyList<TypeIdentityTemplate>? signatureArguments = null)
        {
            return GetMethods(type).Where(method => MethodMatchesReference(
                    method,
                    reference,
                    signatureArguments))
                .Select(method => new ResolvedMethodDefinition(method, typeArguments))
                .ToArray();
        }

        // 比较名称、元数、调用形态、参数和返回类型的完整身份。
        private bool MethodMatchesReference(
            MethodEntry method,
            BehaviorMethodReference reference,
            IReadOnlyList<TypeIdentityTemplate>? typeArguments)
        {
            if (method.Name != reference.Identity.Name
                || method.GenericArity != reference.Identity.GenericArity
                || method.IsStatic == reference.HasInstance
                || method.Parameters.Count != reference.Identity.Parameters.Count)
            {
                return false;
            }

            MethodIdentityTemplate declaration = ReadResolvedMethodSignature(method.AssemblyPath!, method.MetadataToken);
            MethodIdentityTemplate target = ReadResolvedMethodSignature(
                reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken);
            IReadOnlyList<TypeIdentityTemplate> declarationArguments = typeArguments ?? Array.Empty<TypeIdentityTemplate>();
            IReadOnlyList<TypeIdentityTemplate> referenceArguments = typeArguments == null
                ? Array.Empty<TypeIdentityTemplate>()
                : ReadResolvedTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken);

            return declaration.Parameters.Select(parameter => parameter.Substitute(declarationArguments).Text)
                    .SequenceEqual(target.Parameters.Select(parameter =>
                        parameter.Substitute(referenceArguments).Text), StringComparer.Ordinal)
                && declaration.ReturnType.Substitute(declarationArguments).Text
                    == target.ReturnType.Substitute(referenceArguments).Text;
        }

        // 从实际元数据读取签名，并把其中每个命名类型定位到真实定义。
        internal MethodIdentityTemplate ReadResolvedMethodSignature(string path, int token)
        {
            return this.m_resolvedSignatures.GetOrAdd((path, token), key =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(key.Path);
                Cecil.MethodReference method;
                lock (module)
                {
                    method = (Cecil.MethodReference)module.LookupToken(key.Token);
                }

                return MethodCatalog.ManagedMethodDefinitionIdentity(method,
                    type => ReadResolvedTypeId(type, key.Path));
            });
        }

        // 从原始引用读取类或函数实参，保留每个命名类型的实际来源。
        internal IReadOnlyList<TypeIdentityTemplate> ReadResolvedTypeArguments(string path, int token, bool methodArguments = false)
        {
            return this.m_resolvedTypeArguments.GetOrAdd((path, token, methodArguments), key =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(key.Path);
                Cecil.IMetadataTokenProvider reference;
                lock (module)
                {
                    reference = module.LookupToken(key.Token);
                }

                if (key.MethodArguments)
                {
                    return ((Cecil.GenericInstanceMethod)reference).GenericArguments.Select(argument =>
                        MethodCatalog.ManagedTypeIdentity(argument,
                            definition => ReadResolvedTypeId(definition, key.Path))).ToArray();
                }

                Cecil.TypeReference type = reference is Cecil.TypeReference typeReference
                    ? typeReference
                    : ((Cecil.MemberReference)reference).DeclaringType;

                return MethodCatalog.ReadManagedTypeArguments(type,
                    argument => ReadResolvedTypeId(argument, key.Path)).ToArray();
            });
        }

        // 使用现有类型定位流程取得签名中的真实类型身份。
        private string ReadResolvedTypeId(Cecil.TypeReference type, string path)
        {
            return ResolveTypeDefinition(ReadManagedTypeReference(type, path)).Id;
        }

        // 从字段原始引用还原实际声明类型，并代入字段所属类的构造参数。
        internal TypeIdentityTemplate ReadResolvedFieldType(BehaviorMemberReference reference)
        {
            return this.m_resolvedFieldTypes.GetOrAdd((reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken), key =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(key.Path);
                Cecil.FieldReference field;
                lock (module)
                {
                    field = (Cecil.FieldReference)module.LookupToken(key.Token);
                }

                IReadOnlyList<TypeIdentityTemplate> arguments = ReadResolvedTypeArguments(key.Path, key.Token);
                return MethodCatalog.ManagedTypeIdentity(field.FieldType,
                    type => ReadResolvedTypeId(type, key.Path)).Substitute(arguments);
            });
        }

        // 按完整程序集身份从候选表延迟读取一次真实托管文件的类型。
        /// <summary>
        /// 在调用或候选类型实际命中间接程序集时载入其类型定义。
        /// </summary>
        public IReadOnlyList<TypeEntry> LoadAssemblyTypes(
            System.Reflection.AssemblyName identity,
            string referringAssemblyPath)
        {
            return LoadAssemblyTypes(ResolveAssemblyPath(identity, referringAssemblyPath));
        }

        // 按材料已选明的物理文件合并类型，转交唯一性留给实际查询验证。
        private IReadOnlyList<TypeEntry> LoadAssemblyTypes(string path)
        {
            lock (this.m_loadedTypeLock)
            {
                if (this.m_loadedPartsByPath.TryGetValue(path, out MethodCatalog.ManagedAssemblyPart? part))
                {
                    return part.Types.Select(type => this.m_typesByManagedLocation[
                        ManagedTypeLocation(path, type.MetadataToken)]).ToArray();
                }
                if (this.m_dispatchAssembliesClosed)
                {
                    throw new AnalysisException($"派发预扫描后出现新程序集，必须先闭合动态加载来源：{path}");
                }
                part = this.m_sourceImagesByPath.TryGetValue(path, out byte[]? image)
                    ? MethodCatalog.ReadManagedTypes(path, image) : MethodCatalog.ReadManagedTypes(path);

                TypeEntry[] nextTypes = this.m_types
                    .Concat(part.Types)
                    .DistinctBy(type => type.Id, StringComparer.Ordinal)
                    .OrderBy(type => type.Id, StringComparer.Ordinal)
                    .ToArray();
                MethodCatalog.ForwardedTypeEntry[] knownForwarders = this.m_loadedPartsByPath.Values.Append(part)
                    .SelectMany(item => item.Forwarders)
                    .ToArray();
                this.m_forwardedTargets.Clear();
                this.m_forwardersByAlias = MethodCatalog.IndexForwarders(knownForwarders);
                this.m_types = nextTypes;
                this.m_typesById = nextTypes.ToDictionary(type => type.Id, StringComparer.Ordinal);
                this.m_typesByManagedLocation = IndexManagedTypeLocations(nextTypes);
                this.m_typesByLogicalId = IndexLogicalTypes(nextTypes);
                this.m_logicalAliasesChanged = false;
                this.m_derivedTypesByBaseId = null;
                this.m_implementingTypesByInterfaceId = null;
                this.m_typeGeneration++;
                this.m_semanticGeneration++;

                this.m_loadedPartsByPath.Add(path, part);
                return part.Types.Select(type => this.m_typesByManagedLocation[
                    ManagedTypeLocation(path, type.MetadataToken)]).ToArray();
            }
        }

        // 合并真实定义与已经证明的转交别名，不再给每个类型复制别名列表。
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> IndexLogicalTypes(IReadOnlyList<TypeEntry> types)
        {
            return types.Select(type => (Name: type.LogicalId, Type: type))
                .Concat(this.m_forwardedTargets.Select(item => (Name: item.Key.TypeId, Type: item.Value)))
                .GroupBy(item => item.Name, StringComparer.Ordinal).ToDictionary(group => group.Key,
                    group => (IReadOnlyList<TypeEntry>)group.Select(item => item.Type).DistinctBy(type => type.Id)
                        .OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        }

        // 为函数总表与函数体读取共用同一套完整程序集身份定位规则。
        internal string ResolveAssemblyPath(
            System.Reflection.AssemblyName identity,
            string referringAssemblyPath)
        {
            IReadOnlyList<string> candidates = ReadAssemblyCandidates(identity);
            string[] localCandidates = candidates.Where(path => string.Equals(
                    Path.GetDirectoryName(path), Path.GetDirectoryName(referringAssemblyPath),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            return !this.m_usesUnityLegacyBinding && localCandidates.Length == 1 ? localCandidates[0] : candidates.Count switch
            {
                1 => candidates[0],
                0 => throw new AnalysisException($"托管程序集依赖不存在：{referringAssemblyPath} => {identity.FullName}"),
                _ => throw new AnalysisException($"托管程序集依赖不唯一：{referringAssemblyPath} => {identity.FullName}："
                    + string.Join("; ", candidates)),
            };
        }

        // 显式预载按元数据名、旁路探测按文件名枚举；仅已审计的 Unity 宿主不要求版本相等。
        private IReadOnlyList<string> ReadAssemblyCandidates(System.Reflection.AssemblyName identity)
        {
            return this.m_assemblyCandidates.GetOrAdd(identity.FullName!, _ =>
            {
                AssemblyRedirectTarget target = NormalizeAssemblyReference(identity);
                if (target.Path != null)
                {
                    return new[] { target.Path };
                }

                identity = target.Identity;
                string assemblyName = identity.Name
                    ?? throw new AnalysisException("程序集引用没有名称。");
                if (this.m_sourceContextsByAssembly.TryGetValue(
                        assemblyName,
                        out MethodCatalog.SourceCatalogContext? sourceContext)
                    && (this.m_usesUnityLegacyBinding || MethodCatalog.MatchesAssemblyIdentity(
                        new System.Reflection.AssemblyName(sourceContext.Material.Compilation.Assembly.Identity.GetDisplayName()),
                        identity)))
                {
                    if (!this.m_usesUnityLegacyBinding)
                    {
                        return new[] { Path.GetFullPath(sourceContext.Material.AssemblyPath) };
                    }
                }
                else
                {
                    sourceContext = null;
                }

                string[] candidates = this.m_lookupPathsByAssemblyName.TryGetValue(
                        assemblyName,
                        out IReadOnlyList<string>? knownPaths)
                    ? knownPaths.Where(path => this.m_usesUnityLegacyBinding || MethodCatalog.MatchesAssemblyIdentity(System.Reflection.AssemblyName.GetAssemblyName(path), identity)).ToArray()
                    : Array.Empty<string>();
                return (sourceContext == null ? candidates : candidates.Append(Path.GetFullPath(sourceContext.Material.AssemblyPath)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            });
        }

        // 在动态调用确实依赖类型层级时要求整条层级关系完整。
        /// <summary>
        /// 验证当前调用需要的基类和接口都能由真实材料定位。
        /// </summary>
        public void RequireClosedHierarchy(TypeEntry type, bool includeInterfaces = true)
        {
            ReadInheritedTypes(type, includeInterfaces: includeInterfaces);
        }

        // 补齐候选关系材料；多个真实载体均保留，实际调用再要求唯一实现。
        internal void RequireClosedDispatchIndex(bool includeInterfaces)
        {
            lock (this.m_loadedTypeLock)
            {
                if (!this.m_dispatchAssembliesClosed)
                {
                    Queue<MethodCatalog.ManagedAssemblyPart> pending = new(this.m_loadedPartsByPath.Values.OrderBy(part => part.Path, StringComparer.OrdinalIgnoreCase));
                    HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
                    while (pending.TryDequeue(out MethodCatalog.ManagedAssemblyPart? part))
                    {
                        if (!visited.Add(part.Path))
                        {
                            continue;
                        }
                        foreach (string reference in part.AssemblyReferences)
                        {
                            foreach (string candidate in ReadAssemblyCandidates(new System.Reflection.AssemblyName(reference)))
                            {
                                if (!this.m_loadedPartsByPath.ContainsKey(candidate))
                                {
                                    LoadAssemblyTypes(candidate);
                                }
                                if (!visited.Contains(candidate))
                                {
                                    pending.Enqueue(this.m_loadedPartsByPath[candidate]);
                                }
                            }
                        }
                    }
                    this.m_dispatchAssembliesClosed = true;
                }
                while (!this.m_dispatchGenerations.TryGetValue(includeInterfaces, out int completed)
                    || completed != this.m_typeGeneration)
                {
                    int generation = this.m_typeGeneration;
                    foreach (TypeEntry type in this.m_types)
                    {
                        IEnumerable<TypeRelationEntry> relations = includeInterfaces
                            ? type.Interfaces : Array.Empty<TypeRelationEntry>();
                        foreach (TypeRelationEntry relation in type.BaseType == null
                            ? relations : relations.Prepend(type.BaseType))
                        {
                            ResolveRelationDefinitionIds(relation, type);
                        }
                    }
                    if (generation == this.m_typeGeneration)
                    {
                        this.m_dispatchGenerations[includeInterfaces] = generation;
                    }
                }
            }
        }

        // 使用 Cecil 读取托管调用点的函数引用。
        internal BehaviorMethodReference ReadMethodReference(MethodEntry method)
        {
            return ReadManagedMethodReference((Cecil.MethodDefinition)GetManagedModule(method.AssemblyPath!)
                .LookupToken(method.MetadataToken), method.AssemblyPath!);
        }

        // 初始化事实仍使用同一函数体读取流程，不另写一套指令解释器。
        internal MethodBehavior ReadMethodBehavior(MethodEntry method)
        {
            Cecil.ModuleDefinition module = GetManagedModule(method.AssemblyPath!);
            lock (module)
            {
                return BehaviorReader.ReadManagedBehavior(module, this, method);
            }
        }

        // 反射字段查找直接读取已选定真实类型的元数据。
        internal IReadOnlyList<Cecil.FieldDefinition> GetFields(TypeEntry type)
        {
            return ((Cecil.TypeDefinition)GetManagedModule(type.AssemblyPath!)
                .LookupToken(type.MetadataToken)).Fields.ToArray();
        }

        // 反射属性直接读取已选定真实类型的属性表与访问函数。
        internal IReadOnlyList<Cecil.PropertyDefinition> GetProperties(TypeEntry type)
        {
            return ((Cecil.TypeDefinition)GetManagedModule(type.AssemblyPath!)
                .LookupToken(type.MetadataToken)).Properties.ToArray();
        }

        // 使用 Cecil 读取托管调用点的函数引用。
        internal BehaviorMethodReference ReadManagedMethodReference(
            Cecil.MethodReference method,
            string referringAssemblyPath)
        {
            MethodIdentityTemplate identity = MethodCatalog.ManagedMethodIdentity(
                method);
            TypeIdentityTemplate[] genericArguments = method is Cecil.GenericInstanceMethod generic
                ? generic.GenericArguments.Select(argument =>
                    MethodCatalog.ManagedTypeIdentity(argument)).ToArray()
                : Array.Empty<TypeIdentityTemplate>();
            return new BehaviorMethodReference(
                identity,
                MethodCatalog.ManagedNamedTypeDefinitionId(method.DeclaringType.GetElementType()),
                genericArguments.Select(type => type.Text).ToArray(),
                method.HasThis,
                MethodCatalog.ReadManagedAssemblyName(
                    method.DeclaringType.GetElementType(), fullName: true),
                referringAssemblyPath)
            {
                ReferenceMetadataToken = method.MetadataToken.ToInt32(),
                KnownDeclaringTypeId = ReadKnownTypeId(method.DeclaringType, referringAssemblyPath),
                KnownMetadataToken = method.GetElementMethod() is Cecil.MethodDefinition methodDefinition
                    ? methodDefinition.MetadataToken.ToInt32()
                    : null,
            };
        }

        // 使用 Cecil 读取托管字段引用。
        internal BehaviorMemberReference ReadManagedFieldReference(
            Cecil.FieldReference field,
            string referringAssemblyPath)
        {
            TypeIdentityTemplate[] declaringArguments = MethodCatalog.ReadManagedTypeArguments(
                field.DeclaringType).ToArray();
            TypeIdentityTemplate fieldType = MethodCatalog.ManagedTypeIdentity(
                field.FieldType).Substitute(declaringArguments);

            TypeIdentityTemplate declaringType = MethodCatalog.ManagedTypeIdentity(field.DeclaringType);
            return new BehaviorMemberReference(
                declaringType,
                MethodCatalog.ManagedNamedTypeDefinitionId(field.DeclaringType.GetElementType()),
                field.Name,
                fieldType.Text)
            {
                ReferenceMetadataToken = field.MetadataToken.ToInt32(),
                KnownDeclaringTypeId = ReadKnownTypeId(field.DeclaringType, referringAssemblyPath),
                TargetAssemblyIdentity = MethodCatalog.ReadManagedAssemblyName(
                    field.DeclaringType.GetElementType(), fullName: true),
                ReferringAssemblyPath = referringAssemblyPath,
            };
        }

        // 使用 Cecil 读取托管类型引用。
        internal BehaviorTypeReference ReadManagedTypeReference(
            Cecil.TypeReference type,
            string referringAssemblyPath)
        {
            TypeIdentityTemplate identity = MethodCatalog.ManagedTypeIdentity(type);
            Cecil.TypeReference definition = type.GetElementType();

            if (definition is Cecil.GenericParameter)
            {
                return new BehaviorTypeReference(identity, MethodCatalog.ManagedTypeIdentity(definition),
                    Array.Empty<TypeIdentityTemplate>(), null, referringAssemblyPath, null);
            }

            return new BehaviorTypeReference(
                identity,
                new TypeIdentityTemplate(MethodCatalog.ManagedNamedTypeDefinitionId(
                    definition)),
                MethodCatalog.ReadManagedTypeArguments(type).ToArray(),
                MethodCatalog.ReadManagedAssemblyName(definition, fullName: true),
                referringAssemblyPath,
                ReadKnownTypeId(type, referringAssemblyPath));
        }

        // 按当前文件和TypeDef标记取得源码回贴后的真实类型身份。
        private string? ReadKnownTypeId(Cecil.TypeReference type, string referringAssemblyPath)
        {
            if (type.GetElementType() is not Cecil.TypeDefinition definition)
            {
                return null;
            }

            string key = ManagedTypeLocation(referringAssemblyPath, definition.MetadataToken.ToInt32());
            lock (this.m_loadedTypeLock)
            {
                if (!this.m_typesByManagedLocation.TryGetValue(key, out TypeEntry? known)
                    || known.LogicalId != MethodCatalog.ManagedNamedTypeDefinitionId(
                        definition))
                {
                    throw new AnalysisException(
                        $"本地类型标记没有目录定义：{referringAssemblyPath} @ {definition.MetadataToken}");
                }

                return known.Id;
            }
        }

        // 读取一个托管类型直接声明的函数事实。
        private IReadOnlyList<MethodEntry> ReadManagedMethodDeclarations(TypeEntry type)
        {
            Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
            lock (module)
            {
                return MethodCatalog.ReadManagedTypeMethods(
                    type,
                    module);
            }
        }

        // 按类型读取真实方法实现表，保留接口构造实参，供显式优先和关系补全共用。
        internal IReadOnlyDictionary<string, ResolvedMethodDefinition[]> ReadExplicitMethodTargets(TypeEntry type, bool includeInterfaces = true)
        {
            return this.m_explicitTargetsByTypeId.GetOrAdd(type.Id + "|" + includeInterfaces, _ =>
            {
                Dictionary<string, ResolvedMethodDefinition[]> result = new(StringComparer.Ordinal);
                Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
                HashSet<string>? classOwners = includeInterfaces ? null
                    : ReadInheritedTypes(type, includeInterfaces: false).Select(relation => relation.Definition.Id).ToHashSet(StringComparer.Ordinal);
                foreach (MethodEntry method in GetMethods(type))
                {
                    Cecil.MethodReference[] overrides;
                    lock (module)
                    {
                        overrides = ((Cecil.MethodDefinition)module.LookupToken(method.MetadataToken)).Overrides.ToArray();
                    }
                    if (classOwners != null)
                    {
                        overrides = overrides.Where(target =>
                        {
                            string? known = ReadKnownTypeId(target.DeclaringType, type.AssemblyPath!);
                            return known != null ? classOwners.Contains(known)
                                : FindTypeDefinitions(MethodCatalog.ManagedNamedTypeDefinitionId(target.DeclaringType.GetElementType()),
                                    MethodCatalog.ReadManagedAssemblyName(target.DeclaringType.GetElementType(), fullName: true), type.AssemblyPath, loadMissing: false)
                                    .Any(owner => classOwners.Contains(owner.Id));
                        }).ToArray();
                    }

                    if (overrides.Length != 0)
                    {
                        result.Add(method.Id, overrides.Select(target => ResolveMethodDefinition(
                            ReadManagedMethodReference(target, method.AssemblyPath!), searchInherited: false)).ToArray());
                    }
                }

                return result;
            });
        }

        // 列出一个类型直接或间接继承的全部构造基类和接口。
        internal IReadOnlyList<InheritedTypeRelation> ReadInheritedTypes(
            TypeEntry type,
            IReadOnlyList<TypeIdentityTemplate>? typeArguments = null,
            bool includeInterfaces = true)
        {
            bool reuse = this.m_dispatchAssembliesClosed;
            var query = (type.Id, string.Concat((typeArguments ?? Array.Empty<TypeIdentityTemplate>())
                .Select(argument => $"{argument.Text.Length}:{argument.Text}")), includeInterfaces);
            if (reuse && this.m_inheritedTypes.TryGetValue(query, out IReadOnlyList<InheritedTypeRelation>? known))
            {
                return known;
            }
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<InheritedTypeRelation> result = new();

            PushRelations(pending, type, 1, canImplementInterface: true, typeArguments, includeInterfaces);
            while (pending.Count > 0)
            {
                (TypeRelationEntry relation, TypeEntry owner, bool isInterface,
                    bool canImplementInterface, int depth) = pending.Pop();
                IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(relation, owner, loadMissing: true);
                if (definitions.Count == 0)
                {
                    throw new AnalysisException($"类型层级无法闭合：{owner.Id} => {relation.DefinitionId}，来源：{owner.AssemblyPath}");
                }
                foreach (TypeEntry definition in definitions)
                {
                    string key = $"{definition.Id}|{relation.TypeId}|{isInterface}";
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    TypeIdentityTemplate[] arguments = relation.TypeArguments
                        .Select(value => new TypeIdentityTemplate(value))
                        .ToArray();
                    result.Add(new InheritedTypeRelation(
                        definition,
                        arguments,
                        isInterface,
                        canImplementInterface,
                        depth));
                    PushRelations(
                        pending,
                        definition,
                        depth + 1,
                        canImplementInterface,
                        arguments,
                        includeInterfaces);
                }
            }

            if (reuse)
            {
                this.m_inheritedTypes.TryAdd(query, result);
            }
            return result;
        }

        // 把一个类型的直接基类和接口放入继承遍历栈。
        private void PushRelations(
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending,
            TypeEntry type,
            int depth,
            bool canImplementInterface,
            IReadOnlyList<TypeIdentityTemplate>? ownerArguments,
            bool includeInterfaces)
        {
            if (type.BaseType != null)
            {
                pending.Push((
                    InstantiateRelation(type.BaseType, type, ownerArguments),
                    type,
                    false,
                    false,
                    depth));
            }

            foreach (TypeRelationEntry relation in includeInterfaces ? type.Interfaces.Reverse() : Array.Empty<TypeRelationEntry>())
            {
                pending.Push((
                    InstantiateRelation(relation, type, ownerArguments),
                    type,
                    true,
                    canImplementInterface,
                    depth));
            }
        }

        // 用当前构造类型实参实例化一条继承关系。
        private TypeRelationEntry InstantiateRelation(
            TypeRelationEntry relation,
            TypeEntry owner,
            IReadOnlyList<TypeIdentityTemplate>? ownerArguments)
        {
            if (relation.TypeArguments.Count == 0)
            {
                return relation;
            }

            TypeIdentityTemplate[] arguments = ReadResolvedTypeArguments(owner.AssemblyPath!, relation.ReferenceMetadataToken)
                .Select(argument => argument.Substitute(ownerArguments ?? Array.Empty<TypeIdentityTemplate>()))
                .ToArray();

            return relation with
            {
                TypeId = relation.DefinitionId + "<" + string.Join(",", arguments.Select(argument => argument.Text)) + ">",
                TypeArguments = arguments.Select(argument => argument.Text).ToArray(),
            };
        }

        // 按继承声明的完整程序集身份定位类型定义。
        private IReadOnlyList<TypeEntry> FindTypeDefinitions(
            TypeRelationEntry relation,
            TypeEntry owner,
            bool loadMissing = false)
        {
            if (relation.KnownMetadataToken is int metadataToken)
            {
                if (owner.AssemblyPath == null)
                {
                    throw new AnalysisException($"本地类型关系没有程序集路径：{owner.Id}");
                }

                string key = ManagedTypeLocation(owner.AssemblyPath, metadataToken);
                lock (this.m_loadedTypeLock)
                {
                    return this.m_typesByManagedLocation.TryGetValue(key, out TypeEntry? known)
                        ? new[] { known }
                        : throw new AnalysisException(
                            $"本地类型关系没有目录定义：{owner.Id} @ {metadataToken}");
                }
            }

            return FindTypeDefinitions(
                relation.DefinitionId,
                relation.AssemblyIdentity,
                owner.AssemblyPath,
                loadMissing);
        }

        // 按开放类型身份和完整程序集身份定位唯一定义。
        private IReadOnlyList<TypeEntry> FindTypeDefinitions(
            string definitionId, string assemblyIdentity, string? referringAssemblyPath, bool loadMissing)
        {
            string identity = new System.Reflection.AssemblyName(assemblyIdentity).FullName!;
            lock (this.m_loadedTypeLock)
            {
                MethodCatalog.ForwardedTypeKey key = MethodCatalog.CreateForwardedTypeKey(definitionId, identity);
                if (this.m_forwardedTargets.TryGetValue(key, out TypeEntry? cached))
                {
                    return new[] { cached };
                }
                Dictionary<MethodCatalog.ForwardedTypeKey, TypeEntry?> resolved = new();
                TypeEntry? result = MethodCatalog.ResolveForwardedTarget(definitionId, identity, referringAssemblyPath,
                    (typeId, fullName, path) => ReadForwardedTypeNode(typeId, fullName, path, loadMissing),
                    resolved,
                    new HashSet<MethodCatalog.ForwardedTypeKey>(), loadMissing && referringAssemblyPath != null);
                foreach (var item in resolved.Where(item => item.Value != null))
                {
                    if (!this.m_forwardedTargets.ContainsKey(item.Key) && item.Key.TypeId != item.Value!.LogicalId)
                    {
                        this.m_semanticGeneration++;
                    }
                    this.m_forwardedTargets[item.Key] = item.Value!;
                    this.m_logicalAliasesChanged |= item.Key.TypeId != item.Value!.LogicalId;
                }
                return result == null ? Array.Empty<TypeEntry>() : new[] { result };
            }
        }

        // 必要时加载当前节点所在程序集，终点与循环仍由共同求解过程判断。
        private MethodCatalog.ForwardedTypeNode ReadForwardedTypeNode(
            string definitionId, string assemblyIdentity, string? referringPath, bool loadMissing, bool potential = false)
        {
            System.Reflection.AssemblyName identity = NormalizeAssemblyReference(new System.Reflection.AssemblyName(assemblyIdentity)).Identity;
            IReadOnlyList<string> candidates = ReadAssemblyCandidates(new System.Reflection.AssemblyName(assemblyIdentity));
            if (loadMissing)
            {
                foreach (string candidate in candidates.Where(path => !this.m_loadedPartsByPath.ContainsKey(path)))
                {
                    LoadAssemblyTypes(candidate);
                }
            }
            MethodCatalog.ForwardedTypeKey key = MethodCatalog.CreateForwardedTypeKey(definitionId, identity.FullName!);
            // 文件名请求选定物理载体后，类型名称在该载体内查找，不再按其元数据名重新选择程序集。
            string ReadActualTypeId(string path)
            {
                if (!this.m_usesUnityLegacyBinding)
                {
                    return definitionId;
                }
                string requested = new System.Reflection.AssemblyName(assemblyIdentity).Name!.ToUpperInvariant();
                string actual = GetManagedModule(path).Assembly.Name.Name.ToUpperInvariant();
                return $"A{actual.Length}:{actual}" + definitionId[$"A{requested.Length}:{requested}".Length..];
            }
            TypeEntry[] definitions = candidates.SelectMany(path =>
                (this.m_typesByLogicalId.GetValueOrDefault(ReadActualTypeId(path)) ?? Array.Empty<TypeEntry>())
                    .Where(type => string.Equals(type.AssemblyPath, path, StringComparison.OrdinalIgnoreCase))).ToArray();
            MethodCatalog.ForwardedTypeEntry[] forwarders = candidates.Count == 0
                ? this.m_forwardersByAlias.GetValueOrDefault(key) ?? Array.Empty<MethodCatalog.ForwardedTypeEntry>()
                : candidates.SelectMany(path => this.m_forwardersByAlias.GetValueOrDefault(MethodCatalog.CreateForwardedTypeKey(
                        ReadActualTypeId(path), GetManagedModule(path).Assembly.Name.FullName)) ?? Array.Empty<MethodCatalog.ForwardedTypeEntry>())
                    .Where(item => candidates.Contains(item.AssemblyPath, StringComparer.OrdinalIgnoreCase)).Distinct().ToArray();
            if (!potential && candidates.Count > 1 && candidates.Any(path =>
                !definitions.Any(type => string.Equals(type.AssemblyPath, path, StringComparison.OrdinalIgnoreCase))
                && !forwarders.Any(item => string.Equals(item.AssemblyPath, path, StringComparison.OrdinalIgnoreCase))))
            {
                throw new AnalysisException($"类型在不同运行载体中的存在性不一致：{definitionId}，来源：{referringPath}；"
                    + string.Join("; ", candidates));
            }
            if (definitions.Length == 0 && forwarders.Length == 0 && loadMissing && referringPath != null)
            {
                string path = ResolveAssemblyPath(identity, referringPath);
                throw new AnalysisException($"实际载体没有请求的类型：{definitionId}；{referringPath} => {path}");
            }

            return new MethodCatalog.ForwardedTypeNode(definitions, forwarders);
        }

        // 为一个真实文件只建立一份 Cecil 模块并协调并行首次读取。
        private Cecil.ModuleDefinition GetManagedModule(string path)
        {
            lock (this.m_moduleLock)
            {
                if (!this.m_modulesByPath.TryGetValue(path, out Cecil.ModuleDefinition? module))
                {
                    module = this.m_sourceImagesByPath.TryGetValue(path, out byte[]? image)
                        ? MethodCatalog.OpenModule(image)
                        : MethodCatalog.OpenModule(path);
                    this.m_modulesByPath.Add(path, module);
                }

                return module;
            }
        }

        // 保留一条继承声明可能指向的全部物理类型，不把候选当成执行结论。
        private IReadOnlyList<string> ResolveRelationDefinitionIds(
            TypeRelationEntry relation,
            TypeEntry owner)
        {
            if (relation.KnownMetadataToken != null)
            {
                return FindTypeDefinitions(relation, owner).Select(type => type.Id).ToArray();
            }
            HashSet<string> result = new(StringComparer.Ordinal);
            ReadPotentialRelation(relation.DefinitionId, relation.AssemblyIdentity, owner.AssemblyPath!,
                new HashSet<MethodCatalog.ForwardedTypeKey>(), result);
            return result.Order(StringComparer.Ordinal).ToArray();
        }

        // 沿真实转交记录收集候选；同文件按名转交覆盖本地同名定义。
        private void ReadPotentialRelation(string typeId, string assemblyIdentity, string referringPath,
            HashSet<MethodCatalog.ForwardedTypeKey> visiting, HashSet<string> result)
        {
            AssemblyRedirectTarget target = NormalizeAssemblyReference(new System.Reflection.AssemblyName(assemblyIdentity));
            MethodCatalog.ForwardedTypeKey key = MethodCatalog.CreateForwardedTypeKey(typeId, target.Identity.FullName!);
            if (!visiting.Add(key))
            {
                throw new AnalysisException($"类型转交循环：{typeId} @ {assemblyIdentity}，来源：{referringPath}");
            }
            MethodCatalog.ForwardedTypeNode node = ReadForwardedTypeNode(typeId, assemblyIdentity, referringPath, true, potential: true);
            TypeEntry[] definitions = node.Definitions.Where(type => target.Path == null
                || string.Equals(type.AssemblyPath, target.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            MethodCatalog.ForwardedTypeEntry[] forwarders = node.Forwarders.Where(item => target.Path == null
                || string.Equals(item.AssemblyPath, target.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (definitions.Length == 0 && forwarders.Length == 0)
            {
                throw new AnalysisException($"类型层级无法闭合：{typeId} @ {assemblyIdentity}，来源：{referringPath}");
            }
            foreach (TypeEntry definition in definitions.Where(type => !forwarders.Any(item =>
                string.Equals(item.AssemblyPath, type.AssemblyPath, StringComparison.OrdinalIgnoreCase))))
            {
                result.Add(definition.Id);
            }
            foreach (MethodCatalog.ForwardedTypeEntry forwarder in forwarders)
            {
                ReadPotentialRelation(forwarder.TargetTypeId, forwarder.TargetAssemblyIdentity,
                    forwarder.AssemblyPath, visiting, result);
            }
            visiting.Remove(key);
        }

        // 按每个项目声明的多个键建立固定顺序的反向索引。
        private static IReadOnlyDictionary<string, IReadOnlyList<T>> IndexMany<T>(
            IEnumerable<T> items,
            Func<T, IEnumerable<string>> keys,
            Func<T, string> order)
            where T : class
        {
            return items.SelectMany(item => keys(item)
                    .Distinct(StringComparer.Ordinal)
                    .Select(key => (Key: key, Item: item)))
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<T>)group.Select(pair => pair.Item)
                        .OrderBy(order, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        /// <summary>保存一个已经归一到实际运行文件的程序集身份。</summary>
        private sealed record AssemblyRedirectTarget(
            System.Reflection.AssemblyName Identity,
            string? Path);

        /// <summary>保存一条已经闭合到类型定义的继承关系。</summary>
        internal sealed record InheritedTypeRelation(
            TypeEntry Definition,
            IReadOnlyList<TypeIdentityTemplate> TypeArguments,
            bool IsInterface,
            bool CanImplementInterface,
            int Depth);
    }
}
