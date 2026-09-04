using SetterChecker.Core;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证函数总表的公开内容和查找索引。
    /// </summary>
    [TestClass]
    public sealed class MethodCatalogTests
    {
        // 检查源码函数种类、标签、参数和统计范围。
        /// <summary>
        /// 验证总表保留分析节点，但只报告日志工具实际处理的函数。
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
                    // 声明后续模块判断的原生边界。
                    [System.Runtime.InteropServices.DllImport("native")]
                    public static extern void Native();
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
                method.TypeName == "Sample"
                && method.Name == "Plain");
            string[] reportableNames = result.Methods
                .Where(method => method.IsReportable)
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
                },
                reportableNames);
            Assert.AreEqual("System.Int32", plain.Parameters.Single().TypeName);
            Assert.AreEqual(22, plain.Line);
            Assert.IsTrue(plain.MethodAttributes.Any(attribute =>
                attribute.TypeName == "NoLogTrackAttribute"
                && attribute.HasArguments));
            Assert.IsTrue(plain.TypeAttributes.Any(attribute =>
                attribute.TypeName == "NoLogTrackAttribute"));
            Assert.IsTrue(result.Methods.Any(method => method.Name == ".ctor"));
            Assert.IsTrue(result.Methods.Any(method => method.Name == "get_Value"));
            Assert.IsTrue(result.Methods.Any(method => method.Kind == CatalogMethodKind.LocalFunction));
            Assert.IsTrue(result.Methods.Any(method => method.Kind == CatalogMethodKind.AnonymousFunction));
        }

        // 检查类型关系和函数重写在正式分析前已经建立索引。
        /// <summary>
        /// 验证接口实现、继承和重写目标都可以直接查到。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncIndexesTypeAndOverrideRelations()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public interface IWorker
                {
                    // 声明接口函数。
                    void Run();
                }

                public class Base
                {
                    // 声明可重写函数。
                    public virtual void Work() { }
                }

                public sealed class Derived : Base, IWorker
                {
                    // 重写基类函数。
                    public override void Work() { }
                    // 实现接口函数。
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
            MethodEntry baseMethod = result.Methods.Single(method =>
                method.TypeId == baseType.Id && method.Name == "Work");
            MethodEntry derivedMethod = result.Methods.Single(method =>
                method.TypeId == derived.Id && method.Name == "Work");

            CollectionAssert.Contains(
                result.ImplementingTypesByInterfaceId[contract.Id].Select(type => type.Id).ToArray(),
                derived.Id);
            CollectionAssert.Contains(
                result.DerivedTypesByBaseId[baseType.Id].Select(type => type.Id).ToArray(),
                derived.Id);
            CollectionAssert.Contains(
                result.OverridesByMethodId[baseMethod.Id].Select(method => method.Id).ToArray(),
                derivedMethod.Id);
            CollectionAssert.Contains(
                result.MethodsByName["Run"].Select(method => method.TypeId).ToArray(),
                derived.Id);
        }

        // 检查真实托管动态链接库中的函数进入同一张总表。
        /// <summary>
        /// 验证外部函数具有程序集路径、指令标记和参数信息。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncCatalogsManagedAssemblyMethods()
        {
            using TestProject project = TestProject.CreateWithExternalMethods();
            project.WriteRootSource("""
                public interface ISourceExternal<T>
                {
                    // 声明由嵌套泛型类型隐式实现的函数。
                    void Apply(T value);
                }

                public sealed class SourceOuter<T>
                {
                    public sealed class Inner<TValue> : ISourceExternal<TValue>
                    {
                        // 隐式实现构造泛型接口。
                        public void Apply(TValue value) { }
                        // 返回使用两层开放泛型参数的嵌套类型。
                        public SourceOuter<T>.Inner<TValue> Echo(
                            SourceOuter<T>.Inner<TValue> value) => value;
                        // 返回只保留外层开放参数的嵌套类型。
                        public SourceOuter<T>.Inner<int> CaptureOuter(
                            SourceOuter<T>.Inner<int> value) => value;

                        public sealed class Deep<TItem>
                        {
                            // 返回使用三层开放泛型参数的嵌套类型。
                            public SourceOuter<T>.Inner<TValue>.Deep<TItem> Echo(
                                SourceOuter<T>.Inner<TValue>.Deep<TItem> value) => value;
                        }
                    }
                }

                public sealed class SourceGenericSelf<T>
                {
                    // 返回使用当前开放泛型参数的类型。
                    public SourceGenericSelf<T> Echo(SourceGenericSelf<T> value) => value;
                }

                public interface ISourceAccessors
                {
                    int Value { get; set; }
                    event System.Action Changed;
                }

                public sealed class SourceExplicitAccessors : ISourceAccessors
                {
                    int ISourceAccessors.Value { get => 0; set { } }
                    event System.Action ISourceAccessors.Changed { add { } remove { } }
                }

                public interface ISourceInContract
                {
                    // 声明只读引用参数函数。
                    void Accept(in int value);
                }

                public sealed class SourceInImplementation : ISourceInContract
                {
                    // 隐式实现只读引用参数函数。
                    public void Accept(in int value) { }
                }

                public readonly struct SourceArithmetic
                {
                    // 声明普通显式转换。
                    public static explicit operator int(SourceArithmetic value) => 0;
                    // 声明 checked 转换要求的匹配普通版本。
                    public static explicit operator long(SourceArithmetic value) => 0;
                    // 声明 checked 显式转换。
                    public static explicit operator checked long(SourceArithmetic value) => 0;
                }

                public sealed class SourceReturnShapes
                {
                    private int m_value;

                    // 返回可写引用。
                    public ref int GetRef() => ref m_value;
                    // 返回只读引用。
                    public ref readonly int GetReadOnlyRef() => ref m_value;
                    // 读取只读引用参数。
                    public void ReadIn(in int value) { }
                    // 声明带返回修饰符的初始化属性。
                    public int Value { get; init; }
                }

                public sealed class SourceType
                {
                    // 声明与外部函数参数相同的源码函数。
                    public void Change(int value) { }
                    // 声明名称类似访问器但实际是普通函数的反例。
                    public void get_Fake() { }
                    // 返回外层和内层都已构造的嵌套泛型类型。
                    public SourceOuter<int>.Inner<string> Echo(
                        SourceOuter<int>.Inner<string> value) => value;
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            string runtimePath = typeof(object).Assembly.Location;
            material = material with
            {
                ExternalAssemblies = material.ExternalAssemblies
                    .Append(new ExternalAssemblyMaterial(runtimePath, new[] { runtimePath }))
                    .ToArray(),
            };

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry externalType = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "ExternalType");
            TypeEntry externalContract = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "IExternal");
            TypeEntry externalBase = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "ExternalBase");
            TypeEntry explicitExternal = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "ExplicitExternal");
            TypeEntry nestedExternal = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "Inner");
            TypeEntry newSlotExternal = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "NewSlotExternal");
            TypeEntry reimplementedExternal = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "ReimplementedExternal");
            MethodEntry external = result.GetMethods(externalType).Single(method =>
                method.Name == "Change");
            MethodEntry fakeAccessor = result.GetMethods(externalType).Single(method =>
                method.Name == "get_Fake");
            MethodEntry baseMethod = result.GetMethods(externalBase).Single(method =>
                method.Name == "Change");
            MethodEntry interfaceMethod = result.GetMethods(externalContract).Single(method =>
                method.Name == "Apply");
            MethodEntry baseInterfaceMethod = result.GetMethods(externalBase).Single(method =>
                method.Name == "Apply");
            MethodEntry explicitMethod = result.GetMethods(explicitExternal).Single(method =>
                method.Name.EndsWith(".Apply", StringComparison.Ordinal));
            MethodEntry newSlotMethod = result.GetMethods(newSlotExternal).Single(method =>
                method.Name == "Apply");
            MethodEntry reimplementedMethod = result.GetMethods(reimplementedExternal).Single(method =>
                method.Name == "Apply");
            MethodEntry source = result.Methods.Single(method =>
                method.TypeName == "SourceType" && method.Name == "Change");
            MethodEntry sourceNested = result.Methods.Single(method =>
                method.TypeName == "SourceType" && method.Name == "Echo");
            MethodEntry sourceGenericSelf = result.Methods.Single(method =>
                method.TypeName == "SourceGenericSelf<T>" && method.Name == "Echo");
            MethodEntry sourceOpenNested = result.Methods.Single(method =>
                method.TypeName == "SourceOuter<T>.Inner<TValue>" && method.Name == "Echo");
            MethodEntry sourceOuterOnly = result.Methods.Single(method =>
                method.TypeName == "SourceOuter<T>.Inner<TValue>"
                && method.Name == "CaptureOuter");
            MethodEntry sourceImplicit = result.Methods.Single(method =>
                method.TypeName == "SourceOuter<T>.Inner<TValue>"
                && method.Name == "Apply");
            MethodEntry sourceContract = result.Methods.Single(method =>
                method.TypeName == "ISourceExternal<T>"
                && method.Name == "Apply");
            MethodEntry sourceThreeLevels = result.Methods.Single(method =>
                method.TypeName == "SourceOuter<T>.Inner<TValue>.Deep<TItem>"
                && method.Name == "Echo");
            TypeEntry externalNestedUser = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "NestedUser");
            MethodEntry externalNested = result.GetMethods(externalNestedUser).Single(method =>
                method.Name == "Echo");
            TypeEntry externalGenericSelf = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "GenericSelf");
            MethodEntry externalOpenSelf = result.GetMethods(externalGenericSelf).Single(method =>
                method.Name == "Echo");
            MethodEntry externalOpenNested = result.GetMethods(nestedExternal).Single(method =>
                method.Name == "Echo");
            MethodEntry externalOuterOnly = result.GetMethods(nestedExternal).Single(method =>
                method.Name == "CaptureOuter");
            TypeEntry externalDeep = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "Deep");
            MethodEntry externalThreeLevels = result.GetMethods(externalDeep).Single(method =>
                method.Name == "Echo");
            TypeEntry externalAccessors = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "ExplicitAccessors");
            CatalogMethodKind[] sourceAccessorKinds = result.Methods
                .Where(method => method.TypeName == "SourceExplicitAccessors"
                    && method.Name.Contains("ISourceAccessors.", StringComparison.Ordinal))
                .Select(method => method.Kind)
                .Order()
                .ToArray();
            CatalogMethodKind[] externalAccessorKinds = result.GetMethods(externalAccessors)
                .Where(method => method.Name.Contains("IAccessors.", StringComparison.Ordinal))
                .Select(method => method.Kind)
                .Order()
                .ToArray();
            MethodEntry sourceCheckedConversion = result.Methods.Single(method =>
                method.TypeName == "SourceArithmetic"
                && method.Name == "op_CheckedExplicit");
            TypeEntry externalArithmetic = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "Arithmetic");
            MethodEntry externalCheckedConversion = result.GetMethods(externalArithmetic).Single(method =>
                method.Name == "op_CheckedExplicit");
            TypeEntry externalInContract = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "IInContract");
            TypeEntry externalInImplementation = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "InImplementation");
            MethodEntry externalInContractMethod = result.GetMethods(externalInContract).Single(method =>
                method.Name == "Accept");
            MethodEntry externalInImplementationMethod = result.GetMethods(externalInImplementation)
                .Single(method => method.Name == "Accept");
            MethodEntry sourceInContractMethod = result.Methods.Single(method =>
                method.TypeName == "ISourceInContract" && method.Name == "Accept");
            MethodEntry sourceInImplementationMethod = result.Methods.Single(method =>
                method.TypeName == "SourceInImplementation" && method.Name == "Accept");
            TypeEntry externalReturnShapes = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "ReturnShapes");
            MethodEntry sourceRefReturn = result.Methods.Single(method =>
                method.TypeName == "SourceReturnShapes" && method.Name == "GetRef");
            MethodEntry sourceReadOnlyRefReturn = result.Methods.Single(method =>
                method.TypeName == "SourceReturnShapes" && method.Name == "GetReadOnlyRef");
            MethodEntry sourceInitSetter = result.Methods.Single(method =>
                method.TypeName == "SourceReturnShapes" && method.Name == "set_Value");
            MethodEntry sourceInParameter = result.Methods.Single(method =>
                method.TypeName == "SourceReturnShapes" && method.Name == "ReadIn");
            MethodEntry externalRefReturn = result.GetMethods(externalReturnShapes).Single(method =>
                method.Name == "GetRef");
            MethodEntry externalReadOnlyRefReturn = result.GetMethods(externalReturnShapes).Single(method =>
                method.Name == "GetReadOnlyRef");
            MethodEntry externalInitSetter = result.GetMethods(externalReturnShapes).Single(method =>
                method.Name == "set_Value");
            MethodEntry externalInParameter = result.GetMethods(externalReturnShapes).Single(method =>
                method.Name == "ReadIn");

            Assert.AreEqual("System.Int32", external.Parameters.Single().TypeName);
            Assert.AreEqual(source.Parameters.Single().TypeId, external.Parameters.Single().TypeId);
            Assert.IsTrue(external.MetadataToken > 0);
            Assert.IsFalse(external.IsReportable);
            Assert.AreEqual(CatalogMethodKind.Ordinary, fakeAccessor.Kind);
            StringAssert.EndsWith(
                sourceNested.Parameters.Single().TypeId,
                "<System.Int32,System.String>");
            StringAssert.EndsWith(
                sourceNested.ReturnTypeId,
                "<System.Int32,System.String>");
            StringAssert.EndsWith(
                externalNested.Parameters.Single().TypeId,
                "<System.Int32,System.String>");
            StringAssert.EndsWith(sourceGenericSelf.Parameters.Single().TypeId, "<!0>");
            StringAssert.EndsWith(externalOpenSelf.Parameters.Single().TypeId, "<!0>");
            StringAssert.EndsWith(sourceOpenNested.Parameters.Single().TypeId, "<!0,!1>");
            StringAssert.EndsWith(externalOpenNested.Parameters.Single().TypeId, "<!0,!1>");
            StringAssert.EndsWith(sourceOuterOnly.Parameters.Single().TypeId, "<!0,System.Int32>");
            StringAssert.EndsWith(externalOuterOnly.Parameters.Single().TypeId, "<!0,System.Int32>");
            StringAssert.EndsWith(sourceThreeLevels.Parameters.Single().TypeId, "<!0,!1,!2>");
            StringAssert.EndsWith(externalThreeLevels.Parameters.Single().TypeId, "<!0,!1,!2>");
            CollectionAssert.AreEqual(sourceAccessorKinds, externalAccessorKinds);
            Assert.AreEqual(sourceCheckedConversion.Kind, externalCheckedConversion.Kind);
            Assert.AreEqual(CatalogMethodKind.Conversion, externalCheckedConversion.Kind);
            CollectionAssert.Contains(
                sourceInImplementationMethod.RelatedMethodIds.ToArray(),
                sourceInContractMethod.LogicalId);
            CollectionAssert.Contains(
                externalInImplementationMethod.RelatedMethodIds.ToArray(),
                externalInContractMethod.LogicalId);
            Assert.AreEqual(externalRefReturn.ReturnTypeId, sourceRefReturn.ReturnTypeId);
            Assert.AreEqual(
                externalReadOnlyRefReturn.ReturnTypeId,
                sourceReadOnlyRefReturn.ReturnTypeId);
            Assert.AreEqual(externalInitSetter.ReturnTypeId, sourceInitSetter.ReturnTypeId);
            Assert.AreEqual(
                externalInParameter.Parameters.Single().TypeId,
                sourceInParameter.Parameters.Single().TypeId);
            Assert.AreEqual(CatalogRefKind.In, externalInParameter.Parameters.Single().RefKind);
            Assert.AreEqual(CatalogRefKind.In, sourceInParameter.Parameters.Single().RefKind);
            Assert.AreEqual(
                externalRefReturn.LogicalId[externalRefReturn.LogicalId.IndexOf("::", StringComparison.Ordinal)..],
                sourceRefReturn.LogicalId[sourceRefReturn.LogicalId.IndexOf("::", StringComparison.Ordinal)..]);
            Assert.AreEqual(
                externalReadOnlyRefReturn.LogicalId[externalReadOnlyRefReturn.LogicalId.IndexOf("::", StringComparison.Ordinal)..],
                sourceReadOnlyRefReturn.Id[sourceReadOnlyRefReturn.Id.IndexOf("::", StringComparison.Ordinal)..]);
            Assert.AreEqual(
                externalInitSetter.LogicalId[externalInitSetter.LogicalId.IndexOf("::", StringComparison.Ordinal)..],
                sourceInitSetter.LogicalId[sourceInitSetter.LogicalId.IndexOf("::", StringComparison.Ordinal)..]);
            CollectionAssert.Contains(
                sourceImplicit.RelatedMethodIds.ToArray(),
                sourceContract.LogicalId);
            CollectionAssert.Contains(external.RelatedMethodIds.ToArray(), baseMethod.LogicalId);
            CollectionAssert.Contains(
                baseInterfaceMethod.RelatedMethodIds.ToArray(),
                interfaceMethod.LogicalId);
            CollectionAssert.Contains(explicitMethod.RelatedMethodIds.ToArray(), interfaceMethod.LogicalId);
            CollectionAssert.DoesNotContain(
                newSlotMethod.RelatedMethodIds.ToArray(),
                interfaceMethod.LogicalId);
            CollectionAssert.Contains(
                reimplementedMethod.RelatedMethodIds.ToArray(),
                interfaceMethod.LogicalId);
            CollectionAssert.Contains(
                result.ImplementingTypesByInterfaceId[externalContract.LogicalId]
                    .Select(type => type.Id)
                    .ToArray(),
                externalType.Id);
            CollectionAssert.Contains(
                result.ImplementingTypesByInterfaceId[externalContract.LogicalId]
                    .Select(type => type.Id)
                    .ToArray(),
                nestedExternal.Id);
            foreach (TypeEntry type in result.Types.Where(type =>
                         type.AssemblyPath == project.ExternalAssemblyPath))
            {
                result.GetMethods(type);
            }

            TypeEntry generatedIterator = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName.Contains("EnumerateMatches>d__", StringComparison.Ordinal));
            MethodEntry[] generatedMethods = result.GetMethods(generatedIterator).ToArray();
            Assert.IsTrue(generatedMethods.Any(method =>
                method.Name.EndsWith(".GetEnumerator", StringComparison.Ordinal)
                && method.RelatedMethodIds.Any(id => id.Contains(
                    "IEnumerable`1::13:GetEnumerator",
                    StringComparison.Ordinal))),
                string.Join(
                    Environment.NewLine,
                generatedMethods.Select(method =>
                        $"{method.Id} => {string.Join(";", method.RelatedMethodIds)}")));
        }

        // 检查 MethodImpl 的实现函数由当前类型成员引用表示时仍连接接口声明。
        /// <summary>
        /// 验证合法 MemberReference MethodBody 与普通 MethodDef 实现得到同一函数关系。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsConnectsMemberReferenceMethodBodyToDeclaration()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            MaterialSet loaded = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            string assemblyPath = project.WriteMemberReferenceMethodBodyAssembly();
            MaterialSet material = loaded with
            {
                ExternalAssemblies = loaded.ExternalAssemblies.Append(
                    new ExternalAssemblyMaterial(assemblyPath, new[] { assemblyPath })).ToArray(),
            };
            System.Runtime.Loader.AssemblyLoadContext loadContext = new(
                "MemberReferenceMethodBody",
                isCollectible: true);
            System.Reflection.Assembly runtimeAssembly;
            using (FileStream stream = File.OpenRead(assemblyPath))
            {
                runtimeAssembly = loadContext.LoadFromStream(stream);
            }

            Type runtimeImplementation = runtimeAssembly.GetType("Implementation")!;
            Type runtimeContract = runtimeAssembly.GetType("IContract")!;
            Assert.AreEqual(
                "Body",
                runtimeImplementation.GetInterfaceMap(runtimeContract).TargetMethods.Single().Name);
            loadContext.Unload();

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == assemblyPath && type.Name == "Implementation");
            TypeEntry contract = result.Types.Single(type =>
                type.AssemblyPath == assemblyPath && type.Name == "IContract");
            MethodEntry body = result.GetMethods(implementation).Single(method =>
                method.Name == "Body");
            MethodEntry declaration = result.GetMethods(contract).Single(method =>
                method.Name == "Describe");

            CollectionAssert.Contains(body.RelatedMethodIds.ToArray(), declaration.LogicalId);
        }

        // 检查泛型当前类型的 TypeSpec 成员引用仍精确连接接口声明。
        /// <summary>
        /// 验证 Implementation&lt;T&gt;.Body(T) 的成员引用 MethodBody 可以闭合目录关系。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsConnectsGenericSelfTypeSpecificationMethodBody()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            MaterialSet loaded = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            (string validPath, _) = project.WriteGenericMemberReferenceMethodBodyAssemblies();
            MaterialSet material = loaded with
            {
                ExternalAssemblies = loaded.ExternalAssemblies.Append(
                    new ExternalAssemblyMaterial(validPath, new[] { validPath })).ToArray(),
            };
            System.Runtime.Loader.AssemblyLoadContext loadContext = new(
                "GenericMemberReferenceMethodBody",
                isCollectible: true);
            System.Reflection.Assembly runtimeAssembly;
            using (FileStream stream = File.OpenRead(validPath))
            {
                runtimeAssembly = loadContext.LoadFromStream(stream);
            }

            Type runtimeImplementation = runtimeAssembly.GetType("Implementation`1")!
                .MakeGenericType(typeof(string));
            Type runtimeContract = runtimeAssembly.GetType("IContract`1")!
                .MakeGenericType(typeof(string));
            Assert.AreEqual(
                "Body",
                runtimeImplementation.GetInterfaceMap(runtimeContract).TargetMethods.Single().Name);
            loadContext.Unload();

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == validPath && type.Name == "Implementation");
            TypeEntry contract = result.Types.Single(type =>
                type.AssemblyPath == validPath && type.Name == "IContract");
            MethodEntry body = result.GetMethods(implementation).Single(method =>
                method.Name == "Body");
            MethodEntry declaration = result.GetMethods(contract).Single(method =>
                method.Name == "Run");

            CollectionAssert.Contains(body.RelatedMethodIds.ToArray(), declaration.LogicalId);
        }

        // 检查泛型 MethodBody 的成员引用父级不是当前类型时明确失败。
        /// <summary>
        /// 验证外部泛型类型的成员引用不能冒充当前类型的 MethodImpl 实现函数。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsRejectsGenericMethodBodyFromForeignParent()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            MaterialSet loaded = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            (_, string foreignParentPath) = project.WriteGenericMemberReferenceMethodBodyAssemblies();
            MaterialSet material = loaded with
            {
                ExternalAssemblies = loaded.ExternalAssemblies.Append(
                    new ExternalAssemblyMaterial(
                        foreignParentPath,
                        new[] { foreignParentPath })).ToArray(),
            };

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == foreignParentPath && type.Name == "Implementation");
            AnalysisException exception = Assert.Throws<AnalysisException>(() =>
                result.GetMethods(implementation));

            StringAssert.Contains(exception.Message, "托管函数实现体引用了当前类型以外的类型");
            StringAssert.Contains(exception.Message, "ForeignBody");
        }

        // 检查 MethodImpl 明确指向实例函数时不与同签名静态函数混淆。
        /// <summary>
        /// 验证同名同参数的实例和静态函数仍按 MethodDef 精确连接接口关系。
        /// </summary>
        [TestMethod]
        public async Task GetMethodsConnectsInstanceMethodDefinitionWhenStaticTwinExists()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            MaterialSet loaded = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            string assemblyPath = project.WriteStaticAndInstanceMethodBodyAssembly();
            MaterialSet material = loaded with
            {
                ExternalAssemblies = loaded.ExternalAssemblies.Append(
                    new ExternalAssemblyMaterial(assemblyPath, new[] { assemblyPath })).ToArray(),
            };
            System.Runtime.Loader.AssemblyLoadContext loadContext = new(
                "StaticAndInstanceMethodBody",
                isCollectible: true);
            System.Reflection.Assembly runtimeAssembly;
            using (FileStream stream = File.OpenRead(assemblyPath))
            {
                runtimeAssembly = loadContext.LoadFromStream(stream);
            }

            Type runtimeImplementation = runtimeAssembly.GetType("Implementation")!;
            Type runtimeContract = runtimeAssembly.GetType("IContract")!;
            System.Reflection.MethodInfo runtimeTarget = runtimeImplementation
                .GetInterfaceMap(runtimeContract)
                .TargetMethods
                .Single();
            Assert.AreEqual("Body", runtimeTarget.Name);
            Assert.IsFalse(runtimeTarget.IsStatic);
            loadContext.Unload();

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == assemblyPath && type.Name == "Implementation");
            TypeEntry contract = result.Types.Single(type =>
                type.AssemblyPath == assemblyPath && type.Name == "IContract");
            MethodEntry[] bodies = result.GetMethods(implementation)
                .Where(method => method.Name == "Body")
                .ToArray();
            MethodEntry instanceBody = bodies.Single(method => !method.IsStatic);
            MethodEntry staticBody = bodies.Single(method => method.IsStatic);
            MethodEntry declaration = result.GetMethods(contract).Single(method =>
                method.Name == "Describe");

            CollectionAssert.Contains(instanceBody.RelatedMethodIds.ToArray(), declaration.LogicalId);
            CollectionAssert.DoesNotContain(staticBody.RelatedMethodIds.ToArray(), declaration.LogicalId);
        }

        // 检查多个返回和参数修饰符与真实托管元数据使用同一顺序。
        /// <summary>
        /// 验证双修饰符的类型身份、函数身份和重写关系完全一致。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncMatchesManagedCustomModifierOrder()
        {
            using TestProject project = TestProject.CreateWithCustomModifiers();
            project.WriteRootSource("""
                public sealed class CustomDerivedType : CustomBaseType
                {
                    // 重写带双返回修饰符的函数。
                    public override int Read() => 0;
                    // 重写带双参数修饰符的函数。
                    public override void Write(int value) { }
                    private int m_value;
                    // 重写两组修饰符分处引用符号两侧的函数。
                    public override ref int MixedReturn() => ref m_value;
                    // 重写两组修饰符分处引用参数两侧的函数。
                    public override void MixedParameter(ref int value) { }
                }

                public sealed class NestedModifierDerived : NestedModifierBase
                {
                    // 重写泛型实参带修饰信息的函数。
                    public override Box<int> Echo(Box<int> value) => value;
                }

                public unsafe sealed class SourcePointerSignatures
                {
                    // 返回使用非托管 Cdecl 调用约定的函数指针。
                    public delegate* unmanaged[Cdecl]<void> Echo(
                        delegate* unmanaged[Cdecl]<void> value) => value;
                    // 返回使用扩展非托管调用约定的函数指针。
                    public delegate* unmanaged[SuppressGCTransition]<void> Suppress(
                        delegate* unmanaged[SuppressGCTransition]<void> value) => value;
                }

                public unsafe sealed class PurePointerImplementation : IPurePointerContract
                {
                    // 实现纯接口托管文件中的扩展调用约定函数。
                    public void Accept(
                        delegate* unmanaged[SuppressGCTransition]<void> value) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceRead = result.Methods.Single(method =>
                method.TypeName == "CustomDerivedType" && method.Name == "Read");
            MethodEntry sourceWrite = result.Methods.Single(method =>
                method.TypeName == "CustomDerivedType" && method.Name == "Write");
            MethodEntry sourceMixedReturn = result.Methods.Single(method =>
                method.TypeName == "CustomDerivedType" && method.Name == "MixedReturn");
            MethodEntry sourceMixedParameter = result.Methods.Single(method =>
                method.TypeName == "CustomDerivedType" && method.Name == "MixedParameter");
            TypeEntry baseType = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "CustomBaseType");
            MethodEntry managedRead = result.GetMethods(baseType).Single(method =>
                method.Name == "Read");
            MethodEntry managedWrite = result.GetMethods(baseType).Single(method =>
                method.Name == "Write");
            MethodEntry managedMixedReturn = result.GetMethods(baseType).Single(method =>
                method.Name == "MixedReturn");
            MethodEntry managedMixedParameter = result.GetMethods(baseType).Single(method =>
                method.Name == "MixedParameter");
            TypeEntry closingContract = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "IClosingContract");
            TypeEntry closingImplementation = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "ClosingImplementation");
            MethodEntry closingContractMethod = result.GetMethods(closingContract).Single(method =>
                method.Name == "Accept");
            MethodEntry closingImplementationMethod = result.GetMethods(closingImplementation)
                .Single(method => method.Name == "Accept");
            TypeEntry functionContract = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "IFunctionModifierContract");
            TypeEntry functionImplementation = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "FunctionModifierImplementation");
            MethodEntry functionContractMethod = result.GetMethods(functionContract).Single(method =>
                method.Name == "Accept");
            MethodEntry functionImplementationMethod = result.GetMethods(functionImplementation)
                .Single(method => method.Name == "Accept");
            TypeEntry falseContract = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "IFalseContract");
            TypeEntry falseImplementation = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "FalseImplementation");
            MethodEntry falseContractMethod = result.GetMethods(falseContract).Single(method =>
                method.Name == "Accept");
            MethodEntry falseImplementationMethod = result.GetMethods(falseImplementation).Single(method =>
                method.Name == "Accept");
            MethodEntry sourceNestedEcho = result.Methods.Single(method =>
                method.TypeName == "NestedModifierDerived" && method.Name == "Echo");
            TypeEntry nestedBase = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "NestedModifierBase");
            MethodEntry managedNestedEcho = result.GetMethods(nestedBase).Single(method =>
                method.Name == "Echo");
            TypeEntry genericFunctionContract = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "IGenericFunctionContract");
            TypeEntry genericFunctionImplementation = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "GenericFunctionImplementation");
            MethodEntry genericFunctionContractMethod = result.GetMethods(genericFunctionContract)
                .Single(method => method.Name == "Accept");
            MethodEntry genericFunctionImplementationMethod = result.GetMethods(
                genericFunctionImplementation).Single(method => method.Name == "Accept");
            TypeEntry callConventionContract = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "ICallConventionContract");
            TypeEntry wrongCallConventionImplementation = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "WrongCallConventionImplementation");
            TypeEntry callConventionOverloads = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "CallConventionOverloads");
            TypeEntry functionPointerArityOverloads = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "FunctionPointerArityOverloads");
            MethodEntry callConventionContractMethod = result.GetMethods(callConventionContract)
                .Single(method => method.Name == "Accept");
            MethodEntry wrongCallConventionMethod = result.GetMethods(
                wrongCallConventionImplementation).Single(method => method.Name == "Accept");
            MethodEntry[] callConventionOverloadMethods = result.GetMethods(callConventionOverloads)
                .Where(method => method.Name == "Pick")
                .ToArray();
            MethodEntry[] functionPointerArityOverloadMethods = result.GetMethods(
                functionPointerArityOverloads).Where(method => method.Name == "Pick").ToArray();
            MethodEntry sourcePointerMethod = result.Methods.Single(method =>
                method.TypeName == "SourcePointerSignatures" && method.Name == "Echo");
            TypeEntry managedPointerType = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "PointerSignatures.dll")
                && type.Name == "ManagedPointerSignatures");
            MethodEntry managedPointerMethod = result.GetMethods(managedPointerType).Single(method =>
                method.Name == "Echo");
            MethodEntry sourceSuppressMethod = result.Methods.Single(method =>
                method.TypeName == "SourcePointerSignatures" && method.Name == "Suppress");
            MethodEntry managedSuppressMethod = result.GetMethods(managedPointerType).Single(method =>
                method.Name == "Suppress");
            MethodEntry purePointerImplementation = result.Methods.Single(method =>
                method.TypeName == "PurePointerImplementation" && method.Name == "Accept");
            TypeEntry purePointerContract = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "PurePointerContract.dll")
                && type.Name == "IPurePointerContract");
            MethodEntry purePointerContractMethod = result.GetMethods(purePointerContract).Single(method =>
                method.Name == "Accept");
            TypeEntry instancePointerOverloads = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "InstancePointerOverloads");
            MethodEntry[] instancePointerOverloadMethods = result.GetMethods(instancePointerOverloads)
                .Where(method => method.Name == "Pick")
                .ToArray();
            TypeEntry varArgOverloads = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "VarArgOverloads");
            MethodEntry[] varArgOverloadMethods = result.GetMethods(varArgOverloads)
                .Where(method => method.Name == "Pick")
                .ToArray();
            TypeEntry collisionOverloads = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "CollisionOverloads");
            MethodEntry[] collisionOverloadMethods = result.GetMethods(collisionOverloads)
                .Where(method => method.Name == "Pick")
                .ToArray();

            Assert.AreEqual(managedRead.ReturnTypeId, sourceRead.ReturnTypeId);
            Assert.AreEqual(
                managedWrite.Parameters.Single().TypeId,
                sourceWrite.Parameters.Single().TypeId);
            CollectionAssert.AreEqual(
                new[] { managedRead.LogicalId },
                sourceRead.RelatedMethodIds.ToArray());
            CollectionAssert.AreEqual(
                new[] { managedWrite.LogicalId },
                sourceWrite.RelatedMethodIds.ToArray());
            Assert.AreEqual(managedMixedReturn.ReturnTypeId, sourceMixedReturn.ReturnTypeId);
            Assert.AreEqual(
                managedMixedParameter.Parameters.Single().TypeId,
                sourceMixedParameter.Parameters.Single().TypeId);
            CollectionAssert.AreEqual(
                new[] { managedMixedReturn.LogicalId },
                sourceMixedReturn.RelatedMethodIds.ToArray());
            CollectionAssert.AreEqual(
                new[] { managedMixedParameter.LogicalId },
                sourceMixedParameter.RelatedMethodIds.ToArray());
            CollectionAssert.Contains(
                closingImplementationMethod.RelatedMethodIds.ToArray(),
                closingContractMethod.LogicalId);
            CollectionAssert.Contains(
                functionImplementationMethod.RelatedMethodIds.ToArray(),
                functionContractMethod.LogicalId);
            CollectionAssert.DoesNotContain(
                falseImplementationMethod.RelatedMethodIds.ToArray(),
                falseContractMethod.LogicalId);
            Assert.AreEqual(managedNestedEcho.ReturnTypeId, sourceNestedEcho.ReturnTypeId);
            Assert.AreEqual(
                managedNestedEcho.Parameters.Single().TypeId,
                sourceNestedEcho.Parameters.Single().TypeId);
            CollectionAssert.Contains(
                sourceNestedEcho.RelatedMethodIds.ToArray(),
                managedNestedEcho.LogicalId);
            CollectionAssert.Contains(
                genericFunctionImplementationMethod.RelatedMethodIds.ToArray(),
                genericFunctionContractMethod.LogicalId);
            CollectionAssert.DoesNotContain(
                wrongCallConventionMethod.RelatedMethodIds.ToArray(),
                callConventionContractMethod.LogicalId);
            Assert.AreEqual(2, callConventionOverloadMethods.Length);
            Assert.AreEqual(
                2,
                callConventionOverloadMethods.Select(method => method.Id).Distinct().Count());
            Assert.AreEqual(2, functionPointerArityOverloadMethods.Length);
            Assert.AreEqual(
                2,
                functionPointerArityOverloadMethods.Select(method => method.Id).Distinct().Count());
            Assert.IsTrue(functionPointerArityOverloadMethods.Any(method =>
                method.LogicalId.Contains("``1", StringComparison.Ordinal)));
            Assert.AreEqual(
                managedPointerMethod.ReturnTypeId,
                sourcePointerMethod.ReturnTypeId);
            Assert.AreEqual(
                managedPointerMethod.Parameters.Single().TypeId,
                sourcePointerMethod.Parameters.Single().TypeId);
            Assert.AreEqual(
                managedSuppressMethod.ReturnTypeId,
                sourceSuppressMethod.ReturnTypeId);
            Assert.AreEqual(
                managedSuppressMethod.Parameters.Single().TypeId,
                sourceSuppressMethod.Parameters.Single().TypeId);
            CollectionAssert.Contains(
                purePointerImplementation.RelatedMethodIds.ToArray(),
                purePointerContractMethod.LogicalId);
            Assert.IsTrue(HasManagedRelation(
                "INonUnmanagedModifierContract",
                "NonUnmanagedModifierImplementation"));
            Assert.IsFalse(HasManagedRelation(
                "IRequiredCallConventionContract",
                "RequiredCallConventionImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IDuplicateCallConventionContract",
                "DuplicateCallConventionImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IFakeCallConventionContract",
                "FakeCallConventionImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "ICallConventionBaseContract",
                "CallConventionBaseImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IForwardedCallConventionContract",
                "ForwardedCallConventionImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IBangTypeNameContract",
                "BangTypeNameImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IBangNamespaceContract",
                "BangNamespaceImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IBangAssemblyContract",
                "BangAssemblyImplementation"));
            Assert.IsTrue(HasManagedRelation(
                "IForwardedDelimiterContract",
                "ForwardedDelimiterImplementation"));
            Assert.IsFalse(HasManagedRelation(
                "IVarArgSentinelContract",
                "VarArgRequiredImplementation"));
            Assert.AreEqual(2, varArgOverloadMethods.Length);
            Assert.AreEqual(
                2,
                varArgOverloadMethods.Select(method => method.LogicalId).Distinct().Count());
            Assert.AreEqual(2, collisionOverloadMethods.Length);
            Assert.AreEqual(
                2,
                collisionOverloadMethods.Select(method => method.Id).Distinct().Count());
            Assert.AreEqual(
                2,
                collisionOverloadMethods.Select(method => method.LogicalId).Distinct().Count());
            Assert.AreEqual(
                2,
                collisionOverloadMethods.Select(method => method.MetadataToken).Distinct().Count());
            Assert.AreEqual(3, instancePointerOverloadMethods.Length);
            Assert.AreEqual(
                3,
                instancePointerOverloadMethods.Select(method => method.Id).Distinct().Count());
            // 判断同一测试文件中的托管类型是否建立了预期接口函数关系。
            bool HasManagedRelation(string contractName, string implementationName)
            {
                TypeEntry contractType = result.Types.Single(type =>
                    type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                    && type.Name == contractName);
                TypeEntry implementationType = result.Types.Single(type =>
                    type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                    && type.Name == implementationName);
                MethodEntry contractMethod = result.GetMethods(contractType).Single(method =>
                    method.Name == "Accept");
                MethodEntry implementationMethod = result.GetMethods(implementationType).Single(method =>
                    method.Name == "Accept");

                return implementationMethod.RelatedMethodIds.Contains(
                    contractMethod.LogicalId,
                    StringComparer.Ordinal);
            }
        }

        // 检查名字形似调用约定但实际零泛型的类型仍按普通命名类型读取。
        /// <summary>
        /// 验证反引号只是合法名称字符时不会改变类型身份或普通函数签名。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncTreatsBacktickCallConvNameAsOrdinaryType()
        {
            using TestProject project = TestProject.CreateWithCustomModifiers();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry literalType = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.FullName == "System.Runtime.CompilerServices.CallConv`Literal");
            TypeEntry userType = result.Types.Single(type =>
                type.AssemblyPath == Path.Combine(project.RootPath, "CustomBase.dll")
                && type.Name == "LiteralBacktickConventionUser");
            MethodEntry echo = result.GetMethods(userType).Single(method => method.Name == "Echo");

            Assert.AreEqual(literalType.LogicalId, echo.ReturnTypeId);
            Assert.AreEqual(literalType.LogicalId, echo.Parameters.Single().TypeId);
        }

        // 检查编译门面中的类型名称可以定位到真实托管类型。
        /// <summary>
        /// 验证类型转交文件的身份可以查到真实函数所在类型。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncMapsForwardedTypeIdentityToImplementationType()
        {
            using TestProject project = TestProject.CreateWithForwardedType();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.Name == "ForwardedType");
            TypeEntry forwarderCollision = result.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.Name == "CollisionType");
            TypeEntry targetCollision = result.Types.Single(type =>
                type.AssemblyPath == project.ForwardTargetPath
                && type.Name == "CollisionType");
            string forwardedAlias = implementation.AliasIds.Single();

            CollectionAssert.Contains(
                result.TypesByLogicalId[forwardedAlias]
                    .Select(type => type.Id)
                    .ToArray(),
                implementation.Id);
            CollectionAssert.Contains(
                result.TypesByLogicalId[forwarderCollision.LogicalId]
                    .Select(type => type.Id)
                    .ToArray(),
                forwarderCollision.Id);
            CollectionAssert.DoesNotContain(
                result.TypesByLogicalId[forwarderCollision.LogicalId]
                    .Select(type => type.Id)
                    .ToArray(),
                targetCollision.Id);
        }

        // 检查程序集名和类型名中的合法分隔字符不会合并两个真实类型。
        /// <summary>
        /// 验证类型身份保留程序集、命名空间和嵌套类型的字段边界。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsNamedTypeBoundariesSeparate()
        {
            using TestProject project = TestProject.CreateWithTypeIdentityCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry firstContract = result.Types.Single(type =>
                type.AssemblyName == "A" && type.FullName == "B|C");
            TypeEntry secondContract = result.Types.Single(type =>
                type.AssemblyName == "A|B" && type.FullName == "C");
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyName == "CollisionImplementation"
                && type.FullName == "Implementation");
            MethodEntry firstMethod = result.GetMethods(firstContract).Single(method =>
                method.Name == "Accept");
            MethodEntry secondMethod = result.GetMethods(secondContract).Single(method =>
                method.Name == "Accept");
            MethodEntry implementationMethod = result.GetMethods(implementation).Single(method =>
                method.Name == "Accept");
            TypeEntry[] plusTypes = result.Types.Where(type =>
                type.AssemblyName == "A" && type.FullName == "Outer+Inner").ToArray();
            TypeEntry[] dotTypes = result.Types.Where(type =>
                type.AssemblyName == "A" && type.FullName == "N.X.Y").ToArray();
            TypeEntry weirdContract = result.Types.Single(type =>
                type.AssemblyName == "A" && type.FullName == "IWeirdContract");
            TypeEntry weirdImplementation = result.Types.Single(type =>
                type.AssemblyName == "A" && type.FullName == "WeirdImplementation");
            MethodEntry weirdContractMethod = result.GetMethods(weirdContract).Single();
            MethodEntry weirdImplementationMethod = result.GetMethods(weirdImplementation).Single();
            TypeEntry backtickType = result.Types.Single(type =>
                type.AssemblyName == "A" && type.FullName == "Plain`Name");

            Assert.AreNotEqual(firstContract.LogicalId, secondContract.LogicalId);
            Assert.AreNotEqual(firstMethod.LogicalId, secondMethod.LogicalId);
            Assert.AreEqual(2, plusTypes.Length);
            Assert.AreEqual(2, plusTypes.Select(type => type.LogicalId).Distinct().Count());
            Assert.AreEqual(2, dotTypes.Length);
            Assert.AreEqual(2, dotTypes.Select(type => type.LogicalId).Distinct().Count());
            CollectionAssert.Contains(
                implementationMethod.RelatedMethodIds.ToArray(),
                firstMethod.LogicalId);
            CollectionAssert.DoesNotContain(
                implementationMethod.RelatedMethodIds.ToArray(),
                secondMethod.LogicalId);
            Assert.AreEqual(CatalogRefKind.None, weirdContractMethod.Parameters.Single().RefKind);
            Assert.AreEqual(CatalogRefKind.None, weirdImplementationMethod.Parameters.Single().RefKind);
            CollectionAssert.Contains(
                weirdImplementationMethod.RelatedMethodIds.ToArray(),
                weirdContractMethod.LogicalId);
            Assert.AreEqual("Plain`Name", backtickType.Name);
        }

        // 检查只跳过元数据首行伪类型而保留其他位置的同名合法类型。
        /// <summary>
        /// 验证命名空间中的合法尖括号类型不会被模块伪类型规则误删。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncSkipsPseudoModuleAndKeepsLegalModuleName()
        {
            using TestProject project = TestProject.CreateWithTypeIdentityCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry legalModuleName = result.Types.Single(type =>
                type.AssemblyName == "A" && type.FullName == "X.<Module>");

            Assert.AreEqual("<Module>", legalModuleName.Name);
            Assert.IsFalse(result.Types.Any(type =>
                type.AssemblyName == "A" && type.FullName == "<Module>"));
        }

        // 检查引用与定义只差程序集名称大小写时仍连接到同一接口函数。
        /// <summary>
        /// 验证托管运行时的程序集简单名称大小写规则用于闭合函数关系。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncMatchesAssemblySimpleNamesIgnoringCase()
        {
            using TestProject project = TestProject.CreateWithTypeIdentityCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry contract = result.Types.Single(type =>
                type.AssemblyName == "A" && type.FullName == "B|C");
            TypeEntry implementation = result.Types.Single(type =>
                type.AssemblyName == "CollisionImplementation"
                && type.FullName == "Implementation");
            MethodEntry contractMethod = result.GetMethods(contract).Single(method =>
                method.Name == "Accept");
            MethodEntry implementationMethod = result.GetMethods(implementation).Single(method =>
                method.Name == "Accept");

            CollectionAssert.Contains(
                implementationMethod.RelatedMethodIds.ToArray(),
                contractMethod.LogicalId);
        }

        // 检查大小写碰撞的真实程序集不会因输入顺序或并行数量改变规范名称。
        /// <summary>
        /// 验证同一组物理文件按不同顺序、不同并行数量建立完全一致的公开总表。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsCaseCollisionCanonicalAcrossInputOrderAndJobs()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            MaterialSet loaded = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            (string upperPath, string lowerPath) = project.WriteAssemblyCaseCollisionFiles();
            ExternalAssemblyMaterial upper = new(upperPath, new[] { upperPath });
            ExternalAssemblyMaterial lower = new(lowerPath, new[] { lowerPath });
            ExternalAssemblyMaterial[] unchanged = loaded.ExternalAssemblies.ToArray();
            MaterialSet upperFirst = loaded with
            {
                ExternalAssemblies = unchanged.Concat(new[] { upper, lower }).ToArray(),
            };
            MaterialSet lowerFirst = loaded with
            {
                ExternalAssemblies = unchanged.Concat(new[] { lower, upper }).ToArray(),
            };
            MethodCatalog catalog = new();

            MethodCatalogResult upperFirstOneJob = await catalog.BuildAsync(upperFirst, 1);
            MethodCatalogResult lowerFirstOneJob = await catalog.BuildAsync(lowerFirst, 1);
            MethodCatalogResult lowerFirstFourJobs = await catalog.BuildAsync(lowerFirst, 4);
            string expectedSnapshot = ReadCaseCollisionSnapshot(upperFirstOneJob);

            Assert.AreEqual(expectedSnapshot, ReadCaseCollisionSnapshot(lowerFirstOneJob));
            Assert.AreEqual(expectedSnapshot, ReadCaseCollisionSnapshot(lowerFirstFourJobs));
            TypeEntry[] sharedTypes = upperFirstOneJob.Types.Where(type =>
                type.FullName == "CaseIdentity.SharedType").ToArray();
            MethodEntry[] sharedMethods = sharedTypes
                .SelectMany(upperFirstOneJob.GetMethods)
                .Where(method => method.Name == "Touch")
                .ToArray();
            Assert.AreEqual(2, sharedTypes.Length);
            Assert.IsTrue(sharedTypes.All(type => type.AssemblyName == "A"));
            Assert.AreEqual(1, sharedTypes.Select(type => type.LogicalId).Distinct().Count());
            Assert.AreEqual(2, sharedTypes.Select(type => type.Id).Distinct().Count());
            Assert.AreEqual(1, sharedMethods.Select(method => method.LogicalId).Distinct().Count());
            Assert.AreEqual(2, sharedMethods.Select(method => method.Id).Distinct().Count());
        }

        // 检查同程序集身份的不同物理文件不会互相覆盖。
        /// <summary>
        /// 验证函数身份包含实际文件来源，而不是只依赖程序集名称。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncKeepsDuplicateExternalAssemblyIdentitiesSeparate()
        {
            using TestProject project = TestProject.CreateWithDuplicateExternalIdentity();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry[] externalTypes = result.Types
                .Where(type => type.Name == "ExternalType")
                .ToArray();

            Assert.AreEqual(2, externalTypes.Length);
            Assert.AreEqual(externalTypes[0].LogicalId, externalTypes[1].LogicalId);
            Assert.AreNotEqual(externalTypes[0].Id, externalTypes[1].Id);
            Assert.IsTrue(externalTypes.All(type =>
                result.GetMethods(type).Any(method => method.Name == "Change")));
        }

        // 检查同一门面类型不能静默对应两个真实类型。
        /// <summary>
        /// 验证歧义类型身份会停止建立函数总表并列出全部候选。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncRejectsAmbiguousForwardedTypeIdentity()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            MaterialSet loaded = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            project.WriteAmbiguousForwardedAssemblies();
            string secondFacadePath = Path.Combine(project.RootPath, "FacadeCopy.dll");
            string secondTargetPath = Path.Combine(project.RootPath, "ForwardTargetB.dll");
            MaterialSet material = loaded with
            {
                ExternalAssemblies = new[]
                {
                    new ExternalAssemblyMaterial(
                        project.ExternalAssemblyPath,
                        new[]
                        {
                            project.ExternalAssemblyPath,
                            secondFacadePath,
                            project.ForwardTargetPath,
                            secondTargetPath,
                        }),
                },
            };

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new MethodCatalog().BuildAsync(material, 2));

            StringAssert.Contains(exception.Message, "Facade");
            StringAssert.Contains(exception.Message, "ImplA");
            StringAssert.Contains(exception.Message, "ImplB");
        }

        // 检查目标包内的源码依赖也进入最终统计。
        /// <summary>
        /// 验证按包目录自动纳入函数，而不是只认根程序集名称。
        /// </summary>
        [TestMethod]
        public async Task BuildAsyncReportsSourceDependenciesInsideTargetPackage()
        {
            using TestProject project = TestProject.CreateWithTargetPackageDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));

            MethodCatalogResult result = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry dependency = result.Methods.Single(method =>
                method.Name == "DependencyMethod");

            Assert.IsTrue(dependency.IsReportable);
        }

        // 对比单线程和四线程建立的稳定函数清单。
        /// <summary>
        /// 验证并行数量不会改变函数、类型和索引顺序。
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

        // 读取大小写碰撞类型及其函数的规范名称、逻辑身份和物理身份。
        private static string ReadCaseCollisionSnapshot(MethodCatalogResult result)
        {
            TypeEntry[] types = result.Types
                .Where(type => type.FullName == "CaseIdentity.SharedType")
                .OrderBy(type => type.Id, StringComparer.Ordinal)
                .ToArray();
            IEnumerable<string> typeLines = types.Select(type =>
                $"Type|{type.AssemblyName}|{type.LogicalId}|{type.Id}");
            IEnumerable<string> methodLines = types
                .SelectMany(result.GetMethods)
                .Where(method => method.Name == "Touch")
                .OrderBy(method => method.Id, StringComparer.Ordinal)
                .Select(method => $"Method|{method.AssemblyName}|{method.LogicalId}|{method.Id}");

            return string.Join('\n', typeLines.Concat(methodLines));
        }
    }
}
