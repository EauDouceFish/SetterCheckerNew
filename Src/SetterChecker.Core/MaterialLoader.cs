using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace SetterChecker.Core
{
    /// <summary>
    /// 读取 khengine 当前 Unity 编译所使用的全部分析材料。
    /// </summary>
    public sealed class MaterialLoader
    {
        // 按当前 Unity 编译参数建立后续模块唯一使用的材料集合。
        /// <summary>
        /// 读取源码程序集、外部编译文件和分析器文件。
        /// </summary>
        public async Task<MaterialSet> LoadAsync(
            MaterialRequest request,
            CancellationToken cancellationToken = default)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            ValidateRequest(request);
            string assemblyDefinitionPath = Path.GetFullPath(request.AssemblyDefinitionPath);
            string projectRoot = FindProjectRoot(assemblyDefinitionPath);
            string reportRoot = FindReportRoot(assemblyDefinitionPath, projectRoot);
            string assemblyName = ReadAssemblyName(assemblyDefinitionPath);
            string responsePath = FindRootResponse(projectRoot, assemblyName);
            IReadOnlyList<CompilerResponse> responses = ReadResponseClosure(
                projectRoot,
                assemblyName,
                responsePath);
            IReadOnlyDictionary<string, SyntaxTree> trees = await ParseSourcesAsync(
                responses,
                request.Jobs,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, ISourceGenerator[]> generatorsByAnalyzerSet =
                LoadGeneratorSets(responses);
            HashSet<string> sourceReferencePaths = responses.SelectMany(response => response.SourceReferences)
                .Select(reference => reference.ReferencePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<CompilerReference, PortableExecutableReference> metadataReferences =
                CreateMetadataReferences(responses, sourceReferencePaths, request.Jobs, cancellationToken);
            bool enableCompilerParallel = responses.Count == 1 && request.Jobs > 1;
            Dictionary<string, SourceAssemblyMaterial> builtAssemblies = new(StringComparer.Ordinal);
            List<CompilerResponse> remaining = responses.ToList();
            Stopwatch compilationWatch = Stopwatch.StartNew();
            while (remaining.Count != 0)
            {
                CompilerResponse[] ready = remaining.Where(response => response.SourceReferences
                    .All(reference => builtAssemblies.ContainsKey(reference.AssemblyName))).ToArray();
                if (ready.Length == 0)
                {
                    throw new AnalysisException("源码程序集存在循环依赖，不能使用旧参考拆开编译："
                        + string.Join("; ", remaining.Select(response => response.AssemblyName)));
                }

                SourceAssemblyMaterial[] layer = new SourceAssemblyMaterial[ready.Length];
                await Parallel.ForEachAsync(Enumerable.Range(0, ready.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = request.Jobs, CancellationToken = cancellationToken },
                    (index, token) =>
                    {
                        CompilerResponse response = ready[index];
                        SourceAssemblyMaterial source = BuildSourceAssembly(response, reportRoot, enableCompilerParallel,
                            trees, metadataReferences, generatorsByAnalyzerSet[AnalyzerSetKey(response.AnalyzerPaths)], token);
                        using MemoryStream image = new();
                        EmitResult result = source.Compilation.Emit(image, options: response.EmitOptions, cancellationToken: token);
                        if (!result.Success)
                        {
                            throw new AnalysisException($"当前源码编译失败：{source.Name} => "
                                + string.Join("; ", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
                        }

                        layer[index] = source with { AssemblyImage = image.ToArray() };
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
                foreach (SourceAssemblyMaterial source in layer)
                {
                    builtAssemblies.Add(source.Name, source);
                    HashSet<string> referencePaths = responses.SelectMany(response => response.SourceReferences)
                        .Where(reference => reference.AssemblyName == source.Name).Select(reference => reference.ReferencePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (CompilerReference reference in responses.SelectMany(response => response.References)
                                 .Where(reference => referencePaths.Contains(reference.Path)).Distinct())
                    {
                        metadataReferences.Add(reference, MetadataReference.CreateFromImage(source.AssemblyImage,
                            reference.Properties, filePath: source.AssemblyPath));
                    }
                }

                remaining.RemoveAll(response => builtAssemblies.ContainsKey(response.AssemblyName));
            }
            compilationWatch.Stop();
            SourceAssemblyMaterial[] sourceAssemblies = builtAssemblies.Values
                .OrderBy(assembly => assembly.Name, StringComparer.Ordinal).ToArray();
            string[] externalAssemblyPaths = responses
                .SelectMany(response => response.References)
                .Select(reference => reference.Path)
                .Where(path => !sourceReferencePaths.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ExternalMaterialResolution external = await ResolveExternalAssembliesAsync(
                projectRoot,
                externalAssemblyPaths,
                responses.Single(response => string.Equals(
                    response.AssemblyName,
                    assemblyName,
                    StringComparison.Ordinal)),
                request.Jobs,
                cancellationToken).ConfigureAwait(false);
            string[] analyzerPaths = responses
                .SelectMany(response => response.AnalyzerPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            stopwatch.Stop();

            return new MaterialSet(
                projectRoot,
                reportRoot,
                sourceAssemblies,
                external.Assemblies,
                external.LookupPaths,
                analyzerPaths,
                stopwatch.Elapsed)
            {
                CompilationElapsed = compilationWatch.Elapsed,
                UnityRuntimeFacadePaths = external.UnityRuntimeFacadePaths,
                AssemblyRedirects = external.AssemblyRedirects,
            };
        }

        // 从输入程序集定义向上找到包含 package.json 的目标包目录。
        private static string FindReportRoot(string assemblyDefinitionPath, string projectRoot)
        {
            DirectoryInfo? directory = new FileInfo(assemblyDefinitionPath).Directory;

            while (directory != null && IsUnderDirectory(directory.FullName, projectRoot))
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new AnalysisException($"程序集定义不属于一个有 package.json 的 Unity 包：{assemblyDefinitionPath}");
        }

        // 把只有声明的外部参考文件连接到 Unity 当前真实输出文件。
        private static async Task<ExternalMaterialResolution> ResolveExternalAssembliesAsync(
            string projectRoot,
            IReadOnlyList<string> referencePaths,
            CompilerResponse rootResponse,
            int jobs,
            CancellationToken cancellationToken)
        {
            UnityRuntimeSelection? unityRuntime = FindUnityRuntime(
                referencePaths,
                rootResponse.ParseOptions);
            ExternalAssemblyMaterial[] assemblies = new ExternalAssemblyMaterial[referencePaths.Count];
            ParallelOptions options = new()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = jobs,
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, referencePaths.Count),
                options,
                (index, _) =>
                {
                    string path = referencePaths[index];
                    assemblies[index] = new ExternalAssemblyMaterial(
                        path,
                        FindImplementationPaths(projectRoot, path, unityRuntime));

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            string[] knownPaths = assemblies
                .SelectMany(assembly => assembly.ImplementationPaths)
                .Concat(unityRuntime?.AssemblyPaths ?? Array.Empty<string>())
                .Concat(FindSiblingAssemblyPaths(assemblies, unityRuntime))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<string, string> assemblyRedirects = ReadAssemblyRedirects(
                assemblies,
                knownPaths,
                unityRuntime);
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName =
                IndexAssemblyPaths(knownPaths);
            ExternalAssemblyMaterial[] resolved = new ExternalAssemblyMaterial[assemblies.Length];
            ConcurrentDictionary<string, Lazy<IReadOnlyList<AssemblyReferenceIdentity>>> forwardedAssembliesByPath =
                new(StringComparer.OrdinalIgnoreCase);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, assemblies.Length),
                options,
                (index, _) =>
                {
                    ExternalAssemblyMaterial assembly = assemblies[index];
                    resolved[index] = assembly with
                    {
                        ImplementationPaths = FollowForwardedAssemblies(
                            assembly.ImplementationPaths,
                            pathsByAssemblyName,
                            unityRuntime,
                            forwardedAssembliesByPath),
                    };

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            return new ExternalMaterialResolution(
                resolved.OrderBy(
                        assembly => assembly.ReferencePath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                knownPaths.Where(path => CanBeAssemblyLookupPath(path, unityRuntime))
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                unityRuntime?.FacadePaths ?? Array.Empty<string>(),
                assemblyRedirects);
        }

        // 将编译引用所在目录的托管文件加入按需查找候选而不提前分析。
        private static IEnumerable<string> FindSiblingAssemblyPaths(
            IEnumerable<ExternalAssemblyMaterial> assemblies,
            UnityRuntimeSelection? unityRuntime)
        {
            return assemblies.SelectMany(assembly => assembly.ImplementationPaths)
                .Select(Path.GetDirectoryName)
                .Where(path => path != null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(path => Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly))
                .Where(IsManagedAssembly)
                .Select(Path.GetFullPath)
                .Where(path => CanBeAssemblyLookupPath(path, unityRuntime));
        }

        // 排除只用于编译和类型别名的Unity参考根文件。
        private static bool CanBeAssemblyLookupPath(string path, UnityRuntimeSelection? unityRuntime)
        {
            return unityRuntime == null
                || !unityRuntime.ReferenceRoots.Any(root => IsUnderDirectory(path, root));
        }

        // 判断动态链接库是否含有可由 Cecil 读取的托管元数据。
        private static bool IsManagedAssembly(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);

            return portableExecutable.HasMetadata;
        }

        // 把一条程序集引用转换为可核对的完整身份。
        private static AssemblyReferenceIdentity ReadAssemblyReference(
            MetadataReader metadata,
            AssemblyReferenceHandle handle)
        {
            AssemblyReference reference = metadata.GetAssemblyReference(handle);

            return new AssemblyReferenceIdentity(
                metadata.GetString(reference.Name),
                reference.Version,
                reference.Culture.IsNil ? string.Empty : metadata.GetString(reference.Culture),
                reference.PublicKeyOrToken.IsNil
                    ? ImmutableArray<byte>.Empty
                    : metadata.GetBlobBytes(reference.PublicKeyOrToken).ToImmutableArray(),
                (reference.Flags & AssemblyFlags.PublicKey) != 0);
        }


        // 按 Unity 明确的输出位置寻找一份参考文件的真实实现。
        private static IReadOnlyList<string> FindImplementationPaths(
            string projectRoot,
            string referencePath,
            UnityRuntimeSelection? unityRuntime)
        {
            const string suffix = ".ref.dll";
            bool isPureForwardingFacade = IsPureForwardingFacade(referencePath);

            if (unityRuntime != null
                && unityRuntime.ReferenceRoots.Any(root => IsUnderDirectory(referencePath, root)))
            {
                string runtimeFileName = Path.GetFileName(referencePath);
                string[] runtimeCandidates = unityRuntime.AssemblyPaths
                    .Where(path => string.Equals(
                        Path.GetFileName(path),
                        runtimeFileName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                string[] compatibleCandidates = runtimeCandidates.Where(path =>
                        MatchesReferenceCandidate(referencePath, path, unityRuntime))
                    .ToArray();

                IReadOnlyList<string> selectedPaths = compatibleCandidates.Length switch
                {
                    1 => compatibleCandidates,
                    0 when runtimeCandidates.Length == 0 && isPureForwardingFacade =>
                        new[] { referencePath },
                    0 when runtimeCandidates.Length == 0 => throw new AnalysisException(
                        $"Unity 当前运行目录没有参考文件的真实实现：{referencePath}"),
                    0 => throw new AnalysisException(
                        $"参考文件与真实文件的程序集身份不同：{referencePath} => "
                            + string.Join("; ", runtimeCandidates)),
                    _ => throw new AnalysisException(
                        $"Unity 当前运行目录存在多份实现：{referencePath} => "
                            + string.Join("; ", compatibleCandidates)),
                };

                RequireMatchingAssemblyIdentity(referencePath, selectedPaths[0], unityRuntime);

                return selectedPaths;
            }

            if (isPureForwardingFacade)
            {
                return new[] { referencePath };
            }

            if (!referencePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return new[] { referencePath };
            }

            string fileName = $"{Path.GetFileName(referencePath)[..^suffix.Length]}.dll";
            string referenceDirectory = Path.GetDirectoryName(referencePath)!;
            string[] candidates = new[]
                {
                    Path.Combine(referenceDirectory, fileName),
                    Path.Combine(referenceDirectory, "post-processed", fileName),
                    Path.Combine(projectRoot, "Library", "ScriptAssemblies", fileName),
                }
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            IReadOnlyList<string> implementationPaths = candidates.Length switch
            {
                1 => candidates,
                0 => throw new AnalysisException(
                    $"没有找到参考文件的真实实现：{referencePath}"),
                _ => throw new AnalysisException(
                    $"参考文件存在多份真实实现：{referencePath} => {string.Join("; ", candidates)}"),
            };

            RequireMatchingAssemblyIdentity(
                referencePath,
                implementationPaths[0],
                unityRuntime: null);

            return implementationPaths;
        }

        // 检查参考文件是否可以精确连接到候选运行文件。
        private static bool MatchesReferenceCandidate(
            string referencePath,
            string implementationPath,
            UnityRuntimeSelection unityRuntime)
        {
            AssemblyName reference = AssemblyName.GetAssemblyName(referencePath);
            AssemblyName implementation = AssemblyName.GetAssemblyName(implementationPath);

            return SameAssemblyNameCultureAndToken(reference, implementation)
                && (Equals(reference.Version, implementation.Version)
                    || IsCompatibleUnityFacade(reference, implementationPath, unityRuntime)
                    || IsCompatibleUnityFramework(
                        reference.Name!, reference.Version!, implementation.Version!, unityRuntime));
        }

        // 核对参考文件与真实实现声明的是同一个程序集。
        private static void RequireMatchingAssemblyIdentity(
            string referencePath,
            string implementationPath,
            UnityRuntimeSelection? unityRuntime)
        {
            AssemblyName reference = AssemblyName.GetAssemblyName(referencePath);
            AssemblyName implementation = AssemblyName.GetAssemblyName(implementationPath);
            bool sameIdentity = SameAssemblyNameCultureAndToken(reference, implementation)
                && (Equals(reference.Version, implementation.Version)
                    || IsCompatibleUnityFacade(reference, implementationPath, unityRuntime)
                    || IsCompatibleUnityFramework(
                        reference.Name!, reference.Version!, implementation.Version!, unityRuntime));

            if (!sameIdentity)
            {
                throw new AnalysisException(
                    $"参考文件与真实文件的程序集身份不同：{referencePath} => {implementationPath}");
            }
        }

        // 比较程序集的名称、区域和公钥标记。
        private static bool SameAssemblyNameCultureAndToken(
            AssemblyName expected,
            AssemblyName candidate)
        {
            return string.Equals(expected.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    expected.CultureName ?? string.Empty,
                    candidate.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (expected.GetPublicKeyToken() ?? Array.Empty<byte>())
                    .SequenceEqual(candidate.GetPublicKeyToken() ?? Array.Empty<byte>());
        }

        // 保留材料模块已唯一证明的普通框架身份重定向。
        private static IReadOnlyDictionary<string, string> ReadAssemblyRedirects(
            IEnumerable<ExternalAssemblyMaterial> assemblies,
            IReadOnlyList<string> lookupPaths,
            UnityRuntimeSelection? unityRuntime)
        {
            Dictionary<string, string> redirects = new(StringComparer.OrdinalIgnoreCase);
            if (unityRuntime == null)
            {
                return redirects;
            }

            IReadOnlyDictionary<string, IReadOnlyList<string>> runtimePaths = IndexAssemblyPaths(unityRuntime.AssemblyPaths);
            IEnumerable<AssemblyName> references = assemblies.Select(assembly =>
                    AssemblyName.GetAssemblyName(assembly.ReferencePath))
                .Concat(lookupPaths.SelectMany(ReadReferencedAssemblyNames))
                .DistinctBy(reference => reference.FullName, StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyName reference in references)
            {
                if (!runtimePaths.TryGetValue(reference.Name!, out IReadOnlyList<string>? candidates)
                    || !unityRuntime.FrameworkRemappings.ContainsKey(reference.Name!))
                {
                    continue;
                }

                string[] matching = candidates.Where(path =>
                {
                    AssemblyName actual = AssemblyName.GetAssemblyName(path);

                    return !Equals(reference.Version, actual.Version)
                        && SameAssemblyNameCultureAndToken(reference, actual)
                        && IsCompatibleUnityFramework(reference.Name!, reference.Version!, actual.Version!, unityRuntime);
                }).ToArray();
                if (matching.Length > 1)
                {
                    throw new AnalysisException($"参考程序集身份重定向不唯一：{reference.FullName} => {string.Join("; ", matching)}");
                }

                if (matching.Length == 1)
                {
                    redirects.Add(reference.FullName!, matching[0]);
                }
            }

            return redirects;
        }

        // 只读已纳入候选文件的依赖身份，使间接引用使用相同的运行时规则。
        private static IReadOnlyList<AssemblyName> ReadReferencedAssemblyNames(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);
            MetadataReader metadata = portableExecutable.GetMetadataReader();

            return metadata.AssemblyReferences.Select(handle =>
                metadata.GetAssemblyReference(handle).GetAssemblyName()).ToArray();
        }

        // 沿类型转交记录找到最终承载类型的编译文件。
        private static IReadOnlyList<string> FollowForwardedAssemblies(
            IReadOnlyList<string> rootPaths,
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName,
            UnityRuntimeSelection? unityRuntime,
            ConcurrentDictionary<string, Lazy<IReadOnlyList<AssemblyReferenceIdentity>>> forwardedAssembliesByPath)
        {
            Queue<string> pending = new(rootPaths);
            List<string> paths = new();
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

            while (pending.TryDequeue(out string? path))
            {
                if (!visited.Add(path))
                {
                    continue;
                }

                paths.Add(path);
                string normalizedPath = Path.GetFullPath(path);
                IReadOnlyList<AssemblyReferenceIdentity> forwardedAssemblies = forwardedAssembliesByPath.GetOrAdd(
                    normalizedPath,
                    static currentPath => new Lazy<IReadOnlyList<AssemblyReferenceIdentity>>(
                        () => ReadForwardedAssemblies(currentPath),
                        LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                foreach (AssemblyReferenceIdentity identity in forwardedAssemblies)
                {
                    string? forwardedPath = FindForwardedAssembly(
                        path,
                        identity,
                        pathsByAssemblyName,
                        unityRuntime);
                    if (forwardedPath != null)
                    {
                        pending.Enqueue(forwardedPath);
                    }
                }
            }

            return paths;
        }

        // 读取一份托管文件中的全部类型转交目标程序集名称。
        private static IReadOnlyList<AssemblyReferenceIdentity> ReadForwardedAssemblies(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);
            MetadataReader metadata = portableExecutable.GetMetadataReader();
            Dictionary<string, AssemblyReferenceIdentity> identities = new(
                StringComparer.OrdinalIgnoreCase);

            foreach (ExportedTypeHandle handle in metadata.ExportedTypes)
            {
                ExportedType exportedType = metadata.GetExportedType(handle);

                EntityHandle implementation = exportedType.Implementation;

                while (implementation.Kind == HandleKind.ExportedType)
                {
                    implementation = metadata
                        .GetExportedType((ExportedTypeHandle)implementation)
                        .Implementation;
                }

                if (implementation.Kind == HandleKind.AssemblyReference)
                {
                    AssemblyReferenceIdentity identity = ReadAssemblyReference(
                        metadata,
                        (AssemblyReferenceHandle)implementation);

                    identities[identity.Key] = identity;
                }
            }

            return identities.Values
                .OrderBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // 在转交文件明确可达的位置寻找唯一已有目标程序集。
        private static string? FindForwardedAssembly(
            string forwardingPath,
            AssemblyReferenceIdentity identity,
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName,
            UnityRuntimeSelection? unityRuntime)
        {
            DirectoryInfo directory = new(Path.GetDirectoryName(forwardingPath)!);
            HashSet<string> candidates = pathsByAssemblyName.TryGetValue(
                identity.Name,
                out IReadOnlyList<string>? knownPaths)
                    ? knownPaths
                        .Where(path => MatchesIdentity(path, identity, unityRuntime))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(
                candidates,
                Path.Combine(directory.FullName, $"{identity.Name}.dll"),
                identity,
                unityRuntime);
            if (string.Equals(directory.Name, "Facades", StringComparison.OrdinalIgnoreCase)
                && directory.Parent != null)
            {
                AddCandidate(
                    candidates,
                    Path.Combine(directory.Parent.FullName, $"{identity.Name}.dll"),
                    identity,
                    unityRuntime);
            }

            if (unityRuntime != null)
            {
                candidates.RemoveWhere(path => unityRuntime.ReferenceRoots.Any(root => IsUnderDirectory(path, root)));
            }

            return candidates.Count switch
            {
                1 => candidates.Single(),
                0 => null,
                _ => throw new AnalysisException(
                    $"类型转交目标不唯一：{forwardingPath} => {identity.Name}：{string.Join("; ", candidates)}"),
            };
        }

        // 按程序集自身名称建立一次运行内的快速文件索引。
        private static IReadOnlyDictionary<string, IReadOnlyList<string>> IndexAssemblyPaths(
            IEnumerable<string> paths)
        {
            return paths
                .GroupBy(ReadAssemblyIdentityName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key != null)
                .ToDictionary(
                    group => group.Key!,
                    group => (IReadOnlyList<string>)group
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        // 在候选文件存在时加入目标集合。
        private static void AddCandidate(
            ISet<string> candidates,
            string path,
            AssemblyReferenceIdentity identity,
            UnityRuntimeSelection? unityRuntime)
        {
            if (File.Exists(path) && MatchesIdentity(path, identity, unityRuntime))
            {
                candidates.Add(Path.GetFullPath(path));
            }
        }

        // 检查候选文件是否符合类型转交记录中的完整程序集身份。
        private static bool MatchesIdentity(
            string path,
            AssemblyReferenceIdentity identity,
            UnityRuntimeSelection? unityRuntime)
        {
            AssemblyName candidate = AssemblyName.GetAssemblyName(path);
            byte[] candidateKey = identity.UsesFullPublicKey
                ? candidate.GetPublicKey() ?? Array.Empty<byte>()
                : candidate.GetPublicKeyToken() ?? Array.Empty<byte>();
            bool versionMatches = identity.Version == new Version(0, 0, 0, 0)
                || Equals(identity.Version, candidate.Version)
                || IsCompatibleUnityFacade(identity, path, unityRuntime)
                || (candidate.Version != null && IsCompatibleUnityFramework(
                    identity.Name,
                    identity.Version,
                    candidate.Version,
                    unityRuntime));

            return string.Equals(
                    identity.Name,
                    candidate.Name,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    identity.Culture,
                    candidate.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && identity.PublicKeyOrToken.SequenceEqual(candidateKey)
                && versionMatches;
        }

        // 只允许 Unity 当前运行配置的纯转交门面使用门面版本规则。
        private static bool IsCompatibleUnityFacade(
            AssemblyName expected,
            string candidatePath,
            UnityRuntimeSelection? unityRuntime)
        {
            if (unityRuntime == null
                || !unityRuntime.FacadePaths.Contains(candidatePath, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            AssemblyName candidate = AssemblyName.GetAssemblyName(candidatePath);

            return SameAssemblyNameCultureAndToken(expected, candidate)
                && candidate.Version?.Major >= expected.Version?.Major;
        }

        // 只对已验证的 Unity 纯转交门面放宽转交目标的主版本。
        private static bool IsCompatibleUnityFacade(
            AssemblyReferenceIdentity expected,
            string candidatePath,
            UnityRuntimeSelection? unityRuntime)
        {
            if (unityRuntime == null
                || !unityRuntime.FacadePaths.Contains(candidatePath, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            AssemblyName candidate = AssemblyName.GetAssemblyName(candidatePath);
            byte[] candidateKey = expected.UsesFullPublicKey
                ? candidate.GetPublicKey() ?? Array.Empty<byte>()
                : candidate.GetPublicKeyToken() ?? Array.Empty<byte>();

            return string.Equals(expected.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    expected.Culture,
                    candidate.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && expected.PublicKeyOrToken.SequenceEqual(candidateKey)
                && candidate.Version?.Major >= expected.Version.Major;
        }

        // 核对一条已证明的Mono框架版本重映射。
        private static bool IsCompatibleUnityFramework(
            string expectedName,
            Version expectedVersion,
            Version candidateVersion,
            UnityRuntimeSelection? unityRuntime)
        {
            if (unityRuntime == null
                || !unityRuntime.FrameworkRemappings.TryGetValue(expectedName, out UnityFrameworkRemapping? mapping)
                || mapping.RuntimeVersion != candidateVersion)
            {
                return false;
            }

            return !mapping.OnlyLowerVersions || expectedVersion < mapping.RuntimeVersion;
        }

        // 核对运行配置 Facades 目录中的文件只含类型转交表。
        private static bool IsPureForwardingFacade(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);
            MetadataReader metadata = portableExecutable.GetMetadataReader();
            TypeDefinitionHandle[] definitions = metadata.TypeDefinitions.ToArray();

            return metadata.ExportedTypes.Count > 0
                && definitions.Length == 1
                && metadata.GetString(metadata.GetTypeDefinition(definitions[0]).Name) == "<Module>"
                && metadata.ExportedTypes.All(handle =>
                {
                    EntityHandle implementation = metadata.GetExportedType(handle).Implementation;
                    while (implementation.Kind == HandleKind.ExportedType)
                    {
                        implementation = metadata.GetExportedType((ExportedTypeHandle)implementation).Implementation;
                    }

                    return implementation.Kind == HandleKind.AssemblyReference;
                });
        }

        // 读取托管文件自身声明的程序集名称。
        private static string? ReadAssemblyIdentityName(string path)
        {
            return AssemblyName.GetAssemblyName(path).Name;
        }

        // 从当前编译标记和 Unity 安装路径选择唯一运行目录。
        private static UnityRuntimeSelection? FindUnityRuntime(
            IReadOnlyList<string> referencePaths,
            CSharpParseOptions parseOptions)
        {
            string[] dataDirectories = referencePaths
                .Select(FindUnityDataDirectory)
                .Where(path => path != null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (dataDirectories.Length == 0)
            {
                return null;
            }

            if (dataDirectories.Length > 1)
            {
                throw new AnalysisException(
                    $"编译引用来自多套 Unity 安装：{string.Join("; ", dataDirectories)}");
            }

            string dataDirectory = dataDirectories[0];
            string runtimeName = SelectUnityRuntimeName(parseOptions.PreprocessorSymbolNames);
            string runtimeDirectory = Path.Combine(
                dataDirectory,
                "MonoBleedingEdge",
                "lib",
                "mono",
                runtimeName);

            if (!Directory.Exists(runtimeDirectory))
            {
                throw new AnalysisException($"Unity 当前运行目录不存在：{runtimeDirectory}");
            }

            string managedDirectory = Path.Combine(dataDirectory, "Managed");
            string facadeDirectory = Path.Combine(runtimeDirectory, "Facades");
            string[] assemblyPaths = Directory.EnumerateFiles(
                    runtimeDirectory,
                    "*.dll",
                    SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    managedDirectory,
                    "*.dll",
                    SearchOption.TopDirectoryOnly))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] facadePaths = Directory.Exists(facadeDirectory)
                ? Directory.EnumerateFiles(facadeDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                    .Where(IsManagedAssembly)
                    .Where(IsPureForwardingFacade)
                    .Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            IReadOnlyDictionary<string, UnityFrameworkRemapping> frameworkRemappings =
                ReadUnityFrameworkRemappings(dataDirectory, runtimeDirectory);

            return new UnityRuntimeSelection(
                new[]
                {
                    Path.Combine(dataDirectory, "NetStandard"),
                    Path.Combine(dataDirectory, "UnityReferenceAssemblies"),
                },
                assemblyPaths,
                facadePaths,
                frameworkRemappings);
        }

        // 从当前Unity携带的Mono源码读取普通框架版本重映射。
        private static IReadOnlyDictionary<string, UnityFrameworkRemapping> ReadUnityFrameworkRemappings(
            string dataDirectory,
            string runtimeDirectory)
        {
            string metadataDirectory = Path.Combine(dataDirectory, "il2cpp", "external", "mono", "mono", "metadata");
            string assemblySourcePath = Path.Combine(metadataDirectory, "assembly.c");
            string domainSourcePath = Path.Combine(metadataDirectory, "domain.c");
            string coreLibraryPath = Path.Combine(runtimeDirectory, "mscorlib.dll");
            if (!File.Exists(assemblySourcePath) || !File.Exists(domainSourcePath) || !File.Exists(coreLibraryPath))
            {
                throw new AnalysisException($"Unity当前安装缺少Mono框架版本表证据：{metadataDirectory}");
            }

            Version runtimeVersion = AssemblyName.GetAssemblyName(coreLibraryPath).Version
                ?? throw new AnalysisException($"Unity运行时核心库没有版本：{coreLibraryPath}");
            Version[] versionSets = ReadRuntimeVersionSets(domainSourcePath, runtimeVersion);
            string table = ReadSourceArray(assemblySourcePath, "framework_assemblies []");
            Dictionary<string, UnityFrameworkRemapping> result = new(StringComparer.OrdinalIgnoreCase);
            const string entryPattern = "^\\s*\\{\\s*\"(?<name>[^\"]+)\"\\s*,\\s*(?<index>\\d+)"
                + "(?:\\s*,\\s*(?<newName>NULL|\"[^\"]+\"))?(?:\\s*,\\s*(?<lower>TRUE|FALSE))?\\s*\\}\\s*,?\\s*$";

            foreach (string line in table.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Regex.IsMatch(line, "^\\s*FACADE_ASSEMBLY\\s*\\(\\s*\"[^\"]+\"\\s*\\)\\s*,?\\s*$"))
                {
                    continue;
                }

                Match entry = Regex.Match(line, entryPattern);
                if (!entry.Success || !int.TryParse(entry.Groups["index"].Value, out int versionIndex)
                    || versionIndex >= versionSets.Length)
                {
                    throw new AnalysisException($"Unity Mono框架版本表格式无法证明：{assemblySourcePath} => {line.Trim()}");
                }

                if (!entry.Groups["newName"].Success || entry.Groups["newName"].Value == "NULL")
                {
                    result.Add(entry.Groups["name"].Value, new UnityFrameworkRemapping(
                        versionSets[versionIndex],
                        entry.Groups["lower"].Value == "TRUE"));
                }
            }

            return result;
        }

        // 从Mono当前运行时行读取各框架表项的目标版本。
        private static Version[] ReadRuntimeVersionSets(string path, Version runtimeVersion)
        {
            string table = ReadSourceArray(path, "supported_runtimes[]");
            const string rowPattern = "^\\s*\\{\\s*\"[^\"]+\"\\s*,\\s*\"[^\"]+\"\\s*,\\s*\\{(?<sets>.*)\\}\\s*\\}\\s*,?\\s*$";
            const string versionPattern = "\\{\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*\\}";
            List<Version[]> matchingRows = new();

            foreach (string line in table.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Match row = Regex.Match(line, rowPattern);
                if (!row.Success)
                {
                    continue;
                }

                MatchCollection matches = Regex.Matches(row.Groups["sets"].Value, versionPattern);
                Version[] versions = matches.Select(match => new Version(
                    int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value))).ToArray();
                if (versions.Length > 0 && versions[0] == runtimeVersion)
                {
                    string unparsed = Regex.Replace(row.Groups["sets"].Value, versionPattern, string.Empty);
                    if (Regex.IsMatch(unparsed, "[^\\s,]"))
                    {
                        throw new AnalysisException($"Unity Mono框架版本表含未解析版本槽：{path} => {line}");
                    }

                    matchingRows.Add(versions);
                }
            }

            return matchingRows.Count == 1
                ? matchingRows[0]
                : throw new AnalysisException($"Unity Mono框架版本表无法匹配当前运行时：{path} => {runtimeVersion}");
        }

        // 精确截取Mono源码中指定的静态数组内容。
        private static string ReadSourceArray(string path, string name)
        {
            string source = File.ReadAllText(path);
            int nameStart = source.IndexOf(name, StringComparison.Ordinal);
            int bodyStart = nameStart < 0 ? -1 : source.IndexOf('{', nameStart);
            Match bodyEnd = bodyStart < 0 ? Match.Empty : Regex.Match(source[bodyStart..], "(?m)^\\s*};\\s*$");
            if (bodyStart < 0 || !bodyEnd.Success)
            {
                throw new AnalysisException($"Unity Mono框架版本表格式无法证明：{path} => {name}");
            }

            return source.Substring(bodyStart + 1, bodyEnd.Index - 1);
        }

        // 读取 Unity 平台标记对应的即时或预编译运行目录名称。
        private static string SelectUnityRuntimeName(IEnumerable<string> symbols)
        {
            HashSet<string> symbolSet = symbols.ToHashSet(StringComparer.Ordinal);
            string? hostName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "win32"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "macos"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? "linux"
                        : null;

            if (hostName == null)
            {
                throw new AnalysisException("当前系统没有对应的 Unity 托管运行目录。");
            }

            string[] editorNames = new[]
                {
                    (Symbol: "UNITY_EDITOR_WIN", Name: "unityjit-win32"),
                    (Symbol: "UNITY_EDITOR_OSX", Name: "unityjit-macos"),
                    (Symbol: "UNITY_EDITOR_LINUX", Name: "unityjit-linux"),
                }
                .Where(item => symbolSet.Contains(item.Symbol))
                .Select(item => item.Name)
                .ToArray();

            if (editorNames.Length > 1)
            {
                throw new AnalysisException("Unity 编译标记同时指定了多个编辑器系统。");
            }

            if (editorNames.Length == 1)
            {
                return editorNames[0];
            }

            bool usesAot = symbolSet.Contains("ENABLE_IL2CPP");
            bool usesJustInTime = symbolSet.Contains("ENABLE_MONO");

            if (usesAot == usesJustInTime)
            {
                throw new AnalysisException("Unity 编译标记不能唯一确定运行方式。");
            }

            return usesAot ? $"unityaot-{hostName}" : $"unityjit-{hostName}";
        }

        // 找到引用路径所属的 Unity Data 目录。
        private static string? FindUnityDataDirectory(string referencePath)
        {
            DirectoryInfo? directory = new FileInfo(referencePath).Directory;

            while (directory != null)
            {
                if (string.Equals(directory.Name, "Data", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(directory.FullName, "MonoBleedingEdge")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }

        // 判断文件是否位于一个已确认的目录内部。
        private static bool IsUnderDirectory(string filePath, string directoryPath)
        {
            string relativePath = Path.GetRelativePath(directoryPath, filePath);

            return !Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal);
        }

        // 检查调用方明确约定的输入范围。
        private static void ValidateRequest(MaterialRequest request)
        {
            if (request.Jobs <= 0)
            {
                throw new AnalysisException("工作数量必须是正整数。");
            }

            if (!File.Exists(request.AssemblyDefinitionPath))
            {
                throw new AnalysisException($"程序集定义文件不存在：{request.AssemblyDefinitionPath}");
            }

            if (!string.Equals(
                Path.GetExtension(request.AssemblyDefinitionPath),
                ".asmdef",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new AnalysisException($"输入必须是 Unity 程序集定义文件：{request.AssemblyDefinitionPath}");
            }
        }

        // 从程序集定义向上找到当前 Unity 工程根目录。
        private static string FindProjectRoot(string assemblyDefinitionPath)
        {
            DirectoryInfo? directory = new FileInfo(assemblyDefinitionPath).Directory;

            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Library", "Bee", "artifacts")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new AnalysisException(
                $"没有找到 Unity 当前生成的 Library/Bee/artifacts：{assemblyDefinitionPath}");
        }

        // 从程序集定义读取 Unity 使用的程序集名称。
        private static string ReadAssemblyName(string assemblyDefinitionPath)
        {
            using FileStream stream = File.OpenRead(assemblyDefinitionPath);
            using JsonDocument document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("name", out JsonElement nameElement)
                || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                throw new AnalysisException($"程序集定义没有有效名称：{assemblyDefinitionPath}");
            }

            return nameElement.GetString()!;
        }

        // 找到当前编译唯一使用的根程序集响应文件。
        private static string FindRootResponse(string projectRoot, string assemblyName)
        {
            string artifactsPath = Path.Combine(projectRoot, "Library", "Bee", "artifacts");
            string[] candidates = Directory.EnumerateFiles(
                    artifactsPath,
                    $"{assemblyName}.rsp",
                    SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return candidates.Length switch
            {
                1 => candidates[0],
                0 => throw new AnalysisException(
                    $"没有找到 {assemblyName} 当前使用的 Unity 编译响应文件。"),
                _ => throw new AnalysisException(
                    $"找到多份 {assemblyName} 编译响应文件：{string.Join("; ", candidates)}"),
            };
        }

        // 沿 Unity 生成的参考文件读取全部源码程序集响应文件。
        private static IReadOnlyList<CompilerResponse> ReadResponseClosure(
            string projectRoot,
            string rootAssemblyName,
            string rootResponsePath)
        {
            Queue<(string Name, string Path)> pending = new();
            Dictionary<string, CompilerResponse> responses = new(StringComparer.Ordinal);

            pending.Enqueue((rootAssemblyName, rootResponsePath));
            while (pending.TryDequeue(out (string Name, string Path) current))
            {
                if (responses.ContainsKey(current.Name))
                {
                    continue;
                }

                CompilerResponse response = ReadResponse(projectRoot, current.Name, current.Path);

                responses.Add(current.Name, response);
                foreach (SourceReference sourceReference in response.SourceReferences)
                {
                    pending.Enqueue((sourceReference.AssemblyName, sourceReference.ResponsePath));
                }
            }

            return responses.Values
                .OrderBy(response => response.AssemblyName, StringComparer.Ordinal)
                .ToArray();
        }

        // 解析一份 Unity 编译响应文件并核对其中的真实路径。
        private static CompilerResponse ReadResponse(
            string projectRoot,
            string assemblyName,
            string responsePath)
        {
            CSharpCommandLineArguments arguments = CSharpCommandLineParser.Default.Parse(
                new[] { $"@{responsePath}" },
                projectRoot,
                sdkDirectory: null);
            Diagnostic[] errors = arguments.Errors
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();

            if (errors.Length > 0)
            {
                throw new AnalysisException(
                    $"无法读取 Unity 编译响应文件 {responsePath}：{string.Join("; ", errors.AsEnumerable())}");
            }

            string[] sourcePaths = arguments.SourceFiles
                .Select(source => ResolvePath(projectRoot, source.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CompilerReference[] references = arguments.MetadataReferences
                .Select(reference => new CompilerReference(
                    ResolvePath(projectRoot, reference.Reference),
                    reference.Properties))
                .ToArray();
            string[] analyzerPaths = arguments.AnalyzerReferences
                .Select(reference => ResolvePath(projectRoot, reference.FilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] additionalFilePaths = arguments.AdditionalFiles
                .Select(file => ResolvePath(projectRoot, file.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            RequireFiles(sourcePaths, "源码", responsePath);
            RequireFiles(references.Select(reference => reference.Path), "外部编译文件", responsePath);
            RequireFiles(analyzerPaths, "分析器", responsePath);
            RequireFiles(additionalFilePaths, "附加文件", responsePath);

            SourceReference[] sourceReferences = references
                .Select(reference => FindSourceResponse(reference.Path))
                .Where(reference => reference != null)
                .Cast<SourceReference>()
                .ToArray();

            return new CompilerResponse(
                assemblyName,
                arguments.ParseOptions,
                arguments.CompilationOptions,
                Path.GetFullPath(Path.Combine(arguments.OutputDirectory,
                    arguments.OutputFileName ?? throw new AnalysisException($"Unity 响应文件缺少输出路径：{responsePath}"))),
                arguments.EmitOptions.WithDebugInformationFormat(DebugInformationFormat.PortablePdb),
                sourcePaths,
                references,
                analyzerPaths,
                additionalFilePaths,
                sourceReferences);
        }

        // 验证 Unity 声明参与编译的文件确实存在。
        private static void RequireFiles(
            IEnumerable<string> paths,
            string kind,
            string responsePath)
        {
            string? missingPath = paths.FirstOrDefault(path => !File.Exists(path));

            if (missingPath != null)
            {
                throw new AnalysisException(
                    $"Unity 编译响应文件 {responsePath} 中的{kind}不存在：{missingPath}");
            }
        }

        // 判断参考文件是否对应另一个有源码响应文件的 Unity 程序集。
        private static SourceReference? FindSourceResponse(string referencePath)
        {
            const string suffix = ".ref.dll";

            if (!referencePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string assemblyName = Path.GetFileName(referencePath)[..^suffix.Length];
            string responsePath = Path.Combine(
                Path.GetDirectoryName(referencePath)!,
                $"{assemblyName}.rsp");

            return File.Exists(responsePath)
                ? new SourceReference(assemblyName, referencePath, responsePath)
                : null;
        }

        // 并行读取并解析全部源码文件。
        private static async Task<IReadOnlyDictionary<string, SyntaxTree>> ParseSourcesAsync(
            IReadOnlyList<CompilerResponse> responses,
            int jobs,
            CancellationToken cancellationToken)
        {
            SourceInput[] inputs = responses
                .SelectMany(response => response.SourcePaths.Select(path =>
                    new SourceInput(response.AssemblyName, path, response.ParseOptions)))
                .ToArray();
            ConcurrentDictionary<string, SyntaxTree> trees = new(StringComparer.OrdinalIgnoreCase);

            await Parallel.ForEachAsync(
                inputs,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                async (input, token) =>
                {
                    string text = await File.ReadAllTextAsync(input.Path, token).ConfigureAwait(false);
                    string key = SourceKey(input.AssemblyName, input.Path);

                    trees[key] = CSharpSyntaxTree.ParseText(
                        text,
                        input.ParseOptions,
                        input.Path,
                        cancellationToken: token);
                }).ConfigureAwait(false);

            return trees;
        }

        // 并行建立可由多个源码程序集安全共用的不可变元数据参考。
        private static Dictionary<CompilerReference, PortableExecutableReference>
            CreateMetadataReferences(
            IReadOnlyList<CompilerResponse> responses,
            IReadOnlySet<string> sourceReferencePaths,
            int jobs,
            CancellationToken cancellationToken)
        {
            CompilerReference[] inputs = responses
                .SelectMany(response => response.References)
                .Where(reference => !sourceReferencePaths.Contains(reference.Path))
                .Distinct()
                .ToArray();
            PortableExecutableReference[] references = new PortableExecutableReference[inputs.Length];

            Parallel.For(
                0,
                inputs.Length,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                index => references[index] = MetadataReference.CreateFromFile(
                    inputs[index].Path,
                    inputs[index].Properties));

            return Enumerable.Range(0, inputs.Length)
                .ToDictionary(index => inputs[index], index => references[index]);
        }

        // 用已解析源码和原始编译选项建立程序集编译内容。
        private static SourceAssemblyMaterial BuildSourceAssembly(
            CompilerResponse response,
            string reportRoot,
            bool enableCompilerParallel,
            IReadOnlyDictionary<string, SyntaxTree> trees,
            IReadOnlyDictionary<CompilerReference, PortableExecutableReference> metadataReferences,
            IReadOnlyList<ISourceGenerator> generators,
            CancellationToken cancellationToken)
        {
            SyntaxTree[] assemblyTrees = response.SourcePaths
                .Select(path => trees[SourceKey(response.AssemblyName, path)])
                .ToArray();
            PortableExecutableReference[] references = response.References
                .Select(reference => metadataReferences[reference])
                .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(
                response.AssemblyName,
                assemblyTrees,
                references,
                response.CompilationOptions.WithConcurrentBuild(enableCompilerParallel));
            if (generators.Count > 0)
            {
                AdditionalText[] additionalTexts = response.AdditionalFilePaths
                    .Select(path => (AdditionalText)new DiskAdditionalText(path))
                    .ToArray();
                GeneratorDriver driver = CSharpGeneratorDriver.Create(
                    generators,
                    additionalTexts,
                    parseOptions: response.ParseOptions);

                driver.RunGeneratorsAndUpdateCompilation(
                    compilation,
                    out Compilation generatedCompilation,
                    out ImmutableArray<Diagnostic> diagnostics,
                    cancellationToken);
                Diagnostic[] errors = diagnostics
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .ToArray();

                if (errors.Length > 0)
                {
                    throw new AnalysisException(
                        $"源码生成器执行失败：{string.Join("; ", errors.AsEnumerable())}");
                }

                compilation = (CSharpCompilation)generatedCompilation;
            }

            string[] reportSourcePaths = response.SourcePaths
                .Where(path => IsUnderDirectory(path, reportRoot))
                .ToArray();

            return new SourceAssemblyMaterial(
                response.AssemblyName,
                reportSourcePaths.Length > 0,
                compilation,
                response.SourcePaths,
                reportSourcePaths,
                response.OutputPath,
                Array.Empty<byte>());
        }

        // 让编译参数相同的源码程序集共用一次分析器加载结果。
        private static IReadOnlyDictionary<string, ISourceGenerator[]> LoadGeneratorSets(
            IReadOnlyList<CompilerResponse> responses)
        {
            return responses
                .GroupBy(response => AnalyzerSetKey(response.AnalyzerPaths), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => LoadGenerators(group.First().AnalyzerPaths),
                    StringComparer.Ordinal);
        }

        // 把一组已排序的分析器路径转换成一次运行内的查找键。
        private static string AnalyzerSetKey(IReadOnlyList<string> analyzerPaths)
        {
            return string.Join('\0', analyzerPaths);
        }

        // 从 Unity 声明的分析器文件中读取 C# 源码生成器。
        private static ISourceGenerator[] LoadGenerators(IReadOnlyList<string> analyzerPaths)
        {
            AnalyzerAssemblyLoader loader = new();
            List<ISourceGenerator> generators = new();
            List<string> problems = new();

            foreach (string path in analyzerPaths)
            {
                loader.AddDependencyLocation(path);
            }

            foreach (string path in analyzerPaths)
            {
                AnalyzerFileReference reference = new(path, loader);

                reference.AnalyzerLoadFailed += (_, arguments) =>
                    problems.Add($"{path}：{arguments.Message}");
                generators.AddRange(reference.GetGenerators(LanguageNames.CSharp));
            }

            if (problems.Count > 0)
            {
                throw new AnalysisException(
                    $"无法读取 Unity 分析器：{string.Join("; ", problems)}");
            }

            return generators.ToArray();
        }

        // 建立不同程序集下源码文件的唯一查找键。
        private static string SourceKey(string assemblyName, string sourcePath)
        {
            return $"{assemblyName}\0{sourcePath}";
        }

        // 把响应文件中的路径解析为工程内实际绝对路径。
        private static string ResolvePath(string projectRoot, string path)
        {
            string unquotedPath = path.Trim('"');

            return Path.GetFullPath(Path.IsPathRooted(unquotedPath)
                ? unquotedPath
                : Path.Combine(projectRoot, unquotedPath));
        }

        private sealed record CompilerReference(
            string Path,
            MetadataReferenceProperties Properties);

        private sealed record SourceReference(
            string AssemblyName,
            string ReferencePath,
            string ResponsePath);

        private sealed record SourceInput(
            string AssemblyName,
            string Path,
            CSharpParseOptions ParseOptions);

        private sealed record CompilerResponse(
            string AssemblyName,
            CSharpParseOptions ParseOptions,
            CSharpCompilationOptions CompilationOptions,
            string OutputPath,
            EmitOptions EmitOptions,
            IReadOnlyList<string> SourcePaths,
            IReadOnlyList<CompilerReference> References,
            IReadOnlyList<string> AnalyzerPaths,
            IReadOnlyList<string> AdditionalFilePaths,
            IReadOnlyList<SourceReference> SourceReferences);

        private sealed class DiskAdditionalText : AdditionalText
        {
            // 保存 Unity 响应文件给出的附加文件绝对路径。
            public DiskAdditionalText(string path)
            {
                this.Path = path;
            }

            public override string Path { get; }

            // 在源码生成器请求时读取附加文件文本。
            public override SourceText GetText(CancellationToken cancellationToken = default)
            {
                return SourceText.From(File.ReadAllText(this.Path), Encoding.UTF8);
            }
        }

        private sealed record UnityRuntimeSelection(
            IReadOnlyList<string> ReferenceRoots,
            IReadOnlyList<string> AssemblyPaths,
            IReadOnlyList<string> FacadePaths,
            IReadOnlyDictionary<string, UnityFrameworkRemapping> FrameworkRemappings);

        private sealed record UnityFrameworkRemapping(
            Version RuntimeVersion,
            bool OnlyLowerVersions);

        private sealed record ExternalMaterialResolution(
            IReadOnlyList<ExternalAssemblyMaterial> Assemblies,
            IReadOnlyList<string> LookupPaths,
            IReadOnlyList<string> UnityRuntimeFacadePaths,
            IReadOnlyDictionary<string, string> AssemblyRedirects);

        private sealed record AssemblyReferenceIdentity(
            string Name,
            Version Version,
            string Culture,
            ImmutableArray<byte> PublicKeyOrToken,
            bool UsesFullPublicKey)
        {
            public string Key { get; } = $"{Name}|{Version}|{Culture}|{Convert.ToHexString(PublicKeyOrToken.AsSpan())}";
        }

        private sealed class AnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
        {
            private readonly AssemblyLoadContext m_context = new(
                "SetterCheckerAnalyzers",
                isCollectible: true);
            private readonly Dictionary<string, string> m_paths = new(
                StringComparer.OrdinalIgnoreCase);

            // 建立不会锁住 Unity 文件的分析器加载环境。
            public AnalyzerAssemblyLoader()
            {
                this.m_context.Resolving += this.ResolveDependency;
            }

            // 记录分析器可能引用的同组文件。
            public void AddDependencyLocation(string fullPath)
            {
                string? assemblyName = AssemblyName.GetAssemblyName(fullPath).Name;

                if (assemblyName != null)
                {
                    this.m_paths[assemblyName] = fullPath;
                }
            }

            // 在当前工具进程中加载 Unity 指定的分析器文件。
            public Assembly LoadFromPath(string fullPath)
            {
                using FileStream stream = File.OpenRead(fullPath);

                return this.m_context.LoadFromStream(stream);
            }

            // 从同组分析器文件解析加载时依赖。
            private Assembly? ResolveDependency(
                AssemblyLoadContext context,
                AssemblyName assemblyName)
            {
                if (assemblyName.Name == null
                    || !this.m_paths.TryGetValue(assemblyName.Name, out string? path))
                {
                    return null;
                }

                using FileStream stream = File.OpenRead(path);

                return context.LoadFromStream(stream);
            }
        }
    }
}
