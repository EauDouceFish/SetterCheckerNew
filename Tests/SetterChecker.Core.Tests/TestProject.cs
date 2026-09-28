using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SetterChecker.Core.Tests
{
    /// <summary>只为 khengine 关键写法建立最小源码上下文和可选 DLL 对照。</summary>
    internal sealed class TestProject : IDisposable
    {
        private static readonly UTF8Encoding s_encoding = new(false);

        /// <summary>测试工程的战斗程序集：被测源码所在的 khengine.runtime 与 external 模式的 External.dll；Tools 为非战斗库。</summary>
        internal static readonly IReadOnlySet<string> CombatAssemblies = new HashSet<string>(StringComparer.Ordinal) { "khengine.runtime", "External" };

        // 保存本次测试独占的临时目录。
        private TestProject(string rootPath)
        {
            this.RootPath = rootPath;
            this.AssemblyDefinitionPath = Path.Combine(rootPath, "Packages/khengine/Runtime/khengine.runtime.asmdef");
        }

        public string RootPath { get; }
        public string AssemblyDefinitionPath { get; }

        // 外围注册单独放在上层源码程序集；非战斗工具库可以是源码程序集或真实 DLL。
        internal static TestProject Create(string source, bool external = false, string? registrations = null, string? librarySource = null,
            string? toolSource = null, bool toolAsDll = false)
        {
            TestProject project = new(Path.Combine(Path.GetTempPath(), $"SetterChecker-key-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path.GetDirectoryName(project.AssemblyDefinitionPath)!);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Library/Bee/artifacts/build"));
            project.Write("Packages/khengine/Runtime/khengine.runtime.asmdef", "{\"name\":\"khengine.runtime\"}");
            project.Write("Packages/khengine/package.json", "{\"name\":\"khengine\"}");
            string core = typeof(object).Assembly.Location;
            List<string> references = new() { core };
            if (toolSource != null)
            {
                if (toolAsDll)
                {
                    references.Add(project.AddLibrary("Tools.dll", "Tools", toolSource));
                }
                else
                {
                    string toolPath = project.Write("Assets/Tools/Tools.cs", toolSource);
                    project.Write("Assets/Tools/Tools.asmdef", "{\"name\":\"Tools\"}");
                    project.WriteResponse("Tools", toolPath, new[] { core });
                    references.Add(Path.Combine(project.RootPath, "Library/Bee/artifacts/build/Tools.dll"));
                }
            }
            if (external || librarySource != null)
            {
                string dll = project.AddLibrary("External.dll", "External", librarySource ?? source, references.Skip(1).ToArray());
                references.Add(dll);
                if (external)
                {
                    source = "public sealed class Placeholder { }";
                }
            }
            string rootSource = project.Write("Packages/khengine/Runtime/Root.cs", source);
            project.WriteResponse("khengine.runtime", rootSource, references);
            if (registrations != null)
            {
                string registrationSource = project.Write("Assets/Registrations.cs", registrations);
                project.WriteResponse("Assembly-CSharp", registrationSource,
                    references.Append(Path.Combine(project.RootPath, "Library/Bee/artifacts/build/khengine.runtime.dll")));
            }
            return project;
        }

        // 本测试工程的读取请求，声明测试自己的战斗程序集。
        internal MaterialRequest Request(int jobs) => new(new[] { this.AssemblyDefinitionPath }, jobs) { CombatAssemblies = CombatAssemblies };

        // 生成测试自己的真实 DLL，用于核对编译引用与同目录候选文件的区别。
        internal string AddLibrary(string fileName, string assemblyName, string source, params string[] references)
        {
            string path = Path.Combine(this.RootPath, fileName);
            CSharpCompilation compilation = CSharpCompilation.Create(assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                references.Prepend(typeof(object).Assembly.Location).Select(reference => MetadataReference.CreateFromFile(reference)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using FileStream stream = File.Create(path);
            var result = compilation.Emit(stream);
            Assert.IsTrue(result.Success, string.Join("; ", result.Diagnostics));
            return path;
        }

        // 通过模块公开结果检查指定真实写法，不检查私有实现或指令布局。
        internal async Task<(MethodCatalogResult Catalog, CallTargetResolutionResult Calls, EffectAnalysisResult Effects)>
            AnalyzeAsync(string typeName, string methodName, int jobs = 4, bool crossMethodOrigins = true)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            MaterialSet material = await new MaterialLoader().LoadAsync(Request(jobs), deadline.Token);
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs, deadline.Token);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.FullName == typeName))
                .Single(method => method.Name == methodName);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(
                material, catalog, new[] { root }, jobs, deadline.Token, requireCompleteCalls: crossMethodOrigins);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, new[] { root }, calls, deadline.Token);
            return (catalog, calls, effects);
        }

        // 测试文本统一无 BOM，目录只位于本次临时工程。
        private string Write(string relativePath, string text)
        {
            string path = Path.Combine(this.RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, s_encoding);
            return path;
        }

        // 声明实际源码与引用关系，沿用 Unity 响应文件及构建节点格式。
        private void WriteResponse(string name, string source, IEnumerable<string> references)
        {
            string directory = Path.Combine(this.RootPath, "Library/Bee/artifacts/build");
            string output = Path.Combine(directory, name + ".dll");
            string response = Path.Combine(directory, name + ".rsp");
            File.WriteAllLines(response, new[] { "-target:library", $"-out:\"{output}\"" }
                .Concat(references.Select(path => $"-r:\"{path}\"")).Append($"\"{source}\""), s_encoding);
            var nodes = Directory.EnumerateFiles(directory, "*.rsp").Select(path =>
            {
                CSharpCommandLineArguments arguments = CSharpCommandLineParser.Default.Parse(new[] { "@" + path }, this.RootPath, null);
                return new
                {
                    Annotation = "Csc " + arguments.OutputFileName,
                    Inputs = arguments.MetadataReferences.Select(reference => reference.Reference).Append(path).ToArray(),
                    Outputs = new[] { Path.Combine(arguments.OutputDirectory, arguments.OutputFileName!) },
                };
            }).ToArray();
            this.Write("Library/Bee/build.json", JsonSerializer.Serialize(new { Nodes = nodes }));
        }

        // 回收测试独占目录，不接触真实游戏或仓库。
        public void Dispose()
        {
            Directory.Delete(this.RootPath, recursive: true);
        }
    }
}
