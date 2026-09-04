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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
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
            IReadOnlyDictionary<CompilerReference, PortableExecutableReference> metadataReferences =
                CreateMetadataReferences(responses, request.Jobs, cancellationToken);
            bool enableCompilerParallel = responses.Count == 1 && request.Jobs > 1;
            SourceAssemblyMaterial[] sourceAssemblies = responses
                .Select(response => BuildSourceAssembly(
                    response,
                    reportRoot,
                    enableCompilerParallel,
                    trees,
                    metadataReferences,
                    generatorsByAnalyzerSet[AnalyzerSetKey(response.AnalyzerPaths)],
                    cancellationToken))
                .OrderBy(assembly => assembly.Name, StringComparer.Ordinal)
                .ToArray();

            RequireMatchingSourceReferences(responses, sourceAssemblies);
            HashSet<string> sourceReferencePaths = responses
                .SelectMany(response => response.SourceReferencePaths)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] externalAssemblyPaths = responses
                .SelectMany(response => response.References)
                .Select(reference => reference.Path)
                .Where(path => !sourceReferencePaths.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ExternalAssemblyMaterial[] externalAssemblies = await ResolveExternalAssembliesAsync(
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
                externalAssemblies,
                analyzerPaths,
                stopwatch.Elapsed);
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

        // 核对源码依赖参考文件与重新建立的源码程序集身份完全一致。
        private static void RequireMatchingSourceReferences(
            IReadOnlyList<CompilerResponse> responses,
            IReadOnlyList<SourceAssemblyMaterial> sourceAssemblies)
        {
            IReadOnlyDictionary<string, SourceAssemblyMaterial> assembliesByName = sourceAssemblies
                .ToDictionary(assembly => assembly.Name, StringComparer.Ordinal);

            foreach (SourceReference reference in responses.SelectMany(response =>
                         response.SourceReferences))
            {
                AssemblyName actual = AssemblyName.GetAssemblyName(reference.ReferencePath);
                AssemblyIdentity expected = assembliesByName[reference.AssemblyName]
                    .Compilation
                    .Assembly
                    .Identity;
                bool matches = string.Equals(
                        actual.Name,
                        expected.Name,
                        StringComparison.OrdinalIgnoreCase)
                    && Equals(actual.Version, expected.Version)
                    && string.Equals(
                        actual.CultureName ?? string.Empty,
                        expected.CultureName,
                        StringComparison.OrdinalIgnoreCase)
                    && (actual.GetPublicKey() ?? Array.Empty<byte>()).SequenceEqual(expected.PublicKey);

                if (!matches)
                {
                    throw new AnalysisException(
                        $"源码依赖参考文件与源码程序集身份不同：{reference.ReferencePath} => {reference.AssemblyName}");
                }
            }
        }

        // 把只有声明的外部参考文件连接到 Unity 当前真实输出文件。
        private static async Task<ExternalAssemblyMaterial[]> ResolveExternalAssembliesAsync(
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
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName =
                IndexAssemblyPaths(knownPaths);
            ExternalAssemblyMaterial[] resolved = new ExternalAssemblyMaterial[assemblies.Length];

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
                            pathsByAssemblyName),
                    };

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            HashSet<string> implementationPaths = resolved
                .SelectMany(assembly => assembly.ImplementationPaths)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ExternalAssemblyMaterial[] runtimeDependencies = (unityRuntime?.AssemblyPaths
                    ?? Array.Empty<string>())
                .Where(path => !implementationPaths.Contains(path))
                .Select(path => new ExternalAssemblyMaterial(path, new[] { path }))
                .ToArray();

            return resolved
                .Concat(runtimeDependencies)
                .OrderBy(assembly => assembly.ReferencePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // 按 Unity 明确的输出位置寻找一份参考文件的真实实现。
        private static IReadOnlyList<string> FindImplementationPaths(
            string projectRoot,
            string referencePath,
            UnityRuntimeSelection? unityRuntime)
        {
            const string suffix = ".ref.dll";

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

                IReadOnlyList<string> selectedPaths = runtimeCandidates.Length switch
                {
                    1 => runtimeCandidates,
                    0 => throw new AnalysisException(
                        $"Unity 当前运行目录没有参考文件的真实实现：{referencePath}"),
                    _ => throw new AnalysisException(
                        $"Unity 当前运行目录存在多份实现：{referencePath} => {string.Join("; ", runtimeCandidates)}"),
                };

                RequireMatchingAssemblyIdentity(
                    referencePath,
                    selectedPaths[0],
                    allowVersionDifference: true);

                return selectedPaths;
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
                allowVersionDifference: false);

            return implementationPaths;
        }

        // 核对参考文件与真实实现声明的是同一个程序集。
        private static void RequireMatchingAssemblyIdentity(
            string referencePath,
            string implementationPath,
            bool allowVersionDifference)
        {
            AssemblyName reference = AssemblyName.GetAssemblyName(referencePath);
            AssemblyName implementation = AssemblyName.GetAssemblyName(implementationPath);
            bool sameVersion = allowVersionDifference
                || Equals(reference.Version, implementation.Version);
            bool sameIdentity = string.Equals(
                    reference.Name,
                    implementation.Name,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    reference.CultureName ?? string.Empty,
                    implementation.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (reference.GetPublicKeyToken() ?? Array.Empty<byte>())
                    .SequenceEqual(implementation.GetPublicKeyToken() ?? Array.Empty<byte>())
                && sameVersion;

            if (!sameIdentity)
            {
                throw new AnalysisException(
                    $"参考文件与真实文件的程序集身份不同：{referencePath} => {implementationPath}");
            }
        }

        // 沿类型转交记录找到最终承载类型的编译文件。
        private static IReadOnlyList<string> FollowForwardedAssemblies(
            IReadOnlyList<string> rootPaths,
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName)
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
                foreach (ForwardedAssemblyIdentity identity in ReadForwardedAssemblies(path))
                {
                    pending.Enqueue(FindForwardedAssembly(
                        path,
                        identity,
                        pathsByAssemblyName));
                }
            }

            return paths;
        }

        // 读取一份托管文件中的全部类型转交目标程序集名称。
        private static IReadOnlyList<ForwardedAssemblyIdentity> ReadForwardedAssemblies(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader portableExecutable = new(stream);
            MetadataReader metadata = portableExecutable.GetMetadataReader();
            Dictionary<string, ForwardedAssemblyIdentity> identities = new(
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
                    AssemblyReference reference = metadata.GetAssemblyReference(
                        (AssemblyReferenceHandle)implementation);
                    ForwardedAssemblyIdentity identity = new(
                        metadata.GetString(reference.Name),
                        reference.Version,
                        reference.Culture.IsNil
                            ? string.Empty
                            : metadata.GetString(reference.Culture),
                        reference.PublicKeyOrToken.IsNil
                            ? ImmutableArray<byte>.Empty
                            : metadata.GetBlobBytes(reference.PublicKeyOrToken).ToImmutableArray(),
                        (reference.Flags & AssemblyFlags.PublicKey) != 0);

                    identities[identity.Key] = identity;
                }
            }

            return identities.Values
                .OrderBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // 在转交文件明确可达的位置寻找唯一目标程序集。
        private static string FindForwardedAssembly(
            string forwardingPath,
            ForwardedAssemblyIdentity identity,
            IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByAssemblyName)
        {
            DirectoryInfo directory = new(Path.GetDirectoryName(forwardingPath)!);
            HashSet<string> candidates = pathsByAssemblyName.TryGetValue(
                identity.Name,
                out IReadOnlyList<string>? knownPaths)
                    ? knownPaths
                        .Where(path => MatchesIdentity(path, identity))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(
                candidates,
                Path.Combine(directory.FullName, $"{identity.Name}.dll"),
                identity);
            if (string.Equals(directory.Name, "Facades", StringComparison.OrdinalIgnoreCase)
                && directory.Parent != null)
            {
                AddCandidate(
                    candidates,
                    Path.Combine(directory.Parent.FullName, $"{identity.Name}.dll"),
                    identity);
            }

            return candidates.Count switch
            {
                1 => candidates.Single(),
                0 => throw new AnalysisException(
                    $"类型转交目标不存在或程序集身份不符：{forwardingPath} => {identity.Name}"),
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
            ForwardedAssemblyIdentity identity)
        {
            if (File.Exists(path) && MatchesIdentity(path, identity))
            {
                candidates.Add(Path.GetFullPath(path));
            }
        }

        // 检查候选文件是否符合类型转交记录中的完整程序集身份。
        private static bool MatchesIdentity(
            string path,
            ForwardedAssemblyIdentity identity)
        {
            AssemblyName candidate = AssemblyName.GetAssemblyName(path);
            byte[] candidateKey = identity.UsesFullPublicKey
                ? candidate.GetPublicKey() ?? Array.Empty<byte>()
                : candidate.GetPublicKeyToken() ?? Array.Empty<byte>();
            bool versionMatches = identity.Version == new Version(0, 0, 0, 0)
                || Equals(identity.Version, candidate.Version);

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
                .Order(StringComparer.OrdinalIgnoreCase)
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
        private static IReadOnlyDictionary<CompilerReference, PortableExecutableReference>
            CreateMetadataReferences(
            IReadOnlyList<CompilerResponse> responses,
            int jobs,
            CancellationToken cancellationToken)
        {
            CompilerReference[] inputs = responses
                .SelectMany(response => response.References)
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
                reportSourcePaths);
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
            IReadOnlyList<string> SourcePaths,
            IReadOnlyList<CompilerReference> References,
            IReadOnlyList<string> AnalyzerPaths,
            IReadOnlyList<string> AdditionalFilePaths,
            IReadOnlyList<SourceReference> SourceReferences)
        {
            public IReadOnlyList<string> SourceReferencePaths { get; } = SourceReferences
                .Select(reference => reference.ReferencePath)
                .ToArray();
        }

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

        private sealed record ForwardedAssemblyIdentity(
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
