using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace SetterChecker.Core
{
    /// <summary>独立应用日志标签规则，不改变行为分析。</summary>
    public sealed class AnnotationEvaluator
    {
        // 从真实源码标签和已证明行为生成逐函数决定及最短告警调用过程。
        /// <summary>未知行为和冲突保留失败，依赖函数不进入修改清单。</summary>
        public AnnotationResult Evaluate(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots, EffectAnalysisResult effects,
            CallTargetResolutionResult? calls, CancellationToken cancellationToken = default)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Dictionary<string, MethodEffect> facts = effects.Methods.ToDictionary(method => method.MethodId);
            List<AnnotationMethod> methods = new();
            foreach (MethodEntry method in roots.OrderBy(method => method.Id, StringComparer.Ordinal))
            {
                IMethodSymbol symbol = method.SourceSymbol!;
                AttributeData? nlt = FindAttribute(symbol, "KH.NoLogTrackAttribute");
                bool nltClass = FindAttribute(symbol.ContainingType, "KH.NoLogTrackAttribute") != null;
                bool sourceNlt = nlt != null || nltClass;
                bool log = FindAttribute(symbol, "KH.LogTrackAttribute") != null
                    || FindAttribute(symbol.ContainingType, "KH.LogTrackAttribute") != null;
                bool reason = nlt?.ConstructorArguments.Any(argument => argument.Type?.SpecialType == SpecialType.System_String
                    && argument.Value is string text && !string.IsNullOrWhiteSpace(text)) == true;
                bool trustedClass = nltClass && !log;
                bool traceHook = !sourceNlt && IsTraceHook(symbol);
                MethodEffectKind? actual = trustedClass ? MethodEffectKind.Getter : facts.GetValueOrDefault(method.Id)?.Kind;
                string? failure = log && sourceNlt ? "NoLogTrack 与 LogTrack 冲突"
                    : trustedClass ? null
                    : actual == null ? effects.Failures.GetValueOrDefault(method.Id)?.Detail ?? "尚未取得真实行为证明"
                    : null;
                string? decision = log && sourceNlt ? null : nltClass ? "NLTClass" : sourceNlt ? "NoLogTrack"
                    : log || traceHook ? "ShouldTrack" : failure != null ? null
                    : actual == MethodEffectKind.Setter ? "ShouldTrack" : "NoLogTrack";
                bool informational = sourceNlt && !log && actual == null
                    || failure?.StartsWith("接口或重写没有合法实现：", StringComparison.Ordinal) == true
                    && FindAttribute(symbol.ContainingType, "System.ObsoleteAttribute") != null
                    && calls != null && calls.Behaviors.MethodsById.TryGetValue(method.Id, out MethodBehavior? body)
                    && body.Failure == null && body.Writes.Count == 0 && body.Calls.Count == 1
                    && body.Calls[0].Target.SourceSymbol?.ContainingType.TypeKind == TypeKind.Interface;
                methods.Add(new AnnotationMethod(method.Id, method.TypeName, method.Name, method.SourcePath!, method.Line,
                    sourceNlt, actual, decision, failure,
                    failure == null && actual == MethodEffectKind.Setter && nlt != null && !reason && !nltClass,
                    failure == null && actual == MethodEffectKind.Setter && (reason || nltClass),
                    method.IsReportable && failure == null && actual == MethodEffectKind.Getter
                        && !sourceNlt && !log && decision == "NoLogTrack",
                    facts.GetValueOrDefault(method.Id)?.Evidence ?? effects.Failures.GetValueOrDefault(method.Id))
                {
                    SourceMethod = method,
                    InformationalOnly = informational,
                });
            }

            Dictionary<string, (AnnotationMethod Warning, string? Next)> shortest = methods.Where(method => method.MissingReason)
                .ToDictionary(method => method.Id, method => (method, (string?)null), StringComparer.Ordinal);
            ILookup<string, string> callers = (calls?.Calls ?? Array.Empty<ResolvedCall>())
                .SelectMany(call => call.Targets.Select(target => (Target: target.MethodId, Caller: call.CallerMethodId)))
                .Concat(shortest.Count == 0 || calls == null ? Enumerable.Empty<(string Target, string Caller)>()
                    : calls.Behaviors.Methods.SelectMany(body => body.Calls
                        .Where(call => call.Kind is BehaviorCallKind.Direct or BehaviorCallKind.ObjectCreation
                            && call.Target.SourceSymbol?.DeclaringSyntaxReferences.Length > 0)
                        .Select(call => (Target: catalog.ResolveMethodDefinition(call.Target, false).Method.Id, Caller: body.MethodId))))
                .Distinct().ToLookup(pair => pair.Target, pair => pair.Caller, StringComparer.Ordinal);
            Queue<string> pending = new(shortest.Keys.Order(StringComparer.Ordinal));
            // 每个上游函数只保存一步关联，输出时再还原最短告警过程。
            while (pending.TryDequeue(out string? current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string caller in callers[current].Order(StringComparer.Ordinal))
                {
                    if (shortest.TryAdd(caller, (shortest[current].Warning, current)))
                    {
                        pending.Enqueue(caller);
                    }
                }
            }
            foreach (AnnotationMethod method in methods.Where(method => method.IsReportable && shortest.ContainsKey(method.Id)))
            {
                List<string> path = new();
                for (string? current = method.Id; current != null; current = shortest[current].Next)
                {
                    path.Add(current);
                }
                shortest[method.Id].Warning.WarningPaths.Add(path.ToArray());
            }
            HashSet<string> informationalMethods = methods.Where(method => method.InformationalOnly).Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
            return new AnnotationResult(methods, calls?.PendingCalls.Count(call => !informationalMethods.Contains(call.CallerMethodId)) ?? 0, watch.Elapsed);
        }

        // 按完整特性类型名读取当前声明，不把同名业务类型当作日志规则。
        internal static AttributeData? FindAttribute(ISymbol symbol, string name)
        {
            return symbol.GetAttributes().SingleOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);
        }

        // R9：声明了参数而函数体没有语句的源码函数是日志打点，注入器专门为它记录参数，不建议 NoLogTrack。
        private static bool IsTraceHook(IMethodSymbol symbol)
        {
            return symbol.Parameters.Length != 0 && symbol.DeclaringSyntaxReferences.Length == 1
                && symbol.DeclaringSyntaxReferences[0].GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax
                { Body.Statements.Count: 0, ExpressionBody: null };
        }

        // 类级可信豁免在行为分析前直接闭合；标签冲突仍需进入分析。
        internal static bool IsTrustedClassNoLogTrack(MethodEntry method)
        {
            IMethodSymbol symbol = method.SourceSymbol!;
            return FindAttribute(symbol.ContainingType, "KH.NoLogTrackAttribute") != null
                && FindAttribute(symbol, "KH.LogTrackAttribute") == null
                && FindAttribute(symbol.ContainingType, "KH.LogTrackAttribute") == null;
        }
    }

    /// <summary>保存一项日志决定及证据；未知值保留为空。</summary>
    public sealed record AnnotationMethod(string Id, string Class, string Name, string File, int Line,
        bool SourceNoLogTrack, MethodEffectKind? Actual, string? Decision, string? Failure,
        bool MissingReason, bool ReviewExemption, bool SuggestNoLogTrack, EffectEvidence? Evidence)
    {
        internal MethodEntry SourceMethod { get; init; } = null!;

        /// <summary>可信豁免的行为审计未知或废弃接口无实现，仅提示，不阻塞日志决定。</summary>
        public bool InformationalOnly { get; init; }

        /// <summary>最终日志决定来自人工基线，Actual 与失败证据仍保留。</summary>
        public bool UsesManualBaseline { get; init; }

        /// <summary>类级豁免独立复查不扩大标签统计范围。</summary>
        public bool IsReportable => this.SourceMethod.IsReportable;

        /// <summary>每个上层可报告函数到该告警的最短调用过程。</summary>
        public List<string[]> WarningPaths { get; } = new();

    }

    /// <summary>保存统一报告输入和标签阶段耗时。</summary>
    public sealed record AnnotationResult(IReadOnlyList<AnnotationMethod> Methods, int PendingCalls, TimeSpan Elapsed)
    {
        /// <summary>说明本轮基线是否有效，不把失效配置静默当作已采用。</summary>
        public string? ManualBaselineStatus { get; init; }

        /// <summary>每个函数的行为与冲突均已检查完毕；只影响已证明 Setter 的待定调用不阻塞。</summary>
        public bool Complete => this.Methods.All(method => method.Failure == null
            || method.InformationalOnly && !method.SourceNoLogTrack);
    }

    /// <summary>保存已确认的当前标签，不缓存函数行为或调用分析状态。</summary>
    public sealed record ManualBaseline(int Version, string InputKey, IReadOnlyList<ManualBaselineEntry> Methods)
    {
        // 原生实现无法读取时采用当前人工标签，依据函数体类别而不是错误文本猜测。
        internal static AnnotationResult ApplyNative(AnnotationResult result, CallTargetResolutionResult? calls)
        {
            if (calls == null)
            {
                return result;
            }
            AnnotationMethod[] methods = result.Methods.Select(method =>
            {
                EffectEvidence? evidence = method.Evidence;
                if (!method.IsReportable || method.Failure == null || evidence?.Detail != method.Failure
                    || evidence.MethodPath.Count == 0
                    || !calls.Behaviors.MethodsById.TryGetValue(evidence.MethodPath[^1], out MethodBehavior? body)
                    || body.Failure != null
                    || body.BodyKind is not (MethodBodyKind.PlatformInvocation or MethodBodyKind.RuntimeImplementation))
                {
                    return method;
                }
                string reason = body.NativeBoundary is { } boundary ? boundary.LibraryName + " native in cpp"
                    : "runtime native implementation";
                return method with { Decision = ReadSourceDecision(method), UsesManualBaseline = true, Failure = reason };
            }).ToArray();
            return result with
            {
                Methods = methods,
                ManualBaselineStatus = (result.ManualBaselineStatus == null ? string.Empty : result.ManualBaselineStatus + " ")
                    + "原生实现不可读取时采用当前人工标签，不代表真实行为已证明。",
            };
        }

        // 只替代明确因反射短路而未证明的日志决定，保留真实未知与其他错误。
        internal static AnnotationResult ApplyReflection(AnnotationResult result)
        {
            return result with
            {
                Methods = result.Methods.Select(method => method.IsReportable && method.Failure == ValueSourceIndex.ReflectionBaselineFailure
                    ? method with { Decision = ReadSourceDecision(method), UsesManualBaseline = true } : method).ToArray(),
                ManualBaselineStatus = "已启用反射人工基线：本轮按当前源码标签决定日志，不代表反射行为已证明。",
            };
        }

        // 只收录当前无法证明的可报告函数，冲突标签不得进入基线。
        /// <summary>显式将当前未知函数的源码标签保存为人工基线，不将其标为已证明。</summary>
        public static void Save(AnalysisRun run, string path)
        {
            string key = run.Material.BaselineInputKey ?? throw new AnalysisException("保存人工基线前必须核验本轮材料。");
            if (run.Failure != null)
            {
                throw new AnalysisException("分析流程失败，不能据此保存人工基线：" + run.Failure);
            }
            ManualBaselineEntry[] entries = run.Annotations.Methods
                .Where(method => method.IsReportable && method.Failure != null && method.Failure != "NoLogTrack 与 LogTrack 冲突")
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .Select(method => new ManualBaselineEntry(method.Id, ReadSourceDecision(method),
                    Path.GetRelativePath(run.Material.ProjectRoot, method.File).Replace('\\', '/'), method.Line, method.Failure)).ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(new ManualBaseline(1, key, entries),
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        // 整份材料一致才采用基线；失效时继续输出本轮真实分析，不猜测依赖范围。
        internal static AnnotationResult Apply(AnnotationResult result, MaterialSet material, string path)
        {
            ManualBaseline baseline;
            try
            {
                baseline = JsonSerializer.Deserialize<ManualBaseline>(File.ReadAllText(path))
                    ?? throw new AnalysisException("人工基线内容为空：" + path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new AnalysisException("无法读取人工基线：" + exception.Message);
            }
            if (baseline.Version != 1 || baseline.Methods == null || string.IsNullOrWhiteSpace(baseline.InputKey))
            {
                throw new AnalysisException("人工基线格式或版本不支持：" + path);
            }
            if (material.BaselineInputKey == null || baseline.InputKey != material.BaselineInputKey)
            {
                return result with { ManualBaselineStatus = "人工基线已失效：源码、引用或工具版本发生变化，本轮未采用；需重新确认。" };
            }
            Dictionary<string, ManualBaselineEntry> entries = new(StringComparer.Ordinal);
            foreach (ManualBaselineEntry entry in baseline.Methods)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id)
                    || entry.Decision is not ("NoLogTrack" or "NLTClass" or "ShouldTrack") || !entries.TryAdd(entry.Id, entry))
                {
                    throw new AnalysisException("人工基线存在无效决定或重复函数：" + path);
                }
            }
            AnnotationMethod[] methods = result.Methods.Select(method =>
                method.IsReportable && method.Failure != null && method.Failure != "NoLogTrack 与 LogTrack 冲突"
                    && entries.TryGetValue(method.Id, out ManualBaselineEntry? entry) && entry.Decision == ReadSourceDecision(method)
                    ? method with { Decision = entry.Decision, UsesManualBaseline = true } : method).ToArray();
            return result with { Methods = methods, ManualBaselineStatus = "已采用当前材料的人工基线；未证明项仍保留原始诊断。" };
        }

        // 类级标签优先；没有 NoLogTrack 按项目人工标注约定追踪。
        private static string ReadSourceDecision(AnnotationMethod method)
        {
            return AnnotationEvaluator.FindAttribute(method.SourceMethod.SourceSymbol!.ContainingType, "KH.NoLogTrackAttribute") != null
                ? "NLTClass" : method.SourceNoLogTrack ? "NoLogTrack" : "ShouldTrack";
        }
    }

    /// <summary>一个已确认的人工标签及其来源位置，原失败原因保留供复查。</summary>
    public sealed record ManualBaselineEntry(string Id, string Decision, string File, int Line, string? Reason);
}
