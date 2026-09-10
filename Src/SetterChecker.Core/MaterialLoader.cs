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
            string assemblyName = ReadAssemblyDefinition(assemblyDefinitionPath).Name;
            string responsePath = FindRootResponse(projectRoot, assemblyName);
            IReadOnlyList<CompilerResponse> responses = ReadResponseClosure(
                projectRoot,
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
            ILookup<string, CompilerReference> sourceConsumers = responses.SelectMany(response => response.SourceReferences).Distinct()
                .Join(responses.SelectMany(response => response.References).Distinct(), source => source.ReferencePath, reference => reference.Path,
                    (source, reference) => (source.AssemblyName, Reference: reference), StringComparer.OrdinalIgnoreCase)
                .ToLookup(item => item.AssemblyName, item => item.Reference, StringComparer.Ordinal);
            Dictionary<string, SourceAssemblyMaterial> builtAssemblies = new(StringComparer.Ordinal);
            List<CompilerResponse> remaining = responses.ToList();
            List<Task<SourceAssemblyMaterial>> running = new();
            Stopwatch compilationWatch = Stopwatch.StartNew();
            try
            {
                while (remaining.Count != 0 || running.Count != 0)
                {
                    if (running.Any(task => task.IsFaulted || task.IsCanceled))
                    {
                        await Task.WhenAll(running).ConfigureAwait(false);
                    }
                    CompilerResponse[] ready = remaining.Where(response => response.SourceReferences
                        .All(reference => builtAssemblies.ContainsKey(reference.AssemblyName)))
                        .Take(request.Jobs - running.Count).ToArray();
                    if (ready.Length == 0 && running.Count == 0)
                    {
                        throw new AnalysisException("源码程序集存在循环依赖，不能使用旧参考拆开编译："
                            + string.Join("; ", remaining.Select(response => response.AssemblyName)));
                    }

                    foreach (CompilerResponse response in ready)
                    {
                        PortableExecutableReference[] references = response.References.Select(reference => metadataReferences[reference]).ToArray();
                        remaining.Remove(response);
                        running.Add(Task.Run(() =>
                        {
                            SourceAssemblyMaterial source = BuildSourceAssembly(response, reportRoot,
                                trees, references, generatorsByAnalyzerSet[AnalyzerSetKey(response.AnalyzerPaths)], cancellationToken);
                            using MemoryStream image = new();
                            EmitResult result = source.Compilation.Emit(image, options: response.EmitOptions, cancellationToken: cancellationToken);
                            if (!result.Success)
                            {
                                throw new AnalysisException($"当前源码编译失败：{source.Name} => "
                                    + string.Join("; ", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
                            }

                            return source with { AssemblyImage = image.ToArray() };
                        }, cancellationToken));
                    }
                    Task<SourceAssemblyMaterial> completed = await Task.WhenAny(running).ConfigureAwait(false);
                    if (!completed.IsCompletedSuccessfully)
                    {
                        await Task.WhenAll(running).ConfigureAwait(false);
                    }
                    SourceAssemblyMaterial finished = await completed.ConfigureAwait(false);
                    running.Remove(completed);
                    builtAssemblies.Add(finished.Name, finished);
                    foreach (CompilerReference reference in sourceConsumers[finished.Name].Distinct())
                    {
                        metadataReferences.Add(reference, MetadataReference.CreateFromImage(finished.AssemblyImage,
                            reference.Properties, filePath: finished.AssemblyPath));
                    }
                }
            }
            finally
            {
                // 正常结束时队列为空；失败时只收拢其他任务，原始异常仍由主流程抛出。
                await ((Task)Task.WhenAll(running)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            compilationWatch.Stop();
            IReadOnlySet<string> editorOnlyAssemblies = ReadEditorOnlyReportAssemblies(reportRoot);
            SourceAssemblyMaterial[] sourceAssemblies = builtAssemblies.Values
                .Select(source => editorOnlyAssemblies.Contains(source.Name)
                    ? source with { IsReportAssembly = false, ReportSourcePaths = Array.Empty<string>() } : source)
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
                sourceAssemblies.Select(source => source.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
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
                AssemblyRedirects = external.AssemblyRedirects,
                UsesUnityLegacyBinding = external.UsesUnityLegacyBinding,
                ExplicitRuntimeAssemblyPaths = external.ExplicitRuntimeAssemblyPaths,
            };
        }

        // 只排除 asmdef 明确声明仅用于编辑器的报告程序集，不删除分析材料。
        private static IReadOnlySet<string> ReadEditorOnlyReportAssemblies(string reportRoot)
        {
            Dictionary<string, (bool EditorOnly, string Path)> definitions = new(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(reportRoot, "*.asmdef", SearchOption.AllDirectories))
            {
                var definition = ReadAssemblyDefinition(path);
                if (!definitions.TryAdd(definition.Name, (definition.EditorOnly, path)))
                {
                    throw new AnalysisException($"报告包内程序集定义名称重复：{definition.Name}；{definitions[definition.Name].Path}；{path}");
                }
            }
            return definitions.Where(pair => pair.Value.EditorOnly).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        }

        // 从输入程序集定义向上找到包含 package.json 的目标包目录。
        private static string FindReportRoot(string assemblyDefinitionPath, string projectRoot)
        {
            return ReadParentDirectories(assemblyDefinitionPath).TakeWhile(directory => IsUnderDirectory(directory.FullName, projectRoot))
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory.FullName, "package.json")))?.FullName
                ?? throw new AnalysisException($"程序集定义不属于一个有 package.json 的 Unity 包：{assemblyDefinitionPath}");
        }

        // 把只有声明的外部参考文件连接到 Unity 当前真实输出文件。
        private static async Task<ExternalMaterialResolution> ResolveExternalAssembliesAsync(
            string projectRoot,
            IReadOnlyList<string> referencePaths,
            IReadOnlySet<string> sourceAssemblyNames,
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
            HashSet<string>? explicitPaths = unityRuntime == null ? null : assemblies
                .Where(assembly => CanBeAssemblyLookupPath(assembly.ReferencePath, unityRuntime)
                    && !assembly.ReferencePath.EndsWith(".ref.dll", StringComparison.OrdinalIgnoreCase))
                .Select(assembly => assembly.ReferencePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName = IndexAssemblyPaths(
                knownPaths.Where(path => CanBeAssemblyLookupPath(path, unityRuntime)), explicitPaths);
            IReadOnlyDictionary<string, string> assemblyRedirects = ReadAssemblyRedirects(
                assemblies,
                knownPaths,
                pathsByAssemblyName,
                sourceAssemblyNames,
                unityRuntime);
            ExternalAssemblyMaterial[] resolved = new ExternalAssemblyMaterial[assemblies.Length];
            ConcurrentDictionary<string, Lazy<IReadOnlyList<AssemblyName>>> forwardedAssembliesByPath =
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
                assemblyRedirects,
                unityRuntime != null,
                explicitPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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
            UnityRuntimeSelection? unityRuntime)
        {
            return MatchesIdentity(implementationPath, AssemblyName.GetAssemblyName(referencePath),
                unityRuntime, allowUnspecifiedVersion: false, useFullPublicKey: false);
        }

        // 核对参考文件与真实实现声明的是同一个程序集。
        private static void RequireMatchingAssemblyIdentity(
            string referencePath,
            string implementationPath,
            UnityRuntimeSelection? unityRuntime)
        {
            if (!MatchesReferenceCandidate(referencePath, implementationPath, unityRuntime))
            {
                throw new AnalysisException(
                    $"参考文件与真实文件的程序集身份不同：{referencePath} => {implementationPath}");
            }
        }

        // 比较程序集的名称、区域和公钥标记。
        internal static bool SameAssemblyNameCultureAndToken(
            AssemblyName expected,
            AssemblyName candidate,
            bool useFullPublicKey = false)
        {
            byte[] expectedKey = (useFullPublicKey ? expected.GetPublicKey() : expected.GetPublicKeyToken()) ?? Array.Empty<byte>();
            byte[] candidateKey = (useFullPublicKey ? candidate.GetPublicKey() : candidate.GetPublicKeyToken()) ?? Array.Empty<byte>();

            return string.Equals(expected.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    expected.CultureName ?? string.Empty,
                    candidate.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && expectedKey.SequenceEqual(candidateKey);
        }

        // 只有全部实际候选唯一时，保存不依赖引用方位置的运行身份对应。
        private static IReadOnlyDictionary<string, string> ReadAssemblyRedirects(
            IEnumerable<ExternalAssemblyMaterial> assemblies,
            IReadOnlyList<string> lookupPaths,
            IReadOnlyDictionary<string, IReadOnlyList<string>> runtimePaths,
            IReadOnlySet<string> sourceAssemblyNames,
            UnityRuntimeSelection? unityRuntime)
        {
            Dictionary<string, string> redirects = new(StringComparer.OrdinalIgnoreCase);
            if (unityRuntime == null)
            {
                return redirects;
            }

            IEnumerable<AssemblyName> references = assemblies.Select(assembly =>
                    AssemblyName.GetAssemblyName(assembly.ReferencePath))
                .Concat(lookupPaths.SelectMany(ReadReferencedAssemblyNames))
                .DistinctBy(reference => reference.FullName, StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyName reference in references)
            {
                if (sourceAssemblyNames.Contains(reference.Name!)
                    || !runtimePaths.TryGetValue(reference.Name!, out IReadOnlyList<string>? candidates)
                    || candidates.Count != 1 || !unityRuntime.AssemblyPaths.Contains(candidates[0], StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(reference.FullName, AssemblyName.GetAssemblyName(candidates[0]).FullName, StringComparison.OrdinalIgnoreCase))
                {
                    redirects.Add(reference.FullName!, candidates[0]);
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
            ConcurrentDictionary<string, Lazy<IReadOnlyList<AssemblyName>>> forwardedAssembliesByPath)
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
                IReadOnlyList<AssemblyName> forwardedAssemblies = forwardedAssembliesByPath.GetOrAdd(
                    normalizedPath,
                    static currentPath => new Lazy<IReadOnlyList<AssemblyName>>(
                        () => ReadForwardedAssemblies(currentPath),
                        LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                foreach (AssemblyName identity in forwardedAssemblies)
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
        private static IReadOnlyList<AssemblyName> ReadForwardedAssemblies(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);
            MetadataReader metadata = portableExecutable.GetMetadataReader();
            return metadata.ExportedTypes.Select(handle => ReadExportedTypeImplementation(metadata, handle))
                .Where(implementation => implementation.Kind == HandleKind.AssemblyReference)
                .Select(implementation => metadata.GetAssemblyReference((AssemblyReferenceHandle)implementation).GetAssemblyName())
                .GroupBy(ReadAssemblyReferenceKey, StringComparer.OrdinalIgnoreCase).Select(group => group.Last())
                .OrderBy(ReadAssemblyReferenceKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // 保留转交引用的原始公钥表示，完整公钥与 token 不能在索引时相互覆盖。
        private static string ReadAssemblyReferenceKey(AssemblyName identity)
        {
            bool fullKey = (identity.Flags & AssemblyNameFlags.PublicKey) != 0;
            byte[] key = (fullKey ? identity.GetPublicKey() : identity.GetPublicKeyToken()) ?? Array.Empty<byte>();
            return $"{identity.Name}|{identity.Version}|{identity.CultureName}|{fullKey}|{Convert.ToHexString(key)}";
        }

        // 在转交文件明确可达的位置寻找唯一已有目标程序集。
        private static string? FindForwardedAssembly(
            string forwardingPath,
            AssemblyName identity,
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName,
            UnityRuntimeSelection? unityRuntime)
        {
            DirectoryInfo directory = new(Path.GetDirectoryName(forwardingPath)!);
            HashSet<string> candidates = pathsByAssemblyName.TryGetValue(
                identity.Name!,
                out IReadOnlyList<string>? knownPaths)
                    ? knownPaths
                        .Where(path => unityRuntime != null || MatchesIdentity(path, identity, unityRuntime))
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
                _ when unityRuntime != null => null,
                _ => throw new AnalysisException(
                    $"类型转交目标不唯一：{forwardingPath} => {identity.Name}：{string.Join("; ", candidates)}"),
            };
        }

        // 严格宿主使用元数据名；当前 Unity 允许文件名探测及显式运行引用的预载名称。
        internal static IReadOnlyDictionary<string, IReadOnlyList<string>> IndexAssemblyPaths(
            IEnumerable<string> paths, IReadOnlySet<string>? explicitRuntimePaths)
        {
            return paths
                .Where(path => explicitRuntimePaths == null || !path.EndsWith(".ref.dll", StringComparison.OrdinalIgnoreCase))
                .SelectMany(path => (explicitRuntimePaths == null ? new[] { AssemblyName.GetAssemblyName(path).Name }
                    : explicitRuntimePaths.Contains(path) ? new[] { Path.GetFileNameWithoutExtension(path), AssemblyName.GetAssemblyName(path).Name }
                    : new[] { Path.GetFileNameWithoutExtension(path) }).Select(name => (Name: name, Path: path)))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key != null)
                .ToDictionary(
                    group => group.Key!,
                    group => (IReadOnlyList<string>)group.Select(item => item.Path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        // 在候选文件存在时加入目标集合。
        private static void AddCandidate(
            ISet<string> candidates,
            string path,
            AssemblyName identity,
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
            AssemblyName identity,
            UnityRuntimeSelection? unityRuntime,
            bool allowUnspecifiedVersion = true,
            bool? useFullPublicKey = null)
        {
            if (unityRuntime != null)
            {
                return string.Equals(Path.GetFileNameWithoutExtension(path), identity.Name, StringComparison.OrdinalIgnoreCase);
            }
            AssemblyName candidate = AssemblyName.GetAssemblyName(path);
            bool versionMatches = allowUnspecifiedVersion && identity.Version == new Version(0, 0, 0, 0)
                || Equals(identity.Version, candidate.Version);

            return SameAssemblyNameCultureAndToken(identity, candidate,
                    useFullPublicKey ?? (identity.Flags & AssemblyNameFlags.PublicKey) != 0)
                && versionMatches;
        }

        // 核对门面只含类型转交，不用参考桩补出实际运行库没有的行为。
        private static bool IsPureForwardingFacade(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);
            MetadataReader metadata = portableExecutable.GetMetadataReader();
            TypeDefinitionHandle[] definitions = metadata.TypeDefinitions.ToArray();

            return metadata.ExportedTypes.Count > 0
                && definitions.Length == 1
                && metadata.GetString(metadata.GetTypeDefinition(definitions[0]).Name) == "<Module>"
                && metadata.ExportedTypes.All(handle => ReadExportedTypeImplementation(metadata, handle).Kind == HandleKind.AssemblyReference);
        }

        // 嵌套导出类型沿父记录找到实际承载位置，调用处再核对是否为程序集。
        private static EntityHandle ReadExportedTypeImplementation(MetadataReader metadata, ExportedTypeHandle handle)
        {
            EntityHandle implementation = metadata.GetExportedType(handle).Implementation;
            while (implementation.Kind == HandleKind.ExportedType)
            {
                implementation = metadata.GetExportedType((ExportedTypeHandle)implementation).Implementation;
            }
            return implementation;
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
            string hostPath = Path.Combine(Path.GetDirectoryName(dataDirectory)!, "Unity.exe");
            if (!File.Exists(hostPath) || FileVersionInfo.GetVersionInfo(hostPath) is not
                { FileVersion: "2021.3.16.0", ProductVersion: "2021.3.16f1_0" }
                || !File.Exists(Path.Combine(dataDirectory, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll")))
            {
                throw new AnalysisException($"Unity 宿主装载规则尚未审计：{hostPath}");
            }
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
            return new UnityRuntimeSelection(
                new[]
                {
                    Path.Combine(dataDirectory, "NetStandard"),
                    Path.Combine(dataDirectory, "UnityReferenceAssemblies"),
                },
                assemblyPaths);
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
            return ReadParentDirectories(referencePath).FirstOrDefault(directory =>
                string.Equals(directory.Name, "Data", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(directory.FullName, "MonoBleedingEdge")))?.FullName;
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
            return ReadParentDirectories(assemblyDefinitionPath).FirstOrDefault(directory =>
                Directory.Exists(Path.Combine(directory.FullName, "Library", "Bee", "artifacts")))?.FullName
                ?? throw new AnalysisException($"没有找到 Unity 当前生成的 Library/Bee/artifacts：{assemblyDefinitionPath}");
        }

        // 从文件所在目录开始逐级向上，共用工程、目标包与运行时目录的查找顺序。
        private static IEnumerable<DirectoryInfo> ReadParentDirectories(string filePath)
        {
            for (DirectoryInfo? directory = new FileInfo(filePath).Directory; directory != null; directory = directory.Parent)
            {
                yield return directory;
            }
        }

        // 读取真实程序集名称及明确的编辑器专用平台声明。
        private static (string Name, bool EditorOnly) ReadAssemblyDefinition(string assemblyDefinitionPath)
        {
            try
            {
                using FileStream stream = File.OpenRead(assemblyDefinitionPath);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("name", out JsonElement nameElement)
                    || nameElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    throw new AnalysisException($"程序集定义没有有效名称：{assemblyDefinitionPath}");
                }
                bool hasPlatforms = document.RootElement.TryGetProperty("includePlatforms", out JsonElement platforms);
                if (hasPlatforms && (platforms.ValueKind != JsonValueKind.Array || platforms.EnumerateArray().Any(
                    platform => platform.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(platform.GetString()))))
                {
                    throw new AnalysisException($"程序集平台声明不是有效字符串列表：{assemblyDefinitionPath}");
                }
                return (nameElement.GetString()!, hasPlatforms && platforms.GetArrayLength() == 1 && platforms[0].GetString() == "Editor");
            }
            catch (JsonException exception)
            {
                throw new AnalysisException($"程序集定义不是有效 JSON：{assemblyDefinitionPath}；{exception.Message}");
            }
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

            string buildLogPath = Path.Combine(projectRoot, "Library", "Bee", "tundra.log.json");
            if (File.Exists(buildLogPath))
            {
                using JsonDocument buildLog = ReadBuildLogHeader(buildLogPath);
                JsonElement initialization = buildLog.RootElement;
                if (initialization.ValueKind != JsonValueKind.Object
                    || !initialization.TryGetProperty("msg", out JsonElement message) || message.ValueKind != JsonValueKind.String
                    || !initialization.TryGetProperty("dagFile", out JsonElement graph) || graph.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(graph.GetString())
                    || !initialization.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Array
                    || targets.EnumerateArray().Any(target => target.ValueKind != JsonValueKind.String))
                {
                    throw new AnalysisException($"Unity 构建记录首行缺少有效的 msg、dagFile 或 targets：{buildLogPath}");
                }
                if (message.GetString() != "init"
                    || !targets.EnumerateArray()
                        .Any(target => target.GetString() == "ScriptAssemblies"))
                {
                    throw new AnalysisException($"Unity 当前构建记录不是脚本程序集构建：{buildLogPath}");
                }

                string graphPath = Path.GetFullPath(Path.Combine(projectRoot, graph.GetString()!));
                if (!string.Equals(Path.GetDirectoryName(graphPath), Path.GetDirectoryName(buildLogPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new AnalysisException($"Unity 构建图不在当前项目的 Bee 目录：{graphPath}");
                }
                if (!File.Exists(graphPath))
                {
                    throw new AnalysisException($"Unity 当前构建图不存在：{graphPath}；构建记录：{buildLogPath}");
                }

                string recordedResponse = Path.Combine(artifactsPath, Path.GetFileName(graphPath), $"{assemblyName}.rsp");
                return candidates.SingleOrDefault(path => string.Equals(path, recordedResponse, StringComparison.OrdinalIgnoreCase))
                    ?? throw new AnalysisException($"Unity 当前构建缺少响应文件：{recordedResponse}；构建记录：{buildLogPath}");
            }

            return candidates.Length switch
            {
                1 => candidates[0],
                0 => throw new AnalysisException(
                    $"没有找到 {assemblyName} 当前使用的 Unity 编译响应文件。"),
                _ => throw new AnalysisException(
                    $"找到多份 {assemblyName} 编译响应文件：{string.Join("; ", candidates)}"),
            };
        }

        // 读取当前构建记录的首行，格式错误时保留确切文件位置并停止。
        private static JsonDocument ReadBuildLogHeader(string path)
        {
            try
            {
                return JsonDocument.Parse(File.ReadLines(path).FirstOrDefault() ?? string.Empty);
            }
            catch (JsonException exception)
            {
                throw new AnalysisException($"Unity 构建记录首行不是完整 JSON：{path}；{exception.Message}");
            }
        }

        // 当前构建的全部源码都可能提供接口实现或回调；引用关系只认本次构建声明的产物。
        private static IReadOnlyList<CompilerResponse> ReadResponseClosure(
            string projectRoot,
            string rootResponsePath)
        {
            IReadOnlyDictionary<string, string> outputs = ReadBuildOutputs(projectRoot, rootResponsePath);
            return outputs.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                CompilerResponse response = ReadResponse(projectRoot, Path.GetFileNameWithoutExtension(path), path, outputs);
                if (!outputs.TryGetValue(response.OutputPath, out string? owner)
                    || !string.Equals(owner, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AnalysisException($"编译输出不属于当前构建节点：{path} => {response.OutputPath}");
                }
                return response;
            }).ToArray();
        }

        // 使用 Unity 本次 Csc 节点的产物定位源码，不按相邻文件或库名推测构建关系。
        private static IReadOnlyDictionary<string, string> ReadBuildOutputs(string projectRoot, string rootResponsePath)
        {
            string graphPath = Path.Combine(projectRoot, "Library", "Bee",
                Path.GetFileName(Path.GetDirectoryName(rootResponsePath)!) + ".json");
            RequireFiles(new[] { graphPath }, "当前构建图的 JSON", rootResponsePath);
            using JsonDocument graph = JsonDocument.Parse(File.ReadAllText(graphPath));
            var nodes = graph.RootElement.GetProperty("Nodes").EnumerateArray()
                .Where(node => node.GetProperty("Annotation").GetString()!.StartsWith("Csc ", StringComparison.Ordinal))
                .Select(node => new
                {
                    Inputs = node.GetProperty("Inputs").EnumerateArray().Select(value => ResolvePath(projectRoot, value.GetString()!)).ToArray(),
                    Outputs = node.GetProperty("Outputs").EnumerateArray().Select(value => ResolvePath(projectRoot, value.GetString()!)).ToArray(),
                }).Select(node => new
                {
                    node.Outputs,
                    Response = node.Inputs.Single(path => path.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase)),
                }).ToArray();
            if (!nodes.Any(node => string.Equals(node.Response, rootResponsePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AnalysisException($"当前构建图未声明根编译：{graphPath} => {rootResponsePath}");
            }
            return nodes.SelectMany(node => node.Outputs.Select(path => (Path: path, node.Response)))
                .ToDictionary(item => item.Path, item => item.Response, StringComparer.OrdinalIgnoreCase);
        }

        // 解析一份 Unity 编译响应文件并核对其中的真实路径。
        private static CompilerResponse ReadResponse(
            string projectRoot,
            string assemblyName,
            string responsePath,
            IReadOnlyDictionary<string, string> buildOutputs)
        {
            CSharpCommandLineArguments arguments = CSharpCommandLineParser.Default.Parse(
                new[] { $"@{responsePath}" },
                projectRoot,
                sdkDirectory: null);
            RequireNoErrors(arguments.Errors, $"无法读取 Unity 编译响应文件 {responsePath}");

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
                .Where(reference => buildOutputs.ContainsKey(reference.Path))
                .Select(reference => new SourceReference(Path.GetFileNameWithoutExtension(buildOutputs[reference.Path]),
                    reference.Path))
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
            IReadOnlyDictionary<string, SyntaxTree> trees,
            IReadOnlyList<PortableExecutableReference> references,
            IReadOnlyList<ISourceGenerator> generators,
            CancellationToken cancellationToken)
        {
            SyntaxTree[] assemblyTrees = response.SourcePaths
                .Select(path => trees[SourceKey(response.AssemblyName, path)])
                .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(
                response.AssemblyName,
                assemblyTrees,
                references,
                response.CompilationOptions.WithConcurrentBuild(false));
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
                RequireNoErrors(diagnostics, "源码生成器执行失败");

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

        // 编译响应和源码生成共用错误诊断检查，保留原错误内容。
        private static void RequireNoErrors(IEnumerable<Diagnostic> diagnostics, string context)
        {
            Diagnostic[] errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0)
            {
                throw new AnalysisException($"{context}：{string.Join("; ", errors.AsEnumerable())}");
            }
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
            string ReferencePath);

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
            IReadOnlyList<string> AssemblyPaths);

        private sealed record ExternalMaterialResolution(
            IReadOnlyList<ExternalAssemblyMaterial> Assemblies,
            IReadOnlyList<string> LookupPaths,
            IReadOnlyDictionary<string, string> AssemblyRedirects,
            bool UsesUnityLegacyBinding,
            IReadOnlySet<string> ExplicitRuntimeAssemblyPaths);

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

                return LoadFromPath(path);
            }
        }
    }
}
