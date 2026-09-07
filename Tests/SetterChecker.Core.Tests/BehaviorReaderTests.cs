namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证源码和真实托管函数会生成相同的行为事实。
    /// </summary>
    [TestClass]
    public sealed class BehaviorReaderTests
    {
        // 验证泛型调用保留原始方法规格标记，同时另存实际本地定义标记。
        /// <summary>
        /// 源码内存产物和 DLL 的泛型调用都使用原始 MethodSpec，而不是解包后的 MethodDef。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncPreservesOriginalGenericCallMetadataToken()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    public static T Echo<T>(T value) => value;
                    public static int Run() => Echo(1);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls")
                .SelectMany(catalog.GetMethods).Where(method => method.Name == "Run").ToArray();
            Assert.HasCount(2, roots);

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(material, catalog, roots, 2);

            foreach (MethodBehavior body in result.Methods)
            {
                BehaviorMethodReference reference = body.Calls.Single().Target;
                Assert.AreEqual(0x2b000000, reference.ReferenceMetadataToken & unchecked((int)0xff000000));
                Assert.IsNotNull(reference.KnownMetadataToken);
                Assert.AreEqual(0x06000000, reference.KnownMetadataToken.Value & unchecked((int)0xff000000));
                MethodEntry caller = roots.Single(method => method.Id == body.MethodId);
                MethodEntry target = catalog.GetMethods(catalog.TypesById[caller.TypeId]).Single(method => method.Name == "Echo");
                Assert.AreEqual(target.MetadataToken, reference.KnownMetadataToken.Value);
                CollectionAssert.AreEqual(new[] { "System.Int32" }, reference.GenericArgumentTypeIds.ToArray());
            }
        }

        // 验证函数参数和返回值经过类型转交后仍定位同一真实声明。
        /// <summary>
        /// 参考库中的泛型嵌套类型与运行库中的定义不能按程序集名称误判不同。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncResolvesForwardedParameterAndReturnTypes()
        {
            using TestProject project = TestProject.CreateWithForwardedMethodSignature();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry consumer = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "SignatureConsumer");
            MethodEntry root = catalog.GetMethods(consumer).Single(method => method.Name == "Run");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(material, catalog, new[] { root }, 2);

            CollectionAssert.AreEqual(new[] { ".ctor", "GetEnumerator", "Accept" },
                result.Methods.Single().Calls.Select(call => call.Target.Name).ToArray());
        }

        // 验证本地定义标记与外部引用来源在行为事实中完整保留。
        /// <summary>
        /// 同名类型在本地保留定义并向外转交时，函数、对象和字段仍可区分。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncPreservesLocalTokensAndExternalReferenceScope()
        {
            using TestProject project = TestProject.CreateWithDefinitionAndSameFileForwarder();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            catalog.RequireClosedHierarchy(catalog.Types.Single(type =>
                type.FullName == "SameFileForwardingConsumer.ExternalDerived"));
            TypeEntry localEntry = catalog.Types.Single(type => type.AssemblyPath == project.ExternalReferencePath
                && type.FullName == "SameFileForwarding.Entry");
            MethodEntry constructor = catalog.GetMethods(localEntry).Single(method => method.Name == ".ctor");
            foreach ((string typeName, string methodName, bool isLocal) in new[]
            {
                ("SameFileForwarding.LocalDerived", "FieldLocal", true),
                ("SameFileForwardingConsumer.ExternalDerived", "FieldExternal", false),
            })
            {
                TypeEntry type = catalog.Types.Single(candidate => candidate.FullName == typeName);
                MethodEntry root = catalog.GetMethods(type).Single(method => method.Name == methodName);
                MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                    material, catalog, new[] { root }, 2)).Methods.Single();
                BehaviorValue created = behavior.Values.Single(value => value.Kind == BehaviorValueKind.NewObject
                    && value.Type!.DefinitionId == localEntry.LogicalId);
                BehaviorCall call = behavior.Calls.Single(candidate => candidate.Target.Name == ".ctor"
                    && candidate.Target.DeclaringTypeDefinitionId == localEntry.LogicalId);
                BehaviorMemberReference field = behavior.Writes.Single(write => write.Kind == BehaviorWriteKind.Field).Member!;

                Assert.AreEqual(isLocal ? localEntry.Id : null, created.Type!.KnownTypeId);
                Assert.AreEqual(isLocal ? localEntry.Id : null, call.Target.KnownDeclaringTypeId);
                Assert.AreEqual(isLocal ? constructor.MetadataToken : null, call.Target.KnownMetadataToken);
                Assert.AreEqual(isLocal ? constructor.MetadataToken : 0x0A000000,
                    isLocal ? call.Target.ReferenceMetadataToken : call.Target.ReferenceMetadataToken & unchecked((int)0xFF000000));
                Assert.AreEqual(isLocal ? localEntry.Id : null, field.KnownDeclaringTypeId);
                Assert.AreEqual(localEntry.AssemblyIdentity, call.Target.TargetAssemblyIdentity);
                Assert.AreEqual(localEntry.AssemblyIdentity, field.TargetAssemblyIdentity);
                Assert.AreEqual(root.AssemblyPath, call.Target.ReferringAssemblyPath);
                Assert.AreEqual(root.AssemblyPath, field.ReferringAssemblyPath);
            }
        }

        // 验证大正数并行上限不会让分块运算溢出或改变结果。
        /// <summary>
        /// 验证工作数量仅作为上限，实际少量函数不受整数乘法溢出影响。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncAcceptsLargePositiveJobLimits()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                1));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 1);
            MethodEntry target = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample" && method.Name == "Apply");
            BehaviorReader reader = new();
            BehaviorReadResult expected = await reader.ReadAsync(material, catalog, new[] { target }, 1);
            foreach (int jobs in new[] { 1 << 30, int.MaxValue })
            {
                BehaviorReadResult actual = await reader.ReadAsync(material, catalog, new[] { target }, jobs);
                Assert.AreEqual(
                    System.Text.Json.JsonSerializer.Serialize(expected.Methods),
                    System.Text.Json.JsonSerializer.Serialize(actual.Methods));
            }
        }

        // 检查源码与托管对象创建都记录完整类型，而不是只记录元素或调用名称。
        /// <summary>
        /// 验证新对象和一维数组可用同一份结构化类型进入后续分析。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsCreatedObjectAndArrayTypes()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "CreateAndReturn",
                "CreateArray");

            foreach (MethodBehavior behavior in pairs["CreateAndReturn"])
            {
                BehaviorCall creation = behavior.Calls.Single();
                BehaviorValue value = behavior.Values[creation.ResultValueId!.Value];
                Assert.IsNotNull(value.Type);
                Assert.AreEqual(creation.Target.DeclaringTypeId, value.Type.Id);
            }

            BehaviorValue[] arrays = pairs["CreateArray"].Select(behavior =>
                behavior.Values.Single(value => value.Kind == BehaviorValueKind.NewArray)).ToArray();
            Assert.IsNotNull(arrays[0].Type);
            Assert.IsNotNull(arrays[1].Type);
            Assert.AreEqual(arrays[0].Type!.Id, arrays[1].Type!.Id);
            StringAssert.Contains(arrays[0].Type!.Id, "[]");
        }

        // 检查读取调用指令时不会接受文件名相同但版本不同的依赖。
        /// <summary>
        /// 验证托管行为读取与函数总表使用相同的完整程序集身份要求。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncRejectsMismatchedTransitiveAssemblyVersion()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                       project.ForwardTargetPath,
                       new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                module.Assembly.Name.Version = new Version(9, 0, 0, 0);
                module.Write(project.ForwardTargetPath);
            }

            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry type = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "TransitiveSamples.DerivedType");
            MethodEntry caller = catalog.GetMethods(type).Single(method => method.Name == "Call");

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new BehaviorReader().ReadAsync(material, catalog, new[] { caller }, 2));

            StringAssert.Contains(exception.Message, "ForwardTarget");
            StringAssert.Contains(exception.Message, "Version=0.0.0.0");
            StringAssert.Contains(exception.Message, project.ExternalAssemblyPath);
        }

        // 检查未实现的托管指令不会因固定栈数量而被当作普通计算。
        /// <summary>
        /// 验证遇到尚未读取的跳转调用时明确停止并指出指令位置。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncRejectsUnsupportedManagedInstruction()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                       project.ExternalAssemblyPath,
                       new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition method = module.Types
                    .Single(type => type.FullName == "ExternalSamples.BehaviorSample")
                    .Methods.Single(method => method.Name == "Apply");
                method.Body.Instructions.Clear();
                method.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(
                    Mono.Cecil.Cil.OpCodes.Jmp,
                    method));
                module.Write(project.ExternalAssemblyPath);
            }

            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry type = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry target = catalog.GetMethods(type).Single(method => method.Name == "Apply");

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new BehaviorReader().ReadAsync(material, catalog, new[] { target }, 2));

            StringAssert.Contains(exception.Message, target.Id);
            StringAssert.Contains(exception.Message, "jmp");
        }

        // 验证工具总入口只读取当前可报告根函数行为。
        /// <summary>
        /// 验证模块三已经接入固定分析顺序，而不是只供测试单独调用。
        /// </summary>
        [TestMethod]
        public async Task AnalyzeAsyncReadsReportableSourceMethodBehaviors()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();

            (MaterialSet _, MethodCatalogResult catalog, BehaviorReadResult behaviors) =
                await new SetterChecker().AnalyzeAsync(new MaterialRequest(
                    project.AssemblyDefinitionPath,
                    4));

            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable).ToArray();

            Assert.HasCount(roots.Length, behaviors.Methods);
            CollectionAssert.AreEqual(
                roots.Select(method => method.Id).Order(StringComparer.Ordinal).ToArray(),
                behaviors.Methods.Select(method => method.MethodId).ToArray());
        }

        // 验证当前对象字段写入会保留接收对象和值的来源。
        /// <summary>
        /// 验证源码与托管函数都能精确读取当前对象字段写入。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsInstanceFieldWriteFromSourceAndManagedMethod()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(method =>
                method.AssemblyName == "khengine.runtime"
                && method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "WriteField");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(method =>
                method.Name == "WriteField");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { sourceMethod, managedMethod },
                2);

            AssertFieldWrite(result.MethodsById[sourceMethod.Id]);
            AssertFieldWrite(result.MethodsById[managedMethod.Id]);
        }

        // 验证赋值表达式既记录写入也把右侧值作为表达式结果。
        /// <summary>
        /// 验证返回赋值表达式时不会把赋值节点当成未知值。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsAssignmentExpressionValue()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "AddAndReturn",
                "AssignAndReturn",
                "IncrementAndReturn");

            foreach (MethodBehavior behavior in pairs.Values.SelectMany(items => items))
            {
                BehaviorWrite write = behavior.Writes.Single();
                BehaviorReturn returned = behavior.Returns.Single();

                Assert.IsTrue(
                    behavior.Values.Any(value =>
                        ReachesAtPoint(behavior, write.ValueId, value.Id)
                        && ReachesAtPoint(behavior, returned.ValueId!.Value, value.Id)),
                    behavior.MethodId);
            }
        }

        // 验证间接动态链接库调用保留之后精确载入目标所需的信息。
        /// <summary>
        /// 验证 A.dll 调用 B.dll 时，行为结果可以把 B 的目标函数接回统一总表。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsTransitiveAssemblyCallResolvable()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry callerType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "TransitiveSamples.DerivedType");
            MethodEntry caller = catalog.GetMethods(callerType).Single(method => method.Name == "Call");

            BehaviorReadResult behaviors = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { caller },
                2);
            BehaviorMethodReference target = behaviors.Methods.Single().Calls.Single().Target;
            IReadOnlyList<TypeEntry> targetTypes = catalog.LoadAssemblyTypes(
                new System.Reflection.AssemblyName(target.TargetAssemblyIdentity),
                target.ReferringAssemblyPath!);
            TypeEntry targetType = targetTypes.Single(type =>
                type.FullName == "TransitiveSamples.BaseType");

            Assert.IsTrue(catalog.GetMethods(targetType).Any(method => method.Name == "Touch"));
        }

        // 验证静态字段、数组元素和引用位置会保留不同写入种类。
        /// <summary>
        /// 验证源码与托管函数都能读取三种基础写入位置。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsStaticArrayAndReferenceWrites()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && method.Name is "WriteStatic" or "WriteArray" or "WriteReference")
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    method.Name is "WriteStatic" or "WriteArray" or "WriteReference")
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                BehaviorWrite write = behavior.Writes.Single();

                switch (method.Name)
                {
                    case "WriteStatic":
                        Assert.AreEqual(BehaviorWriteKind.Field, write.Kind);
                        Assert.IsNull(write.ReceiverValueId);
                        AssertParameter(behavior, write.ValueId, 0);
                        break;
                    case "WriteArray":
                        Assert.AreEqual(BehaviorWriteKind.ArrayElement, write.Kind);
                        AssertParameter(behavior, write.ReceiverValueId!.Value, 0);
                        AssertParameter(behavior, write.ValueId, 1);
                        break;
                    case "WriteReference":
                        Assert.AreEqual(BehaviorWriteKind.Indirect, write.Kind);
                        AssertParameter(behavior, write.ReceiverValueId!.Value, 0);
                        AssertParameter(behavior, write.ValueId, 1);
                        break;
                }
            }
        }

        // 验证普通调用和创建对象会保留结构化目标、实参、结果与返回关系。
        /// <summary>
        /// 验证源码与托管函数都能读取调用和对象创建的完整事实。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsCallsCreationAssignmentsAndReturns()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && method.Name is "Call" or "CreateAndReturn")
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    method.Name is "Call" or "CreateAndReturn")
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                BehaviorCall call = behavior.Calls.Single();

                if (method.Name == "Call")
                {
                    Assert.AreEqual(BehaviorCallKind.Virtual, call.Kind);
                    Assert.AreEqual("Read", call.Target.Name);
                    Assert.IsNotNull(call.ReceiverValueId);
                    AssertParameter(behavior, call.ReceiverValueId.Value, 0);
                    AssertParameter(behavior, call.Arguments.Single().ValueId, 1);
                    Assert.IsNotNull(call.ResultValueId);
                    Assert.AreEqual(
                        BehaviorValueKind.CallResult,
                        behavior.Values[call.ResultValueId.Value].Kind);
                    AssertValueFlowsFrom(
                        behavior,
                        behavior.Returns.Single().ValueId!.Value,
                        call.ResultValueId.Value);
                }
                else
                {
                    Assert.AreEqual(BehaviorCallKind.ObjectCreation, call.Kind);
                    Assert.AreEqual(".ctor", call.Target.Name);
                    Assert.IsNotNull(call.ResultValueId);
                    Assert.AreEqual(
                        BehaviorValueKind.NewObject,
                        behavior.Values[call.ResultValueId.Value].Kind);
                    AssertValueFlowsFrom(
                        behavior,
                        behavior.Writes.Single().ReceiverValueId!.Value,
                        call.ResultValueId.Value);
                    AssertValueFlowsFrom(
                        behavior,
                        behavior.Returns.Single().ValueId!.Value,
                        call.ResultValueId.Value);
                }
            }
        }

        // 验证分支中的两个赋值来源和字段读取不会被线性指令顺序破坏。
        /// <summary>
        /// 验证源码与托管函数都能保留分支来源和字段读取事实。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsBranchAssignmentsAndFieldRead()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "ChooseAndWrite");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(method =>
                method.Name == "ChooseAndWrite");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { sourceMethod, managedMethod },
                2);

            foreach (MethodEntry method in new[] { sourceMethod, managedMethod })
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                BehaviorWrite write = behavior.Writes.Single();
                int receiverId = write.ReceiverValueId!.Value;
                int[] possibleSources = behavior.Values
                    .Where(value => value.Kind == BehaviorValueKind.Parameter
                        && ReachesAtPoint(behavior, receiverId, value.Id))
                    .Select(value => value.ParameterIndex!.Value)
                    .Order()
                    .ToArray();

                CollectionAssert.AreEqual(new[] { 0, 1 }, possibleSources);
                BehaviorValue writtenValue = behavior.Values[write.ValueId];
                Assert.AreEqual(BehaviorValueKind.FieldRead, writtenValue.Kind);
                Assert.AreEqual("m_value", writtenValue.Member!.Name);
                Assert.AreEqual(
                    BehaviorValueKind.CurrentInstance,
                    behavior.Values[writtenValue.InputValueIds.Single()].Kind);
                int returnedId = behavior.Returns.Single().ValueId!.Value;
                foreach (BehaviorValue source in behavior.Values.Where(value =>
                             value.Kind == BehaviorValueKind.Parameter
                             && value.ParameterIndex is 0 or 1))
                {
                    AssertReachesAtPoint(behavior, returnedId, source.Id);
                }
            }
        }

        // 验证属性、事件和集合修改在本模块只保留为普通调用事实。
        /// <summary>
        /// 验证源码与托管函数不会把调用点提前判断为直接写入。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsPropertyEventAndCollectionAsCalls()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] names = { "AddCollection", "AddEvent", "SetProperty" };
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            IReadOnlyDictionary<string, string> expectedTargets = new Dictionary<string, string>
            {
                ["AddCollection"] = "Add",
                ["AddEvent"] = "add_Changed",
                ["SetProperty"] = "set_Value",
            };
            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];

                Assert.IsEmpty(behavior.Writes);
                Assert.AreEqual(expectedTargets[method.Name], behavior.Calls.Single().Target.Name);
            }
        }

        // 验证自动事件访问器保留旧委托、新委托和真实合并或移除调用。
        /// <summary>
        /// 验证源码与 DLL 的 add/remove 访问器不会退化成无来源的字段覆盖。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsAutomaticEventAccessors()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "add_Changed",
                "remove_Changed");

            foreach ((string accessor, MethodBehavior[] behaviors) in pairs)
            {
                string expectedTarget = accessor == "add_Changed" ? "Combine" : "Remove";
                foreach (MethodBehavior behavior in behaviors)
                {
                    BehaviorCall call = behavior.Calls.Single(item =>
                        item.Target.Name == expectedTarget
                        && item.Target.DeclaringTypeDefinitionId.Contains(
                            "System.Delegate",
                            StringComparison.Ordinal));
                    BehaviorValue oldValue = behavior.Values.Single(value =>
                        value.Kind == BehaviorValueKind.FieldRead
                        && value.Member?.Name == "Changed");

                    AssertValueFlowsFrom(behavior, call.Arguments[0].ValueId, oldValue.Id);
                    AssertParameter(behavior, call.Arguments[1].ValueId, 0);
                    if (behavior.Writes.Count > 0)
                    {
                        BehaviorWrite write = behavior.Writes.Single(item =>
                            item.Member?.Name == "Changed");
                        AssertValueFlowsFrom(
                            behavior,
                            write.ValueId,
                            call.ResultValueId!.Value);
                    }
                }
            }
        }

        // 验证泛型声明类型和泛型函数调用分别保留定义、实参和实例化签名。
        /// <summary>
        /// 验证源码与托管函数都能精确读取泛型字段和函数引用。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncInstantiatesGenericMemberReferences()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] names = { "UseBox", "UseGeneric" };
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                if (method.Name == "UseBox")
                {
                    BehaviorWrite write = behavior.Writes.Single();
                    Assert.AreEqual("System.Int32", write.Member!.FieldTypeId);
                    CollectionAssert.AreEqual(
                        new[] { "System.Int32" },
                        write.Member.DeclaringTypeArgumentIds.ToArray());
                    BehaviorMethodReference target = behavior.Calls.Single().Target;
                    CollectionAssert.AreEqual(
                        new[] { "System.Int32" },
                        target.ParameterTypeIds.ToArray());
                    Assert.AreEqual("System.Int32", target.ReturnTypeId);
                    CollectionAssert.AreEqual(
                        new[] { "System.Int32" },
                        target.DeclaringTypeArgumentIds.ToArray());
                }
                else
                {
                    BehaviorMethodReference target = behavior.Calls.Single().Target;
                    Assert.AreEqual(BehaviorCallKind.Direct, behavior.Calls.Single().Kind);
                    CollectionAssert.AreEqual(
                        new[] { "!!0" },
                        target.ParameterTypeIds.ToArray());
                    Assert.AreEqual("!!0", target.ReturnTypeId);
                    CollectionAssert.AreEqual(
                        new[] { "System.Int32" },
                        target.GenericArgumentTypeIds.ToArray());
                }
            }
        }

        // 验证条件表达式在两条执行路径汇合后保留全部可能接收对象。
        /// <summary>
        /// 验证源码与托管函数使用控制流汇合值，而不是按文本顺序猜测。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncMergesEvaluationStackAcrossBranches()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "WriteChosen");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(method =>
                method.Name == "WriteChosen");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { sourceMethod, managedMethod },
                2);

            foreach (MethodEntry method in new[] { sourceMethod, managedMethod })
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                BehaviorWrite write = behavior.Writes.Single();
                BehaviorValue receiver = behavior.Values[write.ReceiverValueId!.Value];
                int[] parameterValueIds = behavior.Values.Where(value =>
                        value.Kind == BehaviorValueKind.Parameter
                        && value.ParameterIndex is 0 or 1)
                    .OrderBy(value => value.ParameterIndex)
                    .Select(value => value.Id)
                    .ToArray();

                Assert.HasCount(2, parameterValueIds);
                foreach (int parameterValueId in parameterValueIds)
                {
                    AssertValueFlowsFrom(behavior, receiver.Id, parameterValueId);
                }

                int inputValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 3).Id;
                AssertValueFlowsFrom(behavior, write.ValueId, inputValueId);
            }
        }

        // 验证字段、数组元素地址和数组读取都保留可追踪的对象来源。
        /// <summary>
        /// 验证源码与托管函数都能读取引用实参和数组读取事实。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsManagedAddressesAndArrayElements()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] names = { "PassArrayByReference", "PassFieldByReference", "ReadArray" };
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                if (method.Name == "ReadArray")
                {
                    BehaviorValue value = behavior.Values.Single(item =>
                        item.Kind == BehaviorValueKind.ArrayElementRead);
                    AssertParameter(behavior, value.InputValueIds[0], 0);
                    AssertParameter(behavior, value.InputValueIds[1], 1);

                    continue;
                }

                BehaviorCall call = behavior.Calls.Single();
                BehaviorArgument argument = call.Arguments[0];
                BehaviorValue address = behavior.Values[argument.ValueId];

                Assert.AreEqual(CatalogRefKind.Ref, argument.RefKind);
                Assert.AreEqual(BehaviorValueKind.Address, address.Kind);
                if (method.Name == "PassFieldByReference")
                {
                    Assert.AreEqual("m_value", address.Member!.Name);
                    Assert.AreEqual(
                        BehaviorValueKind.CurrentInstance,
                        behavior.Values[address.InputValueIds.Single()].Kind);
                }
                else
                {
                    AssertParameter(behavior, address.InputValueIds[0], 0);
                }
            }
        }

        // 验证委托调用和方法绑定分别保留调用点与绑定函数身份。
        /// <summary>
        /// 验证源码与托管函数都能读取委托使用所需的原始事实。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsDelegateInvocationAndBinding()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] names = { "BindDelegate", "InvokeDelegate" };
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                if (method.Name == "InvokeDelegate")
                {
                    BehaviorCall call = behavior.Calls.Single();
                    Assert.AreEqual(BehaviorCallKind.Delegate, call.Kind);
                    Assert.AreEqual("Invoke", call.Target.Name);
                    AssertParameter(behavior, call.ReceiverValueId!.Value, 0);
                }
                else
                {
                    BehaviorValue function = behavior.Values.Single(value =>
                        value.Kind == BehaviorValueKind.Function);
                    Assert.AreEqual("Read", function.Method!.Name);
                    BehaviorCall constructor = behavior.Calls.Single();
                    Assert.AreEqual(BehaviorCallKind.ObjectCreation, constructor.Kind);
                    Assert.AreEqual(function.Id, constructor.Arguments[1].ValueId);
                }
            }
        }

        // 验证相同签名匿名函数仍指向不同生成函数并保存闭包来源。
        /// <summary>
        /// 验证控制流匿名函数不会退化为无法区分的逻辑函数名。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsAnonymousFunctionIdentityAndCaptures()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "BindCaptured");

            MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { method },
                2)).Methods.Single();
            BehaviorValue[] functions = behavior.Values.Where(value =>
                    value.Kind == BehaviorValueKind.Function)
                .ToArray();
            Assert.HasCount(2, functions);
            Assert.AreEqual(
                2,
                functions.Select(value =>
                        $"{value.Method!.DeclaringTypeDefinitionId}|{value.Method.Name}|"
                            + string.Join('|', value.Method.ParameterTypeIds))
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.IsTrue(functions.All(value =>
                ResolveManagedMethod(catalog, value.Method!).MetadataToken > 0));
            BehaviorValue parameter = behavior.Values.Single(value =>
                value.Kind == BehaviorValueKind.Parameter
                && value.ParameterIndex == 0);
            Assert.IsTrue(behavior.Writes.Any(write =>
                write.Member?.DeclaringTypeDefinitionId
                    == functions[0].Method!.DeclaringTypeDefinitionId
                && ReachesAtPoint(behavior, write.ValueId, parameter.Id)));
        }

        // 验证局部函数绑定保留实际生成函数和闭包字段来源。
        /// <summary>
        /// 验证局部函数不会在绑定成委托时丢失捕获关系。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsLocalFunctionIdentityAndCaptures()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "BindLocal");

            MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { method },
                2)).Methods.Single();
            BehaviorValue[] functions = behavior.Values.Where(value =>
                    value.Kind == BehaviorValueKind.Function)
                .ToArray();
            Assert.AreEqual(
                1,
                functions.Length,
                string.Join(" | ", behavior.Values.Select(value =>
                    $"{value.Id}:{value.Kind}:{value.Reference}")));
            BehaviorValue function = functions.Single();
            BehaviorMethodReference target = function.Method
                ?? throw new InvalidOperationException("局部函数值缺少生成函数引用。");
            BehaviorValue parameter = behavior.Values.Single(value =>
                value.Kind == BehaviorValueKind.Parameter
                && value.ParameterIndex == 0);

            Assert.IsTrue(ResolveManagedMethod(catalog, target).MetadataToken > 0);
            Assert.IsTrue(behavior.Writes.Any(write =>
                write.Member?.DeclaringTypeDefinitionId
                    == target.DeclaringTypeDefinitionId
                && ReachesAtPoint(behavior, write.ValueId, parameter.Id)));
        }

        // 验证源码与托管转换都保留结构化目标类型。
        /// <summary>
        /// 验证显式转换与安全转换不会只留下操作码文本。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsStructuredConversionTypes()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] names = { "CastSample", "TryCastSample" };
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && names.Contains(method.Name, StringComparer.Ordinal))
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    names.Contains(method.Name, StringComparer.Ordinal))
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                BehaviorValue conversion = result.MethodsById[method.Id].Values.Single(value =>
                    value.Kind == BehaviorValueKind.Conversion);
                Assert.IsNotNull(conversion.Type);
                StringAssert.Contains(conversion.Type.DefinitionId, "BehaviorSample");
            }
        }

        // 验证编译器确认不可达的源码分支与最终 DLL 行为一致。
        /// <summary>
        /// 验证固定为假的分支不会留下字段写入或函数调用。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncSkipsCompilerUnreachableBlocks()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "SkipConstantFalse");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(method =>
                method.Name == "SkipConstantFalse");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { sourceMethod, managedMethod },
                2);

            foreach (MethodEntry method in new[] { sourceMethod, managedMethod })
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                Assert.IsEmpty(behavior.Writes);
                Assert.IsEmpty(behavior.Calls);
            }
        }

        // 验证多维数组的源码与 DLL 都使用数组事实而不是伪函数调用。
        /// <summary>
        /// 验证多维数组读取、写入、创建和引用地址保持一致。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsMultidimensionalArraysWithoutPseudoCalls()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "CreateMatrix",
                "MatrixAddress",
                "ReadMatrix",
                "WriteMatrix");

            foreach (MethodBehavior behavior in pairs.Values.SelectMany(value => value))
            {
                Assert.IsFalse(behavior.Calls.Any(call =>
                    call.Target.Name is "Get" or "Set" or "Address"));
            }

            foreach (MethodBehavior behavior in pairs["ReadMatrix"])
            {
                BehaviorValue read = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.ArrayElementRead);
                Assert.HasCount(3, read.InputValueIds);
            }

            foreach (MethodBehavior behavior in pairs["WriteMatrix"])
            {
                BehaviorWrite write = behavior.Writes.Single();
                Assert.AreEqual(BehaviorWriteKind.ArrayElement, write.Kind);
                Assert.HasCount(2, write.IndexValueIds);
            }

            foreach (MethodBehavior behavior in pairs["CreateMatrix"])
            {
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewArray);
                Assert.HasCount(2, created.InputValueIds);
            }

            foreach (MethodBehavior behavior in pairs["MatrixAddress"])
            {
                BehaviorValue address = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Address);
                Assert.HasCount(3, address.InputValueIds);
                AssertValueFlowsFrom(
                    behavior,
                    behavior.Returns.Single().ValueId!.Value,
                    address.Id);
            }
        }

        // 验证插值字符串保留其中表达式的值来源。
        /// <summary>
        /// 验证真实项目使用的插值字符串能被读取且关联输入参数。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsInterpolatedStringInputs()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "Interpolate");

            MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { method },
                2)).Methods.Single();
            int parameterValueId = behavior.Values.Single(value =>
                value.Kind == BehaviorValueKind.Parameter
                && value.ParameterIndex == 0).Id;

            BehaviorCall inputCall = behavior.Calls.Single(call =>
                call.Arguments.Any(argument => FlowsFrom(
                    behavior,
                    argument.ValueId,
                    parameterValueId,
                    new HashSet<int>())));
            BehaviorCall resultCall = behavior.Calls.Single(call =>
                call.ResultValueId.HasValue
                && FlowsFrom(
                    behavior,
                    behavior.Returns.Single().ValueId!.Value,
                    call.ResultValueId.Value,
                    new HashSet<int>()));

            Assert.IsTrue(inputCall.Position <= resultCall.Position);
            Assert.IsNotNull(inputCall.ReceiverValueId);
            Assert.IsNotNull(resultCall.ReceiverValueId);
            BehaviorValue inputReceiver = behavior.Values.Single(value => value.Id == inputCall.ReceiverValueId);
            BehaviorValue resultReceiver = behavior.Values.Single(value => value.Id == resultCall.ReceiverValueId);
            Assert.AreEqual(BehaviorValueKind.Address, inputReceiver.Kind);
            Assert.AreEqual(BehaviorValueKind.Address, resultReceiver.Kind);
            Assert.HasCount(1, inputReceiver.InputValueIds);
            CollectionAssert.AreEqual(inputReceiver.InputValueIds.ToArray(), resultReceiver.InputValueIds.ToArray());
        }

        // 验证最小整数不会与托管指令解析的内部哨兵混淆。
        /// <summary>
        /// 验证源码与 DLL 都保留 int.MinValue 常量。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsMinimumIntegerConstant()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "ReturnMinValue");

            foreach (MethodBehavior behavior in pairs["ReturnMinValue"])
            {
                BehaviorValue constant = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Constant);
                Assert.AreEqual("-2147483648", constant.Reference);
                AssertValueFlowsFrom(
                    behavior,
                    behavior.Returns.Single().ValueId!.Value,
                    constant.Id);
            }
        }

        // 验证 catch 入口的异常对象具有明确值来源。
        /// <summary>
        /// 验证捕获异常可继续作为属性接收对象参与行为读取。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsCaughtExceptionSource()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "CatchMessage");

            MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { method },
                2)).Methods.Single();
            BehaviorValue exception = behavior.Values.Single(value =>
                value.Kind == BehaviorValueKind.Exception);
            BehaviorCall getter = behavior.Calls.Single(call => call.Target.Name == "get_Message");

            AssertValueFlowsFrom(
                behavior,
                getter.ReceiverValueId!.Value,
                exception.Id);
        }

        // 验证 new T() 保留编译后创建调用的泛型类型身份。
        /// <summary>
        /// 验证类型参数对象创建不会被当成未知源码值。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsTypeParameterObjectCreation()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "CreateGeneric");

            MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { method },
                2)).Methods.Single();
            BehaviorCall creation = behavior.Calls.Single(call =>
                call.Target.Name == "CreateInstance");

            CollectionAssert.AreEqual(
                new[] { "!!0" },
                creation.Target.GenericArgumentTypeIds.ToArray());
            Assert.IsNotNull(creation.ResultValueId);
            AssertValueFlowsFrom(
                behavior,
                behavior.Returns.Single().ValueId!.Value,
                creation.ResultValueId.Value);
        }

        // 验证元组构造调用保留每个元素的输入来源。
        /// <summary>
        /// 验证真实项目使用的元组值能进入后续数据流。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsTupleElementSources()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry method = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "Pair");

            MethodBehavior behavior = (await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { method },
                2)).Methods.Single();
            BehaviorCall tuple = behavior.Calls.Single(call =>
                call.Kind == BehaviorCallKind.ObjectCreation);

            StringAssert.Contains(tuple.Target.DeclaringTypeDefinitionId, "System.ValueTuple");
            AssertParameter(behavior, tuple.Arguments[0].ValueId, 0);
            AssertParameter(behavior, tuple.Arguments[1].ValueId, 1);
            Assert.IsNotNull(tuple.ResultValueId);
            AssertValueFlowsFrom(
                behavior,
                behavior.Returns.Single().ValueId!.Value,
                tuple.ResultValueId.Value);
        }

        // 验证迭代器工厂与实际 MoveNext 函数的执行时机分离。
        /// <summary>
        /// 验证调用工厂只创建迭代器，业务写入只存在于生成的遍历函数。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncMarksIteratorBodyAsDeferredExecution()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceFactory = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "YieldWrite");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedFactory = catalog.GetMethods(managedType).Single(method =>
                method.Name == "YieldWrite");
            MethodEntry[] factories = { sourceFactory, managedFactory };
            BehaviorReadResult factoryResult = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                factories,
                2);
            MethodEntry[] moveNextMethods = factories.Select(factory =>
                ResolveIteratorMoveNext(
                    catalog,
                    factoryResult.MethodsById[factory.Id]))
                .ToArray();
            BehaviorReadResult bodyResult = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                moveNextMethods,
                2);

            for (int index = 0; index < factories.Length; index++)
            {
                MethodBehavior factory = factoryResult.MethodsById[factories[index].Id];
                MethodBehavior body = bodyResult.MethodsById[moveNextMethods[index].Id];
                BehaviorValue iterator = factory.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);

                Assert.IsFalse(factory.Writes.Any(write => write.Member?.Name == "m_value"));
                Assert.IsTrue(body.Writes.Any(write => write.Member?.Name == "m_value"));
                AssertValueFlowsFrom(
                    factory,
                    factory.Returns.Single().ValueId!.Value,
                    iterator.Id);
            }
        }

        // 验证匿名函数注册保留真实闭包对象中的 this、参数和局部值。
        /// <summary>
        /// 验证注册调用、闭包字段写入和生成函数字段读取可以完整对应。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncConnectsRegisteredAnonymousFunctionCaptures()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(candidate =>
                candidate.TypeName == "SourceSamples.BehaviorSample"
                && candidate.Name == "RegisterCaptured");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(candidate =>
                candidate.Name == "RegisterCaptured");
            MethodEntry[] methods = { sourceMethod, managedMethod };
            BehaviorReadResult outerResult = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                methods,
                2);
            MethodEntry[] generatedMethods = methods.Select(method =>
                ResolveManagedMethod(
                    catalog,
                    outerResult.MethodsById[method.Id].Values.Single(value =>
                        value.Kind == BehaviorValueKind.Function).Method!))
                .ToArray();
            BehaviorReadResult innerResult = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                generatedMethods,
                2);

            for (int index = 0; index < methods.Length; index++)
            {
                MethodBehavior outer = outerResult.MethodsById[methods[index].Id];
                MethodBehavior inner = innerResult.MethodsById[generatedMethods[index].Id];
                BehaviorValue function = outer.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Function);
                BehaviorCall registration = outer.Calls.Single(call =>
                    call.Target.Name == "Register");
                BehaviorValue closure = outer.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject
                    && value.Type?.DefinitionId
                        == function.Method!.DeclaringTypeDefinitionId);
                BehaviorWrite[] closureWrites = outer.Writes.Where(item =>
                        item.Member?.DeclaringTypeDefinitionId == closure.Type!.DefinitionId)
                    .ToArray();
                BehaviorValue[] closureReads = inner.Values.Where(value =>
                        value.Kind == BehaviorValueKind.FieldRead
                        && closureWrites.Any(item => SameMember(item.Member!, value.Member!)))
                    .ToArray();
                BehaviorWrite businessWrite = inner.Writes.Single(item =>
                    item.Member?.Name == "m_value");

                AssertValueFlowsFrom(
                    outer,
                    registration.Arguments.Single().ValueId,
                    function.Id);
                Assert.HasCount(3, closureWrites);
                Assert.HasCount(3, closureReads);
                Assert.IsTrue(closureReads.All(value =>
                    FlowsFrom(
                        inner,
                        businessWrite.ReceiverValueId!.Value,
                        value.Id,
                        new HashSet<int>())
                    || FlowsFrom(
                        inner,
                        businessWrite.ValueId,
                        value.Id,
                        new HashSet<int>())));
            }
        }

        // 验证输出参数赋值和条件调用的 out 局部地址不会丢失。
        /// <summary>
        /// 验证源码与 DLL 对 out 写入和条件 TryGetValue 使用相同事实。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsOutputWritesAndConditionalOutCalls()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "ConditionalTryGet",
                "SetOutput");

            foreach (MethodBehavior behavior in pairs["SetOutput"])
            {
                BehaviorWrite write = behavior.Writes.Single();
                Assert.AreEqual(BehaviorWriteKind.Indirect, write.Kind);
                int parameter = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;
                AssertValueFlowsFrom(
                    behavior,
                    write.ReceiverValueId!.Value,
                    parameter);
            }

            foreach (MethodBehavior behavior in pairs["ConditionalTryGet"])
            {
                BehaviorCall call = behavior.Calls.Single(item =>
                    item.Target.Name == "TryGetValue");
                int parameter = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;
                AssertValueFlowsFrom(
                    behavior,
                    call.ReceiverValueId!.Value,
                    parameter);
                BehaviorArgument output = call.Arguments.Single(argument =>
                    argument.RefKind == CatalogRefKind.Out);
                Assert.AreEqual(
                    BehaviorValueKind.Address,
                    behavior.Values[output.ValueId].Kind);
            }
        }

        // 验证顺序覆盖和条件分支使用各自真正能到达写入点的对象来源。
        /// <summary>
        /// 验证源码与 DLL 不会把已被覆盖的局部变量旧值带到后续字段写入。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsOnlyDefinitionsReachingEachUse()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "WriteAfterBranch",
                "WriteAfterExternalOverwrite",
                "WriteAfterNewOverwrite");

            foreach (MethodBehavior behavior in pairs["WriteAfterNewOverwrite"])
            {
                BehaviorWrite write = behavior.Writes.Single(item => item.Member?.Name == "m_value");
                BehaviorValue external = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0);
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);

                AssertReachesAtPoint(behavior, write.ReceiverValueId!.Value, created.Id);
                Assert.IsFalse(
                    ReachesAtPoint(behavior, write.ReceiverValueId.Value, external.Id),
                    behavior.MethodId);
            }

            foreach (MethodBehavior behavior in pairs["WriteAfterExternalOverwrite"])
            {
                BehaviorWrite write = behavior.Writes.Single(item => item.Member?.Name == "m_value");
                BehaviorValue external = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0);
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);

                AssertReachesAtPoint(behavior, write.ReceiverValueId!.Value, external.Id);
                Assert.IsFalse(
                    ReachesAtPoint(behavior, write.ReceiverValueId.Value, created.Id),
                    behavior.MethodId);
            }

            foreach (MethodBehavior behavior in pairs["WriteAfterBranch"])
            {
                BehaviorWrite write = behavior.Writes.Single(item => item.Member?.Name == "m_value");
                BehaviorValue external = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0);
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);

                AssertReachesAtPoint(behavior, write.ReceiverValueId!.Value, external.Id);
                AssertReachesAtPoint(behavior, write.ReceiverValueId.Value, created.Id);
            }
        }

        // 验证调用入栈后的旧值不会被同一表达式中的后续赋值改写。
        /// <summary>
        /// 验证源码与 DLL 为两个调用实参保存不同读取点和正确来源。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsOldStackValueBeforeLaterAssignment()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "ConsumeOldThenOverwrite");

            foreach (MethodBehavior behavior in pairs["ConsumeOldThenOverwrite"])
            {
                BehaviorCall call = behavior.Calls.Single(item => item.Target.Name == "ConsumePair");
                BehaviorValue external = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0);
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);
                BehaviorValue first = behavior.Values[call.Arguments[0].ValueId];

                Assert.AreEqual(BehaviorValueKind.SlotRead, first.Kind, behavior.MethodId);
                AssertReachesAtPoint(behavior, first.Id, external.Id);
                Assert.IsFalse(ReachesAtPoint(behavior, first.Id, created.Id), behavior.MethodId);
                AssertReachesAtPoint(behavior, call.Arguments[1].ValueId, created.Id);
                Assert.AreNotEqual(
                    first.Point,
                    behavior.Values[call.Arguments[1].ValueId].Point,
                    behavior.MethodId);
            }
        }

        // 验证 ref/out 调用前后的普通读取不会退化成同一个槽值。
        /// <summary>
        /// 验证源码与 DLL 保留 out 地址以及调用前后两个独立读取点。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsReadsAroundOutputCallDistinct()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "ReadAroundOutput");

            foreach (MethodBehavior behavior in pairs["ReadAroundOutput"])
            {
                BehaviorCall[] observations = behavior.Calls.Where(call =>
                        call.Target.Name == "Observe")
                    .OrderBy(call => call.Point.BlockId)
                    .ThenBy(call => call.Point.Order)
                    .ToArray();
                BehaviorCall replace = behavior.Calls.Single(call => call.Target.Name == "Replace");
                BehaviorValue before = behavior.Values[observations[0].Arguments[0].ValueId];
                BehaviorValue after = behavior.Values[observations[1].Arguments[0].ValueId];
                BehaviorValue address = behavior.Values[replace.Arguments.Single().ValueId];

                Assert.AreEqual(BehaviorValueKind.SlotRead, before.Kind, behavior.MethodId);
                Assert.AreEqual(BehaviorValueKind.SlotRead, after.Kind, behavior.MethodId);
                Assert.AreNotEqual(before.Point, after.Point, behavior.MethodId);
                Assert.AreEqual(before.InputValueIds.Single(), after.InputValueIds.Single());
                Assert.AreEqual(BehaviorValueKind.Address, address.Kind, behavior.MethodId);
                Assert.AreEqual(before.InputValueIds.Single(), address.InputValueIds.Single());
                Assert.AreEqual(CatalogRefKind.Out, replace.Arguments.Single().RefKind);
            }
        }

        // 验证匿名函数绑定后重赋会写入同一个真实闭包字段。
        /// <summary>
        /// 验证闭包字段先保存外部对象、绑定委托，再保存新对象。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsCapturedSlotAcrossLaterAssignment()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "CaptureThenOverwrite");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(method =>
                method.Name == "CaptureThenOverwrite");
            MethodEntry[] methods = { sourceMethod, managedMethod };
            BehaviorReadResult outerResult = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                methods,
                2);
            MethodEntry[] generatedMethods = methods.Select(method =>
                ResolveManagedMethod(
                    catalog,
                    outerResult.MethodsById[method.Id].Values.Single(value =>
                        value.Kind == BehaviorValueKind.Function).Method!))
                .ToArray();
            BehaviorReadResult innerResult = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                generatedMethods,
                2);

            for (int index = 0; index < methods.Length; index++)
            {
                MethodBehavior outer = outerResult.MethodsById[methods[index].Id];
                MethodBehavior inner = innerResult.MethodsById[generatedMethods[index].Id];
                BehaviorValue function = outer.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Function);
                BehaviorValue external = outer.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0);
                BehaviorValue[] createdValues = outer.Values.Where(value =>
                        value.Kind == BehaviorValueKind.NewObject
                        && value.Type?.DefinitionId
                            != function.Method!.DeclaringTypeDefinitionId)
                    .ToArray();
                BehaviorValue created = createdValues.Single(value =>
                    !outer.Calls.Any(call =>
                        call.Kind == BehaviorCallKind.ObjectCreation
                        && call.ResultValueId == value.Id
                        && call.Arguments.Any(argument => FlowsFrom(
                            outer,
                            argument.ValueId,
                            function.Id,
                            new HashSet<int>()))));
                IGrouping<string, BehaviorWrite> capturedField =
                    outer.Writes.Where(write =>
                            write.Member?.DeclaringTypeDefinitionId
                                == function.Method!.DeclaringTypeDefinitionId)
                        .GroupBy(write =>
                            $"{write.Member!.DeclaringTypeDefinitionId}|{write.Member.Name}|"
                                + write.Member.FieldTypeId,
                            StringComparer.Ordinal)
                        .Single(group => group.Count() == 2);
                BehaviorWrite[] writes = capturedField.OrderBy(write => write.Position).ToArray();
                BehaviorCall constructor = outer.Calls.Single(call =>
                    call.Kind == BehaviorCallKind.ObjectCreation
                    && call.Arguments.Any(argument =>
                        FlowsFrom(
                            outer,
                            argument.ValueId,
                            function.Id,
                            new HashSet<int>())));
                BehaviorValue capturedRead = inner.Values.Single(value =>
                    value.Kind == BehaviorValueKind.FieldRead
                    && SameMember(value.Member!, writes[0].Member!));
                BehaviorCall observation = inner.Calls.Single(call =>
                    call.Target.Name == "Observe");

                AssertReachesAtPoint(outer, writes[0].ValueId, external.Id);
                AssertReachesAtPoint(outer, writes[1].ValueId, created.Id);
                Assert.IsTrue(writes[0].Position < constructor.Position);
                Assert.IsTrue(constructor.Position < writes[1].Position);
                AssertValueFlowsFrom(
                    inner,
                    observation.Arguments.Single().ValueId,
                    capturedRead.Id);
            }
        }

        // 验证反射注册表的索引器写入保留接收者、键和结构化类型。
        /// <summary>
        /// 验证 Dictionary&lt;int, Type&gt; 赋值可供后续解析动态类型。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsTypeRegistryIndexerAssignment()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "RegisterType");

            foreach (MethodBehavior behavior in pairs["RegisterType"])
            {
                BehaviorCall setter = behavior.Calls.Single(call =>
                    call.Target.Name == "set_Item");
                AssertParameter(behavior, setter.ReceiverValueId!.Value, 0);
                AssertParameter(behavior, setter.Arguments[0].ValueId, 1);
                BehaviorValue type = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Type);
                StringAssert.Contains(type.Type!.DefinitionId, "BehaviorSample");
                AssertValueFlowsFrom(
                    behavior,
                    setter.Arguments[1].ValueId,
                    type.Id);
            }
        }

        // 验证没有显式声明的静态构造函数仍执行字段与集合初始化。
        /// <summary>
        /// 验证隐式 .cctor 在源码与 DLL 中保留创建、写入和 Add 调用。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsImplicitStaticConstructorInitializers()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceMethod = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.StaticDefaults"
                && method.Name == ".cctor");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.StaticDefaults");
            MethodEntry managedMethod = catalog.GetMethods(managedType).Single(method =>
                method.Name == ".cctor");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { sourceMethod, managedMethod },
                2);

            foreach (MethodEntry method in new[] { sourceMethod, managedMethod })
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                Assert.HasCount(2, behavior.Writes);
                Assert.HasCount(
                    2,
                    behavior.Values.Where(value =>
                        value.Kind == BehaviorValueKind.NewObject));
                BehaviorCall add = behavior.Calls.Single(call => call.Target.Name == "Add");
                BehaviorValue list = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject
                    && value.Type!.DefinitionId.Contains("List", StringComparison.Ordinal));
                AssertValueFlowsFrom(
                    behavior,
                    add.ReceiverValueId!.Value,
                    list.Id);
                int entry = behavior.Blocks.Single(block =>
                    block.Kind == BehaviorFlowBlockKind.Entry).Id;
                Assert.IsTrue(behavior.Writes.All(write =>
                    CanReachBlock(behavior, entry, write.Point.BlockId)));
            }
        }

        // 验证实例字段初始化器位于唯一入口与构造函数正文之间。
        /// <summary>
        /// 验证源码与 DLL 的构造初始化器都能从入口按执行顺序到达。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncConnectsConstructorInitializersAfterEntry()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(".ctor");

            foreach (MethodBehavior behavior in pairs[".ctor"])
            {
                BehaviorWrite initializer = behavior.Writes.Single(write =>
                    write.Member?.Name == "m_marker");
                BehaviorWrite body = behavior.Writes.Single(write =>
                    write.Member?.Name == "m_value");
                int entry = behavior.Blocks.Single(block =>
                    block.Kind == BehaviorFlowBlockKind.Entry).Id;

                Assert.IsTrue(CanReachBlock(behavior, entry, initializer.Point.BlockId));
                Assert.IsTrue(CanReachBlock(
                    behavior,
                    initializer.Point.BlockId,
                    body.Point.BlockId));
            }
        }

        // 验证字段与自动属性初始化器按源码声明顺序交错执行。
        /// <summary>
        /// 验证源码与 DLL 的初始化调用顺序都是一、二、三。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsInterleavedInitializerOrder()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry source = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.InterleavedInitializers"
                && method.Name == ".cctor");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.InterleavedInitializers");
            MethodEntry managed = catalog.GetMethods(managedType).Single(method =>
                method.Name == ".cctor");
            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { source, managed },
                2);

            foreach (MethodBehavior behavior in new[]
                     {
                         result.MethodsById[source.Id],
                         result.MethodsById[managed.Id],
                     })
            {
                string?[] arguments = behavior.Calls.Where(call => call.Target.Name == "Mark")
                    .OrderBy(call => call.Point.BlockId)
                    .ThenBy(call => call.Point.Order)
                    .Select(call => behavior.Values[call.Arguments.Single().ValueId].Reference)
                    .ToArray();
                CollectionAssert.AreEqual(new[] { "1", "2", "3" }, arguments);
                Assert.IsTrue(behavior.Blocks.All(block => block.EnclosingRegionId == 0));
            }
        }

        // 验证隐式实例构造函数仍保留真实基类构造调用。
        /// <summary>
        /// 验证源码与 DLL 的隐式派生构造函数都直接调用基类无参构造函数。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsImplicitBaseConstructorCall()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry source = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.ImplicitDerived"
                && method.Name == ".ctor");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.ImplicitDerived");
            MethodEntry managed = catalog.GetMethods(managedType).Single(method =>
                method.Name == ".ctor");
            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[] { source, managed },
                2);

            foreach (MethodBehavior behavior in new[]
                     {
                         result.MethodsById[source.Id],
                         result.MethodsById[managed.Id],
                     })
            {
                BehaviorCall call = behavior.Calls.Single();
                Assert.AreEqual(BehaviorCallKind.Direct, call.Kind);
                Assert.AreEqual(".ctor", call.Target.Name);
                StringAssert.Contains(call.Target.DeclaringTypeDefinitionId, "BaseInitialized");
                Assert.AreEqual(
                    BehaviorValueKind.CurrentInstance,
                    behavior.Values[call.ReceiverValueId!.Value].Kind);
            }
        }

        // 验证表达式体函数、属性和索引器也具备完整可达控制流图。
        /// <summary>
        /// 验证三种表达式体的源码与 DLL 事实都从唯一入口通向返回点。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncBuildsFlowForExpressionBodies()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "ExpressionMethod",
                "get_ExpressionProperty",
                "get_Item");

            foreach (MethodBehavior behavior in pairs.Values.SelectMany(items => items))
            {
                Assert.IsNotEmpty(behavior.Blocks, behavior.MethodId);
                int entry = behavior.Blocks.Single(block =>
                    block.Kind == BehaviorFlowBlockKind.Entry).Id;
                BehaviorReturn returned = behavior.Returns.Single();
                Assert.IsTrue(CanReachBlock(behavior, entry, returned.Point.BlockId));
                BehaviorValue field = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.FieldRead
                    && value.Member?.Name == "m_value");
                AssertReachesAtPoint(behavior, returned.ValueId!.Value, field.Id);
                if (behavior.MethodId.Contains("get_Item", StringComparison.Ordinal))
                {
                    BehaviorValue index = behavior.Values.Single(value =>
                        value.Kind == BehaviorValueKind.Parameter
                        && value.ParameterIndex == 0);
                    AssertReachesAtPoint(behavior, returned.ValueId.Value, index.Id);
                }
            }
        }

        // 验证数组创建、typeof 和字符串字面量保留后续动态目标解析所需的信息。
        /// <summary>
        /// 验证源码与托管函数都能读取类型和常量值来源。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsArrayTypeAndStringValues()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] names = { "CreateArray", "ReadType", "ReturnText" };
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    names.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            foreach (MethodEntry method in sourceMethods.Concat(managedMethods))
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                if (method.Name == "CreateArray")
                {
                    BehaviorValue value = behavior.Values.Single(item =>
                        item.Kind == BehaviorValueKind.NewArray);
                    StringAssert.Contains(value.Type!.DefinitionId, "Object");
                }
                else if (method.Name == "ReadType")
                {
                    BehaviorValue value = behavior.Values.Single(item =>
                        item.Kind == BehaviorValueKind.Type);
                    StringAssert.Contains(value.Type!.DefinitionId, "BehaviorSample");
                }
                else
                {
                    BehaviorValue value = behavior.Values.Single(item =>
                        item.Kind == BehaviorValueKind.Constant
                        && item.Reference == "Foo");
                    AssertValueFlowsFrom(
                        behavior,
                        behavior.Returns.Single().ValueId!.Value,
                        value.Id);
                }
            }
        }

        // 验证接口声明和平台调用不会被误当成没有行为的普通函数。
        /// <summary>
        /// 验证源码与托管文件都保留明确的无托管体边界。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncClassifiesDeclarationsAndPlatformInvocations()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry sourceDeclaration = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.IBehavior"
                && method.Name == "Apply");
            MethodEntry sourceNative = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample"
                && method.Name == "Native");
            TypeEntry managedInterface = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.IBehavior");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managedDeclaration = catalog.GetMethods(managedInterface).Single(method =>
                method.Name == "Apply");
            MethodEntry managedNative = catalog.GetMethods(managedType).Single(method =>
                method.Name == "Native");

            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                new[]
                {
                    sourceDeclaration,
                    sourceNative,
                    managedDeclaration,
                    managedNative,
                },
                2);

            foreach (MethodEntry method in new[] { sourceDeclaration, managedDeclaration })
            {
                Assert.AreEqual(MethodBodyKind.Declaration, result.MethodsById[method.Id].BodyKind);
            }

            foreach (MethodEntry method in new[] { sourceNative, managedNative })
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                Assert.AreEqual(MethodBodyKind.PlatformInvocation, behavior.BodyKind);
                Assert.AreEqual("behavior-native", behavior.NativeBoundary!.LibraryName);
                Assert.AreEqual("behavior_entry", behavior.NativeBoundary.EntryPoint);
            }
        }

        // 验证自动属性、自动事件和字段初始化不会因没有显式函数体而消失。
        /// <summary>
        /// 验证源码与托管文件都能读取编译器补出的访问器和构造行为。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsCompilerProvidedAccessorsAndInitializers()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            string[] accessorNames = { "add_Changed", "get_Value", "remove_Changed", "set_Value" };
            MethodEntry[] sourceAccessors = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && accessorNames.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            MethodEntry sourceConstructor = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.Box<T>"
                && method.Kind == CatalogMethodKind.Constructor);
            TypeEntry managedBehaviorType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            TypeEntry managedBoxType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName.StartsWith("ExternalSamples.Box", StringComparison.Ordinal));
            MethodEntry[] managedAccessors = catalog.GetMethods(managedBehaviorType).Where(method =>
                    accessorNames.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToArray();
            MethodEntry managedConstructor = catalog.GetMethods(managedBoxType).Single(method =>
                method.Kind == CatalogMethodKind.Constructor);

            MethodEntry[] methods = sourceAccessors
                .Append(sourceConstructor)
                .Concat(managedAccessors)
                .Append(managedConstructor)
                .ToArray();
            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                methods,
                2);

            foreach (MethodEntry method in methods)
            {
                MethodBehavior behavior = result.MethodsById[method.Id];
                Assert.IsTrue(
                    behavior.Writes.Count + behavior.Calls.Count + behavior.Returns.Count > 0,
                    method.Id);
            }

            Assert.HasCount(1, result.MethodsById[sourceConstructor.Id].Writes);
            Assert.HasCount(1, result.MethodsById[managedConstructor.Id].Writes);
        }

        // 验证空值赋值和解构中的每个真实写入都不会被遍历器静默跳过。
        /// <summary>
        /// 验证源码与托管函数都能读取空值赋值和解构写入。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsCoalescingAndDeconstructionWrites()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "DeconstructWrite",
                "EnsureChild");

            foreach (MethodBehavior behavior in pairs["EnsureChild"])
            {
                Assert.HasCount(1, behavior.Writes, behavior.MethodId);
                BehaviorWrite write = behavior.Writes.Single();
                Assert.AreEqual("m_child", write.Member!.Name);
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);
                AssertValueFlowsFrom(behavior, write.ValueId, created.Id);
            }

            foreach (MethodBehavior behavior in pairs["DeconstructWrite"])
            {
                Assert.HasCount(2, behavior.Writes);
                foreach (BehaviorWrite write in behavior.Writes)
                {
                    Assert.IsNotNull(
                        write.ReceiverValueId,
                        $"{behavior.MethodId}|{write.Member?.Name}");
                }

                CollectionAssert.AreEqual(
                    new[] { 0, 1 },
                    behavior.Writes.SelectMany(write => behavior.Values
                            .Where(value => value.Kind == BehaviorValueKind.Parameter)
                            .Where(value => FlowsFrom(
                                behavior,
                                write.ReceiverValueId!.Value,
                                value.Id,
                                new HashSet<int>()))
                            .Select(value => value.ParameterIndex!.Value))
                        .Order()
                        .ToArray());
            }
        }

        // 验证初始化器中的写入和调用属于刚创建的对象而不是外层对象。
        /// <summary>
        /// 验证源码与托管函数都能保留对象、集合和数组初始化器的对象来源。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncConnectsInitializersToCreatedValues()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "CreateCollection",
                "CreateInitialized",
                "CreateInitializedArray");

            foreach (MethodBehavior behavior in pairs["CreateInitialized"])
            {
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);
                AssertValueFlowsFrom(
                    behavior,
                    behavior.Writes.Single().ReceiverValueId!.Value,
                    created.Id);
            }

            foreach (MethodBehavior behavior in pairs["CreateCollection"])
            {
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject);
                BehaviorCall add = behavior.Calls.Single(call => call.Target.Name == "Add");
                AssertValueFlowsFrom(behavior, add.ReceiverValueId!.Value, created.Id);
            }

            foreach (MethodBehavior behavior in pairs["CreateInitializedArray"])
            {
                BehaviorValue created = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewArray);
                BehaviorWrite write = behavior.Writes.Single();
                Assert.AreEqual(BehaviorWriteKind.ArrayElement, write.Kind);
                AssertValueFlowsFrom(behavior, write.ReceiverValueId!.Value, created.Id);
            }
        }

        // 验证条件访问、属性读取和用户运算符都保留准确调用关系。
        /// <summary>
        /// 验证不会因为调用隐藏在表达式中而漏掉目标或接收对象。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsExpressionCallsAndReceivers()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "Add",
                "ConditionalCall",
                "ConditionalRead",
                "IsBehavior",
                "MatchBehavior",
                "ReadPropertyInCondition");

            foreach (MethodBehavior behavior in pairs["ConditionalCall"])
            {
                BehaviorCall call = behavior.Calls.Single(call => call.Target.Name == "Apply");
                int parameterValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;
                AssertValueFlowsFrom(behavior, call.ReceiverValueId!.Value, parameterValueId);
            }

            foreach (MethodBehavior behavior in pairs["ConditionalRead"])
            {
                BehaviorCall call = behavior.Calls.Single(call => call.Target.Name == "Read");
                int parameterValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;
                AssertValueFlowsFrom(behavior, call.ReceiverValueId!.Value, parameterValueId);
            }

            foreach (MethodBehavior behavior in pairs["ReadPropertyInCondition"])
            {
                BehaviorCall call = behavior.Calls.Single(call => call.Target.Name == "get_Value");
                int parameterValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;
                AssertValueFlowsFrom(behavior, call.ReceiverValueId!.Value, parameterValueId);
            }

            foreach (MethodBehavior behavior in pairs["Add"])
            {
                BehaviorCall call = behavior.Calls.Single(call => call.Target.Name == "op_Addition");
                Assert.AreEqual(BehaviorCallKind.Direct, call.Kind);
            }

            foreach (MethodBehavior behavior in pairs["IsBehavior"])
            {
                int parameterValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;

                Assert.IsTrue(FlowsFrom(
                    behavior,
                    behavior.Returns.Single().ValueId!.Value,
                    parameterValueId,
                    new HashSet<int>()));
            }
            foreach (MethodBehavior behavior in pairs["MatchBehavior"])
            {
                BehaviorCall[] calls = behavior.Calls.Where(call =>
                        call.Target.Name == "WriteField")
                    .ToArray();
                Assert.AreEqual(
                    1,
                    calls.Length,
                    $"{behavior.MethodId}: {string.Join(", ", behavior.Calls.Select(call => call.Target.Name))}");
                BehaviorCall call = calls.Single();
                BehaviorValue[] parameters = behavior.Values.Where(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).ToArray();
                Assert.AreEqual(
                    1,
                    parameters.Length,
                    $"{behavior.MethodId}: {string.Join(", ", behavior.Values.Select(value =>
                        $"{value.Id}:{value.Kind}:{value.ParameterIndex}:{value.Reference}"))}");
                int parameterValueId = parameters.Single().Id;

                Assert.IsTrue(FlowsFrom(
                    behavior,
                    call.ReceiverValueId!.Value,
                    parameterValueId,
                    new HashSet<int>()));
            }
        }

        // 验证 base 调用即使目标函数可重写也静态绑定到基类实现。
        /// <summary>
        /// 验证源码与托管读取都把 base.ToString 记为直接调用。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsBaseCallAsDirect()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync("ToString");

            foreach (MethodBehavior behavior in pairs["ToString"])
            {
                BehaviorCall call = behavior.Calls.Single(call => call.Target.Name == "ToString");
                Assert.AreEqual(BehaviorCallKind.Direct, call.Kind);
            }
        }

        // 验证编译器隐含的资源释放和锁调用会进入函数行为。
        /// <summary>
        /// 验证 using 与 lock 在源码和托管函数中产生相同的必要调用。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsCompilerInsertedCalls()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "DisposeBoxed",
                "UseLock",
                "UseResource",
                "UseSequence");

            foreach (MethodBehavior behavior in pairs["UseResource"])
            {
                Assert.IsTrue(behavior.Calls.Any(call => call.Target.Name == "Dispose"));
            }

            foreach (MethodBehavior behavior in pairs["UseLock"])
            {
                string[] names = behavior.Calls.Select(call => call.Target.Name).ToArray();
                CollectionAssert.Contains(names, "Enter");
                CollectionAssert.Contains(names, "Exit");
            }


            foreach (MethodBehavior behavior in pairs["UseSequence"])
            {
                string[] names = behavior.Calls.Select(call => call.Target.Name).ToArray();
                CollectionAssert.Contains(names, "GetEnumerator");
                CollectionAssert.Contains(names, "MoveNext");
                CollectionAssert.Contains(names, "get_Current");
                CollectionAssert.Contains(names, "Dispose");
                BehaviorCall dispose = behavior.Calls.Single(call => call.Target.Name == "Dispose");
                Assert.IsNotNull(dispose.ConstrainedReceiverType, behavior.MethodId);
                StringAssert.Contains(
                    dispose.ConstrainedReceiverType!.DefinitionId,
                    "EffectSequence+Enumerator");
            }


            foreach (MethodBehavior behavior in pairs["DisposeBoxed"])
            {
                BehaviorCall dispose = behavior.Calls.Single(call => call.Target.Name == "Dispose");
                Assert.IsNull(dispose.ConstrainedReceiverType);
            }
        }

        // 验证异常图不会在进入内部 catch 时提前执行外层 finally。
        /// <summary>
        /// 验证源码与 DLL 保留 catch 进入边和外层 finally 的分离关系。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsCatchBeforeOuterFinally()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "CatchInsideFinally");

            foreach (MethodBehavior behavior in pairs["CatchInsideFinally"])
            {
                BehaviorFlowEdge thrown = behavior.Blocks.SelectMany(block => block.Successors)
                    .Single(edge => edge.Semantics == BehaviorFlowBranchSemantics.Throw);
                Assert.IsEmpty(thrown.FinallyRegionIds, behavior.MethodId);
                BehaviorCall caught = behavior.Calls.Single(call => call.Target.Name == "Apply");
                BehaviorCall final = behavior.Calls.Single(call => call.Target.Name == "WriteStatic");
                BehaviorFlowRegion catchRegion = behavior.Regions.Single(region =>
                    region.Kind == BehaviorFlowRegionKind.Catch
                    && ContainsBlock(region, caught.Point.BlockId));
                BehaviorFlowRegion finallyRegion = behavior.Regions.Single(region =>
                    region.Kind == BehaviorFlowRegionKind.Finally
                    && ContainsBlock(region, final.Point.BlockId));
                Assert.AreEqual(catchRegion.Id, behavior.Blocks.Single(block =>
                    block.Id == caught.Point.BlockId).EnclosingRegionId);
                Assert.IsTrue(behavior.Blocks.Where(block =>
                        ContainsBlock(catchRegion, block.Id))
                    .SelectMany(block => block.Successors)
                    .Any(edge => edge.FinallyRegionIds.Contains(finallyRegion.Id)
                        || edge.TargetBlockId is int continuation
                        && behavior.Blocks.Where(block =>
                                CanReachBlock(behavior, continuation, block.Id))
                            .SelectMany(block => block.Successors)
                            .Any(next => next.FinallyRegionIds.Contains(finallyRegion.Id))),
                    System.Text.Json.JsonSerializer.Serialize(behavior));
            }
        }

        // 验证正常离开嵌套 try 时按由内到外记录两个 finally。
        /// <summary>
        /// 验证源码与 DLL 的离开边携带完整且有序的 finally 区域。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsNestedFinallyOrder()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "LeaveNestedFinally");

            foreach (MethodBehavior behavior in pairs["LeaveNestedFinally"])
            {
                BehaviorCall[] writes = behavior.Calls.Where(call =>
                        call.Target.Name == "WriteStatic")
                    .OrderBy(call => behavior.Values[call.Arguments.Single().ValueId].Reference)
                    .ToArray();
                BehaviorFlowRegion inner = behavior.Regions.Single(region =>
                    region.Kind == BehaviorFlowRegionKind.Finally
                    && ContainsBlock(region, writes[0].Point.BlockId));
                BehaviorFlowRegion outer = behavior.Regions.Single(region =>
                    region.Kind == BehaviorFlowRegionKind.Finally
                    && ContainsBlock(region, writes[1].Point.BlockId));
                (BehaviorFlowBlock Block, BehaviorFlowEdge Edge) innerExit = behavior.Blocks
                    .SelectMany(block => block.Successors.Select(edge => (block, edge)))
                    .Single(item => item.edge.FinallyRegionIds.Contains(inner.Id));
                int outerIndex = Array.IndexOf(
                    innerExit.Edge.FinallyRegionIds.ToArray(),
                    outer.Id);
                if (outerIndex >= 0)
                {
                    Assert.IsTrue(
                        Array.IndexOf(
                            innerExit.Edge.FinallyRegionIds.ToArray(),
                            inner.Id) < outerIndex);
                }
                else
                {
                    int continuation = innerExit.Edge.TargetBlockId
                        ?? throw new InvalidOperationException("内部 finally 缺少继续执行块。");
                    Assert.IsTrue(behavior.Blocks.Where(block =>
                            CanReachBlock(behavior, continuation, block.Id))
                        .SelectMany(block => block.Successors)
                        .Any(edge => edge.FinallyRegionIds.Contains(outer.Id)));
                }
            }
        }

        // 验证 using 声明与 using 语句都记录编译器保证执行的释放调用。
        /// <summary>
        /// 验证 using 声明在源码与托管行为中都保留 Dispose 调用。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsUsingDeclaration()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "UseResourceDeclaration");

            foreach (MethodBehavior behavior in pairs["UseResourceDeclaration"])
            {
                Assert.IsTrue(behavior.Calls.Any(call => call.Target.Name == "Dispose"));
            }
        }

        // 验证数组 foreach 使用元素来源而不伪造枚举器函数调用。
        /// <summary>
        /// 验证数组 foreach 的源码事实与真实托管指令一致。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsArrayForEachWithoutEnumeratorCalls()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync("UseArray");

            foreach (MethodBehavior behavior in pairs["UseArray"])
            {
                Assert.IsFalse(
                    behavior.Calls.Any(call => call.Target.Name is
                        "GetEnumerator" or "MoveNext" or "get_Current" or "Dispose"),
                    $"{behavior.MethodId}: {string.Join(", ", behavior.Calls.Select(call =>
                        call.Target.Name))}");
                Assert.IsTrue(behavior.Values.Any(value =>
                    value.Kind == BehaviorValueKind.ArrayElementRead));
            }
        }

        // 验证递归模式保留输入来源与属性读取的真实 getter 调用。
        /// <summary>
        /// 验证递归模式变量和属性子模式不会丢失行为事实。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncReadsRecursivePattern()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "MatchRecursive");

            foreach (MethodBehavior behavior in pairs["MatchRecursive"])
            {
                BehaviorCall write = behavior.Calls.Single(call => call.Target.Name == "WriteField");
                int parameterValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;

                Assert.IsTrue(behavior.Calls.Any(call => call.Target.Name == "get_Value"));
                AssertValueFlowsFrom(
                    behavior,
                    write.ReceiverValueId!.Value,
                    parameterValueId);
            }
        }

        // 验证条件返回与抛出分支不会把返回对象来源丢掉。
        /// <summary>
        /// 验证返回值仍来自源码参数，抛出分支不产生伪返回值。
        /// </summary>
        [TestMethod]
        public async Task ReadAsyncKeepsReturnSourceAcrossThrowBranch()
        {
            IReadOnlyDictionary<string, MethodBehavior[]> pairs = await ReadPairsAsync(
                "ReturnOrThrow");

            foreach (MethodBehavior behavior in pairs["ReturnOrThrow"])
            {
                int parameterValueId = behavior.Values.Single(value =>
                    value.Kind == BehaviorValueKind.Parameter
                    && value.ParameterIndex == 0).Id;
                BehaviorReturn returned = behavior.Returns.Single(item => item.ValueId != null);

                AssertValueFlowsFrom(behavior, returned.ValueId!.Value, parameterValueId);
            }
        }

        // 验证不同工作数只改变速度，不改变函数事实或顺序。
        /// <summary>
        /// 验证 -j1、-j2、-j4 与 -j8 的完整公开行为结果一致。
        /// </summary>
        [TestMethod]
        public async Task AnalyzeAsyncKeepsBehaviorOrderAcrossJobCounts()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();

            BehaviorReadResult single = (await new SetterChecker().AnalyzeAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 1))).Behaviors;
            string[] expected = ProjectBehaviors(single).ToArray();
            foreach (int jobs in new[] { 2, 4, 8 })
            {
                BehaviorReadResult parallel = (await new SetterChecker().AnalyzeAsync(
                    new MaterialRequest(project.AssemblyDefinitionPath, jobs))).Behaviors;
                CollectionAssert.AreEqual(
                    expected,
                    ProjectBehaviors(parallel).ToArray(),
                    $"-j{jobs.ToString()} 输出与 -j1 不同。");
            }
        }

        // 一次建立测试项目并读取所需函数的源码与真实托管版本。
        private static async Task<IReadOnlyDictionary<string, MethodBehavior[]>> ReadPairsAsync(
            params string[] methodNames)
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] sourceMethods = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && methodNames.Contains(method.Name, StringComparer.Ordinal))
                .ToArray();
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] managedMethods = catalog.GetMethods(managedType).Where(method =>
                    methodNames.Contains(method.Name, StringComparer.Ordinal))
                .ToArray();
            BehaviorReadResult result = await new BehaviorReader().ReadAsync(
                material,
                catalog,
                sourceMethods.Concat(managedMethods).ToArray(),
                2);

            return methodNames.ToDictionary(
                name => name,
                name => sourceMethods.Concat(managedMethods)
                    .Where(method => method.Name == name)
                    .Select(method => result.MethodsById[method.Id])
                    .ToArray(),
                StringComparer.Ordinal);
        }

        // 用结构化函数引用在函数总表中定位真实托管定义。
        private static MethodEntry ResolveManagedMethod(
            MethodCatalogResult catalog,
            BehaviorMethodReference reference)
        {
            TypeEntry? type = catalog.Types.SingleOrDefault(candidate =>
                candidate.Id == reference.DeclaringTypeDefinitionId
                || candidate.Id == reference.DeclaringTypeId
                || candidate.LogicalId == reference.DeclaringTypeDefinitionId
                || candidate.LogicalId == reference.DeclaringTypeId
                || candidate.AliasIds.Contains(
                    reference.DeclaringTypeDefinitionId,
                    StringComparer.Ordinal)
                || candidate.AliasIds.Contains(
                    reference.DeclaringTypeId,
                    StringComparer.Ordinal));
            if (type == null)
            {
                throw new InvalidOperationException(
                    $"找不到结构化声明类型：{reference.DeclaringTypeId} / "
                        + reference.DeclaringTypeDefinitionId);
            }

            return catalog.GetMethods(type).Single(candidate =>
                candidate.Name == reference.Name
                && candidate.GenericArity == reference.GenericArity
                && candidate.ReturnTypeId == reference.ReturnTypeId
                && candidate.Parameters.Select(parameter => parameter.TypeId)
                    .SequenceEqual(reference.ParameterTypeIds, StringComparer.Ordinal));
        }

        // 从迭代器工厂实际创建的类型定位生成的遍历函数。
        private static MethodEntry ResolveIteratorMoveNext(
            MethodCatalogResult catalog,
            MethodBehavior factory)
        {
            BehaviorTypeReference stateMachine = factory.Values.Single(value =>
                    value.Kind == BehaviorValueKind.NewObject)
                .Type
                ?? throw new InvalidOperationException("迭代器创建值缺少生成类型。");
            TypeEntry type = catalog.Types.Single(candidate =>
                candidate.Id == stateMachine.DefinitionId
                || candidate.LogicalId == stateMachine.DefinitionId);

            return catalog.GetMethods(type).Single(method =>
                method.Name == "MoveNext"
                && method.Parameters.Count == 0
                && method.ReturnTypeId == "System.Boolean");
        }

        // 比较跨函数行为中的同一个结构化字段身份。
        private static bool SameMember(
            BehaviorMemberReference first,
            BehaviorMemberReference second)
        {
            return first.DeclaringTypeDefinitionId == second.DeclaringTypeDefinitionId
                && first.Name == second.Name
                && first.FieldTypeId == second.FieldTypeId;
        }

        // 核对一条字段写入事实中的当前对象和第一个参数来源。
        private static void AssertFieldWrite(MethodBehavior behavior)
        {
            Assert.AreEqual(MethodBodyKind.Executable, behavior.BodyKind);
            BehaviorWrite write = behavior.Writes.Single();
            Assert.AreEqual(BehaviorWriteKind.Field, write.Kind);
            Assert.IsNotNull(write.ReceiverValueId);
            Assert.AreEqual(
                BehaviorValueKind.CurrentInstance,
                behavior.Values[write.ReceiverValueId.Value].Kind);
            AssertParameter(behavior, write.ValueId, 0);
        }

        // 核对一个值来源是指定位置的函数参数。
        private static void AssertParameter(MethodBehavior behavior, int valueId, int parameterIndex)
        {
            BehaviorValue parameter = behavior.Values.Single(value =>
                value.Kind == BehaviorValueKind.Parameter
                && value.ParameterIndex == parameterIndex);
            AssertValueFlowsFrom(behavior, valueId, parameter.Id);
        }

        // 核对目标值本身或其局部赋值来源最终来自指定值。
        private static void AssertValueFlowsFrom(
            MethodBehavior behavior,
            int targetValueId,
            int expectedSourceValueId)
        {
            if (targetValueId == expectedSourceValueId)
            {
                return;
            }

            Assert.IsTrue(
                FlowsFrom(
                    behavior,
                    targetValueId,
                    expectedSourceValueId,
                    new HashSet<int>()),
                behavior.MethodId);
        }

        // 核对一个读取点按控制流回溯后能到达指定来源。
        private static void AssertReachesAtPoint(
            MethodBehavior behavior,
            int targetValueId,
            int expectedSourceValueId)
        {
            Assert.IsTrue(
                ReachesAtPoint(behavior, targetValueId, expectedSourceValueId),
                System.Text.Json.JsonSerializer.Serialize(behavior));
        }

        // 从一次值读取的准确位置开始寻找能到达它的赋值。
        private static bool ReachesAtPoint(
            MethodBehavior behavior,
            int targetValueId,
            int expectedSourceValueId)
        {
            return ReachesValueAtPoint(
                behavior,
                targetValueId,
                expectedSourceValueId,
                new HashSet<int>(),
                new HashSet<(int BlockId, int Order, int SlotId)>());
        }

        // 沿派生值输入或槽读取位置继续回溯来源。
        private static bool ReachesValueAtPoint(
            MethodBehavior behavior,
            int targetValueId,
            int expectedSourceValueId,
            HashSet<int> visitedValues,
            HashSet<(int BlockId, int Order, int SlotId)> visitedSlots)
        {
            if (targetValueId == expectedSourceValueId)
            {
                return true;
            }

            if (!visitedValues.Add(targetValueId))
            {
                return false;
            }

            BehaviorValue value = behavior.Values[targetValueId];
            if (value.Kind == BehaviorValueKind.SlotRead)
            {
                int slotId = value.InputValueIds.Single();
                BehaviorFlowPoint point = value.Point
                    ?? throw new InvalidOperationException("槽读取缺少执行位置。");
                return ReachesSlotAtPoint(
                    behavior,
                    slotId,
                    point,
                    expectedSourceValueId,
                    visitedValues,
                    visitedSlots);
            }

            return value.InputValueIds.Any(inputValueId => ReachesValueAtPoint(
                behavior,
                inputValueId,
                expectedSourceValueId,
                visitedValues,
                visitedSlots));
        }

        // 在一个块内取最近赋值，没有时沿所有可达前驱继续回溯。
        private static bool ReachesSlotAtPoint(
            MethodBehavior behavior,
            int slotId,
            BehaviorFlowPoint point,
            int expectedSourceValueId,
            HashSet<int> visitedValues,
            HashSet<(int BlockId, int Order, int SlotId)> visitedSlots)
        {
            if (!visitedSlots.Add((point.BlockId, point.Order, slotId)))
            {
                return false;
            }

            BehaviorAssignment? nearest = behavior.Assignments
                .Where(assignment => assignment.TargetValueId == slotId
                    && assignment.Point.BlockId == point.BlockId
                    && assignment.Point.Order < point.Order)
                .MaxBy(assignment => assignment.Point.Order);
            if (nearest != null)
            {
                return ReachesValueAtPoint(
                    behavior,
                    nearest.ValueId,
                    expectedSourceValueId,
                    visitedValues,
                    visitedSlots);
            }

            int[] predecessors = behavior.Blocks
                .Where(block => block.IsReachable
                    && block.Successors.Any(edge => edge.TargetBlockId == point.BlockId))
                .Select(block => block.Id)
                .ToArray();
            if (predecessors.Length == 0)
            {
                return slotId == expectedSourceValueId;
            }

            return predecessors.Any(predecessor => ReachesSlotAtPoint(
                behavior,
                slotId,
                new BehaviorFlowPoint(predecessor, int.MaxValue),
                expectedSourceValueId,
                visitedValues,
                visitedSlots));
        }

        // 判断控制流图中是否存在从一个块到另一个块的路径。
        private static bool CanReachBlock(
            MethodBehavior behavior,
            int startBlockId,
            int targetBlockId)
        {
            Queue<int> pending = new();
            HashSet<int> visited = new();
            pending.Enqueue(startBlockId);
            while (pending.TryDequeue(out int blockId))
            {
                if (blockId == targetBlockId)
                {
                    return true;
                }

                if (!visited.Add(blockId))
                {
                    continue;
                }

                BehaviorFlowBlock block = behavior.Blocks.Single(item => item.Id == blockId);
                foreach (int successor in block.Successors
                             .Where(edge => edge.TargetBlockId != null)
                             .Select(edge => edge.TargetBlockId!.Value))
                {
                    pending.Enqueue(successor);
                }
            }

            return false;
        }

        // 判断一个基本块是否位于指定的连续控制流区域内。
        private static bool ContainsBlock(BehaviorFlowRegion region, int blockId)
        {
            return blockId >= region.FirstBlockId && blockId <= region.LastBlockId;
        }

        // 沿局部赋值继续查找测试需要的值来源。
        private static bool FlowsFrom(
            MethodBehavior behavior,
            int targetValueId,
            int expectedSourceValueId,
            HashSet<int> visited)
        {
            if (targetValueId == expectedSourceValueId)
            {
                return true;
            }

            return visited.Add(targetValueId)
                && (behavior.Values[targetValueId].InputValueIds.Any(inputValueId =>
                        FlowsFrom(
                            behavior,
                            inputValueId,
                            expectedSourceValueId,
                            visited))
                    || behavior.Assignments.Where(item => item.TargetValueId == targetValueId)
                        .Any(assignment => FlowsFrom(
                            behavior,
                            assignment.ValueId,
                            expectedSourceValueId,
                        visited)));
        }

        // 生成不包含计时的完整行为文本以核对并行结果。
        private static IEnumerable<string> ProjectBehaviors(BehaviorReadResult result)
        {
            return result.Methods.Select(behavior =>
                System.Text.Json.JsonSerializer.Serialize(behavior));
        }
    }
}
