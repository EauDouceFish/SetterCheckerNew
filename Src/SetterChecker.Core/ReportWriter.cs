using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SetterChecker.Core
{
    /// <summary>从同一份结论生成报告、逐函数结果、补标提醒与补丁，并可按同一批修改为源码补 NoLogTrack。</summary>
    public sealed class ReportWriter
    {
        private const string AddNoLogTrack = "补 NoLogTrack";

        // 固定排序输出数量、差异、告警、失败和耗时，不改写被分析项目。
        /// <summary>在指定输出目录生成 report.md、report.json、functions.json、functions.csv、notify.json、notify.md 和 preview.patch。</summary>
        /// <param name="run">本次分析结果。</param>
        /// <param name="directory">报告目录。</param>
        /// <param name="previousFunctions">上一次的 functions.json；给出时提醒只列出其中尚未要求补标的函数，否则列出全部。</param>
        public void Write(AnalysisRun run, string directory, string? previousFunctions = null)
        {
            IReadOnlySet<string> previous = previousFunctions == null ? new HashSet<string>() : ReadPreviousSuggestions(previousFunctions);
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
            NoLogTrackEdits edits = ReadNoLogTrackEdits(run);
            WriteGroups(text, "可补标但声明上不能直接加特性（需手工处理）", edits.Skipped);
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
                    BodyReads = run.Calls?.Behaviors.Methods.Count(body => body.BodyKind != MethodBodyKind.LibraryModel) ?? 0,
                    LibraryModels = run.Calls?.Behaviors.Methods.Count(body => body.BodyKind == MethodBodyKind.LibraryModel) ?? 0,
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
            File.WriteAllText(Path.Combine(directory, "preview.patch"), ReadPreview(edits, run.Material.ProjectRoot), new UTF8Encoding(false));
            IReadOnlyList<FunctionRow> rows = ReadFunctionRows(methods, names, run.Material.ProjectRoot);
            JsonSerializerOptions readable = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(Path.Combine(directory, "functions.json"), JsonSerializer.Serialize(new { Functions = rows }, readable), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "functions.csv"), ReadCsv(rows), new UTF8Encoding(true));
            FunctionRow[] notify = rows.Where(row => row.Action == AddNoLogTrack && !previous.Contains(row.Id)).ToArray();
            File.WriteAllText(Path.Combine(directory, "notify.json"), JsonSerializer.Serialize(new
            {
                Previous = previousFunctions == null ? null : Path.GetFullPath(previousFunctions),
                Count = notify.Length,
                Functions = notify,
            }, readable), new UTF8Encoding(false));
            StringBuilder message = new();
            message.AppendLine("# NoLogTrack 补标提醒\n");
            message.AppendLine(previousFunctions == null ? "对比基准：无，列出全部。" : $"对比基准：{Path.GetFullPath(previousFunctions)}");
            message.AppendLine($"判定为 NoLogTrack、源码却没有标签的函数{(previousFunctions == null ? string.Empty : "（相比上次新增）")}：{notify.Length} 个。\n");
            foreach (FunctionRow row in notify)
            {
                message.AppendLine($"- {row.Class}.{row.Name}（{row.File}:{row.Line}）");
            }
            File.WriteAllText(Path.Combine(directory, "notify.md"), message.ToString(), new UTF8Encoding(false));
        }

        // 把判定为 NoLogTrack 而源码缺少标签的函数写入标签，全部文件核对无误后才开始写。
        /// <summary>按与 preview.patch 相同的修改为源码补 NoLogTrack，保留原文件 BOM 与换行。</summary>
        /// <returns>补标函数数和改动文件数。</returns>
        public (int Methods, int Files) ApplyNoLogTrack(AnalysisRun run)
        {
            NoLogTrackEdits edits = ReadNoLogTrackEdits(run);
            UTF8Encoding strict = new(false, true);
            var files = edits.Files.Select(file =>
            {
                byte[] bytes = File.ReadAllBytes(file.Path);
                bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                string current;
                try
                {
                    current = strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                }
                catch (DecoderFallbackException)
                {
                    throw new AnalysisException($"源码不是有效 UTF-8，未补标：{file.Path}");
                }
                return current == file.Original ? (file, bom) : throw new AnalysisException($"源码在分析后已改变，未补标：{file.Path}");
            }).ToArray();
            foreach (var (file, bom) in files)
            {
                File.WriteAllText(file.Path, file.Updated, new UTF8Encoding(bom));
            }
            return (edits.Files.Sum(file => file.Count), edits.Files.Count);
        }

        // 在声明的首个特性或修饰符前另起一行，缩进和换行沿用该声明所在行；短名称在该位置不指向 KH.NoLogTrackAttribute 时写限定名称。
        private static NoLogTrackEdits ReadNoLogTrackEdits(AnalysisRun run)
        {
            List<NoLogTrackFile> files = new();
            List<AnnotationMethod> skipped = new();
            foreach (var file in run.Annotations.Methods.Where(method => method.IsReportable && method.SuggestNoLogTrack)
                .GroupBy(method => method.File).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                List<TextChange> changes = new();
                SourceText? original = null;
                SemanticModel? model = null;
                foreach (AnnotationMethod method in file)
                {
                    SyntaxNode node = method.SourceMethod.SourceSymbol!.DeclaringSyntaxReferences.Single().GetSyntax();
                    if (node is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
                    {
                        skipped.Add(method);
                        continue;
                    }
                    original ??= node.SyntaxTree.GetText();
                    model ??= run.Material.SourceAssemblies.Single(source => source.Compilation.ContainsSyntaxTree(node.SyntaxTree))
                        .Compilation.GetSemanticModel(node.SyntaxTree);
                    string name = new[] { "NoLogTrack", "KH.NoLogTrack" }.FirstOrDefault(candidate => model.GetSpeculativeSymbolInfo(node.SpanStart,
                            SyntaxFactory.Attribute(SyntaxFactory.ParseName(candidate))).Symbol?.ContainingType.ToDisplayString() == "KH.NoLogTrackAttribute")
                        ?? "global::KH.NoLogTrack";
                    TextLine line = original.Lines.GetLineFromPosition(node.SpanStart);
                    string indent = original.ToString(TextSpan.FromBounds(line.Start, node.SpanStart));
                    string newline = original.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
                    changes.Add(new TextChange(new TextSpan(node.SpanStart, 0), string.IsNullOrWhiteSpace(indent) && newline.Length != 0
                        ? $"[{name}]{newline}{indent}" : $"[{name}] "));
                }
                if (original != null)
                {
                    files.Add(new NoLogTrackFile(file.Key, original.ToString(), original.WithChanges(changes).ToString(), changes.Count));
                }
            }
            return new NoLogTrackEdits(files, skipped);
        }

        // 每个可报告函数一行、列固定，按文件和行号排序；表格查看、流水线对比和提醒共用这一份。
        private static IReadOnlyList<FunctionRow> ReadFunctionRows(IReadOnlyList<AnnotationMethod> methods,
            IReadOnlyDictionary<string, MethodEntry> names, string projectRoot)
        {
            return methods.Select(method => new FunctionRow(method.Id, Path.GetRelativePath(projectRoot, method.File).Replace('\\', '/'),
                    method.Line, method.Class, method.Name, method.SourceLabel, method.Actual?.ToString() ?? "未证明", method.Decision ?? "未确定",
                    method.SuggestNoLogTrack ? AddNoLogTrack : method.MissingReason ? "去掉 NoLogTrack 或补 Reason"
                    : method.ReviewExemption ? "复查豁免"
                    : method.Failure != null && !method.InformationalOnly && !method.UsesManualBaseline ? "人工判断" : string.Empty,
                    method.Failure ?? (method.Actual == MethodEffectKind.Setter && method.Evidence is EffectEvidence evidence
                        ? evidence.Detail + (evidence.MethodPath.Count > 1 ? "；调用过程：" + string.Join(" → ", evidence.MethodPath
                            .Select(id => names.TryGetValue(id, out MethodEntry? entry) ? entry.TypeName + "." + entry.Name : id)) : string.Empty)
                        : string.Empty)))
                .OrderBy(row => row.File, StringComparer.Ordinal).ThenBy(row => row.Line).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        }

        // 表头用中文；含逗号、引号或换行的字段按 CSV 规则加引号。
        private static string ReadCsv(IReadOnlyList<FunctionRow> rows)
        {
            static string Field(string value) => value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
            StringBuilder csv = new("文件,行,类,函数,源码标签,真实行为,最终决定,需要处理,依据,函数标识\n");
            foreach (FunctionRow row in rows)
            {
                csv.Append(string.Join(",", new[] { row.File, row.Line.ToString(), row.Class, row.Name, row.SourceLabel, row.Actual,
                    row.Decision, row.Action, row.Detail, row.Id }.Select(Field))).Append('\n');
            }
            return csv.ToString();
        }

        // 读取上一次 functions.json 中已要求补标的函数标识。
        private static IReadOnlySet<string> ReadPreviousSuggestions(string path)
        {
            if (!File.Exists(path))
            {
                throw new AnalysisException($"上一次的结果不存在：{path}");
            }
            using JsonDocument previous = JsonDocument.Parse(File.ReadAllText(path));
            return previous.RootElement.GetProperty("Functions").EnumerateArray()
                .Where(row => row.GetProperty(nameof(FunctionRow.Action)).GetString() == AddNoLogTrack)
                .Select(row => row.GetProperty(nameof(FunctionRow.Id)).GetString()!).ToHashSet(StringComparer.Ordinal);
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

        // 从分析时的源码快照制作补丁，内容与 ApplyNoLogTrack 写入的修改相同。
        private static string ReadPreview(NoLogTrackEdits edits, string projectRoot)
        {
            StringBuilder patch = new();
            foreach (NoLogTrackFile file in edits.Files)
            {
                string path = Path.GetRelativePath(projectRoot, file.Path).Replace('\\', '/');
                patch.Append($"--- a/{path}\n+++ b/{path}\n");
                string[] oldLines = file.Original.Split('\n');
                string[] newLines = file.Updated.Split('\n');
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

        /// <summary>一个源码文件补标前后的完整内容。</summary>
        private sealed record NoLogTrackFile(string Path, string Original, string Updated, int Count);

        /// <summary>可写入的补标文件，以及声明上不能直接加特性、需手工处理的函数。</summary>
        private sealed record NoLogTrackEdits(IReadOnlyList<NoLogTrackFile> Files, IReadOnlyList<AnnotationMethod> Skipped);

        /// <summary>统一格式的单个函数扫描结果。</summary>
        private sealed record FunctionRow(string Id, string File, int Line, string Class, string Name, string SourceLabel,
            string Actual, string Decision, string Action, string Detail);
    }
}
