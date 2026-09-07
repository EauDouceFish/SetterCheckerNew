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
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                (string projectPath, int jobs) = ParseArguments(arguments);
                Stopwatch stopwatch = Stopwatch.StartNew();
                (
                    MaterialSet material,
                    MethodCatalogResult catalog,
                    BehaviorReadResult behaviors) = await new SetterChecker.Core.SetterChecker()
                        .AnalyzeAsync(new MaterialRequest(projectPath, jobs));
                stopwatch.Stop();

                Console.WriteLine($"源码程序集：{material.SourceAssemblies.Count}");
                Console.WriteLine($"源码文件：{material.SourceAssemblies.Sum(item => item.SourcePaths.Count)}");
                Console.WriteLine($"外部编译文件：{material.ExternalAssemblies.Count}");
                Console.WriteLine($"分析器文件：{material.AnalyzerPaths.Count}");
                Console.WriteLine($"材料读取耗时（含编译）：{material.Elapsed.TotalMilliseconds:F0} 毫秒");
                Console.WriteLine($"其中当前源码编译：{material.CompilationElapsed.TotalMilliseconds:F0} 毫秒");
                Console.WriteLine($"已建立源码函数：{catalog.Methods.Count}");
                Console.WriteLine($"已建立全部类型：{catalog.Types.Count}");
                Console.WriteLine($"khengine 可报告函数：{catalog.Methods.Count(method => method.IsReportable)}");
                Console.WriteLine($"函数总表耗时：{catalog.Elapsed.TotalMilliseconds:F0} 毫秒");
                Console.WriteLine($"已读取函数行为：{behaviors.Methods.Count}");
                Console.WriteLine($"函数行为读取耗时：{behaviors.Elapsed.TotalMilliseconds:F0} 毫秒");
                Console.WriteLine($"当前完整流程耗时：{stopwatch.Elapsed.TotalMilliseconds:F0} 毫秒");

                return 0;
            }
            catch (AnalysisException exception)
            {
                Console.Error.WriteLine(exception.Message);

                return 1;
            }
        }

        // 解析项目路径和全程序共用的最大并行数。
        private static (string ProjectPath, int Jobs) ParseArguments(string[] arguments)
        {
            string? projectPath = null;
            int jobs = 4;

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

            return (projectPath ?? throw new AnalysisException("缺少 --project 项目路径。"), jobs);
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
