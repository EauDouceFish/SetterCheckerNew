using SetterChecker.Core;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证 Unity 材料读取的外部行为。
    /// </summary>
    [TestClass]
    public sealed class MaterialLoaderTests
    {
        // 检查最小 Unity 工程能够得到完整且区分清楚的材料。
        /// <summary>
        /// 验证根程序集、源码依赖和外部文件都来自真实编译参数。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncReadsRootAndSourceDependency()
        {
            using TestProject project = TestProject.Create();
            MaterialLoader loader = new();

            MaterialSet result = await loader.LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));

            CollectionAssert.AreEqual(
                new[] { "Dependency", "khengine.runtime" },
                result.SourceAssemblies.Select(assembly => assembly.Name).ToArray());
            Assert.IsTrue(result.SourceAssemblies.Single(assembly =>
                assembly.Name == "khengine.runtime").IsReportAssembly);
            CollectionAssert.AreEqual(
                new[] { project.ExternalAssemblyPath },
                result.ExternalAssemblies.Select(assembly => assembly.ReferencePath).ToArray());
            CollectionAssert.AreEqual(
                new[] { project.AnalyzerPath },
                result.AnalyzerPaths.ToArray());
        }

        // 检查源码生成器产生的代码进入真实编译内容。
        /// <summary>
        /// 验证 Unity 响应文件中的源码生成器不会被丢弃。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncIncludesGeneratedSource()
        {
            using TestProject project = TestProject.CreateWithGenerator();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            Assert.IsNotNull(result.SourceAssemblies
                .Single(assembly => assembly.IsReportAssembly)
                .Compilation
                .GetTypeByMetadataName("GeneratedFromAdditionalFile"));
        }

        // 检查源码依赖不能只按参考文件名关联。
        /// <summary>
        /// 验证源码依赖的参考程序集身份必须和源码编译结果一致。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMismatchedSourceReference()
        {
            using TestProject project = TestProject.CreateWithMismatchedSourceReference();

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "源码依赖参考文件");
        }

        // 检查外部参考文件映射到同目录的真实运行文件。
        /// <summary>
        /// 验证只有声明的外部文件不会被当成函数实现。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncMapsExternalReferenceToImplementation()
        {
            using TestProject project = TestProject.CreateWithExternalReference();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial external = result.ExternalAssemblies.Single();

            Assert.AreEqual(project.ExternalReferencePath, external.ReferencePath);
            CollectionAssert.AreEqual(
                new[] { project.ExternalAssemblyPath },
                external.ImplementationPaths.ToArray());
        }

        // 检查参考文件不能连接到程序集身份不同的同名文件。
        /// <summary>
        /// 验证文件名相似不会绕过程序集身份检查。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMismatchedReferenceImplementation()
        {
            using TestProject project = TestProject.CreateWithMismatchedExternalReference();

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查类型转交会继续找到真正声明类型的程序集。
        /// <summary>
        /// 验证转交文件不是分析调用链的终点。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncFollowsForwardedTypes()
        {
            using TestProject project = TestProject.CreateWithForwardedType();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial external = result.ExternalAssemblies.Single();

            CollectionAssert.AreEqual(
                new[] { project.ExternalAssemblyPath, project.ForwardTargetPath },
                external.ImplementationPaths.ToArray());
        }

        // 检查类型转交目标必须符合转交记录中的程序集身份。
        /// <summary>
        /// 验证同名路径不能替代身份不同的转交目标。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMismatchedForwardTarget()
        {
            using TestProject project = TestProject.CreateWithMismatchedForwardTarget();

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "类型转交目标");
        }

        // 检查缺失源码不会被忽略或替换。
        /// <summary>
        /// 验证响应文件声明的源码缺失时停止并指出文件。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMissingSource()
        {
            using TestProject project = TestProject.Create();

            File.Delete(project.RootSourcePath);
            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, project.RootSourcePath);
        }

        // 检查当前编译无法唯一确定时给出全部候选。
        /// <summary>
        /// 验证多份根响应文件不会被随意挑选。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsAmbiguousRootResponse()
        {
            using TestProject project = TestProject.Create();

            string secondResponsePath = project.CopyRootResponseToSecondBuild();
            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, project.RootResponsePath);
            StringAssert.Contains(exception.Message, secondResponsePath);
        }

        // 检查外部文件缺失时错误直接指向原路径。
        /// <summary>
        /// 验证响应文件声明的外部编译文件缺失时停止。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMissingExternalAssembly()
        {
            using TestProject project = TestProject.Create();

            File.Delete(project.ExternalAssemblyPath);
            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, project.ExternalAssemblyPath);
        }

        // 检查无效并行数量在读取文件前被拒绝。
        /// <summary>
        /// 验证工作数量必须是正整数。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsInvalidJobCount()
        {
            using TestProject project = TestProject.Create();

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    0)));

            StringAssert.Contains(exception.Message, "正整数");
        }

        // 对比单线程与四线程得到的公开材料清单。
        /// <summary>
        /// 验证并行数量只改变执行方式，不改变材料顺序。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncKeepsDeterministicOrderAcrossJobCounts()
        {
            using TestProject project = TestProject.Create();
            MaterialLoader loader = new();

            MaterialSet oneJob = await loader.LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                1));
            MaterialSet fourJobs = await loader.LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                4));

            CollectionAssert.AreEqual(
                oneJob.SourceAssemblies.Select(Describe).ToArray(),
                fourJobs.SourceAssemblies.Select(Describe).ToArray());
            CollectionAssert.AreEqual(
                oneJob.ExternalAssemblies.Select(Describe).ToArray(),
                fourJobs.ExternalAssemblies.Select(Describe).ToArray());
            Assert.IsFalse(oneJob.SourceAssemblies.Single(assembly =>
                assembly.IsReportAssembly).Compilation.Options.ConcurrentBuild);
            Assert.IsFalse(fourJobs.SourceAssemblies.Single(assembly =>
                assembly.IsReportAssembly).Compilation.Options.ConcurrentBuild);

            using TestProject singleAssemblyProject = TestProject.CreateSingleAssembly();
            MaterialSet singleAssembly = await loader.LoadAsync(new MaterialRequest(
                singleAssemblyProject.AssemblyDefinitionPath,
                4));

            Assert.IsTrue(singleAssembly.SourceAssemblies.Single().Compilation.Options.ConcurrentBuild);
        }

        // 把源码程序集转换成不包含耗时和对象地址的稳定描述。
        private static string Describe(SourceAssemblyMaterial assembly)
        {
            return $"{assembly.Name}|{assembly.IsReportAssembly}|{string.Join(";", assembly.SourcePaths)}";
        }

        // 把外部文件转换成不包含对象地址的稳定描述。
        private static string Describe(ExternalAssemblyMaterial assembly)
        {
            return $"{assembly.ReferencePath}|{string.Join(";", assembly.ImplementationPaths)}";
        }
    }
}
