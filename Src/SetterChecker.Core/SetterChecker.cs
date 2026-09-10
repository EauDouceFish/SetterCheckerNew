using Microsoft.CodeAnalysis.CSharp;

namespace SetterChecker.Core
{
    /// <summary>
    /// 按固定顺序运行已经完成的分析模块。
    /// </summary>
    public sealed class SetterChecker
    {
        // 依次读取材料、建立目录、展开调用、判断行为和处理标签。
        /// <summary>
        /// 运行当前已经完成的分析步骤。
        /// </summary>
        public async Task<AnalysisRun> AnalyzeAsync(
            MaterialRequest request,
            CancellationToken cancellationToken = default, Action<string>? progress = null, Action<AnalysisRun>? reportProgress = null)
        {
            progress?.Invoke("正在读取当前游戏源码及编译参数。");
            MaterialSet material = await new MaterialLoader().LoadAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            progress?.Invoke($"材料读取完成：{material.SourceAssemblies.Count} 个源码程序集，包含编译耗时 {material.Elapsed.TotalSeconds:F1} 秒。");
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(
                material,
                request.Jobs,
                cancellationToken).ConfigureAwait(false);
            progress?.Invoke($"函数目录完成：{catalog.Methods.Count} 个源码函数，开始连接实际调用。");
            HashSet<string> reportPaths = material.SourceAssemblies.SelectMany(assembly => assembly.ReportSourcePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable
                || method.SourceSymbol is { IsImplicitlyDeclared: false, IsAbstract: false, IsExtern: false } symbol
                    && reportPaths.Contains(method.SourcePath!)
                    && AnnotationEvaluator.FindAttribute(symbol.ContainingType, "KH.NoLogTrackAttribute") != null).ToArray();
            CallTargetResolutionResult? calls = null;
            EffectAnalysisResult effects = new(Array.Empty<MethodEffect>(), TimeSpan.Zero);
            string? failure = null;
            int reportedProofs = -1;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            // 进度和最终交付共用标签处理与报告数据，未证明项目始终保留失败。
            AnalysisRun ReadRun(bool isInProgress = false)
            {
                AnnotationResult annotations = new AnnotationEvaluator().Evaluate(catalog, roots, effects, calls, cancellationToken, deferTracking: isInProgress);
                return new AnalysisRun(material, catalog, calls, annotations, failure, new Dictionary<string, double>
                {
                    ["材料（包含编译）"] = material.Elapsed.TotalSeconds,
                    ["其中源码编译"] = material.CompilationElapsed.TotalSeconds,
                    ["函数总表"] = catalog.Elapsed.TotalSeconds,
                    ["读取行为、调用、效果与进度记录"] = watch.Elapsed.TotalSeconds - annotations.Elapsed.TotalSeconds,
                    ["其中读取行为"] = calls?.Behaviors.Elapsed.TotalSeconds ?? 0,
                    ["标签检查"] = annotations.Elapsed.TotalSeconds,
                })
                { IsInProgress = isInProgress };
            }
            try
            {
                calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, request.Jobs,
                    cancellationToken, requireCompleteCalls: false, progress: progress, reportProgress: (current, proofs) =>
                    {
                        calls = current;
                        effects = proofs;
                        if (proofs.Methods.Count != reportedProofs)
                        {
                            reportProgress?.Invoke(ReadRun(isInProgress: true));
                            reportedProofs = proofs.Methods.Count;
                        }
                    }).ConfigureAwait(false);
                effects = new EffectAnalyzer().AnalyzeAvailable(catalog, roots, calls, false, cancellationToken);
                if (calls.PendingCalls.Count != 0)
                {
                    calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, request.Jobs,
                        cancellationToken, requireCompleteCalls: false, previous: calls, progress: progress).ConfigureAwait(false);
                    effects = new EffectAnalyzer().AnalyzeAvailable(catalog, roots, calls, false, cancellationToken);
                }
            }
            catch (AnalysisException exception)
            {
                failure = exception.Message;
            }
            return ReadRun();
        }
    }

    /// <summary>保存本轮材料、已证结论与失败，不把诊断冒充完整结果。</summary>
    public sealed record AnalysisRun(MaterialSet Material, MethodCatalogResult Catalog,
        CallTargetResolutionResult? Calls, AnnotationResult Annotations, string? Failure, Dictionary<string, double> Timings)
    {
        /// <summary>本次结果是同步发布的进度快照，不能视作最终验收。</summary>
        public bool IsInProgress { get; init; }

        /// <summary>所有根函数及其标签审计均已完成。</summary>
        public bool Complete => !this.IsInProgress && this.Failure == null && this.Annotations.Complete && this.Calls?.Behaviors.Methods.All(body => body.Failure == null) == true;
    }

    /// <summary>
    /// 表示分析不能继续时的明确错误。
    /// </summary>
    public sealed class AnalysisException : Exception
    {
        // 保存能够直接定位问题的错误信息。
        /// <summary>
        /// 建立带错误说明的分析异常。
        /// </summary>
        public AnalysisException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 表示一次材料读取请求。
    /// </summary>
    public sealed record MaterialRequest(string AssemblyDefinitionPath, int Jobs);

    /// <summary>
    /// 表示一个源码程序集及其真实编译内容。
    /// </summary>
    public sealed record SourceAssemblyMaterial(
        string Name,
        bool IsReportAssembly,
        CSharpCompilation Compilation,
        IReadOnlyList<string> SourcePaths,
        IReadOnlyList<string> ReportSourcePaths,
        string AssemblyPath,
        byte[] AssemblyImage);

    /// <summary>
    /// 表示一个编译引用及其真实函数内容所在文件。
    /// </summary>
    public sealed record ExternalAssemblyMaterial(
        string ReferencePath,
        IReadOnlyList<string> ImplementationPaths);

    /// <summary>
    /// 表示后续分析所需的全部材料。
    /// </summary>
    public sealed record MaterialSet(
        string ProjectRoot,
        string ReportRoot,
        IReadOnlyList<SourceAssemblyMaterial> SourceAssemblies,
        IReadOnlyList<ExternalAssemblyMaterial> ExternalAssemblies,
        IReadOnlyList<string> AssemblyLookupPaths,
        IReadOnlyList<string> AnalyzerPaths,
        TimeSpan Elapsed)
    {
        /// <summary>材料耗时中用于把当前源码编译到内存的部分，不重复计入总耗时。</summary>
        public TimeSpan CompilationElapsed { get; init; }

        /// <summary>当前 Unity 宿主已证明使用按简单程序集名装载的规则。</summary>
        public bool UsesUnityLegacyBinding { get; init; }

        /// <summary>本轮编译显式引用的运行文件；只有这些文件额外具备预载元数据名称入口。</summary>
        public IReadOnlySet<string> ExplicitRuntimeAssemblyPaths { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>参考完整身份到材料模块已唯一选定的真实文件。</summary>
        public IReadOnlyDictionary<string, string> AssemblyRedirects { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
