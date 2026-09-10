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
            int detectedNlt = methods.Count(method => method.Decision is "NoLogTrack" or "NLTClass");
            int detectedTrack = methods.Count(method => method.Decision == "ShouldTrack");
            int conflicts = methods.Count(method => method.Failure == "NoLogTrack 与 LogTrack 冲突");
            int unproved = methods.Count(method => method.Decision == null) - conflicts;
            var pending = (run.Calls?.PendingCalls ?? Array.Empty<PendingCall>()).GroupBy(call => new
            { call.CallerMethodId, Position = call.Call.Point.BlockId, call.Failure, Target = call.Call.Target.Identity.Text })
                .Select(group => new { group.Key.CallerMethodId, group.Key.Position, group.Key.Failure, group.Key.Target, Count = group.Count() }).ToArray();
            var proved = run.Annotations.Methods.Where(method => method.Actual != null && method.Decision != null).Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
            var native = (run.Calls?.Calls ?? Array.Empty<ResolvedCall>()).SelectMany(call => call.Targets.Where(target =>
                    run.Calls!.Behaviors.MethodsById.TryGetValue(target.MethodId, out MethodBehavior? body)
                    && body.BodyKind is MethodBodyKind.PlatformInvocation or MethodBodyKind.RuntimeImplementation
                    && !run.Calls.ValueSources.IsRuntimeDelegateCreation(call, target)).Select(target => new
                    { Root = run.Calls!.ValueSources.GetInstance(run.Calls.ValueSources.GetInstance(call.CallerInstanceId).RootId).MethodId, Target = target.MethodId }))
                .GroupBy(item => item).OrderBy(group => group.Key.Root, StringComparer.Ordinal).ThenBy(group => group.Key.Target, StringComparer.Ordinal)
                .Select(group => new
                {
                    group.Key.Root,
                    group.Key.Target,
                    Count = group.Count(),
                    Deferred = proved.Contains(group.Key.Root),
                    run.Calls!.Behaviors.MethodsById[group.Key.Target].BodyKind,
                    run.Calls.Behaviors.MethodsById[group.Key.Target].NativeBoundary
                }).ToArray();
            StringBuilder text = new();
            text.AppendLine(run.IsInProgress ? "# 分析进行中：以下仅为当前已证明的部分结果" : run.Complete ? "# 分析完成" : "# 验收未通过：以下仅为已证明的部分结果");
            text.AppendLine($"\nkhengine 总函数数量：{methods.Count} 个");
            text.AppendLine($"NoLogTrack：{detectedNlt}（检测出）/ {sourceNlt}（源码中）");
            text.AppendLine($"ShouldTrack：{detectedTrack}（检测出）/ {methods.Count - sourceNlt}（源码中）");
            text.AppendLine($"尚未证明：{unproved}；标签冲突：{conflicts}；待处理调用：{run.Annotations.PendingCalls}");
            text.AppendLine("\n日志豁免不改变真实行为；未知项不计入两种检测结论。待处理调用不为零时，上游告警清单尚不完整。");
            text.AppendLine($"源码程序集：{run.Material.SourceAssemblies.Count}；仅保留编译上下文：{run.Material.SourceAssemblies.Count(source => source.CompilationOrigin == CompilationOrigin.Deferred)}；实际编译：{run.Material.SourceAssemblies.Count(source => source.CompilationOrigin == CompilationOrigin.Built)}；会话复用：{run.Material.SourceAssemblies.Count(source => source.CompilationOrigin == CompilationOrigin.Session)}；磁盘复用：{run.Material.SourceAssemblies.Count(source => source.CompilationOrigin == CompilationOrigin.DiskCache)}。");
            text.AppendLine("补丁仅供预览。手工检查时使用 git -c core.autocrlf=false apply --check preview.patch，避免个人 Git 配置转换源码换行。");
            WriteGroups(text, "源码有 NoLogTrack、真实行为为 Setter（保留人工豁免）", methods.Where(method => method.SourceNoLogTrack && method.Actual == MethodEffectKind.Setter));
            WriteGroups(text, "源码无 NoLogTrack、可补标", methods.Where(method => method.SuggestNoLogTrack));
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
            text.AppendLine("\n## 尚需解决\n");
            text.AppendLine(run.Failure);
            foreach (MethodBehavior body in run.Calls?.Behaviors.Methods.Where(body => body.Failure != null) ?? Array.Empty<MethodBehavior>())
            {
                text.AppendLine($"- 函数体未读取：{body.MethodId}；{body.Failure}");
            }
            foreach (AnnotationMethod method in run.Annotations.Methods.Where(method => method.Failure != null))
            {
                text.AppendLine($"- {method.Class}.{method.Name}（{method.File}:{method.Line}）：{method.Failure}");
                if ((method.TrackingEvidence ?? method.Evidence) is EffectEvidence evidence)
                {
                    text.AppendLine($"  位置：{evidence.Position}；调用过程：" + string.Join(" → ", evidence.MethodPath
                        .Select(id => names.TryGetValue(id, out MethodEntry? entry) ? entry.TypeName + "." + entry.Name : id)));
                }
            }
            foreach (var call in pending)
            {
                text.AppendLine($"- 待处理调用 {call.CallerMethodId} @ {call.Position}（{call.Count} 次）：{call.Failure ?? call.Target}");
            }
            text.AppendLine("\n## 已绑定的原生边界\n");
            foreach (var boundary in native)
            {
                text.AppendLine($"- 原生边界（{(boundary.Deferred ? "结论已有独立证据，可延期" : "尚未证明不影响结论")}）：{boundary.Root} → {boundary.Target}；{boundary.BodyKind}；{boundary.NativeBoundary}；{boundary.Count} 个调用绑定");
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
                run.IsInProgress,
                Total = methods.Count,
                SourceNoLogTrack = sourceNlt,
                SourceShouldTrack = methods.Count - sourceNlt,
                DetectedNoLogTrack = detectedNlt,
                DetectedShouldTrack = detectedTrack,
                Unproved = unproved,
                run.Failure,
                run.Annotations.PendingCalls,
                Methods = methods,
                ExcludedClassAudit = run.Annotations.Methods.Where(method => !method.IsReportable).ToArray(),
                run.Timings,
                UnreadBodies = run.Calls?.Behaviors.Methods.Where(body => body.Failure != null).Select(body => new { body.MethodId, body.Failure }).ToArray(),
                Pending = pending,
                NativeBoundaries = native,
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
                text.AppendLine($"Class: {group.Key} | " + string.Join(", ", group.Select(method => $"{method.Name}（{method.File}:{method.Line}）")));
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
