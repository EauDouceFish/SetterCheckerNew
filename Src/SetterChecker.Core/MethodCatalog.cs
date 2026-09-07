using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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
            IReadOnlyDictionary<string, string> assemblyNames = ReadAssemblyNames(
                material.SourceAssemblies,
                managedPaths,
                jobs);
            SourceCatalogContext[] sourceContexts = material.SourceAssemblies
                .Select(assembly => new SourceCatalogContext(
                    assembly,
                    assembly.ReportSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    assemblyNames))
                .ToArray();
            ConcurrentBag<ManagedAssemblyPart> managedParts = new();

            await Parallel.ForEachAsync(
                managedPaths,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (path, _) =>
                {
                    managedParts.Add(ReadManagedTypes(path, assemblyNames));

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            TypeEntry[] sourceTypes = sourceContexts
                .SelectMany(context => EnumerateTypes(
                    context.Material.Compilation.Assembly.GlobalNamespace)
                    .Select(type => CreateSourceType(type, context)))
                .ToArray();
            ManagedAssemblyPart[] orderedManagedParts = managedParts
                .OrderBy(part => part.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            TypeEntry[] managedTypes = orderedManagedParts.SelectMany(part => part.Types).ToArray();
            TypeEntry[] allTypes = AttachForwardedAliases(
                sourceTypes.Concat(managedTypes).ToArray(),
                orderedManagedParts.SelectMany(part => part.Forwarders).ToArray());
            IReadOnlyDictionary<string, TypeEntry> sourceTypesById = sourceTypes.ToDictionary(
                type => type.Id,
                StringComparer.Ordinal);
            ConcurrentBag<IReadOnlyList<MethodEntry>> sourceMethodParts = new();
            SourceMethodWorkItem[] workItems = sourceContexts
                .SelectMany(context => EnumerateTypes(
                        context.Material.Compilation.Assembly.GlobalNamespace)
                    .Select(type => new SourceMethodWorkItem(context, type, null))
                    .Concat(context.Material.Compilation.SyntaxTrees.Select(tree =>
                        new SourceMethodWorkItem(context, null, tree))))
                .ToArray();

            await Parallel.ForEachAsync(
                workItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (workItem, _) =>
                {
                    sourceMethodParts.Add(workItem.Type != null
                        ? ReadSourceTypeMethods(
                            workItem.Context,
                            workItem.Type,
                            sourceTypesById)
                        : ReadSourceNestedMethods(
                            workItem.Context,
                            workItem.Tree!,
                            sourceTypesById));

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            MethodEntry[] sourceMethods = sourceMethodParts
                .SelectMany(methods => methods)
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
            TypeEntry[] orderedTypes = allTypes.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();

            RequireUniqueMethods(sourceMethods);
            RequireUniqueTypes(orderedTypes);

            return new MethodCatalogResult(
                sourceMethods,
                orderedTypes,
                sourceContexts,
                assemblyNames,
                material.AssemblyLookupPaths,
                stopwatch,
                jobs);
        }

        // 读取全部实际程序集名称并固定名称大小写。
        private static IReadOnlyDictionary<string, string> ReadAssemblyNames(
            IReadOnlyList<SourceAssemblyMaterial> sourceAssemblies,
            IReadOnlyList<string> managedPaths,
            int jobs)
        {
            ConcurrentBag<string> names = new();
            Parallel.ForEach(
                managedPaths,
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                path =>
                {
                    using Cecil.ModuleDefinition module = OpenModule(path);
                    names.Add(module.Assembly.Name.Name);
                    foreach (Cecil.AssemblyNameReference reference in module.AssemblyReferences)
                    {
                        names.Add(reference.Name);
                    }
                });
            string[] allNames = sourceAssemblies
                .Select(assembly => assembly.Compilation.Assembly.Identity.Name)
                .Concat(sourceAssemblies.SelectMany(assembly => assembly.Compilation.ReferencedAssemblyNames)
                    .Select(identity => identity.Name))
                .Concat(names)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

            foreach (string name in allNames)
            {
                result.TryAdd(name, name);
            }

            return result;
        }

        // 延迟读取一个真实托管文件，避免仅建目录时解析无关特性依赖。
        private static Cecil.ModuleDefinition OpenModule(string path)
        {
            return Cecil.ModuleDefinition.ReadModule(
                Path.GetFullPath(path),
                new Cecil.ReaderParameters
                {
                    InMemory = true,
                    ReadingMode = Cecil.ReadingMode.Deferred,
                });
        }

        // 返回材料中已经核实的程序集名称大小写。
        private static string CanonicalAssemblyName(
            IReadOnlyDictionary<string, string> assemblyNames,
            string assemblyName)
        {
            return assemblyNames.TryGetValue(assemblyName, out string? canonicalName)
                ? canonicalName
                : throw new AnalysisException($"程序集名称没有进入材料总表：{assemblyName}");
        }

        // 拒绝两个源码函数共享同一个物理身份。
        private static void RequireUniqueMethods(IEnumerable<MethodEntry> methods)
        {
            IGrouping<string, MethodEntry>? duplicate = methods
                .GroupBy(method => method.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Skip(1).Any());
            if (duplicate != null)
            {
                throw new AnalysisException($"函数身份不唯一：{duplicate.Key}");
            }
        }

        // 拒绝两个类型共享同一个物理身份。
        private static void RequireUniqueTypes(IEnumerable<TypeEntry> types)
        {
            IGrouping<string, TypeEntry>? duplicate = types
                .GroupBy(type => type.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Skip(1).Any());
            if (duplicate != null)
            {
                throw new AnalysisException($"类型身份不唯一：{duplicate.Key}");
            }
        }

        // 递归列出命名空间中的全部源码类型。
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
                    foreach (INamedTypeSymbol item in EnumerateTypes(type))
                    {
                        yield return item;
                    }
                }
            }
        }

        // 递归列出一个源码类型及其内部类型。
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

        // 把一个源码类型转换为统一类型记录。
        private static TypeEntry CreateSourceType(INamedTypeSymbol type, SourceCatalogContext context)
        {
            INamedTypeSymbol definition = type.OriginalDefinition;
            string logicalId = SourceNamedTypeDefinitionId(definition, context);

            return new TypeEntry(
                logicalId,
                logicalId,
                Array.Empty<string>(),
                CanonicalAssemblyName(context.AssemblyNames, definition.ContainingAssembly.Identity.Name),
                definition.Name,
                DisplaySourceType(definition),
                definition.BaseType == null ? null : CreateSourceRelation(definition.BaseType, context),
                definition.Interfaces
                    .Select(item => CreateSourceRelation(item, context))
                    .DistinctBy(item => item.TypeId, StringComparer.Ordinal)
                    .OrderBy(item => item.TypeId, StringComparer.Ordinal)
                    .ToArray(),
                ReadSourceAttributes(definition.GetAttributes()),
                definition.TypeKind == TypeKind.Interface,
                definition,
                null,
                0);
        }

        // 把一个源码基类或接口保存为结构化关系。
        private static TypeRelationEntry CreateSourceRelation(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate[] arguments = ReadSourceNamedTypeArguments(type, context).ToArray();
            TypeIdentityTemplate identity = SourceTypeIdentity(type, context);

            return new TypeRelationEntry(
                SourceNamedTypeDefinitionId(type.OriginalDefinition, context),
                identity.Text,
                type.ContainingAssembly.Identity.GetDisplayName(),
                arguments.Select(argument => argument.Text).ToArray());
        }

        // 读取一个源码类型直接声明的全部函数。
        private static IReadOnlyList<MethodEntry> ReadSourceTypeMethods(
            SourceCatalogContext context,
            INamedTypeSymbol type,
            IReadOnlyDictionary<string, TypeEntry> sourceTypesById)
        {
            TypeEntry typeEntry = sourceTypesById[SourceNamedTypeDefinitionId(
                type.OriginalDefinition,
                context)];
            IReadOnlyDictionary<IMethodSymbol, MethodIdentityTemplate[]> interfaceTargets =
                ReadSourceInterfaceTargets(type, context);

            return type.GetMembers()
                .OfType<IMethodSymbol>()
                .Select(method => CreateSourceMethod(
                    method,
                    typeEntry,
                    context,
                    interfaceTargets.GetValueOrDefault(NormalizeMethod(method))
                        ?? Array.Empty<MethodIdentityTemplate>()))
                .ToArray();
        }

        // 读取一个源码文件中的局部函数和匿名函数。
        private static IReadOnlyList<MethodEntry> ReadSourceNestedMethods(
            SourceCatalogContext context,
            SyntaxTree tree,
            IReadOnlyDictionary<string, TypeEntry> sourceTypesById)
        {
            SyntaxNode root = tree.GetRoot();
            SyntaxNode[] declarations = root.DescendantNodes()
                .Where(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                .ToArray();
            if (declarations.Length == 0)
            {
                return Array.Empty<MethodEntry>();
            }

            SemanticModel model = context.Material.Compilation.GetSemanticModel(tree, true);
            List<MethodEntry> methods = new();
            foreach (SyntaxNode declaration in declarations)
            {
                IMethodSymbol? symbol = declaration switch
                {
                    LocalFunctionStatementSyntax local => model.GetDeclaredSymbol(local) as IMethodSymbol,
                    AnonymousFunctionExpressionSyntax anonymous =>
                        (model.GetOperation(anonymous) as IAnonymousFunctionOperation)?.Symbol,
                    _ => null,
                };
                if (symbol == null)
                {
                    throw new AnalysisException(
                        $"无法建立内部函数身份：{tree.FilePath}@{declaration.Span.Start}");
                }

                methods.Add(CreateSourceMethod(
                    symbol,
                    sourceTypesById[SourceNamedTypeDefinitionId(
                        symbol.ContainingType.OriginalDefinition,
                        context)],
                    context,
                    Array.Empty<MethodIdentityTemplate>()));
            }

            return methods;
        }

        // 把一个源码函数转换为统一函数记录。
        private static MethodEntry CreateSourceMethod(
            IMethodSymbol method,
            TypeEntry type,
            SourceCatalogContext context,
            IReadOnlyList<MethodIdentityTemplate> interfaceTargets)
        {
            IMethodSymbol definition = NormalizeMethod(method);
            Location? location = definition.Locations.FirstOrDefault(item => item.IsInSource);
            string? sourcePath = location?.SourceTree?.FilePath;
            int line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1;
            ParameterEntry[] parameters = definition.Parameters
                .Select(parameter => CreateSourceParameter(parameter, context))
                .ToArray();
            TypeIdentityTemplate returnType = SourceReturnTypeIdentity(definition, context);
            CatalogMethodKind kind = GetSourceMethodKind(definition);
            MethodIdentityTemplate identity = new(
                TypeIdentityTemplate.NamedType(type.LogicalId),
                definition.MetadataName,
                definition.Arity,
                parameters.Select(parameter => parameter.TypeIdentity).ToArray(),
                returnType);
            string logicalId = identity.Text;
            string id = kind is CatalogMethodKind.LocalFunction or CatalogMethodKind.AnonymousFunction
                ? $"{logicalId}@{sourcePath}:{location?.SourceSpan.Start ?? 0}"
                : logicalId;
            MethodIdentityTemplate[] relations = ReadSourceRelations(
                definition,
                context,
                interfaceTargets);

            return new MethodEntry(
                id,
                logicalId,
                CanonicalAssemblyName(context.AssemblyNames, definition.ContainingAssembly.Identity.Name),
                type.Id,
                type.FullName,
                definition.MetadataName,
                DisplaySourceType(definition.ReturnType) + (definition.ReturnsByRef || definition.ReturnsByRefReadonly ? "&" : string.Empty),
                returnType.Text,
                parameters,
                kind,
                ReadSourceAttributes(definition.GetAttributes()),
                type.Attributes,
                relations.Select(relation => relation.Text).ToArray(),
                sourcePath,
                line,
                IsReportable(definition, kind, sourcePath, context),
                definition.IsAbstract,
                definition.DeclaredAccessibility == Accessibility.Public,
                definition.IsStatic,
                definition.IsVirtual || definition.IsAbstract || definition.IsOverride,
                (definition.IsVirtual || definition.IsAbstract) && !definition.IsOverride,
                definition.Arity,
                definition,
                null,
                0);
        }

        // 找出源码类型直接提供的全部接口函数实现。
        private static IReadOnlyDictionary<IMethodSymbol, MethodIdentityTemplate[]>
            ReadSourceInterfaceTargets(INamedTypeSymbol type, SourceCatalogContext context)
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

                if (!targets.TryGetValue(definition, out List<MethodIdentityTemplate>? methodTargets))
                {
                    methodTargets = new List<MethodIdentityTemplate>();
                    targets.Add(definition, methodTargets);
                }

                methodTargets.Add(SourceMethodIdentity(contract.OriginalDefinition, context));
            }

            Dictionary<IMethodSymbol, MethodIdentityTemplate[]> result = new(
                SymbolEqualityComparer.Default);
            foreach ((IMethodSymbol method, List<MethodIdentityTemplate> identities) in targets)
            {
                result.Add(
                    method,
                    identities.DistinctBy(identity => identity.Text, StringComparer.Ordinal)
                        .OrderBy(identity => identity.Text, StringComparer.Ordinal)
                        .ToArray());
            }

            return result;
        }

        // 读取源码函数的接口实现和重写目标。
        private static MethodIdentityTemplate[] ReadSourceRelations(
            IMethodSymbol method,
            SourceCatalogContext context,
            IReadOnlyList<MethodIdentityTemplate> interfaceTargets)
        {
            return method.ExplicitInterfaceImplementations
                .Append(method.OverriddenMethod)
                .Where(target => target != null)
                .Cast<IMethodSymbol>()
                .Select(target => SourceMethodIdentity(target.OriginalDefinition, context))
                .Concat(interfaceTargets)
                .DistinctBy(identity => identity.Text, StringComparer.Ordinal)
                .OrderBy(identity => identity.Text, StringComparer.Ordinal)
                .ToArray();
        }

        // 还原扩展函数、部分函数及构造函数的原始声明。
        private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
        {
            return (method.ReducedFrom ?? method.PartialImplementationPart ?? method).OriginalDefinition;
        }

        // 建立一个源码函数的稳定声明身份。
        private static MethodIdentityTemplate SourceMethodIdentity(
            IMethodSymbol method,
            SourceCatalogContext context)
        {
            IMethodSymbol definition = NormalizeMethod(method);

            return new MethodIdentityTemplate(
                TypeIdentityTemplate.NamedType(SourceNamedTypeDefinitionId(
                    definition.ContainingType.OriginalDefinition,
                    context)),
                definition.MetadataName,
                definition.Arity,
                definition.Parameters.Select(parameter =>
                    SourceParameterTypeIdentity(parameter, context)).ToArray(),
                SourceReturnTypeIdentity(definition, context));
        }

        // 建立一个源码调用点的完整函数引用。
        internal static ManagedMethodReferenceInfo ReadSourceMethodReference(
            IMethodSymbol method,
            SourceCatalogContext context)
        {
            IMethodSymbol target = method.ReducedFrom ?? method;
            IMethodSymbol definition = NormalizeMethod(method);
            TypeIdentityTemplate declaringType = SourceTypeIdentity(target.ContainingType, context);
            TypeIdentityTemplate[] declaringArguments = ReadSourceNamedTypeArguments(
                target.ContainingType,
                context).ToArray();
            MethodIdentityTemplate identity = SourceMethodIdentity(definition, context)
                .Instantiate(declaringType, declaringArguments);
            TypeIdentityTemplate[] methodArguments = target.IsGenericMethod
                ? target.TypeArguments.Select(argument => SourceTypeIdentity(argument, context)).ToArray()
                : Array.Empty<TypeIdentityTemplate>();

            return new ManagedMethodReferenceInfo(
                identity,
                !target.IsStatic,
                target.ReturnsVoid,
                target.Parameters.Select(ReadSourceRefKind).ToArray(),
                methodArguments,
                TypeIdentityTemplate.NamedType(SourceNamedTypeDefinitionId(
                    target.ContainingType.OriginalDefinition,
                    context)),
                declaringArguments,
                target.ContainingAssembly.Identity.GetDisplayName(),
                null);
        }

        // 建立一个源码字段的完整引用。
        internal static ManagedFieldReferenceInfo ReadSourceFieldReference(
            IFieldSymbol field,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate[] arguments = ReadSourceNamedTypeArguments(
                field.ContainingType,
                context).ToArray();

            return new ManagedFieldReferenceInfo(
                SourceTypeIdentity(field.ContainingType, context),
                field.MetadataName,
                SourceTypeIdentity(field.Type, context),
                TypeIdentityTemplate.NamedType(SourceNamedTypeDefinitionId(
                    field.ContainingType.OriginalDefinition,
                    context)),
                arguments);
        }

        // 建立一个源码字段式事件的完整引用。
        internal static ManagedFieldReferenceInfo ReadSourceEventReference(
            IEventSymbol eventSymbol,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate[] arguments = ReadSourceNamedTypeArguments(
                eventSymbol.ContainingType,
                context).ToArray();

            return new ManagedFieldReferenceInfo(
                SourceTypeIdentity(eventSymbol.ContainingType, context),
                eventSymbol.MetadataName,
                SourceTypeIdentity(eventSymbol.Type, context),
                TypeIdentityTemplate.NamedType(SourceNamedTypeDefinitionId(
                    eventSymbol.ContainingType.OriginalDefinition,
                    context)),
                arguments);
        }

        // 建立一个源码自动属性后备字段的完整引用。
        internal static ManagedFieldReferenceInfo ReadSourcePropertyStorageReference(
            IPropertySymbol property,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate[] arguments = ReadSourceNamedTypeArguments(
                property.ContainingType,
                context).ToArray();

            return new ManagedFieldReferenceInfo(
                SourceTypeIdentity(property.ContainingType, context),
                $"<{property.MetadataName}>k__BackingField",
                SourceTypeIdentity(property.Type, context),
                TypeIdentityTemplate.NamedType(SourceNamedTypeDefinitionId(
                    property.ContainingType.OriginalDefinition,
                    context)),
                arguments);
        }

        // 建立一个源码类型使用点的实际和定义身份。
        internal static ManagedTypeReferenceInfo ReadSourceTypeReference(
            ITypeSymbol type,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate identity = SourceTypeIdentity(type, context);
            if (type is not INamedTypeSymbol namedType)
            {
                return new ManagedTypeReferenceInfo(
                    identity,
                    identity,
                    Array.Empty<TypeIdentityTemplate>(),
                    type.ContainingAssembly?.Identity.GetDisplayName(),
                    null);
            }

            return new ManagedTypeReferenceInfo(
                identity,
                TypeIdentityTemplate.NamedType(SourceNamedTypeDefinitionId(
                    namedType.OriginalDefinition,
                    context)),
                ReadSourceNamedTypeArguments(namedType, context).ToArray(),
                namedType.ContainingAssembly.Identity.GetDisplayName(),
                null);
        }

        // 把源码参数转换为统一参数记录。
        private static ParameterEntry CreateSourceParameter(
            IParameterSymbol parameter,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate type = SourceParameterTypeIdentity(parameter, context);

            return new ParameterEntry(
                parameter.Name,
                DisplaySourceType(parameter.Type) + (parameter.RefKind == RefKind.None ? string.Empty : "&"),
                type.Text,
                ReadSourceRefKind(parameter))
            {
                TypeIdentity = type,
            };
        }

        // 读取源码参数的引用传递方式。
        private static CatalogRefKind ReadSourceRefKind(IParameterSymbol parameter)
        {
            return parameter.RefKind switch
            {
                RefKind.Ref => CatalogRefKind.Ref,
                RefKind.Out => CatalogRefKind.Out,
                RefKind.In or RefKind.RefReadOnlyParameter => CatalogRefKind.In,
                _ => CatalogRefKind.None,
            };
        }

        // 建立源码参数在托管签名中的类型身份。
        private static TypeIdentityTemplate SourceParameterTypeIdentity(
            IParameterSymbol parameter,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate type = SourceTypeIdentity(parameter.Type, context);

            return parameter.RefKind == RefKind.None ? type : type.Append("&");
        }

        // 建立源码返回值在托管签名中的类型身份。
        private static TypeIdentityTemplate SourceReturnTypeIdentity(
            IMethodSymbol method,
            SourceCatalogContext context)
        {
            TypeIdentityTemplate type = SourceTypeIdentity(method.ReturnType, context);

            return method.ReturnsByRef || method.ReturnsByRefReadonly ? type.Append("&") : type;
        }

        // 建立源码类型的确定身份。
        private static TypeIdentityTemplate SourceTypeIdentity(
            ITypeSymbol type,
            SourceCatalogContext context)
        {
            if (TryReadPrimitive(type.SpecialType, out string? primitive))
            {
                return TypeIdentityTemplate.Literal(primitive!);
            }

            return type switch
            {
                ITypeParameterSymbol parameter when parameter.TypeParameterKind == TypeParameterKind.Method =>
                    TypeIdentityTemplate.Literal($"!!{parameter.Ordinal}"),
                ITypeParameterSymbol parameter =>
                    TypeIdentityTemplate.TypeParameter(ReadSourceTypeParameterOrdinal(parameter)),
                IArrayTypeSymbol array => SourceTypeIdentity(array.ElementType, context)
                    .Append($"[{new string(',', array.Rank - 1)}]"),
                IPointerTypeSymbol pointer => SourceTypeIdentity(pointer.PointedAtType, context).Append("*"),
                IFunctionPointerTypeSymbol function => TypeIdentityTemplate.Literal(
                    function.ToDisplayString(s_typeDisplayFormat)),
                IDynamicTypeSymbol => TypeIdentityTemplate.Literal("System.Object"),
                INamedTypeSymbol named => SourceNamedTypeIdentity(named, context),
                _ => throw new AnalysisException($"不支持的源码类型：{type.Kind} {type}"),
            };
        }

        // 建立源码命名类型定义或构造类型的身份。
        private static TypeIdentityTemplate SourceNamedTypeIdentity(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            string definitionId = SourceNamedTypeDefinitionId(type.OriginalDefinition, context);
            TypeIdentityTemplate[] arguments = ReadSourceNamedTypeArguments(type, context).ToArray();

            return arguments.Length == 0
                ? TypeIdentityTemplate.NamedType(definitionId)
                : TypeIdentityTemplate.NamedType(definitionId).Append(
                    $"<{string.Join(',', arguments.Select(argument => argument.Text))}>");
        }

        // 按外层到内层顺序读取源码命名类型的泛型实参。
        private static IEnumerable<TypeIdentityTemplate> ReadSourceNamedTypeArguments(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            if (type.ContainingType != null)
            {
                foreach (TypeIdentityTemplate argument in ReadSourceNamedTypeArguments(
                             type.ContainingType,
                             context))
                {
                    yield return argument;
                }
            }

            ImmutableArray<ITypeSymbol> arguments = type.IsUnboundGenericType
                ? type.TypeParameters.Cast<ITypeSymbol>().ToImmutableArray()
                : type.TypeArguments;
            foreach (ITypeSymbol argument in arguments)
            {
                yield return SourceTypeIdentity(argument, context);
            }
        }

        // 计算嵌套源码类型参数在 CLR 类型参数列表中的位置。
        private static int ReadSourceTypeParameterOrdinal(ITypeParameterSymbol parameter)
        {
            int offset = 0;
            INamedTypeSymbol? containing = parameter.ContainingType?.ContainingType;
            while (containing != null)
            {
                offset += containing.Arity;
                containing = containing.ContainingType;
            }

            return offset + parameter.Ordinal;
        }

        // 生成源码类型定义使用的元数据全名。
        private static string SourceMetadataTypeName(INamedTypeSymbol type)
        {
            if (type.ContainingType != null)
            {
                return $"{SourceMetadataTypeName(type.ContainingType)}+{type.MetadataName}";
            }

            string namespaceName = type.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : type.ContainingNamespace.ToDisplayString();

            return namespaceName.Length == 0 ? type.MetadataName : $"{namespaceName}.{type.MetadataName}";
        }

        // 建立源码命名类型定义的跨文件身份。
        private static string SourceNamedTypeDefinitionId(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            return NamedTypeId(
                CanonicalAssemblyName(context.AssemblyNames, type.ContainingAssembly.Identity.Name),
                SourceMetadataTypeName(type));
        }

        // 生成用户可读的源码类型名称。
        private static string DisplaySourceType(ITypeSymbol type)
        {
            return type.ToDisplayString(s_typeDisplayFormat);
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

            return context.Material.IsReportAssembly
                && sourcePath != null
                && context.ReportablePaths.Contains(sourcePath)
                && allowedKind
                && !method.IsAbstract
                && !method.IsImplicitlyDeclared
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

        // 把 Roslyn 函数种类映射为项目枚举。
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
            return attributes.Select(attribute => new AttributeEntry(
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

        // 把常见 CLR 基础类型统一为不带程序集的稳定名称。
        private static bool TryReadPrimitive(SpecialType type, out string? name)
        {
            name = type switch
            {
                SpecialType.System_Void => "System.Void",
                SpecialType.System_Boolean => "System.Boolean",
                SpecialType.System_Char => "System.Char",
                SpecialType.System_SByte => "System.SByte",
                SpecialType.System_Byte => "System.Byte",
                SpecialType.System_Int16 => "System.Int16",
                SpecialType.System_UInt16 => "System.UInt16",
                SpecialType.System_Int32 => "System.Int32",
                SpecialType.System_UInt32 => "System.UInt32",
                SpecialType.System_Int64 => "System.Int64",
                SpecialType.System_UInt64 => "System.UInt64",
                SpecialType.System_Decimal => "System.Decimal",
                SpecialType.System_Single => "System.Single",
                SpecialType.System_Double => "System.Double",
                SpecialType.System_String => "System.String",
                SpecialType.System_Object => "System.Object",
                SpecialType.System_IntPtr => "System.IntPtr",
                SpecialType.System_UIntPtr => "System.UIntPtr",
                _ => null,
            };

            return name != null;
        }

        // 生成边界明确的程序集加类型身份。
        private static string NamedTypeId(string assemblyName, string metadataName)
        {
            return $"A{assemblyName.Length}:{assemblyName}T{metadataName.Length}:{metadataName}";
        }

        /// <summary>保存读取源码函数时需要的编译上下文。</summary>
        internal sealed record SourceCatalogContext(
            SourceAssemblyMaterial Material,
            IReadOnlySet<string> ReportablePaths,
            IReadOnlyDictionary<string, string> AssemblyNames);

        /// <summary>保存一个源码类型或语法树读取任务。</summary>
        private sealed record SourceMethodWorkItem(
            SourceCatalogContext Context,
            INamedTypeSymbol? Type,
            SyntaxTree? Tree);

        // 使用 Cecil 读取一个真实托管文件中的全部类型和转交声明。
        internal static ManagedAssemblyPart ReadManagedTypes(
            string path,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string fullPath = Path.GetFullPath(path);
            using Cecil.ModuleDefinition module = OpenModule(fullPath);
            string assemblyName = CanonicalAssemblyName(assemblyNames, module.Assembly.Name.Name);
            TypeEntry[] types = module.Types
                .SelectMany(EnumerateManagedTypes)
                .Where(type => type.Name != "<Module>" || !string.IsNullOrEmpty(type.Namespace))
                .Select(type => CreateManagedType(type, fullPath, assemblyName, assemblyNames))
                .ToArray();
            ForwardedTypeEntry[] forwarders = module.ExportedTypes
                .Where(type => type.IsForwarder)
                .Select(type => CreateForwarder(type, assemblyName, assemblyNames))
                .ToArray();

            return new ManagedAssemblyPart(
                fullPath,
                types,
                forwarders);
        }

        // 递归列出一个 Cecil 类型及其内部类型。
        private static IEnumerable<Cecil.TypeDefinition> EnumerateManagedTypes(
            Cecil.TypeDefinition type)
        {
            yield return type;

            foreach (Cecil.TypeDefinition nested in type.NestedTypes)
            {
                foreach (Cecil.TypeDefinition item in EnumerateManagedTypes(nested))
                {
                    yield return item;
                }
            }
        }

        // 把一个 Cecil 类型定义转换为统一类型记录。
        private static TypeEntry CreateManagedType(
            Cecil.TypeDefinition type,
            string path,
            string assemblyName,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string logicalId = ManagedNamedTypeDefinitionId(type, assemblyNames);
            string id = PhysicalTypeId(logicalId, path);

            return new TypeEntry(
                id,
                logicalId,
                Array.Empty<string>(),
                assemblyName,
                RemoveGenericArity(type.Name),
                DisplayManagedTypeDefinition(type),
                type.BaseType == null ? null : CreateManagedRelation(type.BaseType, assemblyNames),
                type.Interfaces
                    .Select(item => CreateManagedRelation(item.InterfaceType, assemblyNames))
                    .DistinctBy(item => item.TypeId, StringComparer.Ordinal)
                    .OrderBy(item => item.TypeId, StringComparer.Ordinal)
                    .ToArray(),
                Array.Empty<AttributeEntry>(),
                type.IsInterface,
                null,
                path,
                type.MetadataToken.ToInt32());
        }

        // 把一个 Cecil 基类或接口保存为结构化关系。
        private static TypeRelationEntry CreateManagedRelation(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            TypeIdentityTemplate identity = ManagedTypeIdentity(type, assemblyNames);
            TypeIdentityTemplate[] arguments = ReadManagedTypeArguments(type, assemblyNames).ToArray();

            return new TypeRelationEntry(
                ManagedNamedTypeDefinitionId(ManagedNamedTypeElement(type), assemblyNames),
                identity.Text,
                ReadManagedAssemblyFullName(ManagedNamedTypeElement(type)),
                arguments.Select(argument => argument.Text).ToArray());
        }

        // 把 Cecil 类型转交声明转换为目标类型别名。
        private static ForwardedTypeEntry CreateForwarder(
            Cecil.ExportedType type,
            string facadeAssemblyName,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            Cecil.IMetadataScope scope = ReadForwardedScope(type);
            if (scope is not Cecil.AssemblyNameReference assembly)
            {
                throw new AnalysisException($"类型转交目标不是程序集：{type.FullName}");
            }

            return new ForwardedTypeEntry(
                NamedTypeId(facadeAssemblyName, NormalizeManagedFullName(type.FullName)),
                NamedTypeId(
                    CanonicalAssemblyName(assemblyNames, assembly.Name),
                    NormalizeManagedFullName(type.FullName)));
        }

        // 沿嵌套转交类型找到最终程序集范围。
        private static Cecil.IMetadataScope ReadForwardedScope(Cecil.ExportedType type)
        {
            Cecil.ExportedType root = type;
            while (root.DeclaringType != null)
            {
                root = root.DeclaringType;
            }

            return root.Scope;
        }

        // 把参考程序集身份附加到唯一真实类型。
        private static TypeEntry[] AttachForwardedAliases(
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<ForwardedTypeEntry> forwarders)
        {
            IReadOnlyDictionary<string, IReadOnlyList<string>> aliasesByTarget = forwarders
                .GroupBy(item => item.TargetTypeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(item => item.AliasTypeId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
            HashSet<string> knownTargets = types.Select(type => type.LogicalId).ToHashSet(StringComparer.Ordinal);
            ForwardedTypeEntry? missing = forwarders.FirstOrDefault(item => !knownTargets.Contains(item.TargetTypeId));
            if (missing != null)
            {
                throw new AnalysisException(
                    $"类型转交目标没有进入材料总表：{missing.AliasTypeId} => {missing.TargetTypeId}");
            }

            IGrouping<string, ForwardedTypeEntry>? ambiguous = forwarders
                .GroupBy(item => item.AliasTypeId, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Select(item => item.TargetTypeId)
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any());
            if (ambiguous != null)
            {
                throw new AnalysisException(
                    $"参考类型身份对应多个真实类型：{ambiguous.Key} => "
                    + string.Join("; ", ambiguous.Select(item => item.TargetTypeId)));
            }

            return types.Select(type => aliasesByTarget.TryGetValue(
                        type.LogicalId,
                        out IReadOnlyList<string>? aliases)
                    ? type with { AliasIds = aliases }
                    : type)
                .ToArray();
        }

        // 读取一个 Cecil 类型直接声明的全部函数。
        internal static IReadOnlyList<MethodEntry> ReadManagedTypeMethods(
            TypeEntry type,
            IReadOnlyDictionary<string, string> assemblyNames,
            Cecil.ModuleDefinition module)
        {
            Cecil.IMetadataTokenProvider provider = module.LookupToken(type.MetadataToken)
                ?? throw new AnalysisException($"托管类型标记不存在：{type.Id}");
            if (provider is not Cecil.TypeDefinition definition)
            {
                throw new AnalysisException($"托管类型标记不是类型定义：{type.Id}");
            }

            IReadOnlyDictionary<Cecil.MethodDefinition, CatalogMethodKind> accessorKinds =
                ReadManagedAccessorKinds(definition);

            return definition.Methods.Select(method => CreateManagedMethod(
                    method,
                    type,
                    accessorKinds.GetValueOrDefault(method, ReadManagedOrdinaryKind(method)),
                    assemblyNames))
                .ToArray();
        }

        // 建立 Cecil 属性和事件访问器的准确函数种类表。
        private static IReadOnlyDictionary<Cecil.MethodDefinition, CatalogMethodKind>
            ReadManagedAccessorKinds(Cecil.TypeDefinition type)
        {
            Dictionary<Cecil.MethodDefinition, CatalogMethodKind> result = new();
            foreach (Cecil.PropertyDefinition property in type.Properties)
            {
                if (property.GetMethod != null)
                {
                    result.Add(property.GetMethod, CatalogMethodKind.PropertyGetter);
                }

                if (property.SetMethod != null)
                {
                    result.Add(property.SetMethod, CatalogMethodKind.PropertySetter);
                }
            }

            foreach (Cecil.EventDefinition eventDefinition in type.Events)
            {
                if (eventDefinition.AddMethod != null)
                {
                    result.Add(eventDefinition.AddMethod, CatalogMethodKind.EventAdder);
                }

                if (eventDefinition.RemoveMethod != null)
                {
                    result.Add(eventDefinition.RemoveMethod, CatalogMethodKind.EventRemover);
                }
            }

            return result;
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
            CatalogMethodKind kind,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            ParameterEntry[] parameters = method.Parameters.Select(parameter =>
                CreateManagedParameter(parameter, assemblyNames)).ToArray();
            TypeIdentityTemplate returnType = ManagedTypeIdentity(method.ReturnType, assemblyNames);
            MethodIdentityTemplate identity = new(
                TypeIdentityTemplate.NamedType(type.LogicalId),
                method.Name,
                method.GenericParameters.Count,
                parameters.Select(parameter => parameter.TypeIdentity).ToArray(),
                returnType);
            string id = PhysicalMethodId(identity.Text, type.AssemblyPath!, method.MetadataToken.ToInt32());
            MethodIdentityTemplate[] explicitRelations = method.Overrides
                .Select(target => ManagedMethodDefinitionIdentity(target, assemblyNames))
                .DistinctBy(target => target.Text, StringComparer.Ordinal)
                .OrderBy(target => target.Text, StringComparer.Ordinal)
                .ToArray();

            return new MethodEntry(
                id,
                identity.Text,
                type.AssemblyName,
                type.Id,
                type.FullName,
                method.Name,
                DisplayManagedType(method.ReturnType),
                returnType.Text,
                parameters,
                kind,
                Array.Empty<AttributeEntry>(),
                type.Attributes,
                explicitRelations.Select(relation => relation.Text).ToArray(),
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
                method.MetadataToken.ToInt32());
        }

        // 把一个 Cecil 参数转换为统一参数记录。
        private static ParameterEntry CreateManagedParameter(
            Cecil.ParameterDefinition parameter,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            TypeIdentityTemplate identity = ManagedTypeIdentity(parameter.ParameterType, assemblyNames);

            return new ParameterEntry(
                parameter.Name,
                DisplayManagedType(parameter.ParameterType),
                identity.Text,
                ReadManagedRefKind(parameter))
            {
                TypeIdentity = identity,
            };
        }

        // 读取 Cecil 参数的引用传递方式。
        private static CatalogRefKind ReadManagedRefKind(Cecil.ParameterDefinition parameter)
        {
            if (parameter.ParameterType is not Cecil.ByReferenceType)
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
            Cecil.MethodReference method,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            Cecil.MethodReference element = method.GetElementMethod();
            TypeIdentityTemplate[] declaringArguments = ReadManagedTypeArguments(
                method.DeclaringType,
                assemblyNames).ToArray();
            MethodIdentityTemplate definition = new(
                TypeIdentityTemplate.NamedType(ManagedNamedTypeDefinitionId(
                    ManagedNamedTypeElement(element.DeclaringType),
                    assemblyNames)),
                element.Name,
                element.GenericParameters.Count,
                element.Parameters.Select(parameter =>
                    ManagedTypeIdentity(parameter.ParameterType, assemblyNames)).ToArray(),
                ManagedTypeIdentity(element.ReturnType, assemblyNames));

            return definition.Instantiate(
                ManagedTypeIdentity(method.DeclaringType, assemblyNames),
                declaringArguments);
        }

        // 建立 Cecil 函数引用所指向的开放声明身份。
        private static MethodIdentityTemplate ManagedMethodDefinitionIdentity(
            Cecil.MethodReference method,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            Cecil.MethodReference element = method.GetElementMethod();

            return new MethodIdentityTemplate(
                TypeIdentityTemplate.NamedType(ManagedNamedTypeDefinitionId(
                    ManagedNamedTypeElement(element.DeclaringType),
                    assemblyNames)),
                element.Name,
                element.GenericParameters.Count,
                element.Parameters.Select(parameter =>
                    ManagedTypeIdentity(parameter.ParameterType, assemblyNames)).ToArray(),
                ManagedTypeIdentity(element.ReturnType, assemblyNames));
        }

        // 建立 Cecil 类型定义或构造类型的确定身份。
        internal static TypeIdentityTemplate ManagedTypeIdentity(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            if (TryReadPrimitive(type.MetadataType, out string? primitive))
            {
                return TypeIdentityTemplate.Literal(primitive!);
            }

            return type switch
            {
                Cecil.GenericParameter parameter when parameter.Type == Cecil.GenericParameterType.Method =>
                    TypeIdentityTemplate.Literal($"!!{parameter.Position}"),
                Cecil.GenericParameter parameter =>
                    TypeIdentityTemplate.TypeParameter(parameter.Position),
                Cecil.GenericInstanceType instance => TypeIdentityTemplate.NamedType(
                        ManagedNamedTypeDefinitionId(instance.ElementType, assemblyNames))
                    .Append($"<{string.Join(',', instance.GenericArguments.Select(argument =>
                        ManagedTypeIdentity(argument, assemblyNames).Text))}>"),
                Cecil.ArrayType array => ManagedTypeIdentity(array.ElementType, assemblyNames)
                    .Append($"[{new string(',', array.Rank - 1)}]"),
                Cecil.ByReferenceType reference => ManagedTypeIdentity(reference.ElementType, assemblyNames)
                    .Append("&"),
                Cecil.PointerType pointer => ManagedTypeIdentity(pointer.ElementType, assemblyNames)
                    .Append("*"),
                Cecil.PinnedType pinned => ManagedTypeIdentity(pinned.ElementType, assemblyNames),
                Cecil.OptionalModifierType optional => ManagedTypeIdentity(optional.ElementType, assemblyNames),
                Cecil.RequiredModifierType required => ManagedTypeIdentity(required.ElementType, assemblyNames),
                Cecil.FunctionPointerType function => TypeIdentityTemplate.Literal(
                    DisplayManagedType(function)),
                Cecil.SentinelType sentinel => ManagedTypeIdentity(sentinel.ElementType, assemblyNames)
                    .Append("..."),
                _ => TypeIdentityTemplate.NamedType(ManagedNamedTypeDefinitionId(type, assemblyNames)),
            };
        }

        // 读取 Cecil 构造类型的全部实际类型参数。
        internal static IEnumerable<TypeIdentityTemplate> ReadManagedTypeArguments(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            if (type is not Cecil.GenericInstanceType instance)
            {
                return Array.Empty<TypeIdentityTemplate>();
            }

            return instance.GenericArguments.Select(argument =>
                ManagedTypeIdentity(argument, assemblyNames)).ToArray();
        }

        // 取出 Cecil 类型规格背后的命名类型定义。
        internal static Cecil.TypeReference ManagedNamedTypeElement(Cecil.TypeReference type)
        {
            Cecil.TypeReference current = type;
            while (current is Cecil.TypeSpecification specification)
            {
                current = specification.ElementType;
            }

            return current;
        }

        // 建立 Cecil 命名类型定义的跨文件身份。
        internal static string ManagedNamedTypeDefinitionId(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            Cecil.TypeReference element = ManagedNamedTypeElement(type);
            string assemblyName = CanonicalAssemblyName(
                assemblyNames,
                ReadManagedAssemblyName(element));

            return NamedTypeId(assemblyName, ManagedMetadataTypeName(element));
        }

        // 沿 Cecil 嵌套类型找到类型所属程序集。
        private static string ReadManagedAssemblyName(Cecil.TypeReference type)
        {
            Cecil.TypeReference root = type;
            while (root.DeclaringType != null)
            {
                root = root.DeclaringType;
            }

            return root.Scope switch
            {
                Cecil.AssemblyNameReference assembly => assembly.Name,
                Cecil.ModuleDefinition module => module.Assembly.Name.Name,
                Cecil.ModuleReference module => module.Name,
                _ => throw new AnalysisException(
                    $"托管类型没有明确的程序集范围：{type.FullName}"),
            };
        }

        // 沿 Cecil 嵌套类型读取完整程序集身份。
        internal static string ReadManagedAssemblyFullName(Cecil.TypeReference type)
        {
            Cecil.TypeReference root = type;
            while (root.DeclaringType != null)
            {
                root = root.DeclaringType;
            }

            return root.Scope switch
            {
                Cecil.AssemblyNameReference assembly => assembly.FullName,
                Cecil.ModuleDefinition module => module.Assembly.Name.FullName,
                _ => throw new AnalysisException($"托管类型没有程序集身份：{type.FullName}"),
            };
        }

        // 生成 Cecil 类型的元数据全名。
        private static string ManagedMetadataTypeName(Cecil.TypeReference type)
        {
            return NormalizeManagedFullName(ManagedNamedTypeElement(type).FullName);
        }

        // 把 Cecil 的嵌套类型分隔符统一成 CLR 身份分隔符。
        private static string NormalizeManagedFullName(string fullName)
        {
            return fullName.Replace('/', '+');
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

        // 生成用户可读的 Cecil 实际类型名称。
        private static string DisplayManagedType(Cecil.TypeReference type)
        {
            return type switch
            {
                Cecil.GenericParameter parameter => parameter.Name,
                Cecil.GenericInstanceType instance =>
                    $"{DisplayManagedTypeDefinition(instance.ElementType)}"
                    + $"<{string.Join(',', instance.GenericArguments.Select(DisplayManagedType))}>",
                Cecil.ArrayType array =>
                    $"{DisplayManagedType(array.ElementType)}[{new string(',', array.Rank - 1)}]",
                Cecil.ByReferenceType reference => $"{DisplayManagedType(reference.ElementType)}&",
                Cecil.PointerType pointer => $"{DisplayManagedType(pointer.ElementType)}*",
                Cecil.PinnedType pinned => DisplayManagedType(pinned.ElementType),
                Cecil.OptionalModifierType optional => DisplayManagedType(optional.ElementType),
                Cecil.RequiredModifierType required => DisplayManagedType(required.ElementType),
                _ when TryReadPrimitive(type.MetadataType, out string? primitive) => primitive!,
                _ => DisplayManagedTypeDefinition(type),
            };
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

        // 把 Cecil 基础类型统一为不带程序集的稳定名称。
        private static bool TryReadPrimitive(Cecil.MetadataType type, out string? name)
        {
            name = type switch
            {
                Cecil.MetadataType.Void => "System.Void",
                Cecil.MetadataType.Boolean => "System.Boolean",
                Cecil.MetadataType.Char => "System.Char",
                Cecil.MetadataType.SByte => "System.SByte",
                Cecil.MetadataType.Byte => "System.Byte",
                Cecil.MetadataType.Int16 => "System.Int16",
                Cecil.MetadataType.UInt16 => "System.UInt16",
                Cecil.MetadataType.Int32 => "System.Int32",
                Cecil.MetadataType.UInt32 => "System.UInt32",
                Cecil.MetadataType.Int64 => "System.Int64",
                Cecil.MetadataType.UInt64 => "System.UInt64",
                Cecil.MetadataType.Single => "System.Single",
                Cecil.MetadataType.Double => "System.Double",
                Cecil.MetadataType.String => "System.String",
                Cecil.MetadataType.Object => "System.Object",
                Cecil.MetadataType.IntPtr => "System.IntPtr",
                Cecil.MetadataType.UIntPtr => "System.UIntPtr",
                _ => null,
            };

            return name != null;
        }

        // 生成托管类型包含物理文件的唯一身份。
        private static string PhysicalTypeId(string logicalId, string path)
        {
            string fullPath = Path.GetFullPath(path);

            return $"{logicalId}|P{fullPath.Length}:{fullPath}";
        }

        // 生成托管函数包含物理文件和元数据标记的唯一身份。
        private static string PhysicalMethodId(string logicalId, string path, int token)
        {
            string fullPath = Path.GetFullPath(path);

            return $"{logicalId}|P{fullPath.Length}:{fullPath}|M{token}";
        }

        /// <summary>保存一次 Cecil 托管文件读取结果。</summary>
        internal sealed record ManagedAssemblyPart(
            string Path,
            IReadOnlyList<TypeEntry> Types,
            IReadOnlyList<ForwardedTypeEntry> Forwarders);

        /// <summary>保存一个参考类型身份到真实类型身份的转交。</summary>
        internal sealed record ForwardedTypeEntry(string AliasTypeId, string TargetTypeId);
    }

    /// <summary>
    /// 保存一个已经确定的类型身份；Cecil 和 Roslyn 负责解析，当前对象只负责传递。
    /// </summary>
    internal sealed record TypeIdentityTemplate(string Text)
    {
        internal string StableText => this.Text;

        // 保存一个已经解析完成的类型身份。
        internal static TypeIdentityTemplate Literal(string text)
        {
            return new TypeIdentityTemplate(text);
        }

        // 保存一个已经解析完成的命名类型身份。
        internal static TypeIdentityTemplate NamedType(string typeId)
        {
            return new TypeIdentityTemplate(typeId);
        }

        // 保存一个 CLR 类型泛型参数位置。
        internal static TypeIdentityTemplate TypeParameter(int index)
        {
            return new TypeIdentityTemplate($"!{index}");
        }

        // 在已经解析的类型身份后追加一个确定后缀。
        internal TypeIdentityTemplate Append(string suffix)
        {
            return new TypeIdentityTemplate(this.Text + suffix);
        }

        // 用构造类型实参替换 CLR 类型泛型参数。
        internal TypeIdentityTemplate Substitute(IReadOnlyList<TypeIdentityTemplate> arguments)
        {
            if (arguments.Count == 0 || !this.Text.Contains('!'))
            {
                return this;
            }

            System.Text.StringBuilder result = new();
            for (int index = 0; index < this.Text.Length; index++)
            {
                char current = this.Text[index];
                if (current != '!'
                    || index + 1 >= this.Text.Length
                    || this.Text[index + 1] == '!'
                    || (index > 0 && this.Text[index - 1] == '!')
                    || !char.IsDigit(this.Text[index + 1]))
                {
                    result.Append(current);
                    continue;
                }

                int end = index + 1;
                while (end < this.Text.Length && char.IsDigit(this.Text[end]))
                {
                    end++;
                }

                int position = int.Parse(this.Text.AsSpan(index + 1, end - index - 1));
                if (position >= arguments.Count)
                {
                    result.Append(this.Text, index, end - index);
                }
                else
                {
                    result.Append(arguments[position].Text);
                }

                index = end - 1;
            }

            return new TypeIdentityTemplate(result.ToString());
        }
    }

    /// <summary>
    /// 保存函数声明类型、名称、泛型元数、参数和返回类型的唯一身份。
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
                string parameters = string.Concat(this.Parameters.Select(parameter =>
                    $"{parameter.StableText.Length}:{parameter.StableText}"));

                return $"{this.DeclaringType.StableText}::{this.Name.Length}:{this.Name}{arity}"
                    + $"({this.Parameters.Count}:{parameters})"
                    + $"->{this.ReturnType.StableText.Length}:{this.ReturnType.StableText}";
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

    /// <summary>保存调用点的函数身份、调用形态和泛型实参。</summary>
    internal sealed record ManagedMethodReferenceInfo(
        MethodIdentityTemplate Identity,
        bool HasInstance,
        bool ReturnsVoid,
        IReadOnlyList<CatalogRefKind> ParameterRefKinds,
        IReadOnlyList<TypeIdentityTemplate> GenericArguments,
        TypeIdentityTemplate DeclaringTypeDefinition,
        IReadOnlyList<TypeIdentityTemplate> DeclaringTypeArguments,
        string TargetAssemblyIdentity,
        string? ReferringAssemblyPath);

    /// <summary>保存字段引用的声明类型、名称和字段类型。</summary>
    internal sealed record ManagedFieldReferenceInfo(
        TypeIdentityTemplate DeclaringType,
        string Name,
        TypeIdentityTemplate FieldType,
        TypeIdentityTemplate DeclaringTypeDefinition,
        IReadOnlyList<TypeIdentityTemplate> DeclaringTypeArguments);

    /// <summary>保存类型引用的实际身份、定义身份和泛型实参。</summary>
    internal sealed record ManagedTypeReferenceInfo(
        TypeIdentityTemplate Identity,
        TypeIdentityTemplate Definition,
        IReadOnlyList<TypeIdentityTemplate> Arguments,
        string? TargetAssemblyIdentity,
        string? ReferringAssemblyPath);

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

    /// <summary>保存后续标签判断需要的特性信息。</summary>
    public sealed record AttributeEntry(
        string TypeName,
        bool HasArguments,
        IReadOnlyList<string> ArgumentValues);

    /// <summary>保存一个函数参数的名称、类型和传递方式。</summary>
    public sealed record ParameterEntry(
        string Name,
        string TypeName,
        string TypeId,
        CatalogRefKind RefKind)
    {
        internal TypeIdentityTemplate TypeIdentity { get; init; } =
            TypeIdentityTemplate.Literal(TypeId);
    }

    /// <summary>保存基类或接口定义及其实际泛型参数。</summary>
    public sealed record TypeRelationEntry(
        string DefinitionId,
        string TypeId,
        string AssemblyIdentity,
        IReadOnlyList<string> TypeArguments);

    /// <summary>保存一个类型及其直接继承关系。</summary>
    public sealed record TypeEntry(
        string Id,
        string LogicalId,
        IReadOnlyList<string> AliasIds,
        string AssemblyName,
        string Name,
        string FullName,
        TypeRelationEntry? BaseType,
        IReadOnlyList<TypeRelationEntry> Interfaces,
        IReadOnlyList<AttributeEntry> Attributes,
        bool IsInterface,
        INamedTypeSymbol? SourceSymbol,
        string? AssemblyPath,
        int MetadataToken);

    /// <summary>保存一个函数的稳定身份、来源、参数、标签和统计属性。</summary>
    public sealed record MethodEntry(
        string Id,
        string LogicalId,
        string AssemblyName,
        string TypeId,
        string TypeName,
        string Name,
        string ReturnTypeName,
        string ReturnTypeId,
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
        int MetadataToken);

    /// <summary>保存一条当前材料中没有目标定义的继承或接口关系。</summary>
    public sealed record MissingTypeRelationEntry(
        string OwnerTypeId,
        string OwnerTypeName,
        string TargetTypeId,
        bool IsInterface,
        string? AssemblyPath);

    /// <summary>
    /// 保存函数总表和后续模块直接使用的查找索引。
    /// </summary>
    public sealed class MethodCatalogResult
    {
        private readonly ConcurrentDictionary<string, IReadOnlyList<MethodEntry>>
            m_managedMethodsByTypeId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Cecil.ModuleDefinition> m_modulesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object m_moduleLock = new();
        private readonly IReadOnlyDictionary<string, string> m_assemblyNames;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> m_lookupPathsByAssemblyName;
        private readonly Dictionary<string, MethodCatalog.ManagedAssemblyPart>
            m_loadedPartsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly object m_loadedTypeLock = new();
        private TypeEntry[] m_types;
        private IReadOnlyDictionary<string, TypeEntry> m_typesById;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_typesByLogicalId;
        private IReadOnlyList<MissingTypeRelationEntry> m_missingTypeRelations;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_derivedTypesByBaseId;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_implementingTypesByInterfaceId;
        private readonly IReadOnlyDictionary<string, MethodCatalog.SourceCatalogContext>
            m_sourceContextsByAssembly;

        // 保存排好顺序的函数、类型和关系索引。
        internal MethodCatalogResult(
            IReadOnlyList<MethodEntry> methods,
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<MethodCatalog.SourceCatalogContext> sourceContexts,
            IReadOnlyDictionary<string, string> assemblyNames,
            IReadOnlyList<string> assemblyLookupPaths,
            System.Diagnostics.Stopwatch stopwatch,
            int jobs)
        {
            this.m_assemblyNames = assemblyNames;
            this.m_lookupPathsByAssemblyName = assemblyLookupPaths
                .GroupBy(
                    path => System.Reflection.AssemblyName.GetAssemblyName(path).Name!,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            this.m_sourceContextsByAssembly = sourceContexts.ToDictionary(
                context => context.Material.Name,
                StringComparer.Ordinal);
            this.Methods = methods;
            this.m_types = types.ToArray();
            IReadOnlyDictionary<string, MethodEntry> methodsById = null!;
            IReadOnlyDictionary<string, TypeEntry> typesById = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByName = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByLogicalId = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByTypeId = null!;
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> typesByLogicalId = null!;
            ParallelOptions options = new() { MaxDegreeOfParallelism = jobs };

            Parallel.Invoke(
                options,
                () => methodsById = methods.ToDictionary(method => method.Id, StringComparer.Ordinal),
                () => typesById = types.ToDictionary(type => type.Id, StringComparer.Ordinal),
                () => methodsByName = Index(methods, method => method.Name, method => method.Id),
                () => methodsByLogicalId = Index(
                    methods,
                    method => method.LogicalId,
                    method => method.Id),
                () => methodsByTypeId = Index(methods, method => method.TypeId, method => method.Id),
                () => typesByLogicalId = IndexMany(
                    types,
                    type => type.AliasIds.Prepend(type.LogicalId),
                    type => type.Id));

            this.MethodsById = methodsById;
            this.m_typesById = typesById;
            this.MethodsByName = methodsByName;
            this.MethodsByLogicalId = methodsByLogicalId;
            this.MethodsByTypeId = methodsByTypeId;
            this.m_typesByLogicalId = typesByLogicalId;
            this.m_missingTypeRelations = ReadMissingTypeRelations(types, typesByLogicalId);
            this.m_derivedTypesByBaseId = IndexMany(
                types.Where(type => type.BaseType != null),
                type => ResolveDefinitionIds(type.BaseType!.DefinitionId),
                type => type.Id);
            this.m_implementingTypesByInterfaceId = BuildInterfaceIndex(types);
            this.OverridesByMethodId = IndexMany(
                methods,
                method => method.RelatedMethodIds,
                method => method.Id);
            stopwatch.Stop();
            this.Elapsed = stopwatch.Elapsed;
        }

        /// <summary>启动时建立的全部源码函数。</summary>
        public IReadOnlyList<MethodEntry> Methods { get; }

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

        /// <summary>按物理身份查找源码函数。</summary>
        public IReadOnlyDictionary<string, MethodEntry> MethodsById { get; }

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
                    return this.m_typesByLogicalId;
                }
            }
        }

        /// <summary>按简单名称查找源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByName { get; }

        /// <summary>按逻辑身份查找源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByLogicalId { get; }

        /// <summary>按声明类型查找源码函数。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> MethodsByTypeId { get; }

        /// <summary>按基类身份查找直接派生类型。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> DerivedTypesByBaseId
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    return this.m_derivedTypesByBaseId;
                }
            }
        }

        /// <summary>按接口身份查找全部实现类型。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> ImplementingTypesByInterfaceId
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    return this.m_implementingTypesByInterfaceId;
                }
            }
        }

        /// <summary>按目标函数身份查找源码实现和重写。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> OverridesByMethodId { get; }

        /// <summary>尚未被当前编译材料提供定义的继承和接口关系。</summary>
        public IReadOnlyList<MissingTypeRelationEntry> MissingTypeRelations
        {
            get
            {
                lock (this.m_loadedTypeLock)
                {
                    return this.m_missingTypeRelations;
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
            if (type.AssemblyPath == null)
            {
                return this.MethodsByTypeId.GetValueOrDefault(type.Id)
                    ?? Array.Empty<MethodEntry>();
            }

            IReadOnlyList<MethodEntry> methods = this.m_managedMethodsByTypeId.GetOrAdd(
                type.Id,
                _ => ReadManagedMethodDeclarations(type));
            IReadOnlyList<InheritedTypeRelation> inherited = ReadInheritedTypes(type);

            return methods.Select(method => CompleteManagedRelations(method, inherited))
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
        }

        // 按完整程序集身份从候选表延迟读取一次真实托管文件的类型。
        /// <summary>
        /// 在调用或候选类型实际命中间接程序集时载入其类型定义。
        /// </summary>
        public IReadOnlyList<TypeEntry> LoadAssemblyTypes(
            System.Reflection.AssemblyName identity,
            string referringAssemblyPath)
        {
            string assemblyName = identity.Name
                ?? throw new AnalysisException("程序集引用没有名称。");
            string siblingPath = Path.Combine(
                Path.GetDirectoryName(referringAssemblyPath)!,
                $"{assemblyName}.dll");
            string[] candidates = this.m_lookupPathsByAssemblyName.TryGetValue(
                    assemblyName,
                    out IReadOnlyList<string>? knownPaths)
                ? knownPaths.Where(path => MatchesAssemblyIdentity(path, identity)).ToArray()
                : Array.Empty<string>();
            if (File.Exists(siblingPath) && MatchesAssemblyIdentity(siblingPath, identity))
            {
                candidates = candidates.Append(Path.GetFullPath(siblingPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            string[] localCandidates = candidates.Where(path => string.Equals(
                    Path.GetDirectoryName(path),
                    Path.GetDirectoryName(referringAssemblyPath),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string path = localCandidates.Length == 1
                ? localCandidates[0]
                : candidates.Length switch
                {
                    1 => candidates[0],
                    0 => throw new AnalysisException(
                        $"托管程序集依赖不存在：{referringAssemblyPath} => {identity.FullName}"),
                    _ => throw new AnalysisException(
                        $"托管程序集依赖不唯一：{referringAssemblyPath} => {identity.FullName}："
                            + string.Join("; ", candidates)),
                };
            lock (this.m_loadedTypeLock)
            {
                if (!this.m_loadedPartsByPath.TryGetValue(
                        path,
                        out MethodCatalog.ManagedAssemblyPart? part))
                {
                    part = MethodCatalog.ReadManagedTypes(path, this.m_assemblyNames);
                    this.m_loadedPartsByPath.Add(path, part);
                }

                TypeEntry[] nextTypes = this.m_types
                    .Concat(part.Types)
                    .DistinctBy(type => type.Id, StringComparer.Ordinal)
                    .OrderBy(type => type.Id, StringComparer.Ordinal)
                    .ToArray();
                if (nextTypes.Length != this.m_types.Length)
                {
                    IReadOnlyDictionary<string, TypeEntry> nextTypesById = nextTypes.ToDictionary(
                        type => type.Id,
                        StringComparer.Ordinal);
                    IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> nextTypesByLogicalId =
                        IndexMany(
                            nextTypes,
                            type => type.AliasIds.Prepend(type.LogicalId),
                            type => type.Id);

                    this.m_types = nextTypes;
                    this.m_typesById = nextTypesById;
                    this.m_typesByLogicalId = nextTypesByLogicalId;
                    this.m_missingTypeRelations = ReadMissingTypeRelations(
                        nextTypes,
                        nextTypesByLogicalId);
                    this.m_derivedTypesByBaseId = IndexMany(
                        nextTypes.Where(type => type.BaseType != null),
                        type => ResolveDefinitionIds(type.BaseType!.DefinitionId),
                        type => type.Id);
                    this.m_implementingTypesByInterfaceId = BuildInterfaceIndex(nextTypes);
                }

                return part.Types;
            }
        }

        // 核对候选文件与调用点记录的完整程序集身份。
        private static bool MatchesAssemblyIdentity(
            string path,
            System.Reflection.AssemblyName expected)
        {
            System.Reflection.AssemblyName actual =
                System.Reflection.AssemblyName.GetAssemblyName(path);

            return string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase)
                && Equals(actual.Version, expected.Version)
                && string.Equals(
                    actual.CultureName ?? string.Empty,
                    expected.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (actual.GetPublicKeyToken() ?? Array.Empty<byte>())
                    .SequenceEqual(expected.GetPublicKeyToken() ?? Array.Empty<byte>());
        }

        // 在动态调用确实依赖类型层级时要求整条层级关系完整。
        /// <summary>
        /// 验证指定类型的全部基类和接口都能由当前真实材料定位。
        /// </summary>
        public void RequireClosedHierarchy(TypeEntry type)
        {
            Stack<TypeEntry> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);

            pending.Push(type);
            while (pending.TryPop(out TypeEntry? current))
            {
                if (!visited.Add(current.Id))
                {
                    continue;
                }

                foreach (TypeRelationEntry relation in current.Interfaces
                             .Prepend(current.BaseType)
                             .Where(relation => relation != null)
                             .Cast<TypeRelationEntry>())
                {
                    IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(
                        relation.DefinitionId,
                        current);
                    if (definitions.Count == 0 && current.AssemblyPath != null)
                    {
                        LoadAssemblyTypes(
                            new System.Reflection.AssemblyName(relation.AssemblyIdentity),
                            current.AssemblyPath);
                        definitions = FindTypeDefinitions(relation.DefinitionId, current);
                    }

                    if (definitions.Count == 0)
                    {
                        throw new AnalysisException(
                            $"类型层级无法闭合：{current.Id} => {relation.DefinitionId}"
                            + (current.AssemblyPath == null ? string.Empty : $"，来源：{current.AssemblyPath}"));
                    }

                    foreach (TypeEntry definition in definitions)
                    {
                        pending.Push(definition);
                    }
                }
            }
        }

        // 使用源码程序集上下文读取一个函数引用。
        internal ManagedMethodReferenceInfo ReadSourceMethodReference(
            IMethodSymbol method,
            string callerAssemblyName)
        {
            return MethodCatalog.ReadSourceMethodReference(
                method,
                RequireSourceContext(callerAssemblyName));
        }

        // 使用源码程序集上下文读取一个字段引用。
        internal ManagedFieldReferenceInfo ReadSourceFieldReference(
            IFieldSymbol field,
            string callerAssemblyName)
        {
            return MethodCatalog.ReadSourceFieldReference(
                field,
                RequireSourceContext(callerAssemblyName));
        }

        // 使用源码程序集上下文读取一个事件字段引用。
        internal ManagedFieldReferenceInfo ReadSourceEventReference(
            IEventSymbol eventSymbol,
            string callerAssemblyName)
        {
            return MethodCatalog.ReadSourceEventReference(
                eventSymbol,
                RequireSourceContext(callerAssemblyName));
        }

        // 使用源码程序集上下文读取一个自动属性后备字段引用。
        internal ManagedFieldReferenceInfo ReadSourcePropertyStorageReference(
            IPropertySymbol property,
            string callerAssemblyName)
        {
            return MethodCatalog.ReadSourcePropertyStorageReference(
                property,
                RequireSourceContext(callerAssemblyName));
        }

        // 使用源码程序集上下文读取一个类型引用。
        internal ManagedTypeReferenceInfo ReadSourceTypeReference(
            ITypeSymbol type,
            string callerAssemblyName)
        {
            return MethodCatalog.ReadSourceTypeReference(
                type,
                RequireSourceContext(callerAssemblyName));
        }

        // 找到已经建立统一身份规则的源码程序集上下文。
        private MethodCatalog.SourceCatalogContext RequireSourceContext(string assemblyName)
        {
            return this.m_sourceContextsByAssembly.TryGetValue(
                assemblyName,
                out MethodCatalog.SourceCatalogContext? context)
                ? context
                : throw new AnalysisException($"源码程序集没有进入函数总表：{assemblyName}");
        }

        // 使用 Cecil 读取托管调用点的函数引用。
        internal ManagedMethodReferenceInfo ReadManagedMethodReference(
            Cecil.MethodReference method,
            string referringAssemblyPath)
        {
            MethodIdentityTemplate identity = MethodCatalog.ManagedMethodIdentity(
                method,
                this.m_assemblyNames);
            TypeIdentityTemplate[] declaringArguments = MethodCatalog.ReadManagedTypeArguments(
                method.DeclaringType,
                this.m_assemblyNames).ToArray();
            TypeIdentityTemplate[] genericArguments = method is Cecil.GenericInstanceMethod generic
                ? generic.GenericArguments.Select(argument =>
                    MethodCatalog.ManagedTypeIdentity(argument, this.m_assemblyNames)).ToArray()
                : Array.Empty<TypeIdentityTemplate>();
            Cecil.MethodReference element = method.GetElementMethod();

            return new ManagedMethodReferenceInfo(
                identity,
                method.HasThis,
                identity.ReturnType.Text == "System.Void",
                element.Parameters.Select(parameter =>
                    parameter.ParameterType is Cecil.ByReferenceType
                        ? parameter.IsOut
                            ? CatalogRefKind.Out
                            : parameter.IsIn
                                ? CatalogRefKind.In
                                : CatalogRefKind.Ref
                        : CatalogRefKind.None).ToArray(),
                genericArguments,
                TypeIdentityTemplate.NamedType(MethodCatalog.ManagedNamedTypeDefinitionId(
                    MethodCatalog.ManagedNamedTypeElement(method.DeclaringType),
                    this.m_assemblyNames)),
                declaringArguments,
                MethodCatalog.ReadManagedAssemblyFullName(
                    MethodCatalog.ManagedNamedTypeElement(method.DeclaringType)),
                referringAssemblyPath);
        }

        // 使用 Cecil 读取托管字段引用。
        internal ManagedFieldReferenceInfo ReadManagedFieldReference(
            Cecil.FieldReference field)
        {
            TypeIdentityTemplate[] declaringArguments = MethodCatalog.ReadManagedTypeArguments(
                field.DeclaringType,
                this.m_assemblyNames).ToArray();
            TypeIdentityTemplate fieldType = MethodCatalog.ManagedTypeIdentity(
                field.FieldType,
                this.m_assemblyNames).Substitute(declaringArguments);

            return new ManagedFieldReferenceInfo(
                MethodCatalog.ManagedTypeIdentity(field.DeclaringType, this.m_assemblyNames),
                field.Name,
                fieldType,
                TypeIdentityTemplate.NamedType(MethodCatalog.ManagedNamedTypeDefinitionId(
                    MethodCatalog.ManagedNamedTypeElement(field.DeclaringType),
                    this.m_assemblyNames)),
                declaringArguments);
        }

        // 使用 Cecil 读取托管类型引用。
        internal ManagedTypeReferenceInfo ReadManagedTypeReference(
            Cecil.TypeReference type,
            string referringAssemblyPath)
        {
            TypeIdentityTemplate identity = MethodCatalog.ManagedTypeIdentity(type, this.m_assemblyNames);
            Cecil.TypeReference definition = MethodCatalog.ManagedNamedTypeElement(type);

            return new ManagedTypeReferenceInfo(
                identity,
                TypeIdentityTemplate.NamedType(MethodCatalog.ManagedNamedTypeDefinitionId(
                    definition,
                    this.m_assemblyNames)),
                MethodCatalog.ReadManagedTypeArguments(type, this.m_assemblyNames).ToArray(),
                MethodCatalog.ReadManagedAssemblyFullName(definition),
                referringAssemblyPath);
        }

        // 读取一个托管类型直接声明的函数事实。
        private IReadOnlyList<MethodEntry> ReadManagedMethodDeclarations(TypeEntry type)
        {
            Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
            lock (module)
            {
                return MethodCatalog.ReadManagedTypeMethods(
                    type,
                    this.m_assemblyNames,
                    module);
            }
        }

        // 为一个托管函数补齐显式实现、隐式接口实现和普通重写目标。
        private MethodEntry CompleteManagedRelations(
            MethodEntry method,
            IReadOnlyList<InheritedTypeRelation> inherited)
        {
            HashSet<string> relations = method.RelatedMethodIds.ToHashSet(StringComparer.Ordinal);
            foreach (InheritedTypeRelation relation in inherited.Where(item =>
                         item.IsInterface && item.CanImplementInterface))
            {
                if (!method.IsPublic || method.IsStatic)
                {
                    break;
                }

                foreach (MethodEntry target in GetMethods(relation.Definition))
                {
                    if (HasMatchingSignature(method, target, relation.TypeArguments, true))
                    {
                        relations.Add(target.LogicalId);
                    }
                }
            }

            if (method.IsVirtual && !method.IsNewSlot)
            {
                foreach (IGrouping<int, InheritedTypeRelation> level in inherited
                             .Where(item => !item.IsInterface)
                             .GroupBy(item => item.Depth)
                             .OrderBy(group => group.Key))
                {
                    string[] matches = level.SelectMany(relation => GetMethods(relation.Definition)
                            .Where(target => target.IsVirtual
                                && HasMatchingSignature(method, target, relation.TypeArguments, false)))
                        .Select(target => target.LogicalId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (matches.Length > 0)
                    {
                        relations.UnionWith(matches);
                        break;
                    }
                }
            }

            return method with { RelatedMethodIds = relations.Order(StringComparer.Ordinal).ToArray() };
        }

        // 精确比较两个函数在构造继承关系下的名称、元数和参数签名。
        private static bool HasMatchingSignature(
            MethodEntry method,
            MethodEntry target,
            IReadOnlyList<TypeIdentityTemplate> targetTypeArguments,
            bool isInterface)
        {
            if (method.Name != target.Name
                || method.GenericArity != target.GenericArity
                || method.Parameters.Count != target.Parameters.Count
                || method.IsStatic != target.IsStatic)
            {
                return false;
            }

            bool parametersMatch = method.Parameters.Zip(target.Parameters).All(pair =>
                pair.First.RefKind == pair.Second.RefKind
                && pair.First.TypeId == pair.Second.TypeIdentity.Substitute(targetTypeArguments).Text);
            bool returnMatches = !isInterface
                || method.ReturnTypeId == TypeIdentityTemplate.Literal(target.ReturnTypeId)
                    .Substitute(targetTypeArguments)
                    .Text;

            return parametersMatch && returnMatches;
        }

        // 列出一个类型直接或间接继承的全部构造基类和接口。
        private IReadOnlyList<InheritedTypeRelation> ReadInheritedTypes(TypeEntry type)
        {
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<InheritedTypeRelation> result = new();

            PushRelations(pending, type, 1, canImplementInterface: true);
            while (pending.Count > 0)
            {
                (TypeRelationEntry relation, TypeEntry owner, bool isInterface,
                    bool canImplementInterface, int depth) = pending.Pop();
                foreach (TypeEntry definition in FindTypeDefinitions(relation.DefinitionId, owner))
                {
                    string key = $"{definition.Id}|{relation.TypeId}|{isInterface}";
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    TypeIdentityTemplate[] arguments = relation.TypeArguments
                        .Select(TypeIdentityTemplate.Literal)
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
                        arguments);
                }
            }

            return result;
        }

        // 把一个类型的直接基类和接口放入继承遍历栈。
        private static void PushRelations(
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending,
            TypeEntry type,
            int depth,
            bool canImplementInterface,
            IReadOnlyList<TypeIdentityTemplate>? ownerArguments = null)
        {
            if (type.BaseType != null)
            {
                pending.Push((
                    InstantiateRelation(type.BaseType, ownerArguments),
                    type,
                    false,
                    false,
                    depth));
            }

            foreach (TypeRelationEntry relation in type.Interfaces.Reverse())
            {
                pending.Push((
                    InstantiateRelation(relation, ownerArguments),
                    type,
                    true,
                    canImplementInterface,
                    depth));
            }
        }

        // 用当前构造类型实参实例化一条继承关系。
        private static TypeRelationEntry InstantiateRelation(
            TypeRelationEntry relation,
            IReadOnlyList<TypeIdentityTemplate>? ownerArguments)
        {
            if (ownerArguments == null || ownerArguments.Count == 0)
            {
                return relation;
            }

            TypeIdentityTemplate type = TypeIdentityTemplate.Literal(relation.TypeId)
                .Substitute(ownerArguments);
            TypeIdentityTemplate[] arguments = relation.TypeArguments
                .Select(TypeIdentityTemplate.Literal)
                .Select(argument => argument.Substitute(ownerArguments))
                .ToArray();

            return relation with
            {
                TypeId = type.Text,
                TypeArguments = arguments.Select(argument => argument.Text).ToArray(),
            };
        }

        // 按逻辑身份定位类型定义并优先选择同一物理程序集。
        private IReadOnlyList<TypeEntry> FindTypeDefinitions(string definitionId, TypeEntry owner)
        {
            IReadOnlyList<TypeEntry>? candidates;
            lock (this.m_loadedTypeLock)
            {
                candidates = this.m_typesByLogicalId.GetValueOrDefault(definitionId);
            }

            if (candidates == null)
            {
                return Array.Empty<TypeEntry>();
            }

            TypeEntry[] sameAssembly = candidates.Where(candidate => string.Equals(
                    candidate.AssemblyPath,
                    owner.AssemblyPath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return sameAssembly.Length == 0 ? candidates : sameAssembly;
        }

        // 列出材料总表中当前没有目标定义的直接类型关系。
        private static IReadOnlyList<MissingTypeRelationEntry> ReadMissingTypeRelations(
            IReadOnlyList<TypeEntry> types,
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> typesByLogicalId)
        {
            return types.SelectMany(type => type.Interfaces
                    .Select(relation => new MissingTypeRelationEntry(
                        type.Id,
                        type.FullName,
                        relation.DefinitionId,
                        true,
                        type.AssemblyPath))
                    .Prepend(type.BaseType == null
                        ? null
                        : new MissingTypeRelationEntry(
                            type.Id,
                            type.FullName,
                            type.BaseType.DefinitionId,
                            false,
                            type.AssemblyPath))
                    .Where(relation => relation != null)
                    .Cast<MissingTypeRelationEntry>())
                .Where(relation => !typesByLogicalId.ContainsKey(relation.TargetTypeId))
                .Distinct()
                .OrderBy(relation => relation.OwnerTypeId, StringComparer.Ordinal)
                .ThenBy(relation => relation.TargetTypeId, StringComparer.Ordinal)
                .ToArray();
        }

        // 为一个真实文件只建立一份 Cecil 模块并协调并行首次读取。
        private Cecil.ModuleDefinition GetManagedModule(string path)
        {
            lock (this.m_moduleLock)
            {
                if (!this.m_modulesByPath.TryGetValue(path, out Cecil.ModuleDefinition? module))
                {
                    module = Cecil.ModuleDefinition.ReadModule(
                        path,
                        new Cecil.ReaderParameters
                        {
                            InMemory = true,
                            ReadingMode = Cecil.ReadingMode.Deferred,
                        });
                    this.m_modulesByPath.Add(path, module);
                }

                return module;
            }
        }

        // 把参考身份转换为全部真实类型身份。
        private IReadOnlyList<string> ResolveDefinitionIds(string definitionId)
        {
            return this.TypesByLogicalId.TryGetValue(definitionId, out IReadOnlyList<TypeEntry>? types)
                ? types.Select(type => type.LogicalId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : new[] { definitionId };
        }

        // 建立接口到全部直接或间接实现类型的索引。
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> BuildInterfaceIndex(
            IReadOnlyList<TypeEntry> types)
        {
            return types.SelectMany(type => ReadInheritedInterfaceIds(type)
                    .Select(interfaceId => (InterfaceId: interfaceId, Type: type)))
                .GroupBy(item => item.InterfaceId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<TypeEntry>)group.Select(item => item.Type)
                        .DistinctBy(type => type.Id, StringComparer.Ordinal)
                        .OrderBy(type => type.Id, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        // 读取一个类型继承的全部接口定义身份。
        private IReadOnlyList<string> ReadInheritedInterfaceIds(TypeEntry type)
        {
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface)> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            HashSet<string> result = new(StringComparer.Ordinal);

            if (type.BaseType != null)
            {
                pending.Push((type.BaseType, type, false));
            }

            foreach (TypeRelationEntry relation in type.Interfaces)
            {
                pending.Push((relation, type, true));
            }

            while (pending.Count > 0)
            {
                (TypeRelationEntry relation, TypeEntry owner, bool isInterface) = pending.Pop();
                foreach (TypeEntry definition in FindTypeDefinitions(relation.DefinitionId, owner))
                {
                    string key = $"{definition.Id}|{isInterface}";
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    if (isInterface)
                    {
                        result.Add(definition.LogicalId);
                    }

                    if (definition.BaseType != null)
                    {
                        pending.Push((definition.BaseType, definition, false));
                    }

                    foreach (TypeRelationEntry inheritedInterface in definition.Interfaces)
                    {
                        pending.Push((inheritedInterface, definition, true));
                    }
                }
            }

            return result.Order(StringComparer.Ordinal).ToArray();
        }

        // 按单个键建立固定顺序的索引。
        private static IReadOnlyDictionary<string, IReadOnlyList<T>> Index<T>(
            IEnumerable<T> items,
            Func<T, string> key,
            Func<T, string> order)
            where T : class
        {
            return items.GroupBy(key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<T>)group.OrderBy(order, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        }

        // 按每个项目声明的多个键建立固定顺序的反向索引。
        private static IReadOnlyDictionary<string, IReadOnlyList<T>> IndexMany<T>(
            IEnumerable<T> items,
            Func<T, IEnumerable<string>> keys,
            Func<T, string> order)
            where T : class
        {
            return items.SelectMany(item => keys(item).Select(key => (Key: key, Item: item)))
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<T>)group.Select(pair => pair.Item)
                        .OrderBy(order, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        /// <summary>保存一条已经闭合到类型定义的继承关系。</summary>
        private sealed record InheritedTypeRelation(
            TypeEntry Definition,
            IReadOnlyList<TypeIdentityTemplate> TypeArguments,
            bool IsInterface,
            bool CanImplementInterface,
            int Depth);
    }
}
