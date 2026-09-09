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
                (string projectPath, int jobs, string output) = ParseArguments(arguments);
                Stopwatch stopwatch = Stopwatch.StartNew();
                AnalysisRun run = await new SetterChecker.Core.SetterChecker()
                        .AnalyzeAsync(new MaterialRequest(projectPath, jobs), progress: Console.Error.WriteLine,
                            reportProgress: current => new ReportWriter().Write(current, output));
                new ReportWriter().Write(run, output);
                stopwatch.Stop();

                Console.WriteLine(run.Complete ? "分析完成" : "验收未通过；已输出确定结论与待解决问题");
                Console.WriteLine($"khengine 总函数：{run.Annotations.Methods.Count(method => method.IsReportable)}");
                Console.WriteLine($"已证明真实行为：{run.Annotations.Methods.Count(method => method.IsReportable && method.Actual != null)}");
                Console.WriteLine($"报告目录：{Path.GetFullPath(output)}");
                Console.WriteLine($"完整耗时（包含源码编译和报告写出）：{stopwatch.Elapsed.TotalSeconds:F3} 秒");

                return run.Complete ? 0 : 1;
            }
            catch (AnalysisException exception)
            {
                Console.Error.WriteLine(exception.Message);

                return 1;
            }
        }

        // 解析项目路径和全程序共用的最大并行数。
        private static (string ProjectPath, int Jobs, string Output) ParseArguments(string[] arguments)
        {
            string? projectPath = null;
            int jobs = 4;
            string output = Path.Combine(Environment.CurrentDirectory, "reports");

            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];

                if (argument == "analyze")
                {
                    continue;
                }

                if (argument == "--project")
                {
                    projectPath = ReadValue(arguments, ref index, "--project");

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

                if (argument.StartsWith("-j", StringComparison.Ordinal) && argument.Length > 2)
                {
                    jobs = ReadJobs(argument[2..]);

                    continue;
                }

                throw new AnalysisException($"不支持的参数：{argument}");
            }

            return (projectPath ?? throw new AnalysisException("缺少 --project 项目路径。"), jobs, output);
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
