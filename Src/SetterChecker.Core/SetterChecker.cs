using Microsoft.CodeAnalysis.CSharp;

namespace SetterChecker.Core
{
    /// <summary>
    /// 按固定顺序运行已经完成的分析模块。
    /// </summary>
    public sealed class SetterChecker
    {
        private readonly MaterialLoader m_materialLoader = new();
        private readonly SemaphoreSlim m_analysisRequests = new(1);

        // 同一工具实例整轮排队，先固定编辑文本，避免请求重叠突破并行额度。
        /// <summary>
        /// 运行当前已经完成的分析步骤。
        /// </summary>
        public async Task<AnalysisRun> AnalyzeAsync(
            MaterialRequest request,
            CancellationToken cancellationToken = default, Action<string>? progress = null, Action<AnalysisRun>? reportProgress = null)
        {
            request = request with { SourceTexts = request.SourceTexts.ToDictionary(pair => Path.GetFullPath(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase) };
            await this.m_analysisRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 依次读取当前材料、建立目录、展开调用、判断行为和处理标签。
                progress?.Invoke("正在读取当前游戏源码及编译参数。");
                MaterialSet material = await this.m_materialLoader.LoadAsync(
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

                // 同一轮补齐调用后只重查未定行为，已有真实修改证据不用于标签追踪判断。
                EffectAnalysisResult ReadEffects()
                {
                    Dictionary<int, EffectEvidence> proven = effects.Methods.Where(method => method.Kind == MethodEffectKind.Setter)
                        .ToDictionary(method => calls!.ValueSources.RootInstances[method.MethodId].Id, method => method.Evidence!);

                    return new EffectAnalyzer().AnalyzeAvailable(catalog, roots, calls!, false, cancellationToken, frozenSetters: proven);
                }

                // 进度和最终交付共用标签处理与报告数据，未证明项目始终保留失败。
                AnalysisRun ReadRun(bool isInProgress = false)
                {
                    AnnotationResult annotations = new AnnotationEvaluator().Evaluate(catalog, roots, effects, calls, cancellationToken, deferTracking: isInProgress);
                    return new AnalysisRun(material, catalog, calls, annotations, failure, new Dictionary<string, double>
                    {
                        ["材料（包含编译）"] = material.Elapsed.TotalSeconds,
                        ["其中材料阶段编译准备"] = material.CompilationElapsed.TotalSeconds,
                        ["其中生成或复用完整程序集（累计工作秒，含按需部分）"] = material.SourceAssemblies.Where(source => source.Output.IsValueCreated).Sum(source => source.Output.Value.Elapsed.TotalSeconds),
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
                    effects = ReadEffects();
                    if (calls.PendingCalls.Count != 0)
                    {
                        calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, request.Jobs,
                            cancellationToken, requireCompleteCalls: false, previous: calls, progress: progress).ConfigureAwait(false);
                        effects = ReadEffects();
                    }
                }
                catch (AnalysisException exception)
                {
                    failure = exception.Message;
                }
                return ReadRun();
            }
            finally
            {
                this.m_analysisRequests.Release();
            }
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
    public sealed record MaterialRequest(string AssemblyDefinitionPath, int Jobs)
    {
        /// <summary>编辑器当前文本覆盖；路径必须属于本轮真实编译输入，不写回游戏。</summary>
        public IReadOnlyDictionary<string, string> SourceTexts { get; init; } = new Dictionary<string, string>();

        /// <summary>编译产物缓存目录；不指定时仅保留当前会话上下文，不写磁盘缓存。</summary>
        public string? CacheDirectory { get; init; }
    }

    /// <summary>
    /// 表示本轮源码及延迟编译内容；本轮取消后不可再使用，应重新 LoadAsync 请求快照。
    /// </summary>
    public sealed record SourceAssemblyMaterial(
        string Name,
        bool IsReportAssembly,
        CSharpCompilation Compilation,
        IReadOnlyList<string> SourcePaths,
        IReadOnlyList<string> ReportSourcePaths,
        string AssemblyPath,
        Lazy<CompiledAssembly> Output)
    {
        /// <summary>首次需要真实成员或指令时取得完整产物，之后共用同一份内容。</summary>
        public byte[] AssemblyImage => this.Output.Value.Image;

        /// <summary>查询状态不会触发未使用的外部程序集编译。</summary>
        public CompilationOrigin CompilationOrigin => this.Output.IsValueCreated ? this.Output.Value.Origin : CompilationOrigin.Deferred;
    }

    /// <summary>保存一次完整编译或有效复用所得的映像及耗时。</summary>
    public sealed record CompiledAssembly(byte[] Image, CompilationOrigin Origin, TimeSpan Elapsed);

    /// <summary>区分真实编译工作和两种产物复用。</summary>
    public enum CompilationOrigin
    {
        /// <summary>只建立编译上下文，尚未需要完整产物。</summary>
        Deferred,
        /// <summary>本轮实际编译生成。</summary>
        Built,
        /// <summary>核验后保留同会话上下文。</summary>
        Session,
        /// <summary>核验后读取磁盘产物。</summary>
        DiskCache,
    }

    /// <summary>
    /// 表示一个编译引用及其真实函数内容所在文件。
    /// </summary>
    public sealed record ExternalAssemblyMaterial(
        string ReferencePath,
        IReadOnlyList<string> ImplementationPaths);

    /// <summary>
    /// 表示一轮分析的材料快照；创建请求被取消后作废，重试应向同一加载器请求新快照。
    /// </summary>
    public sealed record MaterialSet(
        string ProjectRoot,
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
