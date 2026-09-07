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
                .Except(material.AnalyzerPaths, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<string, string> assemblyNames = ReadAssemblyNames(
                material.SourceAssemblies,
                managedPaths,
                assemblyLookupPaths,
                jobs);
            SourceCatalogContext[] sourceContexts = material.SourceAssemblies
                .Select(assembly => new SourceCatalogContext(
                    assembly,
                    assembly.SourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    assembly.ReportSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            ConcurrentBag<ManagedAssemblyPart> managedParts = new();
            ConcurrentBag<SourceManagedPart> sourceManagedParts = new();
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
                    if (workItem.SourceContext == null)
                    {
                        managedParts.Add(ReadManagedTypes(workItem.Path, assemblyNames));
                    }
                    else
                    {
                        SourceCatalogContext context = workItem.SourceContext;
                        ManagedAssemblyPart part = ReadManagedTypes(
                            workItem.Path,
                            context.Material.AssemblyImage,
                            assemblyNames);
                        IReadOnlyDictionary<string, INamedTypeSymbol> sourceTypes = EnumerateTypes(
                                context.Material.Compilation.Assembly.GlobalNamespace)
                            .Where(type => IsDeclaredSourceType(type, context))
                            .ToDictionary(ReadDocumentationId, StringComparer.Ordinal);
                        TypeEntry[] declaredTypes = part.Types.Where(type =>
                            sourceTypes.ContainsKey(type.DocumentationId)).ToArray();
                        sourceManagedParts.Add(new SourceManagedPart(
                            context,
                            part,
                            ReadManagedMethods(
                                context.Material.AssemblyImage,
                                declaredTypes,
                                assemblyNames),
                            sourceTypes));
                    }

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            SourceManagedPart[] orderedSourceManagedParts = sourceManagedParts
                .OrderBy(item => item.Part.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ManagedAssemblyPart[] orderedManagedParts = managedParts
                .OrderBy(part => part.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SourceTypeWorkItem[] sourceTypeWorkItems = orderedSourceManagedParts
                .SelectMany(ReadSourceTypeWorkItems)
                .ToArray();
            TypeEntry[] sourceTypes = sourceTypeWorkItems.Select(item => item.SourceType).ToArray();
            HashSet<string> sourceMetadataTypeIds = sourceTypeWorkItems
                .Select(item => item.MetadataType.Id)
                .ToHashSet(StringComparer.Ordinal);
            TypeEntry[] generatedSourceTypes = orderedSourceManagedParts
                .SelectMany(item => item.Part.Types)
                .Where(type => !sourceMetadataTypeIds.Contains(type.Id))
                .ToArray();
            TypeEntry[] managedTypes = orderedManagedParts.SelectMany(part => part.Types).ToArray();
            ManagedAssemblyPart[] allManagedParts = orderedManagedParts
                .Concat(orderedSourceManagedParts.Select(item => item.Part))
                .OrderBy(part => part.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            TypeEntry[] allTypes = AttachForwardedAliases(
                sourceTypes.Concat(generatedSourceTypes).Concat(managedTypes).ToArray(),
                allManagedParts.SelectMany(part => part.Forwarders).ToArray());
            ConcurrentBag<IReadOnlyList<MethodEntry>> sourceMethodParts = new();

            await Parallel.ForEachAsync(
                sourceTypeWorkItems,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (workItem, _) =>
                {
                    sourceMethodParts.Add(ReadSourceTypeMethods(workItem));

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
                allManagedParts,
                sourceContexts,
                assemblyNames,
                assemblyLookupPaths,
                material.UnityRuntimeFacadePaths,
                material.AssemblyRedirects,
                stopwatch,
                jobs);
        }

        // 读取全部实际程序集名称并固定名称大小写。
        private static IReadOnlyDictionary<string, string> ReadAssemblyNames(
            IReadOnlyList<SourceAssemblyMaterial> sourceAssemblies,
            IReadOnlyList<string> managedPaths,
            IReadOnlyList<string> assemblyLookupPaths,
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
                .Concat(assemblyLookupPaths.Select(path =>
                    System.Reflection.AssemblyName.GetAssemblyName(path).Name!))
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

        // 核对候选文件的完整程序集身份。
        internal static bool MatchesAssemblyFile(
            string path,
            System.Reflection.AssemblyName expected)
        {
            return MatchesAssemblyIdentity(
                System.Reflection.AssemblyName.GetAssemblyName(path),
                expected);
        }

        // 核对已读取类型的完整程序集身份。
        internal static bool MatchesAssemblyIdentity(
            string actualIdentity,
            System.Reflection.AssemblyName expected)
        {
            return MatchesAssemblyIdentity(
                new System.Reflection.AssemblyName(actualIdentity),
                expected);
        }

        // 比较程序集名称、版本、区域和公钥标记。
        private static bool MatchesAssemblyIdentity(
            System.Reflection.AssemblyName actual,
            System.Reflection.AssemblyName expected)
        {
            return string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase)
                && Equals(actual.Version, expected.Version)
                && string.Equals(
                    actual.CultureName ?? string.Empty,
                    expected.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (actual.GetPublicKeyToken() ?? Array.Empty<byte>())
                    .SequenceEqual(expected.GetPublicKeyToken() ?? Array.Empty<byte>());
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

        // 判断类型是用户源文件声明而不是编译或生成器产物。
        private static bool IsDeclaredSourceType(
            INamedTypeSymbol type,
            SourceCatalogContext context)
        {
            return type.Locations.Any(location => location.IsInSource
                && location.SourceTree != null
                && context.DeclaredPaths.Contains(location.SourceTree.FilePath));
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
                FullName = DisplaySourceType(definition),
                Attributes = ReadSourceAttributes(definition.GetAttributes()),
                SourceSymbol = definition,
            };
        }

        // 把用户源码类型和内存 PE 中的同一命名类型配对。
        private static IReadOnlyList<SourceTypeWorkItem> ReadSourceTypeWorkItems(
            SourceManagedPart assembly)
        {
            IReadOnlyDictionary<string, TypeEntry> metadataTypes = assembly.Part.Types
                .ToDictionary(type => type.DocumentationId, StringComparer.Ordinal);
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByTypeId = assembly.Methods
                .GroupBy(method => method.TypeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<MethodEntry>)group.ToArray(),
                    StringComparer.Ordinal);

            return assembly.SourceTypesByDocumentationId.Select(item =>
                {
                    TypeEntry metadata = metadataTypes.TryGetValue(item.Key, out TypeEntry? found)
                        ? found
                        : throw new AnalysisException($"源码类型没有元数据定义：{item.Key}");

                    return new SourceTypeWorkItem(
                        assembly.Context,
                        item.Value,
                        CreateSourceType(item.Value, metadata),
                        metadata,
                        methodsByTypeId.GetValueOrDefault(metadata.Id)
                            ?? Array.Empty<MethodEntry>());
                })
                .ToArray();
        }

        // 读取一个源码类型直接声明的全部函数。
        private static IReadOnlyList<MethodEntry> ReadSourceTypeMethods(SourceTypeWorkItem workItem)
        {
            Dictionary<string, IMethodSymbol> sourceMethods = workItem.Symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Select(NormalizeMethod)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .ToDictionary(ReadDocumentationId, StringComparer.Ordinal);
            List<MethodEntry> result = new(workItem.MetadataMethods.Count);
            foreach (MethodEntry metadata in workItem.MetadataMethods)
            {
                if (sourceMethods.Remove(metadata.DocumentationId, out IMethodSymbol? source))
                {
                    result.Add(CreateSourceMethod(source, workItem.SourceType, workItem.Context, metadata));
                }
                else
                {
                    result.Add(metadata with
                    {
                        TypeId = workItem.SourceType.Id,
                        TypeName = workItem.SourceType.FullName,
                        TypeAttributes = workItem.SourceType.Attributes,
                    });
                }
            }

            IMethodSymbol? missing = sourceMethods.Values.FirstOrDefault(method =>
                !method.IsImplicitlyDeclared);
            if (missing != null)
            {
                throw new AnalysisException(
                    $"源码函数没有元数据定义：{ReadDocumentationId(missing)}");
            }

            return result.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray();
        }

        // 把源码显示和标签信息附加到 Cecil 已读取的函数事实。
        private static MethodEntry CreateSourceMethod(
            IMethodSymbol method,
            TypeEntry type,
            SourceCatalogContext context,
            MethodEntry metadata)
        {
            IMethodSymbol definition = NormalizeMethod(method);
            Location? location = definition.Locations.FirstOrDefault(item => item.IsInSource);
            string? sourcePath = location?.SourceTree?.FilePath;
            int line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1;
            if (metadata.Parameters.Count != definition.Parameters.Length)
            {
                throw new AnalysisException(
                    $"源码函数与元数据参数数量不一致：{metadata.LogicalId}");
            }

            ParameterEntry[] parameters = metadata.Parameters.Zip(definition.Parameters)
                .Select(pair => pair.First with
                {
                    Name = pair.Second.Name,
                    TypeName = DisplaySourceType(pair.Second.Type)
                        + (pair.Second.RefKind == RefKind.None ? string.Empty : "&"),
                })
                .ToArray();
            string returnTypeName = DisplaySourceType(definition.ReturnType)
                + (definition.ReturnsByRef || definition.ReturnsByRefReadonly
                    ? "&"
                    : string.Empty);
            CatalogMethodKind kind = definition.MethodKind == MethodKind.Destructor
                ? CatalogMethodKind.Destructor
                : metadata.Kind;

            return metadata with
            {
                Id = metadata.LogicalId,
                TypeId = type.Id,
                TypeName = type.FullName,
                ReturnTypeName = returnTypeName,
                Parameters = parameters,
                Kind = kind,
                MethodAttributes = ReadSourceAttributes(definition.GetAttributes()),
                TypeAttributes = type.Attributes,
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

        // 还原扩展函数、部分函数及构造函数的原始声明。
        private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
        {
            return (method.ReducedFrom ?? method.PartialImplementationPart ?? method).OriginalDefinition;
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

        // 生成边界明确的程序集加类型身份。
        internal static string NamedTypeId(string assemblyName, string metadataName)
        {
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

        /// <summary>保存一个源码程序集的 Cecil 类型和命名类型函数。</summary>
        private sealed record SourceManagedPart(
            SourceCatalogContext Context,
            ManagedAssemblyPart Part,
            IReadOnlyList<MethodEntry> Methods,
            IReadOnlyDictionary<string, INamedTypeSymbol> SourceTypesByDocumentationId);

        /// <summary>保存一个用户源码类型与它的实际元数据。</summary>
        private sealed record SourceTypeWorkItem(
            SourceCatalogContext Context,
            INamedTypeSymbol Symbol,
            TypeEntry SourceType,
            TypeEntry MetadataType,
            IReadOnlyList<MethodEntry> MetadataMethods);

        // 使用 Cecil 读取一个真实托管文件中的全部类型和转交声明。
        internal static ManagedAssemblyPart ReadManagedTypes(
            string path,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string fullPath = Path.GetFullPath(path);
            using Cecil.ModuleDefinition module = OpenModule(fullPath);

            return ReadManagedTypes(fullPath, module, assemblyNames);
        }

        // 从当前源码的内存 PE 读取类型和转交声明。
        internal static ManagedAssemblyPart ReadManagedTypes(
            string path,
            byte[] image,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string fullPath = Path.GetFullPath(path);
            using Cecil.ModuleDefinition module = OpenModule(image);

            return ReadManagedTypes(fullPath, module, assemblyNames);
        }

        // 把已打开模块转换为统一类型目录。
        private static ManagedAssemblyPart ReadManagedTypes(
            string fullPath,
            Cecil.ModuleDefinition module,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            string assemblyName = CanonicalAssemblyName(assemblyNames, module.Assembly.Name.Name);
            TypeEntry[] types = module.GetAllTypes()
                .Where(type => type.Name != "<Module>" || !string.IsNullOrEmpty(type.Namespace))
                .Select(type => CreateManagedType(type, fullPath, assemblyName, assemblyNames))
                .ToArray();
            ForwardedTypeEntry[] forwarders = module.ExportedTypes
                .Where(type => ReadForwardedRoot(type).IsForwarder)
                .Select(type => CreateForwarder(
                    type,
                    fullPath,
                    assemblyName,
                    module.Assembly.Name.FullName,
                    assemblyNames))
                .ToArray();

            return new ManagedAssemblyPart(
                fullPath,
                types,
                forwarders);
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
                type.Module.Assembly.Name.FullName,
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
                type.IsAbstract,
                null,
                path,
                type.MetadataToken.ToInt32())
            {
                DocumentationId = DocCommentId.GetDocCommentId(type),
                IsValueType = type.IsValueType,
                IsNullableValueType = type.Namespace == "System" && type.Name == "Nullable`1"
                    && (type.Module.TypeSystem.CoreLibrary is Cecil.ModuleDefinition coreModule
                        && coreModule == type.Module
                        || type.Module.TypeSystem.CoreLibrary is Cecil.AssemblyNameReference coreLibrary
                        && coreLibrary.FullName == type.Module.Assembly.Name.FullName),
                GenericParameters = type.GenericParameters.Select(parameter =>
                    new GenericParameterRule(
                        parameter.IsCovariant,
                        parameter.IsContravariant,
                        parameter.HasReferenceTypeConstraint,
                        parameter.HasNotNullableValueTypeConstraint,
                        parameter.HasDefaultConstructorConstraint,
                        parameter.Constraints.Select(constraint => ManagedTypeIdentity(
                            constraint.ConstraintType,
                            assemblyNames)).ToArray())).ToArray(),
            };
        }

        // 把一个 Cecil 基类或接口保存为结构化关系。
        private static TypeRelationEntry CreateManagedRelation(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            TypeIdentityTemplate identity = ManagedTypeIdentity(type, assemblyNames);
            TypeIdentityTemplate[] arguments = ReadManagedTypeArguments(type, assemblyNames).ToArray();

            return new TypeRelationEntry(
                ManagedNamedTypeDefinitionId(type.GetElementType(), assemblyNames),
                identity.Text,
                ReadManagedAssemblyFullName(type.GetElementType()),
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
            string facadeAssemblyIdentity,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            Cecil.IMetadataScope scope = ReadForwardedRoot(type).Scope;
            if (scope is not Cecil.AssemblyNameReference assembly)
            {
                throw new AnalysisException($"类型转交目标不是程序集：{type.FullName}");
            }

            return new ForwardedTypeEntry(
                NamedTypeId(facadeAssemblyName, NormalizeManagedFullName(type.FullName)),
                facadeAssemblyIdentity,
                NamedTypeId(
                    CanonicalAssemblyName(assemblyNames, assembly.Name),
                    NormalizeManagedFullName(type.FullName)),
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

        // 把参考程序集身份附加到唯一真实类型。
        internal static TypeEntry[] AttachForwardedAliases(
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<ForwardedTypeEntry> forwarders)
        {
            IReadOnlyDictionary<ForwardedTypeKey, TypeEntry> targets =
                ResolveForwardedTargets(types, forwarders);
            IReadOnlyDictionary<string, string[]> aliasesByTypeId = targets
                .GroupBy(item => item.Value.Id, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Key.TypeId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);

            return types.Select(type =>
                {
                    string[] aliases = aliasesByTypeId.GetValueOrDefault(type.Id)
                        ?? Array.Empty<string>();

                    return aliases.Length == 0 ? type : type with
                    {
                        AliasIds = type.AliasIds.Concat(aliases)
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                    };
                })
                .ToArray();
        }

        // 按每层完整程序集身份将转交链压到最终类型。
        internal static IReadOnlyDictionary<ForwardedTypeKey, TypeEntry> ResolveForwardedTargets(
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<ForwardedTypeEntry> forwarders)
        {
            IReadOnlyDictionary<ForwardedTypeKey, TypeEntry[]> definitions = types
                .GroupBy(type => CreateForwardedTypeKey(type.LogicalId, type.AssemblyIdentity))
                .ToDictionary(group => group.Key, group => group.ToArray());
            IReadOnlyDictionary<ForwardedTypeKey, ForwardedTypeEntry[]> forwardersByAlias = forwarders
                .GroupBy(forwarder => CreateForwardedTypeKey(
                    forwarder.AliasTypeId,
                    forwarder.AliasAssemblyIdentity))
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray());
            IReadOnlyDictionary<ForwardedTypeKey, ForwardedTypeKey[]> nextByAlias =
                forwardersByAlias.ToDictionary(
                    item => item.Key,
                    item => item.Value.Select(forwarder => CreateForwardedTypeKey(
                        forwarder.TargetTypeId,
                        forwarder.TargetAssemblyIdentity))
                    .Distinct()
                    .OrderBy(target => target.TypeId, StringComparer.Ordinal)
                    .ThenBy(target => target.AssemblyIdentity, StringComparer.Ordinal)
                    .ToArray());

            Dictionary<ForwardedTypeKey, TypeEntry?> resolved = new();
            foreach (ForwardedTypeKey alias in nextByAlias.Keys)
            {
                ResolveForwardedTarget(
                    alias,
                    definitions,
                    forwardersByAlias,
                    nextByAlias,
                    resolved,
                    new HashSet<ForwardedTypeKey>());
            }

            return resolved.Where(item => nextByAlias.ContainsKey(item.Key) && item.Value != null)
                .ToDictionary(item => item.Key, item => item.Value!);
        }

        // 递归找到一条转交链的唯一最终定义。
        private static TypeEntry? ResolveForwardedTarget(
            ForwardedTypeKey alias,
            IReadOnlyDictionary<ForwardedTypeKey, TypeEntry[]> definitions,
            IReadOnlyDictionary<ForwardedTypeKey, ForwardedTypeEntry[]> forwardersByAlias,
            IReadOnlyDictionary<ForwardedTypeKey, ForwardedTypeKey[]> nextByAlias,
            IDictionary<ForwardedTypeKey, TypeEntry?> resolved,
            ISet<ForwardedTypeKey> visiting)
        {
            if (resolved.TryGetValue(alias, out TypeEntry? known))
            {
                return known;
            }

            if (!visiting.Add(alias))
            {
                throw new AnalysisException(
                    $"类型转交形成循环：{alias.TypeId} @ {alias.AssemblyIdentity}");
            }

            definitions.TryGetValue(alias, out TypeEntry[]? declaredTypes);
            bool hasForwarder = nextByAlias.TryGetValue(alias, out ForwardedTypeKey[]? targets);
            bool sameFileNameOverride = declaredTypes?.Length == 1
                && hasForwarder
                && forwardersByAlias[alias].All(forwarder => string.Equals(
                    forwarder.AssemblyPath,
                    declaredTypes[0].AssemblyPath,
                    StringComparison.OrdinalIgnoreCase));
            if (declaredTypes?.Length > 0 && hasForwarder && !sameFileNameOverride)
            {
                throw new AnalysisException(
                    $"类型身份同时存在定义和转交：{alias.TypeId} @ {alias.AssemblyIdentity}");
            }

            TypeEntry? result;
            if (declaredTypes?.Length > 1)
            {
                throw new AnalysisException(
                    $"类型转交目标不唯一：{alias.TypeId} @ {alias.AssemblyIdentity}");
            }
            else if (declaredTypes?.Length == 1 && !sameFileNameOverride)
            {
                result = declaredTypes[0];
            }
            else if (hasForwarder)
            {
                TypeEntry?[] targetDefinitions = targets!.Select(target => ResolveForwardedTarget(
                        target,
                        definitions,
                        forwardersByAlias,
                        nextByAlias,
                        resolved,
                        visiting))
                    .ToArray();
                if (targetDefinitions.Any(target => target == null))
                {
                    result = null;
                }
                else
                {
                    TypeEntry[] finalDefinitions = targetDefinitions.Cast<TypeEntry>()
                        .DistinctBy(type => type.Id, StringComparer.Ordinal)
                        .ToArray();
                    if (finalDefinitions.Length != 1)
                    {
                        throw new AnalysisException(
                            $"参考类型身份对应多个真实类型：{alias.TypeId} @ "
                                + $"{alias.AssemblyIdentity} => "
                                + string.Join("; ", finalDefinitions.Select(type => type.Id)));
                    }

                    result = finalDefinitions[0];
                }
            }
            else
            {
                result = null;
            }

            visiting.Remove(alias);
            resolved[alias] = result;
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

        // 用一次内存 PE 读取建立多个源码命名类型的函数声明。
        private static IReadOnlyList<MethodEntry> ReadManagedMethods(
            byte[] image,
            IReadOnlyList<TypeEntry> types,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            using Cecil.ModuleDefinition module = OpenModule(image);

            return types.SelectMany(type => ReadManagedTypeMethods(type, assemblyNames, module))
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
                method.MetadataToken.ToInt32())
            {
                DocumentationId = DocCommentId.GetDocCommentId(method),
            };
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
        internal static CatalogRefKind ReadManagedRefKind(Cecil.ParameterDefinition parameter)
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
                    element.DeclaringType.GetElementType(),
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
        internal static MethodIdentityTemplate ManagedMethodDefinitionIdentity(
            Cecil.MethodReference method,
            IReadOnlyDictionary<string, string> assemblyNames,
            Func<Cecil.TypeReference, string>? namedTypeId = null)
        {
            Cecil.MethodReference element = method.GetElementMethod();

            return new MethodIdentityTemplate(
                TypeIdentityTemplate.NamedType(namedTypeId == null
                    ? ManagedNamedTypeDefinitionId(element.DeclaringType.GetElementType(), assemblyNames)
                    : namedTypeId(element.DeclaringType.GetElementType())),
                element.Name,
                element.GenericParameters.Count,
                element.Parameters.Select(parameter =>
                    ManagedTypeIdentity(parameter.ParameterType, assemblyNames, namedTypeId)).ToArray(),
                ManagedTypeIdentity(element.ReturnType, assemblyNames, namedTypeId));
        }

        // 建立 Cecil 类型定义或构造类型的确定身份。
        internal static TypeIdentityTemplate ManagedTypeIdentity(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames,
            Func<Cecil.TypeReference, string>? namedTypeId = null)
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
                        namedTypeId == null ? ManagedNamedTypeDefinitionId(instance.ElementType, assemblyNames)
                            : namedTypeId(instance.ElementType))
                    .Append($"<{string.Join(',', instance.GenericArguments.Select(argument =>
                        ManagedTypeIdentity(argument, assemblyNames, namedTypeId).Text))}>"),
                Cecil.ArrayType array => ManagedTypeIdentity(array.ElementType, assemblyNames, namedTypeId)
                    .Append($"[{new string(',', array.Rank - 1)}]"),
                Cecil.ByReferenceType reference => ManagedTypeIdentity(reference.ElementType, assemblyNames, namedTypeId)
                    .Append("&"),
                Cecil.PointerType pointer => ManagedTypeIdentity(pointer.ElementType, assemblyNames, namedTypeId)
                    .Append("*"),
                Cecil.PinnedType pinned => ManagedTypeIdentity(pinned.ElementType, assemblyNames, namedTypeId),
                Cecil.OptionalModifierType optional => ManagedTypeIdentity(optional.ElementType, assemblyNames, namedTypeId),
                Cecil.RequiredModifierType required => ManagedTypeIdentity(required.ElementType, assemblyNames, namedTypeId),
                Cecil.FunctionPointerType function => TypeIdentityTemplate.Literal(
                    DisplayManagedType(function)),
                Cecil.SentinelType sentinel => ManagedTypeIdentity(sentinel.ElementType, assemblyNames, namedTypeId)
                    .Append("..."),
                _ => TypeIdentityTemplate.NamedType(namedTypeId == null
                    ? ManagedNamedTypeDefinitionId(type, assemblyNames) : namedTypeId(type)),
            };
        }

        // 读取 Cecil 构造类型的全部实际类型参数。
        internal static IEnumerable<TypeIdentityTemplate> ReadManagedTypeArguments(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames,
            Func<Cecil.TypeReference, string>? namedTypeId = null)
        {
            if (type is not Cecil.GenericInstanceType instance)
            {
                return Array.Empty<TypeIdentityTemplate>();
            }

            return instance.GenericArguments.Select(argument =>
                ManagedTypeIdentity(argument, assemblyNames, namedTypeId)).ToArray();
        }

        // 建立 Cecil 命名类型定义的跨文件身份。
        internal static string ManagedNamedTypeDefinitionId(
            Cecil.TypeReference type,
            IReadOnlyDictionary<string, string> assemblyNames)
        {
            Cecil.TypeReference element = type.GetElementType();
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
            return NormalizeManagedFullName(type.GetElementType().FullName);
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
        internal sealed record ForwardedTypeEntry(
            string AliasTypeId,
            string AliasAssemblyIdentity,
            string TargetTypeId,
            string TargetAssemblyIdentity,
            string AssemblyPath);

        /// <summary>保存转交链中的类型身份和完整程序集身份。</summary>
        internal readonly record struct ForwardedTypeKey(
            string TypeId,
            string AssemblyIdentity);
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

            System.Text.StringBuilder result = new();
            for (int index = 0; index < this.Text.Length; index++)
            {
                char current = this.Text[index];
                int start = index + 1;
                bool methodParameter = start < this.Text.Length && this.Text[start] == '!';
                start += methodParameter ? 1 : 0;
                if (current != '!' || start >= this.Text.Length || !char.IsDigit(this.Text[start]))
                {
                    result.Append(current);
                    continue;
                }

                int end = start;
                while (end < this.Text.Length && char.IsDigit(this.Text[end]))
                {
                    end++;
                }

                int position = int.Parse(this.Text.AsSpan(start, end - start));
                IReadOnlyList<TypeIdentityTemplate>? actualArguments = methodParameter ? methodArguments : arguments;
                if (actualArguments == null || position >= actualArguments.Count)
                {
                    result.Append(this.Text, index, end - index);
                }
                else
                {
                    result.Append(actualArguments[position].Text);
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

    /// <summary>保存一个函数声明及其实际声明类型参数。</summary>
    internal sealed record ResolvedMethodDefinition(
        MethodEntry Method,
        IReadOnlyList<TypeIdentityTemplate> DeclaringTypeArguments);

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
        IReadOnlyList<string> AliasIds,
        string AssemblyName,
        string AssemblyIdentity,
        string Name,
        string FullName,
        TypeRelationEntry? BaseType,
        IReadOnlyList<TypeRelationEntry> Interfaces,
        IReadOnlyList<AttributeEntry> Attributes,
        bool IsInterface,
        bool IsAbstract,
        INamedTypeSymbol? SourceSymbol,
        string? AssemblyPath,
        int MetadataToken)
    {
        internal string DocumentationId { get; init; } = string.Empty;

        internal bool IsValueType { get; init; }

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
        int MetadataToken)
    {
        internal string DocumentationId { get; init; } = string.Empty;
    }

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
        private readonly ConcurrentDictionary<(string Path, int Token), MethodIdentityTemplate>
            m_resolvedSignatures = new();
        private readonly ConcurrentDictionary<(string Path, int Token), IReadOnlyList<TypeIdentityTemplate>>
            m_resolvedTypeArguments = new();
        private readonly ConcurrentDictionary<(string Path, int Token), TypeIdentityTemplate>
            m_resolvedFieldTypes = new();
        private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ResolvedMethodDefinition[]>>
            m_explicitTargetsByTypeId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Cecil.ModuleDefinition> m_modulesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object m_moduleLock = new();
        private readonly IReadOnlyDictionary<string, string> m_assemblyNames;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> m_lookupPathsByAssemblyName;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>>
            m_unityFacadePathsByAssemblyName;
        private readonly IReadOnlyDictionary<string, AssemblyRedirectTarget> m_assemblyRedirects;
        private readonly Dictionary<string, MethodCatalog.ManagedAssemblyPart>
            m_loadedPartsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> m_completedAssemblyPaths =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object m_loadedTypeLock = new();
        private TypeEntry[] m_types;
        private IReadOnlyDictionary<string, TypeEntry> m_typesById;
        private IReadOnlyDictionary<string, TypeEntry> m_typesByManagedLocation;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_typesByLogicalId;
        private IReadOnlyDictionary<MethodCatalog.ForwardedTypeKey, TypeEntry> m_forwardedTargets;
        private IReadOnlySet<MethodCatalog.ForwardedTypeKey> m_forwardedAliases;
        private IReadOnlyList<MissingTypeRelationEntry> m_missingTypeRelations;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_derivedTypesByBaseId;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> m_implementingTypesByInterfaceId;
        private readonly IReadOnlyDictionary<string, MethodCatalog.SourceCatalogContext>
            m_sourceContextsByAssembly;
        private readonly IReadOnlyDictionary<string, byte[]> m_sourceImagesByPath;

        // 保存排好顺序的函数、类型和关系索引。
        internal MethodCatalogResult(
            IReadOnlyList<MethodEntry> methods,
            IReadOnlyList<TypeEntry> types,
            IReadOnlyList<MethodCatalog.ManagedAssemblyPart> managedParts,
            IReadOnlyList<MethodCatalog.SourceCatalogContext> sourceContexts,
            IReadOnlyDictionary<string, string> assemblyNames,
            IReadOnlyList<string> assemblyLookupPaths,
            IReadOnlyList<string> unityRuntimeFacadePaths,
            IReadOnlyDictionary<string, string> assemblyRedirects,
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
            this.m_unityFacadePathsByAssemblyName = unityRuntimeFacadePaths
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
                this.m_completedAssemblyPaths.Add(part.Path);
            }

            this.m_types = types.ToArray();
            this.m_forwardedTargets = MethodCatalog.ResolveForwardedTargets(
                types,
                managedParts.SelectMany(part => part.Forwarders).ToArray());
            this.m_forwardedAliases = managedParts.SelectMany(part => part.Forwarders)
                .Select(forwarder => MethodCatalog.CreateForwardedTypeKey(
                    forwarder.AliasTypeId,
                    forwarder.AliasAssemblyIdentity))
                .ToHashSet();
            IReadOnlyDictionary<string, TypeEntry> typesById = null!;
            IReadOnlyDictionary<string, IReadOnlyList<MethodEntry>> methodsByTypeId = null!;
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> typesByLogicalId = null!;
            ParallelOptions options = new() { MaxDegreeOfParallelism = jobs };

            Parallel.Invoke(
                options,
                () => typesById = types.ToDictionary(type => type.Id, StringComparer.Ordinal),
                () => methodsByTypeId = Index(methods, method => method.TypeId, method => method.Id),
                () => typesByLogicalId = IndexMany(
                    types,
                    type => type.AliasIds.Prepend(type.LogicalId),
                    type => type.Id));

            this.m_typesById = typesById;
            this.m_typesByManagedLocation = IndexManagedTypeLocations(types);
            this.MethodsByTypeId = methodsByTypeId;
            this.m_typesByLogicalId = typesByLogicalId;
            this.m_missingTypeRelations = ReadMissingTypeRelations(types, typesByLogicalId);
            this.m_derivedTypesByBaseId = IndexMany(
                types.Where(type => type.BaseType != null),
                type => ResolveRelationDefinitionIds(type.BaseType!, type),
                type => type.Id);
            this.m_implementingTypesByInterfaceId = BuildInterfaceIndex(types);
            MethodEntry[] completedMethods = CompleteSourceRelations(methods, jobs);
            this.Methods = completedMethods;
            this.MethodsByTypeId = Index(completedMethods, method => method.TypeId, method => method.Id);
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

        // 按类型并行补齐源码函数的隐式接口实现和普通重写关系。
        private MethodEntry[] CompleteSourceRelations(
            IReadOnlyList<MethodEntry> methods,
            int jobs)
        {
            ConcurrentBag<MethodEntry> completed = new();
            Parallel.ForEach(
                methods.GroupBy(method => method.TypeId, StringComparer.Ordinal),
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                group =>
                {
                    TypeEntry type = this.m_typesById.TryGetValue(group.Key, out TypeEntry? found)
                        ? found
                        : throw new AnalysisException($"源码函数的声明类型不存在：{group.Key}");
                    IReadOnlyList<InheritedTypeRelation> inherited = ReadInheritedTypes(type);
                    IReadOnlyDictionary<string, ResolvedMethodDefinition[]> explicitTargets = ReadExplicitMethodTargets(type);
                    foreach (MethodEntry method in group)
                    {
                        completed.Add(CompleteManagedRelations(method, inherited, explicitTargets));
                    }
                });

            return completed.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray();
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

        // 通过源码编译所用核心库把原始类型身份还原成唯一真实类型。
        internal TypeEntry ReadPrimitiveType(string identity)
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
                    MethodCatalog.NamedTypeId(this.m_assemblyNames.GetValueOrDefault(name) ?? name, identity),
                    type.ContainingAssembly.Identity.GetDisplayName(), context.Material.AssemblyPath, loadMissing: true);
                return definitions.Count == 1 ? definitions[0]
                    : throw new AnalysisException($"原始类型定义不唯一：{context.Material.Name} => {identity}，命中 {definitions.Count} 个定义");
            }).DistinctBy(type => type.Id, StringComparer.Ordinal).ToArray();

            return meanings.Length == 1 ? meanings[0]
                : throw new AnalysisException($"原始类型存在不同运行定义：{identity} => {string.Join("; ", meanings.Select(type => type.Id))}");
        }

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
            IReadOnlyList<MethodEntry> methods = ReadDeclaredMethods(type);
            IReadOnlyList<InheritedTypeRelation> inherited = ReadInheritedTypes(type);
            IReadOnlyDictionary<string, ResolvedMethodDefinition[]> explicitTargets = ReadExplicitMethodTargets(type);

            return methods.Select(method => CompleteManagedRelations(method, inherited, explicitTargets))
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
        }

        // 普通调用只读取类型直接声明，继承实现关系在动态调用需要时补全。
        private IReadOnlyList<MethodEntry> ReadDeclaredMethods(TypeEntry type)
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

            Cecil.IMetadataScope coreLibrary = GetManagedModule(type.AssemblyPath).TypeSystem.CoreLibrary;
            string coreName;
            string coreIdentity;
            if (coreLibrary is Cecil.ModuleDefinition coreModule)
            {
                coreName = coreModule.Assembly.Name.Name;
                coreIdentity = coreModule.Assembly.Name.FullName;
            }
            else if (coreLibrary is Cecil.AssemblyNameReference coreReference)
            {
                coreName = coreReference.Name;
                coreIdentity = coreReference.FullName;
            }
            else
            {
                throw new AnalysisException($"类型核心库身份不是程序集：{type.Id}");
            }

            string expectedId = MethodCatalog.NamedTypeId(
                this.m_assemblyNames.GetValueOrDefault(coreName) ?? coreName,
                "System.MulticastDelegate");
            TypeEntry[] actualBases = FindTypeDefinitions(
                type.BaseType,
                type,
                loadMissing: true).ToArray();
            TypeEntry[] coreBases = FindTypeDefinitions(
                expectedId,
                coreIdentity,
                type.AssemblyPath,
                loadMissing: true).ToArray();
            if (actualBases.Length != 1 || coreBases.Length != 1)
            {
                throw new AnalysisException($"委托基类没有唯一运行时定义：{type.Id}");
            }

            return actualBases[0].Id == coreBases[0].Id;
        }

        // 按调用点的完整类型和函数签名找到唯一声明。
        internal ResolvedMethodDefinition ResolveMethodDefinition(
            BehaviorMethodReference reference,
            bool searchInherited)
        {
            IReadOnlyList<TypeIdentityTemplate> arguments = ReadResolvedTypeArguments(
                reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken);
            TypeEntry[] declaringTypes = reference.KnownDeclaringTypeId == null
                ? FindReferencedTypes(reference).ToArray()
                : new[]
                {
                    this.TypesById.TryGetValue(reference.KnownDeclaringTypeId, out TypeEntry? known)
                        ? known
                        : throw new AnalysisException(
                            $"本地函数声明类型尚未载入：{reference.KnownDeclaringTypeId}"),
                };
            ResolvedMethodDefinition[] matches = reference.KnownMetadataToken is int metadataToken
                ? declaringTypes.SelectMany(type => ReadDeclaredMethods(type).Where(method =>
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

        // 按开放类型身份和完整程序集身份筛选真实声明类型。
        private IReadOnlyList<TypeEntry> FindReferencedTypes(BehaviorMethodReference reference)
        {
            return FindTypeDefinitions(
                reference.DeclaringTypeDefinitionId,
                reference.TargetAssemblyIdentity,
                reference.ReferringAssemblyPath,
                loadMissing: true);
        }

        // 核对类型本身的真实程序集身份。
        private bool IsDirectlyDeclaredInAssembly(
            TypeEntry type,
            System.Reflection.AssemblyName identity)
        {
            AssemblyRedirectTarget target = NormalizeAssemblyReference(identity);

            return MethodCatalog.MatchesAssemblyIdentity(type.AssemblyIdentity, target.Identity)
                && (target.Path == null || string.Equals(
                    type.AssemblyPath,
                    target.Path,
                    StringComparison.OrdinalIgnoreCase));
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
            RequireClosedHierarchy(
                type,
                includeBaseTypes: !searchInterfaces,
                includeInterfaces: searchInterfaces);
            foreach (IGrouping<int, InheritedTypeRelation> level in ReadInheritedTypes(
                         type,
                         ReadResolvedTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken))
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
            return ReadDeclaredMethods(type).Where(method => MethodMatchesReference(
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

                return MethodCatalog.ManagedMethodDefinitionIdentity(method, this.m_assemblyNames,
                    type => ReadResolvedTypeId(type, key.Path));
            });
        }

        // 从原始类型或函数引用读取实参，保留每个命名类型的实际来源。
        private IReadOnlyList<TypeIdentityTemplate> ReadResolvedTypeArguments(string path, int token)
        {
            return this.m_resolvedTypeArguments.GetOrAdd((path, token), key =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(key.Path);
                Cecil.IMetadataTokenProvider reference;
                lock (module)
                {
                    reference = module.LookupToken(key.Token);
                }

                Cecil.TypeReference type = reference is Cecil.MethodReference method
                    ? method.DeclaringType
                    : (Cecil.TypeReference)reference;

                return MethodCatalog.ReadManagedTypeArguments(type, this.m_assemblyNames,
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

                TypeIdentityTemplate[] arguments = MethodCatalog.ReadManagedTypeArguments(field.DeclaringType,
                    this.m_assemblyNames, type => ReadResolvedTypeId(type, key.Path)).ToArray();
                return MethodCatalog.ManagedTypeIdentity(field.FieldType, this.m_assemblyNames,
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
            string path = ResolveAssemblyPath(identity, referringAssemblyPath);
            lock (this.m_loadedTypeLock)
            {
                if (!this.m_loadedPartsByPath.TryGetValue(
                        path,
                        out MethodCatalog.ManagedAssemblyPart? part))
                {
                    part = this.m_sourceImagesByPath.TryGetValue(path, out byte[]? image)
                        ? MethodCatalog.ReadManagedTypes(path, image, this.m_assemblyNames)
                        : MethodCatalog.ReadManagedTypes(path, this.m_assemblyNames);
                    this.m_loadedPartsByPath.Add(path, part);
                }
                else if (this.m_completedAssemblyPaths.Contains(path))
                {
                    return part.Types.Select(type => this.m_typesByManagedLocation[
                        ManagedTypeLocation(path, type.MetadataToken)]).ToArray();
                }

                TypeEntry[] addedTypes = this.m_types
                    .Concat(part.Types)
                    .DistinctBy(type => type.Id, StringComparer.Ordinal)
                    .ToArray();
                MethodCatalog.ForwardedTypeEntry[] knownForwarders = this.m_loadedPartsByPath.Values
                    .SelectMany(item => item.Forwarders)
                    .ToArray();
                TypeEntry[] nextTypes = MethodCatalog.AttachForwardedAliases(
                        addedTypes,
                        knownForwarders)
                    .OrderBy(type => type.Id, StringComparer.Ordinal)
                    .ToArray();
                this.m_forwardedTargets = MethodCatalog.ResolveForwardedTargets(
                    nextTypes,
                    knownForwarders);
                this.m_forwardedAliases = knownForwarders.Select(forwarder =>
                        MethodCatalog.CreateForwardedTypeKey(
                            forwarder.AliasTypeId,
                            forwarder.AliasAssemblyIdentity))
                    .ToHashSet();
                if (!nextTypes.SequenceEqual(this.m_types))
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
                    this.m_typesByManagedLocation = IndexManagedTypeLocations(nextTypes);
                    this.m_typesByLogicalId = nextTypesByLogicalId;
                    this.m_missingTypeRelations = ReadMissingTypeRelations(
                        nextTypes,
                        nextTypesByLogicalId);
                    this.m_derivedTypesByBaseId = IndexMany(
                        nextTypes.Where(type => type.BaseType != null),
                        type => ResolveRelationDefinitionIds(type.BaseType!, type),
                        type => type.Id);
                    this.m_implementingTypesByInterfaceId = BuildInterfaceIndex(nextTypes);
                }

                this.m_completedAssemblyPaths.Add(path);
                return part.Types.Select(type => this.m_typesByManagedLocation[
                    ManagedTypeLocation(path, type.MetadataToken)]).ToArray();
            }
        }

        // 为函数总表与函数体读取共用同一套完整程序集身份定位规则。
        internal string ResolveAssemblyPath(
            System.Reflection.AssemblyName identity,
            string referringAssemblyPath)
        {
            AssemblyRedirectTarget target = NormalizeAssemblyReference(identity);
            if (target.Path != null)
            {
                return target.Path;
            }

            identity = target.Identity;
            string assemblyName = identity.Name
                ?? throw new AnalysisException("程序集引用没有名称。");
            if (this.m_sourceContextsByAssembly.TryGetValue(
                    assemblyName,
                    out MethodCatalog.SourceCatalogContext? sourceContext)
                && MethodCatalog.MatchesAssemblyIdentity(
                    sourceContext.Material.Compilation.Assembly.Identity.GetDisplayName(),
                    identity))
            {
                return Path.GetFullPath(sourceContext.Material.AssemblyPath);
            }

            string[] candidates = this.m_lookupPathsByAssemblyName.TryGetValue(
                    assemblyName,
                    out IReadOnlyList<string>? knownPaths)
                ? knownPaths.Where(path => MethodCatalog.MatchesAssemblyFile(path, identity)).ToArray()
                : Array.Empty<string>();
            if (candidates.Length == 0
                && this.m_unityFacadePathsByAssemblyName.TryGetValue(
                    assemblyName,
                    out IReadOnlyList<string>? facadePaths))
            {
                candidates = facadePaths.Where(path => MatchesUnityFacadeAssembly(path, identity))
                    .ToArray();
            }

            string[] localCandidates = candidates.Where(path => string.Equals(
                    Path.GetDirectoryName(path),
                    Path.GetDirectoryName(referringAssemblyPath),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return localCandidates.Length == 1
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
        }

        // 按 Unity 运行时的门面身份规则核对已验证候选。
        private static bool MatchesUnityFacadeAssembly(
            string path,
            System.Reflection.AssemblyName expected)
        {
            System.Reflection.AssemblyName candidate =
                System.Reflection.AssemblyName.GetAssemblyName(path);

            return string.Equals(candidate.Name, expected.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidate.CultureName ?? string.Empty,
                    expected.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (candidate.GetPublicKeyToken() ?? Array.Empty<byte>())
                    .SequenceEqual(expected.GetPublicKeyToken() ?? Array.Empty<byte>())
                && candidate.Version?.Major >= expected.Version?.Major;
        }

        // 在动态调用确实依赖类型层级时要求整条层级关系完整。
        /// <summary>
        /// 验证指定类型的全部基类和接口都能由当前真实材料定位。
        /// </summary>
        public void RequireClosedHierarchy(TypeEntry type)
        {
            RequireClosedHierarchy(type, includeBaseTypes: true, includeInterfaces: true);
        }

        // 只闭合当前调用查找确实依赖的类型关系。
        private void RequireClosedHierarchy(
            TypeEntry type,
            bool includeBaseTypes,
            bool includeInterfaces)
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

                IEnumerable<TypeRelationEntry> relations = includeInterfaces
                    ? current.Interfaces
                    : Array.Empty<TypeRelationEntry>();
                if (includeBaseTypes && current.BaseType != null)
                {
                    relations = relations.Prepend(current.BaseType);
                }

                foreach (TypeRelationEntry relation in relations)
                {
                    IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(
                        relation,
                        current,
                        loadMissing: true);

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

        // 使用 Cecil 读取托管调用点的函数引用。
        internal BehaviorMethodReference ReadManagedMethodReference(
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
            Cecil.MethodReference definition = method;
            while (definition is Cecil.GenericInstanceMethod instance)
            {
                definition = instance.ElementMethod;
            }

            return new BehaviorMethodReference(
                identity.DeclaringType.Text,
                MethodCatalog.ManagedNamedTypeDefinitionId(method.DeclaringType.GetElementType(), this.m_assemblyNames),
                declaringArguments.Select(type => type.Text).ToArray(),
                identity.Name,
                identity.GenericArity,
                identity.Parameters.Select(type => type.Text).ToArray(),
                identity.ReturnType.Text,
                genericArguments.Select(type => type.Text).ToArray(),
                method.HasThis,
                MethodCatalog.ReadManagedAssemblyFullName(
                    method.DeclaringType.GetElementType()),
                referringAssemblyPath)
            {
                Identity = identity,
                GenericArgumentIdentities = genericArguments,
                ReferenceMetadataToken = method.MetadataToken.ToInt32(),
                KnownDeclaringTypeId = ReadKnownTypeId(method.DeclaringType, referringAssemblyPath),
                KnownMetadataToken = definition is Cecil.MethodDefinition methodDefinition
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
                field.DeclaringType,
                this.m_assemblyNames).ToArray();
            TypeIdentityTemplate fieldType = MethodCatalog.ManagedTypeIdentity(
                field.FieldType,
                this.m_assemblyNames).Substitute(declaringArguments);

            TypeIdentityTemplate declaringType = MethodCatalog.ManagedTypeIdentity(field.DeclaringType, this.m_assemblyNames);
            return new BehaviorMemberReference(
                declaringType.Text,
                MethodCatalog.ManagedNamedTypeDefinitionId(field.DeclaringType.GetElementType(), this.m_assemblyNames),
                declaringArguments.Select(type => type.Text).ToArray(),
                field.Name,
                fieldType.Text)
            {
                DeclaringTypeIdentity = declaringType,
                FieldTypeIdentity = fieldType,
                ReferenceMetadataToken = field.MetadataToken.ToInt32(),
                KnownDeclaringTypeId = ReadKnownTypeId(field.DeclaringType, referringAssemblyPath),
                TargetAssemblyIdentity = MethodCatalog.ReadManagedAssemblyFullName(
                    field.DeclaringType.GetElementType()),
                ReferringAssemblyPath = referringAssemblyPath,
            };
        }

        // 使用 Cecil 读取托管类型引用。
        internal BehaviorTypeReference ReadManagedTypeReference(
            Cecil.TypeReference type,
            string referringAssemblyPath)
        {
            TypeIdentityTemplate identity = MethodCatalog.ManagedTypeIdentity(type, this.m_assemblyNames);
            Cecil.TypeReference definition = type.GetElementType();

            return BehaviorTypeReference.Create(
                identity,
                TypeIdentityTemplate.NamedType(MethodCatalog.ManagedNamedTypeDefinitionId(
                    definition,
                    this.m_assemblyNames)),
                MethodCatalog.ReadManagedTypeArguments(type, this.m_assemblyNames).ToArray(),
                MethodCatalog.ReadManagedAssemblyFullName(definition),
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
                        definition,
                        this.m_assemblyNames))
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
                    this.m_assemblyNames,
                    module);
            }
        }

        // 按类型读取真实方法实现表，保留接口构造实参，供显式优先和关系补全共用。
        internal IReadOnlyDictionary<string, ResolvedMethodDefinition[]> ReadExplicitMethodTargets(TypeEntry type)
        {
            return this.m_explicitTargetsByTypeId.GetOrAdd(type.Id, _ =>
            {
                Dictionary<string, ResolvedMethodDefinition[]> result = new(StringComparer.Ordinal);
                Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
                foreach (MethodEntry method in ReadDeclaredMethods(type))
                {
                    Cecil.MethodReference[] overrides;
                    lock (module)
                    {
                        overrides = ((Cecil.MethodDefinition)module.LookupToken(method.MetadataToken)).Overrides.ToArray();
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

        // 为一个托管函数补齐显式实现、隐式接口实现和普通重写目标。
        private MethodEntry CompleteManagedRelations(
            MethodEntry method,
            IReadOnlyList<InheritedTypeRelation> inherited,
            IReadOnlyDictionary<string, ResolvedMethodDefinition[]> explicitTargets)
        {
            HashSet<string> relations = new((explicitTargets.GetValueOrDefault(method.Id)
                ?? Array.Empty<ResolvedMethodDefinition>()).Select(target => target.Method.LogicalId), StringComparer.Ordinal);
            foreach (InheritedTypeRelation relation in inherited.Where(item =>
                         item.IsInterface && item.CanImplementInterface))
            {
                if (!method.IsPublic || method.IsStatic)
                {
                    break;
                }

                foreach (MethodEntry target in ReadDeclaredMethods(relation.Definition))
                {
                    if (HasMatchingSignature(method, target, relation.TypeArguments, true)
                        && !explicitTargets.Values.SelectMany(targets => targets).Any(implementation =>
                            implementation.Method.Id == target.Id && implementation.DeclaringTypeArguments.Select(type => type.Text)
                                .SequenceEqual(relation.TypeArguments.Select(type => type.Text), StringComparer.Ordinal)))
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
                    string[] matches = level.SelectMany(relation => ReadDeclaredMethods(relation.Definition)
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
        private bool HasMatchingSignature(
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

            MethodIdentityTemplate implementation = ReadResolvedMethodSignature(method.AssemblyPath!, method.MetadataToken);
            MethodIdentityTemplate declaration = ReadResolvedMethodSignature(target.AssemblyPath!, target.MetadataToken);
            bool parametersMatch = method.Parameters.Zip(target.Parameters).All(pair =>
                    pair.First.RefKind == pair.Second.RefKind)
                && implementation.Parameters.Select(parameter => parameter.Text).SequenceEqual(
                    declaration.Parameters.Select(parameter => parameter.Substitute(targetTypeArguments).Text),
                    StringComparer.Ordinal);
            bool returnMatches = !isInterface
                || implementation.ReturnType.Text == declaration.ReturnType.Substitute(targetTypeArguments).Text;

            return parametersMatch && returnMatches;
        }

        // 列出一个类型直接或间接继承的全部构造基类和接口。
        internal IReadOnlyList<InheritedTypeRelation> ReadInheritedTypes(
            TypeEntry type,
            IReadOnlyList<TypeIdentityTemplate>? typeArguments = null)
        {
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<InheritedTypeRelation> result = new();

            PushRelations(pending, type, 1, canImplementInterface: true, typeArguments);
            while (pending.Count > 0)
            {
                (TypeRelationEntry relation, TypeEntry owner, bool isInterface,
                    bool canImplementInterface, int depth) = pending.Pop();
                foreach (TypeEntry definition in FindTypeDefinitions(relation, owner))
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
        private void PushRelations(
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
                    InstantiateRelation(type.BaseType, type, ownerArguments),
                    type,
                    false,
                    false,
                    depth));
            }

            foreach (TypeRelationEntry relation in type.Interfaces.Reverse())
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
            string definitionId,
            string assemblyIdentity,
            string? referringAssemblyPath,
            bool loadMissing)
        {
            System.Reflection.AssemblyName identity = new(assemblyIdentity);
            TypeEntry[] definitions = FindLoadedTypeDefinitions(definitionId, identity);
            if (definitions.Length != 0)
            {
                return definitions;
            }

            if (!loadMissing || referringAssemblyPath == null)
            {
                return Array.Empty<TypeEntry>();
            }

            string forwardingAssemblyPath = ResolveAssemblyPath(identity, referringAssemblyPath);
            LoadAssemblyTypes(identity, referringAssemblyPath);
            definitions = FindLoadedTypeDefinitions(definitionId, identity);
            if (definitions.Length != 0)
            {
                return definitions;
            }

            MethodCatalog.ForwardedTypeKey? alias = null;
            (MethodCatalog.ForwardedTypeEntry Forwarder, string Path)[] forwarders;
            lock (this.m_loadedTypeLock)
            {
                MethodCatalog.ForwardedTypeEntry[] selected =
                    this.m_loadedPartsByPath[forwardingAssemblyPath].Forwarders
                    .Where(forwarder => forwarder.AliasTypeId == definitionId)
                    .ToArray();
                if (selected.Length > 0)
                {
                    alias = MethodCatalog.CreateForwardedTypeKey(
                        selected[0].AliasTypeId,
                        selected[0].AliasAssemblyIdentity);
                }

                forwarders = alias == null
                    ? Array.Empty<(MethodCatalog.ForwardedTypeEntry, string)>()
                    : this.m_loadedPartsByPath.Values.SelectMany(part => part.Forwarders
                            .Where(forwarder => MethodCatalog.CreateForwardedTypeKey(
                                forwarder.AliasTypeId,
                                forwarder.AliasAssemblyIdentity) == alias)
                            .Select(forwarder => (forwarder, part.Path)))
                        .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                        .DistinctBy(item => MethodCatalog.CreateForwardedTypeKey(
                            item.forwarder.TargetTypeId,
                            item.forwarder.TargetAssemblyIdentity))
                        .ToArray();
            }

            if (forwarders.Length == 0)
            {
                return Array.Empty<TypeEntry>();
            }

            List<TypeEntry> targets = new();
            foreach ((MethodCatalog.ForwardedTypeEntry forwarder, string path) in forwarders)
            {
                IReadOnlyList<TypeEntry> branch = FindTypeDefinitions(
                    forwarder.TargetTypeId,
                    forwarder.TargetAssemblyIdentity,
                    path,
                    loadMissing: true);
                if (branch.Count == 0)
                {
                    throw new AnalysisException(
                        $"类型转交目标没有定义：{forwarder.AliasTypeId} @ "
                            + $"{forwarder.AliasAssemblyIdentity} => {forwarder.TargetTypeId} @ "
                            + forwarder.TargetAssemblyIdentity);
                }

                targets.AddRange(branch);
            }

            TypeEntry[] finalDefinitions = targets.DistinctBy(type => type.Id, StringComparer.Ordinal)
                .ToArray();
            if (finalDefinitions.Length != 1)
            {
                throw new AnalysisException(
                    $"参考类型身份对应多个真实类型：{alias!.Value.TypeId} @ "
                        + $"{alias.Value.AssemblyIdentity} => "
                        + string.Join("; ", finalDefinitions.Select(type => type.Id)));
            }

            return finalDefinitions;
        }

        // 按定义身份或完整转交身份读取当前已经闭合的类型。
        private TypeEntry[] FindLoadedTypeDefinitions(
            string definitionId,
            System.Reflection.AssemblyName identity)
        {
            AssemblyRedirectTarget redirected = NormalizeAssemblyReference(identity);
            MethodCatalog.ForwardedTypeKey alias = MethodCatalog.CreateForwardedTypeKey(
                definitionId,
                redirected.Identity.FullName
                    ?? throw new AnalysisException("程序集引用没有完整身份。"));
            lock (this.m_loadedTypeLock)
            {
                if (this.m_forwardedTargets.TryGetValue(alias, out TypeEntry? target))
                {
                    return new[] { target };
                }

                if (this.m_forwardedAliases.Contains(alias))
                {
                    return Array.Empty<TypeEntry>();
                }

                return this.m_typesByLogicalId.GetValueOrDefault(definitionId)
                    ?.Where(type => IsDirectlyDeclaredInAssembly(type, identity))
                    .ToArray()
                    ?? Array.Empty<TypeEntry>();
            }
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
                    module = this.m_sourceImagesByPath.TryGetValue(path, out byte[]? image)
                        ? MethodCatalog.OpenModule(image)
                        : MethodCatalog.OpenModule(path);
                    this.m_modulesByPath.Add(path, module);
                }

                return module;
            }
        }

        // 把一条具体继承关系转换为它实际指向的类型身份。
        private IReadOnlyList<string> ResolveRelationDefinitionIds(
            TypeRelationEntry relation,
            TypeEntry owner)
        {
            IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(relation, owner);
            return definitions.Count == 0
                ? new[] { relation.DefinitionId }
                : definitions.Select(type => type.LogicalId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
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
                foreach (TypeEntry definition in FindTypeDefinitions(relation, owner))
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
