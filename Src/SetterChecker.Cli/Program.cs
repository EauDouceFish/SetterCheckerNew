using System.Text;
using SetterChecker.Core;

namespace SetterChecker.Cli
{
    internal static class Program
    {
        // 读取命令行并运行当前已经完成的分析模块。
        public static async Task<int> Main(string[] arguments)
        {
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                (string projectPath, int jobs) = ParseArguments(arguments);
                MaterialSet material = await new MaterialLoader().LoadAsync(
                    new MaterialRequest(projectPath, jobs));

                Console.WriteLine($"源码程序集：{material.SourceAssemblies.Count}");
                Console.WriteLine($"源码文件：{material.SourceAssemblies.Sum(item => item.SourcePaths.Count)}");
                Console.WriteLine($"外部编译文件：{material.ExternalAssemblies.Count}");
                Console.WriteLine($"分析器文件：{material.AnalyzerPaths.Count}");
                Console.WriteLine($"材料读取耗时：{material.Elapsed.TotalMilliseconds:F0} 毫秒");

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
