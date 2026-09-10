using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace SetterChecker.Core
{
    /// <summary>独立应用日志标签规则，不改变行为分析。</summary>
    public sealed class AnnotationEvaluator
    {
        // 从真实源码标签和已证明行为生成逐函数决定及最短告警调用过程。
        /// <summary>未知行为和冲突保留失败，依赖函数不进入修改清单。</summary>
        public AnnotationResult Evaluate(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots, EffectAnalysisResult effects,
            CallTargetResolutionResult? calls, CancellationToken cancellationToken = default, bool deferTracking = false)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Dictionary<string, MethodEffect> facts = effects.Methods.ToDictionary(method => method.MethodId);
            Dictionary<string, MethodEffect> tracking = new(facts);
            IReadOnlyDictionary<string, EffectEvidence> trackingFailures = effects.Failures;
            HashSet<string> exempt = (calls?.Methods ?? roots).Where(method => method.HasNoLogTrackExemption).Select(method => method.Id).ToHashSet();
            if (calls != null && exempt.Count != 0)
            {
                HashSet<int> affectedRoots = calls.ValueSources.Instances.Where(instance => exempt.Contains(instance.MethodId)).Select(instance => instance.RootId).ToHashSet();
                MethodEntry[] trackedRoots = roots.Where(method => facts.GetValueOrDefault(method.Id)?.Kind == MethodEffectKind.Setter && !exempt.Contains(method.Id)
                    && affectedRoots.Contains(calls.ValueSources.RootInstances[method.Id].Id)).ToArray();
                EffectAnalysisResult remaining = deferTracking
                    ? new EffectAnalysisResult(Array.Empty<MethodEffect>(), TimeSpan.Zero)
                    { Failures = trackedRoots.ToDictionary(method => method.Id, method => new EffectEvidence(new[] { method.Id }, -1, "等待完成标签影响检查")) }
                    : new EffectAnalyzer().AnalyzeAvailable(catalog, trackedRoots, calls, false, cancellationToken, exemptMethods: exempt,
                        businessAssemblies: roots.Select(method => method.AssemblyPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
                foreach (MethodEntry method in trackedRoots)
                {
                    tracking.Remove(method.Id);
                }
                foreach (MethodEffect method in remaining.Methods)
                {
                    tracking.Add(method.MethodId, method);
                }
                trackingFailures = remaining.Failures;
            }
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
                MethodEffectKind? actual = facts.GetValueOrDefault(method.Id)?.Kind;
                string? failure = log && sourceNlt ? "NoLogTrack 与 LogTrack 冲突"
                    : actual == null ? effects.Failures.GetValueOrDefault(method.Id)?.Detail ?? "尚未取得真实行为证明"
                    : !sourceNlt && !log ? trackingFailures.GetValueOrDefault(method.Id)?.Detail : null;
                string? decision = failure != null ? null : nltClass ? "NLTClass" : sourceNlt ? "NoLogTrack"
                    : log || tracking.GetValueOrDefault(method.Id)?.Kind == MethodEffectKind.Setter ? "ShouldTrack" : "NoLogTrack";
                methods.Add(new AnnotationMethod(method.Id, method.TypeName, method.Name, method.SourcePath!, method.Line,
                    sourceNlt, actual, decision, failure,
                    failure == null && actual == MethodEffectKind.Setter && nlt != null && !reason && !nltClass,
                    failure == null && actual == MethodEffectKind.Setter && (reason || nltClass),
                    method.IsReportable && failure == null && !sourceNlt && !log && decision == "NoLogTrack",
                    facts.GetValueOrDefault(method.Id)?.Evidence ?? effects.Failures.GetValueOrDefault(method.Id))
                {
                    SourceMethod = method,
                    TrackingEvidence = sourceNlt ? null : tracking.GetValueOrDefault(method.Id)?.Evidence ?? trackingFailures.GetValueOrDefault(method.Id),
                });
            }

            Dictionary<string, (AnnotationMethod Warning, string[] Path)> shortest = methods.Where(method => method.MissingReason)
                .ToDictionary(method => method.Id, method => (method, new[] { method.Id }));
            Queue<(int InstanceId, AnnotationMethod Warning, string[] Path)> pending = new((calls?.ValueSources.Instances ?? Array.Empty<MethodCallInstance>())
                .Where(instance => shortest.ContainsKey(instance.MethodId)).OrderBy(instance => instance.MethodId, StringComparer.Ordinal).ThenBy(instance => instance.Id)
                .Select(instance => (instance.Id, shortest[instance.MethodId].Warning, new[] { instance.MethodId })));
            HashSet<int> visited = new();
            // 图中递归只返回祖先；最短上游过程沿父调用即可，所有告警共用一次遍历。
            while (pending.TryDequeue(out var item))
            {
                if (!visited.Add(item.InstanceId))
                {
                    continue;
                }
                MethodCallInstance instance = calls!.ValueSources.GetInstance(item.InstanceId);
                if (instance.ParentId == 0 && (!shortest.TryGetValue(instance.MethodId, out var known) || item.Path.Length < known.Path.Length))
                {
                    shortest[instance.MethodId] = (item.Warning, item.Path);
                }
                if (instance.ParentId != 0)
                {
                    pending.Enqueue((instance.ParentId, item.Warning, item.Path.Prepend(calls.ValueSources.GetInstance(instance.ParentId).MethodId).ToArray()));
                }
            }
            foreach (var path in shortest.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value))
            {
                path.Warning.WarningPaths.Add(path.Path);
            }
            return new AnnotationResult(methods, calls?.PendingCalls.Count ?? 0, watch.Elapsed);
        }

        // 按完整特性类型名读取当前声明，不把同名业务类型当作日志规则。
        internal static AttributeData? FindAttribute(ISymbol symbol, string name)
        {
            return symbol.GetAttributes().SingleOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);
        }
    }

    /// <summary>保存一项日志决定及证据；未知值保留为空。</summary>
    public sealed record AnnotationMethod(string Id, string Class, string Name, string File, int Line,
        bool SourceNoLogTrack, MethodEffectKind? Actual, string? Decision, string? Failure,
        bool MissingReason, bool ReviewExemption, bool SuggestNoLogTrack, EffectEvidence? Evidence)
    {
        internal MethodEntry SourceMethod { get; init; } = null!;

        /// <summary>类级豁免独立复查不扩大标签统计范围。</summary>
        public bool IsReportable => this.SourceMethod.IsReportable;

        /// <summary>每个上层可报告函数到该告警的最短调用过程。</summary>
        public List<string[]> WarningPaths { get; } = new();

        /// <summary>可信标签生效后仍需追踪或尚未证明的独立依据。</summary>
        public EffectEvidence? TrackingEvidence { get; init; }
    }

    /// <summary>保存统一报告输入和标签阶段耗时。</summary>
    public sealed record AnnotationResult(IReadOnlyList<AnnotationMethod> Methods, int PendingCalls, TimeSpan Elapsed)
    {
        /// <summary>行为、冲突与告警调用关系均已检查完毕。</summary>
        public bool Complete => this.PendingCalls == 0 && this.Methods.All(method => method.Failure == null);
    }
}
