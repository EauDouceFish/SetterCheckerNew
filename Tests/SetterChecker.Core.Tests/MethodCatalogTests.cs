using SetterChecker.Core;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证函数总表面向真实源码和普通托管文件的公开能力。
    /// </summary>
    [TestClass]
    public sealed class MethodCatalogTests
    {
        // 检查源码函数种类、标签、参数和统计范围。
        /// <summary>
        /// 验证总表保留全部分析节点，但只报告日志工具处理的函数。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncCatalogsSourceMethodsAndReportableKinds()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                using System;

                public sealed class NoLogTrackAttribute : Attribute
                {
                    // 建立无参数标签。
                    public NoLogTrackAttribute() { }
                    // 建立带原因标签。
                    public NoLogTrackAttribute(string reason) { }
                }

                public interface IWorker
                {
                    // 声明接口函数。
                    void Run();
                }

                [NoLogTrack]
                public sealed class Sample : IWorker
                {
                    // 声明带标签的普通函数。
                    [NoLogTrack("reason")]
                    public void Plain(int value) { }
                    // 声明类型转换函数。
                    public static explicit operator int(Sample value) => 0;
                    public int Value { get; set; }
                    public event Action Changed { add { } remove { } }
                    // 声明构造函数。
                    public Sample() { }
                    // 声明被日志工具排除的单例函数。
                    public static Sample getInstance() => new();
                    // 声明被日志工具排除的代理函数。
                    public void BaseProxy_Test() { }
                    // 实现接口函数。
                    void IWorker.Run() { }
                    // 声明包含内部函数的普通函数。
                    public void WithLocal()
                    {
                        // 声明局部函数。
                        void Local() { }
                        // 声明匿名函数。
                        Action callback = () => { };
                        Local();
                        callback();
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry plain = result.Methods.Single(method =>
                method.TypeName == "Sample" && method.Name == "Plain");
            string[] reportableNames = result.Methods.Where(method => method.IsReportable)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "IWorker.Run",
                    "Native",
                    "Plain",
                    "WithLocal",
                    "add_Changed",
                    "op_Explicit",
                    "remove_Changed",
                    "set_Value",
                }.Where(name => name != "Native").ToArray(),
                reportableNames);
            Assert.AreEqual("System.Int32", plain.Parameters.Single().TypeName);
            Assert.IsTrue(plain.MethodAttributes.Any(attribute =>
                attribute.TypeName == "NoLogTrackAttribute" && attribute.HasArguments));
            Assert.IsTrue(plain.TypeAttributes.Any(attribute =>
                attribute.TypeName == "NoLogTrackAttribute"));
            Assert.IsTrue(result.Methods.Any(method => method.Kind == CatalogMethodKind.LocalFunction));
            Assert.IsTrue(result.Methods.Any(method => method.Kind == CatalogMethodKind.AnonymousFunction));
        }

        // 检查源码类型关系和函数重写索引。
        /// <summary>
        /// 验证接口实现、继承和重写目标都可以直接查到。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncIndexesSourceRelations()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public interface IWorker { void Run(); }
                public class Base { public virtual void Work() { } }
                public sealed class Derived : Base, IWorker
                {
                    public override void Work() { }
                    public void Run() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry contract = result.Types.Single(type => type.Name == "IWorker");
            TypeEntry baseType = result.Types.Single(type => type.Name == "Base");
            TypeEntry derived = result.Types.Single(type => type.Name == "Derived");
            MethodEntry baseMethod = result.GetMethods(baseType).Single(method => method.Name == "Work");
            MethodEntry derivedMethod = result.GetMethods(derived).Single(method => method.Name == "Work");

            CollectionAssert.Contains(
                result.ImplementingTypesByInterfaceId[contract.LogicalId]
                    .Select(type => type.Id).ToArray(),
                derived.Id);
            CollectionAssert.Contains(
                result.DerivedTypesByBaseId[baseType.LogicalId].Select(type => type.Id).ToArray(),
                derived.Id);
            CollectionAssert.Contains(
                result.OverridesByMethodId[baseMethod.LogicalId]
                    .Select(method => method.Id).ToArray(),
                derivedMethod.Id);
        }

        // 检查 Cecil 对普通、泛型、接口、重写和访问器函数的读取。
        /// <summary>
        /// 验证真实托管文件按类型按需展开并保留准确关系和元数据标记。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsCatalogsManagedTypesAndRelations()
        {
            using TestProject project = TestProject.CreateWithExternalMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry contract = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "IExternal");
            TypeEntry baseType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "ExternalBase");
            TypeEntry derivedType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "ExternalType");
            TypeEntry genericType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "GenericSelf");
            TypeEntry accessorType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "ExplicitAccessors");
            TypeEntry multiBaseType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "MultiVirtualBase");
            TypeEntry multiDerivedType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "MultiVirtualDerived");
            TypeEntry returnsIntType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "IReturnsInt");
            TypeEntry returnsStringType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "IReturnsString");
            TypeEntry returnImplementationType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "ReturnImplementation");
            TypeEntry newSlotType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "NewSlotExternal");
            TypeEntry reimplementedType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath && type.Name == "ReimplementedExternal");
            MethodEntry contractApply = result.GetMethods(contract).Single(method => method.Name == "Apply");
            MethodEntry baseChange = result.GetMethods(baseType).Single(method => method.Name == "Change");
            MethodEntry derivedChange = result.GetMethods(derivedType).Single(method => method.Name == "Change");
            MethodEntry genericEcho = result.GetMethods(genericType).Single(method => method.Name == "Echo");
            CatalogMethodKind[] accessors = result.GetMethods(accessorType)
                .Where(method => method.Name.Contains("Value", StringComparison.Ordinal)
                    || method.Name.Contains("Changed", StringComparison.Ordinal))
                .Select(method => method.Kind)
                .Order()
                .ToArray();
            MethodEntry firstBase = result.GetMethods(multiBaseType).Single(method => method.Name == "First");
            MethodEntry secondBase = result.GetMethods(multiBaseType).Single(method => method.Name == "Second");
            MethodEntry firstDerived = result.GetMethods(multiDerivedType).Single(method => method.Name == "First");
            MethodEntry intContract = result.GetMethods(returnsIntType).Single();
            MethodEntry stringContract = result.GetMethods(returnsStringType).Single();
            MethodEntry publicRead = result.GetMethods(returnImplementationType).Single(method => method.Name == "Read");
            MethodEntry newSlotApply = result.GetMethods(newSlotType).Single(method => method.Name == "Apply");
            MethodEntry reimplementedApply = result.GetMethods(reimplementedType).Single(method => method.Name == "Apply");

            Assert.IsTrue(derivedChange.MetadataToken > 0);
            Assert.AreEqual("System.Int32", derivedChange.Parameters.Single().TypeId);
            CollectionAssert.Contains(derivedChange.RelatedMethodIds.ToArray(), baseChange.LogicalId);
            CollectionAssert.Contains(
                result.GetMethods(baseType).Single(method => method.Name == "Apply")
                    .RelatedMethodIds.ToArray(),
                contractApply.LogicalId);
            StringAssert.EndsWith(genericEcho.Parameters.Single().TypeId, "<!0>");
            CollectionAssert.AreEquivalent(
                new[]
                {
                    CatalogMethodKind.PropertyGetter,
                    CatalogMethodKind.PropertySetter,
                    CatalogMethodKind.EventAdder,
                    CatalogMethodKind.EventRemover,
                },
                accessors);
            CollectionAssert.Contains(firstDerived.RelatedMethodIds.ToArray(), firstBase.LogicalId);
            CollectionAssert.DoesNotContain(firstDerived.RelatedMethodIds.ToArray(), secondBase.LogicalId);
            CollectionAssert.Contains(publicRead.RelatedMethodIds.ToArray(), intContract.LogicalId);
            CollectionAssert.DoesNotContain(publicRead.RelatedMethodIds.ToArray(), stringContract.LogicalId);
            CollectionAssert.DoesNotContain(newSlotApply.RelatedMethodIds.ToArray(), contractApply.LogicalId);
            CollectionAssert.Contains(reimplementedApply.RelatedMethodIds.ToArray(), contractApply.LogicalId);
        }

        // 检查参考程序集中的转交类型连接到实际运行文件。
        /// <summary>
        /// 验证类型转交身份可以定位到真实类型及函数。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncMapsForwardedTypeToImplementation()
        {
            using TestProject project = TestProject.CreateWithForwardedType();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath && type.Name == "ForwardedType");
            string alias = implementation.AliasIds.Single();

            CollectionAssert.Contains(
                result.TypesByLogicalId[alias].Select(type => type.Id).ToArray(),
                implementation.Id);
            Assert.IsTrue(result.GetMethods(implementation).Any());
        }

        // 检查与当前分析无关的缺失外部继承目标会被明确登记。
        /// <summary>
        /// 验证函数总表保留缺失关系证据，供真正需要层级解析时再停止。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRecordsMissingExternalTypeRelation()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            File.Delete(project.ForwardTargetPath);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);

            Assert.IsTrue(result.MissingTypeRelations.Any(relation =>
                relation.OwnerTypeName == "TransitiveSamples.DerivedType"
                && relation.TargetTypeId.Contains("TransitiveSamples.BaseType", StringComparison.Ordinal)));
            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.DerivedType");
            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                result.RequireClosedHierarchy(derived));

            StringAssert.Contains(exception.Message, project.ExternalAssemblyPath);
        }

        // 检查实际命中间接程序集时才读取其中的类型。
        /// <summary>
        /// 验证候选文件启动时不读取，命中后加入统一类型清单。
        /// </summary>
        [TestMethod]
        public async Task LoadAssemblyTypesLoadsTransitiveAssemblyOnDemand()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);

            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.DerivedType");

            result.RequireClosedHierarchy(derived);

            Assert.IsTrue(result.Types.Any(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "TransitiveSamples.BaseType"));
            Assert.IsFalse(result.MissingTypeRelations.Any(relation =>
                relation.OwnerTypeId == derived.Id));
        }

        // 检查闭合层级后会重新计算子类的重写关系。
        /// <summary>
        /// 验证首次 GetMethods 只读取已知声明，显式闭合后再读取能补齐关系。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsRefreshesTransitiveOverrideAfterClosure()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derivedType = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.DerivedType");

            Assert.IsFalse(result.Types.Any(type =>
                type.FullName == "TransitiveSamples.BaseType"));

            MethodEntry firstRead = result.GetMethods(derivedType).Single(method =>
                method.Name == "Touch");
            Assert.IsFalse(result.Types.Any(type =>
                type.FullName == "TransitiveSamples.BaseType"));
            Assert.AreEqual(0, firstRead.RelatedMethodIds.Count);

            result.RequireClosedHierarchy(derivedType);
            TypeEntry baseType = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.BaseType");
            MethodEntry baseMethod = result.GetMethods(baseType).Single(method =>
                method.Name == "Touch");
            MethodEntry secondRead = result.GetMethods(derivedType).Single(method =>
                method.Name == "Touch");

            CollectionAssert.Contains(secondRead.RelatedMethodIds.ToArray(), baseMethod.LogicalId);
        }

        // 检查四路并发读取不会在闭合后写回过期的重写关系。
        /// <summary>
        /// 验证四路并发首读后闭合层级，再并发读取的每份结果都已补齐。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsRefreshesTransitiveOverrideForConcurrentReaders()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                4));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 4);
            TypeEntry derivedType = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.DerivedType");
            MethodEntry[] firstReads = new MethodEntry[32];

            Parallel.For(
                0,
                firstReads.Length,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                index => firstReads[index] = result.GetMethods(derivedType).Single(method =>
                    method.Name == "Touch"));

            Assert.IsTrue(firstReads.All(method => method.RelatedMethodIds.Count == 0));
            result.RequireClosedHierarchy(derivedType);
            TypeEntry baseType = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.BaseType");
            string baseMethodId = result.GetMethods(baseType).Single(method =>
                method.Name == "Touch").LogicalId;
            MethodEntry[] secondReads = new MethodEntry[32];

            Parallel.For(
                0,
                secondReads.Length,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                index => secondReads[index] = result.GetMethods(derivedType).Single(method =>
                    method.Name == "Touch"));

            Assert.IsTrue(secondReads.All(method => method.RelatedMethodIds.Contains(
                baseMethodId,
                StringComparer.Ordinal)));
        }

        // 检查构造类型实参不会替换函数自己的泛型参数。
        /// <summary>
        /// 验证真实动态链接库中 GenericBase&lt;int&gt; 的泛型重写仍使用 !!0。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsKeepsMethodGenericParameterWhenTypeIsConstructed()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derivedType = result.Types.Single(type =>
                type.Name == "GenericDerived");

            result.RequireClosedHierarchy(derivedType);
            TypeEntry baseType = result.Types.Single(type =>
                type.Name == "GenericBase");
            MethodEntry baseMethod = result.GetMethods(baseType).Single(method =>
                method.Name == "Echo");
            MethodEntry derivedMethod = result.GetMethods(derivedType).Single(method =>
                method.Name == "Echo");

            Assert.AreEqual("!!0", baseMethod.Parameters.Single().TypeId);
            Assert.AreEqual("!!0", baseMethod.ReturnTypeId);
            Assert.AreEqual("!!0", derivedMethod.Parameters.Single().TypeId);
            Assert.AreEqual("!!0", derivedMethod.ReturnTypeId);
            CollectionAssert.Contains(
                derivedMethod.RelatedMethodIds.ToArray(),
                baseMethod.LogicalId);
        }

        // 检查延迟载入类型时可以同时读取现有函数和类型索引。
        /// <summary>
        /// 验证延迟载入采用完整快照，不会把正在修改的集合暴露给其他线程。
        /// </summary>
        [TestMethod]
        public async Task LoadAssemblyTypesIsStableWhileOtherThreadsReadIndexes()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.DerivedType");

            Parallel.For(
                0,
                32,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                index =>
                {
                    if (index % 2 == 0)
                    {
                        result.RequireClosedHierarchy(derived);
                        return;
                    }

                    _ = result.Types.Count;
                    _ = result.TypesById.Count;
                    _ = result.TypesByLogicalId.Count;
                    _ = result.MissingTypeRelations.Count;
                    _ = result.GetMethods(derived).Count;
                });

            string[] typeIds = result.Types.Select(type => type.Id).ToArray();
            CollectionAssert.AreEqual(
                typeIds.Order(StringComparer.Ordinal).ToArray(),
                typeIds);
            Assert.AreEqual(typeIds.Length, typeIds.Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(1, result.Types.Count(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "TransitiveSamples.BaseType"));
        }

        // 检查同一动态链接库的多个类型可以按 -j 数量并行请求函数。
        /// <summary>
        /// 验证 Cecil 模块读取被正确协调且每个类型结果保持固定顺序。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsIsStableForConcurrentTypesInOneAssembly()
        {
            using TestProject project = TestProject.CreateWithExternalMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry[] types = result.Types.Where(type =>
                    type.AssemblyPath == project.ExternalAssemblyPath)
                .ToArray();
            Dictionary<string, string[]> actual = new(StringComparer.Ordinal);

            Parallel.ForEach(
                types,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                type =>
                {
                    string[] methodIds = result.GetMethods(type)
                        .Select(method => method.Id)
                        .ToArray();

                    lock (actual)
                    {
                        actual.Add(type.Id, methodIds);
                    }
                });

            foreach (TypeEntry type in types)
            {
                CollectionAssert.AreEqual(
                    actual[type.Id],
                    result.GetMethods(type).Select(method => method.Id).ToArray());
            }
        }

        // 检查目标包内的源码依赖进入最终统计。
        /// <summary>
        /// 验证统计范围按包目录判断而不依赖程序集名称。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncReportsSourceDependenciesInsideTargetPackage()
        {
            using TestProject project = TestProject.CreateWithTargetPackageDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);

            Assert.IsTrue(result.Methods.Single(method =>
                method.Name == "DependencyMethod").IsReportable);
        }

        // 对比单线程和四线程建立的稳定清单。
        /// <summary>
        /// 验证并行数量不会改变函数和类型顺序。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsDeterministicOrderAcrossJobCounts()
        {
            using TestProject project = TestProject.CreateWithExternalMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalog catalog = new();

            MethodCatalogResult oneJob = await catalog.BuildAsync(material, 1);
            MethodCatalogResult fourJobs = await catalog.BuildAsync(material, 4);

            CollectionAssert.AreEqual(
                oneJob.Methods.Select(method => method.Id).ToArray(),
                fourJobs.Methods.Select(method => method.Id).ToArray());
            CollectionAssert.AreEqual(
                oneJob.Types.Select(type => type.Id).ToArray(),
                fourJobs.Types.Select(type => type.Id).ToArray());
        }
    }
}
