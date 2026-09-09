using SetterChecker.Core;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证 Unity 材料读取的外部行为。
    /// </summary>
    [TestClass]
    public sealed class MaterialLoaderTests
    {
        // 类型转交不能通过相邻改名文件的元数据名称猜出运行载体。
        /// <summary>仅显式运行引用授予元数据名称入口。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task LoadAsyncFollowsRenamedCarrierOnlyWhenExplicit(bool explicitReference)
        {
            using TestProject project = TestProject.CreateWithLegacyRenamedForwarder(explicitReference);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            ExternalAssemblyMaterial facade = material.ExternalAssemblies.Single(assembly => assembly.ReferencePath == project.UnityReferencePath);
            Assert.AreEqual(explicitReference, facade.ImplementationPaths.Contains(Path.Combine(project.RootPath, "Renamed.dll")));
        }

        // 本轮编译的源码同样是实际运行候选，不能被磁盘运行库覆盖。
        /// <summary>当前构建中的同名源码阻止全局身份重定向。</summary>
        [TestMethod]
        public async Task LoadAsyncKeepsLegacySourceCarrierWithoutGlobalRedirect()
        {
            using TestProject project = TestProject.CreateWithLegacySourceCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string identity = System.Reflection.AssemblyName.GetAssemblyName(project.UnityFrameworkReferencePath).FullName!;
            Assert.IsTrue(material.SourceAssemblies.Any(source => source.Name == "UnityFramework"));
            Assert.IsFalse(material.AssemblyRedirects.ContainsKey(identity));
        }

        // 与 Unity 同简单名的显式改名项目库不能被运行目录中的唯一文件覆盖。
        /// <summary>竞争载体先保留；尚未使用的歧义不阻断材料，也不产生全局重定向。</summary>
        [TestMethod]
        public async Task LoadAsyncKeepsAllLegacyCarriersWithoutGlobalRedirect()
        {
            using TestProject project = TestProject.CreateWithLegacyCarrierCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string referenceIdentity = System.Reflection.AssemblyName.GetAssemblyName(project.UnityFrameworkReferencePath).FullName!;
            Assert.IsTrue(material.UsesUnityLegacyBinding);
            Assert.IsFalse(material.AssemblyRedirects.ContainsKey(referenceIdentity));
            CollectionAssert.IsSubsetOf(new[] { project.UnityRuntimeFrameworkPath, Path.Combine(project.RootPath, "ProjectFramework.dll") },
                material.AssemblyLookupPaths.ToArray());
        }

        // 编辑器专用程序集不进入日志标签统计，但必须保留其源码和依赖。
        /// <summary>按真实 asmdef 平台声明排除报告根，不根据名字或目录判断。</summary>
        [TestMethod]
        [DataRow("Editor", false)]
        [DataRow("Editor,Android", true)]
        [DataRow("", true)]
        public async Task LoadAsyncKeepsEditorOnlyAssemblyOutsideReport(string platforms, bool reportable)
        {
            using TestProject project = TestProject.CreateWithTargetPackageDependency();
            string definitionPath = Path.Combine(project.RootPath, "Packages", "khengine", "Define", "Utilities.asmdef");
            File.WriteAllText(definitionPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                name = "Dependency",
                includePlatforms = platforms.Split(',', StringSplitOptions.RemoveEmptyEntries),
            }), encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(project.RootSourcePath, "public static class Caller { public static void Run(DependencyType value) { value.DependencyMethod(); } }", encoding: new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            SourceAssemblyMaterial dependency = material.SourceAssemblies.Single(source => source.Name == "Dependency");
            Assert.AreEqual(reportable, dependency.IsReportAssembly);
            Assert.AreEqual(reportable ? 1 : 0, dependency.ReportSourcePaths.Count);
            Assert.HasCount(1, dependency.SourcePaths);
            Assert.IsNotEmpty(dependency.AssemblyImage);
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(item => item.Name == "DependencyMethod");
            Assert.AreEqual(reportable, method.IsReportable);
            MethodEntry caller = catalog.Methods.Single(item => item.Name == "Run");
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { caller }, 2);
            Assert.AreEqual(method.Id, calls.Calls.Single().Targets.Single().MethodId);
        }

        // 错误或重复的程序集声明不能被吞掉后改变报告范围。
        /// <summary>平台类型错误、JSON 损坏和同名定义均保留具体文件证据。</summary>
        [TestMethod]
        [DataRow("{\"name\":\"Dependency\",\"includePlatforms\":\"Editor\"}")]
        [DataRow("{\"name\":\"Dependency\",\"includePlatforms\":[null]}")]
        [DataRow("{")]
        [DataRow("{\"name\":\"khengine.runtime\",\"includePlatforms\":[\"Editor\"]}")]
        public async Task LoadAsyncRejectsAmbiguousReportAssemblyDefinition(string content)
        {
            using TestProject project = TestProject.CreateWithTargetPackageDependency();
            string path = Path.Combine(project.RootPath, "Packages", "khengine", "Define", "Utilities.asmdef");
            File.WriteAllText(path, content, encoding: new System.Text.UTF8Encoding(false));
            AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));
            StringAssert.Contains(failure.Message, path);
        }

        // 复刻 KHRTUIManager 通过接口调用 Assets 中实际实现的情况。
        /// <summary>同次构建的反向消费者进入材料和候选表，但不进入 khengine 标签统计。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task LoadAsyncIncludesIncomingImplementation(bool sharedContract, bool precompiledContract)
        {
            using TestProject project = TestProject.CreateWithIncomingImplementation(sharedContract, precompiledContract);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.HasCount(sharedContract && !precompiledContract ? 4 : 3, material.SourceAssemblies);
            Assert.IsFalse(material.SourceAssemblies.Single(source => source.Name == "Consumer").IsReportAssembly);
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            Assert.IsTrue(catalog.Types.Any(type => type.FullName == "Consumer"));
            Assert.IsFalse(catalog.Methods.Any(method => method.TypeName == "Consumer" && method.IsReportable));
            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable).ToArray();
            Assert.HasCount(1, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.Single().Kind);
        }

        // 磁盘残留的邻近响应文件不代表它参与当前构建。
        /// <summary>只按当前构建产物表选择源码，图外 DLL 保留为真实托管材料。</summary>
        [TestMethod]
        public async Task LoadAsyncDoesNotFollowStaleAdjacentResponse()
        {
            using TestProject project = TestProject.Create();
            string graphPath = Path.Combine(project.RootPath, "Library", "Bee", "build.json");
            var graph = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(graphPath))!;
            var nodes = graph["Nodes"]!.AsArray();
            nodes.Remove(nodes.Single(node => node!["Annotation"]!.GetValue<string>().Contains("/Dependency.dll", StringComparison.Ordinal)));
            File.WriteAllText(graphPath, graph.ToJsonString(), encoding: new System.Text.UTF8Encoding(false));
            string directory = Path.GetDirectoryName(project.RootResponsePath)!;
            File.Copy(Path.Combine(directory, "Dependency.ref.dll"), Path.Combine(directory, "Dependency.dll"));
            File.WriteAllText(Path.Combine(project.RootPath, "Packages", "dependency", "Dependency.cs"), "this is stale invalid source", encoding: new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.HasCount(1, material.SourceAssemblies);
            Assert.IsTrue(material.ExternalAssemblies.Any(assembly => assembly.ReferencePath.EndsWith("Dependency.ref.dll", StringComparison.Ordinal)));
        }

        // 保留转交记录提供的完整公钥，不为了建立索引先转换为 token。
        /// <summary>尚未命中的不匹配公钥转交不应阻断其他材料，也不能误连无签名实现。</summary>
        [TestMethod]
        public async Task LoadAsyncKeepsForwarderWithUnmatchedFullPublicKey()
        {
            using TestProject project = TestProject.CreateWithForwardedType(unmatchedFullPublicKey: true);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsFalse(material.ExternalAssemblies.SelectMany(assembly => assembly.ImplementationPaths)
                .Contains(project.ForwardTargetPath));
            Assert.IsTrue(material.ExternalAssemblies.SelectMany(assembly => assembly.ImplementationPaths)
                .Contains(project.ExternalAssemblyPath));
        }

        // 验证编译参考的转交不能替代实际运行文件的转交。
        /// <summary>
        /// 已核实的运行目录按实际文件继续查找类型。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesRuntimeBeforeFollowingReferenceForwarders()
        {
            using TestProject project = TestProject.CreateWithForwardingUnityReference();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            CollectionAssert.AreEqual(new[] { project.UnityRuntimeFacadePath, project.UnityRuntimeTargetPath },
                result.ExternalAssemblies.Single(assembly => assembly.ReferencePath == project.UnityReferencePath).ImplementationPaths.ToArray());
        }

        // 验证源码依赖新增成员后无需等待 Unity 重建旧参考文件。
        /// <summary>
        /// 根程序集必须用本轮依赖源码的成员编译，不能读取旧参考中的成员表。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesCurrentSourceDependencyMembers()
        {
            using TestProject project = TestProject.Create();
            string dependencyPath = Path.Combine(project.RootPath, "Packages", "dependency", "Dependency.cs");
            File.WriteAllText(dependencyPath,
                "public sealed class DependencyType { public static int NewApi() { return 42; } }", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(project.RootSourcePath,
                "public sealed class RootType { public static int Read() { return DependencyType.NewApi(); } }", encoding: new System.Text.UTF8Encoding(false));

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));

            SourceAssemblyMaterial root = result.SourceAssemblies.Single(assembly => assembly.IsReportAssembly);
            Assert.IsTrue(root.Compilation.GetTypeByMetadataName("DependencyType")!.GetMembers("NewApi").Length == 1);
            Assert.IsTrue(root.AssemblyImage.Length > 0);
            Assert.IsFalse(File.Exists(root.AssemblyPath));
        }

        // 验证旧参考中仍然存在但本轮源码已删除的类型不能让编译错误通过。
        /// <summary>
        /// 依赖源码删除成员时，根程序集应准确报告当前源码编译失败。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsRemovedSourceDependencyMembers()
        {
            using TestProject project = TestProject.Create();
            File.WriteAllText(Path.Combine(project.RootPath, "Packages", "dependency", "Dependency.cs"),
                "public sealed class ReplacementType { }", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(project.RootSourcePath,
                "public sealed class RootType { public static object Read() { return new DependencyType(); } }", encoding: new System.Text.UTF8Encoding(false));

            AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(error.Message, "当前源码编译失败");
            StringAssert.Contains(error.Message, "DependencyType");
        }

        // 验证源码顺序来自 Unity 参数，不因路径排序改变跨文件初始化顺序。
        /// <summary>
        /// 保留响应文件列出的源码输入顺序。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncPreservesCompilerSourceOrder()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            string extraPath = Path.Combine(Path.GetDirectoryName(project.RootSourcePath)!, "AAAFirst.cs");
            File.WriteAllText(extraPath, "public sealed class ExtraSource { }", encoding: new System.Text.UTF8Encoding(false));
            File.AppendAllText(project.RootResponsePath, Environment.NewLine + "\"" + extraPath + "\"" + Environment.NewLine, encoding: new System.Text.UTF8Encoding(false));

            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));

            CollectionAssert.AreEqual(new[] { project.RootSourcePath, extraPath },
                material.SourceAssemblies.Single().Compilation.SyntaxTrees.Select(tree => tree.FilePath).ToArray());
        }

        // 验证本次源码编译内容独立存在于内存，不读取或覆盖 Unity 输出。
        /// <summary>
        /// 为统一函数体读取提供当前源码的真实编译结果。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncCompilesCurrentSourceWithoutWritingUnityOutput()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class CurrentSource
                {
                    // 返回本次源码中的明确常量。
                    public static int Read() { return 4321; }
                }
                """);

            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            SourceAssemblyMaterial source = material.SourceAssemblies.Single();
            Assert.IsTrue(Path.IsPathFullyQualified(source.AssemblyPath));
            Assert.IsFalse(File.Exists(source.AssemblyPath), "材料读取不能写回 Unity 输出路径。");
            Assert.IsTrue(source.AssemblyImage.Length > 0);
            using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                new MemoryStream(source.AssemblyImage));
            Mono.Cecil.MethodDefinition method = module.Types.Single(type =>
                type.FullName == "SourceSamples.CurrentSource").Methods.Single(method => method.Name == "Read");
            Assert.IsTrue(method.Body.Instructions.Any(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4
                && Equals(instruction.Operand, 4321)));
            Assert.IsTrue(material.CompilationElapsed > TimeSpan.Zero);
        }

        // 检查 Core 全部手写源码的物理行数，防止新模块突破约定规模。
        /// <summary>
        /// 将 Core 一万三千行上限作为自动化验收条件，计入注释和空行。
        /// </summary>
        [TestMethod]
        public void CoreSourceStaysWithinLineLimit()
        {
            DirectoryInfo? repository = ReadTestSourceDirectory();
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "SetterChecker.slnx")))
            {
                repository = repository.Parent;
            }

            Assert.IsNotNull(repository, "无法从编译时的测试源码位置定位当前解决方案。");
            string corePath = Path.Combine(repository.FullName, "Src", "SetterChecker.Core");
            var files = Directory.EnumerateFiles(corePath, "*.cs", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(corePath, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase)))
                .Select(path => (Name: Path.GetFileName(path), Lines: File.ReadLines(path).Count())).ToArray();

            Assert.IsTrue(files.Sum(file => file.Lines) <= 13_000,
                $"Core 实际 {files.Sum(file => file.Lines)} 行，超过 13000 行；"
                + string.Join("，", files.Select(file => $"{file.Name}: {file.Lines}")));
        }

        // 使用本次编译的源码位置定位仓库，测试产物可以输出到仓库外。
        private static DirectoryInfo ReadTestSourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
        {
            return new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        }

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
            Assert.IsFalse(result.SourceAssemblies.Single(assembly =>
                assembly.Name == "Dependency").IsReportAssembly);
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

        // 检查已找到源码依赖时不会读取旧参考文件的身份或成员。
        /// <summary>
        /// 当前源码编译内容是唯一实现，不受磁盘旧参考内容影响。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncIgnoresStaleSourceReferenceContents()
        {
            using TestProject project = TestProject.CreateWithMismatchedSourceReference();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            SourceAssemblyMaterial root = result.SourceAssemblies.Single(assembly => assembly.IsReportAssembly);
            Assert.IsNotNull(root.Compilation.GetTypeByMetadataName("DependencyType"));
            Assert.IsNull(root.Compilation.GetTypeByMetadataName("WrongType"));
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
            ExternalAssemblyMaterial external = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.ExternalReferencePath);

            Assert.AreEqual(project.ExternalReferencePath, external.ReferencePath);
            CollectionAssert.AreEqual(
                new[] { project.ExternalAssemblyPath },
                external.ImplementationPaths.ToArray());
        }

        // 检查已核实的 Unity 普通程序集使用唯一实际运行载体。
        /// <summary>
        /// 普通程序集和门面遵循同一宿主装载规则。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesUniqueUnityRuntimeImplementationAcrossVersions()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: false);

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            CollectionAssert.AreEqual(new[] { project.UnityRuntimeFacadePath }, result.ExternalAssemblies
                .Single(assembly => assembly.ReferencePath == project.UnityReferencePath).ImplementationPaths.ToArray());
        }

        // 检查 Unity 普通程序集按当前宿主装载规则连接。
        /// <summary>
        /// 验证 4.2 参考可连接到唯一的 4.0 运行实现。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesUniqueUnityLegacyCarrier()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial framework = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.UnityFrameworkReferencePath);

            CollectionAssert.AreEqual(
                new[] { project.UnityRuntimeFrameworkPath },
                framework.ImplementationPaths.ToArray());
            string referenceIdentity = System.Reflection.AssemblyName
                .GetAssemblyName(project.UnityFrameworkReferencePath).FullName!;
            Assert.HasCount(1, result.AssemblyRedirects);
            Assert.AreEqual(project.UnityRuntimeFrameworkPath, result.AssemblyRedirects[referenceIdentity]);
        }

        // 检查不能将未知宿主版本套用已审计的装载规则。
        /// <summary>
        /// 未核实的版本必须报告宿主位置。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsUnauditedUnityHostVersion()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                hostFileVersion: "2022.1.0.0");

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "Unity 宿主装载规则尚未审计");
        }

        // 当前 Unity 的非严格装载只按简单名选择唯一真实载体，版本和公钥不决定候选。
        /// <summary>
        /// 验证间接依赖和编译引用共同进入实际运行文件，不依赖旧框架表。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesUniqueLegacyCarrierAcrossVersionAndPublicKey()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                changeRuntimeToken: true);
            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string referenceIdentity = System.Reflection.AssemblyName.GetAssemblyName(project.UnityFrameworkReferencePath).FullName!;
            Assert.AreEqual(project.UnityRuntimeFrameworkPath, result.AssemblyRedirects[referenceIdentity]);
            CollectionAssert.AreEqual(new[] { project.UnityRuntimeFrameworkPath }, result.ExternalAssemblies
                .Single(assembly => assembly.ReferencePath == project.UnityFrameworkReferencePath).ImplementationPaths.ToArray());
        }

        // 检查附近同名元数据的改名文件不冒充已预载程序集。
        /// <summary>
        /// 未显式引用的改名邻接文件不能阻断唯一的运行载体。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncExcludesRenamedUnreferencedCarrier()
        {
            using TestProject project = TestProject.CreateWithLegacyCarrierCollision(explicitReference: false);
            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string referenceIdentity = System.Reflection.AssemblyName.GetAssemblyName(project.UnityFrameworkReferencePath).FullName!;
            Assert.AreEqual(project.UnityRuntimeFrameworkPath, result.AssemblyRedirects[referenceIdentity]);
        }

        // 检查缺少实际 Unity 宿主时不猜测装载规则。
        /// <summary>
        /// 验证只有库目录不能证明宿主版本。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMissingUnityHost()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                includeHost: false);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "Unity 宿主装载规则尚未审计");
        }

        // 检查宿主文件存在但实际 Mono 运行库缺失时拒绝装载。
        /// <summary>
        /// 验证必要运行文件缺失时保留宿主诊断。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsMissingUnityRuntime()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping();
            File.Delete(Path.Combine(project.RootPath, "Data", "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll"));

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "Unity 宿主装载规则尚未审计");
        }

        // 检查Unity参考文件能连接到当前运行配置的纯转交门面。
        /// <summary>
        /// 验证门面及其转交目标进入材料，参考文件不冒充实现。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesCurrentUnityRuntimeForwardingFacade()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true);

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial facade = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.UnityReferencePath);

            CollectionAssert.AreEqual(
                new[] { project.UnityRuntimeFacadePath, project.UnityRuntimeTargetPath },
                facade.ImplementationPaths.ToArray());
            Assert.IsFalse(facade.ImplementationPaths.Contains(
                project.UnityReferencePath,
                StringComparer.OrdinalIgnoreCase));
        }

        // 检查纯参考门面不会取代当前Unity中同身份的真实定义。
        /// <summary>
        /// 验证编译参考与运行实现分工明确，函数和类型只读取真实运行文件。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesRuntimeDefinitionForPureUnityReference()
        {
            using TestProject project = TestProject.CreateWithUnityPureReferenceAndRuntimeDefinition();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial reference = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.UnityReferencePath);

            CollectionAssert.AreEqual(
                new[] { project.UnityRuntimeFacadePath },
                reference.ImplementationPaths.ToArray());
            CollectionAssert.DoesNotContain(
                reference.ImplementationPaths.ToArray(),
                project.UnityReferencePath);
        }

        // 检查类型转交目标不会把参考门面当成运行载体。
        /// <summary>
        /// 验证同身份参考门面和运行门面同时存在时只沿当前运行目录闭合。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesRuntimeCarrierForForwardingTarget()
        {
            using TestProject project = TestProject.CreateWithUnityForwardingTargetCollision();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial reference = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.UnityReferencePath);

            CollectionAssert.AreEqual(
                new[]
                {
                    project.UnityReferencePath,
                    project.UnityRuntimeTargetFacadePath,
                    project.UnityRuntimeTargetPath,
                },
                reference.ImplementationPaths.ToArray());
            CollectionAssert.DoesNotContain(
                reference.ImplementationPaths.ToArray(),
                project.UnityReferenceTargetFacadePath);
        }

        // 检查运行载体缺失时材料保留参考门面而不伪造目标。
        /// <summary>
        /// 验证参考目录的同身份副本不能冒充运行文件。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncKeepsReferenceFacadeWhenRuntimeCarrierIsMissing()
        {
            using TestProject project = TestProject.CreateWithUnityForwardingTargetCollision(
                includeRuntimeCarrier: false);

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial reference = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.UnityReferencePath);

            CollectionAssert.AreEqual(new[] { project.UnityReferencePath }, reference.ImplementationPaths.ToArray());
        }

        // 检查门面中未使用的缺失转交分支不会阻断已承载类型。
        /// <summary>
        /// 验证材料阶段保留门面与UsedType载体，不提前要求MissingType载体。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncKeepsAvailableForwardersWhenAnotherTargetIsMissing()
        {
            using TestProject project = TestProject.CreateWithPartiallyMissingForwarders();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial facade = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.ExternalReferencePath);

            CollectionAssert.AreEqual(
                new[] { project.ExternalReferencePath, project.ForwardTargetPath },
                facade.ImplementationPaths.ToArray());
            CollectionAssert.DoesNotContain(facade.ImplementationPaths.ToArray(), project.MissingForwardTargetPath);
        }

        // 检查参考门面的兄弟参考定义不会进入运行候选表。
        /// <summary>
        /// 验证门面本身保留转交记录，而同目录的目标桩不能被目录按需载入。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncExcludesReferenceSiblingFromRuntimeLookupPaths()
        {
            using TestProject project = TestProject.CreateWithReferenceSiblingForwarderStub();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial facade = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.UnityReferencePath);

            CollectionAssert.AreEqual(new[] { project.UnityReferencePath }, facade.ImplementationPaths.ToArray());
            CollectionAssert.DoesNotContain(result.AssemblyLookupPaths.ToArray(), project.UnityReferenceSiblingPath);
        }

        // 检查材料阶段只沿当前Unity运行门面的转交分支。
        /// <summary>
        /// 验证编译参考与运行门面指向不同终点时，以真实运行分支为准。
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task LoadAsyncUsesRuntimeForwardingBranch(bool differentEndpoints)
        {
            using TestProject project = TestProject.CreateWithConvergingForwardingBranches(differentEndpoints);

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial alias = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.ConvergingReferenceAliasPath);
            ExternalAssemblyMaterial rootShim = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.ConvergingRootShimPath);

            CollectionAssert.AreEqual(
                new[]
                {
                    project.ConvergingRuntimeAliasPath,
                    project.ConvergingMiddleBPath,
                    differentEndpoints ? project.ConvergingAlternateFinalPath : project.UnityRuntimeTargetPath,
                },
                alias.ImplementationPaths.ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    project.ConvergingRootShimPath,
                    project.ConvergingRuntimeAliasPath,
                    project.ConvergingMiddleBPath,
                    differentEndpoints ? project.ConvergingAlternateFinalPath : project.UnityRuntimeTargetPath,
                },
                rootShim.ImplementationPaths.ToArray());
        }

        // 检查运行目录中的普通文件和门面共用装载规则。
        /// <summary>
        /// 不要求转交文件必须在 Facades 子目录。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesForwardingFacadeInRuntimeDirectory()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true,
                placeInFacadeDirectory: false);

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            CollectionAssert.AreEqual(new[] { Path.Combine(Path.GetDirectoryName(project.UnityRuntimeTargetPath)!, "UnityFacade.dll"), project.UnityRuntimeTargetPath },
                result.ExternalAssemblies.Single(assembly => assembly.ReferencePath == project.UnityReferencePath).ImplementationPaths.ToArray());
        }

        // 检查当前宿主不按主版本排除唯一运行门面。
        /// <summary>
        /// 实际类型继续由运行门面转交。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesOlderUnityRuntimeFacadeMajorVersion()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true,
                referenceVersion: "3.0.0.0",
                runtimeVersion: "2.1.0.0");

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            CollectionAssert.AreEqual(new[] { project.UnityRuntimeFacadePath, project.UnityRuntimeTargetPath },
                result.ExternalAssemblies.Single(assembly => assembly.ReferencePath == project.UnityReferencePath).ImplementationPaths.ToArray());
        }

        // 当前宿主按请求文件名打开库，不要求其元数据名称一致。
        /// <summary>
        /// 名称不同仍沿实际载体的类型转交记录读取。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesProbedFileWithDifferentAssemblyName()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true,
                runtimeAssemblyName: "DifferentFacade");

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            CollectionAssert.AreEqual(new[] { project.UnityRuntimeFacadePath, project.UnityRuntimeTargetPath },
                result.ExternalAssemblies.Single(assembly => assembly.ReferencePath == project.UnityReferencePath).ImplementationPaths.ToArray());
        }

        // 检查普通业务程序集仍要求完整版本一致。
        /// <summary>
        /// 验证Unity门面规则不会放宽项目普通动态链接库。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsVersionChangeForOrdinaryAssembly()
        {
            using TestProject project = TestProject.CreateWithVersionedExternalReference();

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份");
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
            ExternalAssemblyMaterial external = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.ExternalAssemblyPath);

            CollectionAssert.AreEqual(
                new[] { project.ExternalAssemblyPath, project.ForwardTargetPath },
                external.ImplementationPaths.ToArray());
        }

        // 检查外部程序集的普通间接依赖只进入查找表而不被提前分析。
        /// <summary>
        /// 验证不是编译直接引用的基类程序集也能参与后续函数关系闭合。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncKeepsTransitiveDependencyAsLookupOnly()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            CollectionAssert.DoesNotContain(
                result.ExternalAssemblies.SelectMany(assembly => assembly.ImplementationPaths).ToArray(),
                project.ForwardTargetPath);
            CollectionAssert.Contains(result.AssemblyLookupPaths.ToArray(), project.ForwardTargetPath);
        }

        // 检查身份错误的候选不会被材料阶段当成转交目标。
        /// <summary>
        /// 验证同名路径不能替代身份不同的转交目标，但未实际使用时允许后续按需闭合。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncKeepsForwarderWhenOnlyMismatchedTargetExists()
        {
            using TestProject project = TestProject.CreateWithMismatchedForwardTarget();

            MaterialSet result = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            ExternalAssemblyMaterial forwarder = result.ExternalAssemblies.Single(assembly =>
                assembly.ReferencePath == project.ExternalAssemblyPath);

            CollectionAssert.AreEqual(new[] { project.ExternalAssemblyPath }, forwarder.ImplementationPaths.ToArray());
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

        // 验证保留两次构建材料时，以 Unity 实际使用的构建记录选择当前参数。
        /// <summary>复刻 khengine 同时保留普通和调试构建的情况，不按修改时间猜选。</summary>
        [TestMethod]
        public async Task LoadAsyncSelectsRecordedUnityBuildWhenOldArtifactsRemain()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            string second = project.CopyRootResponseToSecondBuild();
            File.AppendAllText(second, Environment.NewLine + "/define:CURRENT_BUILD" + Environment.NewLine, encoding: new System.Text.UTF8Encoding(false));
            File.SetLastWriteTimeUtc(project.RootResponsePath, DateTime.UtcNow.AddHours(1));
            string build = Path.GetFileName(Path.GetDirectoryName(second)!);
            File.WriteAllText(Path.Combine(project.RootPath, "Library", "Bee", build), "test graph", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project.RootPath, "Library", "Bee", "tundra.log.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    msg = "init",
                    dagFile = "Library/Bee/" + build,
                    targets = new[] { "ScriptAssemblies" },
                }) + Environment.NewLine, encoding: new System.Text.UTF8Encoding(false));

            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));

            SourceAssemblyMaterial root = material.SourceAssemblies.Single(source => source.IsReportAssembly);
            var options = (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)root.Compilation.SyntaxTrees.First().Options;
            CollectionAssert.Contains(options.PreprocessorSymbolNames.ToArray(), "CURRENT_BUILD");
        }

        // 验证当前构建缺材料时，不拿唯一残留的旧编译参数继续分析。
        /// <summary>当前记录优先于旧文件，非脚本构建记录也不能猜成有效输入。</summary>
        [TestMethod]
        [DataRow("ScriptAssemblies", "当前构建缺少响应文件")]
        [DataRow("Player", "不是脚本程序集构建")]
        public async Task LoadAsyncRejectsUnusableRecordedBuildWithoutSelectingOldResponse(string target, string message)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            File.WriteAllText(Path.Combine(project.RootPath, "Library", "Bee", "missing.dag"), "test graph", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project.RootPath, "Library", "Bee", "tundra.log.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    msg = "init",
                    dagFile = "Library/Bee/missing.dag",
                    targets = new[] { target },
                }) + Environment.NewLine, encoding: new System.Text.UTF8Encoding(false));

            AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(error.Message, message);
            StringAssert.Contains(error.Message, "tundra.log.json");
        }

        // 验证残留参数和日志不能替代已经缺失的当前构建图。
        /// <summary>已删除的构建图不再提供有效材料选择证据。</summary>
        [TestMethod]
        public async Task LoadAsyncRejectsRecordedBuildWithoutItsGraph()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            string graph = "Library/Bee/" + Path.GetFileName(Path.GetDirectoryName(project.RootResponsePath)!);
            File.WriteAllText(Path.Combine(project.RootPath, "Library", "Bee", "tundra.log.json"),
                System.Text.Json.JsonSerializer.Serialize(new { msg = "init", dagFile = graph, targets = new[] { "ScriptAssemblies" } }), encoding: new System.Text.UTF8Encoding(false));

            AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(error.Message, "构建图不存在");
            StringAssert.Contains(error.Message, "tundra.log.json");
        }

        // 验证不完整或格式错误的 Unity 记录能直接定位文件，而不是继续选旧材料。
        /// <summary>所有缺失字段及类型错误均作为输入错误停止分析。</summary>
        [TestMethod]
        [DataRow("")]
        [DataRow("{")]
        [DataRow("[]")]
        [DataRow("{}")]
        [DataRow("{\"msg\":0,\"dagFile\":\"x\",\"targets\":[\"ScriptAssemblies\"]}")]
        [DataRow("{\"msg\":\"init\",\"targets\":[\"ScriptAssemblies\"]}")]
        [DataRow("{\"msg\":\"init\",\"dagFile\":0,\"targets\":[\"ScriptAssemblies\"]}")]
        [DataRow("{\"msg\":\"init\",\"dagFile\":\"x\",\"targets\":\"ScriptAssemblies\"}")]
        public async Task LoadAsyncReportsInvalidBuildHeaderWithItsPath(string header)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            string log = Path.Combine(project.RootPath, "Library", "Bee", "tundra.log.json");
            File.WriteAllText(log, header, encoding: new System.Text.UTF8Encoding(false));

            AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(error.Message, log);
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

            Assert.IsFalse(singleAssembly.SourceAssemblies.Single().Compilation.Options.ConcurrentBuild);
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
