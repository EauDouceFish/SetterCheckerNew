using SetterChecker.Core;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证 Unity 材料读取的外部行为。
    /// </summary>
    [TestClass]
    public sealed class MaterialLoaderTests
    {
        // 验证纯参考门面不能绕过同名运行文件的身份检查。
        /// <summary>
        /// 同名运行文件身份不兼容时，不能借参考转交绕过运行映射检查。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsIncompatibleRuntimeBeforeFollowingReferenceForwarders()
        {
            using TestProject project = TestProject.CreateWithForwardingUnityReference();

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份不同");
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
                "public sealed class DependencyType { public static int NewApi() { return 42; } }");
            File.WriteAllText(project.RootSourcePath,
                "public sealed class RootType { public static int Read() { return DependencyType.NewApi(); } }");

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
                "public sealed class ReplacementType { }");
            File.WriteAllText(project.RootSourcePath,
                "public sealed class RootType { public static object Read() { return new DependencyType(); } }");

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
            File.WriteAllText(extraPath, "public sealed class ExtraSource { }");
            File.AppendAllText(project.RootResponsePath, Environment.NewLine + "\"" + extraPath + "\"" + Environment.NewLine);

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
        /// 将 Core 一万行上限作为自动化验收条件，计入注释和空行。
        /// </summary>
        [TestMethod]
        public void CoreSourceStaysWithinLineLimit()
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "SetterChecker.slnx")))
            {
                repository = repository.Parent;
            }

            Assert.IsNotNull(repository, "无法从测试输出定位当前解决方案。");
            string corePath = Path.Combine(repository.FullName, "Src", "SetterChecker.Core");
            var files = Directory.EnumerateFiles(corePath, "*.cs", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(corePath, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase)))
                .Select(path => (Name: Path.GetFileName(path), Lines: File.ReadLines(path).Count())).ToArray();

            Assert.IsTrue(files.Sum(file => file.Lines) <= 10_000,
                $"Core 实际 {files.Sum(file => file.Lines)} 行，超过 10000 行；"
                + string.Join("，", files.Select(file => $"{file.Name}: {file.Lines}")));
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

        // 检查Unity运行目录中的普通程序集不能跨版本代替参考文件。
        /// <summary>
        /// 验证只有纯类型转交门面可以使用Unity的门面版本规则。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsVersionChangeForUnityRuntimeImplementation()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: false);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查Unity Mono表内的普通框架程序集按当前运行版本连接。
        /// <summary>
        /// 验证框架表明确的4.2参考可连接到4.0运行实现。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncUsesListedUnityFrameworkVersionRemapping()
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

        // 检查Unity目录不会给未列入Mono表的程序集放宽版本。
        /// <summary>
        /// 验证相同目录和同名候选不能代替框架表证据。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsUnityFrameworkMissingFromRemappingTable()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                includeTableEntry: false);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查框架重映射不会跨过公钥标记差异。
        /// <summary>
        /// 验证Mono表项仍然要求参考与运行文件的公钥标记一致。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsUnityFrameworkWithDifferentPublicKeyToken()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                changeRuntimeToken: true);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查只允许低版本向上重映射的Mono表项。
        /// <summary>
        /// 验证4.2请求不能通过只允许低版本的表项降到4.0。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsUnityFrameworkAgainstOnlyLowerRule()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                onlyLowerVersions: true);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查Unity安装缺少Mono源码证据时不猜测版本关系。
        /// <summary>
        /// 验证缺少当前运行时的映射表时明确失败。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsUnityFrameworkWithoutRemappingEvidence()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                includeEvidence: false);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "Mono框架版本表");
        }

        // 检查选中的Mono运行时行不能含未解析版本槽。
        /// <summary>
        /// 验证NOT_AVAIL不会被删除后造成后续索引移位。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsIncompleteSelectedRuntimeVersionSet()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                runtimeRowContainsUnavailableVersion: true);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2)));

            StringAssert.Contains(exception.Message, "Mono框架版本表");
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
            CollectionAssert.AreEqual(
                new[] { project.UnityRuntimeFacadePath },
                result.UnityRuntimeFacadePaths.ToArray());
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

        // 检查Facades以外的转交文件不能启用Unity版本规则。
        /// <summary>
        /// 验证只有当前运行配置的Facades目录具备门面资格。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsForwardingFacadeOutsideRuntimeFacadeDirectory()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true,
                placeInFacadeDirectory: false);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查运行门面不能满足更高主版本的参考请求。
        /// <summary>
        /// 验证Unity门面只允许候选主版本不低于请求版本。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsOlderUnityRuntimeFacadeMajorVersion()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true,
                referenceVersion: "3.0.0.0",
                runtimeVersion: "2.1.0.0");

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份");
        }

        // 检查Unity门面文件名不能代替真实程序集身份。
        /// <summary>
        /// 验证门面规则仍要求程序集名称、区域和公钥标记一致。
        /// </summary>
        [TestMethod]
        public async Task LoadAsyncRejectsUnityFacadeWithDifferentAssemblyName()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true,
                runtimeAssemblyName: "DifferentFacade");

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MaterialLoader().LoadAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    2)));

            StringAssert.Contains(exception.Message, "程序集身份");
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
