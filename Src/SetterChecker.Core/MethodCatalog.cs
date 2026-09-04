using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SetterChecker.Core
{
    /// <summary>
    /// 从源码编译内容和真实托管文件建立统一函数总表。
    /// </summary>
    public sealed class MethodCatalog
    {
        private static readonly SymbolDisplayFormat s_typeDisplayFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);
        // 并行读取所有源码程序集和真实托管文件后建立稳定索引。
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
            IReadOnlyDictionary<string, string> assemblyNames = ReadAssemblyNames(material, jobs);
            SourceCatalogContext[] sourceContexts = material.SourceAssemblies
                .Select(assembly => new SourceCatalogContext(
                    assembly,
                    EnumerateTypes(assembly.Compilation.Assembly.GlobalNamespace).ToArray(),
                    assembly.ReportSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                    assemblyNames))
                .ToArray();
            IReadOnlyDictionary<string, string> callingConventionTypes = ReadCallingConventionTypes(
                sourceContexts);
            CatalogTypeWorkItem[] typeWorkItems = sourceContexts
                .SelectMany(context => context.Types.Select(type =>
                    new CatalogTypeWorkItem(context, type, null)))
                .Concat(material.ExternalAssemblies
                    .SelectMany(assembly => assembly.ImplementationPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new CatalogTypeWorkItem(null, null, path)))
                .ToArray();
            ConcurrentBag<CatalogPart> typeParts = new();

            await Parallel.ForEachAsync(
                typeWorkItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (workItem, _) =>
                {
                    CatalogPart part = workItem.SourceType != null
                        ? new CatalogPart(
                            new[] { CreateSourceType(workItem.SourceType, workItem.Context!) },
                            null)
                        : ReadManagedAssemblyTypes(
                            workItem.AssemblyPath!,
                            callingConventionTypes,
                            assemblyNames);

                    typeParts.Add(part);

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            TypeEntry[] allTypes = typeParts.SelectMany(part => part.Types).ToArray();
            IReadOnlyDictionary<string, IReadOnlyList<string>> managedTypeIdsByNameKey = allTypes
                .Where(type => type.AssemblyPath != null)
                .GroupBy(type => type.NameKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .Select(type => type.LogicalId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
            sourceContexts = sourceContexts
                .Select(context => context with
                {
                    ManagedTypeIdsByNameKey = managedTypeIdsByNameKey,
                })
                .ToArray();
            IReadOnlyDictionary<string, TypeEntry> sourceTypesById = allTypes
                .Where(type => type.SourceSymbol != null)
                .ToDictionary(type => type.Id, StringComparer.Ordinal);
            CatalogMethodWorkItem[] methodWorkItems = sourceContexts
                .SelectMany(context => context.Types.Select(type =>
                    new CatalogMethodWorkItem(context, type, null))
                    .Concat(context.Material.Compilation.SyntaxTrees.Select(tree =>
                        new CatalogMethodWorkItem(context, null, tree))))
                .ToArray();
            ConcurrentBag<IReadOnlyList<MethodEntry>> methodParts = new();

            await Parallel.ForEachAsync(
                methodWorkItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (workItem, _) =>
                {
                    IReadOnlyList<MethodEntry> methods = workItem.SourceType != null
                        ? ReadSourceTypeMethods(
                            workItem.Context,
                            workItem.SourceType,
                            sourceTypesById)
                        : ReadSourceNestedMethods(
                            workItem.Context,
                            workItem.SyntaxTree!,
                            sourceTypesById);

                    methodParts.Add(methods);

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            MethodEntry[] allMethods = methodParts.SelectMany(methods => methods).ToArray();
            AssemblyTypeMap[] loadedMaps = typeParts
                .Where(part => part.AssemblyMap != null)
                .Select(part => part.AssemblyMap!)
                .ToArray();
            allTypes = AttachTypeAliases(
                material.ExternalAssemblies,
                allTypes,
                loadedMaps,
                callingConventionTypes,
                assemblyNames,
                jobs);

            RequireUniqueMethods(allMethods);
            RequireUniqueTypes(allTypes);
            MethodEntry[] orderedMethods = allMethods
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
            TypeEntry[] orderedTypes = allTypes
                .OrderBy(type => type.Id, StringComparer.Ordinal)
                .ToArray();

            return new MethodCatalogResult(
                orderedMethods,
                orderedTypes,
                callingConventionTypes,
                assemblyNames,
                stopwatch,
                jobs);
        }

        // 读取源码和托管文件中的真实程序集名称并建立大小写无关的唯一名称表。
        private static IReadOnlyDictionary<string, string> ReadAssemblyNames(
            MaterialSet material,
            int jobs)
        {
            string[] implementationPaths = material.ExternalAssemblies
                .SelectMany(assembly => assembly.ImplementationPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] allPaths = implementationPaths
                .Concat(material.ExternalAssemblies.Select(assembly => assembly.ReferencePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ConcurrentBag<AssemblyNameInfo> fileNames = new();
            Parallel.ForEach(
                allPaths,
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                path => fileNames.Add(ReadAssemblyNameInfo(path)));
            IReadOnlySet<string> implementationPathSet = implementationPaths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] actualNames = material.SourceAssemblies
                .Select(assembly => assembly.Compilation.Assembly.Identity.Name)
                .Concat(fileNames
                    .Where(info => implementationPathSet.Contains(info.Path))
                    .Select(info => info.DefinitionName))
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] referencedNames = material.SourceAssemblies
                .SelectMany(assembly => assembly.Compilation.References
                    .Select(reference => assembly.Compilation.GetAssemblyOrModuleSymbol(reference)))
                .OfType<IAssemblySymbol>()
                .Select(symbol => symbol.Identity.Name)
                .Concat(fileNames.Select(info => info.DefinitionName))
                .Concat(fileNames.SelectMany(info => info.ReferenceNames))
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

            foreach (string name in actualNames.Concat(referencedNames))
            {
                result.TryAdd(name, name);
            }

            return result;
        }

        // 从一个托管文件读取程序集定义名称和全部直接引用名称。
        private static AssemblyNameInfo ReadAssemblyNameInfo(string assemblyPath)
        {
            string fullPath = Path.GetFullPath(assemblyPath);
            using FileStream stream = File.OpenRead(fullPath);
            using PEReader portableExecutable = new(stream);
            MetadataReader reader = portableExecutable.GetMetadataReader();

            return new AssemblyNameInfo(
                fullPath,
                reader.GetString(reader.GetAssemblyDefinition().Name),
                reader.AssemblyReferences
                    .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }

        // 返回程序集实际定义采用的名称大小写。
        private static string CanonicalAssemblyName(
            IReadOnlyDictionary<string, string> assemblyNames,
            string assemblyName)
        {
            return assemblyNames.TryGetValue(assemblyName, out string? canonicalName)
                ? canonicalName
                : throw new AnalysisException($"程序集名称没有进入材料总表：{assemblyName}");
        }

        // 拒绝会让调用目标无法唯一定位的重复函数身份。
        private static void RequireUniqueMethods(IEnumerable<MethodEntry> methods)
        {
            IGrouping<string, MethodEntry>? duplicate = methods
                .GroupBy(method => method.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Skip(1).Any());

            if (duplicate != null)
            {
                string sources = string.Join("; ", duplicate.Select(method =>
                    $"{method.SourcePath ?? method.AssemblyPath}:{method.Line}:{method.MetadataToken}"));

                throw new AnalysisException($"函数身份不唯一：{duplicate.Key} => {sources}");
            }
        }

        // 拒绝会让继承关系无法唯一定位的重复类型身份。
        private static void RequireUniqueTypes(IEnumerable<TypeEntry> types)
        {
            IGrouping<string, TypeEntry>? duplicate = types
                .GroupBy(type => type.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Skip(1).Any());

            if (duplicate != null)
            {
                string sources = string.Join("; ", duplicate.Select(type =>
                    $"{type.SourceSymbol?.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? type.AssemblyPath}:"
                    + $"{type.MetadataToken}"));

                throw new AnalysisException($"类型身份不唯一：{duplicate.Key} => {sources}");
            }
        }

        // 读取一个源码类型直接声明的全部成员函数。
        private static IReadOnlyList<MethodEntry> ReadSourceTypeMethods(
            SourceCatalogContext context,
            INamedTypeSymbol type,
            IReadOnlyDictionary<string, TypeEntry> typesById)
        {
            List<MethodEntry> methods = new();
            TypeEntry typeEntry = typesById[NamedTypeDefinitionId(
                type.OriginalDefinition,
                context)];
            IReadOnlyDictionary<IMethodSymbol, MethodIdentityTemplate[]> interfaceTargets =
                ReadSourceInterfaceTargets(type, context);

            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                IMethodSymbol definition = NormalizeMethod(method);
                methods.Add(CreateSourceMethod(
                    method,
                    typeEntry,
                    context,
                    context.Material.IsReportAssembly,
                    interfaceTargets.GetValueOrDefault(definition)
                        ?? Array.Empty<MethodIdentityTemplate>()));
            }

            return methods;
        }

        // 读取一个源码文件中的局部函数和匿名函数。
        private static IReadOnlyList<MethodEntry> ReadSourceNestedMethods(
            SourceCatalogContext context,
            SyntaxTree tree,
            IReadOnlyDictionary<string, TypeEntry> typesById)
        {
            List<MethodEntry> methods = new();
            SyntaxNode root = tree.GetRoot();
            SyntaxNode[] nestedFunctions = root.DescendantNodes()
                .Where(node => node is LocalFunctionStatementSyntax
                    or AnonymousFunctionExpressionSyntax)
                .ToArray();

            if (nestedFunctions.Length == 0)
            {
                return methods;
            }

            SemanticModel model = context.Material.Compilation.GetSemanticModel(tree, true);
            foreach (SyntaxNode declaration in nestedFunctions)
            {
                IMethodSymbol? symbol = declaration switch
                {
                    LocalFunctionStatementSyntax local =>
                        model.GetDeclaredSymbol(local) as IMethodSymbol,
                    AnonymousFunctionExpressionSyntax anonymous =>
                        (model.GetOperation(anonymous) as IAnonymousFunctionOperation)?.Symbol,
                    _ => null,
                };

                if (symbol != null)
                {
                    methods.Add(CreateSourceMethod(
                        symbol,
                        typesById[NamedTypeDefinitionId(
                            symbol.ContainingType.OriginalDefinition,
                            context)],
                        context,
                        false,
                        Array.Empty<MethodIdentityTemplate>()));
                }
            }

            return methods;
        }

        // 递归列出当前源码程序集声明的全部命名类型。
        private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol rootNamespace)
        {
            foreach (INamespaceOrTypeSymbol member in rootNamespace.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    foreach (INamedTypeSymbol type in EnumerateTypes(childNamespace))
                    {
                        yield return type;
                    }
                }
                else if (member is INamedTypeSymbol type)
                {
                    foreach (INamedTypeSymbol nested in EnumerateTypes(type))
                    {
                        yield return nested;
                    }
                }
            }
        }

        // 从源码实际引用的核心库和类型转交文件取得全部合法调用约定类型身份。
        private static IReadOnlyDictionary<string, string> ReadCallingConventionTypes(
            IReadOnlyList<SourceCatalogContext> contexts)
        {
            if (contexts.Count == 0)
            {
                throw new AnalysisException("无法从源码编译内容确定核心库。");
            }

            (SourceCatalogContext Context, IAssemblySymbol CoreLibrary)[] coreLibraries = contexts
                .Select(context => (
                    Context: context,
                    ObjectType: context.Material.Compilation.GetSpecialType(
                        SpecialType.System_Object)))
                .Where(item => item.ObjectType.TypeKind != TypeKind.Error)
                .Select(item => (item.Context, item.ObjectType.ContainingAssembly))
                .ToArray();
            if (coreLibraries.Length == 0)
            {
                throw new AnalysisException("源码编译内容没有可解析的 System.Object 核心库定义。");
            }
            Dictionary<string, string> result = new(StringComparer.Ordinal);

            foreach ((SourceCatalogContext context, IAssemblySymbol currentCoreLibrary) in coreLibraries)
            {
                INamespaceSymbol? systemNamespace = currentCoreLibrary.GlobalNamespace
                    .GetNamespaceMembers()
                    .SingleOrDefault(item => item.Name == "System");
                INamespaceSymbol? runtimeNamespace = systemNamespace?
                    .GetNamespaceMembers()
                    .SingleOrDefault(item => item.Name == "Runtime");
                INamespaceSymbol? compilerServicesNamespace = runtimeNamespace?
                    .GetNamespaceMembers()
                    .SingleOrDefault(item => item.Name == "CompilerServices");
                INamedTypeSymbol[] conventionTypes = compilerServicesNamespace?
                    .GetTypeMembers()
                    .Where(type => type.Arity == 0
                        && type.Name != "CallConv"
                        && type.Name.StartsWith("CallConv", StringComparison.Ordinal))
                    .OrderBy(MetadataTypeName, StringComparer.Ordinal)
                    .ToArray()
                    ?? Array.Empty<INamedTypeSymbol>();
                foreach (INamedTypeSymbol conventionType in conventionTypes)
                {
                    string nameKey = MetadataTypeNameKey(conventionType);
                    string canonicalTypeId = CallingConventionCanonicalTypeId(nameKey);
                    AddCallingConventionType(
                        result,
                        CallingConventionTypeKey(
                            CanonicalAssemblyName(
                                context.AssemblyNames,
                                currentCoreLibrary.Identity.Name),
                            nameKey),
                        canonicalTypeId);
                }

                foreach (MetadataReference reference in context.Material.Compilation.References)
                {
                    if (context.Material.Compilation.GetAssemblyOrModuleSymbol(reference)
                        is not IAssemblySymbol referencedAssembly)
                    {
                        continue;
                    }

                    foreach (INamedTypeSymbol conventionType in conventionTypes)
                    {
                        string fullName = MetadataTypeName(conventionType);
                        INamedTypeSymbol? resolvedType = referencedAssembly.GetTypeByMetadataName(
                            fullName)
                            ?? referencedAssembly.ResolveForwardedType(fullName);
                        if (resolvedType != null
                            && SymbolEqualityComparer.Default.Equals(
                                resolvedType.ContainingAssembly,
                                currentCoreLibrary))
                        {
                            AddCallingConventionType(
                                result,
                                CallingConventionTypeKey(
                                    CanonicalAssemblyName(
                                        context.AssemblyNames,
                                        referencedAssembly.Identity.Name),
                                    MetadataTypeNameKey(conventionType)),
                                CallingConventionCanonicalTypeId(
                                    MetadataTypeNameKey(conventionType)));
                        }
                    }
                }
            }

            return result;
        }

        // 保存一个调用约定别名到唯一真实定义的映射并拒绝歧义。
        private static void AddCallingConventionType(
            IDictionary<string, string> types,
            string alias,
            string canonicalTypeId)
        {
            if (types.TryGetValue(alias, out string? existingTypeId))
            {
                if (!string.Equals(existingTypeId, canonicalTypeId, StringComparison.Ordinal))
                {
                    throw new AnalysisException(
                        $"调用约定类型转交目标不唯一：{alias} => {existingTypeId}; {canonicalTypeId}");
                }

                return;
            }

            types.Add(alias, canonicalTypeId);
        }

        // 生成程序集名称不区分大小写而类型名称区分大小写的调用约定查找键。
        private static string CallingConventionTypeKey(string assemblyName, string nameKey)
        {
            return NamedTypeId(assemblyName, nameKey);
        }

        // 为已经证明合法的 CLR 调用约定类型生成跨核心库门面的统一身份。
        private static string CallingConventionCanonicalTypeId(string nameKey)
        {
            return $"callconv:{nameKey}";
        }

        // 递归列出一个类型及其内部类型。
        private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamedTypeSymbol type)
        {
            yield return type;

            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            {
                foreach (INamedTypeSymbol item in EnumerateTypes(nested))
                {
                    yield return item;
                }
            }
        }

        // 把源码类型转换为统一类型记录。
        private static TypeEntry CreateSourceType(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            INamedTypeSymbol definition = type.OriginalDefinition;
            string typeId = NamedTypeDefinitionId(definition, context);
            TypeRelationEntry? baseType = definition.BaseType == null
                ? null
                : CreateSourceTypeRelation(definition.BaseType, context);

            return new TypeEntry(
                typeId,
                typeId,
                Array.Empty<string>(),
                CanonicalAssemblyName(
                    context.AssemblyNames,
                    definition.ContainingAssembly.Identity.Name),
                definition.Name,
                DisplayType(definition, context),
                MetadataTypeNameKey(definition),
                baseType,
                definition.Interfaces
                    .Select(type => CreateSourceTypeRelation(type, context))
                    .DistinctBy(relation => relation.TypeId, StringComparer.Ordinal)
                    .OrderBy(relation => relation.TypeId, StringComparer.Ordinal)
                    .ToArray(),
                ReadSourceAttributes(definition.GetAttributes()),
                definition.TypeKind == TypeKind.Interface,
                definition,
                null,
                0);
        }

        // 把源码中的基类或接口类型保存为无需反解析的关系记录。
        private static TypeRelationEntry CreateSourceTypeRelation(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            CatalogType[] arguments = ReadNamedTypeArguments(type, context).ToArray();
            TypeIdentityTemplate typeIdentity = BuildNamedTypeIdentity(
                type,
                arguments,
                useMatchIdentity: false,
                context);
            TypeIdentityTemplate matchTypeIdentity = BuildNamedTypeIdentity(
                type,
                arguments,
                useMatchIdentity: true,
                context);

            return new TypeRelationEntry(
                NamedTypeDefinitionId(type.OriginalDefinition, context),
                typeIdentity.Text,
                matchTypeIdentity.Text,
                arguments.Select(argument => argument.Id).ToArray(),
                arguments.Select(argument => argument.MatchId).ToArray())
            {
                TypeIdentity = typeIdentity,
                MatchTypeIdentity = matchTypeIdentity,
                TypeArgumentIdentities = arguments.Select(argument => argument.TypeIdentity).ToArray(),
                MatchTypeArgumentIdentities = arguments
                    .Select(argument => argument.MatchTypeIdentity)
                    .ToArray(),
            };
        }

        // 把源码函数转换为统一函数记录。
        private static MethodEntry CreateSourceMethod(
            IMethodSymbol method,
            TypeEntry type,
            SourceCatalogContext context,
            bool isReportAssembly,
            IReadOnlyList<MethodIdentityTemplate> interfaceTargets)
        {
            IMethodSymbol definition = NormalizeMethod(method);
            Location? location = definition.Locations.FirstOrDefault(item => item.IsInSource);
            string? sourcePath = location?.SourceTree?.FilePath;
            int line = location == null
                ? 0
                : location.GetLineSpan().StartLinePosition.Line + 1;
            ParameterEntry[] parameters = definition.Parameters.IsEmpty
                ? Array.Empty<ParameterEntry>()
                : definition.Parameters.Select(parameter =>
                    CreateSourceParameter(parameter, context)).ToArray();
            CatalogType returnType = SourceReturnType(definition, context);
            CatalogMethodKind kind = GetSourceMethodKind(definition);
            MethodIdentityTemplate identity = new(
                TypeIdentityTemplate.NamedType(type.Id),
                definition.MetadataName,
                definition.Arity,
                parameters.Select(parameter => parameter.TypeIdentity).ToArray(),
                returnType.TypeIdentity);
            string id = identity.Text;

            if (kind is CatalogMethodKind.LocalFunction or CatalogMethodKind.AnonymousFunction)
            {
                id = $"{id}@{sourcePath}:{location?.SourceSpan.Start ?? 0}";
            }
            MethodIdentityTemplate[] relatedMethodIdentities = ReadSourceRelations(
                definition,
                context,
                interfaceTargets);
            string[] relatedMethodIds = relatedMethodIdentities
                .Select(item => item.Text)
                .ToArray();

            return new MethodEntry(
                id,
                id,
                CanonicalAssemblyName(
                    context.AssemblyNames,
                    definition.ContainingAssembly.Identity.Name),
                type.Id,
                type.FullName,
                definition.MetadataName,
                returnType.Name,
                returnType.Id,
                returnType.MatchId,
                parameters,
                kind,
                ReadSourceAttributes(definition.GetAttributes()),
                type.Attributes,
                relatedMethodIds,
                sourcePath,
                line,
                IsReportable(
                    definition,
                    kind,
                    sourcePath,
                    isReportAssembly,
                    context.ReportablePaths),
                definition.IsAbstract,
                definition.DeclaredAccessibility == Accessibility.Public,
                definition.IsStatic,
                definition.IsVirtual || definition.IsAbstract || definition.IsOverride,
                (definition.IsVirtual || definition.IsAbstract) && !definition.IsOverride,
                definition.Arity,
                definition,
                null,
                0)
            {
                ReturnTypeIdentity = returnType.TypeIdentity,
                ReturnMatchTypeIdentity = returnType.MatchTypeIdentity,
                Identity = identity,
                RelatedMethodIdentities = relatedMethodIdentities,
            };
        }

        // 找出当前源码类型直接提供的全部接口函数实现目标。
        private static IReadOnlyDictionary<IMethodSymbol, MethodIdentityTemplate[]>
            ReadSourceInterfaceTargets(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            Dictionary<IMethodSymbol, List<MethodIdentityTemplate>> targets = new(
                SymbolEqualityComparer.Default);

            foreach (IMethodSymbol contract in type.AllInterfaces
                         .SelectMany(item => item.GetMembers().OfType<IMethodSymbol>()))
            {
                if (type.FindImplementationForInterfaceMember(contract) is not IMethodSymbol implementation)
                {
                    continue;
                }

                IMethodSymbol definition = NormalizeMethod(implementation);
                if (!SymbolEqualityComparer.Default.Equals(
                        definition.ContainingType.OriginalDefinition,
                        type.OriginalDefinition))
                {
                    continue;
                }

                if (!targets.TryGetValue(
                        definition,
                        out List<MethodIdentityTemplate>? methodTargets))
                {
                    methodTargets = new List<MethodIdentityTemplate>();
                    targets.Add(definition, methodTargets);
                }

                methodTargets.Add(SourceMethodIdentity(contract.OriginalDefinition, context));
            }

            Dictionary<IMethodSymbol, MethodIdentityTemplate[]> result = new(
                SymbolEqualityComparer.Default);
            foreach ((IMethodSymbol method, List<MethodIdentityTemplate> methodTargets) in targets)
            {
                result.Add(
                    method,
                    methodTargets
                    .DistinctBy(item => item.Text, StringComparer.Ordinal)
                    .OrderBy(item => item.Text, StringComparer.Ordinal)
                    .ToArray());
            }

            return result;
        }

        // 读取源码函数的接口实现或重写目标身份。
        private static MethodIdentityTemplate[] ReadSourceRelations(
            IMethodSymbol method,
            SourceCatalogContext context,
            IReadOnlyList<MethodIdentityTemplate> interfaceTargets)
        {
            if (method.ExplicitInterfaceImplementations.IsEmpty
                && method.OverriddenMethod == null
                && interfaceTargets.Count == 0)
            {
                return Array.Empty<MethodIdentityTemplate>();
            }

            return method.ExplicitInterfaceImplementations
                .Append(method.OverriddenMethod)
                .Where(item => item != null)
                .Cast<IMethodSymbol>()
                .Select(item => SourceMethodIdentity(item.OriginalDefinition, context))
                .Concat(interfaceTargets)
                .DistinctBy(item => item.Text, StringComparer.Ordinal)
                .OrderBy(item => item.Text, StringComparer.Ordinal)
                .ToArray();
        }

        // 合并部分函数并还原扩展函数的原始定义。
        private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
        {
            return (method.ReducedFrom ?? method.PartialImplementationPart ?? method).OriginalDefinition;
        }

        // 建立一个源码函数与托管函数共用的稳定身份。
        private static MethodIdentityTemplate SourceMethodIdentity(
            IMethodSymbol method,
            SourceCatalogContext context)
        {
            IMethodSymbol definition = NormalizeMethod(method);
            ParameterEntry[] parameters = definition.Parameters
                .Select(parameter => CreateSourceParameter(parameter, context))
                .ToArray();
            CatalogType returnType = SourceReturnType(definition, context);

            return new MethodIdentityTemplate(
                TypeIdentityTemplate.NamedType(NamedTypeDefinitionId(
                    definition.ContainingType.OriginalDefinition,
                    context)),
                definition.MetadataName,
                definition.Arity,
                parameters.Select(parameter => parameter.TypeIdentity).ToArray(),
                returnType.TypeIdentity);
        }

        // 按托管元数据的顺序生成源码函数返回签名。
        private static CatalogType SourceReturnType(
            IMethodSymbol method,
            SourceCatalogContext context)
        {
            string name = DisplayType(method.ReturnType, context);
            TypeIdentityTemplate typeIdentity = SourceTypeIdentity(
                method.ReturnType,
                context,
                useMatchIdentity: false);
            TypeIdentityTemplate matchTypeIdentity = SourceTypeIdentity(
                method.ReturnType,
                context,
                useMatchIdentity: true);
            typeIdentity = AppendSourceCustomModifiers(
                typeIdentity,
                method.ReturnTypeCustomModifiers,
                context);
            if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            {
                name += "&";
                typeIdentity = typeIdentity.Append("&");
                matchTypeIdentity = matchTypeIdentity.Append("&");
            }

            typeIdentity = AppendSourceCustomModifiers(
                typeIdentity,
                method.RefCustomModifiers,
                context);

            return new CatalogType(name, typeIdentity.Text, matchTypeIdentity.Text)
            {
                TypeIdentity = typeIdentity,
                MatchTypeIdentity = matchTypeIdentity,
            };
        }

        // 把源码自定义修饰符作为普通文字附加到结构化类型身份。
        private static TypeIdentityTemplate AppendSourceCustomModifiers(
            TypeIdentityTemplate typeIdentity,
            ImmutableArray<CustomModifier> modifiers,
            SourceCatalogContext context)
        {
            foreach (CustomModifier modifier in modifiers.Reverse())
            {
                string modifierKind = modifier.IsOptional ? "modopt" : "modreq";
                typeIdentity = typeIdentity
                    .Append($" {modifierKind}(")
                    .Append(SourceModifierTypeIdentity(modifier, context))
                    .Append(")");
            }

            return typeIdentity;
        }

        // 还原编译器生成但源码符号未绑定的运行时修饰符类型节点。
        private static TypeIdentityTemplate SourceModifierTypeIdentity(
            CustomModifier modifier,
            SourceCatalogContext context)
        {
            ITypeSymbol type = modifier.Modifier;
            if (IsSourceCallingConventionModifier(type, context))
            {
                return TypeIdentityTemplate.NamedType(
                    CallingConventionCanonicalTypeId(MetadataTypeNameKey((INamedTypeSymbol)type)));
            }

            if (type.TypeKind != TypeKind.Error
                || !SymbolEqualityComparer.Default.Equals(
                    type.ContainingAssembly,
                    context.Material.Compilation.Assembly))
            {
                return SourceTypeIdentity(type, context, useMatchIdentity: false);
            }

            INamedTypeSymbol namedType = (INamedTypeSymbol)type;
            string nameKey = MetadataTypeNameKey(namedType);
            if (!context.ManagedTypeIdsByNameKey.TryGetValue(
                    nameKey,
                    out IReadOnlyList<string>? candidates)
                || candidates.Count != 1)
            {
                throw new AnalysisException(
                    $"无法唯一定位源码返回修饰符：{MetadataTypeName(namedType)} => "
                    + string.Join("; ", candidates ?? Array.Empty<string>()));
            }

            return TypeIdentityTemplate.NamedType(candidates[0]);
        }

        // 把源码参数转换为统一参数记录。
        private static ParameterEntry CreateSourceParameter(
            IParameterSymbol parameter,
            SourceCatalogContext context)
        {
            CatalogRefKind refKind = parameter.RefKind switch
            {
                RefKind.Ref => CatalogRefKind.Ref,
                RefKind.Out => CatalogRefKind.Out,
                RefKind.In or RefKind.RefReadOnlyParameter => CatalogRefKind.In,
                _ => CatalogRefKind.None,
            };
            CatalogType type = SourceParameterType(parameter, context, refKind);

            return new ParameterEntry(
                parameter.Name,
                type.Name,
                type.Id,
                type.MatchId,
                refKind)
            {
                TypeIdentity = type.TypeIdentity,
                MatchTypeIdentity = type.MatchTypeIdentity,
            };
        }

        // 按托管元数据的顺序生成源码参数签名。
        private static CatalogType SourceParameterType(
            IParameterSymbol parameter,
            SourceCatalogContext context,
            CatalogRefKind refKind)
        {
            string name = DisplayType(parameter.Type, context);
            TypeIdentityTemplate typeIdentity = SourceTypeIdentity(
                parameter.Type,
                context,
                useMatchIdentity: false);
            TypeIdentityTemplate matchTypeIdentity = SourceTypeIdentity(
                parameter.Type,
                context,
                useMatchIdentity: true);
            typeIdentity = AppendSourceCustomModifiers(
                typeIdentity,
                parameter.CustomModifiers,
                context);
            if (refKind != CatalogRefKind.None)
            {
                typeIdentity = typeIdentity.Append("&");
                matchTypeIdentity = matchTypeIdentity.Append("&");
            }

            typeIdentity = AppendSourceCustomModifiers(
                typeIdentity,
                parameter.RefCustomModifiers,
                context);

            return new CatalogType(name, typeIdentity.Text, matchTypeIdentity.Text)
            {
                TypeIdentity = typeIdentity,
                MatchTypeIdentity = matchTypeIdentity,
            };
        }

        // 判断源码函数是否属于最终标签统计范围。
        private static bool IsReportable(
            IMethodSymbol method,
            CatalogMethodKind kind,
            string? sourcePath,
            bool isReportAssembly,
            IReadOnlySet<string> reportablePaths)
        {
            bool allowedKind = kind is CatalogMethodKind.Ordinary
                or CatalogMethodKind.PropertySetter
                or CatalogMethodKind.EventAdder
                or CatalogMethodKind.EventRemover
                or CatalogMethodKind.Operator
                or CatalogMethodKind.Conversion;

            return isReportAssembly
                && sourcePath != null
                && reportablePaths.Contains(sourcePath)
                && allowedKind
                && !method.IsAbstract
                && !method.IsImplicitlyDeclared
                && method.ContainingType.TypeKind is TypeKind.Class or TypeKind.Struct
                && method.MetadataName != "getInstance"
                && !method.MetadataName.StartsWith("BaseProxy_", StringComparison.Ordinal)
                && !HasCompilerGeneratedAttribute(method)
                && !HasCompilerGeneratedAttribute(method.ContainingType);
        }

        // 检查源码符号是否由编译器生成。
        private static bool HasCompilerGeneratedAttribute(ISymbol symbol)
        {
            return symbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() ==
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        // 把 Roslyn 函数种类映射为稳定的项目枚举。
        private static CatalogMethodKind GetSourceMethodKind(IMethodSymbol method)
        {
            return method.MethodKind switch
            {
                MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation =>
                    CatalogMethodKind.Ordinary,
                MethodKind.Constructor => CatalogMethodKind.Constructor,
                MethodKind.StaticConstructor => CatalogMethodKind.StaticConstructor,
                MethodKind.PropertyGet => CatalogMethodKind.PropertyGetter,
                MethodKind.PropertySet => CatalogMethodKind.PropertySetter,
                MethodKind.EventAdd => CatalogMethodKind.EventAdder,
                MethodKind.EventRemove => CatalogMethodKind.EventRemover,
                MethodKind.UserDefinedOperator => CatalogMethodKind.Operator,
                MethodKind.Conversion => CatalogMethodKind.Conversion,
                MethodKind.LocalFunction => CatalogMethodKind.LocalFunction,
                MethodKind.AnonymousFunction => CatalogMethodKind.AnonymousFunction,
                MethodKind.Destructor => CatalogMethodKind.Destructor,
                _ => CatalogMethodKind.Other,
            };
        }

        // 读取源码特性的完整类型名和显式参数。
        private static IReadOnlyList<AttributeEntry> ReadSourceAttributes(
            ImmutableArray<AttributeData> attributes)
        {
            if (attributes.IsEmpty)
            {
                return Array.Empty<AttributeEntry>();
            }

            return attributes
                .Select(attribute => new AttributeEntry(
                    attribute.AttributeClass?.ToDisplayString() ?? string.Empty,
                    attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax syntax
                        && syntax.ArgumentList?.Arguments.Count > 0,
                    attribute.ConstructorArguments
                        .Concat(attribute.NamedArguments.Select(argument => argument.Value))
                        .Select(argument => argument.Value?.ToString() ?? "null")
                        .ToArray()))
                .OrderBy(attribute => attribute.TypeName, StringComparer.Ordinal)
                .ToArray();
        }

        // 生成用户可读的源码类型名称。
        private static string DisplayType(
            ITypeSymbol type,
            SourceCatalogContext context)
        {
            return context.TypeNames.GetOrAdd(
                type,
                symbol => symbol.ToDisplayString(s_typeDisplayFormat));
        }

        // 生成只在明确泛型参数位置允许替换的源码类型身份。
        private static TypeIdentityTemplate SourceTypeIdentity(
            ITypeSymbol type,
            SourceCatalogContext context,
            bool useMatchIdentity)
        {
            ConcurrentDictionary<ITypeSymbol, TypeIdentityTemplate> cache = useMatchIdentity
                ? context.MatchTypeIdentities
                : context.TypeIdentities;

            return cache.GetOrAdd(
                type,
                symbol => BuildSourceTypeIdentity(symbol, context, useMatchIdentity));
        }

        // 从源码类型符号建立尚未保存过的结构化类型身份。
        private static TypeIdentityTemplate BuildSourceTypeIdentity(
            ITypeSymbol type,
            SourceCatalogContext context,
            bool useMatchIdentity)
        {
            PrimitiveTypeCode? primitiveTypeCode = GetPrimitiveTypeCode(type.SpecialType);
            if (primitiveTypeCode != null)
            {
                return TypeIdentityTemplate.Literal(PrimitiveTypeName(primitiveTypeCode.Value));
            }

            if (type is ITypeParameterSymbol methodParameter
                && methodParameter.TypeParameterKind == TypeParameterKind.Method)
            {
                return TypeIdentityTemplate.Literal($"!!{methodParameter.Ordinal}");
            }

            if (type is ITypeParameterSymbol typeParameter)
            {
                return TypeIdentityTemplate.TypeParameter(ReadTypeParameterOrdinal(typeParameter));
            }

            TypeIdentityTemplate identity = type switch
            {
                IArrayTypeSymbol array => SourceTypeIdentity(
                    array.ElementType,
                    context,
                    useMatchIdentity).Append($"[{new string(',', array.Rank - 1)}]"),
                IPointerTypeSymbol pointer => SourceTypeIdentity(
                    pointer.PointedAtType,
                    context,
                    useMatchIdentity).Append("*"),
                IFunctionPointerTypeSymbol pointer => SourceFunctionPointerIdentity(
                    pointer.Signature,
                    context,
                    useMatchIdentity),
                INamedTypeSymbol named => BuildNamedTypeIdentity(
                    named,
                    ReadNamedTypeArguments(named, context).ToArray(),
                    useMatchIdentity,
                    context),
                _ => TypeIdentityTemplate.Literal(DisplayType(type, context)),
            };

            if (useMatchIdentity)
            {
                return identity;
            }

            return type switch
            {
                IArrayTypeSymbol array => AppendSourceCustomModifiers(
                    identity,
                    array.CustomModifiers,
                    context),
                IPointerTypeSymbol pointer => AppendSourceCustomModifiers(
                    identity,
                    pointer.CustomModifiers,
                    context),
                _ => identity,
            };
        }

        // 生成保留嵌套泛型参数位置的源码函数指针身份。
        private static TypeIdentityTemplate SourceFunctionPointerIdentity(
            IMethodSymbol signature,
            SourceCatalogContext context,
            bool useMatchIdentity)
        {
            ParameterEntry[] parameters = signature.Parameters
                .Select(parameter => CreateSourceParameter(parameter, context))
                .ToArray();
            CatalogType returnType = SourceReturnType(signature, context);
            CatalogCustomModifier[] callingConventionModifiers =
                ReadSourceCallingConventionModifiers(signature, context);

            return BuildFunctionPointerIdentityTemplate(
                signature.CallingConvention.ToString(),
                signature.Arity,
                signature.Parameters.Length,
                isInstance: false,
                hasExplicitThis: false,
                callingConventionModifiers,
                parameters.Select(parameter => useMatchIdentity
                    ? parameter.MatchTypeIdentity
                    : parameter.TypeIdentity),
                useMatchIdentity
                    ? returnType.MatchTypeIdentity
                    : returnType.TypeIdentity);
        }

        // 读取源码函数指针中真正参与扩展非托管调用约定的修饰符。
        private static CatalogCustomModifier[] ReadSourceCallingConventionModifiers(
            IMethodSymbol signature,
            SourceCatalogContext context)
        {
            if (signature.CallingConvention != SignatureCallingConvention.Unmanaged)
            {
                return Array.Empty<CatalogCustomModifier>();
            }

            ImmutableArray<CustomModifier> modifiers = signature.RefKind == RefKind.None
                ? signature.ReturnTypeCustomModifiers
                : signature.RefCustomModifiers;

            return modifiers
                .Where(modifier => IsSourceCallingConventionModifier(modifier.Modifier, context))
                .Select(modifier => new CatalogCustomModifier(
                    SourceModifierTypeIdentity(modifier, context).Text,
                    !modifier.IsOptional,
                    true))
                .ToArray();
        }

        // 判断源码修饰类型是否是当前核心库定义的顶层 CallConv 类型。
        private static bool IsSourceCallingConventionModifier(
            ITypeSymbol type,
            SourceCatalogContext context)
        {
            if (type is not INamedTypeSymbol namedType)
            {
                return false;
            }

            IAssemblySymbol coreLibrary = context.Material.Compilation.GetSpecialType(
                SpecialType.System_Object).ContainingAssembly;

            return SymbolEqualityComparer.Default.Equals(namedType.ContainingAssembly, coreLibrary)
                && namedType.ContainingType == null
                && namedType.Arity == 0
                && namedType.Name != "CallConv"
                && namedType.Name.StartsWith("CallConv", StringComparison.Ordinal)
                && namedType.ContainingNamespace.ToDisplayString() ==
                    "System.Runtime.CompilerServices";
        }

        // 用同一结构生成可以安全实例化泛型参数的函数指针身份。
        private static TypeIdentityTemplate BuildFunctionPointerIdentityTemplate(
            string callingConvention,
            int genericArity,
            int requiredParameterCount,
            bool isInstance,
            bool hasExplicitThis,
            IEnumerable<CatalogCustomModifier> callingConventionModifiers,
            IEnumerable<TypeIdentityTemplate> parameterTypeIdentities,
            TypeIdentityTemplate returnTypeIdentity)
        {
            string modifierText = string.Join(",", callingConventionModifiers
                .Select(modifier => modifier.IsRequired
                    ? $"modreq({modifier.TypeId})"
                    : $"modopt({modifier.TypeId})")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
            string convention = modifierText.Length == 0
                ? callingConvention
                : $"{callingConvention}:{modifierText}";
            string arity = genericArity == 0 ? string.Empty : $"``{genericArity}";
            string requiredParameters = callingConvention == SignatureCallingConvention.VarArgs.ToString()
                ? $";required={requiredParameterCount}"
                : string.Empty;
            string instance = isInstance ? ";instance" : string.Empty;
            string explicitThis = hasExplicitThis ? ";explicit" : string.Empty;

            return TypeIdentityTemplate.Join(
                    $"fn[{convention}{requiredParameters}{instance}{explicitThis}]{arity}(",
                    parameterTypeIdentities,
                    ",",
                    ")->")
                .Append(returnTypeIdentity);
        }

        // 把 Roslyn 基础类型映射到托管签名使用的类型代码。
        private static PrimitiveTypeCode? GetPrimitiveTypeCode(SpecialType specialType)
        {
            return specialType switch
            {
                SpecialType.System_Object => PrimitiveTypeCode.Object,
                SpecialType.System_Void => PrimitiveTypeCode.Void,
                SpecialType.System_Boolean => PrimitiveTypeCode.Boolean,
                SpecialType.System_Char => PrimitiveTypeCode.Char,
                SpecialType.System_SByte => PrimitiveTypeCode.SByte,
                SpecialType.System_Byte => PrimitiveTypeCode.Byte,
                SpecialType.System_Int16 => PrimitiveTypeCode.Int16,
                SpecialType.System_UInt16 => PrimitiveTypeCode.UInt16,
                SpecialType.System_Int32 => PrimitiveTypeCode.Int32,
                SpecialType.System_UInt32 => PrimitiveTypeCode.UInt32,
                SpecialType.System_Int64 => PrimitiveTypeCode.Int64,
                SpecialType.System_UInt64 => PrimitiveTypeCode.UInt64,
                SpecialType.System_Single => PrimitiveTypeCode.Single,
                SpecialType.System_Double => PrimitiveTypeCode.Double,
                SpecialType.System_String => PrimitiveTypeCode.String,
                SpecialType.System_IntPtr => PrimitiveTypeCode.IntPtr,
                SpecialType.System_UIntPtr => PrimitiveTypeCode.UIntPtr,
                SpecialType.System_TypedReference => PrimitiveTypeCode.TypedReference,
                _ => null,
            };
        }

        // 生成源码与托管文件共用的基础类型身份。
        private static string PrimitiveTypeName(PrimitiveTypeCode typeCode)
        {
            return typeCode switch
            {
                PrimitiveTypeCode.Boolean
                    or PrimitiveTypeCode.Byte
                    or PrimitiveTypeCode.Char
                    or PrimitiveTypeCode.Double
                    or PrimitiveTypeCode.Int16
                    or PrimitiveTypeCode.Int32
                    or PrimitiveTypeCode.Int64
                    or PrimitiveTypeCode.IntPtr
                    or PrimitiveTypeCode.Object
                    or PrimitiveTypeCode.SByte
                    or PrimitiveTypeCode.Single
                    or PrimitiveTypeCode.String
                    or PrimitiveTypeCode.TypedReference
                    or PrimitiveTypeCode.UInt16
                    or PrimitiveTypeCode.UInt32
                    or PrimitiveTypeCode.UInt64
                    or PrimitiveTypeCode.UIntPtr
                    or PrimitiveTypeCode.Void => $"System.{typeCode}",
                _ => throw new AnalysisException($"不支持的基础托管类型：{typeCode}"),
            };
        }

        // 用命名类型定义和结构化实参生成完整或匹配身份。
        private static TypeIdentityTemplate BuildNamedTypeIdentity(
            INamedTypeSymbol type,
            IReadOnlyList<CatalogType> arguments,
            bool useMatchIdentity,
            SourceCatalogContext context)
        {
            string definitionId = NamedTypeDefinitionId(type.OriginalDefinition, context);
            if (arguments.Count == 0)
            {
                return TypeIdentityTemplate.NamedType(definitionId);
            }

            return TypeIdentityTemplate.NamedType(definitionId)
                .Append(TypeIdentityTemplate.Join(
                    "<",
                    arguments.Select(argument => useMatchIdentity
                        ? argument.MatchTypeIdentity
                        : argument.TypeIdentity),
                    ",",
                    ">"));
        }

        // 生成不带构造实参的命名类型定义身份。
        private static string NamedTypeDefinitionId(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            INamedTypeSymbol definition = type.OriginalDefinition;
            string assemblyName = definition.ContainingAssembly?.Identity.Name
                ?? throw new AnalysisException($"类型没有所属程序集：{definition}");

            return NamedTypeId(
                CanonicalAssemblyName(context.AssemblyNames, assemblyName),
                MetadataTypeNameKey(definition));
        }

        // 把程序集字段与已经结构化的类型名称组合成唯一类型身份。
        private static string NamedTypeId(string assemblyName, string nameKey)
        {
            return $"A{assemblyName.Length}:{assemblyName}{nameKey}";
        }

        // 把嵌套类型自己的泛型参数序号换成元数据使用的扁平序号。
        private static int ReadTypeParameterOrdinal(ITypeParameterSymbol parameter)
        {
            int ordinal = parameter.Ordinal;
            INamedTypeSymbol? containingType = parameter.ContainingType?.ContainingType;

            while (containingType != null)
            {
                ordinal += containingType.Arity;
                containingType = containingType.ContainingType;
            }

            return ordinal;
        }

        // 按外层到内层顺序读取嵌套类型的全部泛型实参。
        private static IEnumerable<CatalogType> ReadNamedTypeArguments(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            if (type.ContainingType != null)
            {
                foreach (CatalogType argument in ReadNamedTypeArguments(type.ContainingType, context))
                {
                    yield return argument;
                }
            }

            for (int index = 0; index < type.TypeArguments.Length; index++)
            {
                ITypeSymbol argument = type.TypeArguments[index];
                TypeIdentityTemplate typeIdentity = AppendSourceCustomModifiers(
                    SourceTypeIdentity(argument, context, useMatchIdentity: false),
                    type.GetTypeArgumentCustomModifiers(index),
                    context);
                TypeIdentityTemplate matchTypeIdentity = SourceTypeIdentity(
                    argument,
                    context,
                    useMatchIdentity: true);

                yield return new CatalogType(
                    DisplayType(argument, context),
                    typeIdentity.Text,
                    matchTypeIdentity.Text)
                {
                    TypeIdentity = typeIdentity,
                    MatchTypeIdentity = matchTypeIdentity,
                };
            }
        }

        // 生成与托管元数据一致的嵌套类型名称。
        private static string MetadataTypeName(INamedTypeSymbol type)
        {
            Stack<string> names = new();
            INamedTypeSymbol? current = type;

            while (current != null)
            {
                names.Push(current.MetadataName);
                current = current.ContainingType;
            }

            string nestedName = string.Join("+", names);
            string namespaceName = type.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : type.ContainingNamespace.ToDisplayString();

            return namespaceName.Length == 0 ? nestedName : $"{namespaceName}.{nestedName}";
        }

        // 分别编码命名空间和每层元数据类型名以保留真实边界。
        private static string MetadataTypeNameKey(INamedTypeSymbol type)
        {
            Stack<string> names = new();
            INamedTypeSymbol? current = type;

            while (current != null)
            {
                names.Push(current.MetadataName);
                current = current.ContainingType;
            }

            string namespaceName = type.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : type.ContainingNamespace.ToDisplayString();

            return NamedTypeNameKey(namespaceName, names);
        }

        // 用长度编码命名空间和每层类型名。
        private static string NamedTypeNameKey(
            string namespaceName,
            IEnumerable<string> metadataNames)
        {
            string[] names = metadataNames.ToArray();
            string types = string.Concat(names.Select(name => $"T{name.Length}:{name}"));

            return $"N{namespaceName.Length}:{namespaceName}C{names.Length}:{types}";
        }

        // 读取一份真实托管文件中的类型和继承关系。
        private static CatalogPart ReadManagedAssemblyTypes(
            string assemblyPath,
            IReadOnlyDictionary<string, string> callingConventionTypes,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader portableExecutable = new(stream);
            MetadataReader reader = portableExecutable.GetMetadataReader();
            string assemblyName = CanonicalAssemblyName(
                assemblyNames,
                reader.GetString(reader.GetAssemblyDefinition().Name));
            string fullAssemblyPath = Path.GetFullPath(assemblyPath);
            MetadataTypeProvider provider = new(
                reader,
                assemblyName,
                callingConventionTypes,
                assemblyNames);
            List<TypeEntry> types = new();

            foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
            {
                if (MetadataTokens.GetRowNumber(typeHandle) == 1)
                {
                    continue;
                }

                TypeDefinition definition = reader.GetTypeDefinition(typeHandle);
                string typeName = reader.GetString(definition.Name);
                CatalogType catalogType = provider.GetTypeFromDefinition(reader, typeHandle, 0);
                string logicalTypeId = catalogType.Id;
                string typeId = $"{logicalTypeId}@{fullAssemblyPath}";
                TypeEntry type = new(
                    typeId,
                    logicalTypeId,
                    Array.Empty<string>(),
                    assemblyName,
                    RemoveGenericArity(typeName, definition.GetGenericParameters().Count > 0),
                    catalogType.Name,
                    catalogType.NameKey
                        ?? throw new AnalysisException($"托管命名类型缺少结构名称：{catalogType.Name}"),
                    definition.BaseType.IsNil
                        ? null
                        : CreateManagedTypeRelation(provider.GetType(definition.BaseType)),
                    definition.GetInterfaceImplementations()
                        .Select(handle => reader.GetInterfaceImplementation(handle))
                        .Select(implementation => CreateManagedTypeRelation(
                            provider.GetType(implementation.Interface)))
                        .DistinctBy(relation => relation.TypeId, StringComparer.Ordinal)
                        .OrderBy(relation => relation.TypeId, StringComparer.Ordinal)
                        .ToArray(),
                    Array.Empty<AttributeEntry>(),
                    (definition.Attributes & TypeAttributes.Interface) != 0,
                    null,
                    fullAssemblyPath,
                    MetadataTokens.GetToken(typeHandle));

                types.Add(type);
            }

            return new CatalogPart(
                types,
                ReadAssemblyTypeMap(
                    reader,
                    fullAssemblyPath,
                    assemblyName,
                    provider,
                    types.Select(type => type.NameKey).ToHashSet(StringComparer.Ordinal)));
        }

        // 把托管元数据中的基类或接口类型保存为无需反解析的关系记录。
        private static TypeRelationEntry CreateManagedTypeRelation(CatalogType type)
        {
            string definitionId = type.DefinitionId
                ?? throw new AnalysisException($"继承关系不是命名类型：{type.Id}");

            return new TypeRelationEntry(
                definitionId,
                type.Id,
                type.MatchId,
                type.TypeArguments ?? Array.Empty<string>(),
                type.MatchTypeArguments ?? Array.Empty<string>())
            {
                TypeIdentity = type.TypeIdentity,
                MatchTypeIdentity = type.MatchTypeIdentity,
                TypeArgumentIdentities = type.TypeArgumentIdentities
                    ?? Array.Empty<TypeIdentityTemplate>(),
                MatchTypeArgumentIdentities = type.MatchTypeArgumentIdentities
                    ?? Array.Empty<TypeIdentityTemplate>(),
            };
        }

        // 把编译时参考文件中的类型身份连接到真实函数所在类型。
        private static TypeEntry[] AttachTypeAliases(
            IReadOnlyList<ExternalAssemblyMaterial> assemblies,
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<AssemblyTypeMap> loadedMaps,
            IReadOnlyDictionary<string, string> callingConventionTypes,
            IReadOnlyDictionary<string, string> assemblyNames,
            int jobs)
        {
            string[] paths = assemblies
                .Select(assembly => assembly.ReferencePath)
                .Concat(assemblies.SelectMany(assembly => assembly.ImplementationPaths))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ConcurrentDictionary<string, AssemblyTypeMap> maps = new(
                loadedMaps.ToDictionary(map => map.Path, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(
                paths.Where(path => !maps.ContainsKey(path)),
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                path => maps[path] = ReadAssemblyTypeMap(
                    path,
                    callingConventionTypes,
                    assemblyNames));
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypeEntry>> typesByLocation = types
                .Where(type => type.AssemblyPath != null)
                .GroupBy(
                    type => Path.GetFullPath(type.AssemblyPath!),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<string, TypeEntry>)group.ToDictionary(
                        type => type.NameKey,
                        StringComparer.Ordinal),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> aliasesByTypeId = new(StringComparer.Ordinal);

            foreach (ExternalAssemblyMaterial assembly in assemblies)
            {
                HashSet<string> implementationPaths = assembly.ImplementationPaths
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                AssemblyTypeMap reference = maps[assembly.ReferencePath];
                AssemblyTypeMap[] roots = implementationPaths
                    .Select(path => maps[path])
                    .Where(map => string.Equals(
                        map.AssemblyName,
                        reference.AssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                bool referenceContainsImplementations = roots.Any(root => string.Equals(
                    root.Path,
                    reference.Path,
                    StringComparison.OrdinalIgnoreCase));
                IEnumerable<string> definitionNameKeys = referenceContainsImplementations
                    ? Array.Empty<string>()
                    : reference.DefinitionNameKeys;

                foreach (string nameKey in definitionNameKeys
                             .Concat(reference.ForwardedAssembliesByNameKey.Keys)
                             .Distinct(StringComparer.Ordinal))
                {
                    string alias = NamedTypeId(reference.AssemblyName, nameKey);
                    foreach (TypeEntry type in ResolveReferenceType(
                                 nameKey,
                                 roots,
                                 implementationPaths,
                                 maps,
                                 typesByLocation))
                    {
                        if (string.Equals(alias, type.LogicalId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!aliasesByTypeId.TryGetValue(type.Id, out HashSet<string>? aliases))
                        {
                            aliases = new HashSet<string>(StringComparer.Ordinal);
                            aliasesByTypeId.Add(type.Id, aliases);
                        }

                        aliases.Add(alias);
                    }
                }
            }

            return types
                .Select(type => type with
                {
                    AliasIds = aliasesByTypeId.TryGetValue(type.Id, out HashSet<string>? aliases)
                        ? aliases.Order(StringComparer.Ordinal).ToArray()
                        : Array.Empty<string>(),
                })
                .ToArray();
        }

        // 沿真实文件明确声明的类型转交链定位类型定义。
        private static IReadOnlyList<TypeEntry> ResolveReferenceType(
            string nameKey,
            IReadOnlyList<AssemblyTypeMap> roots,
            IReadOnlySet<string> implementationPaths,
            IReadOnlyDictionary<string, AssemblyTypeMap> maps,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypeEntry>> typesByLocation)
        {
            Stack<AssemblyTypeMap> pending = new(roots.Reverse());
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            List<TypeEntry> result = new();

            while (pending.Count > 0)
            {
                AssemblyTypeMap map = pending.Pop();
                if (!visited.Add(map.Path))
                {
                    continue;
                }

                if (map.DefinitionNameKeys.Contains(nameKey)
                    && typesByLocation.TryGetValue(map.Path, out IReadOnlyDictionary<string, TypeEntry>? typesByName)
                    && typesByName.TryGetValue(nameKey, out TypeEntry? type))
                {
                    result.Add(type);
                }

                if (map.ForwardedAssembliesByNameKey.TryGetValue(
                    nameKey,
                    out string? targetAssemblyName))
                {
                    foreach (AssemblyTypeMap target in maps.Values
                                 .Where(candidate => implementationPaths.Contains(candidate.Path)
                                     && string.Equals(
                                         candidate.AssemblyName,
                                         targetAssemblyName,
                                         StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(target);
                    }
                }
            }

            return result
                .DistinctBy(type => type.Id, StringComparer.Ordinal)
                .OrderBy(type => type.Id, StringComparer.Ordinal)
                .ToArray();
        }

        // 读取一份托管文件定义的类型和逐类型转交目标。
        private static AssemblyTypeMap ReadAssemblyTypeMap(
            string assemblyPath,
            IReadOnlyDictionary<string, string> callingConventionTypes,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string fullPath = Path.GetFullPath(assemblyPath);
            using FileStream stream = File.OpenRead(fullPath);
            using PEReader portableExecutable = new(stream);
            MetadataReader reader = portableExecutable.GetMetadataReader();
            string assemblyName = CanonicalAssemblyName(
                assemblyNames,
                reader.GetString(reader.GetAssemblyDefinition().Name));
            MetadataTypeProvider provider = new(
                reader,
                assemblyName,
                callingConventionTypes,
                assemblyNames);

            return ReadAssemblyTypeMap(reader, fullPath, assemblyName, provider, null);
        }

        // 从已经打开的托管元数据读取类型定义和逐类型转交目标。
        private static AssemblyTypeMap ReadAssemblyTypeMap(
            MetadataReader reader,
            string fullPath,
            string assemblyName,
            MetadataTypeProvider provider,
            IReadOnlySet<string>? knownDefinitionNameKeys)
        {
            Dictionary<ExportedTypeHandle, ManagedTypeName> names = new();
            Dictionary<ExportedTypeHandle, string?> targets = new();
            IReadOnlySet<string> definitions = knownDefinitionNameKeys
                ?? reader.TypeDefinitions
                    .Where(handle => MetadataTokens.GetRowNumber(handle) != 1)
                    .Select(handle => provider.GetTypeFromDefinition(reader, handle, 0).NameKey
                        ?? throw new AnalysisException("托管类型定义缺少结构名称。"))
                    .ToHashSet(StringComparer.Ordinal);
            Dictionary<string, string> forwarders = reader.ExportedTypes
                .Select(handle => (
                    NameKey: ReadExportedTypeName(reader, handle, names).Key,
                    Target: ReadExportedAssemblyName(reader, handle, targets)))
                .Where(item => item.Target != null)
                .ToDictionary(
                    item => item.NameKey,
                    item => item.Target!,
                    StringComparer.Ordinal);

            return new AssemblyTypeMap(
                fullPath,
                assemblyName,
                definitions,
                forwarders);
        }

        // 读取一个可能嵌套的导出类型结构名称。
        private static ManagedTypeName ReadExportedTypeName(
            MetadataReader reader,
            ExportedTypeHandle handle,
            IDictionary<ExportedTypeHandle, ManagedTypeName> names)
        {
            if (names.TryGetValue(handle, out ManagedTypeName? knownName))
            {
                return knownName;
            }

            ExportedType type = reader.GetExportedType(handle);
            string name = reader.GetString(type.Name);
            ManagedTypeName result;
            if (type.Implementation.Kind == HandleKind.ExportedType)
            {
                ManagedTypeName parent = ReadExportedTypeName(
                    reader,
                    (ExportedTypeHandle)type.Implementation,
                    names);
                result = new ManagedTypeName(
                    parent.NamespaceName,
                    parent.MetadataNames.Append(name).ToArray());
            }
            else
            {
                result = new ManagedTypeName(
                    reader.GetString(type.Namespace),
                    new[] { name });
            }

            names.Add(handle, result);

            return result;
        }

        // 沿嵌套导出类型读取最终承载它的程序集名称。
        private static string? ReadExportedAssemblyName(
            MetadataReader reader,
            ExportedTypeHandle handle,
            IDictionary<ExportedTypeHandle, string?> targets)
        {
            if (targets.TryGetValue(handle, out string? knownTarget))
            {
                return knownTarget;
            }

            EntityHandle implementation = reader.GetExportedType(handle).Implementation;
            string? target = implementation.Kind switch
            {
                HandleKind.AssemblyReference => reader.GetString(
                    reader.GetAssemblyReference((AssemblyReferenceHandle)implementation).Name),
                HandleKind.ExportedType => ReadExportedAssemblyName(
                    reader,
                    (ExportedTypeHandle)implementation,
                    targets),
                _ => null,
            };

            targets.Add(handle, target);

            return target;
        }

        // 按元数据标记读取一个托管类型的全部函数。
        internal static IReadOnlyList<MethodEntry> ReadManagedTypeMethods(
            TypeEntry type,
            IReadOnlyDictionary<string, string> callingConventionTypes,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string assemblyPath = type.AssemblyPath
                ?? throw new AnalysisException($"类型不是托管文件类型：{type.Id}");

            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader portableExecutable = new(stream);
            MetadataReader reader = portableExecutable.GetMetadataReader();
            string assemblyName = CanonicalAssemblyName(
                assemblyNames,
                reader.GetString(reader.GetAssemblyDefinition().Name));
            MetadataTypeProvider provider = new(
                reader,
                assemblyName,
                callingConventionTypes,
                assemblyNames);
            TypeDefinitionHandle typeHandle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(
                type.MetadataToken);
            TypeDefinition definition = reader.GetTypeDefinition(typeHandle);
            IReadOnlyDictionary<MethodDefinitionHandle, MethodIdentityTemplate[]> implementations =
                ReadManagedMethodImplementations(reader, provider, typeHandle, definition);
            IReadOnlyDictionary<MethodDefinitionHandle, CatalogMethodKind> accessorKinds =
                ReadManagedAccessorKinds(reader, definition);

            return definition.GetMethods()
                .Select(methodHandle => CreateManagedMethod(
                    reader,
                    provider,
                    assemblyPath,
                    assemblyName,
                    type,
                    methodHandle,
                    implementations.GetValueOrDefault(methodHandle)
                        ?? Array.Empty<MethodIdentityTemplate>(),
                    accessorKinds.TryGetValue(methodHandle, out CatalogMethodKind kind)
                        ? kind
                        : null))
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
        }

        // 把托管函数元数据转换为统一函数记录。
        private static MethodEntry CreateManagedMethod(
            MetadataReader reader,
            MetadataTypeProvider provider,
            string assemblyPath,
            string assemblyName,
            TypeEntry type,
            MethodDefinitionHandle methodHandle,
            IReadOnlyList<MethodIdentityTemplate> relatedMethodIdentities,
            CatalogMethodKind? accessorKind)
        {
            MethodDefinition definition = reader.GetMethodDefinition(methodHandle);
            MethodSignature<CatalogType> signature = definition.DecodeSignature(provider, null);
            string methodName = reader.GetString(definition.Name);
            Dictionary<int, Parameter> parametersBySequence = definition.GetParameters()
                .Select(handle => reader.GetParameter(handle))
                .Where(parameter => parameter.SequenceNumber > 0)
                .ToDictionary(parameter => parameter.SequenceNumber);
            ParameterEntry[] parameters = signature.ParameterTypes
                .Select((parameter, index) =>
                {
                    bool found = parametersBySequence.TryGetValue(
                        index + 1,
                        out Parameter metadata);

                    return CreateManagedParameter(
                        reader,
                        parameter,
                        found ? metadata : null,
                        index);
                })
                .ToArray();
            MethodIdentityTemplate identity = new(
                TypeIdentityTemplate.NamedType(type.LogicalId),
                methodName,
                definition.GetGenericParameters().Count,
                parameters.Select(parameter => parameter.TypeIdentity).ToArray(),
                signature.ReturnType.TypeIdentity);
            string logicalId = identity.Text;
            int metadataToken = MetadataTokens.GetToken(methodHandle);
            string id = $"{Path.GetFullPath(assemblyPath)}#{metadataToken:X8}";
            string[] relatedMethodIds = relatedMethodIdentities
                .Select(item => item.Text)
                .ToArray();

            return new MethodEntry(
                id,
                logicalId,
                assemblyName,
                type.Id,
                type.FullName,
                methodName,
                signature.ReturnType.Name,
                signature.ReturnType.Id,
                signature.ReturnType.MatchId,
                parameters,
                GetManagedMethodKind(definition, methodName, accessorKind),
                Array.Empty<AttributeEntry>(),
                type.Attributes,
                relatedMethodIds,
                null,
                0,
                false,
                (definition.Attributes & MethodAttributes.Abstract) != 0,
                (definition.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public,
                (definition.Attributes & MethodAttributes.Static) != 0,
                (definition.Attributes & MethodAttributes.Virtual) != 0,
                (definition.Attributes & MethodAttributes.NewSlot) != 0,
                definition.GetGenericParameters().Count,
                null,
                assemblyPath,
                metadataToken)
            {
                ReturnTypeIdentity = signature.ReturnType.TypeIdentity,
                ReturnMatchTypeIdentity = signature.ReturnType.MatchTypeIdentity,
                Identity = identity,
                RelatedMethodIdentities = relatedMethodIdentities,
            };
        }

        // 从属性和事件元数据读取每个访问函数的实际种类。
        private static IReadOnlyDictionary<MethodDefinitionHandle, CatalogMethodKind>
            ReadManagedAccessorKinds(MetadataReader reader, TypeDefinition type)
        {
            Dictionary<MethodDefinitionHandle, CatalogMethodKind> kinds = new();

            foreach (PropertyDefinitionHandle handle in type.GetProperties())
            {
                PropertyAccessors accessors = reader.GetPropertyDefinition(handle).GetAccessors();
                if (!accessors.Getter.IsNil)
                {
                    kinds.Add(accessors.Getter, CatalogMethodKind.PropertyGetter);
                }

                if (!accessors.Setter.IsNil)
                {
                    kinds.Add(accessors.Setter, CatalogMethodKind.PropertySetter);
                }
            }

            foreach (EventDefinitionHandle handle in type.GetEvents())
            {
                EventAccessors accessors = reader.GetEventDefinition(handle).GetAccessors();
                if (!accessors.Adder.IsNil)
                {
                    kinds.Add(accessors.Adder, CatalogMethodKind.EventAdder);
                }

                if (!accessors.Remover.IsNil)
                {
                    kinds.Add(accessors.Remover, CatalogMethodKind.EventRemover);
                }
            }

            return kinds;
        }

        // 读取当前类型显式声明的接口函数或重写目标。
        private static IReadOnlyDictionary<MethodDefinitionHandle, MethodIdentityTemplate[]>
            ReadManagedMethodImplementations(
            MetadataReader reader,
            MetadataTypeProvider provider,
            TypeDefinitionHandle typeHandle,
            TypeDefinition type)
        {
            CatalogType currentType = provider.GetTypeFromDefinition(reader, typeHandle, 0);
            IReadOnlyDictionary<string, MethodDefinitionHandle[]> methodsBySignature = type
                .GetMethods()
                .GroupBy(
                    handle => ManagedMethodDefinitionLookupKey(reader, provider, handle),
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.Ordinal);

            return type.GetMethodImplementations()
                .Select(handle => reader.GetMethodImplementation(handle))
                .Select(implementation => (
                    Body: ResolveManagedMethodBody(
                        reader,
                        provider,
                        implementation.MethodBody,
                        typeHandle,
                        currentType,
                        type.GetGenericParameters().Count,
                        methodsBySignature),
                    Declaration: ManagedMethodId(
                        reader,
                        provider,
                        implementation.MethodDeclaration)))
                .GroupBy(implementation => implementation.Body)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(implementation => implementation.Declaration)
                        .DistinctBy(item => item.Text, StringComparer.Ordinal)
                        .OrderBy(item => item.Text, StringComparer.Ordinal)
                        .ToArray());
        }

        // 把函数定义或函数引用形式的实现体精确对应到当前类型的唯一函数定义。
        private static MethodDefinitionHandle ResolveManagedMethodBody(
            MetadataReader reader,
            MetadataTypeProvider provider,
            EntityHandle body,
            TypeDefinitionHandle currentTypeHandle,
            CatalogType currentType,
            int currentTypeArity,
            IReadOnlyDictionary<string, MethodDefinitionHandle[]> methodsBySignature)
        {
            if (body.Kind == HandleKind.MethodDefinition)
            {
                MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)body;
                if (reader.GetMethodDefinition(methodHandle).GetDeclaringType()
                    != currentTypeHandle)
                {
                    throw new AnalysisException(
                        $"托管函数实现体定义不属于当前类型：{ManagedMethodId(reader, provider, body).Text}");
                }

                return methodHandle;
            }

            if (body.Kind != HandleKind.MemberReference)
            {
                throw new AnalysisException($"不支持的托管函数实现体句柄：{body.Kind}");
            }

            MemberReference reference = reader.GetMemberReference((MemberReferenceHandle)body);
            MethodIdentityTemplate identity = ManagedMethodId(reader, provider, body);
            CatalogType parent = provider.GetType(reference.Parent);
            if (!IsCurrentManagedType(parent, currentType, currentTypeArity))
            {
                throw new AnalysisException(
                    $"托管函数实现体引用了当前类型以外的类型：{identity.Text}");
            }

            identity = identity.WithDeclaringType(currentType.TypeIdentity);
            MethodSignature<CatalogType> signature = reference.DecodeMethodSignature(
                provider,
                null);
            string lookupKey = ManagedMethodLookupKey(signature, identity);
            if (!methodsBySignature.TryGetValue(
                    lookupKey,
                    out MethodDefinitionHandle[]? matches)
                || matches.Length != 1)
            {
                int matchCount = matches?.Length ?? 0;
                throw new AnalysisException(
                    $"托管函数实现体无法唯一对应当前类型函数：{identity.Text} => {matchCount}");
            }

            return matches[0];
        }

        // 为当前类型的函数定义生成包含静态实例和调用约定的内部定位键。
        private static string ManagedMethodDefinitionLookupKey(
            MetadataReader reader,
            MetadataTypeProvider provider,
            MethodDefinitionHandle handle)
        {
            MethodDefinition definition = reader.GetMethodDefinition(handle);
            MethodSignature<CatalogType> signature = definition.DecodeSignature(provider, null);

            return ManagedMethodLookupKey(
                signature,
                ManagedMethodId(reader, provider, handle));
        }

        // 用完整签名头和函数身份生成只供实现体定位使用的键。
        private static string ManagedMethodLookupKey(
            MethodSignature<CatalogType> signature,
            MethodIdentityTemplate identity)
        {
            return $"{signature.Header.RawValue:X2}:{signature.RequiredParameterCount}:{identity.Text}";
        }

        // 判断函数引用父类型是否为当前类型或逐位使用自身参数的当前泛型类型。
        private static bool IsCurrentManagedType(
            CatalogType candidate,
            CatalogType currentType,
            int currentTypeArity)
        {
            if (!string.Equals(
                    candidate.DefinitionId,
                    currentType.DefinitionId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (candidate.TypeArgumentIdentities == null)
            {
                return true;
            }

            return candidate.TypeArgumentIdentities.Count == currentTypeArity
                && candidate.TypeArgumentIdentities.Select(identity => identity.Text)
                    .SequenceEqual(
                        Enumerable.Range(0, currentTypeArity)
                            .Select(index => TypeIdentityTemplate.TypeParameter(index).Text),
                        StringComparer.Ordinal);
        }

        // 根据托管函数引用生成与定义相同的函数身份。
        private static MethodIdentityTemplate ManagedMethodId(
            MetadataReader reader,
            MetadataTypeProvider provider,
            EntityHandle handle)
        {
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                MethodDefinition definition = reader.GetMethodDefinition(
                    (MethodDefinitionHandle)handle);
                TypeDefinitionHandle typeHandle = definition.GetDeclaringType();
                CatalogType type = provider.GetTypeFromDefinition(reader, typeHandle, 0);
                MethodSignature<CatalogType> signature = definition.DecodeSignature(provider, null);

                return new MethodIdentityTemplate(
                    type.TypeIdentity,
                    reader.GetString(definition.Name),
                    definition.GetGenericParameters().Count,
                    signature.ParameterTypes.Select(item => item.TypeIdentity).ToArray(),
                    signature.ReturnType.TypeIdentity);
            }

            if (handle.Kind != HandleKind.MemberReference)
            {
                throw new AnalysisException($"不支持的托管函数身份句柄：{handle.Kind}");
            }

            MemberReference reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            MethodSignature<CatalogType> memberSignature = reference.DecodeMethodSignature(
                provider,
                null);

            return new MethodIdentityTemplate(
                provider.GetType(reference.Parent).TypeIdentity,
                reader.GetString(reference.Name),
                memberSignature.GenericParameterCount,
                memberSignature.ParameterTypes.Select(item => item.TypeIdentity).ToArray(),
                memberSignature.ReturnType.TypeIdentity);
        }

        // 把托管参数元数据转换为统一参数记录。
        private static ParameterEntry CreateManagedParameter(
            MetadataReader reader,
            CatalogType type,
            Parameter? parameter,
            int index)
        {
            bool isByReference = type.IsByReference;
            CatalogRefKind refKind = !isByReference
                ? CatalogRefKind.None
                : parameter is { } metadata
                    && (metadata.Attributes & ParameterAttributes.Out) != 0
                    ? CatalogRefKind.Out
                    : parameter is { } input
                        && (input.Attributes & ParameterAttributes.In) != 0
                        ? CatalogRefKind.In
                        : CatalogRefKind.Ref;

            return new ParameterEntry(
                parameter == null || parameter.Value.Name.IsNil
                    ? $"arg{index}"
                    : reader.GetString(parameter.Value.Name),
                isByReference ? type.Name[..^1] : type.Name,
                type.Id,
                type.MatchId,
                refKind)
            {
                TypeIdentity = type.TypeIdentity,
                MatchTypeIdentity = type.MatchTypeIdentity,
            };
        }

        // 把托管函数元数据映射为稳定的项目枚举。
        private static CatalogMethodKind GetManagedMethodKind(
            MethodDefinition definition,
            string methodName,
            CatalogMethodKind? accessorKind)
        {
            if (accessorKind != null)
            {
                return accessorKind.Value;
            }

            bool isSpecialName = (definition.Attributes & MethodAttributes.SpecialName) != 0;

            return methodName switch
            {
                ".ctor" => CatalogMethodKind.Constructor,
                ".cctor" => CatalogMethodKind.StaticConstructor,
                "op_Implicit" or "op_Explicit" or "op_CheckedExplicit"
                    when isSpecialName => CatalogMethodKind.Conversion,
                _ when isSpecialName && methodName.StartsWith("op_", StringComparison.Ordinal) =>
                    CatalogMethodKind.Operator,
                _ => CatalogMethodKind.Ordinary,
            };
        }

        // 按构造类型实参生成一个定义函数在该类型上的实际身份。
        internal static string InstantiateMethodId(
            MethodEntry method,
            TypeIdentityTemplate instanceTypeIdentity,
            IReadOnlyList<TypeIdentityTemplate> typeArguments)
        {
            return method.Identity.Instantiate(instanceTypeIdentity, typeArguments).Text;
        }

        // 判断元数据声明身份是否指向构造类型上的目标函数定义。
        internal static bool IsDeclarationOf(
            MethodIdentityTemplate declaration,
            MethodEntry target,
            TypeIdentityTemplate instanceTypeIdentity,
            IReadOnlyList<TypeIdentityTemplate> typeArguments,
            Func<string, string> canonicalizeNamedType)
        {
            string declarationId = declaration.Canonicalize(canonicalizeNamedType).Text;
            if (string.Equals(
                declarationId,
                target.Identity.Instantiate(instanceTypeIdentity, typeArguments)
                    .Canonicalize(canonicalizeNamedType).Text,
                StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(
                declarationId,
                target.Identity.WithDeclaringType(instanceTypeIdentity)
                    .Canonicalize(canonicalizeNamedType).Text,
                StringComparison.Ordinal);
        }

        // 比较实现函数与一个构造基类或接口函数的名称和实际签名。
        internal static bool HasMatchingSignature(
            MethodEntry method,
            MethodEntry target,
            IReadOnlyList<TypeIdentityTemplate> targetTypeArguments,
            IReadOnlyList<TypeIdentityTemplate> targetMatchTypeArguments,
            bool isInterface,
            Func<TypeIdentityTemplate, string> normalizeTypeIdentity)
        {
            if (!string.Equals(method.Name, target.Name, StringComparison.Ordinal)
                || method.GenericArity != target.GenericArity
                || method.Parameters.Count != target.Parameters.Count)
            {
                return false;
            }

            bool parametersMatch = method.Parameters.Zip(target.Parameters).All(pair =>
                pair.First.RefKind == pair.Second.RefKind
                && string.Equals(
                    normalizeTypeIdentity(isInterface
                        ? pair.First.MatchTypeIdentity
                        : pair.First.TypeIdentity),
                    normalizeTypeIdentity((isInterface
                            ? pair.Second.MatchTypeIdentity
                            : pair.Second.TypeIdentity)
                        .Substitute(isInterface
                            ? targetMatchTypeArguments
                            : targetTypeArguments)),
                    StringComparison.Ordinal));

            return parametersMatch
                && (!isInterface || string.Equals(
                    normalizeTypeIdentity(method.ReturnMatchTypeIdentity),
                    normalizeTypeIdentity(target.ReturnMatchTypeIdentity
                        .Substitute(targetMatchTypeArguments)),
                    StringComparison.Ordinal));
        }

        // 去掉用户显示名称中的托管泛型元数后缀。
        private static string RemoveGenericArity(string name, bool isGeneric)
        {
            if (!isGeneric)
            {
                return name;
            }

            int separator = name.LastIndexOf('`');

            return separator < 0 ? name : name[..separator];
        }

        /// <summary>
        /// 保存一份源码程序集在函数总表阶段共享的符号和类型名称缓存。
        /// </summary>
        private sealed record SourceCatalogContext(
            SourceAssemblyMaterial Material,
            IReadOnlyList<INamedTypeSymbol> Types,
            IReadOnlySet<string> ReportablePaths,
            IReadOnlyDictionary<string, IReadOnlyList<string>> ManagedTypeIdsByNameKey,
            IReadOnlyDictionary<string, string> AssemblyNames)
        {
            public ConcurrentDictionary<ITypeSymbol, string> TypeNames { get; } = new(
                SymbolEqualityComparer.Default);

            public ConcurrentDictionary<ITypeSymbol, TypeIdentityTemplate> TypeIdentities { get; } =
                new(SymbolEqualityComparer.Default);

            public ConcurrentDictionary<ITypeSymbol, TypeIdentityTemplate> MatchTypeIdentities { get; } =
                new(SymbolEqualityComparer.Default);
        }

        /// <summary>
        /// 表示一个等待读取的源码类型或托管文件。
        /// </summary>
        private sealed record CatalogTypeWorkItem(
            SourceCatalogContext? Context,
            INamedTypeSymbol? SourceType,
            string? AssemblyPath);

        /// <summary>
        /// 表示一个等待读取成员函数的源码类型或源码文件。
        /// </summary>
        private sealed record CatalogMethodWorkItem(
            SourceCatalogContext Context,
            INamedTypeSymbol? SourceType,
            SyntaxTree? SyntaxTree);

        /// <summary>
        /// 保存一个并行工作项读取到的类型和程序集地图。
        /// </summary>
        private sealed record CatalogPart(
            IReadOnlyList<TypeEntry> Types,
            AssemblyTypeMap? AssemblyMap);

        /// <summary>
        /// 保存一个托管文件定义的类型和逐类型转交目标。
        /// </summary>
        private sealed record AssemblyTypeMap(
            string Path,
            string AssemblyName,
            IReadOnlySet<string> DefinitionNameKeys,
            IReadOnlyDictionary<string, string> ForwardedAssembliesByNameKey);

        /// <summary>
        /// 保存一个托管文件的程序集定义名称和直接引用名称。
        /// </summary>
        private sealed record AssemblyNameInfo(
            string Path,
            string DefinitionName,
            IReadOnlyList<string> ReferenceNames);

        /// <summary>
        /// 保存托管命名类型的命名空间和每层元数据名称。
        /// </summary>
        private sealed record ManagedTypeName(
            string NamespaceName,
            IReadOnlyList<string> MetadataNames)
        {
            public string Key => NamedTypeNameKey(this.NamespaceName, this.MetadataNames);
        }

        /// <summary>
        /// 保存函数指针扩展调用约定修饰符的类型和必需性。
        /// </summary>
        private readonly record struct CatalogCustomModifier(
            string TypeId,
            bool IsRequired,
            bool IsCallingConventionModifier);

        /// <summary>
        /// 保存托管签名中的显示名称和稳定类型身份。
        /// </summary>
        private readonly record struct CatalogType(
            string Name,
            string Id,
            string MatchId,
            string? DefinitionId = null,
            IReadOnlyList<string>? TypeArguments = null,
            IReadOnlyList<string>? MatchTypeArguments = null,
            IReadOnlyList<CatalogCustomModifier>? CustomModifiers = null,
            bool IsCallingConventionModifier = false,
            bool IsByReference = false,
            string? NameKey = null)
        {
            public TypeIdentityTemplate TypeIdentity { get; init; } =
                TypeIdentityTemplate.Literal(Id);

            public TypeIdentityTemplate MatchTypeIdentity { get; init; } =
                TypeIdentityTemplate.Literal(MatchId);

            public IReadOnlyList<TypeIdentityTemplate>? TypeArgumentIdentities { get; init; } =
                TypeArguments?.Select(TypeIdentityTemplate.Literal).ToArray();

            public IReadOnlyList<TypeIdentityTemplate>? MatchTypeArgumentIdentities { get; init; } =
                MatchTypeArguments?.Select(TypeIdentityTemplate.Literal).ToArray();
        }

        /// <summary>
        /// 使用 .NET 自带元数据读取器把托管签名转换成统一类型身份。
        /// </summary>
        private sealed class MetadataTypeProvider : ISignatureTypeProvider<CatalogType, object?>
        {
            private readonly MetadataReader m_reader;
            private readonly string m_assemblyName;
            private readonly IReadOnlyDictionary<string, string> m_callingConventionTypes;
            private readonly IReadOnlyDictionary<string, string> m_assemblyNames;

            // 保存当前托管文件及其统一名称表。
            /// <summary>
            /// 创建一个托管类型签名读取器。
            /// </summary>
            public MetadataTypeProvider(
                MetadataReader reader,
                string assemblyName,
                IReadOnlyDictionary<string, string> callingConventionTypes,
                IReadOnlyDictionary<string, string> assemblyNames)
            {
                this.m_reader = reader;
                this.m_assemblyName = assemblyName;
                this.m_callingConventionTypes = callingConventionTypes;
                this.m_assemblyNames = assemblyNames;
            }

            // 按元数据句柄种类读取类型。
            /// <summary>
            /// 读取一个定义、引用或构造类型句柄。
            /// </summary>
            public CatalogType GetType(EntityHandle handle)
            {
                return handle.Kind switch
                {
                    HandleKind.TypeDefinition => this.GetTypeFromDefinition(
                        this.m_reader,
                        (TypeDefinitionHandle)handle,
                        0),
                    HandleKind.TypeReference => this.GetTypeFromReference(
                        this.m_reader,
                        (TypeReferenceHandle)handle,
                        0),
                    HandleKind.TypeSpecification => this.GetTypeFromSpecification(
                        this.m_reader,
                        null,
                        (TypeSpecificationHandle)handle,
                        0),
                    _ => throw new AnalysisException($"不支持的托管类型句柄：{handle.Kind}"),
                };
            }

            // 为元素类型附加多维数组结构。
            /// <summary>
            /// 生成一个多维数组类型。
            /// </summary>
            public CatalogType GetArrayType(CatalogType elementType, ArrayShape shape)
            {
                return AppendType(elementType, $"[{new string(',', shape.Rank - 1)}]");
            }

            // 为元素类型附加引用参数结构。
            /// <summary>
            /// 生成一个引用参数类型。
            /// </summary>
            public CatalogType GetByReferenceType(CatalogType elementType)
            {
                CatalogType result = AppendType(elementType, "&");

                return result with { IsByReference = true };
            }

            // 按完整元数据签名生成函数指针身份。
            /// <summary>
            /// 生成一个函数指针类型。
            /// </summary>
            public CatalogType GetFunctionPointerType(MethodSignature<CatalogType> signature)
            {
                string convention = signature.Header.CallingConvention.ToString();
                CatalogCustomModifier[] conventionModifiers =
                    signature.Header.CallingConvention == SignatureCallingConvention.Unmanaged
                        ? (signature.ReturnType.CustomModifiers
                                ?? Array.Empty<CatalogCustomModifier>())
                            .Where(modifier => modifier.IsCallingConventionModifier)
                            .ToArray()
                        : Array.Empty<CatalogCustomModifier>();
                TypeIdentityTemplate nameIdentity = BuildFunctionPointerIdentityTemplate(
                    convention,
                    signature.GenericParameterCount,
                    signature.RequiredParameterCount,
                    signature.Header.IsInstance,
                    signature.Header.HasExplicitThis,
                    conventionModifiers,
                    signature.ParameterTypes.Select(type => TypeIdentityTemplate.Literal(type.Name)),
                    TypeIdentityTemplate.Literal(signature.ReturnType.Name));
                TypeIdentityTemplate typeIdentity = BuildFunctionPointerIdentityTemplate(
                    convention,
                    signature.GenericParameterCount,
                    signature.RequiredParameterCount,
                    signature.Header.IsInstance,
                    signature.Header.HasExplicitThis,
                    conventionModifiers,
                    signature.ParameterTypes.Select(type => type.TypeIdentity),
                    signature.ReturnType.TypeIdentity);
                TypeIdentityTemplate matchIdentity = BuildFunctionPointerIdentityTemplate(
                    convention,
                    signature.GenericParameterCount,
                    signature.RequiredParameterCount,
                    signature.Header.IsInstance,
                    signature.Header.HasExplicitThis,
                    conventionModifiers,
                    signature.ParameterTypes.Select(type => type.MatchTypeIdentity),
                    signature.ReturnType.MatchTypeIdentity);

                return new CatalogType(nameIdentity.Text, typeIdentity.Text, matchIdentity.Text)
                {
                    TypeIdentity = typeIdentity,
                    MatchTypeIdentity = matchIdentity,
                };
            }

            // 生成函数泛型参数占位符。
            /// <summary>
            /// 生成一个函数泛型参数类型。
            /// </summary>
            public CatalogType GetGenericMethodParameter(object? genericContext, int index)
            {
                return new CatalogType($"!!{index}", $"!!{index}", $"!!{index}");
            }

            // 组合泛型定义与全部实际类型参数。
            /// <summary>
            /// 生成一个构造泛型类型。
            /// </summary>
            public CatalogType GetGenericInstantiation(
                CatalogType genericType,
                ImmutableArray<CatalogType> typeArguments)
            {
                TypeIdentityTemplate typeIdentity = genericType.TypeIdentity.Append(
                    TypeIdentityTemplate.Join(
                        "<",
                        typeArguments.Select(type => type.TypeIdentity),
                        ",",
                        ">"));
                TypeIdentityTemplate matchIdentity = genericType.MatchTypeIdentity.Append(
                    TypeIdentityTemplate.Join(
                        "<",
                        typeArguments.Select(type => type.MatchTypeIdentity),
                        ",",
                        ">"));

                return new CatalogType(
                    $"{genericType.Name}<{string.Join(",", typeArguments.Select(type => type.Name))}>",
                    typeIdentity.Text,
                    matchIdentity.Text,
                    genericType.DefinitionId ?? genericType.Id,
                    typeArguments.Select(type => type.Id).ToArray(),
                    typeArguments.Select(type => type.MatchId).ToArray())
                {
                    TypeIdentity = typeIdentity,
                    MatchTypeIdentity = matchIdentity,
                    TypeArgumentIdentities = typeArguments.Select(type => type.TypeIdentity).ToArray(),
                    MatchTypeArgumentIdentities = typeArguments
                        .Select(type => type.MatchTypeIdentity)
                        .ToArray(),
                };
            }

            // 生成类型泛型参数占位符。
            /// <summary>
            /// 生成一个类型泛型参数类型。
            /// </summary>
            public CatalogType GetGenericTypeParameter(object? genericContext, int index)
            {
                TypeIdentityTemplate identity = TypeIdentityTemplate.TypeParameter(index);

                return new CatalogType(identity.Text, identity.Text, identity.Text)
                {
                    TypeIdentity = identity,
                    MatchTypeIdentity = identity,
                };
            }

            // 保留元数据修饰符且让匹配身份忽略修饰符。
            /// <summary>
            /// 为基础类型附加必需或可选修饰符。
            /// </summary>
            public CatalogType GetModifiedType(
                CatalogType modifier,
                CatalogType unmodifiedType,
                bool isRequired)
            {
                string modifierName = isRequired ? "modreq" : "modopt";
                TypeIdentityTemplate identity = unmodifiedType.TypeIdentity
                    .Append($" {modifierName}(")
                    .Append(modifier.TypeIdentity)
                    .Append(")");

                return unmodifiedType with
                {
                    Id = identity.Text,
                    TypeIdentity = identity,
                    CustomModifiers = (unmodifiedType.CustomModifiers
                            ?? Array.Empty<CatalogCustomModifier>())
                        .Append(new CatalogCustomModifier(
                            modifier.Id,
                            isRequired,
                            modifier.IsCallingConventionModifier))
                        .ToArray(),
                };
            }

            // 为元素类型附加固定地址结构。
            /// <summary>
            /// 生成一个固定地址类型。
            /// </summary>
            public CatalogType GetPinnedType(CatalogType elementType)
            {
                return AppendType(elementType, " pinned");
            }

            // 为元素类型附加指针结构。
            /// <summary>
            /// 生成一个指针类型。
            /// </summary>
            public CatalogType GetPointerType(CatalogType elementType)
            {
                return AppendType(elementType, "*");
            }

            // 把基础类型编码为函数签名使用的统一名称。
            /// <summary>
            /// 生成一个基础运行时类型。
            /// </summary>
            public CatalogType GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                string name = PrimitiveTypeName(typeCode);

                return new CatalogType(name, name, name);
            }

            // 按定义所在程序集和结构名称读取命名类型。
            /// <summary>
            /// 读取当前文件中的类型定义。
            /// </summary>
            public CatalogType GetTypeFromDefinition(
                MetadataReader reader,
                TypeDefinitionHandle handle,
                byte rawTypeKind)
            {
                ManagedTypeName name = ReadDefinitionName(reader, handle);

                return this.CreateNamedType(this.m_assemblyName, name);
            }

            // 按最外层引用范围和结构名称读取命名类型。
            /// <summary>
            /// 读取当前文件中的类型引用。
            /// </summary>
            public CatalogType GetTypeFromReference(
                MetadataReader reader,
                TypeReferenceHandle handle,
                byte rawTypeKind)
            {
                TypeReference reference = reader.GetTypeReference(handle);
                ManagedTypeName name = ReadReferenceName(reader, handle);
                string assemblyName = CanonicalAssemblyName(
                    this.m_assemblyNames,
                    this.GetReferenceAssemblyName(reference.ResolutionScope));

                return this.CreateNamedType(assemblyName, name);
            }

            // 让元数据读取器继续解码构造类型。
            /// <summary>
            /// 读取一个类型规格。
            /// </summary>
            public CatalogType GetTypeFromSpecification(
                MetadataReader reader,
                object? genericContext,
                TypeSpecificationHandle handle,
                byte rawTypeKind)
            {
                return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
            }

            // 为元素类型附加一维数组结构。
            /// <summary>
            /// 生成一个一维数组类型。
            /// </summary>
            public CatalogType GetSZArrayType(CatalogType elementType)
            {
                return AppendType(elementType, "[]");
            }

            // 创建带程序集身份的命名类型并识别已确认的调用约定类型。
            private CatalogType CreateNamedType(string assemblyName, ManagedTypeName managedName)
            {
                string metadataName = managedName.MetadataNames[^1];
                string? callingConventionTypeId = this.GetCallingConventionTypeId(
                    assemblyName,
                    managedName.NamespaceName,
                    metadataName,
                    managedName.MetadataNames.Count > 1);
                string id = callingConventionTypeId ?? NamedTypeId(assemblyName, managedName.Key);
                string nestedName = string.Join("+", managedName.MetadataNames);
                string fullName = managedName.NamespaceName.Length == 0
                    ? nestedName
                    : $"{managedName.NamespaceName}.{nestedName}";
                TypeIdentityTemplate identity = TypeIdentityTemplate.NamedType(id);

                return new CatalogType(
                    fullName,
                    id,
                    id,
                    id,
                    IsCallingConventionModifier: callingConventionTypeId != null,
                    NameKey: managedName.Key)
                {
                    TypeIdentity = identity,
                    MatchTypeIdentity = identity,
                };
            }

            // 只把预扫描确认存在且元数为零的调用约定类型映射到统一身份。
            private string? GetCallingConventionTypeId(
                string assemblyName,
                string namespaceName,
                string metadataName,
                bool isNested)
            {
                if (isNested
                    || namespaceName != "System.Runtime.CompilerServices"
                    || metadataName == "CallConv"
                    || !metadataName.StartsWith("CallConv", StringComparison.Ordinal))
                {
                    return null;
                }

                return this.m_callingConventionTypes.GetValueOrDefault(
                    CallingConventionTypeKey(
                        assemblyName,
                        NamedTypeNameKey(namespaceName, new[] { metadataName })));
            }

            // 沿嵌套引用找到最外层程序集范围。
            private string GetReferenceAssemblyName(EntityHandle scope)
            {
                while (scope.Kind == HandleKind.TypeReference)
                {
                    scope = this.m_reader.GetTypeReference(
                        (TypeReferenceHandle)scope).ResolutionScope;
                }

                return scope.Kind switch
                {
                    HandleKind.AssemblyReference => this.m_reader.GetString(
                        this.m_reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                    HandleKind.ModuleDefinition or HandleKind.ModuleReference => this.m_assemblyName,
                    _ => throw new AnalysisException($"不支持的类型引用范围：{scope.Kind}"),
                };
            }

            // 为数组、指针或引用类型附加结构后缀。
            private static CatalogType AppendType(CatalogType elementType, string suffix)
            {
                return new CatalogType(
                    elementType.Name + suffix,
                    elementType.Id + suffix,
                    elementType.MatchId + suffix)
                {
                    TypeIdentity = elementType.TypeIdentity.Append(suffix),
                    MatchTypeIdentity = elementType.MatchTypeIdentity.Append(suffix),
                };
            }

            // 读取一个可能嵌套的类型定义结构名称。
            private static ManagedTypeName ReadDefinitionName(
                MetadataReader reader,
                TypeDefinitionHandle handle)
            {
                TypeDefinition definition = reader.GetTypeDefinition(handle);
                TypeDefinitionHandle parentHandle = definition.GetDeclaringType();
                string name = reader.GetString(definition.Name);
                if (parentHandle.IsNil)
                {
                    return new ManagedTypeName(
                        reader.GetString(definition.Namespace),
                        new[] { name });
                }

                ManagedTypeName parent = ReadDefinitionName(reader, parentHandle);

                return new ManagedTypeName(
                    parent.NamespaceName,
                    parent.MetadataNames.Append(name).ToArray());
            }

            // 读取一个可能嵌套的类型引用结构名称。
            private static ManagedTypeName ReadReferenceName(
                MetadataReader reader,
                TypeReferenceHandle handle)
            {
                TypeReference reference = reader.GetTypeReference(handle);
                string name = reader.GetString(reference.Name);
                if (reference.ResolutionScope.Kind != HandleKind.TypeReference)
                {
                    return new ManagedTypeName(
                        reader.GetString(reference.Namespace),
                        new[] { name });
                }

                ManagedTypeName parent = ReadReferenceName(
                    reader,
                    (TypeReferenceHandle)reference.ResolutionScope);

                return new ManagedTypeName(
                    parent.NamespaceName,
                    parent.MetadataNames.Append(name).ToArray());
            }
        }

    }

    /// <summary>
    /// 保存类型身份中的普通文字与明确的类型泛型参数位置。
    /// </summary>
    internal sealed record TypeIdentityTemplate(IReadOnlyList<TypeIdentityPart> Parts)
    {
        internal string Text => string.Concat(this.Parts.Select(part => part.Text));

        internal string StableText => string.Concat(this.Parts.Select(part =>
            part.NamedTypeId == null
                ? part.Text
                : $"N{part.NamedTypeId.Length}:{part.NamedTypeId}"));

        // 把确定不会参与泛型替换的内容保存为普通文字。
        internal static TypeIdentityTemplate Literal(string text)
        {
            return new TypeIdentityTemplate(new[] { new TypeIdentityPart(text, null) });
        }

        // 把明确的命名类型保存为可进行门面映射的结构节点。
        internal static TypeIdentityTemplate NamedType(string typeId)
        {
            return new TypeIdentityTemplate(new[] { new TypeIdentityPart(typeId, null, typeId) });
        }

        // 建立一个只会在类型实例化时替换的泛型参数位置。
        internal static TypeIdentityTemplate TypeParameter(int index)
        {
            return new TypeIdentityTemplate(new[] { new TypeIdentityPart($"!{index}", index) });
        }

        // 用前后文字和分隔符组合一组结构化类型身份。
        internal static TypeIdentityTemplate Join(
            string prefix,
            IEnumerable<TypeIdentityTemplate> items,
            string separator,
            string suffix)
        {
            List<TypeIdentityPart> parts = new() { new TypeIdentityPart(prefix, null) };
            bool hasPrevious = false;

            foreach (TypeIdentityTemplate item in items)
            {
                if (hasPrevious)
                {
                    parts.Add(new TypeIdentityPart(separator, null));
                }

                parts.AddRange(item.Parts);
                hasPrevious = true;
            }

            parts.Add(new TypeIdentityPart(suffix, null));

            return new TypeIdentityTemplate(parts);
        }

        // 在类型身份后面附加确定的普通文字。
        internal TypeIdentityTemplate Append(string text)
        {
            return new TypeIdentityTemplate(
                this.Parts.Append(new TypeIdentityPart(text, null)).ToArray());
        }

        // 在当前身份后面附加另一个结构化类型身份。
        internal TypeIdentityTemplate Append(TypeIdentityTemplate identity)
        {
            return new TypeIdentityTemplate(this.Parts.Concat(identity.Parts).ToArray());
        }

        // 用明确的结构化实参替换类型泛型参数位置。
        internal TypeIdentityTemplate Substitute(IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            List<TypeIdentityPart> parts = new();

            foreach (TypeIdentityPart part in this.Parts)
            {
                if (part.TypeParameterIndex is int index && index < arguments.Count)
                {
                    parts.AddRange(arguments[index].Parts);
                }
                else
                {
                    parts.Add(part);
                }
            }

            return new TypeIdentityTemplate(parts);
        }

        // 只对明确的命名类型节点应用参考文件到真实定义的映射。
        internal TypeIdentityTemplate Canonicalize(Func<string, string> canonicalizeNamedType)
        {
            return new TypeIdentityTemplate(this.Parts.Select(part =>
            {
                if (part.NamedTypeId == null)
                {
                    return part;
                }

                string canonicalTypeId = canonicalizeNamedType(part.NamedTypeId);

                return new TypeIdentityPart(canonicalTypeId, null, canonicalTypeId);
            }).ToArray());
        }
    }

    /// <summary>
    /// 保存类型身份中的一段普通文字或一个明确的类型泛型参数。
    /// </summary>
    internal readonly record struct TypeIdentityPart(
        string Text,
        int? TypeParameterIndex,
        string? NamedTypeId = null);

    /// <summary>
    /// 保存函数声明类型、名称、泛型元数、参数和返回类型的结构化身份。
    /// </summary>
    internal sealed record MethodIdentityTemplate(
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
                string parameters = string.Concat(this.Parameters.Select(item =>
                {
                    string identity = item.StableText;

                    return $"{identity.Length}:{identity}";
                }));
                string returnType = this.ReturnType.StableText;

                return $"{this.DeclaringType.StableText}::{this.Name.Length}:{this.Name}{arity}"
                    + $"({this.Parameters.Count}:{parameters})"
                    + $"->{returnType.Length}:{returnType}";
            }
        }

        // 用构造类型及其明确实参生成实际函数身份。
        internal MethodIdentityTemplate Instantiate(
            TypeIdentityTemplate instanceTypeIdentity,
            IReadOnlyList<TypeIdentityTemplate> typeArguments)
        {
            return this with
            {
                DeclaringType = instanceTypeIdentity,
                Parameters = this.Parameters
                    .Select(item => item.Substitute(typeArguments))
                    .ToArray(),
                ReturnType = this.ReturnType.Substitute(typeArguments),
            };
        }

        // 用实际声明类型替换函数身份中的定义类型但不实例化参数。
        internal MethodIdentityTemplate WithDeclaringType(TypeIdentityTemplate instanceTypeIdentity)
        {
            return this with
            {
                DeclaringType = instanceTypeIdentity,
            };
        }

        // 对函数身份中每一个明确命名类型节点应用门面映射。
        internal MethodIdentityTemplate Canonicalize(Func<string, string> canonicalizeNamedType)
        {
            return this with
            {
                DeclaringType = this.DeclaringType.Canonicalize(canonicalizeNamedType),
                Parameters = this.Parameters
                    .Select(item => item.Canonicalize(canonicalizeNamedType))
                    .ToArray(),
                ReturnType = this.ReturnType.Canonicalize(canonicalizeNamedType),
            };
        }
    }

    /// <summary>
    /// 表示函数在源码或托管文件中的种类。
    /// </summary>
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

    /// <summary>
    /// 表示函数参数的传递方式。
    /// </summary>
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

    /// <summary>
    /// 保存后续标签判断需要的特性信息。
    /// </summary>
    public sealed record AttributeEntry(
        string TypeName,
        bool HasArguments,
        IReadOnlyList<string> ArgumentValues);

    /// <summary>
    /// 保存一个函数参数的名称、类型和传递方式。
    /// </summary>
    public sealed record ParameterEntry(
        string Name,
        string TypeName,
        string TypeId,
        string MatchTypeId,
        CatalogRefKind RefKind)
    {
        internal TypeIdentityTemplate TypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(TypeId);

        internal TypeIdentityTemplate MatchTypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(MatchTypeId);
    }

    /// <summary>
    /// 保存基类或接口定义及其已经解码的实际泛型参数。
    /// </summary>
    public sealed record TypeRelationEntry(
        string DefinitionId,
        string TypeId,
        string MatchTypeId,
        IReadOnlyList<string> TypeArguments,
        IReadOnlyList<string> MatchTypeArguments)
    {
        internal TypeIdentityTemplate TypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(TypeId);

        internal TypeIdentityTemplate MatchTypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(MatchTypeId);

        internal IReadOnlyList<TypeIdentityTemplate> TypeArgumentIdentities { get; init; } =
            TypeArguments.Select(TypeIdentityTemplate.Literal).ToArray();

        internal IReadOnlyList<TypeIdentityTemplate> MatchTypeArgumentIdentities { get; init; } =
            MatchTypeArguments.Select(TypeIdentityTemplate.Literal).ToArray();
    }

    /// <summary>
    /// 保存一个类型及其直接继承关系。
    /// </summary>
    public sealed record TypeEntry(
        string Id,
        string LogicalId,
        IReadOnlyList<string> AliasIds,
        string AssemblyName,
        string Name,
        string FullName,
        string NameKey,
        TypeRelationEntry? BaseType,
        IReadOnlyList<TypeRelationEntry> Interfaces,
        IReadOnlyList<AttributeEntry> Attributes,
        bool IsInterface,
        INamedTypeSymbol? SourceSymbol,
        string? AssemblyPath,
        int MetadataToken);

    /// <summary>
    /// 保存一个函数的稳定身份、来源、参数、标签和统计属性。
    /// </summary>
    public sealed record MethodEntry(
        string Id,
        string LogicalId,
        string AssemblyName,
        string TypeId,
        string TypeName,
        string Name,
        string ReturnTypeName,
        string ReturnTypeId,
        string ReturnMatchTypeId,
        IReadOnlyList<ParameterEntry> Parameters,
        CatalogMethodKind Kind,
        IReadOnlyList<AttributeEntry> MethodAttributes,
        IReadOnlyList<AttributeEntry> TypeAttributes,
        IReadOnlyList<string> RelatedMethodIds,
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
        internal MethodIdentityTemplate Identity { get; init; } = null!;

        internal IReadOnlyList<MethodIdentityTemplate> RelatedMethodIdentities { get; init; } =
            Array.Empty<MethodIdentityTemplate>();

        internal TypeIdentityTemplate ReturnTypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(ReturnTypeId);

        internal TypeIdentityTemplate ReturnMatchTypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(ReturnMatchTypeId);
    }

    /// <summary>
    /// 保存函数总表和后续模块直接使用的查找索引。
    /// </summary>
    public sealed class MethodCatalogResult
    {
        private readonly ConcurrentDictionary<string, IReadOnlyList<MethodEntry>>
            m_managedMethodsByTypeId = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> m_callingConventionTypes;
        private readonly IReadOnlyDictionary<string, string> m_assemblyNames;
        private readonly IReadOnlyDictionary<string, string> m_typeAliases;

        // 保存排好顺序的函数、类型和关系索引。
        internal MethodCatalogResult(
            IReadOnlyList<MethodEntry> methods,
            IReadOnlyList<TypeEntry> types,
            IReadOnlyDictionary<string, string> callingConventionTypes,
            IReadOnlyDictionary<string, string> assemblyNames,
            System.Diagnostics.Stopwatch stopwatch,
            int jobs)
        {
            this.m_callingConventionTypes = callingConventionTypes;
            this.m_assemblyNames = assemblyNames;
            this.Methods = methods;
            this.Types = types;
            IReadOnlyDictionary<string, string> typeAliases = BuildTypeAliasMap(types);
            IReadOnlyDictionary<string, MethodEntry> methodsById = null!;
            IReadOnlyDictionary<string, TypeEntry> typesById = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByName = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByLogicalId = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByTypeId = null!;
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> typesByLogicalId = null!;
            ParallelOptions options = new() { MaxDegreeOfParallelism = jobs };

            Parallel.Invoke(
                options,
                () => methodsById = methods.ToDictionary(
                    method => method.Id,
                    StringComparer.Ordinal),
                () => typesById = types.ToDictionary(type => type.Id, StringComparer.Ordinal),
                () => methodsByName = Index(methods, method => method.Name, method => method.Id),
                () => methodsByLogicalId = Index(
                    methods,
                    method => method.LogicalId,
                    method => method.Id),
                () => methodsByTypeId = Index(methods, method => method.TypeId, method => method.Id),
                () => typesByLogicalId = IndexMany(
                    types,
                    type => type.AliasIds.Prepend(type.LogicalId).ToArray(),
                    type => type.Id));

            this.m_typeAliases = typeAliases;
            this.MethodsById = methodsById;
            this.TypesById = typesById;
            this.MethodsByName = methodsByName;
            this.MethodsByLogicalId = methodsByLogicalId;
            this.MethodsByTypeId = methodsByTypeId;
            this.TypesByLogicalId = typesByLogicalId;
            this.DerivedTypesByBaseId = IndexMany(
                types.Where(type => type.BaseType != null),
                type => ResolveTypeDefinitionIds(type.BaseType!.DefinitionId),
                type => type.Id);
            this.ImplementingTypesByInterfaceId = BuildInterfaceIndex(types);
            this.OverridesByMethodId = IndexMany(
                methods,
                method => method.RelatedMethodIds,
                method => method.Id);
            stopwatch.Stop();
            this.Elapsed = stopwatch.Elapsed;
        }

        /// <summary>启动时建立的全部源码函数。</summary>
        public IReadOnlyList<MethodEntry> Methods { get; }

        /// <summary>全部类型。</summary>
        public IReadOnlyList<TypeEntry> Types { get; }

        /// <summary>按唯一身份查找已经建立的源码函数。</summary>
        public IReadOnlyDictionary<string, MethodEntry> MethodsById { get; }

        /// <summary>按唯一身份查找类型。</summary>
        public IReadOnlyDictionary<string, TypeEntry> TypesById { get; }

        /// <summary>按真实身份或参考文件身份查找同名类型。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> TypesByLogicalId { get; }

        /// <summary>按简单名称查找已经建立的源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByName { get; }

        /// <summary>按不含物理路径的身份查找已经建立的同名源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByLogicalId { get; }

        /// <summary>按声明类型查找已经建立的源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByTypeId { get; }

        /// <summary>按基类的不含物理路径身份查找直接派生类型。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> DerivedTypesByBaseId { get; }

        /// <summary>按接口的不含物理路径身份查找全部实现类型。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> ImplementingTypesByInterfaceId { get; }

        /// <summary>按目标函数的不含物理路径身份查找源码中的显式实现和重写。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> OverridesByMethodId { get; }

        /// <summary>建立函数总表的耗时。</summary>
        public TimeSpan Elapsed { get; }

        // 读取源码类型函数，或按需读取一个托管类型的函数并在本次运行内保存。
        /// <summary>
        /// 获取指定类型的全部函数。
        /// </summary>
        public IReadOnlyList<MethodEntry> GetMethods(TypeEntry type)
        {
            if (type.AssemblyPath == null)
            {
                return this.MethodsByTypeId.GetValueOrDefault(type.Id)
                    ?? Array.Empty<MethodEntry>();
            }

            return this.m_managedMethodsByTypeId.GetOrAdd(
                type.Id,
                _ => ReadManagedMethods(type));
        }

        // 读取托管类型函数并补齐元数据中的实现和重写关系。
        private IReadOnlyList<MethodEntry> ReadManagedMethods(TypeEntry type)
        {
            IReadOnlyList<MethodEntry> methods = MethodCatalog.ReadManagedTypeMethods(
                type,
                this.m_callingConventionTypes,
                this.m_assemblyNames);
            IReadOnlyList<InheritedTypeRelation> inheritedTypes = ReadInheritedTypes(type);

            return methods
                .Select(method => CompleteManagedRelations(method, inheritedTypes))
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
        }

        // 为一个托管函数找出显式实现、隐式实现和普通重写目标。
        private MethodEntry CompleteManagedRelations(
            MethodEntry method,
            IReadOnlyList<InheritedTypeRelation> inheritedTypes)
        {
            HashSet<string> relatedMethodIds = new(StringComparer.Ordinal);
            HashSet<string> resolvedDeclarations = new(StringComparer.Ordinal);

            foreach (InheritedTypeRelation relation in inheritedTypes)
            {
                TypeEntry definition = relation.Definition
                    ?? throw new AnalysisException($"继承关系尚未闭合：{relation.TypeId}");

                foreach (MethodEntry target in GetMethods(definition))
                {
                    MethodIdentityTemplate? declaration = method.RelatedMethodIdentities
                        .FirstOrDefault(item =>
                        MethodCatalog.IsDeclarationOf(
                            item,
                            target,
                            relation.TypeIdentity,
                            relation.TypeArgumentIdentities,
                            CanonicalizeNamedTypeId));

                    if (declaration != null)
                    {
                        relatedMethodIds.Add(target.LogicalId);
                        resolvedDeclarations.Add(declaration.Text);
                    }

                    if (relation.IsInterface
                        && relation.IsDirect
                        && method.IsPublic
                        && !method.IsStatic
                        && !target.IsStatic
                            && MethodCatalog.HasMatchingSignature(
                                method,
                                target,
                                relation.TypeArgumentIdentities,
                                relation.MatchTypeArgumentIdentities,
                                true,
                                CanonicalizeTypeIdentity))
                    {
                        relatedMethodIds.Add(target.LogicalId);
                    }
                }
            }

            MethodIdentityTemplate? unresolvedDeclaration = method.RelatedMethodIdentities
                .FirstOrDefault(item => !resolvedDeclarations.Contains(item.Text));
            if (unresolvedDeclaration != null)
            {
                throw new AnalysisException(
                    $"无法定位托管函数显式实现目标：{method.Id} => {unresolvedDeclaration.Text}");
            }

            if (method.IsVirtual && !method.IsNewSlot)
            {
                IGrouping<int, InheritedTypeRelation>[] baseLevels = inheritedTypes
                        .Where(relation => !relation.IsInterface && relation.Definition != null)
                        .GroupBy(relation => relation.Depth)
                        .OrderBy(group => group.Key)
                        .ToArray();

                foreach (IGrouping<int, InheritedTypeRelation> level in baseLevels)
                {
                    string[] matches = level
                        .SelectMany(relation => GetMethods(relation.Definition!)
                            .Where(target => target.IsVirtual
                                && MethodCatalog.HasMatchingSignature(
                                    method,
                                    target,
                                    relation.TypeArgumentIdentities,
                                    relation.MatchTypeArgumentIdentities,
                                    false,
                                    CanonicalizeTypeIdentity)))
                        .Select(target => target.LogicalId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray();

                    if (matches.Length > 0)
                    {
                        relatedMethodIds.UnionWith(matches);
                        break;
                    }
                }
            }

            return method with
            {
                RelatedMethodIds = relatedMethodIds.Order(StringComparer.Ordinal).ToArray(),
            };
        }

        // 列出一个类型直接或间接继承的所有构造基类和接口。
        private IReadOnlyList<InheritedTypeRelation> ReadInheritedTypes(TypeEntry type)
        {
            Stack<InheritedTypeRelation> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<InheritedTypeRelation> result = new();

            PushInheritedTypes(
                pending,
                type,
                Array.Empty<TypeIdentityTemplate>(),
                Array.Empty<TypeIdentityTemplate>(),
                1,
                true);
            while (pending.Count > 0)
            {
                InheritedTypeRelation relation = pending.Pop();
                IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(
                    relation.DefinitionId,
                    relation.Owner);

                if (definitions.Count == 0)
                {
                    throw new AnalysisException(
                        $"无法定位托管继承目标：{relation.Owner.Id} => {relation.DefinitionId}");
                }

                foreach (TypeEntry definition in definitions)
                {
                    string key = $"{definition.Id.Length}:{definition.Id}"
                        + $"{relation.TypeId.Length}:{relation.TypeId}{relation.IsInterface}";

                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    result.Add(relation with { Definition = definition });
                    PushInheritedTypes(
                        pending,
                        definition,
                        relation.TypeArgumentIdentities,
                        relation.MatchTypeArgumentIdentities,
                        relation.Depth + 1,
                        relation.IsInterface && relation.IsDirect);
                }
            }

            return result;
        }

        // 把一个类型定义声明的基类和接口按当前泛型实参加入待处理列表。
        private static void PushInheritedTypes(
            Stack<InheritedTypeRelation> pending,
            TypeEntry definition,
            IReadOnlyList<TypeIdentityTemplate> instanceTypeArguments,
            IReadOnlyList<TypeIdentityTemplate> instanceMatchTypeArguments,
            int depth,
            bool interfacesAreDirect)
        {
            if (definition.BaseType != null)
            {
                pending.Push(CreateInheritedTypeRelation(
                    definition.BaseType,
                    instanceTypeArguments,
                    instanceMatchTypeArguments,
                    false,
                    definition,
                    false,
                    depth));
            }

            foreach (TypeRelationEntry interfaceType in definition.Interfaces.Reverse())
            {
                pending.Push(CreateInheritedTypeRelation(
                    interfaceType,
                    instanceTypeArguments,
                    instanceMatchTypeArguments,
                    true,
                    definition,
                    interfacesAreDirect,
                    depth));
            }
        }

        // 用当前构造类型参数实例化一条直接基类或接口关系。
        private static InheritedTypeRelation CreateInheritedTypeRelation(
            TypeRelationEntry relation,
            IReadOnlyList<TypeIdentityTemplate> instanceTypeArguments,
            IReadOnlyList<TypeIdentityTemplate> instanceMatchTypeArguments,
            bool isInterface,
            TypeEntry owner,
            bool isDirect,
            int depth)
        {
            return new InheritedTypeRelation(
                relation.DefinitionId,
                relation.TypeIdentity.Substitute(instanceTypeArguments),
                relation.MatchTypeIdentity.Substitute(instanceMatchTypeArguments),
                relation.TypeArgumentIdentities
                    .Select(argument => argument.Substitute(instanceTypeArguments))
                    .ToArray(),
                relation.MatchTypeArgumentIdentities
                    .Select(argument => argument.Substitute(instanceMatchTypeArguments))
                    .ToArray(),
                isInterface,
                owner,
                isDirect,
                null,
                depth);
        }

        // 按逻辑类型身份定位定义，并优先选择同一物理程序集中的类型。
        private IReadOnlyList<TypeEntry> FindTypeDefinitions(string definitionId, TypeEntry owner)
        {
            if (!this.TypesByLogicalId.TryGetValue(
                    definitionId,
                    out IReadOnlyList<TypeEntry>? candidates))
            {
                return Array.Empty<TypeEntry>();
            }

            TypeEntry[] sameAssembly = candidates
                .Where(candidate => string.Equals(
                    candidate.AssemblyPath,
                    owner.AssemblyPath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return sameAssembly.Length > 0 ? sameAssembly : candidates;
        }

        // 把参考文件类型身份转换为真实定义使用的全部逻辑身份。
        private IReadOnlyList<string> ResolveTypeDefinitionIds(string definitionId)
        {
            return this.TypesByLogicalId.TryGetValue(
                definitionId,
                out IReadOnlyList<TypeEntry>? definitions)
                ? definitions
                    .Select(definition => definition.LogicalId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : new[] { definitionId };
        }

        // 建立没有歧义的参考类型身份到真实类型身份映射。
        private static IReadOnlyDictionary<string, string> BuildTypeAliasMap(
            IReadOnlyList<TypeEntry> types)
        {
            var aliases = types
                .SelectMany(type => type.AliasIds.Select(alias => (
                    Alias: alias,
                    TypeId: type.LogicalId)))
                .GroupBy(item => item.Alias, StringComparer.Ordinal)
                .Select(group => (
                    Alias: group.Key,
                    TypeIds: group.Select(item => item.TypeId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray()))
                .OrderBy(item => item.Alias, StringComparer.Ordinal)
                .ToArray();
            var ambiguous = aliases.FirstOrDefault(item => item.TypeIds.Length > 1);
            if (ambiguous != default)
            {
                throw new AnalysisException(
                    $"参考类型身份对应多个真实类型：{ambiguous.Alias} => "
                    + string.Join("; ", ambiguous.TypeIds));
            }

            return aliases
                .ToDictionary(
                    item => item.Alias,
                    item => item.TypeIds[0],
                    StringComparer.Ordinal);
        }

        // 把一个明确命名类型节点中的门面身份替换为真实定义身份。
        private string CanonicalizeNamedTypeId(string typeId)
        {
            return this.m_typeAliases.GetValueOrDefault(typeId) ?? typeId;
        }

        // 只在结构化类型身份的命名类型节点中应用门面映射。
        private string CanonicalizeTypeIdentity(TypeIdentityTemplate identity)
        {
            return identity.Canonicalize(CanonicalizeNamedTypeId).StableText;
        }

        // 按一个键把项目分组，并固定组内顺序。
        private static IReadOnlyDictionary<string, IReadOnlyList<T>> Index<T>(
            IEnumerable<T> items,
            Func<T, string> keySelector,
            Func<T, string> orderSelector)
            where T : class
        {
            return items
                .GroupBy(keySelector, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<T>)group
                        .OrderBy(orderSelector, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        // 按每个项目声明的多个键建立反向关系索引。
        private static IReadOnlyDictionary<string, IReadOnlyList<T>> IndexMany<T>(
            IEnumerable<T> items,
            Func<T, IReadOnlyList<string>> keySelector,
            Func<T, string> orderSelector)
            where T : class
        {
            return items
                .SelectMany(item => keySelector(item).Select(key => (Key: key, Item: item)))
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<T>)group
                        .Select(pair => pair.Item)
                        .OrderBy(orderSelector, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        // 沿基类和接口关系建立包含继承实现的接口索引。
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> BuildInterfaceIndex(
            IReadOnlyList<TypeEntry> types)
        {
            return types
                .SelectMany(type => ReadInheritedInterfaceIds(type)
                    .Select(interfaceId => (InterfaceId: interfaceId, Type: type)))
                .GroupBy(item => item.InterfaceId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<TypeEntry>)group
                        .Select(item => item.Type)
                        .DistinctBy(type => type.Id, StringComparer.Ordinal)
                        .OrderBy(type => type.Id, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        // 只按定义关系读取接口索引所需的全部继承接口身份。
        private IReadOnlyList<string> ReadInheritedInterfaceIds(TypeEntry type)
        {
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface)> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            HashSet<string> interfaceIds = new(StringComparer.Ordinal);

            PushInterfaceIndexRelations(pending, type);
            while (pending.Count > 0)
            {
                (TypeRelationEntry relation, TypeEntry owner, bool isInterface) = pending.Pop();
                IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(
                    relation.DefinitionId,
                    owner);

                if (definitions.Count == 0)
                {
                    throw new AnalysisException(
                        $"无法定位托管继承目标：{owner.Id} => {relation.DefinitionId}");
                }

                foreach (TypeEntry definition in definitions)
                {
                    string key = $"{definition.Id.Length}:{definition.Id}{isInterface}";
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    if (isInterface)
                    {
                        interfaceIds.Add(definition.LogicalId);
                    }

                    PushInterfaceIndexRelations(pending, definition);
                }
            }

            return interfaceIds.Order(StringComparer.Ordinal).ToArray();
        }

        // 把一个定义的直接基类和接口放入接口索引待处理列表。
        private static void PushInterfaceIndexRelations(
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface)> pending,
            TypeEntry definition)
        {
            if (definition.BaseType != null)
            {
                pending.Push((definition.BaseType, definition, false));
            }

            foreach (TypeRelationEntry interfaceType in definition.Interfaces.Reverse())
            {
                pending.Push((interfaceType, definition, true));
            }
        }

        /// <summary>
        /// 保存沿基类或接口向上查找时的一条确定关系。
        /// </summary>
        private readonly record struct InheritedTypeRelation(
            string DefinitionId,
            TypeIdentityTemplate TypeIdentity,
            TypeIdentityTemplate MatchTypeIdentity,
            IReadOnlyList<TypeIdentityTemplate> TypeArgumentIdentities,
            IReadOnlyList<TypeIdentityTemplate> MatchTypeArgumentIdentities,
            bool IsInterface,
            TypeEntry Owner,
            bool IsDirect,
            TypeEntry? Definition,
            int Depth)
        {
            public string TypeId => this.TypeIdentity.Text;

            public string MatchTypeId => this.MatchTypeIdentity.Text;
        }
    }
}
