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
            CancellationToken cancellationToken = default, Action<string>? progress = null)
        {
            request = request with { SourceTexts = request.SourceTexts.ToDictionary(pair => Path.GetFullPath(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase) };
            await this.m_analysisRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 依次读取源码、建立固定调用关系、传递修改说明和处理标签。
                progress?.Invoke("正在读取当前游戏源码及编译参数。");
                MaterialSet material = await this.m_materialLoader.LoadAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                progress?.Invoke($"材料读取完成：{material.SourceAssemblies.Count} 个源码上下文，耗时 {material.Elapsed.TotalSeconds:F1} 秒；没有生成源码程序集。");
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
                // 类级豁免只改变该函数自己的日志决定，真实写入仍必须向调用者传播；报告函数的源码重写只参与分析，供 R11 使用。
                HashSet<string> rootIds = roots.Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
                HashSet<string> rootKeys = roots.Select(method => AnnotationEvaluator.ReadOverrideKey(method.SourceSymbol!)).ToHashSet(StringComparer.Ordinal);
                MethodEntry[] overrides = catalog.Types.Where(type => type.IsCandidate && type.SourceSymbol != null)
                    .SelectMany(type => type.SourceSymbol!.GetMembers().OfType<Microsoft.CodeAnalysis.IMethodSymbol>())
                    .Where(symbol => symbol is { IsOverride: true, IsAbstract: false } && symbol.DeclaringSyntaxReferences.Length != 0
                        && AnnotationEvaluator.ReadOverriddenKeys(symbol).Any(rootKeys.Contains))
                    .Select(symbol => catalog.ReadSourceDeclaration(symbol, symbol.DeclaringSyntaxReferences[0].SyntaxTree.FilePath))
                    .Where(method => !rootIds.Contains(method.Id)).DistinctBy(method => method.Id).ToArray();
                MethodEntry[] analysisRoots = roots.Concat(overrides).ToArray();                CallTargetResolutionResult? calls = null;
                EffectAnalysisResult effects = new(Array.Empty<MethodEffect>(), TimeSpan.Zero);
                string? failure = null;
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

                // 完成分析后生成标签与报告数据，未证明项目始终保留失败。
                AnalysisRun ReadRun()
                {
                    AnnotationResult annotations = new AnnotationEvaluator().Evaluate(catalog, roots, effects, calls, cancellationToken, overrides);
                    if (request.ManualBaselinePath != null)
                    {
                        annotations = ManualBaseline.Apply(annotations, material, request.ManualBaselinePath);
                    }
                    if (request.UseReflectionBaseline)
                    {
                        annotations = ManualBaseline.ApplyReflection(annotations);
                    }
                    annotations = ManualBaseline.ApplyNative(annotations, calls);
                    Dictionary<string, double> timings = new()
                    {
                        ["材料及源码上下文"] = material.Elapsed.TotalSeconds,
                        ["其中源码上下文准备"] = material.CompilationElapsed.TotalSeconds,
                        ["函数总表"] = catalog.Elapsed.TotalSeconds,
                        ["读取行为、调用、效果与进度记录"] = watch.Elapsed.TotalSeconds - annotations.Elapsed.TotalSeconds,
                        ["其中读取行为"] = calls?.Behaviors.Elapsed.TotalSeconds ?? 0,
                        ["标签检查"] = annotations.Elapsed.TotalSeconds,
                    };
                    if (calls != null)
                    {
                        foreach (var timing in calls.ValueSources.Timing.ReadSeconds())
                        {
                            timings.Add("分析分项/" + timing.Key, timing.Value);
                        }
                    }
                    foreach (var timing in material.Timings)
                    {
                        timings.Add("材料分项/" + timing.Key, timing.Value);
                    }
                    return new AnalysisRun(material, catalog, calls, annotations, failure, timings)
                    { SummaryUpdates = effects.SummaryUpdates };
                }
                try
                {
                    calls = await new CallTargetResolver().ResolveAsync(material, catalog, analysisRoots, request.Jobs,
                        cancellationToken, requireCompleteCalls: false, progress: progress,
                        useReflectionBaseline: request.UseReflectionBaseline).ConfigureAwait(false);
                    failure = calls.Failure;
                    effects = new EffectAnalyzer().Analyze(catalog, analysisRoots, calls, cancellationToken);
                }
                catch (AnalysisException exception)
                {
                    failure = exception.Message;
                }
                calls?.ValueSources.Timing.Stop();
                if (calls != null)
                {
                    foreach (var timing in calls.ValueSources.Timing.ReadSeconds())
                    {
                        progress?.Invoke($"分析分项：{timing.Key} {timing.Value:F3} 秒。");
                    }
                }
                return ReadRun();
            }
            finally
            {
                this.m_analysisRequests.Release();
            }
        }
    }

    /// <summary>协调线程的互斥计时；嵌套工作暂停上层类别，函数体并行读取按等待的实际时间计入。</summary>
    internal sealed class AnalysisTiming
    {
        internal enum Part { Scheduling, Bodies, Targets, Origins, Effects, Declarations, Dispatch, Reflection, Bindings, Registrations, HierarchyIndex, ImplementationMatch, RegistrationSyntax, ImplementationHierarchy, ImplementationMembers }
        private readonly long[] m_ticks = new long[Enum.GetValues<Part>().Length];
        private readonly long[] m_entries = new long[Enum.GetValues<Part>().Length];
        internal Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
        private long m_last = System.Diagnostics.Stopwatch.GetTimestamp();
        private Part m_current;
        private bool m_stopped;
        private readonly TimeSpan m_initialGcPause = GC.GetTotalPauseDuration();
        private readonly long m_initialAllocatedBytes = GC.GetTotalAllocatedBytes(true);

        // 切换类别前结清上一段时间，同类递归不重复采样。
        internal Scope Measure(Part part)
        {
            if (this.m_stopped)
            {
                return new Scope(this, this.m_current, false);
            }
            Part previous = this.m_current;
            this.m_entries[(int)part]++;
            if (previous != part)
            {
                Switch(part);
            }
            return new Scope(this, previous, previous != part);
        }

        // 将这段墙钟时间只记入当前工作类别。
        private void Switch(Part part)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            this.m_ticks[(int)this.m_current] += now - this.m_last;
            this.m_last = now;
            this.m_current = part;
        }

        // 分析结束后停止计时，报告生成不混入分析耗时。
        internal void Stop()
        {
            if (!this.m_stopped)
            {
                Switch(this.m_current);
                this.m_stopped = true;
                this.Counts["分析期间GC暂停毫秒"] = (long)(GC.GetTotalPauseDuration() - this.m_initialGcPause).TotalMilliseconds;
                this.Counts["分析期间分配字节"] = GC.GetTotalAllocatedBytes(true) - this.m_initialAllocatedBytes;
            }
        }

        // 以固定顺序输出互不重叠的时间。
        internal Dictionary<string, double> ReadSeconds()
        {
            string[] names = { "调度及其他", "函数体读取", "调用目标查找", "对象来源查询", "Setter判断与传播",
                "函数声明定位", "接口候选遍历", "反射操作处理", "固定参数绑定", "外围注册绑定",
                "继承索引准备", "具体实现匹配", "注册语法扫描", "实现继承对应", "实现函数匹配" };
            return Enumerable.Range(0, names.Length).ToDictionary(index => names[index],
                index => (double)this.m_ticks[index] / System.Diagnostics.Stopwatch.Frequency);
        }

        // 次数与秒数分开保存，避免报告把计数当时间相加。
        internal Dictionary<string, long> ReadEntries() => Enum.GetValues<Part>().ToDictionary(part => part.ToString(), part => this.m_entries[(int)part]);

        // 记录实际重复操作数量，与耗时分开比较以排除机器波动。
        internal void Count(string name, long amount = 1) => this.Counts[name] = this.Counts.GetValueOrDefault(name) + amount;

        // 只在枚举实际推进时计时，暂停枚举不会占用上层执行时间。
        internal IEnumerable<T> MeasureEnumeration<T>(IEnumerable<T> values, Part part)
        {
            using IEnumerator<T> iterator = values.GetEnumerator();
            while (true)
            {
                bool moved;
                using (Measure(part))
                {
                    moved = iterator.MoveNext();
                }
                if (!moved)
                {
                    yield break;
                }
                yield return iterator.Current;
            }
        }

        internal readonly struct Scope(AnalysisTiming timing, Part previous, bool changed) : IDisposable
        {
            // 离开嵌套工作后恢复调用者类别。
            public void Dispose()
            {
                if (changed)
                {
                    timing.Switch(previous);
                }
            }
        }
    }

    /// <summary>保存本轮材料、已证结论与失败，不把诊断冒充完整结果。</summary>
    public sealed record AnalysisRun(MaterialSet Material, MethodCatalogResult Catalog,
        CallTargetResolutionResult? Calls, AnnotationResult Annotations, string? Failure, Dictionary<string, double> Timings)
    {
        /// <summary>真实分析与日志分析累计加入的不同修改说明数。</summary>
        public int SummaryUpdates { get; init; }

        /// <summary>所有根函数的真实行为均已证明；影响结论的读取失败和待定调用都会体现为根函数的失败。</summary>
        public bool Complete => this.Failure == null && this.Calls != null && this.Annotations.Complete;

        /// <summary>日志决定可交付不等于真实行为已证明，人工基线单独计数。</summary>
        public bool LogDecisionsComplete => this.Failure == null && this.Annotations.Methods.Where(method => method.IsReportable)
            .All(method => method.Failure == null || method.InformationalOnly || method.UsesManualBaseline);
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
    public sealed record MaterialRequest(IReadOnlyList<string> AssemblyDefinitionPaths, int Jobs)
    {
        /// <summary>V3 设计 2.1 节：声明字段属于战斗状态的源码程序集，依据见 Docs/V3-DESIGN.md。</summary>
        public static readonly IReadOnlySet<string> DefaultCombatAssemblies = new HashSet<string>(StringComparer.Ordinal)
        {
            "khengine.runtime", "khengine.define", "morefun.lockstep.Runtime", "kihan.common.runtime", "kihan.proto",
        };

        /// <summary>声明字段属于战斗状态的程序集；这些程序集的 DLL 函数也读取函数体。</summary>
        public IReadOnlySet<string> CombatAssemblies { get; init; } = DefaultCombatAssemblies;

        /// <summary>编辑器当前文本覆盖；路径必须属于本轮真实编译输入，不写回游戏。</summary>
        public IReadOnlyDictionary<string, string> SourceTexts { get; init; } = new Dictionary<string, string>();

        /// <summary>显式采用已确认的人工标签基线；不改写真实行为。</summary>
        public string? ManualBaselinePath { get; init; }

        /// <summary>为显式保存基线准备当前材料校验值。</summary>
        public bool CaptureManualBaseline { get; init; }

        /// <summary>在查询反射目标之前停止展开，受影响的未证明函数按当前人工标签决定日志。</summary>
        public bool UseReflectionBaseline { get; init; }

    }

    /// <summary>
    /// 保存本轮源码编译上下文；分析直接读取源码，不生成程序集。
    /// </summary>
    public sealed record SourceAssemblyMaterial(
        string Name,
        bool IsReportAssembly,
        CSharpCompilation Compilation,
        IReadOnlyList<string> SourcePaths,
        IReadOnlyList<string> ReportSourcePaths,
        string AssemblyPath,
        CompilationOrigin CompilationOrigin)
    {
        /// <summary>是否作为运行时实现、重写和注册候选来源。</summary>
        public bool IsCandidateSource { get; init; } = true;
    }

    /// <summary>区分本轮建立源码上下文和会话内复用。</summary>
    public enum CompilationOrigin
    {
        /// <summary>本轮建立源码编译上下文。</summary>
        Built,
        /// <summary>核验后保留同会话上下文。</summary>
        Session,
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
        /// <summary>材料耗时中建立源码编译上下文的部分，不生成程序集，不重复计入总耗时。</summary>
        public TimeSpan CompilationElapsed { get; init; }

        /// <summary>仅在保存或采用基线时核验全部输入内容，不凭文件日期复用。</summary>
        public string? BaselineInputKey { get; init; }

        /// <summary>材料读取各步骤的独立耗时，包含在材料总耗时中。</summary>
        public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();

        /// <summary>当前 Unity 宿主已证明使用按简单程序集名装载的规则。</summary>
        public bool UsesUnityLegacyBinding { get; init; }

        /// <summary>本轮编译显式引用的运行文件；只有这些文件额外具备预载元数据名称入口。</summary>
        public IReadOnlySet<string> ExplicitRuntimeAssemblyPaths { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>参考完整身份到材料模块已唯一选定的真实文件。</summary>
        public IReadOnlyDictionary<string, string> AssemblyRedirects { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按 R4 排除的编辑器或测试程序集名称。</summary>
        public IReadOnlyList<string> ExcludedEditorAssemblies { get; init; } = Array.Empty<string>();

        /// <summary>本轮请求声明的战斗程序集。</summary>
        public IReadOnlySet<string> CombatAssemblies { get; init; } = MaterialRequest.DefaultCombatAssemblies;
    }
}
