using SetterChecker.Core;
using Microsoft.CodeAnalysis;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证函数总表面向真实源码和普通托管文件的公开能力。
    /// </summary>
    [TestClass]
    public sealed class MethodCatalogTests
    {
        // 复刻当前构建中用泛型容器的非泛型嵌套类型作参数的声明。
        /// <summary>外层构造实参和内层类型名称共同参加源码与编译函数匹配。</summary>
        [TestMethod]
        [DataRow("ExternalSamples.Configuration.ReadOnly")]
        [DataRow("ExternalSamples.Container<Sample, int>.Inner<string>.Leaf<bool>")]
        public async Task BuildAsyncMatchesNestedTypeInGenericContainer(string parameterType)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Container<T, U> { public struct ReadOnly { } public class Inner<V> { public class Leaf<W> { } } }
                public class Configuration : Container<Configuration, int> { }
                """);
            project.WriteRootSource("""
                public class Sample { public void Initialize(PARAMETER value) { } }
                """.Replace("PARAMETER", parameterType));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(method => method.Name == "Initialize");
            Assert.IsTrue(method.IsReportable);
            Assert.HasCount(1, method.Parameters);
        }

        // 复刻 FluxyContainer 虚函数接收只读引用参数的声明。
        /// <summary>编译器加上的只读参数修饰不应破坏源码与真实函数的唯一配对。</summary>
        [TestMethod]
        public async Task BuildAsyncMatchesVirtualReadonlyReferenceParameter()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public struct Position { public int X; }
                public class Sample { public virtual int Read(in Position position, int index = 0) => position.X + index; }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(method => method.Name == "Read");
            Assert.AreEqual(RefKind.In, method.Parameters[0].RefKind);
            Assert.IsTrue(method.IsReportable);
            Assert.AreEqual(2, method.Parameters.Count);
        }

        // 完整构建中的普通特性可能接收数组，不能将数组当作标量读取。
        /// <summary>类与方法特性的数组、空数组和 null 参数均保留真实内容。</summary>
        [TestMethod]
        public async Task BuildAsyncReadsArrayAttributeArguments()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                using System;
                public sealed class PayloadAttribute : Attribute { public PayloadAttribute(int[] values) { } }
                [Payload(new[] { 1, 2 })]
                public sealed class Sample
                {
                    [Payload(new int[0])] public void Empty() { }
                    [Payload(null)] public void Missing() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            CollectionAssert.AreEqual(new object[] { 1, 2 }, catalog.Types.Single(type => type.Name == "Sample")
                .SourceSymbol!.GetAttributes().Single().ConstructorArguments.Single().Values.Select(value => value.Value).ToArray());
            Assert.IsTrue(catalog.Methods.Single(method => method.Name == "Empty").SourceSymbol!
                .GetAttributes().Single().ConstructorArguments.Single().Values.IsEmpty);
            Assert.IsTrue(catalog.Methods.Single(method => method.Name == "Missing").SourceSymbol!
                .GetAttributes().Single().ConstructorArguments.Single().IsNull);
        }

        // 复刻同名压缩库不同版本把派生类误挂到错误基类的问题。
        /// <summary>未证明的关系不按名称连接，真实基类按需载入后才建立其精确索引。</summary>
        [TestMethod]
        public async Task BuildAsyncIndexesOnlyProvenPhysicalBaseTypes()
        {
            using TestProject project = TestProject.CreateWithVersionedInheritanceCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = catalog.Types.Single(type => type.FullName == "TransitiveSamples.DerivedType");
            TypeEntry wrong = catalog.Types.Single(type => type.AssemblyPath == Path.Combine(project.RootPath, "WrongForwardTarget.dll")
                && type.FullName == "TransitiveSamples.BaseType");
            Assert.IsFalse(catalog.Types.Any(type => type.AssemblyPath == project.ForwardTargetPath));
            IReadOnlyDictionary<string, IReadOnlyList<TypeEntry>> index = catalog.DerivedTypesByBaseId;
            TypeEntry actual = catalog.Types.Single(type => type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "TransitiveSamples.BaseType");
            Assert.IsTrue(index[actual.Id].Any(type => type.Id == derived.Id));
            Assert.IsFalse(index.GetValueOrDefault(wrong.Id)?.Any(type => type.Id == derived.Id) == true);
        }

        // 验证日志排除范围按真实命名空间匹配，不误伤相似名称和普通嵌套类型。
        /// <summary>复刻两个日志注入器共同排除的 Tss 与 UnityEngine 范围。</summary>
        [TestMethod]
        public async Task BuildAsyncMatchesLogNamespaceExclusionsWithoutGuessingTypeNames()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace Tss { public class Item { public void Run() { } public class Nested { public void Run() { } } } }
                namespace Tss.Child { public class Item { public void Run() { } } }
                namespace UnityEngine { public class Item { public void Run() { } public class Nested { public void Run() { } } } }
                namespace UnityEngine.Child { public class Item { public void Run() { } } }
                namespace MyUnityEngine { public class Item { public void Run() { } } }
                namespace Other.UnityEngine { public class Item { public void Run() { } } }
                namespace OtherTypes
                {
                    public class UnityEngine { public void Run() { } public class Nested { public void Run() { } } }
                    public class Tss { public void Run() { } public class Nested { public void Run() { } } }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] methods = catalog.Methods.Where(method => method.Name == "Run").ToArray();

            Assert.HasCount(12, methods);
            CollectionAssert.AreEquivalent(new[]
            {
                "Tss.Child.Item", "MyUnityEngine.Item", "Other.UnityEngine.Item",
                "OtherTypes.UnityEngine", "OtherTypes.UnityEngine.Nested", "OtherTypes.Tss", "OtherTypes.Tss.Nested",
            }, methods.Where(method => method.IsReportable).Select(method => method.TypeName).ToArray());
        }

        // 验证日志不能注入的外部声明不进入标签统计，但仍保留供调用分析。
        /// <summary>复刻 AIService 外部声明和托管包装，二者不能一起排除。</summary>
        [TestMethod]
        public async Task BuildAsyncExcludesBodylessDeclarationsButKeepsTheirCallers()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                using System.Runtime.CompilerServices;
                using System.Runtime.InteropServices;
                public static class Service
                {
                    [DllImport("SampleNative", EntryPoint = "Update")]
                    public static extern int Native(int value);
                    [MethodImpl(MethodImplOptions.InternalCall)]
                    public static extern int Runtime(int value);
                    public static int Update(int value) => Native(value);
                    public static int Read(int value) => Runtime(value);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] methods = catalog.Methods.Where(method => method.TypeName == "Service").ToArray();

            CollectionAssert.AreEquivalent(new[] { "Read", "Update" },
                methods.Where(method => method.IsReportable).Select(method => method.Name).ToArray());
            Assert.HasCount(4, methods);
            BehaviorReadResult behaviors = await new BehaviorReader().ReadAsync(material, catalog, methods, 2);
            Assert.AreEqual(MethodBodyKind.PlatformInvocation,
                behaviors.MethodsById[methods.Single(method => method.Name == "Native").Id].BodyKind);
            Assert.AreEqual(MethodBodyKind.RuntimeImplementation,
                behaviors.MethodsById[methods.Single(method => method.Name == "Runtime").Id].BodyKind);
            foreach (string caller in new[] { "Read", "Update" })
            {
                Assert.HasCount(1, behaviors.MethodsById[methods.Single(method => method.Name == caller).Id].Calls);
            }
        }

        // 验证真实方法实现表指定了接口成员时，公开同名方法不再占用该接口关系。
        /// <summary>
        /// 源码和 DLL 的类、结构体均按同一条显式接口优先规则建表。
        /// </summary>
        [TestMethod]
        [DataRow("class")]
        [DataRow("struct")]
        public async Task GetMethodsExcludesImplicitRelationWhenExplicitImplementationExists(string kind)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IView<T> { T this[int index] { get; } }
                public KIND Sample<T> : IView<T>
                {
                    public T this[int index] => default;
                    T IView<T>.this[int index] => default;
                }
                """.Replace("KIND", kind, StringComparison.Ordinal));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry[] types = result.Types.Where(type => type.FullName is "SourceSamples.Sample<T>" or "ExternalSamples.Sample<T>").ToArray();
            Assert.HasCount(2, types);

            foreach (TypeEntry type in types)
            {
                MethodEntry[] getters = result.GetMethods(type).Where(method => method.Kind == MethodKind.PropertyGet).ToArray();
                Assert.HasCount(2, getters);
                Assert.IsFalse(getters.Single(method => method.IsPublic).IsVirtual);
                TypeEntry contract = result.Types.Single(item => item.FullName is "SourceSamples.IView<T>" or "ExternalSamples.IView<T>"
                    && item.AssemblyPath == type.AssemblyPath);
                Assert.IsTrue(getters.Single(method => !method.IsPublic).IsVirtual);
                CollectionAssert.Contains(result.ImplementingTypesByInterfaceId[contract.Id].Select(item => item.Id).ToArray(), type.Id);
            }
        }

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
            Assert.AreEqual(Microsoft.CodeAnalysis.SpecialType.System_Int32, plain.SourceSymbol!.Parameters.Single().Type.SpecialType);
            Assert.IsTrue(plain.SourceSymbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass!.ToDisplayString() == "NoLogTrackAttribute" && attribute.ConstructorArguments.Length > 0));
            Assert.IsTrue(plain.SourceSymbol.ContainingType.GetAttributes().Any(attribute =>
                attribute.AttributeClass!.ToDisplayString() == "NoLogTrackAttribute"));
            Assert.IsTrue(result.Methods.Any(method => method.TypeName == "Sample"
                && method.SourceSymbol == null
                && !method.IsReportable));
        }

        // 验证带命名空间和全局限定写法的显式接口函数保留源码信息。
        /// <summary>
        /// 复刻协议生成源码使用 global 限定接口实现的写法。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncPairsGloballyQualifiedExplicitInterfaceMethod()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace Contracts
                {
                    public interface IExtension { object GetExtension(bool create); }
                }
                namespace Samples
                {
                    public sealed class Message : global::Contracts.IExtension
                    {
                        object global::Contracts.IExtension.GetExtension(bool create) => null;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);

            MethodEntry method = result.Methods.Single(item => item.TypeName == "Samples.Message"
                && item.Name == "global::Contracts.IExtension.GetExtension");
            Assert.IsNotNull(method.SourceSymbol);
            Assert.IsTrue(method.IsReportable);
            Assert.AreEqual(Microsoft.CodeAnalysis.SpecialType.System_Boolean, method.SourceSymbol!.Parameters.Single().Type.SpecialType);
            Assert.IsTrue(method.MetadataToken > 0);
        }

        // 验证接口和重写关系使用转交后的真实参数及返回类型。
        /// <summary>
        /// 同时覆盖隐式实现、显式实现、泛型接口和普通重写。
        /// </summary>
        [TestMethod]
        [DataRow("Worker", "ForwardedSignature.IUse")]
        [DataRow("ExplicitWorker", "ForwardedSignature.IUse")]
        [DataRow("GenericWorker", "ForwardedSignature.IGenericUse<T>")]
        [DataRow("ExplicitGenericWorker", "ForwardedSignature.IGenericUse<T>")]
        [DataRow("DerivedWorker", "ForwardedSignature.Base")]
        [DataRow("NestedWorker<T>", "ForwardedSignature.IGenericUse<T>")]
        [DataRow("ExplicitNestedWorker<T>", "ForwardedSignature.IGenericUse<T>")]
        [DataRow("DerivedGenericWorker<T>", "ForwardedSignature.GenericBase<T>")]
        public async Task BuildAsyncResolvesForwardedImplementationSignatures(string workerName, string contractName)
        {
            using TestProject project = TestProject.CreateWithForwardedMethodSignature();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry worker = result.Types.Single(type => type.FullName == workerName);
            result.RequireClosedHierarchy(worker);
            TypeEntry contract = result.Types.Single(type => type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == contractName);
            MethodEntry implementation = result.GetMethods(worker).Single(method =>
                method.Name.EndsWith("Convert", StringComparison.Ordinal));
            MethodEntry declaration = result.GetMethods(contract).Single(method => method.Name == "Convert");

            MethodEntry caller = result.GetMethods(result.Types.Single(type => type.Name == "SignatureConsumer"))
                .Single(method => method.Name == "Dispatch" + workerName.Replace("<T>", string.Empty));
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, result, new[] { caller }, 2);
            ResolvedCall dispatch = calls.Calls.Single(call => call.CallerMethodId == caller.Id && call.Call.Kind == BehaviorCallKind.Virtual);
            Assert.AreEqual(implementation.Id, dispatch.Targets.Single().MethodId);
            Assert.AreEqual(project.ForwardTargetPath, declaration.AssemblyPath);
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
                result.ImplementingTypesByInterfaceId[contract.Id]
                    .Select(type => type.Id).ToArray(),
                derived.Id);
            CollectionAssert.Contains(
                result.DerivedTypesByBaseId[baseType.Id].Select(type => type.Id).ToArray(),
                derived.Id);
            Assert.IsTrue(derivedMethod.IsVirtual);
            Assert.IsFalse(derivedMethod.IsNewSlot);
            Assert.IsTrue(baseMethod.IsNewSlot);
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
            MethodKind[] accessors = result.GetMethods(accessorType)
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
            Assert.IsFalse(derivedChange.IsNewSlot);
            Assert.IsTrue(baseChange.IsVirtual);
            Assert.IsTrue(contractApply.IsAbstract);
            StringAssert.EndsWith(genericEcho.Parameters.Single().TypeId, "<!0>");
            CollectionAssert.AreEquivalent(
                new[]
                {
                    MethodKind.PropertyGet,
                    MethodKind.PropertySet,
                    MethodKind.EventAdd,
                    MethodKind.EventRemove,
                },
                accessors);
            Assert.AreEqual(intContract.ReturnTypeId, publicRead.ReturnTypeId);
            Assert.AreNotEqual(stringContract.ReturnTypeId, publicRead.ReturnTypeId);
            Assert.IsTrue(newSlotApply.IsNewSlot);
            MethodEntry[] probes = result.GetMethods(result.Types.Single(type => type.Name == "DispatchSamples")).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, result, probes, 2);
            Dictionary<string, string> expected = new()
            {
                ["First"] = firstDerived.Id,
                ["Second"] = secondBase.Id,
                ["Integer"] = publicRead.Id,
                ["Text"] = result.GetMethods(returnImplementationType).Single(method => !method.IsPublic && method.Name.EndsWith("Read")).Id,
                ["NewSlot"] = result.GetMethods(baseType).Single(method => method.Name == "Apply").Id,
                ["BaseSlot"] = result.GetMethods(baseType).Single(method => method.Name == "Apply").Id,
                ["DeclaredSlot"] = newSlotApply.Id,
                ["Reimplemented"] = reimplementedApply.Id,
            };
            foreach (MethodEntry probe in probes)
            {
                ResolvedCall dispatch = calls.Calls.Single(call => call.CallerMethodId == probe.Id && call.Call.Kind == BehaviorCallKind.Virtual);
                Assert.AreEqual(expected[probe.Name], dispatch.Targets.Single().MethodId);
            }
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
            string alias = result.TypesByLogicalId.Single(item =>
                item.Key.Contains("Forwarder", StringComparison.OrdinalIgnoreCase)
                && item.Value.Any(type => type.Id == implementation.Id)).Key;

            CollectionAssert.Contains(
                result.TypesByLogicalId[alias].Select(type => type.Id).ToArray(),
                implementation.Id);
            Assert.IsTrue(result.GetMethods(implementation).Any());
        }

        // 检查外层门面可以穿过中间门面找到真实类型。
        /// <summary>
        /// 验证每层门面身份都映射到同一个最终类型及函数。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncMapsTwoForwardingFacadesToImplementation()
        {
            using TestProject project = TestProject.CreateWithTwoForwardingFacades();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "ForwardedConsumer.Derived");

            result.RequireClosedHierarchy(derived);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "ForwardedNamespace.ForwardedType");
            string[] aliases = result.TypesByLogicalId.Where(item =>
                item.Key != implementation.LogicalId
                && item.Value.Any(type => type.Id == implementation.Id)).Select(item => item.Key).ToArray();
            string outerAlias = aliases.Single(alias =>
                alias.Contains("OuterFacade", StringComparison.OrdinalIgnoreCase));
            string middleAlias = aliases.Single(alias =>
                alias.Contains("MiddleFacade", StringComparison.OrdinalIgnoreCase));

            Assert.HasCount(2, aliases);
            Assert.AreEqual(
                implementation.Id,
                result.TypesByLogicalId[implementation.LogicalId].Single().Id);
            CollectionAssert.Contains(
                result.TypesByLogicalId[outerAlias].Select(type => type.Id).ToArray(),
                implementation.Id);
            CollectionAssert.Contains(
                result.TypesByLogicalId[middleAlias].Select(type => type.Id).ToArray(),
                implementation.Id);
            MethodEntry target = result.GetMethods(implementation).Single(method =>
                method.Name == "Touch");
            MethodEntry derivedTouch = result.GetMethods(derived).Single(method =>
                method.Name == "Touch");

        }

        // 检查同一门面的未使用缺失分支不阻断已承载类型。
        /// <summary>
        /// 验证函数总表保留全部转交记录，并只闭合实际使用的已承载类型。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncClosesUsedTypeWhenAnotherForwarderTargetIsMissing()
        {
            using TestProject project = TestProject.CreateWithPartiallyMissingForwarders();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry root = catalog.Types.Single(type =>
                type.FullName == "PartialForwardingConsumer.RootType");

            catalog.RequireClosedHierarchy(root);
            TypeEntry target = catalog.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "PartialForwarding.UsedType");
            MethodEntry targetRead = catalog.GetMethods(target).Single(method =>
                method.Name == "Read");
            MethodEntry rootRead = catalog.GetMethods(root).Single(method =>
                method.Name == "Read");

            Assert.IsFalse(catalog.Types.Any(type =>
                type.FullName == "PartialForwarding.MissingType"));
        }

        // 检查参考门面有运行载体时只沿真实运行转交链。
        /// <summary>
        /// 验证目录闭合材料已经选定的唯一运行定义。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncAcceptsForwardingBranchesWithOneFinalDefinition()
        {
            using TestProject project = TestProject.CreateWithConvergingForwardingBranches();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = catalog.Types.Single(type =>
                type.FullName == "ConvergingConsumer.Derived");

            catalog.RequireClosedHierarchy(derived);
            TypeEntry finalType = catalog.Types.Single(type =>
                type.AssemblyPath == project.UnityRuntimeTargetPath
                && type.FullName == "Converging.ForwardedType");
            MethodEntry finalTouch = catalog.GetMethods(finalType).Single(method =>
                method.Name == "Touch");
            MethodEntry derivedTouch = catalog.GetMethods(derived).Single(method =>
                method.Name == "Touch");

        }

        // 检查参考门面的终点不会覆盖运行门面的不同终点。
        /// <summary>
        /// 验证目录只使用材料选定的运行转交链。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncUsesRuntimeEndpointWhenReferenceEndpointDiffers()
        {
            using TestProject project = TestProject.CreateWithConvergingForwardingBranches(
                differentEndpoints: true);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = catalog.Types.Single(type =>
                type.FullName == "ConvergingConsumer.Derived");

            catalog.RequireClosedHierarchy(derived);
            TypeEntry target = catalog.Types.Single(type =>
                type.AssemblyPath == project.ConvergingAlternateFinalPath
                && type.FullName == "Converging.ForwardedType");
            MethodEntry targetTouch = catalog.GetMethods(target).Single(method =>
                method.Name == "Touch");
            MethodEntry derivedTouch = catalog.GetMethods(derived).Single(method =>
                method.Name == "Touch");

        }

        // 检查同一别名的任一缺失分支都不会被另一条完整分支掩盖。
        /// <summary>
        /// 验证实际命中多分支转交时必须闭合每一条分支。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsMissingBranchBehindSameForwardedIdentity()
        {
            using TestProject project = TestProject.CreateWithMissingConvergingForwardingBranch();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = catalog.Types.Single(type =>
                type.FullName == "ConvergingConsumer.Derived");

            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                catalog.RequireClosedHierarchy(derived));

            StringAssert.Contains(exception.Message, "ConvergingMiddleB");
        }

        // 检查接口实现会沿查找目录中的间接基类完整闭合。
        /// <summary>
        /// 验证接口索引不会漏掉从延迟基类继承接口的派生类型。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncIndexesInterfaceInheritedFromLookupOnlyBase()
        {
            using TestProject project = TestProject.CreateWithTransitiveInterfaceHierarchy();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = catalog.Types.Single(type =>
                type.FullName == "LookupConsumer.Derived");
            System.Reflection.AssemblyName contractIdentity =
                System.Reflection.AssemblyName.GetAssemblyName(project.ExternalReferencePath);

            TypeEntry contract = catalog.LoadAssemblyTypes(
                    contractIdentity,
                    project.ExternalAssemblyPath)
                .Single(type => type.FullName == "LookupContract.IRun");

            catalog.RequireClosedHierarchy(derived);
            TypeEntry baseType = catalog.Types.Single(type =>
                type.FullName == "LookupBase.Base");
            CollectionAssert.Contains(catalog.ImplementingTypesByInterfaceId[contract.Id]
                .Select(type => type.Id).ToArray(), baseType.Id);
            CollectionAssert.Contains(
                catalog.DerivedTypesByBaseId[baseType.Id]
                    .Select(type => type.Id).ToArray(),
                derived.Id);
            MethodEntry contractMethod = catalog.GetMethods(contract).Single(method =>
                method.Name == "Run");
            MethodEntry baseMethod = catalog.GetMethods(baseType).Single(method =>
                method.Name == "Run");
            MethodEntry derivedMethod = catalog.GetMethods(derived).Single(method =>
                method.Name == "Run");
        }

        // 检查参考目录的同目录桩不能被目录额外注入为运行载体。
        /// <summary>
        /// 验证目录只从材料核实的候选中选择转交目标。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsReferenceSiblingAsForwardingCarrier()
        {
            using TestProject project = TestProject.CreateWithReferenceSiblingForwarderStub();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            System.Reflection.AssemblyName targetIdentity =
                System.Reflection.AssemblyName.GetAssemblyName(project.UnityReferenceSiblingPath);

            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                catalog.LoadAssemblyTypes(targetIdentity, project.UnityReferencePath));

            StringAssert.Contains(exception.Message, "ReferenceOnlyTarget");
        }

        // 检查真正命中缺失转交载体时不会返回空候选继续分析。
        /// <summary>
        /// 验证缺失类型进入实际继承关系时准确停止并报告目标程序集。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsUsedMissingForwarderTarget()
        {
            using TestProject project = TestProject.CreateWithPartiallyMissingForwarders();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry consumer = catalog.Types.Single(type =>
                type.FullName == "PartialForwardingConsumer.MissingConsumer");

            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                catalog.RequireClosedHierarchy(consumer));

            StringAssert.Contains(exception.Message, "MissingForwardTarget");
        }

        // 检查转交链缺少最终真实类型时不会生成假别名。
        /// <summary>
        /// 验证真实继承关系需要闭合时会报告缺失的最终程序集。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsMissingEndOfForwardingChain()
        {
            using TestProject project = TestProject.CreateWithTwoForwardingFacades();
            File.Delete(project.ForwardTargetPath);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "ForwardedConsumer.Derived");

            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                result.RequireClosedHierarchy(derived));

            StringAssert.Contains(exception.Message, "FinalImplementation");
        }

        // 检查转交链循环时不会无限递归。
        /// <summary>
        /// 验证闭合真实继承关系时准确报告门面循环。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsForwardingCycle()
        {
            using TestProject project = TestProject.CreateWithForwardingCycle();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "ForwardedConsumer.Derived");

            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                result.RequireClosedHierarchy(derived));

            StringAssert.Contains(exception.Message, "循环");
        }

        // 检查同一完整门面身份不能转交到两个目标。
        /// <summary>
        /// 验证转交分叉会报告精确的门面类型身份。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsAmbiguousForwardingTargets()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteAmbiguousForwardedAssemblies();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MethodCatalog().BuildAsync(material, 2));

            StringAssert.Contains(exception.Message, "对应多个真实类型");
        }

        // 同一错误不能随两个门面在输入中的次序改变。
        /// <summary>反序放置同身份门面的分支，完整错误文本保持一致。</summary>
        [TestMethod]
        public async Task BuildAsyncOrdersForwardingFailuresByTargetIdentity()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteAmbiguousForwardedAssemblies();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            AnalysisException first = await Assert.ThrowsAsync<AnalysisException>(() => new MethodCatalog().BuildAsync(material, 1));
            string secondPath = Path.Combine(project.RootPath, "FacadeCopy.dll");
            byte[] firstFacade = File.ReadAllBytes(project.ExternalAssemblyPath);
            File.WriteAllBytes(project.ExternalAssemblyPath, File.ReadAllBytes(secondPath));
            File.WriteAllBytes(secondPath, firstFacade);

            AnalysisException reversed = await Assert.ThrowsAsync<AnalysisException>(() => new MethodCatalog().BuildAsync(material, 4));

            Assert.AreEqual(first.Message, reversed.Message);
        }

        // 检查外层门面与最终实现同名时仍按版本分辨转交节点。
        /// <summary>
        /// 验证同一逻辑类型名的高版本门面可准确落到低版本真实类型。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsSameNameVersionedForwardingAlias()
        {
            using TestProject project =
                TestProject.CreateWithSameNameVersionedForwardingFacades();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "ForwardedConsumer.Derived");

            result.RequireClosedHierarchy(derived);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "ForwardedNamespace.ForwardedType");
            MethodEntry target = result.GetMethods(implementation).Single(method =>
                method.Name == "Touch");
            MethodEntry derivedTouch = result.GetMethods(derived).Single(method =>
                method.Name == "Touch");

            Assert.AreEqual(
                implementation.Id,
                result.TypesByLogicalId[implementation.LogicalId].Single().Id);
        }

        // 检查材料已证明的程序集重映射贯穿类型与函数目录。
        /// <summary>
        /// 验证4.2参考身份通过唯一4.0运行文件闭合继承、迟加载和普通函数查找。
        /// </summary>
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task BuildAsyncUsesProvenAssemblyRedirectForTypesAndMethods(bool includeOldVersionReference)
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping(
                includeOldVersionReference: includeOldVersionReference);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            System.Reflection.AssemblyName referenceAssembly =
                System.Reflection.AssemblyName.GetAssemblyName(
                    project.UnityFrameworkReferencePath);
            TypeEntry derived = catalog.Types.Single(type =>
                type.FullName == "UnityFrameworkConsumer.Derived");

            IReadOnlyList<TypeEntry> loaded = catalog.LoadAssemblyTypes(
                referenceAssembly,
                project.ExternalAssemblyPath);
            catalog.RequireClosedHierarchy(derived);
            TypeEntry baseType = catalog.Types.Single(type =>
                type.FullName == "UnityFrameworkTypes.BaseType");
            MethodEntry baseMethod = catalog.GetMethods(baseType).Single(method =>
                method.Name == "Read");
            MethodEntry derivedMethod = catalog.GetMethods(derived).Single(method =>
                method.Name == "Read");
            BehaviorReadResult behaviors = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { baseMethod, derivedMethod },
                2);

            Assert.IsTrue(loaded.Any(type => type.Id == baseType.Id));
            Assert.AreEqual(project.UnityRuntimeFrameworkPath, baseType.AssemblyPath);
            Assert.AreEqual(project.UnityRuntimeFrameworkPath, baseMethod.AssemblyPath);
            Assert.IsGreaterThan(0, baseMethod.MetadataToken);
            CollectionAssert.AreEquivalent(
                new[] { baseMethod.Id, derivedMethod.Id },
                behaviors.Methods.Select(method => method.MethodId).ToArray());
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

            TypeEntry derived = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.DerivedType");
            StringAssert.Contains(derived.BaseType!.DefinitionId, "TransitiveSamples.BaseType");
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
            Assert.IsTrue(firstRead.IsVirtual);

            result.RequireClosedHierarchy(derivedType);
            TypeEntry baseType = result.Types.Single(type =>
                type.FullName == "TransitiveSamples.BaseType");
            MethodEntry baseMethod = result.GetMethods(baseType).Single(method =>
                method.Name == "Touch");
            MethodEntry secondRead = result.GetMethods(derivedType).Single(method =>
                method.Name == "Touch");
            Assert.AreEqual(firstRead.Id, secondRead.Id);

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

            Assert.HasCount(1, firstReads.Select(method => method.Id).Distinct().ToArray());
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

            Assert.IsTrue(secondReads.All(method => method.Id == firstReads[0].Id));
            Assert.IsFalse(result.TypesById[result.GetMethods(baseType).Single(method => method.LogicalId == baseMethodId).TypeId].IsInterface);
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

        // 检查源码和真实 DLL 都保留抽象类事实。
        /// <summary>
        /// 验证实际接收类型候选可以排除抽象类。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRecordsSourceAndManagedAbstractTypes()
        {
            using TestProject project = TestProject.CreateWithCallTargets(
                "namespace Samples; public abstract class AbstractSample { }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry source = catalog.Types.Single(type =>
                type.FullName == "SourceSamples.AbstractSample");
            TypeEntry managed = catalog.Types.Single(type =>
                type.FullName == "ExternalSamples.AbstractSample");

            Assert.IsTrue(source.IsAbstract);
            Assert.IsTrue(managed.IsAbstract);
        }

        // 检查源码析构函数不会被普通函数统计规则收录。
        /// <summary>
        /// 验证析构函数保留源码种类并排除于最终标签统计范围。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsSourceDestructorKindAndExcludesItFromReport()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Finalizable
                {
                    ~Finalizable() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry destructor = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.Finalizable" && method.Name == "Finalize");

            Assert.AreEqual(MethodKind.Destructor, destructor.Kind);
            Assert.IsFalse(destructor.IsReportable);
        }

        // 检查嵌套泛型源码声明与内存 PE 通过标准成员编号精确配对。
        /// <summary>
        /// 验证源码信息、泛型身份、接口关系和生成函数物理身份同时保留。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncPairsNestedGenericSourceWithEmittedMetadata()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class NoLogTrackAttribute : System.Attribute
                {
                    public NoLogTrackAttribute(string reason) { }
                }
                public interface IContract<T>
                {
                    T Echo<V>(ref T value, V other);
                }
                public sealed class Outer<T>
                {
                    public sealed class Inner<U> : IContract<U>
                    {
                        [NoLogTrack("why")]
                        public U Echo<V>(ref U value, V other) => value;
                        public U Run(U value)
                        {
                            static U Local(U item) => item;
                            System.Func<U, U> callback = item => item;
                            return callback(Local(value));
                        }
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry inner = catalog.Types.Single(type =>
                type.SourceSymbol != null
                && type.FullName == "SourceSamples.Outer<T>.Inner<U>");
            MethodEntry echo = catalog.Methods.Single(method =>
                method.TypeId == inner.Id && method.Name == "Echo");
            MethodEntry contract = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.IContract<T>" && method.Name == "Echo");
            MethodEntry generated = catalog.Methods.Single(method =>
                method.TypeId == inner.Id
                && method.Name.Contains("g__Local", StringComparison.Ordinal));

            Assert.IsNotNull(echo.SourceSymbol);
            Assert.AreEqual(project.RootSourcePath, echo.SourcePath);
            Assert.IsGreaterThan(0, echo.Line);
            Assert.AreEqual(echo.LogicalId, echo.Id);
            Assert.IsGreaterThan(0, echo.MetadataToken);
            Assert.IsTrue(echo.SourceSymbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass!.ToDisplayString() == "SourceSamples.NoLogTrackAttribute"
                && attribute.ConstructorArguments.Length > 0));
            Assert.AreEqual("!1&", echo.Parameters[0].TypeId);
            Assert.AreEqual("!!0", echo.Parameters[1].TypeId);
            Assert.AreEqual("!1", echo.ReturnTypeId);
            Assert.IsNull(generated.SourceSymbol);
            Assert.AreNotEqual(generated.LogicalId, generated.Id);
            Assert.IsGreaterThan(0, generated.MetadataToken);
        }

        // 检查源码编号和位置保留时已绑定内存 PE 元数据。
        /// <summary>
        /// 验证普通函数、属性 setter 和构造函数共用真实函数体入口。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncBindsSourceDeclarationsToEmittedMetadata()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            SourceAssemblyMaterial sourceAssembly = material.SourceAssemblies.Single(assembly =>
                assembly.IsReportAssembly);
            IReadOnlyDictionary<string, int> expectedLines = new Dictionary<string, int>
            {
                [".ctor"] = 103,
                ["set_Value"] = 95,
                ["WriteField"] = 119,
            };

            foreach ((string name, int line) in expectedLines)
            {
                MethodEntry method = catalog.Methods.Single(item =>
                    item.TypeName == "SourceSamples.BehaviorSample" && item.Name == name);
                Assert.AreEqual(method.LogicalId, method.Id);
                Assert.AreEqual(project.RootSourcePath, method.SourcePath);
                Assert.AreEqual(line, method.Line);
                Assert.AreEqual(sourceAssembly.AssemblyPath, method.AssemblyPath);
                Assert.IsGreaterThan(0, method.MetadataToken);
            }
        }

        // 验证普通同名结构体不因名字相同被当作系统类型。
        /// <summary>
        /// 没有专用元素码的值类型必须保留其实际声明来源。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncPreservesAssemblyIdentityForNamedValueTypes()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace System { public struct Decimal { } }
                public class Sample { public System.Decimal Echo(System.Decimal value) => value; }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry localType = catalog.Types.Single(type => type.SourceSymbol != null && type.FullName == "System.Decimal");
            MethodEntry method = catalog.Methods.Single(item => item.TypeName == "Sample" && item.Name == "Echo");

            Assert.AreEqual(localType.LogicalId, method.Parameters.Single().TypeId);
            Assert.AreEqual(localType.LogicalId, method.ReturnTypeId);
        }

        // 验证按程序集读取已经编译的源码时仍返回同一份带标签目录。
        /// <summary>
        /// 延迟读取入口不能重新暴露源码回贴前的内部元数据记录。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsSourceFactsWhenLoadingAnExistingAssembly()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                [System.Obsolete] public class Sample { [System.Obsolete] public void Run() { } }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            SourceAssemblyMaterial source = material.SourceAssemblies.Single();
            TypeEntry expected = catalog.Types.Single(type => type.FullName == "Sample");
            MethodEntry expectedMethod = catalog.Methods.Single(method => method.TypeName == "Sample" && method.Name == "Run");

            TypeEntry loaded = catalog.LoadAssemblyTypes(
                    new System.Reflection.AssemblyName(source.Compilation.Assembly.Identity.GetDisplayName()), source.AssemblyPath)
                .Single(type => type.FullName == "Sample");
            MethodEntry method = catalog.GetMethods(loaded).Single(candidate => candidate.Name == "Run");

            Assert.AreEqual(expected.Id, loaded.Id);
            Assert.AreSame(expected.SourceSymbol, loaded.SourceSymbol);
            Assert.AreEqual(expected.SourceSymbol, loaded.SourceSymbol);
            Assert.AreEqual(expectedMethod.Id, method.Id);
            Assert.AreEqual(expectedMethod.SourcePath, method.SourcePath);
            Assert.AreEqual(expectedMethod.SourceSymbol, method.SourceSymbol);
        }

        // 检查没有专用元数据元素类型的基础值类型也能绑定内存函数。
        /// <summary>
        /// 验证 decimal、IntPtr 与 UIntPtr 的源码签名和 Cecil 签名使用同一身份。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncBindsSpecialValueTypeSignaturesToMetadata()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class ValueTypes
                {
                    // 接收 decimal 参数。
                    public void Write(decimal number) { }
                    // 返回传入的 decimal。
                    public decimal Echo(decimal number) => number;
                    // 保留平台有符号指针类型。
                    public System.IntPtr EchoIntPtr(System.IntPtr value) => value;
                    // 保留平台无符号指针类型。
                    public System.UIntPtr EchoUIntPtr(System.UIntPtr value) => value;
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] methods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.ValueTypes")
                .ToArray();
            TypeEntry decimalType = catalog.Types.Single(type => type.FullName == "System.Decimal"
                && type.AssemblyPath == typeof(decimal).Assembly.Location);
            TypeEntry externalType = catalog.Types.Single(type => type.FullName == "ExternalSamples.ValueTypes");
            IReadOnlyList<MethodEntry> externalMethods = catalog.GetMethods(externalType);

            Assert.HasCount(5, methods);
            Assert.IsTrue(methods.All(method => method.MetadataToken > 0));
            foreach (MethodEntry method in methods.Where(method => method.Name != ".ctor"))
            {
                string expectedType = method.Name switch
                {
                    "EchoIntPtr" => "System.IntPtr",
                    "EchoUIntPtr" => "System.UIntPtr",
                    _ => decimalType.LogicalId,
                };
                Assert.AreEqual(expectedType, method.Parameters.Single().TypeId);
                Assert.AreEqual(expectedType, externalMethods.Single(candidate => candidate.Name == method.Name)
                    .Parameters.Single().TypeId);
                Assert.AreEqual(
                    method.Name == "Write" ? "System.Void" : expectedType,
                    method.ReturnTypeId);
            }

            Assert.AreEqual(decimalType.Id, catalog.TypesByLogicalId[methods.Single(method => method.Name == "Echo")
                .Parameters.Single().TypeId].Single().Id);
        }

        // 检查Unity门面的参考类型身份连接到转交后的真实定义。
        /// <summary>
        /// 验证类型层级闭合使用运行目标，不把参考门面当成实现。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncResolvesUnityFacadeAliasToForwardedType()
        {
            using TestProject project = TestProject.CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: true);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry derived = catalog.Types.Single(type =>
                type.FullName == "UnityFacadeConsumer.Derived");
            TypeEntry baseType = catalog.Types.Single(type =>
                type.FullName == "UnityFacadeTypes.BaseType");

            catalog.RequireClosedHierarchy(derived);
            Assert.AreEqual(project.UnityRuntimeTargetPath, baseType.AssemblyPath);
            Assert.AreEqual(baseType.Id,
                catalog.TypesByLogicalId[derived.BaseType!.DefinitionId].Single().Id);
            Assert.IsFalse(catalog.Types.Any(type => string.Equals(
                type.AssemblyPath,
                project.UnityReferencePath,
                StringComparison.OrdinalIgnoreCase)));
        }

        // 检查源码目录绝不回读输出路径上的旧文件。
        /// <summary>
        /// 验证磁盘输出损坏也不影响已生成的内存 PE。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncReadsSourceMetadataFromMemoryWhenOutputIsStale()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            SourceAssemblyMaterial sourceAssembly = material.SourceAssemblies.Single(assembly =>
                assembly.IsReportAssembly);
            File.WriteAllText(sourceAssembly.AssemblyPath, "stale output", encoding: new System.Text.UTF8Encoding(false));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(item =>
                item.TypeName == "SourceSamples.BehaviorSample" && item.Name == "WriteField");
            TypeEntry generatedType = catalog.Types.First(type =>
                type.SourceSymbol == null
                && type.AssemblyPath == sourceAssembly.AssemblyPath);
            IReadOnlyList<MethodEntry> generatedMethods = catalog.GetMethods(generatedType);

            Assert.AreEqual(sourceAssembly.AssemblyPath, method.AssemblyPath);
            Assert.IsGreaterThan(0, method.MetadataToken);
            Assert.IsNotEmpty(generatedMethods);
            Assert.IsTrue(generatedMethods.All(item => item.MetadataToken > 0));
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

        // 检查同一运行文件的本地标记与外部按名引用遵循Unity Mono的不同规则。
        /// <summary>
        /// 验证本地函数和继承仍使用TypeDef，外部TypeRef使用同名类型转交目标。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncDistinguishesLocalDefinitionsFromForwardedNameLookups()
        {
            using TestProject project = TestProject.CreateWithDefinitionAndSameFileForwarder();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry externalDerived = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "SameFileForwardingConsumer.ExternalDerived");
            catalog.RequireClosedHierarchy(externalDerived);
            TypeEntry localEntry = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalReferencePath
                && type.FullName == "SameFileForwarding.Entry");
            TypeEntry targetEntry = catalog.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.FullName == "SameFileForwarding.Entry");
            TypeEntry localDerived = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalReferencePath
                && type.FullName == "SameFileForwarding.LocalDerived");

            catalog.RequireClosedHierarchy(localDerived);
            MethodEntry localBaseTouch = catalog.GetMethods(localEntry).Single(method =>
                method.Name == "Touch");
            MethodEntry targetTouch = catalog.GetMethods(targetEntry).Single(method =>
                method.Name == "Touch");
            MethodEntry localOverride = catalog.GetMethods(localDerived).Single(method =>
                method.Name == "Touch");
            MethodEntry externalOverride = catalog.GetMethods(externalDerived).Single(method =>
                method.Name == "Touch");

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
