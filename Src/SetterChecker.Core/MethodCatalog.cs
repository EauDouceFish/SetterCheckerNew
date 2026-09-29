using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

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
            IReadOnlyDictionary<string, string> assemblyAliases = IndexIdenticalAssemblies(assemblyLookupPaths.Concat(managedPaths));
            managedPaths = managedPaths.Select(path => assemblyAliases.GetValueOrDefault(path, path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            SourceCatalogContext[] sourceContexts = material.SourceAssemblies
                .Select(assembly => new SourceCatalogContext(
                    assembly,
                    assembly.SourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    assembly.ReportSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            ConcurrentBag<ManagedAssemblyPart> managedParts = new();
            (string Path, SourceCatalogContext? SourceContext)[] assemblyWorkItems = managedPaths
                .Select(path => (path, (SourceCatalogContext?)null))
                .Concat(sourceContexts.Select(context => (context.Material.AssemblyPath, (SourceCatalogContext?)context)))
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
                    managedParts.Add(ReadManagedTypes(workItem.Path, workItem.SourceContext, jobs));

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            ManagedAssemblyPart[] allManagedParts = managedParts
                .OrderBy(part => part.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            MethodEntry[] sourceMethods = allManagedParts
                .SelectMany(part => part.SourceMethods)
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .ToArray();
            TypeEntry[] orderedTypes = allManagedParts.SelectMany(part => part.Types).OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();

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
                material.ExternalAssemblies,
                assemblyAliases,
                stopwatch,
                jobs)
            {
                CombatAssemblies = material.CombatAssemblies,
                AnalyzedAssemblies = material.CombatAssemblies.Concat(material.SourceAssemblies
                    .Where(assembly => assembly.IsReportAssembly).Select(assembly => assembly.Name)).ToHashSet(StringComparer.Ordinal),
            };
        }

        // 仅对同名候选校验完整内容；相同文件共用定义，原引用路径仍保留为别名。
        private static IReadOnlyDictionary<string, string> IndexIdenticalAssemblies(IEnumerable<string> paths)
        {
            Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
            foreach (var group in paths.Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                Dictionary<string, (string Path, byte[] Image)> contents = new(StringComparer.Ordinal);
                foreach (string path in group.Order(StringComparer.OrdinalIgnoreCase))
                {
                    byte[] image = File.ReadAllBytes(path);
                    string key = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(image));
                    if (contents.TryGetValue(key, out var known))
                    {
                        if (!image.AsSpan().SequenceEqual(known.Image))
                        {
                            throw new AnalysisException($"程序集内容校验冲突：{known.Path}；{path}");
                        }
                        aliases.Add(path, known.Path);
                    }
                    else
                    {
                        contents.Add(key, (path, image));
                    }
                }
            }
            return aliases;
        }

        // 直接读取源码声明与标签，不等待生成元数据或为不同调用者复制声明；各类型互不依赖，按类型并行读取。
        private static ManagedAssemblyPart ReadSourceTypes(SourceCatalogContext context, int jobs)
        {
            INamedTypeSymbol[] declarations = context.Material.Compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>().Where(type => type.Locations.Any(location => location.IsInSource
                    && location.SourceTree != null && context.DeclaredPaths.Contains(location.SourceTree.FilePath)))
                .Select(type => type.OriginalDefinition).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
            var read = new (TypeEntry Type, MethodEntry[] Methods)[declarations.Length];
            Parallel.For(0, declarations.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, index =>
            {
                INamedTypeSymbol symbol = declarations[index];
                string id = SourceNamedTypeId(symbol);
                TypeEntry type = new(id, id, context.Material.Name, symbol.ContainingAssembly.Identity.GetDisplayName(),
                    symbol.Name, symbol.ToDisplayString(s_typeDisplayFormat),
                    symbol.BaseType == null ? null : CreateSourceRelation(symbol.BaseType),
                    symbol.Interfaces.Select(CreateSourceRelation).ToArray(), symbol.TypeKind == TypeKind.Interface,
                    symbol.IsAbstract, symbol, context.Material.AssemblyPath, 0)
                {
                    DocumentationId = ReadDocumentationId(symbol),
                    IsValueType = symbol.IsValueType,
                    IsEnum = symbol.TypeKind == TypeKind.Enum,
                    IsSealed = symbol.IsSealed,
                    IsCompilerGenerated = HasAttribute(symbol, CompilerGeneratedAttribute),
                    IsNullableValueType = symbol.SpecialType == SpecialType.System_Nullable_T,
                    IsExplicitLayout = symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
                        == "System.Runtime.InteropServices.StructLayoutAttribute" && attribute.ConstructorArguments.FirstOrDefault().Value is int kind && kind == 2),
                    GenericParameters = ReadSourceGenericParameters(SourceTypeArguments(symbol).Cast<ITypeParameterSymbol>()),
                    IsCandidate = context.Material.IsCandidateSource,
                };
                read[index] = (type, context.Material.IsReportAssembly
                    ? ReadSourceMethodSymbols(symbol)
                        .Select(method => (method.PartialImplementationPart ?? method).OriginalDefinition)
                        .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                        .Select(method => CreateSourceMethod(method, type, context)).ToArray()
                    : Array.Empty<MethodEntry>());
            });
            return new ManagedAssemblyPart(Path.GetFullPath(context.Material.AssemblyPath), read.Select(item => item.Type).ToArray(), Array.Empty<ForwardedTypeEntry>(),
                context.Material.Compilation.ReferencedAssemblyNames.Select(identity => identity.GetDisplayName()).Order(StringComparer.Ordinal).ToArray())
            { SourceMethods = read.SelectMany(item => item.Methods).OrderBy(method => method.Id, StringComparer.Ordinal).ToArray() };
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
                ?? $"{symbol.ContainingSymbol.GetDocumentationCommentId()}@{symbol.Locations.FirstOrDefault()?.SourceSpan.Start}:{symbol.MetadataName}";
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

        // 保留嵌套类型外层到内层的实际类型参数顺序。
        internal static IEnumerable<ITypeSymbol> SourceTypeArguments(INamedTypeSymbol type)
        {
            return (type.ContainingType == null ? Enumerable.Empty<ITypeSymbol>() : SourceTypeArguments(type.ContainingType)).Concat(type.TypeArguments);
        }

        // 源码声明使用与托管声明相同的完整类型名称。
        internal static string SourceNamedTypeId(INamedTypeSymbol type)
        {
            string name = type.MetadataName;
            for (INamedTypeSymbol? parent = type.ContainingType; parent != null; parent = parent.ContainingType)
            {
                name = parent.MetadataName + "+" + name;
            }
            if (!type.ContainingNamespace.IsGlobalNamespace)
            {
                name = type.ContainingNamespace.ToDisplayString() + "." + name;
            }
            return NamedTypeId(type.ContainingAssembly.Name, name);
        }

        // 将源码类型、数组、引用和泛型位置写成统一签名。
        internal static TypeIdentityTemplate SourceTypeIdentity(ITypeSymbol type, Func<INamedTypeSymbol, string>? namedTypeId = null)
        {
            if (type.TypeKind == TypeKind.Error)
            {
                throw new AnalysisException($"当前源码含未绑定类型：{type}");
            }
            if (type.SpecialType is SpecialType.System_Void or SpecialType.System_Object or SpecialType.System_String
                or SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
            {
                return new TypeIdentityTemplate("System." + type.MetadataName);
            }
            return type switch
            {
                IArrayTypeSymbol array => new(SourceTypeIdentity(array.ElementType, namedTypeId).Text + $"[{new string(',', array.Rank - 1)}]"),
                IPointerTypeSymbol pointer => new(SourceTypeIdentity(pointer.PointedAtType, namedTypeId).Text + "*"),
                ITypeParameterSymbol parameter => new((parameter.TypeParameterKind == TypeParameterKind.Method ? "!!" : "!")
                    + (parameter.Ordinal + (parameter.TypeParameterKind == TypeParameterKind.Type && parameter.ContainingType.ContainingType != null
                        ? SourceTypeArguments(parameter.ContainingType.ContainingType).Count() : 0))),
                IDynamicTypeSymbol => new("System.Object"),
                INamedTypeSymbol named => new((namedTypeId == null ? SourceNamedTypeId(named) : namedTypeId(named)) + (SourceTypeArguments(named).Any()
                    ? "<" + string.Join(',', SourceTypeArguments(named).Select(argument => SourceTypeIdentity(argument, namedTypeId).Text)) + ">" : string.Empty)),
                _ => throw new AnalysisException($"源码类型尚不能读取：{type}"),
            };
        }

        // 源码约束与托管约束使用同一份声明数据。
        private static IReadOnlyList<GenericParameterRule> ReadSourceGenericParameters(IEnumerable<ITypeParameterSymbol> parameters)
        {
            return parameters.Select(parameter => new GenericParameterRule(parameter.Variance == VarianceKind.Out,
                parameter.Variance == VarianceKind.In, parameter.HasReferenceTypeConstraint, parameter.HasValueTypeConstraint,
                parameter.HasConstructorConstraint, parameter.ConstraintTypes.Select(type => SourceTypeIdentity(type)).ToArray())).ToArray();
        }

        // 属性、事件和构造函数与普通函数进入同一目录，符号去重。
        internal static IEnumerable<IMethodSymbol> ReadSourceMethodSymbols(INamedTypeSymbol type, bool overridesOnly = false)
        {
            return type.GetMembers().Where(member => !overridesOnly || member.IsOverride).SelectMany(member => member switch
            {
                IMethodSymbol method => new[] { method },
                IPropertySymbol property => new[] { property.GetMethod, property.SetMethod },
                IEventSymbol eventSymbol => new[] { eventSymbol.AddMethod, eventSymbol.RemoveMethod, eventSymbol.RaiseMethod },
                _ => Array.Empty<IMethodSymbol?>(),
            }).Concat(overridesOnly ? Array.Empty<IMethodSymbol>() : type.StaticConstructors)
                .Concat(overridesOnly ? Array.Empty<IMethodSymbol>() : type.InstanceConstructors).OfType<IMethodSymbol>()
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default);
        }

        // 直接保存源码基类和接口的声明身份及类型参数。
        private static TypeRelationEntry CreateSourceRelation(INamedTypeSymbol type)
        {
            return new TypeRelationEntry(SourceNamedTypeId(type), SourceTypeIdentity(type).Text,
                type.ContainingAssembly.Identity.GetDisplayName(), SourceTypeArguments(type).Count())
            { FullName = type.OriginalDefinition.ToDisplayString(s_typeDisplayFormat), SourceSymbol = type };
        }

        // 从源码符号直接建立函数声明，无需用编译产物反向配对。
        internal static MethodEntry CreateSourceMethod(
            IMethodSymbol definition,
            TypeEntry type,
            SourceCatalogContext context)
        {
            definition = definition.PartialImplementationPart ?? definition;
            Location? location = definition.Locations.FirstOrDefault(item => item.IsInSource);
            string? sourcePath = location?.SourceTree?.FilePath;
            int line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1;
            ParameterEntry[] parameters = definition.Parameters.Select(parameter => new ParameterEntry(parameter.Name,
                SourceTypeIdentity(parameter.Type).Text + (parameter.RefKind == RefKind.None ? string.Empty : "&"), parameter.RefKind)).ToArray();
            string returnType = SourceTypeIdentity(definition.ReturnType).Text + (definition.ReturnsByRef || definition.ReturnsByRefReadonly ? "&" : string.Empty);
            string name = definition.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction
                ? definition.MetadataName + "@" + location?.SourceSpan.Start : definition.MetadataName;
            MethodIdentityTemplate identity = new(new(type.LogicalId), name, definition.Arity,
                parameters.Select(parameter => parameter.TypeIdentity).ToArray(), new(returnType));
            bool hasLog = HasAttribute(definition, "KH.LogTrackAttribute") || HasAttribute(definition.ContainingType, "KH.LogTrackAttribute");
            MethodKind kind = definition.MethodKind == MethodKind.ExplicitInterfaceImplementation ? MethodKind.Ordinary : definition.MethodKind;
            return new MethodEntry(identity.Text, identity.Text, type.AssemblyName, type.Id, type.FullName, definition.MetadataName,
                returnType, parameters, kind, sourcePath, line,
                IsReportable(definition, kind, sourcePath, context), definition.IsAbstract,
                definition.DeclaredAccessibility == Accessibility.Public, definition.IsStatic,
                definition.IsVirtual || definition.IsAbstract || definition.IsOverride, !definition.IsOverride, definition.Arity,
                definition, context.Material.AssemblyPath, 0)
            {
                DocumentationId = ReadDocumentationId(definition),
                IsFinal = definition.IsSealed,
                IsPrivate = definition.DeclaredAccessibility == Accessibility.Private,
                GenericParameters = ReadSourceGenericParameters(definition.TypeParameters),
                HasNoLogTrackExemption = !hasLog
                    && (HasAttribute(definition, "KH.NoLogTrackAttribute") || HasAttribute(definition.ContainingType, "KH.NoLogTrackAttribute")),
            };
        }

        // 判断源码函数是否属于最终标签统计范围。
        private static bool IsReportable(
            IMethodSymbol method,
            MethodKind kind,
            string? sourcePath,
            SourceCatalogContext context)
        {
            bool allowedKind = kind is MethodKind.Ordinary
                or MethodKind.PropertySet
                or MethodKind.EventAdd
                or MethodKind.EventRemove
                or MethodKind.UserDefinedOperator
                or MethodKind.Conversion;
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
                && !HasAttribute(method, CompilerGeneratedAttribute)
                && !HasAttribute(method.ContainingType, CompilerGeneratedAttribute);
        }

        // 按完整名称检查源码符号是否带有指定特性，先比短名称，不为无关特性生成显示名称。
        private static bool HasAttribute(ISymbol symbol, string fullName)
        {
            return symbol.GetAttributes().Any(attribute => attribute.AttributeClass is { } type
                && fullName.EndsWith(type.Name, StringComparison.Ordinal) && type.ToDisplayString() == fullName);
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

        // 启动和按需载入共用完整模块读取、源码回贴与资源释放。
        internal static ManagedAssemblyPart ReadManagedTypes(
            string path, SourceCatalogContext? source, int jobs)
        {
            if (source != null)
            {
                return ReadSourceTypes(source, jobs);
            }
            string fullPath = Path.GetFullPath(path);
            using Cecil.ModuleDefinition module = OpenModule(fullPath);
            // 把已打开模块转换为统一类型目录。
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

            ManagedAssemblyPart part = new(
                fullPath,
                types,
                forwarders,
                module.AssemblyReferences.Select(reference => reference.FullName).Order(StringComparer.Ordinal).ToArray());
            return part;
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
                IsCompilerGenerated = type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == CompilerGeneratedAttribute),
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
            return new TypeRelationEntry(
                ManagedNamedTypeDefinitionId(type.GetElementType()),
                identity.Text,
                ReadManagedAssemblyName(type.GetElementType(), fullName: true),
                type is Cecil.GenericInstanceType constructed ? constructed.GenericArguments.Count : 0)
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
                    // 定义与转交可以指向同一个真实类型，只有终点不同才存在歧义。
                    TypeEntry[] unique = targets.Cast<TypeEntry>()
                        .Concat(sameFileNameOverride ? Array.Empty<TypeEntry>() : node.Definitions)
                        .DistinctBy(type => type.Id, StringComparer.Ordinal).ToArray();
                    if (unique.Length != 1)
                    {
                        throw new AnalysisException($"参考类型身份对应多个真实类型：{typeId} @ {identity} => "
                            + string.Join("; ", unique.Select(type => type.Id + " @ " + type.AssemblyPath)));
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

        // 把一个 Cecil 函数定义转换为统一函数记录。
        internal static MethodEntry CreateManagedMethod(
            Cecil.MethodDefinition method,
            TypeEntry type)
        {
            MethodKind kind = method switch
            {
                { IsGetter: true } => MethodKind.PropertyGet,
                { IsSetter: true } => MethodKind.PropertySet,
                { IsAddOn: true } => MethodKind.EventAdd,
                { IsRemoveOn: true } => MethodKind.EventRemove,
                { IsConstructor: true, IsStatic: true } => MethodKind.StaticConstructor,
                { IsConstructor: true } => MethodKind.Constructor,
                { Name: "op_Implicit" or "op_Explicit" or "op_CheckedExplicit" } => MethodKind.Conversion,
                _ => method.Name.StartsWith("op_", StringComparison.Ordinal) ? MethodKind.UserDefinedOperator : MethodKind.Ordinary,
            };
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
                IsPrivate = method.IsPrivate,
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
        internal static RefKind ReadManagedRefKind(Cecil.ParameterDefinition parameter)
        {
            Cecil.TypeReference type = parameter.ParameterType;
            while (type is Cecil.IModifierType modifier)
            {
                type = modifier.ElementType;
            }

            if (type is not Cecil.ByReferenceType)
            {
                return RefKind.None;
            }

            if (parameter.IsOut)
            {
                return RefKind.Out;
            }

            return parameter.IsIn ? RefKind.In : RefKind.Ref;
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
            IReadOnlyList<string> AssemblyReferences)
        {
            internal IReadOnlyList<MethodEntry> SourceMethods { get; init; } = Array.Empty<MethodEntry>();
        }

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
        private static readonly ConditionalWeakTable<MethodIdentityTemplate, string> s_text = new();

        internal string Text
        {
            get
            {
                return s_text.GetValue(this, static identity =>
                {
                    string arity = identity.GenericArity == 0 ? string.Empty : $"``{identity.GenericArity}";
                    string parameters = string.Concat(identity.Parameters.Select(parameter =>
                        $"{parameter.Text.Length}:{parameter.Text}"));
                    return $"{identity.DeclaringType.Text}::{identity.Name.Length}:{identity.Name}{arity}"
                        + $"({identity.Parameters.Count}:{parameters})"
                        + $"->{identity.ReturnType.Text.Length}:{identity.ReturnType.Text}";
                });
            }
        }

        // 使用实际声明类型和类型实参建立构造函数身份。
        internal MethodIdentityTemplate Instantiate(
            TypeIdentityTemplate declaringType,
            IReadOnlyList<TypeIdentityTemplate> typeArguments,
            IReadOnlyList<TypeIdentityTemplate>? methodArguments = null)
        {
            return this with
            {
                DeclaringType = declaringType,
                Parameters = this.Parameters.Select(parameter =>
                    parameter.Substitute(typeArguments, methodArguments)).ToArray(),
                ReturnType = this.ReturnType.Substitute(typeArguments, methodArguments),
            };
        }
    }

    /// <summary>保存一个函数声明及其实际声明类型参数。</summary>
    internal sealed record ResolvedMethodDefinition(
        MethodEntry Method,
        IReadOnlyList<TypeIdentityTemplate> DeclaringTypeArguments)
    {
        /// <summary>当前构造调用的方法实参，与声明类型的实参分开保存。</summary>
        internal IReadOnlyList<TypeIdentityTemplate> MethodTypeArguments { get; init; } = Array.Empty<TypeIdentityTemplate>();

    }

    /// <summary>保存一个函数参数的名称、类型和传递方式。</summary>
    public sealed record ParameterEntry(
        string Name,
        string TypeId,
        RefKind RefKind)
    {
        internal TypeIdentityTemplate TypeIdentity { get; init; } =
            new TypeIdentityTemplate(TypeId);
    }

    /// <summary>保存基类或接口定义及其实际泛型参数。</summary>
    public sealed record TypeRelationEntry(
        string DefinitionId,
        string TypeId,
        string AssemblyIdentity,
        int TypeArgumentCount)
    {
        /// <summary>源码继承声明直接保留实际类型参数。</summary>
        public INamedTypeSymbol? SourceSymbol { get; init; }
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
        /// <summary>总表中的连续类型编号。</summary>
        internal int Ordinal { get; init; } = -1;

        internal string DocumentationId { get; init; } = string.Empty;

        internal bool IsValueType { get; init; }

        internal bool IsEnum { get; init; }

        internal bool IsExplicitLayout { get; init; }

        internal bool IsSealed { get; init; }

        internal bool IsCompilerGenerated { get; init; }

        internal bool IsNullableValueType { get; init; }

        /// <summary>是否允许作为运行时实现、重写和注册候选来源。</summary>
        internal bool IsCandidate { get; init; } = true;

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
        MethodKind Kind,
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
        /// <summary>总表中的连续函数编号。</summary>
        internal int Ordinal { get; init; } = -1;

        internal string DocumentationId { get; init; } = string.Empty;

        internal bool IsPrivate { get; init; }

        internal bool IsFinal { get; init; }

        internal bool HasNoLogTrackExemption { get; init; }

        /// <summary>同一迭代器唯一的延迟函数体，与创建枚举对象的入口分开。</summary>
        internal bool IsIteratorBody { get; init; }

        internal IReadOnlyList<GenericParameterRule> GenericParameters { get; init; } = Array.Empty<GenericParameterRule>();
    }

    /// <summary>
    /// 保存函数总表和后续模块直接使用的查找索引。
    /// </summary>
    public sealed class MethodCatalogResult
    {
        private readonly ConcurrentDictionary<string, bool> m_valueOnlyBodies = new(StringComparer.Ordinal);

        // 只检查真实指令是否仅装入参数并比较，不按库版本或函数名称猜测。
        internal bool HasOnlyValueInstructions(MethodEntry method)
        {
            return this.m_valueOnlyBodies.GetOrAdd(method.Id, _ =>
            {
                if (method.SourceSymbol != null)
                {
                    return false;
                }
                Cecil.ModuleDefinition module = GetManagedModule(method.AssemblyPath!);
                lock (module)
                {
                    Cecil.MethodDefinition definition = (Cecil.MethodDefinition)module.LookupToken(method.MetadataToken);
                    return definition.HasBody && definition.Body.Instructions.All(instruction => instruction.OpCode.Code is
                        Cecil.Cil.Code.Nop or Cecil.Cil.Code.Ldarg or Cecil.Cil.Code.Ldarg_S
                        or Cecil.Cil.Code.Ldarg_0 or Cecil.Cil.Code.Ldarg_1 or Cecil.Cil.Code.Ldarg_2 or Cecil.Cil.Code.Ldarg_3
                        or Cecil.Cil.Code.Ldnull or Cecil.Cil.Code.Ldc_I4_0 or Cecil.Cil.Code.Ldc_I4_1
                        or Cecil.Cil.Code.Ceq or Cecil.Cil.Code.Cgt_Un or Cecil.Cil.Code.Ret);
                }
            });
        }

        // 只读元数据标记判断值类型成员是否声明为 readonly，不读取函数体。
        internal bool IsReadOnlyStructMember(MethodEntry method)
        {
            Cecil.ModuleDefinition module = GetManagedModule(method.AssemblyPath!);
            lock (module)
            {
                Cecil.MethodDefinition definition = (Cecil.MethodDefinition)module.LookupToken(method.MetadataToken);
                return definition.CustomAttributes.Concat(definition.DeclaringType.CustomAttributes)
                    .Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");
            }
        }

        internal ConcurrentDictionary<IMethodSymbol, Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph> SourceGraphs { get; } = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<string, MethodEntry> m_methodsById = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<IMethodSymbol, MethodEntry> m_sourceMethodsBySymbol = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<string, SourceSymbolCache> m_sourceSymbols = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Type, string Interface), ILookup<string, (INamedTypeSymbol Interface, IMethodSymbol Implementation)>> m_sourceInterfaceMaps = new();
        private readonly Dictionary<string, Dictionary<IMethodSymbol, IMethodSymbol>> m_sourceOverrides = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, IReadOnlySet<string>> m_declaredOverrides = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(CSharpCompilation Compilation, SyntaxTree Tree), SemanticModel> m_sourceModels = new();
        private readonly ConcurrentDictionary<BehaviorMethodReference, ResolvedMethodDefinition> m_sourceDefinitions = new();
        private readonly ConditionalWeakTable<BehaviorMethodReference, ResolvedMethodDefinition> m_directReferences = new();
        private readonly ConditionalWeakTable<BehaviorMethodReference, ResolvedMethodDefinition> m_inheritedReferences = new();
        private readonly ConcurrentDictionary<(string Path, int Token), TypeIdentityTemplate> m_typeIdentities = new();
        private readonly ConcurrentDictionary<(string Type, System.Reflection.MemberTypes Kind), IReadOnlyList<ReflectionMember>> m_reflectionMembers = new();
        private readonly ConcurrentDictionary<(string Type, System.Reflection.MemberTypes Kind, bool IgnoreCase), ILookup<string, ReflectionMember>> m_reflectionNames = new();
        private readonly ConcurrentDictionary<string, IReadOnlyList<MethodEntry>>
            m_methodsByTypeId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Type, int Token), MethodEntry> m_methodsByToken = new();
        private readonly ConcurrentDictionary<string, ILookup<(string Name, int Arity, int Parameters, bool Static), int>> m_methodCandidates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ILookup<(string Name, int Arity, int Parameters, bool Static), IMethodSymbol>> m_sourceCandidates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Path, int Token), MethodIdentityTemplate>
            m_resolvedSignatures = new();
        private readonly ConcurrentDictionary<(string Path, int Token, bool MethodArguments), IReadOnlyList<TypeIdentityTemplate>>
            m_resolvedTypeArguments = new();
        private readonly ConcurrentDictionary<string, TypeEntry> m_primitiveTypes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Type, string Name), ILookup<string, (MethodEntry Method, ResolvedMethodDefinition Target)>>
            m_explicitImplementations = new();
        private readonly ConcurrentDictionary<(string Type, string Arguments, bool Interfaces, bool DirectOnly), IReadOnlyList<InheritedTypeRelation>>
            m_inheritedTypes = new();
        private readonly Dictionary<string, Cecil.ModuleDefinition> m_modulesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object m_moduleLock = new();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> m_lookupPathsByAssemblyName;
        private readonly IReadOnlySet<string> m_explicitRuntimeAssemblyPaths;
        private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> m_implementationPaths;
        private readonly IReadOnlyDictionary<string, string> m_assemblyAliases;
        private readonly bool m_usesUnityLegacyBinding;
        private readonly int m_jobs;
        private readonly IReadOnlyDictionary<string, AssemblyRedirectTarget> m_assemblyRedirects;
        private readonly Dictionary<string, MethodCatalog.ManagedAssemblyPart>
            m_loadedPartsByPath = new(StringComparer.OrdinalIgnoreCase);
        private bool m_dispatchAssembliesClosed;
        private readonly object m_loadedTypeLock = new();
        private readonly Dictionary<bool, int> m_dispatchGenerations = new();
        private readonly Dictionary<(TypeRelationEntry Relation, string Path), IReadOnlyList<string>> m_relationDefinitions = new();
        private int m_typeGeneration;

        private TypeEntry[] m_types;
        private readonly ConcurrentDictionary<string, TypeEntry> m_typesById;
        private readonly ConcurrentDictionary<string, TypeEntry> m_typesByManagedLocation;
        private readonly ConcurrentDictionary<string, IReadOnlyList<TypeEntry>> m_typesByLogicalId;
        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> m_assemblyCandidates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ResolvedMethodDefinition> m_methodDefinitions = new(StringComparer.Ordinal);
        private Dictionary<MethodCatalog.ForwardedTypeKey, TypeEntry> m_forwardedTargets;
        private Dictionary<MethodCatalog.ForwardedTypeKey, MethodCatalog.ForwardedTypeEntry[]> m_forwardersByAlias;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>>? m_derivedTypesByBaseId;
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>>? m_implementingTypesByInterfaceId;
        private readonly ConcurrentDictionary<string, IReadOnlyList<TypeEntry>> m_descendants = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, IReadOnlyList<TypeEntry>> m_descendantsWithInterfaces = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, MethodCatalog.SourceCatalogContext>
            m_sourceContextsByAssembly;
        private readonly IReadOnlyDictionary<string, MethodCatalog.SourceCatalogContext> m_sourceContextsByPath;

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
            IReadOnlyList<ExternalAssemblyMaterial> externalAssemblies,
            IReadOnlyDictionary<string, string> assemblyAliases,
            System.Diagnostics.Stopwatch stopwatch,
            int jobs)
        {
            this.m_usesUnityLegacyBinding = usesUnityLegacyBinding;
            this.m_jobs = jobs;
            this.m_assemblyAliases = assemblyAliases;
            this.m_explicitRuntimeAssemblyPaths = explicitRuntimeAssemblyPaths;
            this.m_implementationPaths = externalAssemblies.ToDictionary(assembly => assembly.ReferencePath,
                assembly => (IReadOnlySet<string>)assembly.ImplementationPaths.Select(CanonicalAssemblyPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
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
            this.m_sourceContextsByPath = sourceContexts.ToDictionary(
                context => Path.GetFullPath(context.Material.AssemblyPath),
                context => context,
                StringComparer.OrdinalIgnoreCase);
            foreach (MethodCatalog.ManagedAssemblyPart part in managedParts)
            {
                this.m_loadedPartsByPath.Add(part.Path, part);
            }

            this.m_types = types.Select((type, ordinal) => type with { Ordinal = ordinal }).ToArray();
            this.m_forwardedTargets = usesUnityLegacyBinding ? new() : MethodCatalog.ResolveForwardedTargets(
                types,
                managedParts.SelectMany(part => part.Forwarders).ToArray());
            this.m_forwardersByAlias = MethodCatalog.IndexForwarders(managedParts.SelectMany(part => part.Forwarders));
            ConcurrentDictionary<string, IReadOnlyList<TypeEntry>> typesByLogicalId = null!;
            ParallelOptions options = new() { MaxDegreeOfParallelism = jobs };

            Parallel.Invoke(
                options,
                () =>
                {
                    foreach (IGrouping<string, MethodEntry> group in methods.GroupBy(method => method.TypeId, StringComparer.Ordinal))
                    {
                        this.m_methodsByTypeId[group.Key] = group.OrderBy(method => method.Id, StringComparer.Ordinal).ToArray();
                    }
                },
                () => typesByLogicalId = IndexLogicalTypes(types));

            this.m_typesById = new(types.Select(type => KeyValuePair.Create(type.Id, type)), StringComparer.Ordinal);
            this.m_typesByManagedLocation = new(types.Where(type => type.AssemblyPath != null && type.MetadataToken > 0).Select(type =>
                KeyValuePair.Create(ManagedTypeLocation(type.AssemblyPath!, type.MetadataToken), type)), StringComparer.OrdinalIgnoreCase);
            this.m_typesByLogicalId = typesByLogicalId;
            this.Methods = methods.Select((method, ordinal) => method with { Ordinal = ordinal }).ToArray();
            foreach (MethodEntry method in this.Methods.Where(method => method.SourceSymbol != null))
            {
                this.m_methodsById.TryAdd(method.Id, method);
                this.m_sourceMethodsBySymbol.TryAdd(method.SourceSymbol!.OriginalDefinition, method);
            }
            Parallel.ForEach(types.Where(type => type.SourceSymbol != null && !type.IsInterface), options,
                type => ReadDeclaredOverrides(type));
            stopwatch.Stop();
            this.Elapsed = stopwatch.Elapsed;
        }

        // 生成一个物理文件内TypeDef标记的稳定查找键。
        private string ManagedTypeLocation(string path, int metadataToken)
        {
            return $"{CanonicalAssemblyPath(Path.GetFullPath(path))}|{metadataToken}";
        }

        // 已逐字节确认相同的文件共用物理身份，不改变原引用材料及程序集选择规则。
        private string CanonicalAssemblyPath(string path) => this.m_assemblyAliases.GetValueOrDefault(path, path);

        /// <summary>启动时建立的报告程序集函数；外围函数通过类型目录按需取得。</summary>
        public IReadOnlyList<MethodEntry> Methods { get; }

        /// <summary>按连续编号取得函数，避免热路径拼接长字符串。</summary>
        public MethodEntry MethodAt(int ordinal) => this.Methods[ordinal];

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

        /// <summary>按连续编号取得类型。</summary>
        public TypeEntry TypeAt(int ordinal) => this.m_types[ordinal];

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

        /// <summary>按可能的物理基类身份查找直接派生候选；执行前仍须核实唯一关系。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> DerivedTypesByBaseId
        {
            get => ReadChildIndex(false);
        }

        /// <summary>按可能的物理接口身份查找直接声明实现的候选；间接关系沿两表遍历。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> ImplementingTypesByInterfaceId
        {
            get => ReadChildIndex(true);
        }

        // 完整目录和某个实际调用的候选查询共用同一继承索引；索引只在载入新类型时整体作废，已建成的索引无锁读取。
        private IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> ReadChildIndex(bool interfaces)
        {
            if ((interfaces ? Volatile.Read(ref this.m_implementingTypesByInterfaceId) : Volatile.Read(ref this.m_derivedTypesByBaseId))
                is IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> built)
            {
                return built;
            }
            lock (this.m_loadedTypeLock)
            {
                RequireClosedDispatchIndex(interfaces);
                return interfaces
                    ? this.m_implementingTypesByInterfaceId ??= IndexMany(this.m_types.Where(type => type.IsCandidate),
                        type => type.Interfaces.SelectMany(relation => ResolveRelationDefinitionIds(relation, type)), type => type.Id)
                    : this.m_derivedTypesByBaseId ??= IndexMany(this.m_types.Where(type => type.IsCandidate && type.BaseType != null),
                        type => ResolveRelationDefinitionIds(type.BaseType!, type), type => type.Id);
            }
        }

        /// <summary>建立总表所用的时间。</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>声明字段属于战斗状态的程序集（V3 设计 2.1 节）。</summary>
        internal IReadOnlySet<string> CombatAssemblies { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>读取函数体的程序集：战斗程序集与报告程序集。</summary>
        internal IReadOnlySet<string> AnalyzedAssemblies { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        // V3 设计 3.1 节：不属于战斗或报告程序集的 DLL 函数不读函数体，改用通用库模型。
        internal bool IsLibraryMethod(MethodEntry method) => method.SourceSymbol == null && !this.AnalyzedAssemblies.Contains(method.AssemblyName);

        // 程序集既不是源码也不属于战斗或报告程序集时，其中声明的虚成员属于外部库。
        internal bool IsLibraryAssembly(string name) => !this.m_sourceContextsByAssembly.ContainsKey(name) && !this.AnalyzedAssemblies.Contains(name);

        // 读取源码类型函数，或按需读取并缓存托管类型函数。
        /// <summary>
        /// 获取指定类型直接声明的全部函数。
        /// </summary>
        public IReadOnlyList<MethodEntry> GetMethods(TypeEntry type)
        {
            return this.m_methodsByTypeId.GetOrAdd(type.Id, _ => ReadManagedMethodDeclarations(type));
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
                return this.TypesById.GetValueOrDefault(reference.DefinitionId) ?? ReadPrimitiveType(reference.Id);
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

        // 按运行时类型名查调用程序集和真实核心库，复用已有的程序集定位与转交关系。
        internal TypeEntry? ReadNamedRuntimeType(string name, MethodEntry caller)
        {
            if (!System.Reflection.Metadata.TypeName.TryParse(name, out var parsed))
            {
                throw new AnalysisException($"运行时类型名格式无效，尚未连接其异常返回：{caller.Id} => '{name}'");
            }
            if (name.IndexOfAny(new[] { '[', ']', '*', '&', '\\' }) >= 0)
            {
                throw new AnalysisException($"运行时类型名包含尚未解析的构造形式：{name}");
            }
            TypeEntry callerType = this.TypesById[caller.TypeId];
            TypeEntry coreType = ReadPrimitiveType("System.Object");
            string[] assemblies = parsed.AssemblyName != null ? new[] { parsed.AssemblyName.FullName }
                : new[] { callerType.AssemblyIdentity, coreType.AssemblyIdentity };
            foreach (string assembly in assemblies.Distinct(StringComparer.Ordinal))
            {
                string simpleName = new System.Reflection.AssemblyName(assembly).Name!;
                IReadOnlyList<TypeEntry> found = FindTypeDefinitions(MethodCatalog.NamedTypeId(simpleName, parsed.FullName), assembly, caller.AssemblyPath, loadMissing: true, required: false);
                if (found.Count > 1)
                {
                    throw new AnalysisException($"运行时类型名对应多个实际定义：{name}");
                }
                if (found.Count == 1)
                {
                    return found[0];
                }
            }
            return null;
        }

        // 依据当前模块真实核心库确认类型是否直接继承系统委托基类。
        internal bool IsDelegateType(TypeEntry type)
        {
            if (type.SourceSymbol != null)
            {
                return type.SourceSymbol.TypeKind == TypeKind.Delegate;
            }
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

        // 查声明时还原扩展函数的完整静态签名；调用实参仍由函数行为中的操作绑定保存。
        internal MethodEntry ReadSourceDeclaration(IMethodSymbol symbol, string path)
        {
            IMethodSymbol definition = (symbol.ReducedFrom ?? symbol).OriginalDefinition;
            return this.m_sourceMethodsBySymbol.TryGetValue(definition, out MethodEntry? method) ? method
                : ResolveMethodDefinition(ReadSourceMethodReference(definition, path), false).Method;
        }

        // 同一轮内共用已经精确匹配的声明，不重复查找相同引用。
        internal ResolvedMethodDefinition ResolveMethodDefinition(
            BehaviorMethodReference reference,
            bool searchInherited)
        {
            if (reference.IsIteratorBody)
            {
                ResolvedMethodDefinition original = ResolveMethodDefinition(reference with { IsIteratorBody = false }, searchInherited);
                return original with { Method = ReadIteratorBody(original.Method) };
            }
            return (searchInherited ? this.m_inheritedReferences : this.m_directReferences).GetValue(reference,
                item => ReadCachedMethodDefinition(item, searchInherited));
        }

        // 延迟函数体只登记一次，不为每个枚举对象复制分析环境。
        internal MethodEntry ReadIteratorBody(MethodEntry method)
        {
            return this.m_methodsById.GetOrAdd(method.Id + "#iterator-body", _ => method with
            {
                Id = method.Id + "#iterator-body",
                IsReportable = false,
                IsIteratorBody = true,
            });
        }

        // 不同引用对象仍按完整声明身份共用结果，同一引用不再反复拼接长键。
        private ResolvedMethodDefinition ReadCachedMethodDefinition(BehaviorMethodReference reference, bool searchInherited)
        {
            if (reference.SourceSymbol is IMethodSymbol symbol && this.m_sourceContextsByAssembly.ContainsKey(symbol.ContainingAssembly.Name))
            {
                return this.m_sourceDefinitions.GetOrAdd(reference, item => ReadMethodDefinition(item, false));
            }
            string key = $"{reference.TargetAssemblyIdentity}|{reference.ReferringAssemblyPath}|{reference.Identity.Text}"
                + $"|{reference.ReferenceMetadataToken}|{reference.KnownDeclaringTypeId}|{reference.KnownMetadataToken}|{reference.BoundDeclaringType?.Text}|{searchInherited}"
                + "|" + string.Join(',', reference.GenericArgumentTypeIds);
            return this.m_methodDefinitions.GetOrAdd(key, _ => ReadMethodDefinition(reference, searchInherited));
        }

        // 按调用点的完整类型和函数签名找到唯一声明。
        private ResolvedMethodDefinition ReadMethodDefinition(BehaviorMethodReference reference, bool searchInherited)
        {
            IReadOnlyList<TypeIdentityTemplate> methodArguments = reference.GenericArgumentTypeIds.Select(argument => new TypeIdentityTemplate(argument)).ToArray();
            if (reference.SourceSymbol is IMethodSymbol symbol && this.m_sourceContextsByAssembly.TryGetValue(
                    symbol.ContainingAssembly.Name, out MethodCatalog.SourceCatalogContext? context))
            {
                MethodEntry entry = this.m_sourceMethodsBySymbol.GetOrAdd(symbol.OriginalDefinition, definition =>
                {
                    TypeEntry type = this.TypesById[MethodCatalog.SourceNamedTypeId(definition.ContainingType)];
                    MethodEntry candidate = MethodCatalog.CreateSourceMethod(definition, type, context);
                    return this.m_methodsById.GetOrAdd(candidate.Id, candidate);
                });
                return new ResolvedMethodDefinition(entry, reference.BoundDeclaringType == null
                    ? MethodCatalog.SourceTypeArguments(symbol.ContainingType)
                        .Select(argument => ReadSourceTypeIdentity(argument, reference.ReferringAssemblyPath!)).ToArray()
                    : ReadDeclaringTypeArguments(reference.ReferringAssemblyPath!, 0, reference.BoundDeclaringType))
                { MethodTypeArguments = methodArguments };
            }
            IReadOnlyList<TypeIdentityTemplate> arguments = ReadDeclaringTypeArguments(
                reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken, reference.BoundDeclaringType);
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
                ? declaringTypes.SelectMany(type => ReadMethodByToken(type, metadataToken)
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
                return matches[0] with { MethodTypeArguments = methodArguments };
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
                         ReadDeclaringTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken, reference.BoundDeclaringType),
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
            return ReadMethodCandidates(type, reference.Identity.Name, reference.Identity.GenericArity,
                    reference.Identity.Parameters.Count, !reference.HasInstance).Where(method => MethodMatchesReference(
                    method,
                    reference,
                    signatureArguments))
                .Select(method => new ResolvedMethodDefinition(method, typeArguments))
                .ToArray();
        }

        // 类型编号已经确定时直接查元数据标记，不再逐个比较其全部函数。
        private IEnumerable<MethodEntry> ReadMethodByToken(TypeEntry type, int token)
        {
            if (type.SourceSymbol != null)
            {
                foreach (MethodEntry method in GetMethods(type).Where(method => method.MetadataToken == token))
                {
                    yield return method;
                }
                yield break;
            }
            Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
            bool matches;
            lock (module)
            {
                matches = module.LookupToken(token) is Cecil.MethodDefinition definition
                    && definition.DeclaringType.MetadataToken.ToInt32() == type.MetadataToken;
            }
            if (matches)
            {
                yield return ReadManagedEntry(type, token);
            }
        }

        // 同一源码类型与接口只建一次编译器实现表，继承、显式实现和访问器由编译器对应。
        internal ResolvedMethodDefinition ReadSourceInterfaceImplementation(TypeEntry type, ResolvedMethodDefinition declaration,
            IReadOnlyList<TypeIdentityTemplate> typeArguments)
        {
            var key = (type.Id, declaration.Method.TypeId);
            if (!this.m_sourceInterfaceMaps.TryGetValue(key, out var implementations))
            {
                List<(string Declaration, INamedTypeSymbol Interface, IMethodSymbol Implementation)> entries = new();
                foreach (INamedTypeSymbol contract in type.SourceSymbol!.AllInterfaces)
                {
                    if (!type.IsCandidate)
                    {
                        continue;
                    }
                    if (ResolveTypeDefinition(ReadSourceTypeReference(contract.OriginalDefinition, type.AssemblyPath!)).Id != declaration.Method.TypeId)
                    {
                        continue;
                    }
                    foreach (IMethodSymbol member in MethodCatalog.ReadSourceMethodSymbols(contract))
                    {
                        if (type.SourceSymbol.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                        {
                            string id = ResolveMethodDefinition(ReadSourceMethodReference(member.OriginalDefinition, type.AssemblyPath!), false).Method.Id;
                            entries.Add((id, contract, ReadSourceOverrides(type).GetValueOrDefault(implementation.OriginalDefinition) ?? implementation));
                        }
                    }
                }
                implementations = entries.ToLookup(entry => entry.Declaration, entry => (entry.Interface, entry.Implementation), StringComparer.Ordinal);
                this.m_sourceInterfaceMaps.Add(key, implementations);
            }
            foreach (var entry in implementations[declaration.Method.Id])
            {
                if (!MethodCatalog.SourceTypeArguments(entry.Interface)
                    .Select(argument => ReadSourceTypeIdentity(argument, type.AssemblyPath!).Substitute(typeArguments))
                    .SequenceEqual(declaration.DeclaringTypeArguments))
                {
                    continue;
                }
                ResolvedMethodDefinition target = ResolveMethodDefinition(ReadSourceMethodReference(entry.Implementation, type.AssemblyPath!), false);
                return new ResolvedMethodDefinition(target.Method, target.DeclaringTypeArguments.Select(argument => argument.Substitute(typeArguments)).ToArray())
                { MethodTypeArguments = declaration.MethodTypeArguments };
            }
            throw new AnalysisException($"编译器未找到接口实现：{type.FullName} => {declaration.Method.Id}");
        }

        // 按派生到基类的顺序登记虚函数槽，接口映射到基类时直接取得最终重写。
        private Dictionary<IMethodSymbol, IMethodSymbol> ReadSourceOverrides(TypeEntry type)
        {
            if (!this.m_sourceOverrides.TryGetValue(type.Id, out var overrides))
            {
                overrides = new(SymbolEqualityComparer.Default);
                for (INamedTypeSymbol? current = type.SourceSymbol; current != null; current = current.BaseType)
                {
                    foreach (IMethodSymbol method in MethodCatalog.ReadSourceMethodSymbols(current, overridesOnly: true))
                    {
                        for (IMethodSymbol? slot = method.OverriddenMethod; slot != null; slot = slot.OverriddenMethod)
                        {
                            overrides.TryAdd(slot.OriginalDefinition, method);
                        }
                    }
                }
                this.m_sourceOverrides.Add(type.Id, overrides);
            }
            return overrides;
        }

        // 只让名称和参数形态相同的重载进入完整签名比较。
        internal IEnumerable<MethodEntry> ReadMethodCandidates(TypeEntry type, string name, int arity, int parameters, bool isStatic)
        {
            if (type.SourceSymbol != null)
            {
                return this.m_sourceCandidates.GetOrAdd(type.Id, _ => MethodCatalog.ReadSourceMethodSymbols(type.SourceSymbol)
                    .Select(symbol => (symbol.PartialImplementationPart ?? symbol).OriginalDefinition)
                    .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                    .ToLookup(symbol => (symbol.MetadataName, symbol.Arity, symbol.Parameters.Length, symbol.IsStatic)))
                    [(name, arity, parameters, isStatic)].Select(symbol => ReadSourceEntry(type, symbol));
            }
            return this.m_methodCandidates.GetOrAdd(type.Id, _ =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
                lock (module)
                {
                    return ((Cecil.TypeDefinition)module.LookupToken(type.MetadataToken)).Methods
                        .ToLookup(method => (method.Name, method.GenericParameters.Count, method.Parameters.Count, method.IsStatic),
                            method => method.MetadataToken.ToInt32());
                }
            })[(name, arity, parameters, isStatic)].Select(token => ReadManagedEntry(type, token));
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

            MethodIdentityTemplate declaration = ReadMethodSignature(method);
            MethodIdentityTemplate target = reference.SourceSymbol is IMethodSymbol symbol
                ? ReadSourceMethodIdentity(symbol.OriginalDefinition, reference.ReferringAssemblyPath!)
                : ReadResolvedMethodSignature(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken);
            IReadOnlyList<TypeIdentityTemplate> declarationArguments = typeArguments ?? Array.Empty<TypeIdentityTemplate>();
            IReadOnlyList<TypeIdentityTemplate> referenceArguments = typeArguments == null
                ? Array.Empty<TypeIdentityTemplate>()
                : ReadDeclaringTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken, reference.BoundDeclaringType);

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
        internal IReadOnlyList<TypeIdentityTemplate> ReadResolvedTypeArguments(BehaviorTypeReference reference)
        {
            return reference.ReferenceMetadataToken == 0 ? reference.ArgumentIdentities
                : ReadResolvedTypeArguments(reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken);
        }

        // 还原完整类型的真实身份，数组和指针等外层形状不能被元素声明代替。
        internal TypeIdentityTemplate ReadResolvedTypeIdentity(BehaviorTypeReference reference)
        {
            if (reference.ReferenceMetadataToken == 0)
            {
                return reference.Identity;
            }
            return this.m_typeIdentities.GetOrAdd((reference.ReferringAssemblyPath!, reference.ReferenceMetadataToken), key =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(key.Path);
                Cecil.TypeReference type;
                lock (module)
                {
                    type = (Cecil.TypeReference)module.LookupToken(key.Token);
                }
                return MethodCatalog.ManagedTypeIdentity(type, definition => ReadResolvedTypeId(definition, key.Path));
            });
        }

        // 在固定调用处替换类型参数，替换后的引用不再读取原来的开放元数据标记。
        internal BehaviorTypeReference SubstituteType(BehaviorTypeReference reference,
            IReadOnlyList<TypeIdentityTemplate> typeArguments, IReadOnlyList<TypeIdentityTemplate> methodArguments)
        {
            TypeIdentityTemplate original = ReadResolvedTypeIdentity(reference);
            TypeIdentityTemplate actual = original.Substitute(typeArguments, methodArguments);
            if (actual == original)
            {
                return reference;
            }
            string element = System.Text.RegularExpressions.Regex.Replace(actual.Text, @"(\[[,]*\]|\*|&)+$", string.Empty);
            bool constructed = CallTargetResolver.TryReadConstructedType(element, out string definition, out string[] arguments);
            definition = constructed ? definition : element;
            string? known = null;
            if (!definition.StartsWith('!'))
            {
                known = definition == reference.DefinitionId ? ResolveTypeDefinition(reference).Id
                    : this.TypesById.TryGetValue(definition, out TypeEntry? type) ? type.Id : ReadPrimitiveType(definition).Id;
            }
            return new(actual, new(definition), constructed ? arguments.Select(argument => new TypeIdentityTemplate(argument)).ToArray()
                : Array.Empty<TypeIdentityTemplate>(), null, reference.ReferringAssemblyPath, known);
        }

        // 原元数据使用点保留程序集范围，不能用相同显示名称代替真实构造实参。
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

        // 字段与函数共用实际声明实参，反射构造身份不替换原始元数据标记。
        private IReadOnlyList<TypeIdentityTemplate> ReadDeclaringTypeArguments(string path, int token, TypeIdentityTemplate? boundType)
        {
            if (token == 0 && boundType == null)
            {
                return Array.Empty<TypeIdentityTemplate>();
            }
            return boundType == null ? ReadResolvedTypeArguments(path, token)
                : CallTargetResolver.TryReadConstructedType(boundType.Text, out _, out string[] arguments)
                    ? arguments.Select(argument => new TypeIdentityTemplate(argument)).ToArray() : Array.Empty<TypeIdentityTemplate>();
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
            path = CanonicalAssemblyPath(path);
            lock (this.m_loadedTypeLock)
            {
                if (this.m_loadedPartsByPath.TryGetValue(path, out MethodCatalog.ManagedAssemblyPart? part))
                {
                    return part.Types.Select(type => this.m_typesById[type.Id]).ToArray();
                }
                if (this.m_dispatchAssembliesClosed)
                {
                    throw new AnalysisException($"派发预扫描后出现新程序集，必须先闭合动态加载来源：{path}");
                }
            }
            MethodCatalog.ManagedAssemblyPart loaded = MethodCatalog.ReadManagedTypes(path, this.m_sourceContextsByPath.GetValueOrDefault(path), this.m_jobs);
            lock (this.m_loadedTypeLock)
            {
                if (this.m_loadedPartsByPath.TryGetValue(path, out MethodCatalog.ManagedAssemblyPart? part))
                {
                    return part.Types.Select(type => this.m_typesById[type.Id]).ToArray();
                }
                if (this.m_dispatchAssembliesClosed)
                {
                    throw new AnalysisException($"派发预扫描后出现新程序集，必须先闭合动态加载来源：{path}");
                }
                MergeLoadedPart(loaded);
                return loaded.Types.Select(type => this.m_typesById[type.Id]).ToArray();
            }
        }

        // 调用方已持有类型锁；新类型使继承索引和派生闭包失效。
        private void MergeLoadedPart(MethodCatalog.ManagedAssemblyPart part)
        {
            foreach (var group in part.SourceMethods.GroupBy(method => method.TypeId, StringComparer.Ordinal))
            {
                this.m_methodsByTypeId[group.Key] = group.ToArray();
            }
            foreach (TypeEntry type in part.Types)
            {
                this.m_typesById.TryAdd(type.Id, type);
                if (type.MetadataToken > 0)
                {
                    this.m_typesByManagedLocation.TryAdd(ManagedTypeLocation(part.Path, type.MetadataToken), type);
                }
            }
            foreach (var group in part.Types.GroupBy(type => type.LogicalId, StringComparer.Ordinal))
            {
                this.m_typesByLogicalId[group.Key] = (this.m_typesByLogicalId.GetValueOrDefault(group.Key) ?? Array.Empty<TypeEntry>())
                    .Concat(group).DistinctBy(type => type.Id).OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();
            }
            foreach (var group in MethodCatalog.IndexForwarders(part.Forwarders))
            {
                this.m_forwardersByAlias[group.Key] = (this.m_forwardersByAlias.GetValueOrDefault(group.Key)
                    ?? Array.Empty<MethodCatalog.ForwardedTypeEntry>()).Concat(group.Value).ToArray();
            }
            this.m_types = this.m_types.Concat(part.Types).ToArray();
            this.m_derivedTypesByBaseId = null;
            this.m_implementingTypesByInterfaceId = null;
            this.m_descendants.Clear();
            this.m_descendantsWithInterfaces.Clear();
            this.m_typeGeneration++;
            this.m_loadedPartsByPath.Add(part.Path, part);
        }

        // 合并真实定义与已经证明的转交别名，不再给每个类型复制别名列表。
        private ConcurrentDictionary<string, IReadOnlyList<TypeEntry>> IndexLogicalTypes(IReadOnlyList<TypeEntry> types)
        {
            return new(types.Select(type => (Name: type.LogicalId, Type: type))
                .Concat(this.m_forwardedTargets.Select(item => (Name: item.Key.TypeId, Type: item.Value)))
                .GroupBy(item => item.Name, StringComparer.Ordinal).Select(group => KeyValuePair.Create(group.Key,
                    (IReadOnlyList<TypeEntry>)group.Select(item => item.Type).DistinctBy(type => type.Id)
                        .OrderBy(type => type.Id, StringComparer.Ordinal).ToArray())), StringComparer.Ordinal);
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
                    return new[] { CanonicalAssemblyPath(target.Path) };
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
                    return new[] { Path.GetFullPath(sourceContext.Material.AssemblyPath) };
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
                if (this.m_usesUnityLegacyBinding)
                {
                    string[] explicitPaths = candidates.Where(this.m_explicitRuntimeAssemblyPaths.Contains).ToArray();
                    if (explicitPaths.Length != 0)
                    {
                        candidates = explicitPaths;
                    }
                }
                return (sourceContext == null ? candidates : candidates.Append(Path.GetFullPath(sourceContext.Material.AssemblyPath)))
                    .Select(CanonicalAssemblyPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            });
        }

        // 先闭合再建索引，供调用解析与函数体读取重叠。
        internal void PrepareDispatchIndexes()
        {
            RequireClosedDispatchIndex(true);
            _ = this.DerivedTypesByBaseId;
            _ = this.ImplementingTypesByInterfaceId;
        }

        // 从该类型沿子类和实现类型按广度优先展开，不含自身；同一起点只计算一次。
        internal IReadOnlyList<TypeEntry> ReadDescendants(TypeEntry type, bool includeInterfaceImplementations)
        {
            ConcurrentDictionary<string, IReadOnlyList<TypeEntry>> cache = includeInterfaceImplementations
                ? this.m_descendantsWithInterfaces : this.m_descendants;
            return cache.GetOrAdd(type.Id, _ =>
            {
                IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> derived = this.DerivedTypesByBaseId;
                IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> implementations = includeInterfaceImplementations
                    ? this.ImplementingTypesByInterfaceId : new Dictionary<string, IReadOnlyList<TypeEntry>>(StringComparer.Ordinal);
                List<TypeEntry> result = new();
                Queue<TypeEntry> pending = new(new[] { type });
                HashSet<string> visited = new(StringComparer.Ordinal) { type.Id };
                while (pending.TryDequeue(out TypeEntry? current))
                {
                    foreach (TypeEntry child in (derived.GetValueOrDefault(current.Id) ?? Array.Empty<TypeEntry>())
                        .Concat(implementations.GetValueOrDefault(current.Id) ?? Array.Empty<TypeEntry>()))
                    {
                        if (visited.Add(child.Id))
                        {
                            result.Add(child);
                            pending.Enqueue(child);
                        }
                    }
                }
                return result;
            });
        }

        // 补齐候选关系；同世代的完整接口检查也证明基类完整，实际调用仍要求唯一载体。
        internal void RequireClosedDispatchIndex(bool includeInterfaces)
        {
            CloseDispatchAssemblies();
            lock (this.m_loadedTypeLock)
            {
                while (this.m_dispatchGenerations.GetValueOrDefault(includeInterfaces, -1) != this.m_typeGeneration
                    && (includeInterfaces || this.m_dispatchGenerations.GetValueOrDefault(true, -1) != this.m_typeGeneration))
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

        // 按引用层次并行读取尚未载入的程序集，合并仍按路径顺序。
        private void CloseDispatchAssemblies()
        {
            if (Volatile.Read(ref this.m_dispatchAssembliesClosed))
            {
                return;
            }
            string[] sourcePaths;
            lock (this.m_loadedTypeLock)
            {
                if (this.m_dispatchAssembliesClosed)
                {
                    return;
                }
                sourcePaths = this.m_sourceContextsByPath.Keys.Where(path => !this.m_loadedPartsByPath.ContainsKey(path)).ToArray();
            }
            foreach (string path in sourcePaths)
            {
                LoadAssemblyTypes(path);
            }
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string[] next;
                lock (this.m_loadedTypeLock)
                {
                    HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
                    foreach (MethodCatalog.ManagedAssemblyPart part in this.m_loadedPartsByPath.Values
                        .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
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
                                    pending.Add(candidate);
                                }
                            }
                        }
                    }
                    if (pending.Count == 0)
                    {
                        this.m_dispatchAssembliesClosed = true;
                        return;
                    }
                    next = pending.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                }
                MethodCatalog.ManagedAssemblyPart[] loaded = new MethodCatalog.ManagedAssemblyPart[next.Length];
                Parallel.For(0, next.Length, new ParallelOptions { MaxDegreeOfParallelism = this.m_jobs }, index =>
                {
                    loaded[index] = MethodCatalog.ReadManagedTypes(next[index],
                        this.m_sourceContextsByPath.GetValueOrDefault(next[index]), 1);
                });
                lock (this.m_loadedTypeLock)
                {
                    foreach (MethodCatalog.ManagedAssemblyPart part in loaded)
                    {
                        if (!this.m_loadedPartsByPath.ContainsKey(part.Path))
                        {
                            MergeLoadedPart(part);
                        }
                    }
                }
            }
        }

        // 源码和真实 DLL 共用签名比较，源码不要求元数据标记。
        internal MethodIdentityTemplate ReadMethodSignature(MethodEntry method)
        {
            return method.SourceSymbol == null ? ReadResolvedMethodSignature(method.AssemblyPath!, method.MetadataToken)
                : ReadSourceMethodIdentity(method.SourceSymbol, method.AssemblyPath!);
        }

        // 源码和 DLL 都从真实函数声明取得返回类型，不依赖表达式是否附带类型信息。
        internal BehaviorTypeReference ReadMethodReturnType(MethodEntry method)
        {
            if (method.SourceSymbol != null)
            {
                if (method.IsIteratorBody)
                {
                    ITypeSymbol element = method.SourceSymbol.ReturnType is INamedTypeSymbol { TypeArguments.Length: 1 } sequence
                        ? sequence.TypeArguments[0] : this.m_sourceContextsByPath[method.AssemblyPath!].Material.Compilation.GetSpecialType(SpecialType.System_Object);
                    return ReadSourceTypeReference(element, method.AssemblyPath!);
                }
                return ReadSourceTypeReference(method.SourceSymbol.ReturnType, method.AssemblyPath!);
            }
            Cecil.ModuleDefinition module = GetManagedModule(method.AssemblyPath!);
            lock (module)
            {
                return ReadManagedTypeReference(((Cecil.MethodDefinition)module.LookupToken(method.MetadataToken)).ReturnType, method.AssemblyPath!);
            }
        }

        /// <summary>保存一份编译上下文内已经绑定的符号，不保存调用路径。</summary>
        private sealed class SourceSymbolCache
        {
            internal ConcurrentDictionary<ITypeSymbol, BehaviorTypeReference> Types { get; } = new(SymbolEqualityComparer.Default);
            internal ConcurrentDictionary<ITypeSymbol, TypeIdentityTemplate> TypeIdentities { get; } = new(SymbolEqualityComparer.Default);
            internal ConcurrentDictionary<IMethodSymbol, MethodIdentityTemplate> Signatures { get; } = new(SymbolEqualityComparer.Default);
            internal ConcurrentDictionary<IMethodSymbol, BehaviorMethodReference> Methods { get; } = new(SymbolEqualityComparer.Default);
            internal ConcurrentDictionary<ISymbol, BehaviorMemberReference> Members { get; } = new(SymbolEqualityComparer.Default);
        }

        // 每个源码文件共用编译器的绑定结果，读取函数和注册关系时不重复绑定。
        internal SemanticModel ReadSourceModel(CSharpCompilation compilation, SyntaxTree tree) =>
            this.m_sourceModels.GetOrAdd((compilation, tree), key => key.Compilation.GetSemanticModel(key.Tree));

        // 引用按所属编译上下文缓存，避免不同程序集的同名类型相互混用。
        private SourceSymbolCache ReadSourceSymbols(string path) =>
            this.m_sourceSymbols.GetOrAdd(path, _ => new SourceSymbolCache());

        // 同一编译器类型符号只建立一次引用，保留泛型实参与所属程序集。
        internal BehaviorTypeReference ReadSourceTypeReference(ITypeSymbol symbol, string path)
        {
            return ReadSourceSymbols(path).Types.GetOrAdd(symbol, type => CreateSourceTypeReference(type, path));
        }

        // 源码类型引用保留程序集身份和构造参数，外部类型仍按真实材料解析。
        private BehaviorTypeReference CreateSourceTypeReference(ITypeSymbol symbol, string path)
        {
            if (symbol is INamedTypeSymbol { IsUnboundGenericType: true } generic)
            {
                symbol = generic.OriginalDefinition;
            }
            ITypeSymbol element = symbol;
            while (element is IArrayTypeSymbol or IPointerTypeSymbol)
            {
                element = element is IArrayTypeSymbol array ? array.ElementType : ((IPointerTypeSymbol)element).PointedAtType;
            }
            string definition = element is INamedTypeSymbol named ? MethodCatalog.SourceNamedTypeId(named) : MethodCatalog.SourceTypeIdentity(element).Text;
            string? known = this.TypesById.ContainsKey(definition) ? definition : null;
            if (known == null && element.ContainingAssembly != null
                && this.m_sourceContextsByPath[path].Material.Compilation.GetMetadataReference(element.ContainingAssembly)
                    is PortableExecutableReference { FilePath: not null } reference
                && this.m_implementationPaths.TryGetValue(reference.FilePath, out IReadOnlySet<string>? paths))
            {
                TypeEntry[] matches = (this.m_typesByLogicalId.GetValueOrDefault(definition) ?? Array.Empty<TypeEntry>())
                    .Where(type => type.AssemblyPath != null && paths.Contains(type.AssemblyPath)).ToArray();
                known = matches.Length switch
                {
                    0 => null,
                    1 => matches[0].Id,
                    _ => throw new AnalysisException($"源码引用对应多个真实类型：{reference.FilePath} => {definition}"),
                };
            }
            return new BehaviorTypeReference(MethodCatalog.SourceTypeIdentity(symbol), new(definition),
                symbol is INamedTypeSymbol constructed ? MethodCatalog.SourceTypeArguments(constructed)
                    .Select(argument => ReadSourceTypeIdentity(argument, path)).ToArray() : Array.Empty<TypeIdentityTemplate>(),
                element.ContainingAssembly?.Identity.GetDisplayName(), path,
                known);
        }

        // 用真实程序集中的类型身份比较源码和 DLL 签名。
        internal TypeIdentityTemplate ReadSourceTypeIdentity(ITypeSymbol symbol, string path)
        {
            return ReadSourceSymbols(path).TypeIdentities.GetOrAdd(symbol, type => MethodCatalog.SourceTypeIdentity(type,
                named => ResolveTypeDefinition(ReadSourceTypeReference(named.OriginalDefinition, path)).Id));
        }

        // 函数符号已经包含重载选择和参数位置，不重新按文本名称猜测。
        internal MethodIdentityTemplate ReadSourceMethodIdentity(IMethodSymbol symbol, string path)
        {
            return ReadSourceSymbols(path).Signatures.GetOrAdd(symbol, method => CreateSourceMethodIdentity(method, path));
        }

        // 首次遇到函数时记录编译器已确定的参数、返回类型和泛型位置。
        private MethodIdentityTemplate CreateSourceMethodIdentity(IMethodSymbol symbol, string path)
        {
            string name = symbol.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction
                ? symbol.MetadataName + "@" + symbol.Locations.FirstOrDefault()?.SourceSpan.Start : symbol.MetadataName;
            return new MethodIdentityTemplate(new(MethodCatalog.SourceNamedTypeId(symbol.ContainingType)), name, symbol.Arity,
                symbol.Parameters.Select(parameter => new TypeIdentityTemplate(ReadSourceTypeIdentity(parameter.Type, path).Text
                    + (parameter.RefKind == RefKind.None ? string.Empty : "&"))).ToArray(),
                new(ReadSourceTypeIdentity(symbol.ReturnType, path).Text + (symbol.ReturnsByRef || symbol.ReturnsByRefReadonly ? "&" : string.Empty)));
        }

        // 直接保存编译器绑定后的函数与类型实参。
        internal BehaviorMethodReference ReadSourceMethodReference(IMethodSymbol symbol, string path)
        {
            return ReadSourceSymbols(path).Methods.GetOrAdd(symbol, method => CreateSourceMethodReference(method, path));
        }

        // 每个构造后的函数符号保留自己的类型实参，不把不同泛型调用混为一项。
        private BehaviorMethodReference CreateSourceMethodReference(IMethodSymbol symbol, string path)
        {
            IMethodSymbol genericOwner = symbol;
            while (!genericOwner.IsGenericMethod && genericOwner.ContainingSymbol is IMethodSymbol enclosing)
            {
                genericOwner = enclosing;
            }
            return new BehaviorMethodReference(ReadSourceMethodIdentity(symbol, path), MethodCatalog.SourceNamedTypeId(symbol.ContainingType),
                genericOwner.IsGenericMethod ? genericOwner.TypeArguments.Select(argument => ReadSourceTypeIdentity(argument, path).Text).ToArray()
                    : Array.Empty<string>(), !symbol.IsStatic, symbol.ContainingAssembly.Identity.GetDisplayName(), path)
            {
                SourceSymbol = symbol,
                KnownDeclaringTypeId = ReadSourceTypeReference(symbol.ContainingType, path).KnownTypeId,
                BoundDeclaringType = ReadSourceTypeIdentity(symbol.ContainingType, path),
            };
        }

        // 字段与自动属性保留所属声明，统一参与对象成员关系。
        internal BehaviorMemberReference ReadSourceMemberReference(ISymbol symbol, string path)
        {
            return ReadSourceSymbols(path).Members.GetOrAdd(symbol, member => CreateSourceMemberReference(member, path));
        }

        // 首次遇到成员时记录其声明和存储类型。
        private BehaviorMemberReference CreateSourceMemberReference(ISymbol symbol, string path)
        {
            ITypeSymbol type = symbol switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                IEventSymbol eventSymbol => eventSymbol.Type,
                _ => throw new AnalysisException($"没有存储类型：{symbol}"),
            };
            return new BehaviorMemberReference(MethodCatalog.SourceTypeIdentity(symbol.ContainingType),
                MethodCatalog.SourceNamedTypeId(symbol.ContainingType), symbol.MetadataName)
            {
                SourceSymbol = symbol,
                KnownDeclaringTypeId = ReadSourceTypeReference(symbol.ContainingType, path).KnownTypeId,
                IsReferenceStorage = type.IsReferenceType ? true : type.IsValueType ? false : null,
                IsStatic = symbol.IsStatic,
                TargetAssemblyIdentity = symbol.ContainingAssembly.Identity.GetDisplayName(),
                ReferringAssemblyPath = path,
            };
        }

        // 使用 Cecil 读取托管调用点的函数引用。
        internal BehaviorMethodReference ReadMethodReference(MethodEntry method, IReadOnlyList<TypeIdentityTemplate>? arguments = null,
            IReadOnlyList<TypeIdentityTemplate>? methodArguments = null)
        {
            BehaviorMethodReference reference = method.SourceSymbol != null
                ? ReadSourceMethodReference(method.SourceSymbol, method.AssemblyPath!)
                : ReadManagedMethodReference((Cecil.MethodDefinition)GetManagedModule(method.AssemblyPath!)
                    .LookupToken(method.MetadataToken), method.AssemblyPath!);
            if (method.IsIteratorBody)
            {
                reference = reference with { IsIteratorBody = true };
            }
            if (arguments == null && methodArguments == null)
            {
                return reference;
            }
            arguments ??= Array.Empty<TypeIdentityTemplate>();
            TypeIdentityTemplate type = CallTargetResolver.ConstructTypeIdentity(this.TypesById[method.TypeId], arguments);
            return reference with
            {
                Identity = ReadMethodSignature(method).Instantiate(type, arguments, methodArguments),
                BoundDeclaringType = type,
                GenericArgumentTypeIds = methodArguments?.Select(argument => argument.Text).ToArray() ?? reference.GenericArgumentTypeIds,
            };
        }

        // 初始化事实仍使用同一函数体读取流程，不另写一套指令解释器。
        internal MethodBehavior ReadMethodBehavior(MethodEntry method)
        {
            if (method.SourceSymbol != null)
            {
                return BehaviorReader.ReadSourceBehavior(this, method, this.m_sourceContextsByPath[method.AssemblyPath!].Material.Compilation);
            }
            Cecil.ModuleDefinition module = GetManagedModule(method.AssemblyPath!);
            lock (module)
            {
                if (method.AssemblyName != module.Assembly.Name.Name)
                {
                    throw new AnalysisException($"托管函数与真实程序集不一致：{method.AssemblyPath}");
                }
                return BehaviorReader.ReadManagedBehavior(module, this, method);
            }
        }

        // 反射查询源码与 DLL 的真实成员表，保存访问函数和存储声明。
        internal IReadOnlyList<ReflectionMember> ReadReflectionMembers(TypeEntry type, System.Reflection.MemberTypes kind,
            IReadOnlyList<string>? names = null, bool ignoreCase = false)
        {
            IReadOnlyList<ReflectionMember> members = this.m_reflectionMembers.GetOrAdd((type.Id, kind), _ => BuildReflectionMembers(type, kind));
            if (names == null)
            {
                return members;
            }
            ILookup<string, ReflectionMember> index = this.m_reflectionNames.GetOrAdd((type.Id, kind, ignoreCase),
                _ => members.ToLookup(member => member.Name, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
            return names.SelectMany(name => index[name]).Distinct().ToArray();
        }

        // 每个类型只建立一份反射成员表，后续查询复用。
        private IReadOnlyList<ReflectionMember> BuildReflectionMembers(TypeEntry type, System.Reflection.MemberTypes kind)
        {
            List<ReflectionMember> members = new();
            if ((kind & (System.Reflection.MemberTypes.Method | System.Reflection.MemberTypes.Constructor)) != 0)
            {
                members.AddRange(GetMethods(type).Select(method => new ReflectionMember(method.Name, method.IsStatic,
                    method.IsPublic, null, null, ReadMethodReference(method))
                { IsPrivate = method.IsPrivate }));
            }
            if (type.SourceSymbol != null)
            {
                foreach (ISymbol symbol in type.SourceSymbol.GetMembers())
                {
                    if (symbol is IFieldSymbol field && (kind & System.Reflection.MemberTypes.Field) != 0)
                    {
                        members.Add(new ReflectionMember(field.Name, field.IsStatic, field.DeclaredAccessibility == Accessibility.Public,
                            ReadSourceMemberReference(field, type.AssemblyPath!), null, null)
                        { IsPrivate = field.DeclaredAccessibility == Accessibility.Private });
                    }
                    else if (symbol is IPropertySymbol property && (kind & System.Reflection.MemberTypes.Property) != 0)
                    {
                        members.Add(new ReflectionMember(property.Name, property.IsStatic, property.DeclaredAccessibility == Accessibility.Public, null,
                            new BehaviorPropertyReference(property.GetMethod == null ? null : ReadSourceMethodReference(property.GetMethod, type.AssemblyPath!),
                                property.SetMethod == null ? null : ReadSourceMethodReference(property.SetMethod, type.AssemblyPath!)), null)
                        { IsPrivate = property.DeclaredAccessibility == Accessibility.Private });
                    }
                }
            }
            else
            {
                if ((kind & System.Reflection.MemberTypes.Field) != 0)
                {
                    members.AddRange(GetFields(type).Select(field => new ReflectionMember(field.Name, field.IsStatic, field.IsPublic,
                        ReadManagedFieldReference(field, type.AssemblyPath!), null, null)
                    { IsPrivate = field.IsPrivate }));
                }
                if ((kind & System.Reflection.MemberTypes.Property) != 0)
                {
                    members.AddRange(GetProperties(type).Select(property => new ReflectionMember(property.Name,
                        (property.GetMethod ?? property.SetMethod).IsStatic, property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true, null,
                        new BehaviorPropertyReference(property.GetMethod == null ? null : ReadManagedMethodReference(property.GetMethod, type.AssemblyPath!),
                            property.SetMethod == null ? null : ReadManagedMethodReference(property.SetMethod, type.AssemblyPath!)), null)
                    { IsPrivate = (property.GetMethod?.IsPrivate ?? true) && (property.SetMethod?.IsPrivate ?? true) }));
                }
            }
            return members;
        }

        internal sealed record ReflectionMember(string Name, bool IsStatic, bool IsPublic, BehaviorMemberReference? Field,
            BehaviorPropertyReference? Property, BehaviorMethodReference? Method)
        {
            internal bool IsPrivate { get; init; }
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
                    MethodCatalog.ManagedTypeIdentity(argument, type => ReadResolvedTypeId(type, referringAssemblyPath))).ToArray()
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
            TypeIdentityTemplate declaringType = MethodCatalog.ManagedTypeIdentity(field.DeclaringType);
            return new BehaviorMemberReference(
                declaringType,
                MethodCatalog.ManagedNamedTypeDefinitionId(field.DeclaringType.GetElementType()),
                field.Name)
            {
                IsReferenceStorage = field.FieldType is Cecil.GenericParameter parameter
                    ? parameter.HasReferenceTypeConstraint ? true : parameter.HasNotNullableValueTypeConstraint ? false : null
                    : !field.FieldType.IsValueType,
                IsStatic = (field as Cecil.FieldDefinition)?.IsStatic,
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
                ReadKnownTypeId(type, referringAssemblyPath))
            { ReferenceMetadataToken = type.MetadataToken.RID == 0 ? 0 : type.MetadataToken.ToInt32() };
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

        // 只为已命中的源码声明建立完整资料，其他重载保留编译器符号即可。
        private MethodEntry ReadSourceEntry(TypeEntry type, IMethodSymbol symbol)
        {
            return this.m_sourceMethodsBySymbol.GetOrAdd(symbol, definition =>
            {
                MethodEntry method = MethodCatalog.CreateSourceMethod(definition, type, this.m_sourceContextsByPath[type.AssemblyPath!]);
                return this.m_methodsById.GetOrAdd(method.Id, method);
            });
        }

        // 元数据编号命中后才创建完整函数资料，普通调用和接口匹配共用一份记录。
        private MethodEntry ReadManagedEntry(TypeEntry type, int token)
        {
            return this.m_methodsByToken.GetOrAdd((type.Id, token), _ =>
            {
                Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
                lock (module)
                {
                    MethodEntry method = MethodCatalog.CreateManagedMethod((Cecil.MethodDefinition)module.LookupToken(token), type);
                    return this.m_methodsById.GetOrAdd(method.Id, method);
                }
            });
        }

        // 读取一个托管类型直接声明的函数事实。
        private IReadOnlyList<MethodEntry> ReadManagedMethodDeclarations(TypeEntry type)
        {
            if (type.SourceSymbol != null)
            {
                return MethodCatalog.ReadSourceMethodSymbols(type.SourceSymbol)
                    .Select(symbol => (symbol.PartialImplementationPart ?? symbol).OriginalDefinition)
                    .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                    .Select(symbol => ReadSourceEntry(type, symbol)).ToArray();
            }
            Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
            lock (module)
            {
                return ((Cecil.TypeDefinition)module.LookupToken(type.MetadataToken)).Methods
                    .Select(method => ReadManagedEntry(type, method.MetadataToken.ToInt32())).ToArray();
            }
        }

        // 每个类型只收集一次重写名称，未重写的虚函数直接继承基类匹配结果。
        internal bool HasDeclaredOverride(TypeEntry type, string name) => ReadDeclaredOverrides(type).Contains(name);

        // 源码重写在建表阶段并行准备，外部 DLL 仍按实际需求读取。
        private IReadOnlySet<string> ReadDeclaredOverrides(TypeEntry type)
        {
            return this.m_declaredOverrides.GetOrAdd(type.Id, _ =>
            {
                if (type.SourceSymbol != null)
                {
                    return MethodCatalog.ReadSourceMethodSymbols(type.SourceSymbol, overridesOnly: true).Where(method => method.IsOverride)
                        .Select(method => method.MetadataName).ToHashSet(StringComparer.Ordinal);
                }
                Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
                lock (module)
                {
                    var methods = ((Cecil.TypeDefinition)module.LookupToken(type.MetadataToken)).Methods;
                    return methods.Where(method => method.IsVirtual && !method.IsNewSlot).Select(method => method.Name)
                        .Concat(methods.SelectMany(method => method.Overrides).Select(target => target.Name))
                        .ToHashSet(StringComparer.Ordinal);
                }
            });
        }

        // 按被实现的方法定位显式实现，避免每个接口槽重新扫描整个类的方法。
        internal IEnumerable<(MethodEntry Method, ResolvedMethodDefinition Target)> ReadExplicitImplementations(TypeEntry type, MethodEntry declaration)
        {
            return this.m_explicitImplementations.GetOrAdd((type.Id, declaration.Name), _ =>
            {
                List<(MethodEntry Method, ResolvedMethodDefinition Target)> result = new();
                if (type.SourceSymbol != null)
                {
                    foreach (IMethodSymbol symbol in MethodCatalog.ReadSourceMethodSymbols(type.SourceSymbol)
                        .Select(symbol => (symbol.PartialImplementationPart ?? symbol).OriginalDefinition)
                        .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
                    {
                        IEnumerable<IMethodSymbol> targets = symbol.ExplicitInterfaceImplementations;
                        if (symbol.OverriddenMethod is IMethodSymbol parent)
                        {
                            targets = targets.Append(parent);
                        }
                        foreach (IMethodSymbol target in targets.Where(target => target.MetadataName == declaration.Name))
                        {
                            result.Add((ReadSourceEntry(type, symbol), ResolveMethodDefinition(ReadSourceMethodReference(target, type.AssemblyPath!), false)));
                        }
                    }
                    return result.ToLookup(item => item.Target.Method.Id, StringComparer.Ordinal);
                }
                Cecil.ModuleDefinition module = GetManagedModule(type.AssemblyPath!);
                Cecil.MethodDefinition[] definitions;
                lock (module)
                {
                    definitions = ((Cecil.TypeDefinition)module.LookupToken(type.MetadataToken)).Methods
                        .Where(method => method.HasOverrides).ToArray();
                }
                foreach (Cecil.MethodDefinition definition in definitions)
                {
                    Cecil.MethodReference[] overrides;
                    lock (module)
                    {
                        overrides = definition.Overrides.Where(target => target.Name == declaration.Name).ToArray();
                    }

                    if (overrides.Length != 0)
                    {
                        MethodEntry method = ReadManagedEntry(type, definition.MetadataToken.ToInt32());
                        result.AddRange(overrides.Select(target => (method, ResolveMethodDefinition(
                            ReadManagedMethodReference(target, method.AssemblyPath!), searchInherited: false))));
                    }
                }

                return result.ToLookup(item => item.Target.Method.Id, StringComparer.Ordinal);
            })[declaration.Id];
        }

        // 按需列出直接继承或完整继承，保留构造泛型实参。
        internal IReadOnlyList<InheritedTypeRelation> ReadInheritedTypes(
            TypeEntry type,
            IReadOnlyList<TypeIdentityTemplate>? typeArguments = null,
            bool includeInterfaces = true,
            bool directOnly = false)
        {
            bool reuse = this.m_dispatchAssembliesClosed;
            var query = (type.Id, string.Concat((typeArguments ?? Array.Empty<TypeIdentityTemplate>())
                .Select(argument => $"{argument.Text.Length}:{argument.Text}")), includeInterfaces, directOnly);
            if (reuse && this.m_inheritedTypes.TryGetValue(query, out IReadOnlyList<InheritedTypeRelation>? known))
            {
                return known;
            }
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, TypeIdentityTemplate[] Arguments, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<InheritedTypeRelation> result = new();

            PushRelations(pending, type, 1, canImplementInterface: true, typeArguments, includeInterfaces);
            while (pending.Count > 0)
            {
                (TypeRelationEntry relation, TypeEntry owner, TypeIdentityTemplate[] arguments, bool isInterface,
                    bool canImplementInterface, int depth) = pending.Pop();
                IReadOnlyList<TypeEntry> definitions = FindTypeDefinitions(relation, owner, loadMissing: true);
                if (definitions.Count == 0)
                {
                    throw new AnalysisException($"类型层级无法闭合：{owner.Id} => {relation.DefinitionId}，来源：{owner.AssemblyPath}");
                }
                foreach (TypeEntry definition in definitions)
                {
                    string typeId = arguments.Length == 0 ? relation.TypeId
                        : relation.DefinitionId + "<" + string.Join(",", arguments.Select(argument => argument.Text)) + ">";
                    string key = $"{definition.Id}|{typeId}|{isInterface}";
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    if (definition.IsCandidate)
                    {
                        result.Add(new InheritedTypeRelation(
                        definition,
                        arguments,
                        isInterface,
                        canImplementInterface,
                        depth));
                    }
                    if (!directOnly)
                    {
                        PushRelations(pending, definition, depth + 1, canImplementInterface, arguments, includeInterfaces);
                    }
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
            Stack<(TypeRelationEntry Relation, TypeEntry Owner, TypeIdentityTemplate[] Arguments, bool IsInterface,
                bool CanImplementInterface, int Depth)> pending,
            TypeEntry type,
            int depth,
            bool canImplementInterface,
            IReadOnlyList<TypeIdentityTemplate>? ownerArguments,
            bool includeInterfaces)
        {
            // 实参在原来的入栈位置解析，保留载入和失败顺序，不再转成字符串重新解析。
            void Push(TypeRelationEntry relation, bool isInterface, bool canImplement)
            {
                TypeIdentityTemplate[] arguments = relation.TypeArgumentCount == 0 ? Array.Empty<TypeIdentityTemplate>()
                    : (relation.SourceSymbol != null ? MethodCatalog.SourceTypeArguments(relation.SourceSymbol)
                        .Select(argument => ReadSourceTypeIdentity(argument, type.AssemblyPath!))
                        : ReadResolvedTypeArguments(type.AssemblyPath!, relation.ReferenceMetadataToken))
                        .Select(argument => argument.Substitute(ownerArguments ?? Array.Empty<TypeIdentityTemplate>())).ToArray();
                pending.Push((relation, type, arguments, isInterface, canImplement, depth));
            }
            if (type.BaseType != null)
            {
                Push(type.BaseType, false, false);
            }
            foreach (TypeRelationEntry relation in includeInterfaces ? type.Interfaces.Reverse() : Array.Empty<TypeRelationEntry>())
            {
                Push(relation, true, canImplementInterface);
            }
        }

        // 按继承声明的完整程序集身份定位类型定义。
        private IReadOnlyList<TypeEntry> FindTypeDefinitions(
            TypeRelationEntry relation,
            TypeEntry owner,
            bool loadMissing = false)
        {
            // 源码继承关系使用编译器已绑定的物理引用，不能重新混入其他上下文的同名 DLL。
            if (relation.SourceSymbol != null && owner.AssemblyPath is string sourcePath
                && ReadSourceTypeReference(relation.SourceSymbol.OriginalDefinition, sourcePath) is { KnownTypeId: not null } reference)
            {
                return new[] { ResolveTypeDefinition(reference) };
            }
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
            string definitionId, string assemblyIdentity, string? referringAssemblyPath, bool loadMissing, bool required = true)
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
                    (typeId, fullName, path) => ReadForwardedTypeNode(typeId, fullName, path, loadMissing, required: required),
                    resolved,
                    new HashSet<MethodCatalog.ForwardedTypeKey>(), required && loadMissing && referringAssemblyPath != null);
                foreach (var item in resolved.Where(item => item.Value != null))
                {
                    this.m_forwardedTargets[item.Key] = item.Value!;
                    this.m_typesByLogicalId.AddOrUpdate(item.Key.TypeId, new[] { item.Value! }, (_, known) =>
                        known.Any(type => type.Id == item.Value!.Id) ? known : known.Append(item.Value!).OrderBy(type => type.Id, StringComparer.Ordinal).ToArray());
                }
                return result == null ? Array.Empty<TypeEntry>() : new[] { result };
            }
        }

        // 必要时加载当前节点所在程序集，终点与循环仍由共同求解过程判断。
        private MethodCatalog.ForwardedTypeNode ReadForwardedTypeNode(
            string definitionId, string assemblyIdentity, string? referringPath, bool loadMissing, bool potential = false, bool required = true)
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
                string actual = ReadAssemblyIdentity(path).Name!.ToUpperInvariant();
                return $"A{actual.Length}:{actual}" + definitionId[$"A{requested.Length}:{requested}".Length..];
            }
            TypeEntry[] definitions = candidates.SelectMany(path =>
                (this.m_typesByLogicalId.GetValueOrDefault(ReadActualTypeId(path)) ?? Array.Empty<TypeEntry>())
                    .Where(type => string.Equals(type.AssemblyPath, path, StringComparison.OrdinalIgnoreCase))).ToArray();
            MethodCatalog.ForwardedTypeEntry[] forwarders = candidates.Count == 0
                ? this.m_forwardersByAlias.GetValueOrDefault(key) ?? Array.Empty<MethodCatalog.ForwardedTypeEntry>()
                : candidates.SelectMany(path => this.m_forwardersByAlias.GetValueOrDefault(MethodCatalog.CreateForwardedTypeKey(
                        ReadActualTypeId(path), ReadAssemblyIdentity(path).FullName!)) ?? Array.Empty<MethodCatalog.ForwardedTypeEntry>())
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
                if (required)
                {
                    throw new AnalysisException($"实际载体没有请求的类型：{definitionId}；{referringPath} => {path}");
                }
            }

            return new MethodCatalog.ForwardedTypeNode(definitions, forwarders);
        }

        // 从源码上下文或真实 DLL 读取程序集身份，不为查询身份生成程序集。
        private System.Reflection.AssemblyName ReadAssemblyIdentity(string path)
        {
            return this.m_sourceContextsByPath.TryGetValue(path, out MethodCatalog.SourceCatalogContext? source)
                ? new System.Reflection.AssemblyName(source.Material.Compilation.Assembly.Identity.ToString())
                : new System.Reflection.AssemblyName(GetManagedModule(path).Assembly.Name.FullName);
        }

        // 为一个真实文件只建立一份 Cecil 模块并协调并行首次读取。
        private Cecil.ModuleDefinition GetManagedModule(string path)
        {
            path = CanonicalAssemblyPath(path);
            lock (this.m_moduleLock)
            {
                if (!this.m_modulesByPath.TryGetValue(path, out Cecil.ModuleDefinition? module))
                {
                    if (this.m_sourceContextsByPath.ContainsKey(path))
                    {
                        throw new AnalysisException($"源码应通过符号读取，不能请求托管元数据：{path}");
                    }
                    module = MethodCatalog.OpenModule(path);
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
            var key = (relation, owner.AssemblyPath!);
            if (this.m_dispatchAssembliesClosed && this.m_relationDefinitions.TryGetValue(key, out IReadOnlyList<string>? known))
            {
                return known;
            }
            IReadOnlyList<string> definitions;
            if (relation.KnownMetadataToken != null)
            {
                definitions = FindTypeDefinitions(relation, owner).Select(type => type.Id).ToArray();
            }
            else
            {
                HashSet<string> result = new(StringComparer.Ordinal);
                ReadPotentialRelation(relation.DefinitionId, relation.AssemblyIdentity, owner.AssemblyPath!,
                    new HashSet<MethodCatalog.ForwardedTypeKey>(), result);
                definitions = result.Order(StringComparer.Ordinal).ToArray();
            }
            if (this.m_dispatchAssembliesClosed)
            {
                this.m_relationDefinitions.Add(key, definitions);
            }
            return definitions;
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
