using System.Diagnostics;
using System.Text;
using SetterChecker.Core;

namespace SetterChecker.Cli
{
    /// <summary>
    /// 提供 SetterChecker 的命令行入口。
    /// </summary>
    internal static class Program
    {
        // 读取命令行并运行当前已经完成的分析模块。
        /// <summary>
        /// 解析命令行参数并运行 SetterChecker。
        /// </summary>
        public static async Task<int> Main(string[] arguments)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            try
            {
                bool noReport = arguments.Contains("--no-report");
                bool applyNoLogTrack = arguments.Contains("--apply-nlt");
                (MaterialRequest request, string output, string? saveBaseline, string? previous) = ParseArguments(arguments
                    .Where(argument => argument is not ("--no-report" or "--apply-nlt")).ToArray());
                Stopwatch stopwatch = Stopwatch.StartNew();
                AnalysisRun run = await new SetterChecker.Core.SetterChecker()
                        .AnalyzeAsync(request, progress: Console.Error.WriteLine);
                Stopwatch reportWatch = Stopwatch.StartNew();
                if (!noReport)
                {
                    new ReportWriter().Write(run, output, previous);
                }
                if (applyNoLogTrack)
                {
                    (int methods, int files) = new ReportWriter().ApplyNoLogTrack(run);
                    Console.WriteLine($"已补 NoLogTrack：{methods} 个函数，{files} 个文件；报告中的行号是补标前的位置。");
                }
                if (saveBaseline != null)
                {
                    ManualBaseline.Save(run, saveBaseline);
                    Console.WriteLine("人工基线已保存：" + Path.GetFullPath(saveBaseline));
                }
                reportWatch.Stop();
                stopwatch.Stop();

                Console.WriteLine(run.LogDecisionsComplete ? "分析完成，最终标签已全部确定" : "分析运行结束，仍有未确定项，功能验收未通过");
                Console.WriteLine($"khengine 总函数：{run.Annotations.Methods.Count(method => method.IsReportable)}");
                Console.WriteLine($"尚需处理：{run.Annotations.Methods.Count(method => method.IsReportable && method.Failure != null && !method.UsesManualBaseline && !method.InformationalOnly)}");
                if (!run.LogDecisionsComplete && run.Annotations.ManualBaselineStatus != null)
                {
                    Console.WriteLine(run.Annotations.ManualBaselineStatus);
                }
                if (!noReport)
                {
                    Console.WriteLine($"判定为 NoLogTrack 但源码缺少标签：{run.Annotations.Methods.Count(method => method.IsReportable && method.SuggestNoLogTrack)}；"
                        + $"提醒{(previous == null ? "（无对比基准，列出全部）" : "（相比上次新增）")}见 notify.md");
                    Console.WriteLine($"报告目录：{Path.GetFullPath(output)}");
                }
                if (!noReport)
                {
                    Console.WriteLine($"报告生成及写出：{reportWatch.Elapsed.TotalSeconds:F3} 秒");
                }
                Console.WriteLine($"完整耗时（包含源码准备{(noReport ? "，未生成报告" : "和报告写出")}）：{stopwatch.Elapsed.TotalSeconds:F3} 秒");

                return run.LogDecisionsComplete ? 0 : 1;
            }
            catch (AnalysisException exception)
            {
                Console.Error.WriteLine(exception.Message);

                return 2;
            }
        }

        // 解析项目路径、全程序共用的最大并行数和上一次结果。
        private static (MaterialRequest Request, string Output, string? SaveBaseline, string? Previous) ParseArguments(string[] arguments)
        {
            List<string> projectPaths = new();
            int jobs = 4;
            string output = Path.Combine(Environment.CurrentDirectory, "reports");
            string? baseline = null;
            string? saveBaseline = null;
            string? previous = null;
            bool reflectionBaseline = false;

            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];

                if (argument == "analyze")
                {
                    continue;
                }

                if (argument == "--project")
                {
                    projectPaths.Add(ReadValue(arguments, ref index, "--project"));

                    continue;
                }
                if (argument == "--output")
                {
                    output = ReadValue(arguments, ref index, "--output");
                    continue;
                }

                if (argument is "-j" or "--jobs")
                {
                    jobs = ReadJobs(ReadValue(arguments, ref index, argument));

                    continue;
                }

                if (argument == "--baseline")
                {
                    baseline = ReadValue(arguments, ref index, argument);
                    continue;
                }
                if (argument == "--reflection-baseline")
                {
                    reflectionBaseline = true;
                    continue;
                }
                if (argument == "--save-baseline")
                {
                    saveBaseline = ReadValue(arguments, ref index, argument);
                    continue;
                }
                if (argument == "--previous")
                {
                    previous = ReadValue(arguments, ref index, argument);
                    continue;
                }

                if (argument.StartsWith("-j", StringComparison.Ordinal) && argument.Length > 2)
                {
                    jobs = ReadJobs(argument[2..]);

                    continue;
                }

                throw new AnalysisException($"不支持的参数：{argument}");
            }

            return (new MaterialRequest(projectPaths.Count != 0 ? projectPaths : throw new AnalysisException("缺少 --project 项目路径。"), jobs)
            { ManualBaselinePath = baseline, CaptureManualBaseline = saveBaseline != null, UseReflectionBaseline = reflectionBaseline }, output, saveBaseline, previous);
        }

        // 读取必须紧跟在参数名后的值。
        private static string ReadValue(string[] arguments, ref int index, string argumentName)
        {
            index++;
            if (index >= arguments.Length)
            {
                throw new AnalysisException($"{argumentName} 缺少值。");
            }

            return arguments[index];
        }

        // 把工作数量转换为正整数。
        private static int ReadJobs(string value)
        {
            return int.TryParse(value, out int jobs) && jobs > 0
                ? jobs
                : throw new AnalysisException($"工作数量必须是正整数：{value}");
        }
    }
}
