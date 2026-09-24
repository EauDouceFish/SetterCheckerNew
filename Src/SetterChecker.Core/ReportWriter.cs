using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Text;

namespace SetterChecker.Core
{
    /// <summary>从同一份结论生成报告与只读补丁预览。</summary>
    public sealed class ReportWriter
    {
        // 固定排序输出数量、差异、告警、失败和耗时，不改写被分析项目。
        /// <summary>在指定输出目录生成 report.md、report.json 和 preview.patch。</summary>
        public void Write(AnalysisRun run, string directory)
        {
            Directory.CreateDirectory(directory);
            IReadOnlyList<AnnotationMethod> methods = run.Annotations.Methods.Where(method => method.IsReportable).ToArray();
            Dictionary<string, MethodEntry> names = (run.Calls?.Methods ?? run.Catalog.Methods).ToDictionary(method => method.Id);
            int sourceNlt = methods.Count(method => method.SourceNoLogTrack);
            int detectedNlt = methods.Count(method => !method.UsesManualBaseline && method.Decision is "NoLogTrack" or "NLTClass");
            int detectedTrack = methods.Count(method => !method.UsesManualBaseline && method.Decision == "ShouldTrack");
            int conflicts = methods.Count(method => method.Failure == "NoLogTrack 与 LogTrack 冲突");
            HashSet<string> informational = run.Annotations.Methods.Where(method => method.InformationalOnly).Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> handled = run.Annotations.Methods.Where(method => method.InformationalOnly || method.UsesManualBaseline)
                .Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
            int unproved = methods.Count(method => method.Decision == null && !method.InformationalOnly) - conflicts;
            var pending = (run.Calls?.PendingCalls ?? Array.Empty<PendingCall>()).Where(call => !informational.Contains(call.CallerMethodId)
                && !handled.Contains(call.CallerMethodId) && !handled.Contains(call.Call.Target.Identity.Text)).GroupBy(call => new
            { call.CallerMethodId, Position = call.Call.Point.BlockId, call.Failure, Target = call.Call.Target.Identity.Text })
                .Select(group => new { group.Key.CallerMethodId, group.Key.Position, group.Key.Failure, group.Key.Target, Count = group.Count() }).ToArray();
            var reportAssemblies = run.Material.SourceAssemblies.Where(source => source.IsReportAssembly)
                .Select(source => (string?)source.AssemblyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var external = (run.Calls?.Calls ?? Array.Empty<ResolvedCall>()).SelectMany(call => call.Targets
                .Where(target => !reportAssemblies.Contains(names[target.MethodId].AssemblyPath)).Select(target => new
                {
                    target.MethodId,
                    Caller = call.CallerMethodId,
                    Position = call.Call.Point,
                    call.Call.Kind,
                    DeclaredTarget = call.Call.Target.Identity.Text
                })).GroupBy(reason => reason.MethodId).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new
                {
                    group.Key,
                    Class = names[group.Key].TypeName,
                    names[group.Key].Name,
                    names[group.Key].AssemblyPath,
                    names[group.Key].MetadataToken,
                    names[group.Key].SourcePath,
                    names[group.Key].Line,
                    BodyKind = run.Calls!.Behaviors.MethodsById.TryGetValue(group.Key, out MethodBehavior? body) ? (MethodBodyKind?)body.BodyKind : null,
                    BindingCount = group.Count(),
                    DirectCallerCount = group.Select(reason => reason.Caller).Distinct(StringComparer.Ordinal).Count(),
                    FirstReason = group.OrderBy(reason => reason.Caller, StringComparer.Ordinal)
                        .ThenBy(reason => reason.Position.BlockId).ThenBy(reason => reason.Position.Order)
                        .ThenBy(reason => reason.DeclaredTarget, StringComparer.Ordinal).ThenBy(reason => reason.Kind).First()
                }).ToArray();
            int blockingPending = run.LogDecisionsComplete ? 0 : pending.Length;
            var native = (run.Calls?.Calls ?? Array.Empty<ResolvedCall>()).SelectMany(call => call.Targets.Where(target =>
                    run.Calls!.Behaviors.MethodsById.TryGetValue(target.MethodId, out MethodBehavior? body)
                    && body.BodyKind is MethodBodyKind.PlatformInvocation or MethodBodyKind.RuntimeImplementation
                    && !run.Calls.ValueSources.IsRuntimeDelegateCreation(call, target)).Select(target => new
                    { Caller = call.CallerMethodId, Target = target.MethodId }))
                .GroupBy(item => item.Target).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new
                {
                    Target = group.Key,
                    DirectCallerCount = group.Select(item => item.Caller).Distinct().Count(),
                    FirstCaller = group.Select(item => item.Caller).Order(StringComparer.Ordinal).First(),
                    Count = group.Count(),
                    run.Calls!.Behaviors.MethodsById[group.Key].BodyKind,
                    run.Calls.Behaviors.MethodsById[group.Key].NativeBoundary
                }).ToArray();
            StringBuilder text = new();
            var runtimeOperations = (run.Calls?.Calls ?? Array.Empty<ResolvedCall>()).Where(call => call.RuntimeRule != null)
                .GroupBy(call => (Target: call.Call.Target.Identity.Text, Rule: call.RuntimeRule!))
                .OrderBy(group => group.Key.Target, StringComparer.Ordinal)
                .Select(group => new { group.Key.Target, group.Key.Rule, Count = group.Count() }).ToArray();
            text.AppendLine(run.LogDecisionsComplete ? "# 分析完成：最终标签已全部确定" : "# 验收未通过：以下包含未确定结果");
            text.AppendLine($"\nkhengine 总函数数量：{methods.Count} 个");
            text.AppendLine($"NoLogTrack：{detectedNlt}（检测出）/ {sourceNlt}（源码中）");
            text.AppendLine($"ShouldTrack：{detectedTrack}（检测出）/ {methods.Count - sourceNlt}（源码中）");
            if (!run.LogDecisionsComplete)
            {
                text.AppendLine(run.Annotations.ManualBaselineStatus);
            }
            text.AppendLine($"尚未证明：{unproved}；标签冲突：{conflicts}；待处理调用：{blockingPending}");
            text.AppendLine($"仅提示、不阻塞验收：{methods.Count(method => method.InformationalOnly)}；这些声明不计入 Getter/Setter，不生成补标建议。");
            text.AppendLine(blockingPending == 0
                ? "\n日志豁免不改变真实行为；人工基线不计入检测结论，不生成补标建议。剩余边界证据已归入分析细节，不影响最终标签。"
                : "\n日志豁免不改变真实行为；人工基线不计入检测结论，不生成补标建议。待处理调用不为零时，上游告警清单尚不完整。");
            text.AppendLine($"源码上下文：{run.Material.SourceAssemblies.Count}；本轮建立：{run.Material.SourceAssemblies.Count(source => source.CompilationOrigin == CompilationOrigin.Built)}；会话复用：{run.Material.SourceAssemblies.Count(source => source.CompilationOrigin == CompilationOrigin.Session)}；生成源码程序集：0。");
            text.AppendLine($"已排除的编辑器与测试程序集：{(run.Material.ExcludedEditorAssemblies.Count == 0 ? "无" : string.Join("、", run.Material.ExcludedEditorAssemblies))}。");
            text.AppendLine($"实际读取函数体：{run.Calls?.Behaviors.Methods.Count ?? 0}；固定调用位置：{run.Calls?.Calls.Count ?? 0}。同一个函数不按上游路径重复读取。");
            text.AppendLine($"新增修改说明：{run.SummaryUpdates}；上述调用位置不含未确定目标的 {run.Calls?.PendingCalls.Count ?? 0} 个位置。");
            text.AppendLine("补丁仅供预览。手工检查时使用 git -c core.autocrlf=false apply --check preview.patch，避免个人 Git 配置转换源码换行。");
            var hiddenPlayerCode = (run.Calls?.Behaviors.Methods ?? Array.Empty<MethodBehavior>())
                .Where(body => body.HiddenPlayerCodeLines.Count != 0)
                .ToDictionary(body => body.MethodId, body => body.HiddenPlayerCodeLines, StringComparer.Ordinal);
            text.AppendLine("\n## 编辑器宏隐藏的真机代码\n");
            foreach (var item in hiddenPlayerCode.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"- {item.Key}：第 {string.Join(", ", item.Value)} 行。");
            }
            WriteGroups(text, "源码有 NoLogTrack、真实行为为 Setter（保留人工豁免）", methods.Where(method => method.SourceNoLogTrack && method.Actual == MethodEffectKind.Setter));
            WriteGroups(text, "源码无 NoLogTrack、可补标", methods.Where(method => method.SuggestNoLogTrack));
            WriteGroups(text, "采用人工基线（非行为证明）", methods.Where(method => method.UsesManualBaseline));
            WriteGroups(text, "可信豁免复查（含不参与统计的类级审计项）", run.Annotations.Methods.Where(method => method.ReviewExemption));
            WriteGroups(text, "缺少 Reason", methods.Where(method => method.MissingReason));
            foreach (AnnotationMethod method in methods.Where(method => method.MissingReason))
            {
                foreach (string[] path in method.WarningPaths)
                {
                    text.AppendLine("调用过程：" + string.Join(" → ", path.Select(id => names.TryGetValue(id, out MethodEntry? entry)
                        ? entry.TypeName + "." + entry.Name : id)));
                }
            }
            text.AppendLine(run.LogDecisionsComplete ? "\n## 分析细节（不影响最终标签，无需处理）\n" : "\n## 尚需解决\n");
            text.AppendLine(run.Failure);
            foreach (MethodBehavior body in run.Calls?.Behaviors.Methods.Where(body => body.Failure != null) ?? Array.Empty<MethodBehavior>())
            {
                text.AppendLine($"- 函数体未读取：{body.MethodId}；{body.Failure}");
            }
            foreach (AnnotationMethod method in run.Annotations.Methods.Where(method => method.Failure != null && !method.InformationalOnly && !method.UsesManualBaseline))
            {
                text.AppendLine($"- {method.Class}.{method.Name}（{method.File}:{method.Line}）：{method.Failure}");
                if (method.Evidence is EffectEvidence evidence)
                {
                    text.AppendLine($"  位置：{evidence.Position}；调用过程：" + string.Join(" → ", evidence.MethodPath
                        .Select(id => names.TryGetValue(id, out MethodEntry? entry) ? entry.TypeName + "." + entry.Name : id)));
                }
            }
            foreach (var call in pending)
            {
                text.AppendLine($"- 待处理调用 {call.CallerMethodId} @ {call.Position}（{call.Count} 次）：{call.Failure ?? call.Target}");
            }
            text.AppendLine("\n## 已处理：可信豁免与无实现的废弃声明\n");
            foreach (AnnotationMethod method in run.Annotations.Methods.Where(method => method.InformationalOnly))
            {
                text.AppendLine($"- {method.Class}.{method.Name}（{method.File}:{method.Line}）：{method.Failure}");
            }
            text.AppendLine("\n## 已绑定的原生边界\n");
            text.AppendLine("已采用的 .NET 基础操作说明（未列出的操作仍读取实现或明确报未确定）：");
            foreach (var operation in runtimeOperations)
            {
                text.AppendLine($"- {operation.Target}；{operation.Rule.Operation}；{operation.Rule.Description}；依据：{operation.Rule.Source}；{operation.Count} 处。");
            }
            foreach (var boundary in native)
            {
                text.AppendLine($"- {boundary.Target}；{boundary.BodyKind}；{boundary.NativeBoundary}；{boundary.Count} 个固定绑定，{boundary.DirectCallerCount} 个直接调用者；示例：{boundary.FirstCaller}");
            }
            text.AppendLine("\n## 外部函数的调用纳入依据\n");
            text.AppendLine("以下来自已绑定调用，每个外部函数保留一条固定排序的依据；候选目标不等于运行时必定执行。独立读取的类型初始化不在此清单内。");
            foreach (var function in external)
            {
                var reason = function.FirstReason;
                text.AppendLine($"- {function.Class}.{function.Name}（{function.AssemblyPath}；{function.BodyKind}）：调用者 {reason.Caller} @ {reason.Position}；{reason.Kind}；{function.BindingCount} 个固定绑定，{function.DirectCallerCount} 个直接调用者。");
            }
            text.AppendLine("\n## 耗时（秒；其中项不重复相加）\n");
            foreach (var timing in run.Timings)
            {
                text.AppendLine($"- {timing.Key}：{timing.Value:F3}");
            }
            JsonSerializerOptions options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            string json = JsonSerializer.Serialize(new
            {
                run.Complete,
                run.LogDecisionsComplete,
                run.Annotations.ManualBaselineStatus,
                ManualBaselineCount = methods.Count(method => method.UsesManualBaseline),
                ActualUnproved = methods.Count(method => method.Actual == null),
                Total = methods.Count,
                SourceNoLogTrack = sourceNlt,
                SourceShouldTrack = methods.Count - sourceNlt,
                DetectedNoLogTrack = detectedNlt,
                DetectedShouldTrack = detectedTrack,
                Unproved = unproved,
                InformationalOnly = methods.Count(method => method.InformationalOnly),
                run.Failure,
                blockingPending,
                AnalysisCost = new
                {
                    DistinctMethods = run.Calls?.Methods.Count ?? 0,
                    BodyReads = run.Calls?.Behaviors.Methods.Count ?? 0,
                    FixedCallSites = (run.Calls?.Calls.Select(call => (call.CallerMethodId, call.Call.Point)) ?? Enumerable.Empty<(string, BehaviorFlowPoint)>())
                        .Concat(run.Calls?.PendingCalls.Select(call => (call.CallerMethodId, call.Call.Point)) ?? Enumerable.Empty<(string, BehaviorFlowPoint)>()).Distinct().Count(),
                    run.SummaryUpdates,
                    SourceAssemblyEmissions = 0,
                },
                Methods = methods,
                ExcludedClassAudit = run.Annotations.Methods.Where(method => !method.IsReportable).ToArray(),
                run.Timings,
                OriginCalculations = run.Calls?.ValueSources.OriginReadCount ?? 0,
                TimingEntries = run.Calls?.ValueSources.Timing.ReadEntries(),
                OperationCounts = run.Calls?.ValueSources.Timing.Counts,
                UnreadBodies = run.Calls?.Behaviors.Methods.Where(body => body.Failure != null).Select(body => new { body.MethodId, body.Failure }).ToArray(),
                Pending = pending,
                NativeBoundaries = native,
                RuntimeOperations = runtimeOperations,
                ExternalFunctions = external,
                ExcludedEditorAssemblies = run.Material.ExcludedEditorAssemblies,
                HiddenPlayerCode = hiddenPlayerCode,
                Compilation = run.Material.SourceAssemblies.Select(source => new
                { source.Name, Origin = source.CompilationOrigin, SourceFiles = source.SourcePaths.Count, source.IsReportAssembly }).ToArray(),
            }, options);
            File.WriteAllText(Path.Combine(directory, "report.md"), text.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "report.json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "preview.patch"), ReadPreview(methods, run.Material.ProjectRoot), new UTF8Encoding(false));
        }

        // 同一类的方法保留完整源码位置，避免重载函数混在一起。
        private static void WriteGroups(StringBuilder text, string title, IEnumerable<AnnotationMethod> methods)
        {
            text.AppendLine($"\n## {title}\n");
            foreach (var group in methods.GroupBy(method => method.Class).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"Class: {group.Key} | " + string.Join(", ", group.Select(method => $"{method.Name}（{method.File}:{method.Line}）"
                    + (method.UsesManualBaseline ? $" [{method.Decision}; {method.Failure}]" : string.Empty))));
            }
        }

        // 从分析时的源码快照制作补丁，只给已证明且没有标签冲突的函数添加标签。
        private static string ReadPreview(IReadOnlyList<AnnotationMethod> methods, string projectRoot)
        {
            StringBuilder patch = new();
            foreach (var file in methods.Where(method => method.SuggestNoLogTrack).GroupBy(method => method.File).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var declarations = file.Select(method => method.SourceMethod.SourceSymbol!.DeclaringSyntaxReferences.Single().GetSyntax()).ToArray();
                SourceText original = declarations[0].SyntaxTree.GetText();
                SourceText updated = original.WithChanges(declarations.Select(node => new TextChange(new TextSpan(node.SpanStart, 0), "[global::KH.NoLogTrack] ")));
                string path = Path.GetRelativePath(projectRoot, file.Key).Replace('\\', '/');
                patch.Append($"--- a/{path}\n+++ b/{path}\n");
                string[] oldLines = original.ToString().Split('\n');
                string[] newLines = updated.ToString().Split('\n');
                patch.Append($"@@ -1,{oldLines.Length - (oldLines[^1].Length == 0 ? 1 : 0)} +1,{newLines.Length - (newLines[^1].Length == 0 ? 1 : 0)} @@\n");
                AppendLines(patch, oldLines, '-');
                AppendLines(patch, newLines, '+');
            }
            return patch.ToString();
        }

        // 保留源文件是否以换行结束，保证预览能被标准补丁工具读取。
        private static void AppendLines(StringBuilder patch, string[] lines, char prefix)
        {
            bool endsWithNewline = lines[^1].Length == 0;
            foreach (string line in lines.Take(lines.Length - (endsWithNewline ? 1 : 0)))
            {
                patch.Append(prefix).Append(line).Append('\n');
            }
            if (!endsWithNewline)
            {
                patch.Append("\\ No newline at end of file\n");
            }
        }
    }
}
