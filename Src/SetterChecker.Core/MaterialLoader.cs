using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
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
        private readonly SemaphoreSlim m_requests = new(1);
        private IReadOnlyDictionary<string, CachedCompilation> m_compilations = new Dictionary<string, CachedCompilation>();
        private IReadOnlyDictionary<string, CachedMetadata> m_metadataReferences = new Dictionary<string, CachedMetadata>(StringComparer.OrdinalIgnoreCase);

        // 按当前 Unity 编译参数建立后续模块唯一使用的材料集合。
        /// <summary>
        /// 读取当前编译上下文；取消使整轮快照失效，重试应重新调用本方法。
        /// </summary>
        public async Task<MaterialSet> LoadAsync(
            MaterialRequest request,
            CancellationToken cancellationToken = default)
        {
            request = request with { SourceTexts = request.SourceTexts.ToDictionary(pair => Path.GetFullPath(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase) };
            await this.m_requests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 核对本次实际输入，只有内容一致才复用同会话的编译上下文。
                Stopwatch stopwatch = Stopwatch.StartNew();
                Dictionary<string, double> timings = new();
                Stopwatch stage = Stopwatch.StartNew();

                ValidateRequest(request);
                string[] assemblyDefinitionPaths = request.AssemblyDefinitionPaths.Select(Path.GetFullPath).ToArray();
                string projectRoot = FindProjectRoot(assemblyDefinitionPaths[0]);
                Dictionary<string, string> reportRoots = new(StringComparer.Ordinal);
                foreach (string path in assemblyDefinitionPaths)
                {
                    if (!string.Equals(FindProjectRoot(path), projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AnalysisException($"报告程序集不属于同一个 Unity 工程：{path}；{projectRoot}");
                    }
                    string name = ReadAssemblyDefinition(path).Name;
                    if (!reportRoots.TryAdd(name, FindReportRoot(path, projectRoot)))
                    {
                        throw new AnalysisException($"重复指定报告程序集：{name}");
                    }
                    FindRootResponse(projectRoot, name);
                }
                string assemblyName = ReadAssemblyDefinition(assemblyDefinitionPaths[0]).Name;
                string responsePath = FindRootResponse(projectRoot, assemblyName);
                HashSet<string> editorOnlyAssemblies = new(StringComparer.Ordinal) { "Assembly-CSharp-Editor", "Assembly-CSharp-Editor-firstpass" };
                IReadOnlyList<CompilerResponse> responses = ReadResponseClosure(
                    projectRoot,
                    responsePath,
                    request.SourceTexts,
                    request.Jobs,
                    editorOnlyAssemblies,
                    cancellationToken);
                if (reportRoots.Keys.FirstOrDefault(editorOnlyAssemblies.Contains) is string editorReport)
                {
                    throw new AnalysisException($"分析目标被声明为编辑器或测试程序集：{editorReport}");
                }
                editorOnlyAssemblies.IntersectWith(responses.Select(response => response.AssemblyName));
                responses = SelectPlayerAssemblies(responses, editorOnlyAssemblies);
                timings.Add("编译参数读取", stage.Elapsed.TotalSeconds);
                stage.Restart();
                IReadOnlyDictionary<string, ParsedSource> trees = await ParseSourcesAsync(
                    responses,
                    this.m_compilations,
                    request.SourceTexts,
                    request.Jobs,
                    cancellationToken).ConfigureAwait(false);
                timings.Add("源码读取解析", stage.Elapsed.TotalSeconds);
                stage.Restart();
                Dictionary<string, string> generatorInputs = responses.SelectMany(response => response.AnalyzerPaths.Concat(response.AdditionalFilePaths))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
                        path => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
                ConcurrentDictionary<string, Lazy<ISourceGenerator[]>> generators = new(StringComparer.Ordinal);
                timings.Add("生成器输入读取", stage.Elapsed.TotalSeconds);
                stage.Restart();
                Dictionary<CompilerReference, (MetadataReference Reference, string Key)> metadataReferences =
                    CreateMetadataReferences(responses, request.Jobs, cancellationToken);
                timings.Add("引用读取", stage.Elapsed.TotalSeconds);
                ILookup<string, CompilerReference> sourceConsumers = responses.SelectMany(response => response.References)
                    .Where(reference => reference.SourceAssemblyName != null).Distinct()
                    .ToLookup(reference => reference.SourceAssemblyName!, StringComparer.Ordinal);
                Dictionary<string, CachedCompilation> builtAssemblies = new(StringComparer.Ordinal);
                List<CompilerResponse> remaining = responses.ToList();
                List<Task<CachedCompilation>> running = new();
                Stopwatch compilationWatch = Stopwatch.StartNew();
                try
                {
                    while (remaining.Count != 0 || running.Count != 0)
                    {
                        if (running.Any(task => task.IsFaulted || task.IsCanceled))
                        {
                            await Task.WhenAll(running).ConfigureAwait(false);
                        }
                        CompilerResponse[] ready = remaining.Where(response => response.References
                            .All(reference => reference.SourceAssemblyName == null || builtAssemblies.ContainsKey(reference.SourceAssemblyName)))
                            .Take(request.Jobs - running.Count).ToArray();
                        if (ready.Length == 0 && running.Count == 0)
                        {
                            throw new AnalysisException("源码程序集存在循环依赖，不能使用旧参考拆开编译："
                                + string.Join("; ", remaining.Select(response => response.AssemblyName)));
                        }

                        foreach (CompilerResponse response in ready)
                        {
                            MetadataReference[] references = response.References.Select(reference => metadataReferences[reference].Reference).ToArray();
                            string[] referenceInputs = response.References.Select(reference => metadataReferences[reference].Key).ToArray();
                            remaining.Remove(response);
                            running.Add(Task.Run(() =>
                            {
                                string key = ReadCompilationKey(response, trees, referenceInputs, generatorInputs);
                                bool reused = this.m_compilations.TryGetValue(response.AssemblyName, out CachedCompilation? previous) && previous.Key == key;
                                CSharpCompilation compilation = reused ? previous!.Material.Compilation : BuildCompilation(response,
                                    trees, references, generators.GetOrAdd(string.Join('\0', response.AnalyzerPaths),
                                        _ => new Lazy<ISourceGenerator[]>(() => LoadGenerators(response.AnalyzerPaths))).Value, cancellationToken);
                                string[] reportPaths = !reportRoots.TryGetValue(response.AssemblyName, out string? reportRoot) ? Array.Empty<string>()
                                    : response.SourcePaths.Where(path => IsUnderDirectory(path, reportRoot)).ToArray();
                                SourceAssemblyMaterial source = new(response.AssemblyName, reportPaths.Length > 0, compilation,
                                    response.SourcePaths, reportPaths, response.OutputPath,
                                    reused ? CompilationOrigin.Session : CompilationOrigin.Built)
                                {
                                    IsCandidateSource = !editorOnlyAssemblies.Contains(response.AssemblyName),
                                };
                                return new CachedCompilation(key, source);
                            }, cancellationToken));
                        }
                        Task<CachedCompilation> completed = await Task.WhenAny(running).ConfigureAwait(false);
                        if (!completed.IsCompletedSuccessfully)
                        {
                            await Task.WhenAll(running).ConfigureAwait(false);
                        }
                        CachedCompilation compiled = await completed.ConfigureAwait(false);
                        SourceAssemblyMaterial finished = compiled.Material;
                        running.Remove(completed);
                        builtAssemblies.Add(finished.Name, compiled);
                        foreach (CompilerReference reference in sourceConsumers[finished.Name])
                        {
                            metadataReferences.Add(reference, (finished.Compilation.ToMetadataReference(
                                reference.Properties.Aliases, reference.Properties.EmbedInteropTypes), compiled.Key));
                        }
                    }
                }
                finally
                {
                    // 正常结束时队列为空；失败时只收拢其他任务，原始异常仍由主流程抛出。
                    await ((Task)Task.WhenAll(running)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                compilationWatch.Stop();
                stage.Restart();
                SourceAssemblyMaterial[] sourceAssemblies = builtAssemblies.Values.Select(compiled => compiled.Material)
                    .OrderBy(assembly => assembly.Name, StringComparer.Ordinal).ToArray();
                string[] externalAssemblyPaths = responses
                    .SelectMany(response => response.References)
                    .Where(reference => reference.SourceAssemblyName == null)
                    .Select(reference => reference.Path)
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
                timings.Add("外部实现解析", stage.Elapsed.TotalSeconds);
                string[] analyzerPaths = responses
                    .SelectMany(response => response.AnalyzerPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                string? baselineInputKey = null;
                if (request.ManualBaselinePath != null || request.CaptureManualBaseline)
                {
                    stage.Restart();
                    string[] dllInputs = external.LookupPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                        .AsParallel().WithDegreeOfParallelism(request.Jobs).Select(path =>
                        {
                            using FileStream stream = File.OpenRead(path);
                            return path + ":" + Convert.ToHexString(SHA512.HashData(stream));
                        }).Order(StringComparer.Ordinal).ToArray();
                    // 工具重新编译不应让同一份已确认人工基线无故失效；语义变更时手动提升此版本号。
                    string inputs = string.Join("\n", assemblyDefinitionPaths) + "\n" + "setterchecker-v2-baseline-1" + "\n"
                        + string.Join("\n", builtAssemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value.Key))
                        + "\n" + string.Join("\n", dllInputs);
                    baselineInputKey = Convert.ToHexString(SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(inputs)));
                    timings.Add("人工基线材料核验", stage.Elapsed.TotalSeconds);
                }
                stopwatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                this.m_compilations = builtAssemblies;

                return new MaterialSet(
                    projectRoot,
                    sourceAssemblies,
                    external.Assemblies,
                    external.LookupPaths,
                    analyzerPaths,
                    stopwatch.Elapsed)
                {
                    CompilationElapsed = compilationWatch.Elapsed,
                    Timings = timings,
                    AssemblyRedirects = external.AssemblyRedirects,
                    UsesUnityLegacyBinding = external.UsesUnityLegacyBinding,
                    ExplicitRuntimeAssemblyPaths = external.ExplicitRuntimeAssemblyPaths,
                    BaselineInputKey = baselineInputKey,
                    ExcludedEditorAssemblies = editorOnlyAssemblies.Order(StringComparer.Ordinal).ToArray(),
                    CombatAssemblies = request.CombatAssemblies,
                };
            }
            finally
            {
                this.m_requests.Release();
            }
        }


        // 在建立上下文之前核对源码、生成器输入和引用内容，命中才复用整份编译结果。
        private static string ReadCompilationKey(CompilerResponse response, IReadOnlyDictionary<string, ParsedSource> trees,
            IEnumerable<string> referenceInputs, IReadOnlyDictionary<string, string> generatorInputs)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            IEnumerable<string> inputs = new[] { typeof(MaterialLoader).Module.ModuleVersionId.ToString(), typeof(CSharpCompilation).Module.ModuleVersionId.ToString(), response.AssemblyName, response.OutputPath }
                .Concat(response.Arguments).Concat(referenceInputs)
                .Append(response.CompilationOptions.GeneralDiagnosticOption.ToString())
                .Concat(response.CompilationOptions.SpecificDiagnosticOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => new[] { pair.Key, pair.Value.ToString() }))
                .Concat(response.SourcePaths.SelectMany(path => new[] { path, trees[SourceKey(response.AssemblyName, path)].ContentKey }))
                .Concat(response.AnalyzerPaths.Concat(response.AdditionalFilePaths).SelectMany(path => new[] { path, generatorInputs[path] }));
            foreach (string input in inputs)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] length = new byte[sizeof(int)];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        // R4：编辑器和测试程序集不进真机包；未被保留程序集引用的不再解析和编译，被引用的仅参与编译。
        private static IReadOnlyList<CompilerResponse> SelectPlayerAssemblies(IReadOnlyList<CompilerResponse> responses, IReadOnlySet<string> editorOnly)
        {
            Dictionary<string, CompilerResponse> byName = responses.ToDictionary(response => response.AssemblyName, StringComparer.Ordinal);
            HashSet<string> kept = responses.Where(response => !editorOnly.Contains(response.AssemblyName))
                .Select(response => response.AssemblyName).ToHashSet(StringComparer.Ordinal);
            Queue<string> pending = new(kept.Order(StringComparer.Ordinal));
            while (pending.TryDequeue(out string? name))
            {
                foreach (CompilerReference reference in byName[name].References)
                {
                    if (reference.SourceAssemblyName is string source && byName.ContainsKey(source) && kept.Add(source))
                    {
                        pending.Enqueue(source);
                    }
                }
            }
            return responses.Where(response => kept.Contains(response.AssemblyName)).ToArray();
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
                .OfType<string>()
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

            DirectoryInfo[] directories = string.Equals(directory.Name, "Facades", StringComparison.OrdinalIgnoreCase) && directory.Parent != null
                ? new[] { directory, directory.Parent } : new[] { directory };
            foreach (DirectoryInfo candidateDirectory in directories)
            {
                string path = Path.Combine(candidateDirectory.FullName, $"{identity.Name}.dll");
                if (File.Exists(path) && MatchesIdentity(path, identity, unityRuntime))
                {
                    candidates.Add(Path.GetFullPath(path));
                }
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
                .Select(reference => ReadParentDirectories(reference).FirstOrDefault(directory =>
                    string.Equals(directory.Name, "Data", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(directory.FullName, "MonoBleedingEdge")))?.FullName)
                .OfType<string>()
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

            if (request.AssemblyDefinitionPaths.Count == 0)
            {
                throw new AnalysisException("缺少报告程序集定义文件。");
            }

            foreach (string path in request.AssemblyDefinitionPaths)
            {
                if (!File.Exists(path))
                {
                    throw new AnalysisException($"程序集定义文件不存在：{path}");
                }

                if (!string.Equals(Path.GetExtension(path), ".asmdef", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AnalysisException($"输入必须是 Unity 程序集定义文件：{path}");
                }
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
        private static (string Name, bool EditorOnly, bool TestOnly) ReadAssemblyDefinition(string assemblyDefinitionPath)
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
                bool editorOnly = hasPlatforms && platforms.GetArrayLength() == 1 && platforms[0].GetString() == "Editor";
                bool testOnly = document.RootElement.TryGetProperty("defineConstraints", out JsonElement constraints)
                    && constraints.ValueKind == JsonValueKind.Array
                    && constraints.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                        && item.GetString() == "UNITY_INCLUDE_TESTS");
                return (nameElement.GetString()!, editorOnly, testOnly);
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
                return File.Exists(recordedResponse) ? recordedResponse
                    : throw new AnalysisException($"Unity 当前构建缺少响应文件：{recordedResponse}；构建记录：{buildLogPath}");
            }

            string[] candidates = Directory.EnumerateFiles(artifactsPath, $"{assemblyName}.rsp", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
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
            string rootResponsePath,
            IReadOnlyDictionary<string, string> sourceTexts,
            int jobs,
            HashSet<string> editorOnly,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, string> outputs = ReadBuildOutputs(projectRoot, rootResponsePath);
            Dictionary<string, string[]> responseArguments = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);
            CompilerResponse[] responses = outputs.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                CompilerResponse response = ReadResponse(projectRoot, Path.GetFileNameWithoutExtension(path), path, outputs, responseArguments);
                if (!outputs.TryGetValue(response.OutputPath, out string? owner)
                    || !string.Equals(owner, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AnalysisException($"编译输出不属于当前构建节点：{path} => {response.OutputPath}");
                }
                RequireFiles(response.References.Where(reference => reference.SourceAssemblyName == null)
                    .Select(reference => reference.Path).Where(checkedPaths.Add), "外部编译文件", path);
                RequireFiles(response.AnalyzerPaths.Where(checkedPaths.Add), "分析器", path);
                RequireFiles(response.AdditionalFilePaths.Where(checkedPaths.Add), "附加文件", path);
                return response;
            }).ToArray();
            return RefreshSourcePaths(projectRoot, responses, sourceTexts, jobs, editorOnly, cancellationToken);
        }

        // 沿当前资产目录和已参与编译的包重新归属源码，编译选项及引用仍来自构建节点；同时收集编辑器与测试程序集（R4）。
        private static IReadOnlyList<CompilerResponse> RefreshSourcePaths(string projectRoot, CompilerResponse[] responses,
            IReadOnlyDictionary<string, string> sourceTexts, int jobs, HashSet<string> editorOnly, CancellationToken cancellationToken)
        {
            HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
            string assets = Path.Combine(projectRoot, "Assets");
            if (Directory.Exists(assets))
            {
                roots.Add(assets);
            }

            foreach (string path in responses.SelectMany(response => response.SourcePaths))
            {
                string[] parts = Path.GetRelativePath(projectRoot, path).Replace('\\', '/').Split('/');
                int length = parts[0] == "Packages" ? 2 : parts.Length > 2 && parts[0] == "Library" && parts[1] == "PackageCache" ? 3 : 0;
                if (length > 0)
                {
                    roots.Add(Path.Combine(projectRoot, Path.Combine(parts.Take(length).ToArray())));
                }
            }
            // 逐层并行枚举单个目录，大目录树的子目录分给不同工作者；过滤规则与递归枚举一致。
            List<string> files = new();
            EnumerationOptions options = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };
            for (string[] level = roots.Order(StringComparer.OrdinalIgnoreCase).ToArray(); level.Length > 0;)
            {
                (string Path, bool IsDirectory)[][] entries = new (string, bool)[level.Length][];
                Parallel.For(0, level.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = cancellationToken },
                    index => entries[index] = new System.IO.Enumeration.FileSystemEnumerable<(string, bool)>(level[index],
                        (ref System.IO.Enumeration.FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory), options)
                    {
                        ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                            !entry.FileName.StartsWith(".", StringComparison.Ordinal) && (entry.IsDirectory
                                ? !entry.FileName.EndsWith("~", StringComparison.Ordinal)
                                : entry.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                    || entry.FileName.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
                                    || entry.FileName.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase)),
                    }.ToArray());
                files.AddRange(entries.SelectMany(items => items).Where(entry => !entry.IsDirectory).Select(entry => entry.Path));
                level = entries.SelectMany(items => items).Where(entry => entry.IsDirectory).Select(entry => entry.Path).ToArray();
            }

            Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> guids = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files.Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)))
            {
                var definition = ReadAssemblyDefinition(path);
                string name = definition.Name;
                if (definition.EditorOnly || definition.TestOnly)
                {
                    editorOnly.Add(name);
                }
                owners.Add(Path.GetDirectoryName(path)!, name);
                if (File.Exists(path + ".meta"))
                {
                    string? guid = File.ReadLines(path + ".meta").FirstOrDefault(line => line.StartsWith("guid: ", StringComparison.Ordinal));
                    if (guid != null)
                    {
                        guids.Add(guid[6..].Trim(), name);
                    }
                }
            }
            foreach (string path in files.Where(path => path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase)))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                string reference = document.RootElement.GetProperty("reference").GetString()!;
                string name = reference.StartsWith("GUID:", StringComparison.Ordinal) ? guids.GetValueOrDefault(reference[5..])
                    ?? throw new AnalysisException($"程序集引用没有可定位的定义：{path} => {reference}") : reference;
                owners.Add(Path.GetDirectoryName(path)!, name);
            }
            Dictionary<string, HashSet<string>> sources = responses.ToDictionary(response => response.AssemblyName,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
            Dictionary<string, (string? Owner, bool Scanned)> directories = new(StringComparer.OrdinalIgnoreCase);

            // 目录归属与扫描范围沿父目录计算一次，同目录的所有文件复用结果。
            (string? Owner, bool Scanned) ReadDirectory(string directory)
            {
                if (directories.TryGetValue(directory, out var result))
                {
                    return result;
                }
                string? parent = Path.GetDirectoryName(directory);
                var inherited = parent != null && IsUnderDirectory(directory, projectRoot) ? ReadDirectory(parent) : default;
                result = (owners.GetValueOrDefault(directory) ?? inherited.Owner, roots.Contains(directory) || inherited.Scanned);
                directories.Add(directory, result);
                return result;
            }

            foreach (var group in files.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Concat(sourceTexts.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase))
            {
                string? owner = ReadDirectory(group.Key).Owner;
                if (owner == null && IsUnderDirectory(group.Key, assets))
                {
                    string[] parts = Path.GetRelativePath(assets, group.Key).Split(Path.DirectorySeparatorChar);
                    bool editor = parts.Contains("Editor", StringComparer.OrdinalIgnoreCase);
                    bool firstPass = parts[0] is "Plugins" or "Standard Assets" or "Pro Standard Assets";
                    owner = "Assembly-CSharp" + (editor ? "-Editor" : "") + (firstPass ? "-firstpass" : "");
                }
                if (owner != null && sources.TryGetValue(owner, out var paths))
                {
                    paths.UnionWith(group);
                }
            }
            return responses.Select(response => response with
            {
                SourcePaths = sources[response.AssemblyName].Concat(response.SourcePaths.Where(path => !ReadDirectory(Path.GetDirectoryName(path)!).Scanned))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            }).ToArray();
        }

        // 使用 Unity 本次 Csc 节点的产物定位源码，不按相邻文件或库名推测构建关系。
        private static IReadOnlyDictionary<string, string> ReadBuildOutputs(string projectRoot, string rootResponsePath)
        {
            string graphPath = Path.Combine(projectRoot, "Library", "Bee",
                Path.GetFileName(Path.GetDirectoryName(rootResponsePath)!) + ".json");
            RequireFiles(new[] { graphPath }, "当前构建图的 JSON", rootResponsePath);
            var nodes = ReadCscNodes(graphPath, projectRoot).Select(node => (node.Outputs,
                Response: node.Inputs.Single(path => path.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase)))).ToArray();
            if (!nodes.Any(node => string.Equals(node.Response, rootResponsePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AnalysisException($"当前构建图未声明根编译：{graphPath} => {rootResponsePath}");
            }
            return nodes.SelectMany(node => node.Outputs.Select(path => (Path: path, node.Response)))
                .ToDictionary(item => item.Path, item => item.Response, StringComparer.OrdinalIgnoreCase);
        }

        // 用 Utf8JsonReader 顺序读取构建图的 Nodes，只保留 Annotation 以 "Csc " 开头的节点的输入和输出路径。
        private static List<(string[] Inputs, string[] Outputs)> ReadCscNodes(string graphPath, string projectRoot)
        {
            ReadOnlySpan<byte> json = File.ReadAllBytes(graphPath);
            Utf8JsonReader reader = new(json.StartsWith(Encoding.UTF8.Preamble) ? json[Encoding.UTF8.Preamble.Length..] : json);
            List<(string[] Inputs, string[] Outputs)> nodes = new();
            // 每个属性值处理完后统一 Skip：数组和对象跳到结尾，已读完的值不再移动。
            for (reader.Read(); reader.Read() && reader.TokenType == JsonTokenType.PropertyName; reader.Skip())
            {
                bool isNodes = reader.ValueTextEquals("Nodes"u8);
                for (reader.Read(); isNodes && reader.Read() && reader.TokenType == JsonTokenType.StartObject;)
                {
                    bool? csc = null;
                    string[]? inputs = null, outputs = null;
                    for (; reader.Read() && reader.TokenType == JsonTokenType.PropertyName; reader.Skip())
                    {
                        bool isAnnotation = reader.ValueTextEquals("Annotation"u8), isInputs = reader.ValueTextEquals("Inputs"u8);
                        bool isPaths = (isInputs || reader.ValueTextEquals("Outputs"u8)) && csc != false;
                        reader.Read();
                        if (isAnnotation)
                        {
                            csc = reader.GetString()!.StartsWith("Csc ", StringComparison.Ordinal);
                        }
                        else if (isPaths)
                        {
                            (isInputs ? ref inputs : ref outputs) = JsonElement.ParseValue(ref reader).EnumerateArray()
                                .Select(value => ResolvePath(projectRoot, value.GetString()!)).ToArray();
                        }
                    }
                    if (csc ?? throw new AnalysisException($"当前构建图节点缺少 Annotation：{graphPath}"))
                    {
                        nodes.Add((inputs ?? throw new AnalysisException($"当前构建图 Csc 节点缺少 Inputs：{graphPath}"),
                            outputs ?? throw new AnalysisException($"当前构建图 Csc 节点缺少 Outputs：{graphPath}")));
                    }
                }
            }
            return nodes;
        }

        // 解析一份 Unity 编译响应文件并核对其中的真实路径。
        private static CompilerResponse ReadResponse(
            string projectRoot,
            string assemblyName,
            string responsePath,
            IReadOnlyDictionary<string, string> buildOutputs,
            Dictionary<string, string[]> responseArguments)
        {
            string[] expandedArguments = ReadResponseArguments(projectRoot, responsePath, responseArguments,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            CSharpCommandLineArguments arguments = CSharpCommandLineParser.Default.Parse(
                expandedArguments,
                projectRoot,
                sdkDirectory: null);
            RequireNoErrors(arguments.Errors, $"无法读取 Unity 编译响应文件 {responsePath}");

            string[] sourcePaths = arguments.SourceFiles
                .Select(source => ResolvePath(projectRoot, source.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CompilerReference[] references = arguments.MetadataReferences
                .Select(reference =>
                {
                    string path = ResolvePath(projectRoot, reference.Reference);
                    return new CompilerReference(path, reference.Properties,
                        buildOutputs.TryGetValue(path, out string? sourceResponse) ? Path.GetFileNameWithoutExtension(sourceResponse) : null);
                })
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
                expandedArguments);
        }

        // 一次读取响应文件；嵌套引用、缓存核验和 Roslyn 解析共用同一份参数快照。
        private static string[] ReadResponseArguments(string projectRoot, string path,
            Dictionary<string, string[]> snapshots, HashSet<string> active)
        {
            if (snapshots.TryGetValue(path, out string[]? snapshot))
            {
                return snapshot;
            }
            if (!active.Add(path))
            {
                throw new AnalysisException($"编译响应文件循环引用：{path}");
            }
            string[] arguments = File.ReadLines(path).SelectMany(line => CommandLineParser.SplitCommandLineIntoArguments(line, removeHashComments: true))
                .Select(argument => argument.TrimEnd())
                // 与 Roslyn 的响应文件规则一致：文件内的 noconfig 不作为编译选项生效。
                .Where(argument => !string.Equals(argument, "/noconfig", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(argument, "-noconfig", StringComparison.OrdinalIgnoreCase))
                .SelectMany(argument =>
                {
                    if (!argument.StartsWith('@'))
                    {
                        return new[] { argument };
                    }
                    // 此选项只复用 Roslyn 的单文件路径解码；不读取配置、不按文件列表或程序集名解释路径。
                    CSharpCommandLineArguments nested = CSharpCommandLineParser.Default.Parse(new[] { "/out:ResponsePath.dll", "/appconfig:" + argument[1..] }, projectRoot, sdkDirectory: null);
                    RequireNoErrors(nested.Errors, $"无法读取响应文件路径：{argument}");
                    return ReadResponseArguments(projectRoot, nested.AppConfigPath!, snapshots, active);
                }).ToArray();
            active.Remove(path);
            snapshots.Add(path, arguments);
            return arguments;
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
        private static async Task<IReadOnlyDictionary<string, ParsedSource>> ParseSourcesAsync(
            IReadOnlyList<CompilerResponse> responses,
            IReadOnlyDictionary<string, CachedCompilation> previous,
            IReadOnlyDictionary<string, string> sourceTexts,
            int jobs,
            CancellationToken cancellationToken)
        {
            (string AssemblyName, string Path, CSharpParseOptions ParseOptions)[] inputs = responses
                .SelectMany(response => response.SourcePaths.Select(path => (response.AssemblyName, path, response.ParseOptions)))
                .ToArray();
            ConcurrentDictionary<string, ParsedSource> trees = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SyntaxTree> oldTrees = previous.Values.SelectMany(previousAssembly => previousAssembly.Material.Compilation.SyntaxTrees
                .Select(tree => (Key: SourceKey(previousAssembly.Material.Name, tree.FilePath), Tree: tree))).ToDictionary(item => item.Key, item => item.Tree, StringComparer.OrdinalIgnoreCase);
            HashSet<string> paths = inputs.Select(input => input.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? unknownPath = sourceTexts.Keys.FirstOrDefault(path => !paths.Contains(path));
            if (unknownPath != null)
            {
                throw new AnalysisException($"编辑器文本不属于当前编译输入：{unknownPath}");
            }

            await Parallel.ForEachAsync(
                inputs.GroupBy(input => input.Path, StringComparer.OrdinalIgnoreCase),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                (group, token) =>
                {
                    string text;
                    try
                    {
                        text = sourceTexts.TryGetValue(group.Key, out string? editedText)
                            ? editedText : File.ReadAllText(group.Key);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new AnalysisException($"无法读取源码 {group.Key}：{exception.Message}");
                    }
                    string contentKey = Convert.ToHexString(SHA512.HashData(MemoryMarshal.AsBytes(text.AsSpan())));
                    foreach (var input in group)
                    {
                        string key = SourceKey(input.AssemblyName, input.Path);
                        oldTrees.TryGetValue(key, out SyntaxTree? oldTree);
                        SyntaxTree tree = oldTree != null && oldTree.Options.Equals(input.ParseOptions) && oldTree.GetText(token).ToString() == text
                            ? oldTree : CSharpSyntaxTree.ParseText(text, input.ParseOptions, input.Path, cancellationToken: token);
                        trees[key] = new ParsedSource(tree, contentKey);
                    }
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            return trees;
        }

        // 按文件内容复用会话引用，同一映像的不同属性共享元数据，不跨路径合并 DLL。
        private Dictionary<CompilerReference, (MetadataReference Reference, string Key)>
            CreateMetadataReferences(
            IReadOnlyList<CompilerResponse> responses,
            int jobs,
            CancellationToken cancellationToken)
        {
            CompilerReference[] inputs = responses
                .SelectMany(response => response.References)
                .Where(reference => reference.SourceAssemblyName == null)
                .Distinct()
                .ToArray();
            ConcurrentDictionary<CompilerReference, (MetadataReference Reference, string Key)> references = new();
            ConcurrentDictionary<string, CachedMetadata> current = new(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(
                inputs.GroupBy(input => input.Path, StringComparer.OrdinalIgnoreCase),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = jobs,
                },
                group =>
                {
                    byte[] image = File.ReadAllBytes(group.Key);
                    string key = Convert.ToHexString(SHA512.HashData(image));
                    CachedMetadata? previous = this.m_metadataReferences.GetValueOrDefault(group.Key);
                    if (previous?.Key != key)
                    {
                        previous = null;
                    }
                    Dictionary<MetadataReferenceProperties, PortableExecutableReference> variants = new();
                    foreach (var kind in group.GroupBy(reference => reference.Properties.Kind))
                    {
                        PortableExecutableReference? shared = previous?.References.Values.FirstOrDefault(reference => reference.Properties.Kind == kind.Key);
                        foreach (CompilerReference reference in kind)
                        {
                            if (previous != null && previous.References.TryGetValue(reference.Properties, out PortableExecutableReference? existing))
                            {
                                variants[reference.Properties] = existing;
                            }
                            else
                            {
                                if (shared == null)
                                {
                                    shared = MetadataReference.CreateFromImage(image, reference.Properties, filePath: reference.Path);
                                }
                                variants[reference.Properties] = shared.WithProperties(reference.Properties);
                            }
                            references[reference] = (variants[reference.Properties], key);
                        }
                    }
                    current[group.Key] = new CachedMetadata(key, variants);
                });
            this.m_metadataReferences = current;
            return new Dictionary<CompilerReference, (MetadataReference Reference, string Key)>(references);
        }

        // 用已解析源码和原始编译选项建立程序集编译内容。
        private static CSharpCompilation BuildCompilation(
            CompilerResponse response,
            IReadOnlyDictionary<string, ParsedSource> trees,
            IReadOnlyList<MetadataReference> references,
            IReadOnlyList<ISourceGenerator> generators,
            CancellationToken cancellationToken)
        {
            SyntaxTree[] assemblyTrees = response.SourcePaths
                .Select(path => trees[SourceKey(response.AssemblyName, path)].Tree)
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

            return compilation;
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

        private sealed record CachedMetadata(string Key, IReadOnlyDictionary<MetadataReferenceProperties, PortableExecutableReference> References);

        private sealed record CompilerReference(
            string Path,
            MetadataReferenceProperties Properties,
            string? SourceAssemblyName);

        private readonly record struct ParsedSource(SyntaxTree Tree, string ContentKey);

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
            IReadOnlyList<string> Arguments);

        private sealed record CachedCompilation(string Key, SourceAssemblyMaterial Material);

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
