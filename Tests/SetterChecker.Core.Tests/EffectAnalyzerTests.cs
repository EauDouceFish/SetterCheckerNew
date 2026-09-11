namespace SetterChecker.Core.Tests
{
    /// <summary>通过真实源码和 DLL 验证修改对象的归属。</summary>
    [TestClass]
    public sealed class EffectAnalyzerTests
    {
        // 泛型辅助函数取得成员后，写回的引用仍属于实际传入的对象。
        /// <summary>反射与普通读取共用实际构造类型和对象来源，不能把新外壳里的旧对象误判为新对象。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeGenericHelperReflectionPreservesStoredObject(bool existing)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public sealed class Box<T> { public T Item; public void Put(T value) { Item=value; } }
                public static class Calls
                {
                    public static void Entry(Data outside)
                    {
                        var box = new Box<Data>();
                        Fill(box, INPUT);
                        box.Item.Value = 1;
                    }
                    private static void Fill<T>(Box<T> box, T value)
                    {
                        typeof(Box<T>).GetMethod("Put").Invoke(box, new object[] { value });
                    }
                }
                """.Replace("INPUT", existing ? "outside" : "new Data()"));
            System.Reflection.Assembly runtime = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath));
            object outside = Activator.CreateInstance(runtime.GetType("ExternalSamples.Data")!)!;
            runtime.GetType("ExternalSamples.Calls")!.GetMethod("Entry")!.Invoke(null, new[] { outside });
            Assert.AreEqual(existing ? 1 : 0, outside.GetType().GetField("Value")!.GetValue(outside));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect result in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(existing ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Kind);
            }
        }

        // 反射候选返回值和选择该成员的分支必须属于同一次执行。
        /// <summary>不同类型与同类不同成员的选择都不能拼出不存在的写入路径。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        [DataRow(false, false, true)]
        [DataRow(false, true, true)]
        [DataRow(true, false, true)]
        [DataRow(true, true, true)]
        public async Task AnalyzeKeepsReflectedMemberReturnSelection(bool property, bool writes, bool sameType = false)
        {
            string source = """
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class First { public static object Read() => 1; public static object Number => 1; public static object Other() => 2; public static object Another => 2; }
                public static class Second { public static object Read() => 2; public static object Number => 2; }
                public static class Calls
                {
                    public static void Entry(bool flag, Data outside)
                    {
                        var selected = flag ? typeof(First) : typeof(Second);
                        int value = (int)OPERATION;
                        if (flag && value == EXPECTED) outside.Value=1;
                    }
                }
                """.Replace("OPERATION", property ? "selected.GetProperty(\"Number\").GetValue(null)" : "selected.GetMethod(\"Read\").Invoke(null, null)")
                .Replace("EXPECTED", writes ? "1" : "2");
            if (sameType)
            {
                source = source.Replace("flag ? typeof(First) : typeof(Second)", "typeof(First)")
                    .Replace("GetProperty(\"Number\")", "GetProperty(flag ? \"Number\" : \"Another\")")
                    .Replace("GetMethod(\"Read\")", "GetMethod(flag ? \"Read\" : \"Other\")");
            }
            using TestProject project = TestProject.CreateWithCallTargets(source);
            System.Reflection.Assembly runtime = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath));
            foreach (bool flag in new[] { false, true })
            {
                object outside = Activator.CreateInstance(runtime.GetType("ExternalSamples.Data")!)!;
                runtime.GetType("ExternalSamples.Calls")!.GetMethod("Entry")!.Invoke(null, new[] { (object)flag, outside });
                Assert.AreEqual(writes && flag ? 1 : 0, outside.GetType().GetField("Value")!.GetValue(outside));
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect result in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(writes ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Kind);
            }
        }

        // 数组类型查成员不能误用其元素类型上同名的函数或属性。
        /// <summary>不支持的类型形状明确失败，不能把实际会抛异常的调用证明成写入。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeRejectsElementMembersOfReflectedArray(bool property)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Box<T> { public T Value; public T Item { get=>Value; set=>Value=value; } public void Put(T value) { Value=value; } }
                public static class Calls
                {
                    public static void Entry(Box<int> outside) { OPERATION; }
                }
                """.Replace("OPERATION", property ? "typeof(Box<int>[]).GetProperty(\"Item\").SetValue(outside, 7)"
                    : "typeof(Box<int>[]).GetMethod(\"Put\").Invoke(outside, new object[] { 7 })"));
            System.Reflection.Assembly runtime = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath));
            object outside = Activator.CreateInstance(runtime.GetType("ExternalSamples.Box`1")!.MakeGenericType(typeof(int)))!;
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => runtime.GetType("ExternalSamples.Calls")!.GetMethod("Entry")!.Invoke(null, new[] { outside }));
            Assert.IsInstanceOfType<NullReferenceException>(failure.InnerException);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
        }

        // 构造类型反射保留实际类型参数，属性访问与普通函数仍读取同一份目标行为。
        /// <summary>源码和DLL中的泛型类及继承成员按真实接收对象判断写入。</summary>
        [TestMethod]
        [DataRow("method", false, false)]
        [DataRow("method", true, false)]
        [DataRow("set", false, false)]
        [DataRow("set", true, false)]
        [DataRow("get", false, false)]
        [DataRow("get", true, false)]
        [DataRow("set", false, true)]
        [DataRow("set", true, true)]
        public async Task AnalyzeConstructedGenericReflectionMembers(string operation, bool external, bool inherited)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Box<T>
                {
                    public T Value;
                    public T Item { get => Value; set => Value=value; }
                    public void Put(T value) { Value=value; }
                }
                public sealed class Derived : Box<int> { }
                public static class Calls
                {
                    public static void Entry(OWNER outside)
                    {
                        var receiver = RECEIVER;
                        OPERATION;
                    }
                }
                """.Replace("OWNER", inherited ? "Derived" : "Box<int>")
                .Replace("RECEIVER", external ? "outside" : inherited ? "new Derived()" : "new Box<int>()")
                .Replace("OPERATION", "typeof(" + (inherited ? "Derived" : "Box<int>") + ")" + (operation == "method"
                    ? ".GetMethod(\"Put\").Invoke(receiver, new object[] { 7 })" : operation == "set"
                    ? ".GetProperty(\"Item\").SetValue(receiver, 7)" : ".GetProperty(\"Item\").GetValue(receiver)")));
            System.Reflection.Assembly runtime = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath));
            Type owner = inherited ? runtime.GetType("ExternalSamples.Derived")!
                : runtime.GetType("ExternalSamples.Box`1")!.MakeGenericType(typeof(int));
            object outside = Activator.CreateInstance(owner)!;
            runtime.GetType("ExternalSamples.Calls")!.GetMethod("Entry")!.Invoke(null, new[] { outside });
            Assert.AreEqual(external && operation != "get" ? 7 : 0, owner.GetField("Value")!.GetValue(outside));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect result in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(external && operation != "get" ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Kind);
            }
            foreach (ResolvedCall call in calls.Calls.Where(call => call.Call.Target.Name == (operation == "method" ? "Invoke" : operation == "set" ? "SetValue" : "GetValue")))
            {
                Assert.AreEqual("System.Int32", call.Targets.Single().DeclaringTypeArguments.Single());
            }
        }

        // 类型分支选中的构造函数必须和同一路径绑定，泛型创建也保留真实构造参数。
        /// <summary>不同创建候选不能混用构造写入和对象身份。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(false, true, true)]
        public async Task AnalyzeKeepsReflectionFactoryTypeSelection(bool guard, bool generic, bool genericApi = false)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class State { public static int Value; }
                public sealed class Quiet { }
                public sealed class Writer { public Writer() { State.Value++; } }
                public sealed class Box<T> { public T Value; }
                public static class Calls
                {
                    public static void Entry(bool flag)
                    {
                        System.Type type = flag ? typeof(Quiet) : typeof(Writer);
                        ACTION
                    }
                }
                """.Replace("ACTION", generic
                    ? "var box = " + (genericApi ? "System.Activator.CreateInstance<Box<int>>()" : "(Box<int>)System.Activator.CreateInstance(typeof(Box<int>))") + "; box.Value = 1;"
                    : (guard ? "if (flag) " : string.Empty) + "_ = System.Activator.CreateInstance(type);"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind
                == (guard || generic ? MethodEffectKind.Getter : MethodEffectKind.Setter)));
        }

        // 在同一创建表达式中分别写入旧对象和新对象，读回成员仍跟随实际选择。
        /// <summary>构造函数写入的成员内容不能跨创建分支混合。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeKeepsReflectionFactoryMemberSelection(bool existing, bool delegateCall = false)
        {
            string source = """
                namespace Samples;
                public sealed class Data { public int Value; }
                public class Box { public Data Child; }
                public static class State { public static Data Existing; }
                public sealed class A : Box { public A() { Child = State.Existing; } public static void Fill(Box box) { box.Child = State.Existing; } }
                public sealed class B : Box { public B() { Child = new Data(); } public static void Fill(Box box) { box.Child = new Data(); } }
                public static class Calls
                {
                    public static void Entry(bool flag)
                    {
                        var box = (Box)System.Activator.CreateInstance(flag ? typeof(A) : typeof(B));
                        if (CONDITION) box.Child.Value = 1;
                    }
                }
                """.Replace("CONDITION", existing ? "flag" : "!flag");
            if (delegateCall)
            {
                source = source.Replace("var box = (Box)System.Activator.CreateInstance(flag ? typeof(A) : typeof(B));",
                    "var box = new Box(); System.Action<Box> fill = flag ? new System.Action<Box>(A.Fill) : new System.Action<Box>(B.Fill); fill(box);");
            }
            using TestProject project = TestProject.CreateWithCallTargets(source);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(existing ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 未闭合的递归创建进入待处理清单，重复解析不得反复发布同一个运行时值。
        /// <summary>部分报告模式保留构造递归证据而不是崩溃或无限重试。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeRetainsRecursiveReflectionFactoryFailure(bool generic, bool genericApi = false)
        {
            string source = """
                namespace Samples;
                public sealed class Data { public Data() { _ = System.Activator.CreateInstance(typeof(Data)); } }
                public static class Calls { public static void Entry() { _ = System.Activator.CreateInstance(typeof(Data)); } }
                """;
            if (generic)
            {
                source = source.Replace("class Data", "class Data<T>")
                    .Replace("typeof(Data)", "typeof(Data<int>)")
                    .Replace("Entry() { _ = System.Activator.CreateInstance(typeof(Data<int>))", "Entry() { _ = System.Activator.CreateInstance(typeof(Data<string>))");
            }
            if (genericApi)
            {
                source = source.Replace("System.Activator.CreateInstance(typeof(", "System.Activator.CreateInstance<").Replace("));", ">();");
            }
            using TestProject project = TestProject.CreateWithCallTargets(source);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2,
                cancellation.Token, requireCompleteCalls: false);
            Assert.IsNotEmpty(calls.PendingCalls);
            Assert.IsTrue(calls.PendingCalls.Any(call => call.Failure?.Contains("递归", StringComparison.Ordinal) == true));
        }

        // 数组类型的元素声明不能冒充实际请求创建的类型。
        /// <summary>没有无参构造函数的数组不允许继续证明后续写入。</summary>
        [TestMethod]
        [DataRow("Data[]")]
        [DataRow("Data[,]")]
        [DataRow("Data[]", true)]
        [DataRow("Data[,]", true)]
        public async Task AnalyzeRejectsReflectionFactoryArrayShape(string type, bool genericApi = false)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { _ = CREATE; state = 1; }
                }
                """.Replace("CREATE", genericApi ? $"System.Activator.CreateInstance<{type}>()" : $"System.Activator.CreateInstance(typeof({type}))"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
            StringAssert.Contains(exception.Message, "反射创建");
        }

        // 复刻脚本工厂按 Type 创建对象的入口，构造函数和新对象去向都必须实际分析。
        /// <summary>反射创建不会直接获得固定 Getter 或 Setter 结论。</summary>
        [TestMethod]
        [DataRow("return System.Activator.CreateInstance(typeof(Data));", "Value = 1;", true)]
        [DataRow("_ = System.Activator.CreateInstance(typeof(Data)); return null;", "Value = 1;", false)]
        [DataRow("_ = System.Activator.CreateInstance(typeof(Data)); return null;", "State.Value++;", true)]
        [DataRow("var data = (Data)Create<Data>(); data.Value = 2; return null;", "Value = 1;", false)]
        [DataRow("return System.Activator.CreateInstance<Data>();", "Value = 1;", true)]
        [DataRow("_ = System.Activator.CreateInstance<Data>(); return null;", "Value = 1;", false)]
        [DataRow("_ = System.Activator.CreateInstance<Data>(); return null;", "State.Value++;", true)]
        [DataRow("var data = CreateNew<Data>(); data.Value = 2; return null;", "Value = 1;", false)]
        public async Task AnalyzeFollowsReflectionFactoryConstructor(string action, string constructor, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class State { public static int Value; }
                public sealed class Data { public int Value; public Data() { CONSTRUCTOR } }
                public static class Calls
                {
                    private static object Create<T>() => System.Activator.CreateInstance(typeof(T));
                    private static T CreateNew<T>() where T : new() => new T();
                    public static object Entry() { ACTION }
                }
                """.Replace("CONSTRUCTOR", constructor).Replace("ACTION", action));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 同一字段经过多次条件写入后，只按真正到达读取位置的值检查外部写入。
        /// <summary>长存储倒查保持分支关系，同时记录分析阶段的时间与分配量。</summary>
        [TestMethod]
        [DataRow(16)]
        [DataRow(64)]
        [DataRow(16, true)]
        [DataRow(64, true)]
        public async Task AnalyzeScalesRepeatedConditionalFieldWrites(int count, bool privateSlot = false)
        {
            string writes = string.Join(Environment.NewLine, Enumerable.Range(1, count).Select(index => $"if (flag) local.Value = {index};"));
            string source = """
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data outside, bool flag)
                    {
                        var local = new Data();
                        WRITES
                        if (local.Value == 0 && flag) outside.Value = 1;
                    }
                }
                """.Replace("WRITES", writes);
            if (privateSlot)
            {
                source = """
                    namespace Samples;
                    public sealed class Data { public int Value; }
                    public static class Calls
                    {
                        private static int Identity(int value) => value;
                        public static void Entry(Data outside, int value)
                        {
                            WRITES
                            if (value > 0 && value < 0) outside.Value = 1;
                        }
                    }
                    """.Replace("WRITES", string.Join(Environment.NewLine, Enumerable.Repeat("value = Identity(value);", count)));
            }
            using TestProject project = TestProject.CreateWithCallTargets(source);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            long allocated = GC.GetTotalAllocatedBytes(true);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Console.WriteLine($"conditional_writes={count}; private_slot={privateSlot}; elapsed_ms={watch.Elapsed.TotalMilliseconds:F3}; allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocated}");
            Assert.IsTrue(effects.Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 多层调用逐层改写同一字段，最终条件仍须保留每个实际参数和出口。
        /// <summary>跨函数存储倒查的规模对照同时包含可执行和互斥的外部修改。</summary>
        [TestMethod]
        [DataRow(2, false)]
        [DataRow(6, false)]
        [DataRow(16, false)]
        [DataRow(2, true)]
        [DataRow(6, true)]
        [DataRow(16, true)]
        public async Task AnalyzeScalesNestedConditionalStorage(int depth, bool setter)
        {
            string helpers = string.Join(Environment.NewLine, Enumerable.Range(0, depth).Select(index =>
                $"private static void Step{index}(Data local, bool flag) {{ "
                + (index == 0 ? "if (flag) local.Value = 1; else local.Value = 2;"
                    : $"Step{index - 1}(local, flag); if (local.Value == 1) local.Value = 1; else local.Value = 2;") + " }"));
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    HELPERS
                    public static void Entry(Data outside, bool flag)
                    {
                        var local = new Data();
                        STEP(local, flag);
                        if (local.Value == EXPECTED && flag) outside.Value = 1;
                    }
                }
                """.Replace("HELPERS", helpers).Replace("STEP", $"Step{depth - 1}").Replace("EXPECTED", setter ? "1" : "2"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            long allocated = GC.GetTotalAllocatedBytes(true);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Console.WriteLine($"nested_storage_depth={depth}; setter={setter}; elapsed_ms={watch.Elapsed.TotalMilliseconds:F3}; allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocated}");
            Assert.IsTrue(effects.Methods.All(method => method.Kind == (setter ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 调用候选混合字段写入与只读实现，只读出口仍保留实际选择和调用前的值。
        /// <summary>不改存储的正证不能抹掉其他候选的写入或返回条件。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeKeepsMixedTargetStorageReturns(bool setter, bool guardedReturn)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public interface IWorker { int Execute(Data local, bool other); }
                public sealed class Writes : IWorker
                {
                    public int Execute(Data local, bool other) { local.Value = 1; if (other) return 1; return 2; }
                }
                public sealed class Reads : IWorker
                {
                    public int Execute(Data local, bool other) { if (other) return 1; return 2; }
                }
                public static class Calls
                {
                    public static void Entry(Data outside, bool flag, bool other)
                    {
                        var local = new Data();
                        IWorker worker = flag ? new Writes() : new Reads();
                        int result = worker.Execute(local, other);
                        if (local.Value == EXPECTED && flag RETURN) outside.Value = 1;
                    }
                }
                """.Replace("EXPECTED", setter ? "1" : "0").Replace("RETURN", guardedReturn ? "&& result == 1 && !other" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            MethodEffectKind expected = setter && !guardedReturn ? MethodEffectKind.Setter : MethodEffectKind.Getter;
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == expected));
        }

        // 同一泛型函数在两个实际类型下可能返回旧对象或新对象，不能按函数名合并整条调用。
        /// <summary>只读快捷判断仍须覆盖每个实际调用环境。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeKeepsDifferentGenericFactoryInstances(bool returnsNew, bool constructedInterface)
        {
            string source = """
                namespace Samples;
                public sealed class Data { }
                public struct Probe { public int Read() => 0; }
                public interface IFactory { object Read(); }
                public sealed class Pure : IFactory { public object Read() => null; }
                public sealed class Creates : IFactory { public object Read() => new Data(); }
                public static class Calls
                {
                    private static object Read<T>(T value) where T : IFactory => value.Read();
                    public static object Entry(Probe probe, Pure first, Creates second)
                    {
                        _ = probe.Read();
                        _ = Read(first);
                        return Read(RETURNED);
                    }
                }
                """.Replace("RETURNED", returnsNew ? "second" : "first");
            if (constructedInterface)
            {
                source = source.Replace("interface IFactory", "interface IFactory<T>")
                    .Replace("Pure : IFactory", "Pure : IFactory<int>").Replace("Creates : IFactory", "Creates : IFactory<string>")
                    .Replace("Read<T>(T value) where T : IFactory", "Read<T>(IFactory<T> value)")
                    .Replace("Entry(Probe probe, Pure first, Creates second)", "Entry(Probe probe, IFactory<int> first, IFactory<string> second)");
            }
            using TestProject project = TestProject.CreateWithCallTargets(source);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(returnsNew ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 反射的泛型字段及其值类型都由参考门面转交，存储身份仍对应唯一运行定义。
        /// <summary>真实 DLL 的参数写入和新容器读回旧对象使用相同转交规则。</summary>
        [TestMethod]
        [DataRow(false, 1)]
        [DataRow(true, 1)]
        [DataRow(false, 4)]
        [DataRow(true, 4)]
        public async Task AnalyzeKeepsForwardedReflectionFieldArguments(bool reverse, int jobs)
        {
            using TestProject project = TestProject.CreateWithForwardedMethodSignature(includeReflectedFields: true);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.GetMethods(catalog.Types.Single(type => type.Name == "ReflectedFields")).ToArray();
            Assert.HasCount(2, roots);
            if (reverse)
            {
                Array.Reverse(roots);
            }
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 不同构造参数的对象不能用于同一字段，未实现的异常分支也不能伪造正常写入。
        /// <summary>无效反射接收对象及不兼容字段值保持明确失败。</summary>
        [TestMethod]
        [DataRow("typeof(Box<int>).GetField(\"Value\").SetValue(new Box<string>(), 1);", "反射字段对象关联")]
        [DataRow("typeof(Box<Data>).GetField(\"Value\").GetValue(new Box<Other>());", "反射字段对象关联")]
        [DataRow("typeof(Box<Data>).GetField(\"Value\").SetValue(new Box<Data>(), new Other());", "反射字段赋值转换")]
        public async Task AnalyzeRejectsMismatchedReflectionFieldArguments(string action, string failure)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public sealed class Other { }
                public sealed class Box<T> { public T Value; }
                public static class Calls { private static int state; public static void Entry() { ACTION state = 1; } }
                """.Replace("ACTION", action));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(exception.Message, failure);
            }
        }

        // 常量实参和正常路径读取使用同一位宽运算，溢出或无符号转换不能改变结论。
        /// <summary>在源码和 DLL 中对照整数边界的实际可执行写入。</summary>
        [TestMethod]
        [DataRow("int", "int.MaxValue", "unchecked(value + 1) == int.MinValue", true)]
        [DataRow("int", "int.MaxValue", "unchecked(value + 1) > 0", false)]
        [DataRow("int", "int.MinValue", "unchecked(value - 1) == int.MaxValue", true)]
        [DataRow("long", "long.MaxValue", "unchecked(value + 1) == long.MinValue", true)]
        [DataRow("long", "long.MaxValue", "unchecked(value + 1) > 0", false)]
        [DataRow("long", "long.MinValue", "unchecked(value - 1) == long.MaxValue", true)]
        [DataRow("int", "int.MaxValue", "unchecked(value * 2) == -2", true)]
        [DataRow("uint", "uint.MaxValue", "(ulong)value == 4294967295UL", true)]
        [DataRow("uint", "uint.MaxValue", "(ulong)value == ulong.MaxValue", false)]
        [DataRow("int", "-1", "unchecked((ulong)value) == ulong.MaxValue", true)]
        [DataRow("uint", "uint.MaxValue", "value > 0U", true)]
        [DataRow("ulong", "ulong.MaxValue", "value > 0UL", true)]
        public async Task AnalyzeKeepsIntegerWidthForConstantArguments(string type, string value, string condition, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { Apply(VALUE); }
                    private static void Apply(TYPE value) { if (CONDITION) state = 1; }
                }
                """.Replace("TYPE", type).Replace("VALUE", value).Replace("CONDITION", condition));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.TypeDefinition owner = module.GetType("ExternalSamples.Calls");
                Mono.Cecil.MethodDefinition entry = owner.Methods.Single(method => method.Name == "Entry");
                Mono.Cecil.MethodDefinition apply = owner.Methods.Single(method => method.Name == "Apply");
                Mono.Cecil.Cil.Instruction literal = entry.Body.Instructions.Single(instruction => instruction.OpCode.Code
                    is Mono.Cecil.Cil.Code.Ldc_I4 or Mono.Cecil.Cil.Code.Ldc_I4_M1 or Mono.Cecil.Cil.Code.Ldc_I8);
                Mono.Cecil.Cil.Instruction[] conversions = entry.Body.Instructions.SkipWhile(instruction => instruction != literal).Skip(1)
                    .TakeWhile(instruction => instruction.OpCode != Mono.Cecil.Cil.OpCodes.Call).ToArray();
                Assert.IsTrue(conversions.All(instruction => instruction.OpCode.Code is Mono.Cecil.Cil.Code.Conv_I8 or Mono.Cecil.Cil.Code.Conv_U8));
                Mono.Cecil.Cil.Instruction[] arguments = apply.Body.Instructions.Where(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldarg_0).ToArray();
                Assert.IsNotEmpty(arguments);
                foreach (Mono.Cecil.Cil.Instruction argument in arguments)
                {
                    argument.OpCode = literal.OpCode;
                    argument.Operand = literal.Operand;
                    Mono.Cecil.Cil.Instruction previous = argument;
                    foreach (Mono.Cecil.Cil.Instruction conversion in conversions)
                    {
                        Mono.Cecil.Cil.Instruction next = Mono.Cecil.Cil.Instruction.Create(conversion.OpCode);
                        apply.Body.GetILProcessor().InsertAfter(previous, next);
                        previous = next;
                    }
                }
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(item => item.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 继承字段与实例字段保留实际构造参数，读取到的数值也与普通字段读取关联。
        /// <summary>反射泛型字段使用真实声明、参数和字段内容。</summary>
        [TestMethod]
        [DataRow("typeof(Store<int>).GetField(\"Value\").SetValue(null, 1);", true)]
        [DataRow("typeof(Box<int>).GetField(\"Value\").SetValue(box, 1);", true)]
        [DataRow("typeof(Derived).GetField(\"Value\").SetValue(derived, 1);", true)]
        [DataRow("if ((int)typeof(Store<int>).GetField(\"Value\").GetValue(null) == 1 && Store<int>.Value == 2) state = 1;", false)]
        public async Task AnalyzeKeepsConstructedReflectionFieldTypes(string action, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Store<T> { public static T Value; }
                public class Box<T> { public T Value; }
                public class Derived : Box<int> { }
                public static class Calls
                {
                    private static int state;
                    public static void Entry(Box<int> box, Derived derived) { ACTION }
                }
                """.Replace("ACTION", action));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(item => item.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 类型参数闭合后与直接 typeof 共用反射输入，不丢失字段引用的真实类型。
        /// <summary>自定义类型实参、新对象和由方法类型参数取得的字段分别验证。</summary>
        [TestMethod]
        [DataRow("var box = new Box<Data>(); typeof(Box<Data>).GetField(\"Value\").SetValue(box, outside); box.Value.X = 1;", true)]
        [DataRow("var box = new Box<Data>(); box.Value = outside; ((Data)typeof(Box<Data>).GetField(\"Value\").GetValue(box)).X = 1;", true)]
        [DataRow("var box = new Box<Data>(); box.Value = new Data(); ((Data)typeof(Box<Data>).GetField(\"Value\").GetValue(box)).X = 1;", false)]
        [DataRow("_ = Read<int>(null, \"MaxValue\");", false)]
        [DataRow("var box = new Box<Data>(); box.Value = outside; ((Data)Read<Box<Data>>(box, \"Value\")).X = 1;", true)]
        public async Task AnalyzeKeepsReflectedUserTypeArguments(string action, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int X; }
                public class Box<T> { public T Value; }
                public static class Calls
                {
                    private static object Read<T>(object instance, string name) { return typeof(T).GetField(name).GetValue(instance); }
                    public static void Entry(Data outside) { ACTION }
                }
                """.Replace("ACTION", action));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(item => item.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 静态字段入口快照保留完整类型实参，同槽的互斥类型不能借两个对象拼接成证据。
        /// <summary>相同静态槽、不同字段和不同泛型构造类型分别验证。</summary>
        [TestMethod]
        [DataRow("Store<int>.First", false, false)]
        [DataRow("Store<int>.Second", true, false)]
        [DataRow("Store<string>.First", true, false)]
        [DataRow("Store.First", false, true)]
        [DataRow("Store.Second", true, true)]
        [DataRow("Other.First", true, true)]
        [DataRow("Store<int>.First", false, true)]
        [DataRow("Store<int>.Second", true, true)]
        [DataRow("Store<string>.First", true, true)]
        public async Task AnalyzeKeepsStaticInputFieldIdentity(string second, bool setter, bool reflection)
        {
            // 只改变字段访问方式，期望仍由同槽或不同槽的真实类型约束决定。
            string Read(string field)
            {
                int separator = field.LastIndexOf('.');
                return reflection ? $"typeof({field[..separator]}).GetField(\"{field[(separator + 1)..]}\").GetValue(null)" : field;
            }
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Left { }
                public sealed class Right { }
                public static class Store<T> { public static object First; public static object Second; }
                public static class Store { public static object First; public static object Second; }
                public static class Other { public static object First; }
                public static class Calls
                {
                    private static int state;
                    public static void Entry()
                    {
                        if (FIRST is Left && SECOND is Right) state = 1;
                    }
                }
                """.Replace("FIRST", Read(!reflection || second.Contains('<') ? "Store<int>.First" : "Store.First")).Replace("SECOND", Read(second)));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 反射得到的旧对象继续参与普通字段读取和写入，不能因编号种类不同删除可执行路径。
        /// <summary>静态引用反射值的直接修改、数字条件与嵌套成员均对照源码及 DLL。</summary>
        [TestMethod]
        [DataRow("data.Number = 1;", true)]
        [DataRow("if (data.Number == 1) state = 1;", true)]
        [DataRow("data.Next.Number = 1;", true)]
        [DataRow("if (data.Number == 1 && data.Number == 2) state = 1;", false)]
        [DataRow("if (data.Next is Data) state = 1;", true)]
        [DataRow("if (data.Next.Number == 1) state = 1;", true)]
        [DataRow("typeof(Data).GetField(\"Number\").SetValue(typeof(Store).GetField(\"Value\").GetValue(null), 1);", true)]
        [DataRow("object selected = flag ? new Data() : typeof(Store).GetField(\"Value\").GetValue(null); typeof(Data).GetField(\"Number\").SetValue(selected, 1);", true)]
        public async Task AnalyzeKeepsReflectedStaticObjectThroughMemberAccess(string action, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Number; public Data Next; }
                public static class Store { public static Data Value; }
                public static class Calls
                {
                    private static int state;
                    public static void Entry(bool flag)
                    {
                        var data = (Data)typeof(Store).GetField("Value").GetValue(null);
                        ACTION
                    }
                }
                """.Replace("ACTION", action));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 大量互斥整数条件仍应保持 Getter，性能测量只观察公开分析结果。
        /// <summary>记录条件密集函数的解析时间与分配量，不使用机器相关阈值决定通过。</summary>
        [TestMethod]
        [DataRow(32)]
        [DataRow(128)]
        [DataRow(512)]
        public async Task AnalyzeScalesContradictoryIntegerConditions(int count)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry(int value) { CONDITIONS }
                }
                """.Replace("CONDITIONS", string.Concat(Enumerable.Range(0, count)
                    .Select(index => $"if (value > {index} && value <= {index}) state = {index};"))));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            long allocated = GC.GetTotalAllocatedBytes(true);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            stopwatch.Stop();
            Console.WriteLine($"condition-benchmark count={count} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3} allocatedBytes={GC.GetTotalAllocatedBytes(true) - allocated}");
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 工作数改变只影响准备后条件的求解调度，不能改变跨入口共享调用的结论与证据。
        /// <summary>源码和 DLL 在一、二、四、八路下逐字比较真实效果。</summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(4)]
        [DataRow(8)]
        public async Task AnalyzeKeepsConditionalResultsAcrossJobCounts(int jobs)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    private static int state;
                    private static void Shared(Data target, int left, int right) { if (left == right && left != right) target.Value = 1; }
                    public static void Getter(Data target, int left, int right) { Shared(target, left, right); if (left > right && left <= right) state = 1; }
                    public static void Setter(Data target, int left, int right) { Shared(target, left, right); if (left == right) state = 1; }
                    public static void NarrowGetter(Data target, int value) { if (value == 1) Shared(target, value, 1); }
                    public static void NarrowSetter(Data target, int value) { if (value == 1) target.Value = value; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.IsPublic).ToArray();
            Assert.HasCount(8, roots);
            string? baseline = null;
            foreach (int workers in new[] { 1, jobs })
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, workers);
                EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
                foreach (MethodEntry root in roots)
                {
                    Assert.AreEqual(root.Name.EndsWith("Setter", StringComparison.Ordinal) ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                        result.Methods.Single(method => method.MethodId == root.Id).Kind);
                }
                string actual = System.Text.Json.JsonSerializer.Serialize(result.Methods);
                baseline ??= actual;
                Assert.AreEqual(baseline, actual);
            }
        }

        // 一个分支的类型证明缺口不能遮住另一个可执行分支的确定写入。
        /// <summary>只保留未知调用时仍不产生结论，独立静态写入则足以证明 Setter。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeKeepsIndependentWriteBesideOpenGenericBox(bool writes)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IUse { void Touch(); }
                public struct Copy : IUse { public int Value; public void Touch() { Value = 1; } }
                public static class Calls
                {
                    private static int state;
                    public static void Entry<T>(T value, bool flag) where T : IUse
                    {
                        if (flag) ((IUse)value).Touch();
                        else { WRITE }
                    }
                }
                """.Replace("WRITE", writes ? "state = 1;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                if (writes)
                {
                    Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
                }
                else
                {
                    StringAssert.Contains(Assert.Throws<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message,
                        "类型参数的引用或值类型类别尚未闭合");
                }
            }
        }

        // 区分新结构体盒子、已有接口盒子和引用对象，开放泛型缺口只影响所属入口。
        /// <summary>未知类型类别明确保留失败，不吞成 Getter，也不抹去其他根的确定结论。</summary>
        [TestMethod]
        [DataRow("((IUse)value).Touch();")]
        [DataRow("value.Touch();")]
        public async Task AnalyzeSeparatesConcreteAndOpenGenericBoxInputs(string call)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IUse { void Touch(); }
                public sealed class Reference : IUse { public int Value; public void Touch() { Value = 1; } }
                public struct Copy : IUse { public int Value; public void Touch() { Value = 1; } }
                public static class Calls
                {
                    private static int state;
                    public static void Entry<T>(T value) where T : IUse { CALL }
                    public static void ByStruct(Copy value) => Entry<Copy>(value);
                    public static void ByInterface(IUse value) => Entry<IUse>(value);
                    public static void ByReference(Reference value) => Entry<Reference>(value);
                    public static void Write() { state = 1; }
                }
                """.Replace("CALL", call));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(10, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            MethodEntry[] known = roots.Where(root => root.Name != "Entry" || !call.StartsWith("((IUse)", StringComparison.Ordinal)).ToArray();
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, known, calls);
            foreach (MethodEntry root in roots)
            {
                if (root.Name == "Entry" && call.StartsWith("((IUse)", StringComparison.Ordinal))
                {
                    AnalysisException failure = Assert.Throws<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls));
                    StringAssert.Contains(failure.Message, "类型参数的引用或值类型类别尚未闭合");
                    continue;
                }
                Assert.AreEqual(root.Name == "ByStruct" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsNull(run.Failure, "某一入口的类型证明缺口不应使整轮分析提前退出");
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single(method => method.Name == "Write").Actual);
            Assert.AreEqual(call.StartsWith("((IUse)", StringComparison.Ordinal) ? null : (MethodEffectKind?)MethodEffectKind.Setter,
                run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 同一容器通过泛型引用转换前后访问时，成员存储不能分裂成两份。
        /// <summary>旧引用写入和新引用覆盖两个方向都保留字段身份。</summary>
        [TestMethod]
        [DataRow("Cast<Box>(box).Target = outside; box.Target.Value = 1;", true, false)]
        [DataRow("box.Target = outside; Cast<Box>(box).Target.Value = 1;", true, false)]
        [DataRow("Cast<Box>(box).Target = new Data(); box.Target.Value = 1;", false, false)]
        [DataRow("box.Target = new Data(); Cast<Box>(box).Target.Value = 1;", false, false)]
        [DataRow("box.Target = outside; Cast<Box>(box).Target = new Data(); box.Target.Value = 1;", false, false)]
        [DataRow("Cast<Box>(box).Target = outside; box.Target = new Data(); Cast<Box>(box).Target.Value = 1;", false, false)]
        [DataRow("Cast<Box>(box).Target = outside; box.Target.Value = 1;", true, true)]
        [DataRow("box.Target = outside; Cast<Box>(box).Target.Value = 1;", true, true)]
        [DataRow("box.Target = outside; Cast<Box>(box).Target = new Data(); box.Target.Value = 1;", false, true)]
        public async Task AnalyzeKeepsStorageAcrossGenericReferenceConversion(string body, bool setter, bool roundTrip)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public sealed class Box { public Data Target; }
                public static class Calls
                {
                    private static T Cast<T>(object value) => (T)value;
                    private static T Round<T>(T value) => Cast<T>((object)value);
                    public static void Entry(Data outside) { var box = new Box(); BODY }
                }
                """.Replace("BODY", roundTrip ? body.Replace("Cast<Box>(box)", "Round<Box>(box)") : body));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 泛型拆箱的实际类型为引用时，与普通引用转换共用空值和类型条件。
        /// <summary>合法、空值和不匹配引用分别验证，返回对象身份仍决定写入归属。</summary>
        [TestMethod]
        [DataRow("old", "converted.Value = 1;", true)]
        [DataRow("new Data()", "converted.Value = 1;", false)]
        [DataRow("null", "converted.Value = 1;", false)]
        [DataRow("null", "state = 1;", true)]
        [DataRow("new Other()", "state = 1;", false)]
        public async Task AnalyzeUnboxesGenericReferenceAsReferenceConversion(string input, string after, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public sealed class Other { }
                public static class Calls
                {
                    private static int state;
                    private static T Cast<T>(object value) => (T)value;
                    public static void Entry(Data old) { Data converted = Cast<Data>(INPUT); AFTER }
                }
                """.Replace("INPUT", input).Replace("AFTER", after));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 赋值右侧的除法必须正常完成；除零之前已发生的写入仍然保留。
        /// <summary>实际执行 DLL 核对异常与状态，再对照源码和 DLL 的分析结论。</summary>
        [TestMethod]
        [DataRow(false, 0)]
        [DataRow(true, 0)]
        [DataRow(false, 2)]
        public async Task AnalyzeChecksDivisionBeforePublishingWrite(bool writesFirst, int divisor)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { BEFORE int divisor = DIVISOR; state = 10 / divisor; }
                }
                """.Replace("BEFORE", writesFirst ? "state = 1;" : string.Empty)
                .Replace("DIVISOR", divisor.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                Type actual = execution.LoadFromStream(bytes).GetType("ExternalSamples.Calls", throwOnError: true)!;
                if (divisor == 0)
                {
                    var thrown = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => actual.GetMethod("Entry")!.Invoke(null, null));
                    Assert.IsInstanceOfType<DivideByZeroException>(thrown.InnerException);
                }
                else
                {
                    actual.GetMethod("Entry")!.Invoke(null, null);
                }
                Assert.AreEqual(divisor != 0 ? 5 : writesFirst ? 1 : 0,
                    actual.GetField("state", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            MethodEffectKind expected = writesFirst || divisor != 0 ? MethodEffectKind.Setter : MethodEffectKind.Getter;
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, method.Kind, method.MethodId);
            }
        }

        // 区分整数除法的正常结果、除零、带符号溢出和不抛出的浮点除零。
        /// <summary>实际执行不同位宽的除法，并核对直接赋值和子调用之后的写入。</summary>
        [TestMethod]
        [DataRow("int", "int.MinValue", "-1", false, "OverflowException")]
        [DataRow("long", "long.MinValue", "-1", true, "OverflowException")]
        [DataRow("uint", "uint.MaxValue", "0", false, "DivideByZeroException")]
        [DataRow("ulong", "ulong.MaxValue", "0", true, "DivideByZeroException")]
        [DataRow("int", "-10", "3", true, "")]
        [DataRow("long", "long.MinValue", "1", false, "")]
        [DataRow("uint", "uint.MaxValue", "uint.MaxValue", true, "")]
        [DataRow("ulong", "ulong.MaxValue", "ulong.MaxValue", false, "")]
        [DataRow("float", "10", "0", false, "")]
        [DataRow("double", "0", "0", true, "")]
        public async Task AnalyzeChecksDivisionKindsAndCallContinuation(string type, string dividend, string divisor, bool throughCall, string exceptionName)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static TYPE quotient;
                    private static int state;
                    private static TYPE Divide() { TYPE left = LEFT; TYPE right = RIGHT; return left / right; }
                    public static void Entry() { BODY state = 3; }
                }
                """.Replace("BODY", throughCall ? "Divide();" : "TYPE left = LEFT; TYPE right = RIGHT; quotient = left / right;")
                .Replace("TYPE", type).Replace("LEFT", dividend).Replace("RIGHT", divisor));
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                Type actual = execution.LoadFromStream(bytes).GetType("ExternalSamples.Calls", throwOnError: true)!;
                if (exceptionName.Length != 0)
                {
                    var thrown = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => actual.GetMethod("Entry")!.Invoke(null, null));
                    Assert.AreEqual(exceptionName, thrown.InnerException!.GetType().Name);
                }
                else
                {
                    actual.GetMethod("Entry")!.Invoke(null, null);
                }
                Assert.AreEqual(exceptionName.Length == 0 ? 3 : 0,
                    actual.GetField("state", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(entry => entry.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            MethodEffectKind expected = exceptionName.Length == 0 ? MethodEffectKind.Setter : MethodEffectKind.Getter;
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, method.Kind, method.MethodId);
            }
        }

        // 浮点类型穿过参数、算术和返回值仍保持正常除法语义。
        /// <summary>浮点除法可以产生无穷或非数值，不阻断后续真实写入。</summary>
        [TestMethod]
        [DataRow("value / value")]
        [DataRow("(value + 1) / (value - 1)")]
        [DataRow("Read(value) / 0")]
        [DataRow("((double)integer) / 0")]
        [DataRow("shared / shared")]
        public async Task AnalyzeKeepsFloatingDivisionFromActualOperands(string expression)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static double result;
                    private static double Read(double value) => value;
                    public static void Entry(double value, int integer) { double shared = value + 1; result = EXPRESSION; }
                }
                """.Replace("EXPRESSION", expression));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Setter, method.Kind, method.MethodId);
            }
        }

        // 运算类型正确并不代表取操作数成功，字段访问和拆箱仍需真实执行。
        /// <summary>实际 DLL 的前序异常与源码、DLL 的写入证明分别核对。</summary>
        [TestMethod]
        [DataRow("Box box = null; value = 10.0 / box.Value;", "NullReferenceException")]
        [DataRow("Box box = new Box(); value = 10.0 / box.Value;", "")]
        [DataRow("object boxed = 1.0f; value = 10.0 / (double)boxed;", "InvalidCastException")]
        [DataRow("object boxed = 1.0; value = 10.0 / (double)boxed;", "")]
        [DataRow("object boxed = Code.One; value = (int)boxed;", "")]
        [DataRow("object boxed = 1; value = (int)(Code)boxed;", "")]
        [DataRow("object boxed = 1L; value = (int)(Code)boxed;", "InvalidCastException")]
        [DataRow("object boxed = 1; value = Read<int>(boxed);", "")]
        [DataRow("object boxed = Code.One; if ((int)boxed != 1) return; value = 1;", "")]
        [DataRow("object boxed = 1u; value = (int)(Code)boxed;", "InvalidCastException")]
        [DataRow("object boxed = Other.One; value = (int)(Code)boxed;", "")]
        [DataRow("object boxed = new Cell<string>(); _ = (Cell<int>)boxed; value = 1;", "InvalidCastException")]
        [DataRow("object boxed = new Cell<int>(); _ = (Cell<int>)boxed; value = 1;", "")]
        public async Task AnalyzeChecksOperandAccessBeforeFloatingWrite(string body, string exceptionName)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public enum Code { One = 1 }
                public enum Other { One = 1 }
                public struct Cell<T> { }
                public class Box { public double Value; }
                public static class Calls
                {
                    private static double value;
                    private static int state;
                    private static T Read<T>(object input) => (T)input;
                    public static void Entry() { BODY state = 3; }
                }
                """.Replace("BODY", body));
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                Type actual = execution.LoadFromStream(bytes).GetType("ExternalSamples.Calls", throwOnError: true)!;
                if (exceptionName.Length != 0)
                {
                    var thrown = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => actual.GetMethod("Entry")!.Invoke(null, null));
                    Assert.AreEqual(exceptionName, thrown.InnerException!.GetType().Name);
                }
                else
                {
                    actual.GetMethod("Entry")!.Invoke(null, null);
                }
                Assert.AreEqual(exceptionName.Length == 0 ? 3 : 0,
                    actual.GetField("state", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(exceptionName.Length == 0 ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 反射返回的盒子有独立身份；字段选择、盒内引用和静态修改不能互相混用。
        /// <summary>按实际字段选型及修改对象核对源码和 DLL 的公开分析结果。</summary>
        [TestMethod]
        [DataRow("choice", MethodEffectKind.Getter)]
        [DataRow("static", MethodEffectKind.Setter)]
        [DataRow("static-reference", MethodEffectKind.Setter)]
        [DataRow("own", MethodEffectKind.Getter)]
        [DataRow("external", MethodEffectKind.Setter)]
        [DataRow("global", MethodEffectKind.Setter)]
        [DataRow("same-type", MethodEffectKind.Setter)]
        [DataRow("replace-box", MethodEffectKind.Getter)]
        [DataRow("replace-source", MethodEffectKind.Setter)]
        public async Task AnalyzeReflectedBoxUsesOwnStorage(string scenario, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IRun { void Run(); }
                public class Target { public int Value; }
                public struct A : IRun { public int Value; public Target Target; public void Run() { WRITE } }
                public struct B { }
                public class Holder { public A Left; public A Other; public B Right; }
                public static class Calls
                {
                    public static int State;
                    public static A Shared;
                    public static void Entry(bool flag, Holder holder) { BODY }
                }
                """.Replace("WRITE", scenario == "global" ? "Calls.State = 1;"
                    : scenario == "replace-box" ? "Target = new Target(); Target.Value++;"
                    : scenario is "external" or "same-type" or "replace-source" or "static-reference" ? "Target.Value++;" : "Value++;")
                .Replace("BODY", scenario switch
                {
                    "choice" => "var field = flag ? typeof(Holder).GetField(\"Left\") : typeof(Holder).GetField(\"Right\"); object item = field.GetValue(new Holder()); if (flag && item is B) State = 1;",
                    "static" => "object item = typeof(Calls).GetField(\"Shared\").GetValue(null); if (item is A) State = 1;",
                    "static-reference" => "((IRun)typeof(Calls).GetField(\"Shared\").GetValue(null)).Run();",
                    "same-type" => "var field = flag ? typeof(Holder).GetField(\"Left\") : typeof(Holder).GetField(\"Other\"); ((IRun)field.GetValue(holder)).Run();",
                    "replace-source" => "var local = new Holder(); local.Left = holder.Left; var item = (IRun)typeof(Holder).GetField(\"Left\").GetValue(local); local.Left = new A(); item.Run();",
                    _ => "((IRun)typeof(Holder).GetField(\"Left\").GetValue(holder)).Run();",
                }));
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                System.Reflection.Assembly assembly = execution.LoadFromStream(bytes);
                Type callsType = assembly.GetType("ExternalSamples.Calls", throwOnError: true)!;
                Type holderType = assembly.GetType("ExternalSamples.Holder", throwOnError: true)!;
                Type targetType = assembly.GetType("ExternalSamples.Target", throwOnError: true)!;
                Type valueType = assembly.GetType("ExternalSamples.A", throwOnError: true)!;
                foreach (bool flag in new[] { false, true })
                {
                    object holder = Activator.CreateInstance(holderType)!;
                    object target = Activator.CreateInstance(targetType)!;
                    object value = Activator.CreateInstance(valueType)!;
                    valueType.GetField("Target")!.SetValue(value, target);
                    holderType.GetField("Left")!.SetValue(holder, value);
                    holderType.GetField("Other")!.SetValue(holder, value);
                    callsType.GetField("State")!.SetValue(null, 0);
                    callsType.GetField("Shared")!.SetValue(null, value);
                    callsType.GetMethod("Entry")!.Invoke(null, new[] { (object)flag, holder });
                    bool changed = (int)callsType.GetField("State")!.GetValue(null)! != 0
                        || (int)targetType.GetField("Value")!.GetValue(target)! != 0;
                    Assert.AreEqual(expected == MethodEffectKind.Setter, changed, scenario + ":" + flag);
                    Assert.AreEqual(0, valueType.GetField("Value")!.GetValue(holderType.GetField("Left")!.GetValue(holder)));
                }
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, method.Kind, method.MethodId);
            }
        }

        // 接口可作为泛型实参，空输入不依赖存在一个可构造的引用类型实现。
        /// <summary>接口实参允许空值及装箱结构体，而 new 约束不能拼接不同类型参数。</summary>
        [TestMethod]
        [DataRow("none")]
        [DataRow("struct")]
        [DataRow("new")]
        public async Task AnalyzeKeepsGenericNullInputs(string scenario)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IRun { void Run(); }
                IMPLEMENTATIONS
                public static class Calls
                {
                    public static int State;
                    public static void Entry<T>(T first, T second) where T : IRun CONSTRAINT
                    { BODY }
                }
                """.Replace("IMPLEMENTATIONS", scenario == "none" ? string.Empty
                    : "public struct Writer : IRun { public void Run() { Calls.State = 1; } } public sealed class Quiet : IRun { public void Run() { } }")
                .Replace("CONSTRAINT", scenario == "new" ? ", new()" : string.Empty)
                .Replace("BODY", scenario switch
                {
                    "struct" => "if ((object)first == null) second.Run();",
                    "new" => "if ((object)first == null && second is Writer) State = 1;",
                    _ => "if ((object)first == null) State = 1;",
                }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            if (scenario == "new")
            {
                foreach (MethodEntry root in roots)
                {
                    Assert.Contains("类型参数", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
                }
                return;
            }
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 同一入口中的连续条件调用共用前序执行事实，不同入口和调用实参保持独立。
        /// <summary>源码与 DLL 的矛盾条件仍为 Getter，换实参后的合法修改仍为 Setter。</summary>
        [TestMethod]
        [DataRow(8, 1)]
        [DataRow(8, 4)]
        [DataRow(32, 1)]
        [DataRow(32, 4)]
        public async Task AnalyzeRepeatedConditionalCallsPreserveEachRoot(int count, int jobs)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Box { public int Value; }
                public static class Calls
                {
                    private static int Choose(int value) { if (value == 0) return 1; return 0; }
                    private static void Check(Box target, int value, int other)
                    {
                        if (value != 0 && Choose(other) != 0) target.Value = 1;
                    }
                    public static void Getter(Box target, int value) { CALLS }
                    public static void Setter(Box target, int value) { CALLS Check(target, value, 0); }
                }
                """.Replace("CALLS", string.Join("\n", Enumerable.Repeat("Check(target, value, value);", count))));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "Getter" or "Setter").ToArray();
            Assert.HasCount(4, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Setter" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 直接读取别的类型的静态字段也会触发初始化，不能只检查普通方法调用。
        /// <summary>没有显式写入的读取函数，仍须交代初始化中的真实写入或未知实现。</summary>
        [TestMethod]
        [DataRow(false, "value")]
        [DataRow(false, "discard")]
        [DataRow(false, "address")]
        [DataRow(false, "reflection")]
        [DataRow(true, "value")]
        [DataRow(true, "discard")]
        [DataRow(true, "address")]
        [DataRow(true, "reflection")]
        public async Task AnalyzeKeepsStaticFieldInitializationAtRead(bool unknown, string access)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Storage
                {
                    public static int Value;
                    private static int state;
                    static Storage() { INITIALIZER }
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                    private static extern void Unknown();
                }
                public static class Calls { public static int Entry() { READ } private static int Read(ref int value) => 0; }
                """.Replace("INITIALIZER", unknown ? "Unknown();" : "state=1;")
                .Replace("READ", access switch
                {
                    "discard" => "_ = Storage.Value; return 0;",
                    "address" => "return Read(ref Storage.Value);",
                    "reflection" => "typeof(Storage).GetField(\"Value\").GetValue(null); return 0;",
                    _ => "return Storage.Value;",
                }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            foreach (MethodEntry root in roots)
            {
                Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
            }
        }

        // 共享相同泛型目标查找时，旧对象与新对象以及不同类型实参不能合并。
        /// <summary>源码与 DLL 的实际接收对象选择在单路、四路下保持一致。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task AnalyzePreservesReceiverChoiceWhileSharingGenericTargetLookup(bool differentType, bool useOld)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IUse { void Touch(); }
                public sealed class Box<T> : IUse { public int Value; public void Touch() { Value=1; } }
                public static class Calls
                {
                    public static void Entry(Box<int> outside, bool flag)
                    {
                        IUse target = flag ? outside : new Box<ARGUMENT>();
                        if (CONDITION) target.Touch();
                    }
                }
                """.Replace("ARGUMENT", differentType ? "string" : "int").Replace("CONDITION", useOld ? "flag" : "!flag"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            foreach (int jobs in new[] { 1, 4 })
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                    method.Kind == (useOld ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
            }
        }

        // 连续序列化不能指数展开历史查询，也不能漏掉公共静态开关的初始化。
        /// <summary>复刻 KFBWriter 默认值过滤，局部对象写入不使尚未闭合的静态初始化消失。</summary>
        [TestMethod]
        [DataRow(8)]
        [DataRow(128)]
        public async Task AnalyzeCompletesRepeatedStaticReads(int count)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Writer
                {
                    public static bool SkipDefault = true;
                    private uint m_value;
                    private void WriteFieldHeader(int fieldNumber) { m_value = (uint)fieldNumber; }
                    private void Write(uint value) { m_value = value; }
                    public void WriteField(int fieldNumber, bool value, bool defaultValue = false)
                    {
                        if (SkipDefault && value == defaultValue) return;
                        WriteFieldHeader(fieldNumber);
                        Write(value ? 1u : 0u);
                    }
                }
                public static class Calls
                {
                    public static void Entry(bool value) { Writer writer = new Writer(); CALLS }
                }
                """.Replace("CALLS", string.Join(" ", Enumerable.Range(0, count).Select(index => $"writer.WriteField({index}, value);"))));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEntry root in roots)
            {
                Assert.Contains("初始化写入", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
            }
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(30), $"{count} 次调用分析耗时 {watch.Elapsed}");
        }

        // 局部值即使确定不变，也不能省略其写入前的真实初始化和正常返回检查。
        /// <summary>未知初始化之后的局部常量条件不提供 Setter 证明。</summary>
        [TestMethod]
        [DataRow("int local = 0; Trigger.Touch(); if (local == 0) outside.Value = 1;")]
        [DataRow("Trigger.Touch(); if (flag) State = 1;")]
        [DataRow("Trigger.Set(flag);")]
        [DataRow("if (flag) Trigger.State = 1;")]
        [DataRow("if (Trigger.State == 0 && flag) State = 1;")]
        [DataRow("Trigger.State = 1;")]
        [DataRow("_ = Trigger.State; if (flag) State = 1;")]
        [DataRow("_ = Trigger.State; State = 1;")]
        [DataRow("typeof(Trigger).GetField(\"State\").GetValue(null); if (flag) State = 1;")]
        [DataRow("typeof(Trigger).GetField(\"State\").GetValue(null); State = 1;")]
        public async Task AnalyzeKeepsInitializationBeforePrivateSlotCondition(string entry)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Trigger
                {
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                    private static extern void Unknown();
                    static Trigger() { Unknown(); }
                    public static int State;
                    public static void Touch() { }
                    public static void Set(bool flag) { if (flag) State = 1; }
                }
                public static class Calls { public static int State; public static void Entry(Data outside, bool flag) { ENTRY } }
                """.Replace("ENTRY", entry));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            foreach (int jobs in new[] { 1, 4 })
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, requireCompleteCalls: false);
                foreach (MethodEntry root in roots)
                {
                    Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
                }
            }
        }

        // 初始化失败只排除无法证明的执行路径，不能遮住另一条确定修改路径。
        /// <summary>分别核对汇合前驱、写入后的未知访问以及只有未知前缀的写入。</summary>
        [TestMethod]
        [DataRow("if (flag) _ = Trigger.State; State = 1;", true)]
        [DataRow("State = 1; _ = Trigger.State;", true)]
        [DataRow("if (flag) { _ = Trigger.State; State = 1; }", false)]
        [DataRow("if (flag) { State = 1; _ = Trigger.State; }", true)]
        public async Task AnalyzeKeepsWriteBesideUnprovedInitialization(string entry, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Trigger
                {
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                    private static extern void Unknown();
                    static Trigger() { Unknown(); }
                    public static int State;
                }
                public static class Calls { private static int State; public static void Entry(bool flag) { ENTRY } }
                """.Replace("ENTRY", entry));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            foreach (MethodEntry root in roots)
            {
                if (setter)
                {
                    Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
                }
                else
                {
                    Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
                }
            }
        }

        // 返回局部槽的证明必须延伸到实际返回，不能停在更早的对象创建处。
        /// <summary>未知静态初始化之后的新对象尚未证明能够传出。</summary>
        [TestMethod]
        [DataRow("_ = Trigger.State;")]
        [DataRow("typeof(Trigger).GetField(\"State\").GetValue(null);")]
        public async Task AnalyzeKeepsInitializationBetweenCreationAndReturn(string access)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Trigger
                {
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                    private static extern void Unknown();
                    static Trigger() { Unknown(); }
                    public static int State;
                }
                public static class Calls
                {
                    private static void Consume(Data value) { }
                    public static Data Entry() { Data result = new Data(); Consume(result); ACCESS return result; }
                }
                """.Replace("ACCESS", access));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            foreach (MethodEntry root in roots)
            {
                MethodBehavior body = calls.Behaviors.Methods.Single(method => method.MethodId == root.Id);
                Assert.IsTrue(body.Returns.All(returned => body.Values[returned.ValueId!.Value].Kind == BehaviorValueKind.SlotRead));
                Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
            }
        }

        // 未取地址的变量槽可以省去无关写入追查，但真实赋值和所指对象的变化必须保留。
        /// <summary>源码和 DLL 在单路、四路下核对普通槽与暴露地址槽的不同处理。</summary>
        [TestMethod]
        [DataRow("LocalOnly", MethodEffectKind.Getter)]
        [DataRow("AssignLocal", MethodEffectKind.Setter)]
        [DataRow("AddressLocal", MethodEffectKind.Setter)]
        [DataRow("AddressParameter", MethodEffectKind.Setter)]
        [DataRow("ObjectContents", MethodEffectKind.Setter)]
        [DataRow("StructContents", MethodEffectKind.Setter)]
        [DataRow("AssignParameter", MethodEffectKind.Setter)]
        [DataRow("AddressNumber", MethodEffectKind.Setter)]
        [DataRow("StableParameter", MethodEffectKind.Getter)]
        [DataRow("BeforeParameter", MethodEffectKind.Getter)]
        public async Task AnalyzeSeparatesUnaddressedSlotsFromObjectContents(string entryName, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public static class Calls
                {
                    private static int state;
                    private static void SetNumber(ref int value) { value = 1; }
                    private static void Empty() { }
                    private static void Set(Data value) { value.Value = 1; }
                    private static void Replace(ref Data value, Data outside) { value = outside; }
                    private static void ReplaceHolder(ref Holder value, Data outside) { value.Target = outside; }
                    public static void LocalOnly(Data outside) { Data x = new Data(); Empty(); x.Value = 1; }
                    public static void AssignLocal(Data outside) { Data x = new Data(); Empty(); x = outside; x.Value = 1; }
                    public static void AddressLocal(Data outside) { Data x = new Data(); Replace(ref x, outside); x.Value = 1; }
                    public static void AddressParameter(Data input, Data outside) { input = new Data(); Replace(ref input, outside); input.Value = 1; }
                    public static void ObjectContents(Data outside) { Data x = new Data(); Set(x); if (x.Value == 1) outside.Value = 1; }
                    public static void StructContents(Data outside) { Holder x = default; ReplaceHolder(ref x, outside); x.Target.Value = 1; }
                    public static void AssignParameter(int value) { value = 1; if (value == 1) state = 1; }
                    public static void AddressNumber(int value) { SetNumber(ref value); if (value == 1) state = 1; }
                    public static void StableParameter(int value) { if (value == 1 && value == 2) state = 1; }
                    public static void BeforeParameter(int value) { _ = 1 / (value - value); if (value == 1) state = 1; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == entryName).ToArray();
            Assert.HasCount(2, roots);
            foreach (int jobs in new[] { 1, 4 })
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(effect => effect.Kind == expected));
            }
        }

        // 引用标量的旧值快照不会随后续写回改变，调用者局部槽写入也不算外部修改。
        /// <summary>同一 ref 参数写前写后的比较使用各自真实内容。</summary>
        [TestMethod]
        [DataRow(false, MethodEffectKind.Getter)]
        [DataRow(true, MethodEffectKind.Setter)]
        public async Task AnalyzePreservesScalarReferenceSnapshot(bool unequal, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    private static void Change(ref int value, Data target) { int before = value; value = 1; if (before OP value) target.Value = 1; }
                    public static void Entry(Data target) { int local = 0; Change(ref local, target); }
                }
                """.Replace("OP", unequal ? "!=" : "=="));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(effect => effect.Kind == expected));
        }

        // 参数重绑只改变指针槽，旧地址、结构体自身和输出参数仍指向各自的真实存储。
        /// <summary>源码和真实 DLL 对照验证入口存储身份与条件分支。</summary>
        [TestMethod]
        [DataRow("ScalarNever", MethodEffectKind.Getter)]
        [DataRow("ScalarWrite", MethodEffectKind.Setter)]
        [DataRow("OutInitialized", MethodEffectKind.Setter)]
        [DataRow("RebindFresh", MethodEffectKind.Getter)]
        [DataRow("CapturedAddress", MethodEffectKind.Setter)]
        [DataRow("StructThis", MethodEffectKind.Setter)]
        [DataRow("StructThisNull", MethodEffectKind.Getter)]
        [DataRow("RefObjectNull", MethodEffectKind.Getter)]
        [DataRow("RefObjectWrite", MethodEffectKind.Setter)]
        public async Task AnalyzePreservesRootStorageRole(string entryName, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder
                {
                    public Data Target;
                    public void StructThis() { if (Target != null) Target.Value = 1; }
                    public void StructThisNull() { if (Target == null) Target.Value = 1; }
                }
                public static class Calls
                {
                    public static void ScalarNever(ref int value, Data target) { if (value != value) target.Value = 1; }
                    public static void ScalarWrite(ref int value, Data target) { if (value == 1) target.Value = 1; }
                    public static void OutInitialized(out Holder value) { value = default; value.Target = new Data(); value.Target.Value = 1; }
                    public static void RebindFresh(ref Holder value) { Holder[] local = new Holder[1]; local[0].Target = new Data(); value = ref local[0]; value.Target.Value = 1; }
                    public static void CapturedAddress(ref Holder value)
                    {
                        ref Holder captured = ref value; Holder[] local = new Holder[1]; local[0].Target = new Data();
                        value = ref local[0]; if (captured.Target != null) captured.Target.Value = 1;
                    }
                    public static void RefObjectNull(ref Data value) { if (value == null) value.Value = 1; }
                    public static void RefObjectWrite(ref Data value) { if (value != null) value.Value = 1; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls" || type.Name == "Holder")
                .SelectMany(catalog.GetMethods).Where(method => method.Name == entryName).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(expected, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 引用结构体的成员读取必须使用真实入口存储，不能把内部地址编号当作指令下标。
        /// <summary>源码和真实 DLL 的 ref 结构体引用字段在空值条件下仍正确判定。</summary>
        [TestMethod]
        [DataRow(false, MethodEffectKind.Setter)]
        [DataRow(true, MethodEffectKind.Getter)]
        public async Task AnalyzeReadsRootRefStructField(bool nullBranch, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public static class Calls { public static void Entry(ref Holder value) { if (value.Target OP null) value.Target.Value = 1; } }
                """.Replace("OP", nullBranch ? "==" : "!="));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(effect => effect.Kind == expected));
        }

        // 嵌套结构体入口的引用字段不能因为取了局部地址就被当成新对象。
        /// <summary>嵌套入口地址与源码值读取一致；未实现的复制循环仍单独保留失败。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzePreservesNestedStructInputAndKeepsCopyCycleVisible(bool loop)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public struct Pair { public Holder Left; }
                public static class Calls { public static void Entry(Pair value, bool flag) { BODY } }
                """.Replace("BODY", loop
                    ? "Holder h=default; h.Target=value.Left.Target; while(flag) { Holder copy=h; h=copy; } h.Target.Value=1;"
                    : "value.Left.Target.Value = 1;"));
            if (!loop)
            {
                using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                    new Mono.Cecil.ReaderParameters { InMemory = true });
                Mono.Cecil.MethodDefinition entry = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarga_S, entry.Parameters[0]);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldflda, module.GetType("ExternalSamples.Pair").Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, module.GetType("ExternalSamples.Holder").Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, module.GetType("ExternalSamples.Data").Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEntry root in roots)
            {
                if (loop)
                {
                    Assert.Contains("结构体", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
                }
                else
                {
                    Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
                }
            }
        }

        // 同一嵌套字段的值读取与地址读取共享身份，左右字段和引用槽不能混在一起。
        /// <summary>源码和 DLL 同时验证嵌套根槽、空值条件、局部覆盖与外部引用写入。</summary>
        [TestMethod]
        [DataRow(false, "write", MethodEffectKind.Setter)]
        [DataRow(true, "write", MethodEffectKind.Setter)]
        [DataRow(false, "null", MethodEffectKind.Getter)]
        [DataRow(true, "null", MethodEffectKind.Getter)]
        [DataRow(false, "different", MethodEffectKind.Setter)]
        [DataRow(true, "different", MethodEffectKind.Setter)]
        [DataRow(false, "replace", MethodEffectKind.Getter)]
        [DataRow(true, "replace", MethodEffectKind.Setter)]
        [DataRow(false, "local-old", MethodEffectKind.Setter)]
        [DataRow(false, "local-new", MethodEffectKind.Getter)]
        public async Task AnalyzeKeepsNestedAddressStorageIdentity(bool byReference, string operation, MethodEffectKind expected)
        {
            string body = operation switch
            {
                "write" => "selected.Target.Value=1;",
                "null" => "if(value.Left.Target==null) selected.Target.Value=1;",
                "different" => "if(value.Right.Target==null) selected.Target.Value=1;",
                "replace" => "selected.Target=new Data(); selected.Target.Value=1;",
                "local-old" => "Pair local=default; ref Holder slot=ref local.Left; slot.Target=outside; slot.Target.Value=1;",
                "local-new" => "Pair local=default; ref Holder slot=ref local.Left; slot.Target=new Data(); slot.Target.Value=1;",
                _ => throw new InvalidOperationException(operation),
            };
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public struct Pair { public Holder Left; public Holder Right; }
                public static class Calls
                {
                    public static void Entry(REF Pair value, Data outside)
                    {
                        ref Holder selected=ref value.Left;
                        BODY
                    }
                }
                """.Replace("REF", byReference ? "ref" : string.Empty).Replace("BODY", body));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(expected, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 结构体数组的变量下标尚未表示时，不能沿新数组把里面的旧引用一起忽略。
        /// <summary>未解析的内容来源必须保留失败，不能重新引入 Getter 漏报。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsUnrepresentedStructArrayIndexVisible()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public static class Calls
                {
                    public static void Entry(Data outside, int index)
                    {
                        Holder[] values=new Holder[2];
                        values[0].Target=outside;
                        values[1].Target=outside;
                        values[index].Target.Value=1;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEntry root in roots)
            {
                Assert.Contains("结构体", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls)).Message);
            }
        }

        // 复刻 KHEntityHandle.cs 的相等比较和散列函数，仅省略不参与这些函数的弱引用字段。
        /// <summary>真实只读结构体写法经源码和 DLL 分析都不应因参数取地址而留下失败。</summary>
        [TestMethod]
        public async Task AnalyzeReadsKhEntityHandleComparisonPattern()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public readonly struct KHEntityHandle
                {
                    private readonly int m_sid;
                    private readonly int m_version;
                    public bool Equals(KHEntityHandle other)
                    {
                        return m_sid == other.m_sid && m_version == other.m_version;
                    }
                    public override int GetHashCode()
                    {
                        unchecked { return (m_sid * 397) ^ m_version; }
                    }
                    public static bool operator ==(KHEntityHandle a, KHEntityHandle b) => a.Equals(b);
                    public static bool operator !=(KHEntityHandle a, KHEntityHandle b) => !a.Equals(b);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "KHEntityHandle").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "Equals" or "GetHashCode" or "op_Equality" or "op_Inequality").ToArray();
            Assert.HasCount(8, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 只读递归不等于一定返回，也不能把返回引用用作写入时省去实际返回证明。
        /// <summary>外层仍有写入时，不借用只读子函数跳过来源与执行关系。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsReadonlyRecursiveReturnProofSeparate()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public static class Calls
                {
                    public static void Entry(Holder value) { Find(value, 1).Value=1; }
                    private static Data Find(Holder value, int depth)
                    {
                        if(depth==0) return value.Target;
                        return Find(value, depth-1);
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEntry root in roots)
            {
                Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls));
            }
        }

        // 根结构体取地址不会让它所引用的已有对象变成局部数据。
        /// <summary>实际 DLL 经 ldarga 读取引用字段后写入，仍应发现外部对象修改。</summary>
        [TestMethod]
        [DataRow("always", MethodEffectKind.Setter)]
        [DataRow("null", MethodEffectKind.Getter)]
        [DataRow("nonnull", MethodEffectKind.Setter)]
        public async Task AnalyzeTracksReferenceFieldThroughRootStructAddress(string guard, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public static class Calls { public static void Entry(Holder value) { GUARD value.Target.Value = 1; } }
                """.Replace("GUARD", guard == "always" ? string.Empty : guard == "null" ? "if (value.Target == null)" : "if (value.Target != null)"));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition entry = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                Mono.Cecil.Cil.Instruction returned = writer.Create(Mono.Cecil.Cil.OpCodes.Ret);
                if (guard != "always")
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarga_S, entry.Parameters[0]);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, module.GetType("ExternalSamples.Holder").Fields.Single());
                    writer.Emit(guard == "null" ? Mono.Cecil.Cil.OpCodes.Brtrue : Mono.Cecil.Cil.OpCodes.Brfalse, returned);
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarga_S, entry.Parameters[0]);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, module.GetType("ExternalSamples.Holder").Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, module.GetType("ExternalSamples.Data").Fields.Single());
                writer.Append(returned);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(effect => effect.Kind == expected));
        }

        // 结构体留在求值栈上时已复制完成，后续修改原槽不能改变这份快照。
        /// <summary>实际执行 DLL 核对写入对象，再对照源码副本与求值栈副本。</summary>
        [TestMethod]
        [DataRow(false, "flat")]
        [DataRow(true, "flat")]
        [DataRow(false, "nested")]
        [DataRow(true, "nested")]
        [DataRow(false, "indirect")]
        [DataRow(true, "indirect")]
        public async Task AnalyzePreservesAggregateStackSnapshot(bool oldFirst, string shape)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public struct Pair { public Holder Left; }
                public static class Calls
                {
                    private static void Replace(ref Holder value, Data next) { value.Target = next; }
                    public static void Entry(Data outside)
                    {
                        AGGREGATE local = default; Replace(ref localMEMBER, FIRST); AGGREGATE copy = local;
                        Replace(ref localMEMBER, SECOND); copyMEMBER.Target.Value = 1;
                    }
                }
                """.Replace("FIRST", oldFirst ? "outside" : "new Data()").Replace("SECOND", oldFirst ? "new Data()" : "outside")
                .Replace("AGGREGATE", shape == "nested" ? "Pair" : "Holder").Replace("MEMBER", shape == "nested" ? ".Left" : string.Empty));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.TypeDefinition type = module.GetType("ExternalSamples.Calls");
                Mono.Cecil.TypeDefinition data = module.GetType("ExternalSamples.Data");
                Mono.Cecil.TypeDefinition holder = module.GetType("ExternalSamples.Holder");
                Mono.Cecil.TypeDefinition aggregate = shape == "nested" ? module.GetType("ExternalSamples.Pair") : holder;
                Mono.Cecil.MethodDefinition entry = type.Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                entry.Body.Variables.Clear();
                entry.Body.Variables.Add(new Mono.Cecil.Cil.VariableDefinition(aggregate));
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloca_S, entry.Body.Variables[0]);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Initobj, aggregate);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloca_S, entry.Body.Variables[0]);
                if (shape == "nested")
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldflda, aggregate.Fields.Single());
                }
                if (oldFirst)
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                }
                else
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Newobj, data.Methods.Single(method => method.IsConstructor));
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Call, type.Methods.Single(method => method.Name == "Replace"));
                if (shape == "indirect")
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloca_S, entry.Body.Variables[0]);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldobj, holder);
                }
                else
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloc_0);
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloca_S, entry.Body.Variables[0]);
                if (shape == "nested")
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldflda, aggregate.Fields.Single());
                }
                if (oldFirst)
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Newobj, data.Methods.Single(method => method.IsConstructor));
                }
                else
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Call, type.Methods.Single(method => method.Name == "Replace"));
                if (shape == "nested")
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, aggregate.Fields.Single());
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, holder.Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, data.Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                module.Write(project.ExternalAssemblyPath);
            }
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                System.Reflection.Assembly assembly = execution.LoadFromStream(bytes);
                Type data = assembly.GetType("ExternalSamples.Data", throwOnError: true)!;
                object outside = Activator.CreateInstance(data)!;
                assembly.GetType("ExternalSamples.Calls", throwOnError: true)!.GetMethod("Entry")!.Invoke(null, new[] { outside });
                Assert.AreEqual(oldFirst ? 1 : 0, data.GetField("Value")!.GetValue(outside));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(oldFirst ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 局部变量本身与它指向的对象分开处理，取地址后仍完整读取调用写回。
        /// <summary>直接赋值、对象修改、ref 参数及结构体地址在源码和 DLL 中保持相同结果。</summary>
        [TestMethod]
        [DataRow("LocalOnly", MethodEffectKind.Getter)]
        [DataRow("ObjectChanged", MethodEffectKind.Setter)]
        [DataRow("LocalReplaced", MethodEffectKind.Setter)]
        [DataRow("ParameterReplaced", MethodEffectKind.Getter)]
        [DataRow("StructReplaced", MethodEffectKind.Setter)]
        [DataRow("StructReset", MethodEffectKind.Getter)]
        [DataRow("StructCopied", MethodEffectKind.Setter)]
        [DataRow("StructCopyKept", MethodEffectKind.Getter)]
        [DataRow("StructByValue", MethodEffectKind.Getter)]
        [DataRow("StructReadByValue", MethodEffectKind.Setter)]
        [DataRow("StructCopyConditional", MethodEffectKind.Setter)]
        [DataRow("NestedCopy", MethodEffectKind.Setter)]
        [DataRow("NestedOther", MethodEffectKind.Getter)]
        [DataRow("NestedReset", MethodEffectKind.Getter)]
        [DataRow("ArrayReplaceOld", MethodEffectKind.Setter)]
        [DataRow("ArrayReplaceFresh", MethodEffectKind.Getter)]
        [DataRow("RootStruct", MethodEffectKind.Setter)]
        [DataRow("StructReadThroughCopy", MethodEffectKind.Setter)]
        [DataRow("BranchReplaced", MethodEffectKind.Getter)]
        public async Task AnalyzeSeparatesPrivateSlotsFromAddressedStorage(string name, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public struct Holder { public Data Target; }
                public struct Pair { public Holder Left; public Holder Right; }
                public static class Calls
                {
                    private static void Change(Data value) { value.Value = 1; }
                    private static void Replace(ref Data value, Data next) { value = next; }
                    private static void ReplaceHolder(ref Holder value, Data next) { value.Target = next; }
                    private static void ReplaceCopy(Holder value, Data next) { value.Target = next; }
                    private static void ReadCopy(Holder value) { value.Target.Value = 1; }
                    private static void ForwardCopy(Holder value) { ReadCopy(value); }
                    private static void ReplaceElement(Holder[] values, Holder next) { values[0] = next; }
                    public static void LocalOnly(Data outside)
                    {
                        Data local = new Data(); Change(new Data());
                        if (local.Value == 1) outside.Value = 1;
                    }
                    public static void ObjectChanged(Data outside)
                    {
                        Data local = new Data(); Change(local);
                        if (local.Value == 1) outside.Value = 1;
                    }
                    public static void LocalReplaced(Data outside)
                    {
                        Data local = new Data(); Replace(ref local, outside); local.Value = 1;
                    }
                    public static void ParameterReplaced(Data outside)
                    {
                        Replace(ref outside, new Data()); outside.Value = 1;
                    }
                    public static void StructReplaced(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, outside); local.Target.Value = 1;
                    }
                    public static void StructReset(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, outside); local = default;
                        if (local.Target != null) local.Target.Value = 1;
                    }
                    public static void StructCopied(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, outside); Holder copy = local;
                        ReplaceHolder(ref local, new Data()); copy.Target.Value = 1;
                    }
                    public static void StructCopyKept(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, new Data()); Holder copy = local;
                        ReplaceHolder(ref local, outside); copy.Target.Value = 1;
                    }
                    public static void StructByValue(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, new Data());
                        ReplaceCopy(local, outside); local.Target.Value = 1;
                    }
                    public static void StructReadByValue(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, outside); ReadCopy(local);
                    }
                    public static void StructCopyConditional(Data outside, bool flag)
                    {
                        Holder local = default; ReplaceHolder(ref local, outside); Holder copy = local;
                        if (flag) copy.Target.Value = 1;
                    }
                    public static void NestedCopy(Data outside)
                    {
                        Pair local = default; local.Left.Target = outside; Pair copy = local;
                        copy.Left.Target.Value = 1;
                    }
                    public static void NestedOther(Data outside)
                    {
                        Pair local = default; local.Left.Target = outside; Pair copy = local;
                        if (copy.Right.Target != null) copy.Right.Target.Value = 1;
                    }
                    public static void NestedReset(Data outside)
                    {
                        Pair local = default; local.Left.Target = outside; local.Left = default;
                        if (local.Left.Target != null) local.Left.Target.Value = 1;
                    }
                    public static void ArrayReplaceOld(Data outside)
                    {
                        Holder[] values = new Holder[1]; Holder next = default; next.Target = outside;
                        ReplaceElement(values, next); values[0].Target.Value = 1;
                    }
                    public static void ArrayReplaceFresh(Data outside)
                    {
                        Holder[] values = new Holder[1]; values[0].Target = outside;
                        Holder next = default; next.Target = new Data();
                        ReplaceElement(values, next); values[0].Target.Value = 1;
                    }
                    public static void RootStruct(Holder value) { value.Target.Value = 1; }
                    public static void StructReadThroughCopy(Data outside)
                    {
                        Holder local = default; ReplaceHolder(ref local, outside); ForwardCopy(local);
                    }
                    public static void BranchReplaced(Data outside, bool flag)
                    {
                        Data local = new Data();
                        if (flag) Replace(ref local, outside);
                        if (!flag) local.Value = 1;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == name).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, effects.Methods);
            Assert.IsTrue(effects.Methods.All(method => method.Kind == expected), System.Text.Json.JsonSerializer.Serialize(effects.Methods));
        }

        // 显式创建委托排除自动缓存，锁定旧审计命名实参和空可写语法的效果。
        /// <summary>源码与真实 DLL 共用正式参数槽，空 ref 和空属性 setter 不产生写入。</summary>
        [TestMethod]
        [DataRow("NamedOld", MethodEffectKind.Setter)]
        [DataRow("NamedFresh", MethodEffectKind.Getter)]
        [DataRow("EmptyRef", MethodEffectKind.Getter)]
        [DataRow("EmptyProperty", MethodEffectKind.Getter)]
        public async Task AnalyzeMatchesLegacyArgumentAndEmptyWriteCases(string name, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Box
                {
                    public int Value;
                    public int Ignored { set { } }
                }
                public delegate void Callback(Box x, Box y);
                public static class Calls
                {
                    private static int field;
                    private static void Change(Box x, Box y) { x.Value = 1; }
                    private static void Read(ref int value) { }
                    public static void NamedOld(Box outside) { Callback d = new Callback(Change); d(y: new Box(), x: outside); }
                    public static void NamedFresh(Box outside) { Callback d = new Callback(Change); d(y: outside, x: new Box()); }
                    public static void EmptyRef() { Read(ref field); }
                    public static void EmptyProperty(Box outside) { outside.Ignored = 1; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == name).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, effects.Methods);
            Assert.IsTrue(effects.Methods.All(method => method.Kind == expected));
        }

        // 一个根排除递归回边后，另一个根的同名递归仍不能借用它的返回证明。
        /// <summary>延迟条件先形成回边，再触发入口重查；只允许真实返回的根证明后序写入。</summary>
        [TestMethod]
        [DataRow(1, "normal")]
        [DataRow(4, "normal")]
        [DataRow(1, "native")]
        [DataRow(4, "native")]
        [DataRow(1, "changing")]
        [DataRow(4, "changing")]
        public async Task AnalyzeSeparatesRecursiveEntriesAfterDelayedPruning(int jobs, string form)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void First() { BEFORE if (Read(Gate())) state = 1; }
                    public static void Second() { if (Read(true)) state = 2; }
                    public static void Third() { if (Gate()) Unknown(); }
                    private static bool Gate() => Gate2();
                    private static bool Gate2() => Gate3();
                    private static bool Gate3() => false;
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                    private static extern void Unknown();
                    private static bool Read(bool again) { BODY }
                }
                """.Replace("BEFORE", form == "native" ? "Unknown();" : string.Empty).Replace("BODY", form switch
            {
                "changing" => "if (again) return Read(!again); return Read(true);",
                _ => "if (again) return Read(again); return true;",
            }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "First" or "Second" or "Third").ToArray();
            HashSet<string> firstRoots = roots.Where(root => root.Name == "First").Select(root => root.Id).ToHashSet();
            HashSet<string> observed = new();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, requireCompleteCalls: false,
                reportProgress: (snapshot, _) =>
                {
                    foreach (string root in firstRoots)
                    {
                        HashSet<int> children = snapshot.Calls.Where(call => call.CallerMethodId == root)
                            .SelectMany(call => call.Targets).Select(target => target.InstanceId).ToHashSet();
                        if (snapshot.Calls.Any(call => children.Contains(call.CallerInstanceId)
                            && call.Targets.Any(target => target.InstanceId == call.CallerInstanceId)))
                        {
                            observed.Add(root);
                        }
                    }
                });
            CollectionAssert.AreEquivalent(firstRoots.ToArray(), observed.ToArray());
            foreach (MethodEntry root in roots)
            {
                if (root.Name == "Third")
                {
                    Assert.AreEqual(MethodEffectKind.Getter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
                }
                else if (root.Name == "First" && form == "normal")
                {
                    Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
                }
                else
                {
                    Assert.Throws<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls));
                }
            }
        }

        // 子函数从未改写字段的出口返回时，读取调用者原值而非内部续查标记。
        /// <summary>数值字段与返回条件关联，未写入分支保留原存储内容。</summary>
        [TestMethod]
        [DataRow(1, MethodEffectKind.Setter)]
        [DataRow(2, MethodEffectKind.Getter)]
        public async Task AnalyzeReadsPreviousNumericStorageOnUnwrittenReturn(int expectedValue, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public int Value; }
                public static class Calls
                {
                    private static int state;
                    private static int Change(Holder holder, bool flag)
                    {
                        if (flag) { holder.Value = 2; return 1; }
                        return 0;
                    }
                    public static void Entry(bool flag)
                    {
                        var holder = new Holder { Value = 1 };
                        int result = Change(holder, flag);
                        if (result == 0 && holder.Value == EXPECTED) state = 1;
                    }
                }
                """.Replace("EXPECTED", expectedValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition change = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Change");
                change.Body.Instructions.Clear();
                change.Body.Variables.Clear();
                Mono.Cecil.Cil.ILProcessor writer = change.Body.GetILProcessor();
                Mono.Cecil.Cil.Instruction unchanged = writer.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Brfalse, unchanged);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_2);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, module.GetType("ExternalSamples.Holder").Fields.Single());
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                writer.Append(unchanged);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, effect.Kind, effect.MethodId);
            }
        }

        // 静态候选与内部对象字段的选择不能混为已经发生的静态修改。
        /// <summary>只有实际选中的字段为静态状态时才取得 Setter 证据。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeCorrelatesReflectedStaticMemberWrite(bool writesStatic)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public static System.Action Shared; public System.Action Local; }
                public static class Calls
                {
                    private static void Get() { }
                    public static void Entry(bool flag)
                    {
                        var holder = new Holder();
                        if (WRITE) typeof(Holder).GetField(flag ? "Shared" : "Local").SetValue(holder, new System.Action(Get));
                    }
                }
                """.Replace("WRITE", writesStatic ? "flag" : "!flag"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(writesStatic ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 条件选择写入字段时，未被选中的字段必须保留原回调。
        /// <summary>反射成员选择不能变成同时覆盖两个字段。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task AnalyzeCorrelatesReflectedMemberWrite(bool invokeSetter, bool duplicateMember)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public System.Action First; public System.Action Second; }
                public static class Calls
                {
                    private static int state;
                    private static void Set() { state = 1; }
                    private static void Get() { }
                    public static void Entry(bool flag)
                    {
                        var holder = new Holder { First = new System.Action(Set), Second = new System.Action(Get) };
                        LOOKUP.SetValue(holder, new System.Action(Get));
                        if (INVOKE) holder.First();
                    }
                }
                """.Replace("INVOKE", invokeSetter ? "!flag" : "flag").Replace("LOOKUP", duplicateMember
                    ? "(flag ? typeof(Holder).GetField(\"First\") : typeof(Holder).GetField(\"First\"))"
                    : "typeof(Holder).GetField(flag ? \"First\" : \"Second\")"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(invokeSetter && !duplicateMember ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 反射替换回调和后续调用必须出现在同一条实际执行路径。
        /// <summary>有条件的反射字段写入按普通存储处理，不拼接相反分支的委托。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeCorrelatesReflectedCallbackWriteAndInvocation(bool invokeSetter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public System.Action Next; }
                public static class Calls
                {
                    private static int state;
                    private static void Set() { state = 1; }
                    private static void Get() { }
                    private static void Replace(Holder holder, bool flag)
                    {
                        if (flag) typeof(Holder).GetField("Next").SetValue(holder, new System.Action(Set));
                    }
                    public static void Entry(bool flag)
                    {
                        var holder = new Holder(); holder.Next = new System.Action(Get);
                        Replace(holder, flag);
                        if (INVOKE) holder.Next();
                    }
                }
                """.Replace("INVOKE", invokeSetter ? "flag" : "!flag"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(invokeSetter ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 中间容器即便只有一次无条件赋值，也不能丢掉上层选择它的条件。
        /// <summary>三层容器只填充未被返回的叶子时为 Getter，填充被选叶子时为 Setter。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzePreservesAncestorSelectionThroughSingleStore(bool selected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static object Entry(bool flag)
                    {
                        var outer = new object[1];
                        var midA = new object[1]; var midB = new object[1];
                        var leafA = new object[1]; var leafB = new object[1];
                        outer[0] = flag ? midA : midB;
                        midA[0] = leafA; midB[0] = leafB;
                        if (SELECTED) leafA[0] = new Data(); else leafB[0] = new Data();
                        return outer;
                    }
                }
                """.Replace("SELECTED", selected ? "flag" : "!flag"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(selected ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 反射参数数组的长度也必须取实际返回值，不能直接使用窄返回之前的栈常量。
        /// <summary>截短为零的数组应调用无参目标，不能凭原始二百五十六构造虚假实参。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeNormalizesReflectedArgumentArrayLength(bool directArithmetic)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    private static byte Count() => 0;
                    public static void Touch() { state = 1; }
                    public static void Entry() => typeof(Calls).GetMethod("Touch").Invoke(null, new object[Count()]);
                }
                """.Replace("new object[Count()]", directArithmetic ? "new object[0]" : "new object[Count()]"));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition count = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Count");
                count.Body.Instructions.Clear();
                Mono.Cecil.Cil.ILProcessor writer = count.Body.GetILProcessor();
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, 256);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                if (directArithmetic)
                {
                    Mono.Cecil.MethodDefinition entry = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Entry");
                    Mono.Cecil.Cil.Instruction length = entry.Body.Instructions.Single(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Newarr).Previous;
                    Assert.AreEqual(Mono.Cecil.Cil.OpCodes.Ldc_I4_0, length.OpCode);
                    length.OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4_2;
                    foreach (Mono.Cecil.Cil.OpCode code in new[] { Mono.Cecil.Cil.OpCodes.Ldc_I4_3, Mono.Cecil.Cil.OpCodes.Add,
                        Mono.Cecil.Cil.OpCodes.Ldc_I4_4, Mono.Cecil.Cil.OpCodes.Ldc_I4_5, Mono.Cecil.Cil.OpCodes.Add,
                        Mono.Cecil.Cil.OpCodes.Mul, Mono.Cecil.Cil.OpCodes.Ldc_I4, Mono.Cecil.Cil.OpCodes.Sub })
                    {
                        Mono.Cecil.Cil.Instruction next = code == Mono.Cecil.Cil.OpCodes.Ldc_I4
                            ? Mono.Cecil.Cil.Instruction.Create(code, 45) : Mono.Cecil.Cil.Instruction.Create(code);
                        entry.Body.GetILProcessor().InsertAfter(length, next);
                        length = next;
                    }
                }
                module.Write(project.ExternalAssemblyPath);
            }
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                System.Reflection.Assembly assembly = execution.LoadFromStream(bytes);
                Type actual = assembly.GetType("ExternalSamples.Calls", throwOnError: true)!;
                actual.GetMethod("Entry")!.Invoke(null, null);
                Assert.AreEqual(1, actual.GetField("state", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Setter, effect.Kind, effect.MethodId);
            }
        }

        // 委托目标的构造分支必须和真正调用它的分支一致。
        /// <summary>全局可构造的 Setter 委托不能被拼到实际只调用 Getter 的路径上。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeCorrelatesDelegateSelectionAndInvocation(bool invokeSetter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    private static void Set() { state = 1; }
                    private static void Get() { }
                    public static void Entry(bool flag)
                    {
                        System.Action action = flag ? new System.Action(Set) : new System.Action(Get);
                        if (INVOKE) action();
                    }
                }
                """.Replace("INVOKE", invokeSetter ? "flag" : "!flag"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(invokeSetter ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 只有完整无条件的下标才可固化为位置，不能抽取某一条返回路径的常量。
        /// <summary>分支下标和仅部分正常返回的下标仍保留失败，不发布虚构的修改结论。</summary>
        [TestMethod]
        [DataRow("flag ? 0 : 1")]
        [DataRow("Pick(flag)")]
        [DataRow("flag ? Pick(flag) : 1")]
        public async Task AnalyzeKeepsConditionalArrayIndicesUnproven(string index)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    private static int Pick(bool flag) { if (flag) return 0; throw null; }
                    public static void Entry(bool flag)
                    {
                        var a = new int[2];
                        a[0] = 1;
                        a[INDEX] = 0;
                        if (a[0] == 1) state = 1;
                    }
                }
                """.Replace("INDEX", index));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                await Assert.ThrowsExactlyAsync<AnalysisException>(async () =>
                {
                    CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls);
                });
            }
        }

        // 同一个窄整数作为数组下标时也必须经过实际加载，不能把覆盖同一元素误认作不同位置。
        /// <summary>下标截成零后的第二次写入覆盖旧值，后面的静态写入不可达。</summary>
        [TestMethod]
        public async Task AnalyzeNormalizesNarrowArrayIndices()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { private static int state; public static void Entry() { byte i = 0; var a = new int[1]; a[0] = 1; a[i] = 0; if (a[0] == 1) state = 1; } }");
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.TypeDefinition type = module.GetType("ExternalSamples.Calls");
                Mono.Cecil.MethodDefinition entry = type.Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                entry.Body.Variables.Clear();
                entry.Body.Variables.Add(new Mono.Cecil.Cil.VariableDefinition(module.TypeSystem.Byte));
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, 256);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stloc_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Newarr, module.TypeSystem.Int32);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Dup);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stelem_I4);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Dup);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloc_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stelem_I4);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldelem_I4);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                Mono.Cecil.Cil.Instruction exit = writer.Create(Mono.Cecil.Cil.OpCodes.Ret);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Bne_Un, exit);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stsfld, type.Fields.Single());
                writer.Append(exit);
                module.Write(project.ExternalAssemblyPath);
            }
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                System.Reflection.Assembly assembly = execution.LoadFromStream(bytes);
                Type actual = assembly.GetType("ExternalSamples.Calls", throwOnError: true)!;
                actual.GetMethod("Entry")!.Invoke(null, null);
                Assert.AreEqual(0, actual.GetField("state", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 复刻 khengine 的两个行为树状态更新及 AI 对战计分函数，基类更新原实现也是空体。
        /// <summary>真实枚举比较和 switch 写法在源码与 DLL 中均取得修改证据。</summary>
        [TestMethod]
        public async Task AnalyzeHandlesKhengineEnumStateUpdates()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public enum BtNodeStatus { Inactive, Failure, Success, Running, Undefine }
                public class BtDecoratorNode
                {
                    protected BtNodeStatus m_nodeStatus;
                    protected int m_currChildIndex;
                    public virtual void UpdateWithChildrenStatus(BtNodeStatus childStatus) { }
                }
                public class BtReturnInvert : BtDecoratorNode
                {
                    public override void UpdateWithChildrenStatus(BtNodeStatus childStatus)
                    {
                        base.UpdateWithChildrenStatus(childStatus);
                        if (childStatus != BtNodeStatus.Running)
                        {
                            if (childStatus == BtNodeStatus.Failure)
                            {
                                m_nodeStatus = BtNodeStatus.Success;
                            }
                            else if (childStatus == BtNodeStatus.Success)
                            {
                                m_nodeStatus = BtNodeStatus.Failure;
                            }
                            else
                            {
                                m_nodeStatus = childStatus;
                            }
                        }
                        else
                        {
                            m_nodeStatus = childStatus;
                        }
                        m_currChildIndex++;
                    }
                }
                public class BtReturnSuccess : BtDecoratorNode
                {
                    public override void UpdateWithChildrenStatus(BtNodeStatus childStatus)
                    {
                        base.UpdateWithChildrenStatus(childStatus);
                        if (childStatus != BtNodeStatus.Running)
                        {
                            m_nodeStatus = BtNodeStatus.Success;
                        }
                        else
                        {
                            m_nodeStatus = childStatus;
                        }
                        m_currChildIndex++;
                    }
                }
                public sealed class AIChallengeBattleState
                {
                    public enum WinType { Right, Left, None }
                    public int scoreRightPlayer = 0;
                    public int scoreLeftPlayer = 0;
                    public void AddScore(WinType type)
                    {
                        switch (type)
                        {
                            case WinType.Right:
                                scoreRightPlayer += 1;
                                break;
                            case WinType.Left:
                                scoreLeftPlayer += 1;
                                break;
                            case WinType.None:
                                scoreRightPlayer += 1;
                                scoreLeftPlayer += 1;
                                break;
                        }
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name is "BtReturnInvert" or "BtReturnSuccess" or "AIChallengeBattleState")
                .SelectMany(catalog.GetMethods).Where(method => method.Name is "UpdateWithChildrenStatus" or "AddScore").ToArray();
            Assert.HasCount(6, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Setter, effect.Kind, effect.MethodId);
            }
        }

        // 实际存储和调用边界可以截断计算栈上的较宽整数，读取必须看见保存后的位宽。
        /// <summary>窄枚举不能沿用写入前整数并漏掉后续静态修改。</summary>
        [TestMethod]
        [DataRow(false, 256, 0, "field")]
        [DataRow(true, 255, -1, "field")]
        [DataRow(false, 256, 0, "argument")]
        [DataRow(true, 255, -1, "argument")]
        [DataRow(false, 256, 0, "return")]
        [DataRow(true, 255, -1, "return")]
        [DataRow(false, 256, 0, "local")]
        [DataRow(true, 255, -1, "local")]
        [DataRow(false, 256, 0, "array")]
        [DataRow(true, 255, -1, "array")]
        [DataRow(false, 256, 0, "array_typed")]
        [DataRow(true, 255, -1, "array_typed")]
        [DataRow(false, 256, 0, "argument_slot")]
        [DataRow(true, 255, -1, "argument_slot")]
        [DataRow(false, 256, 0, "field_widen")]
        [DataRow(true, 255, -1, "field_widen")]
        [DataRow(false, 256, 0, "local_widen")]
        [DataRow(true, 255, -1, "local_widen")]
        [DataRow(false, 256, 0, "array_widen")]
        [DataRow(true, 255, -1, "array_widen")]
        [DataRow(false, 256, 0, "return_widen")]
        [DataRow(true, 255, -1, "return_widen")]
        public async Task AnalyzeReadsNarrowEnumStorageAfterWideStore(bool signed, int stored, int expected, string boundary)
        {
            using TestProject project = TestProject.CreateWithCallTargets(("namespace Samples; public enum State : " + (signed ? "sbyte" : "byte")
                + " { A } public sealed class Data { public State Current; } public static class Calls { private static int state; private static State Make() => State.A; private static void Touch(State value) { if ((int)value == EXPECTED) state = 1; } public static void Entry() { state = 1; } }")
                .Replace("EXPECTED", expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.TypeDefinition type = module.GetType("ExternalSamples.Calls");
                Mono.Cecil.TypeDefinition data = module.GetType("ExternalSamples.Data");
                Mono.Cecil.MethodDefinition entry = type.Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                entry.Body.Variables.Clear();
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                Mono.Cecil.FieldDefinition wide = new("Wide", Mono.Cecil.FieldAttributes.Public, module.TypeSystem.Int32);
                if (boundary.EndsWith("_widen", StringComparison.Ordinal))
                {
                    data.Fields.Add(wide);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Newobj, data.Methods.Single(method => method.IsConstructor));
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Dup);
                }
                if (boundary.StartsWith("field", StringComparison.Ordinal))
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Newobj, data.Methods.Single(method => method.IsConstructor));
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Dup);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, data.Fields.Single(field => field.Name == "Current"));
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, data.Fields.Single(field => field.Name == "Current"));
                }
                else if (boundary.StartsWith("return", StringComparison.Ordinal))
                {
                    Mono.Cecil.MethodDefinition make = type.Methods.Single(method => method.Name == "Make");
                    make.Body.Instructions.Clear();
                    Mono.Cecil.Cil.ILProcessor returned = make.Body.GetILProcessor();
                    returned.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored);
                    returned.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Call, make);
                }
                else if (boundary.StartsWith("argument", StringComparison.Ordinal))
                {
                    Mono.Cecil.MethodDefinition touch = type.Methods.Single(method => method.Name == "Touch");
                    if (boundary == "argument_slot")
                    {
                        Mono.Cecil.Cil.ILProcessor called = touch.Body.GetILProcessor();
                        Mono.Cecil.Cil.Instruction first = touch.Body.Instructions[0];
                        called.InsertBefore(first, called.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored));
                        called.InsertBefore(first, called.Create(Mono.Cecil.Cil.OpCodes.Starg, touch.Parameters[0]));
                    }
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Call, touch);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                }
                else if (boundary.StartsWith("array", StringComparison.Ordinal))
                {
                    Mono.Cecil.TypeReference element = boundary == "array_typed" ? module.GetType("ExternalSamples.State")
                        : signed ? module.TypeSystem.SByte : module.TypeSystem.Byte;
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Newarr, element);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Dup);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Stelem_I1);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                    if (boundary == "array_typed")
                    {
                        writer.Emit(Mono.Cecil.Cil.OpCodes.Ldelem_Any, element);
                    }
                    else
                    {
                        writer.Emit(signed ? Mono.Cecil.Cil.OpCodes.Ldelem_I1 : Mono.Cecil.Cil.OpCodes.Ldelem_U1);
                    }
                }
                else
                {
                    entry.Body.Variables.Add(new Mono.Cecil.Cil.VariableDefinition(module.GetType("ExternalSamples.State")));
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Stloc_0);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldloc_0);
                }
                if (boundary.EndsWith("_widen", StringComparison.Ordinal))
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, wide);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, wide);
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, expected);
                Mono.Cecil.Cil.Instruction exit = writer.Create(Mono.Cecil.Cil.OpCodes.Ret);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Bne_Un, exit);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stsfld, type.Fields.Single());
                writer.Append(exit);
                module.Write(project.ExternalAssemblyPath);
            }
            System.Runtime.Loader.AssemblyLoadContext execution = new(null, isCollectible: true);
            try
            {
                using FileStream bytes = File.OpenRead(project.ExternalAssemblyPath);
                System.Reflection.Assembly assembly = execution.LoadFromStream(bytes);
                Type actual = assembly.GetType("ExternalSamples.Calls", throwOnError: true)!;
                actual.GetMethod("Entry")!.Invoke(null, null);
                Assert.AreEqual(1, actual.GetField("state", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            }
            finally
            {
                execution.Unload();
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Setter, effect.Kind, effect.MethodId);
            }
        }

        // 枚举使用真实底层整数范围，不把声明过的几个名字误当成全部可能值。
        /// <summary>枚举参数和公开字段共用整数分支判断，保留窄整数、无符号和未命名值。</summary>
        [TestMethod]
        [DataRow("int", "value == State.A", false, MethodEffectKind.Setter)]
        [DataRow("int", "value == State.A && value == State.B", false, MethodEffectKind.Getter)]
        [DataRow("byte", "(int)value > 255", false, MethodEffectKind.Getter)]
        [DataRow("sbyte", "(int)value < -128", false, MethodEffectKind.Getter)]
        [DataRow("ushort", "(int)value > 65535", false, MethodEffectKind.Getter)]
        [DataRow("short", "(int)value < -32768", false, MethodEffectKind.Getter)]
        [DataRow("byte", "(int)value == 200", false, MethodEffectKind.Setter)]
        [DataRow("sbyte", "(int)value == -127", false, MethodEffectKind.Setter)]
        [DataRow("uint", "value == (State)uint.MaxValue", false, MethodEffectKind.Setter)]
        [DataRow("long", "value == (State)long.MaxValue", false, MethodEffectKind.Setter)]
        [DataRow("ulong", "value == (State)ulong.MaxValue", false, MethodEffectKind.Setter)]
        [DataRow("int", "value == State.A", true, MethodEffectKind.Setter)]
        [DataRow("byte", "(int)value > 255", true, MethodEffectKind.Getter)]
        [DataRow("sbyte", "(int)value < -128", true, MethodEffectKind.Getter)]
        [DataRow("long", "value == (State)long.MaxValue", true, MethodEffectKind.Setter)]
        [DataRow("ulong", "value == (State)ulong.MaxValue", true, MethodEffectKind.Setter)]
        public async Task AnalyzeUsesUnderlyingEnumDomain(string underlying, string condition, bool field, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public enum State : " + underlying
                + " { A, B } public static class Shared { public static int Value; } public sealed class Calls { public State Current; public void Entry("
                + (field ? string.Empty : "State value") + ") { if (" + condition.Replace("value", field ? "Current" : "value") + ") Shared.Value = 1; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, effect.Kind, effect.MethodId);
            }
        }

        // 只读引用限制的是引用槽，不能据此忽略对象虚函数中的写入。
        /// <summary>只读与泛型引用参数共用实际类型和虚调用目标。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeTracksReadonlyAndGenericReferenceReceiver(bool generic)
        {
            string signature = generic ? "Entry<T>(ref T input) where T : Data" : "Entry(in Data input)";
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public class Data { public int Value; public virtual void Set() { Value = 1; } } public static class Calls { public static void "
                + signature + " { input.Set(); } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Setter, effect.Kind, effect.MethodId);
            }
        }

        // 引用参数所指的入口对象与保存该引用的槽都来自调用方，不能因加载形式丢失其归属。
        /// <summary>根引用参数的字段写入能够提供真实外部修改证据。</summary>
        [TestMethod]
        [DataRow("input.Value = 1;", MethodEffectKind.Setter)]
        [DataRow("if (input != null) input.Value = 1;", MethodEffectKind.Setter)]
        [DataRow("if (input == null) input.Value = 1;", MethodEffectKind.Getter)]
        [DataRow("if (input.Value == 0) input.Value = 1;", MethodEffectKind.Setter)]
        [DataRow("if (input.Value == 0 && input.Value != 0) input.Value = 1;", MethodEffectKind.Getter)]
        [DataRow("Write(input);", MethodEffectKind.Setter)]
        [DataRow("var fresh = new Data(); ref Data selected = ref (flag ? ref input : ref fresh); if (flag) selected.Value = 1;", MethodEffectKind.Setter)]
        [DataRow("var fresh = new Data(); ref Data selected = ref (flag ? ref input : ref fresh); if (!flag) selected.Value = 1;", MethodEffectKind.Getter)]
        [DataRow("var copy = input; Replace(ref copy); copy.Value = 1;", MethodEffectKind.Getter)]
        [DataRow("if (input != null) { var copy = input; if (copy == null) copy.Value = 1; }", MethodEffectKind.Getter)]
        public async Task AnalyzeTracksRootReferenceObjectFieldWrite(string body, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(ref Data input, bool flag) { BODY }
                    private static void Write(Data input) { input.Value = 1; }
                    private static void Replace(ref Data input) { input = new Data(); }
                }
                """.Replace("BODY", body));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, effect.Kind, effect.MethodId);
            }
        }

        // 固定选择值必须在展开调用前排除其他 case，未执行的原生函数不应阻塞入口。
        /// <summary>实际调用参数选择正常分支后，无关原生边界不进入报告。</summary>
        [TestMethod]
        [DataRow(2)]
        [DataRow(-1)]
        [DataRow(7)]
        public async Task AnalyzeExcludesUnselectedSwitchCalls(int key)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Runtime.InteropServices;
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    [DllImport("missing-native")] private static extern void Native();
                    public static void Entry() { Pick(KEY); state = 1; }
                    private static void Pick(int key)
                    {
                        switch (key)
                        {
                            case 0: Native(); break;
                            case 1: break;
                            case 2: break;
                            case 3: Native(); break;
                            default: break;
                        }
                    }
                }
                """.Replace("KEY", key.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition pick = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Pick");
                Mono.Cecil.Cil.Instruction table = pick.Body.Instructions.Single(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Switch);
                ((Mono.Cecil.Cil.Instruction[])table.Operand)[2] = table.Next;
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            MethodEntry[] switches = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Pick").ToArray();
            BehaviorReadResult bodies = await new BehaviorReader().ReadAsync(material, catalog, switches, 2);
            Assert.HasCount(2, bodies.Methods);
            Assert.IsTrue(bodies.Methods.All(body => body.Blocks.Any(block => block.SwitchTargetBlockIds != null)));
            Assert.IsTrue(bodies.Methods.Where(body => switches.Single(method => method.Id == body.MethodId).SourceSymbol == null)
                .All(body => body.Blocks.Any(block => block.SwitchTargetBlockIds != null
                && block.JumpTargetBlockId.HasValue && block.SwitchTargetBlockIds.Contains(block.JumpTargetBlockId.Value))));
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsFalse(calls.Calls.Any(call => call.Call.Target.Name == "Native"));
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 值类型经过引用复制后仍应保留与直接返回相同的业务对象来源。
        /// <summary>直接返回和通过条件引用返回同一种新建结构体，不能得到相反结论。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeFollowsReturnedStructReference(bool reference)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public struct Data { public int Value; public Data(int value) { Value = value; } }
                public static class Calls
                {
                    private static Data Make(int value) => new Data(value);
                    public static Data Entry(bool flag) { BODY }
                }
                """.Replace("BODY", reference ? "Data first = Make(1), second = Make(2); ref Data selected = ref (flag ? ref first : ref second); return selected;" : "return Make(1);"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 逐句复刻 KH_DoGSArg_Serializer.cs:8 的方法体，外部读入只替换为确定值。
        /// <summary>khengine 序列化分支中的字段赋值必须由实际 switch 路径证明为 Setter。</summary>
        [TestMethod]
        public async Task AnalyzeHandlesKhengineReadFieldSwitchPattern()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public class KFBReader
                {
                    public string ReadString() => "target";
                    public int ReadInt32() => 1;
                    public void SkipField() { }
                }
                public class KHScriptData { public virtual void ReadField(KFBReader reader, int fieldNumber) { } }
                public static class ObjectDecorator
                {
                    public static void ReadBaseFields(KFBReader reader, Action<KFBReader, int> read) => read(reader, -1);
                }
                public class DoGSArg : KHScriptData
                {
                    public string Target;
                    public int ScriptID;
                    public override void ReadField(KFBReader reader, int fieldNumber)
                    {
                        switch (fieldNumber)
                        {
                            case 0: ObjectDecorator.ReadBaseFields(reader, base.ReadField); break;
                            case 1: Target = reader.ReadString(); break;
                            case 2: ScriptID = reader.ReadInt32(); break;
                            default: reader.SkipField(); break;
                        }
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "DoGSArg").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "ReadField").ToArray();
            Assert.HasCount(2, roots);
            BehaviorReadResult bodies = await new BehaviorReader().ReadAsync(material, catalog, roots, 2);
            Assert.IsTrue(bodies.Methods.All(body => body.Blocks.Any(block => block.SwitchTargetBlockIds != null)));
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 密集分支表可能让多个编号共享目标，默认分支还包含负数和超界编号。
        /// <summary>沿原 switch 编号判定可达写入，不把同目标分支或默认分支合并成无条件。</summary>
        [TestMethod]
        [DataRow(0, false)]
        [DataRow(5, false)]
        [DataRow(-3, false)]
        [DataRow(0, true)]
        public async Task AnalyzeUsesSwitchCaseAndDefaultConditions(int offset, bool write)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry(int selector)
                    {
                        int key = selector - OFFSET;
                        switch (key)
                        {
                            case 0: case 2: if (key != 0 && key != 2) state = 1; break;
                            case 1: case 3: if (key != 1 && key != 3) state = 1; break;
                            case 4: WRITE break;
                            case 5: if (key != 5) state = 1; break;
                            default: if (key >= 0 && key <= 5) state = 1; break;
                        }
                    }
                }
                """.Replace("OFFSET", offset.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("WRITE", write ? "state = 1;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            BehaviorReadResult bodies = await new BehaviorReader().ReadAsync(material, catalog, roots, 2);
            Assert.IsTrue(bodies.Methods.All(body => body.Values.Any(value => value.Reference == "switch")));
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(write ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind, effect.MethodId);
            }
        }

        // 返回引用所指的对象和直接返回对象应有完全相同的日志含义。
        /// <summary>条件引用不丢失新对象，互斥返回路径也不能借另一支的新对象作证。</summary>
        [TestMethod]
        [DataRow(false, MethodEffectKind.Setter)]
        [DataRow(true, MethodEffectKind.Getter)]
        public async Task AnalyzeFollowsReturnedManagedReference(bool returnExisting, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static Data Entry(Data external, bool flag)
                    {
                        Data first = new Data(), second = SECOND;
                        ref Data selected = ref (flag ? ref first : ref second);
                        EXIT
                        return selected;
                    }
                }
                """.Replace("SECOND", returnExisting ? "external" : "new Data()")
                .Replace("EXIT", returnExisting ? "if (flag) return external;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, effect.Kind, effect.MethodId);
            }
        }

        // 真实托管指令允许把较宽栈值截入窄存储，读取必须执行对应符号扩展。
        /// <summary>实际 DLL 的窄整数间接加载不能直接沿用截断前的整数。</summary>
        [TestMethod]
        [DataRow(false, false, 256, 0, 0)]
        [DataRow(false, true, 255, -1, 0)]
        [DataRow(true, false, 65536, 0, 0)]
        [DataRow(true, true, 65535, -1, 0)]
        [DataRow(false, false, 256, 0, 1)]
        [DataRow(false, true, 255, -1, 1)]
        [DataRow(true, false, 65536, 0, 1)]
        [DataRow(true, true, 65535, -1, 1)]
        [DataRow(false, false, 256, 0, 2)]
        [DataRow(false, true, 255, -1, 2)]
        [DataRow(true, false, 65536, 0, 2)]
        [DataRow(true, true, 65535, -1, 2)]
        [DataRow(false, false, 256, 0, 3)]
        [DataRow(false, true, 255, -1, 3)]
        [DataRow(true, false, 65536, 0, 3)]
        [DataRow(true, true, 65535, -1, 3)]
        [DataRow(false, false, 256, 0, 4)]
        public async Task AnalyzePreservesNarrowIndirectLoadWidth(bool wide, bool signed, int stored, int expected, int load)
        {
            string scalar = wide ? signed ? "short" : "ushort" : signed ? "sbyte" : "byte";
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public enum Narrow : " + scalar
                + " { Zero = 0 } public static class Calls { private static int state; private static T Load<T>(ref T value) => value; public static void Entry() { state = 1; } }");
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.TypeDefinition type = module.GetType("ExternalSamples.Calls");
                Mono.Cecil.MethodDefinition entry = type.Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                entry.Body.Variables.Clear();
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                Mono.Cecil.TypeReference element = wide ? signed ? module.TypeSystem.Int16 : module.TypeSystem.UInt16
                    : signed ? module.TypeSystem.SByte : module.TypeSystem.Byte;
                if (load is 2 or 4)
                {
                    element = module.GetType("ExternalSamples.Narrow");
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Newarr, element);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldelema, element);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Dup);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, stored);
                writer.Emit(wide ? Mono.Cecil.Cil.OpCodes.Stind_I2 : Mono.Cecil.Cil.OpCodes.Stind_I1);
                if (load == 0)
                {
                    writer.Emit(wide ? signed ? Mono.Cecil.Cil.OpCodes.Ldind_I2 : Mono.Cecil.Cil.OpCodes.Ldind_U2
                        : signed ? Mono.Cecil.Cil.OpCodes.Ldind_I1 : Mono.Cecil.Cil.OpCodes.Ldind_U1);
                }
                else if (load >= 3)
                {
                    Mono.Cecil.GenericInstanceMethod generic = new(type.Methods.Single(method => method.Name == "Load"));
                    generic.GenericArguments.Add(element);
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Call, generic);
                }
                else
                {
                    writer.Emit(Mono.Cecil.Cil.OpCodes.Ldobj, element);
                }
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, expected);
                Mono.Cecil.Cil.Instruction exit = writer.Create(Mono.Cecil.Cil.OpCodes.Ret);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Bne_Un, exit);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stsfld, type.Fields.Single(field => field.Name == "state"));
                writer.Append(exit);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(MethodEffectKind.Setter, effect.Kind, effect.MethodId);
            }
        }

        // 同一数组元素通过参数或局部引用读取时，必须看见数组的实际改写。
        /// <summary>区分引用变量本身与其所指内容，不能用局部槽快排隐藏 Setter。</summary>
        [TestMethod]
        [DataRow("parameter", MethodEffectKind.Setter)]
        [DataRow("parameter_written", MethodEffectKind.Setter)]
        [DataRow("mixed_array", MethodEffectKind.Setter)]
        [DataRow("mixed_local", MethodEffectKind.Getter)]
        [DataRow("write_array", MethodEffectKind.Getter)]
        [DataRow("write_local", MethodEffectKind.Setter)]
        public async Task AnalyzePreservesArrayWritesVisibleThroughManagedReferences(string form, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry(bool flag)
                    {
                        var array = new int[] { 1 };
                        BODY
                    }
                    private static void Touch(ref int value, int[] array)
                    {
                        INITIALIZE
                        array[0] ^= 1;
                        if (value == 0) state = 1;
                    }
                }
                """.Replace("INITIALIZE", form == "parameter_written" ? "value = 1;" : string.Empty)
                .Replace("BODY", form.StartsWith("parameter", StringComparison.Ordinal) ? "Touch(ref array[0], array);"
                    : "int local = 1; ref int value = ref (flag ? ref local : ref array[0]); "
                        + (form.StartsWith("write", StringComparison.Ordinal) ? "value = 0; ref int read = ref local;" : "array[0] ^= 1;")
                        + " if (" + (form is "mixed_array" or "write_array" ? "!flag" : "flag")
                        + " && " + (form.StartsWith("write", StringComparison.Ordinal) ? "read" : "value") + " == 0) state = 1;"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(expected, effect.Kind, effect.MethodId);
            }
        }

        // 普通数组元素读取也必须使用返回该数组的分支来判定对象归属。
        /// <summary>不能借未返回数组中保存的外部对象，把实际的新对象写入误报为 Setter。</summary>
        [TestMethod]
        public async Task AnalyzeUsesReturnPathWhenReadingAnElement()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data external, bool flag) { Pick(external, flag)[0].Value = 1; }
                    private static Data[] Pick(Data external, bool flag)
                    {
                        var a = new Data[1]; var b = new Data[] { new Data() };
                        if (flag) { a[0] = external; return b; }
                        a[0] = new Data(); return a;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 动态调用的一种实现返回数组，另一种实现的修改不能混入其返回内容。
        /// <summary>同一个调用点的候选实现互斥，返回证据和存储效果必须使用相同目标。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsReturningDispatchTargetWithItsWrites()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public interface IFactory { object Pick(object[] values); }
                public sealed class Keep : IFactory { public object Pick(object[] values) { return values; } }
                public sealed class Discard : IFactory { public object Pick(object[] values) { values[0] = new Data(); return null; } }
                public static class Calls
                {
                    public static object Entry(IFactory factory) { var values = new object[1]; return factory.Pick(values); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 同一数组可由多条出口传出，内容不同的返回路径都必须检查。
        /// <summary>不能因为先访问了空数组路径，就跳过同一数组传出新对象的路径。</summary>
        [TestMethod]
        public async Task AnalyzeChecksEveryPathReturningTheSameArray()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static object Entry(bool flag) { return Pick(flag); }
                    private static object Pick(bool flag)
                    {
                        var values = new object[1];
                        if (flag) return values;
                        values[0] = new Data();
                        return values;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 返回数组时只查看真正传出该数组的执行分支。
        /// <summary>不返回容器的分支创建业务对象，不构成该函数传出对象的证明。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeKeepsContainerContentsOnReturningPath(bool useFinally, bool helper)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    WRAPPER
                    public static object METHOD(bool flag)
                    {
                        var values = new object[1];
                        BODY
                    }
                }
                """.Replace("WRAPPER", helper ? "public static object Entry(bool flag) { return Pick(flag); }" : string.Empty)
                .Replace("METHOD", helper ? "Pick" : "Entry").Replace("BODY", useFinally
                    ? "try { return values; } finally { values[0] = new Data(); }"
                    : "if (flag) { values[0] = new Data(); return null; } return values;"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (useFinally ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 嵌套容器沿同一返回路径观察，之后的普通修改调用仍须计算。
        /// <summary>返回位置约束不能遗漏内层容器，也不能过滤无关调用的真实写入。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeCarriesReturnPathIntoNestedContainers(bool populate)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static object[] Entry(bool flag)
                    {
                        var values = Pick(flag);
                        POPULATE
                        return values;
                    }
                    private static object[] Pick(bool flag)
                    {
                        var inner = new object[1]; var values = new object[] { inner };
                        if (flag) { inner[0] = new Data(); return null; }
                        return values;
                    }
                    private static object Populate(object[] values)
                    {
                        if (values != null) { values[0] = new Data(); return null; }
                        return values;
                    }
                }
                """.Replace("POPULATE", populate ? "Populate(values);" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (populate ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 同一辅助函数在不同对象值下会走不同分支，不能把不会执行的写入传播给上层。
        /// <summary>仅排除已证明不成立的条件，未知条件与无符号比较仍保留真实写入。</summary>
        [TestMethod]
        [DataRow("", "holder.Value > 0", false)]
        [DataRow("holder.Value = 1;", "holder.Value > 0", true)]
        [DataRow("holder.Value = -1;", "holder.Value > 0", false)]
        [DataRow("holder.Value = -1;", "(uint)holder.Value > 0", true)]
        [DataRow("", "1 > holder.Value", true)]
        [DataRow("holder.Value = 2;", "1 > holder.Value", false)]
        [DataRow("if (flag) holder.Value = 1;", "holder.Value > 0", true)]
        public async Task AnalyzeUsesKnownFieldValuesToExcludeImpossibleWrites(string assignment, string condition, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public int Value; }
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data data, bool flag)
                    {
                        var holder = new Holder();
                        ASSIGNMENT
                        Check(holder, data);
                    }
                    private static void Check(Holder holder, Data data) { if (CONDITION) data.Value = 1; }
                }
                """.Replace("ASSIGNMENT", assignment).Replace("CONDITION", condition));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (setter ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 条件引用可能写到另一个数组，不能把候选过滤成必然覆盖当前数组。
        /// <summary>间接地址合流保留全部目标，未被覆盖的新对象仍构成 Setter 证据。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeRetainsOldArrayValueUnderConditionalReferenceWrite(bool create)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static Data[] Entry(bool flag, Data existing)
                    {
                        var a = new Data[1]; var b = new Data[1];
                        a[0] = ITEM;
                        ref Data slot = ref (flag ? ref a[0] : ref b[0]);
                        slot = existing;
                        return a;
                    }
                }
                """.Replace("ITEM", create ? "new Data()" : "existing"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (create ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 返回分支可选择不同新数组，每份数组均按返回时的精确内容判断。
        /// <summary>合流不会遮挡其中一份数组传出的新对象，后续覆盖仍消除传出证据。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeReadsEachReturnedArrayAfterBranchMerge(bool overwrite)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static Data[] Entry(bool first, Data existing)
                    {
                        var left = new Data[1]; var right = new Data[1];
                        left[0] = new Data();
                        OVERWRITE
                        return first ? left : right;
                    }
                }
                """.Replace("OVERWRITE", overwrite ? "left[0] = existing;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (overwrite ? MethodEffectKind.Getter : MethodEffectKind.Setter)));
        }

        // 新数组只含默认空引用，未知长度不会凭空创建业务对象。
        /// <summary>判断返回容器只需追踪实际保存的内容，不要求枚举全部空元素。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task AnalyzeDoesNotRequireLengthOfEmptyReturnedArray(bool scratch, bool writeExisting)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static Data[] Entry(int count, int index, Data existing)
                    {
                        SCRATCH
                        var result = new Data[count];
                        WRITE
                        return result;
                    }
                }
                """.Replace("SCRATCH", scratch ? "var scratch = new Data[2]; scratch[index] = existing;" : string.Empty)
                .Replace("WRITE", writeExisting ? "result[0] = existing;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 同一返回容器有一个未知字段时，仍检查其他字段是否已确定传出新业务对象。
        /// <summary>独立传出证据不会被前一个字段遮挡，没有证据时仍保持未知。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeKeepsKnownPayloadBesideUnknownContainerField(bool withProof, bool uncertainWrite)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Runtime.CompilerServices;
                namespace Samples;
                public sealed class Data { }
                [CompilerGenerated] public sealed class Carrier { public Data[] Items; public Data Unknown; public Data Known; }
                public static class Calls
                {
                    public static object Entry(int count, Data first, Data second)
                    {
                        var carrier = new Carrier();
                        UNKNOWN
                        PROOF
                        return carrier;
                    }
                }
                """.Replace("PROOF", withProof ? "carrier.Known = new Data();" : string.Empty)
                .Replace("UNKNOWN", uncertainWrite
                    ? "var other = new Carrier(); var target = count == 0 ? carrier : other; target.Unknown = count == 0 ? first : second;"
                    : "carrier.Items = new Data[2]; carrier.Items[count] = first;"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            if (withProof)
            {
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            }
            else
            {
                Assert.Contains(uncertainWrite ? "对象与保存值的分支对应尚未闭合" : "写入位置尚未闭合",
                    Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, calls)).Message);
            }
        }

        // 编译器生成的包装对象不算业务对象，但其实际传出的字段可能包含新业务对象。
        /// <summary>容器字段通过泛型函数写入并覆盖时，按返回时内容判断传出。</summary>
        [TestMethod]
        [DataRow(false, false, "field")]
        [DataRow(true, false, "field")]
        [DataRow(true, true, "field")]
        [DataRow(false, false, "array")]
        [DataRow(true, false, "array")]
        [DataRow(true, true, "array")]
        [DataRow(false, false, "ref")]
        [DataRow(true, false, "ref")]
        [DataRow(true, true, "ref")]
        public async Task AnalyzeReadsBusinessObjectsInsideReturnedCarrier(bool create, bool overwrite, string storage)
        {
            bool array = storage == "array";
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Runtime.CompilerServices;
                namespace Samples;
                public sealed class Data { }
                [CompilerGenerated] public sealed class Carrier<T> { public T Item; }
                public static class Calls
                {
                    public static object Entry(Data existing)
                    {
                        var carrier = new Carrier<VALUE>();
                        Fill(carrier, ITEM);
                        OVERWRITE
                        return carrier;
                    }
                    private static void Fill<T>(Carrier<T> carrier, T item) { carrier.Item = item; }
                }
                """.Replace("{ carrier.Item = item; }", storage == "ref"
                    ? "{ ref T slot = ref carrier.Item; slot = item; }" : "{ carrier.Item = item; }")
                .Replace("VALUE", array ? "Data[]" : "Data")
                .Replace("ITEM", array ? "new[] { " + (create ? "new Data()" : "existing") + " }" : create ? "new Data()" : "existing")
                .Replace("OVERWRITE", overwrite ? array ? "Fill(carrier, new[] { existing });" : "Fill(carrier, existing);" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (create && !overwrite ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 复刻 FindObjectFrame 的父级循环、局部合流和虚属性读取，查询对象只返回已有帧。
        /// <summary>循环中的接收对象来源须算完，out 写入仍准确证明为 Setter。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AnalyzeClosesVirtualReadInsideParentSearch(bool throughWrapper, bool writingOverride)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Frame { }
                public sealed class FrameMap
                {
                    public Frame Value;
                    public bool TryGetValue(int key, out Frame frame) { frame = Value; return frame != null; }
                }
                public sealed class Data { public FrameMap objectFrames; }
                public sealed class FollowInfo { public Actor parent; }
                public sealed class RecoverData { public Actor targetPlayer; }
                public class Model { public bool Used; }
                public sealed class PlayerModel : Model { public RecoverData transferNinjaRecoverData; }
                public class Actor
                {
                    protected FollowInfo m_followInfo;
                    public FollowInfo FollowInfo { get { return m_followInfo; } set { m_followInfo = value; } }
                    private Model m_model;
                    public Model model { get { return m_model; } }
                    public Actor FollowParent
                    {
                        get
                        {
                            if (FollowInfo != null && FollowInfo.parent != null && FollowInfo.parent.Used
                                && FollowInfo.parent.model != null && FollowInfo.parent.model.Used)
                            {
                                PlayerModel playerModel = FollowInfo.parent.model as PlayerModel;
                                if (playerModel != null && playerModel.Used && playerModel.transferNinjaRecoverData != null
                                    && playerModel.transferNinjaRecoverData.targetPlayer != null && playerModel.transferNinjaRecoverData.targetPlayer.Used)
                                {
                                    return playerModel.transferNinjaRecoverData.targetPlayer;
                                }
                            }
                            return FollowInfo == null ? null : FollowInfo.parent;
                        }
                    }
                    public bool Used;
                    protected Data m_actorData;
                    public virtual Data ActorData { get { return m_actorData; } }
                }
                public sealed class ItemActor : Actor
                {
                    public Actor Parent;
                    public override Data ActorData => base.ActorData;
                    public Actor getParentTarget() => Parent;
                }
                public sealed class PlayerActor : Actor { public override Data ActorData => base.ActorData; }
                public static class Calls
                {
                    public static Frame Entry(Actor start, int frameIndex, bool recursive, out Actor ownerActor)
                    {
                        ownerActor = null;
                        if (frameIndex == 0 || IsNull(start)) return null;
                        Actor cur = start;
                        int guard = 5;
                        while (!IsNull(cur) && guard-- > 0)
                        {
                            Frame frameData = null;
                            cur.ActorData?.objectFrames?.TryGetValue(frameIndex, out frameData);
                            if (frameData != null) { ownerActor = cur; return frameData; }
                            if (!recursive) return null;
                            Actor parent = null;
                            if (cur is ItemActor itemActor) parent = itemActor.getParentTarget();
                            if (IsNull(parent)) parent = cur.FollowParent;
                            if (IsNull(parent) || parent == cur) return null;
                            cur = parent;
                        }
                        return null;
                    }
                    private static bool IsNull(Actor target) => target == null || !target.Used;
                }
                """.Replace("public override Data ActorData => base.ActorData;", writingOverride
                    ? "public override Data ActorData { get { m_actorData = null; return base.ActorData; } }"
                    : "public override Data ActorData => base.ActorData;")
                .Replace("public static Frame Entry(Actor start, int frameIndex, bool recursive, out Actor ownerActor)", throughWrapper
                    ? "public static Frame Entry(Actor start, int frameIndex, bool recursive) => Find(start, frameIndex, recursive, out _); private static Frame Find(Actor start, int frameIndex, bool recursive, out Actor ownerActor)"
                    : "public static Frame Entry(Actor start, int frameIndex, bool recursive, out Actor ownerActor)"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (throughWrapper && !writingOverride ? MethodEffectKind.Getter : MethodEffectKind.Setter)));
        }

        // 源码和实际 DLL 使用相同对象选择，参数映射后仍须对应同一次写入。
        /// <summary>局部、下层参数和字段所有者的选择不能拼成另一条修改路径。</summary>
        [TestMethod]
        [DataRow("var target = flag ? new Data() : old; if (flag) target.Value = 1;", false)]
        [DataRow("var target = old; if (flag) target = new Data(); if (flag) target.Value = 1;", false)]
        [DataRow("var target = flag ? old : new Data(); if (!flag) target.Value = 1;", false)]
        [DataRow("var target = flag ? new Data() : old; if (!flag) target.Value = 1;", true)]
        [DataRow("var target = flag ? new Data() : old; target.Value = 1;", true)]
        [DataRow("var target = flag ? new Data() : old; Write(target, flag);", false)]
        [DataRow("var target = flag ? new Data() : old; Pick(target, new Data(), flag);", false)]
        [DataRow("var target = flag ? old : new Data(); Pick(target, new Data(), flag);", true)]
        [DataRow("var target = flag ? new Data() : old; target.Next.Value = 1;", true)]
        [DataRow("var target = flag ? null : old; if (flag) target.Value = 1;", false)]
        [DataRow("var holder = new Data { Next = old }; holder.Next.Value = 1;", true)]
        [DataRow("var values = new[] { old }; values[0].Value = 1;", true)]
        [DataRow("var holder = new Data { Next = old }; var target = holder.Next; holder.Next = new Data(); target.Value = 1;", true)]
        [DataRow("var target = flag ? new Data() : old; Choose(target, new Data(), flag).Value = 1;", false)]
        [DataRow("var target = flag ? old : new Data(); Choose(target, new Data(), flag).Value = 1;", true)]
        [DataRow("var target = flag ? old : new Data(); if (!flag && old != null) target.Value = 1;", false)]
        [DataRow("var target = flag ? old : new Data(); if (flag && old != null) target.Value = 1;", true)]
        [DataRow("int value = flag ? 1 : 0; var target = flag ? new Data() : old; if (value != 0 && old != null) target.Value = 1;", false)]
        [DataRow("var target = flag ? old : new Data(); var snapshot = old; old = null; if (flag && snapshot != null) target.Value = 1;", true)]
        [DataRow("var target = flag ? old : new Data(); if (flag && old == null) target.Value = 1;", false)]
        [DataRow("if (old == null) old.Value = 1;", false)]
        [DataRow("var target = flag ? old.Next : new Data(); if (flag && old.Next == null) target.Value = 1;", false)]
        [DataRow("if (old.Next.Next == null) old.Next.Next.Value = 1;", false)]
        [DataRow("if (old.Next.Next != null) old.Next.Next.Value = 1;", true)]
        [DataRow("var target = old.Value > 0 ? new Data() : old; if (old.Value > 0) target.Value = 1;", false)]
        [DataRow("var target = old.Value > 0 ? old : new Data(); if (old.Value > 0) target.Value = 1;", true)]
        [DataRow("var holder = flag ? new Data { Next = new Data() } : old.Next; var target = flag ? new Data() : holder.Next; target.Value = 1;", true)]
        [DataRow("bool b = old.Value > 0; var holder = flag ? new Data { Next = new Data() } : old.Next; var target = b ? new Data() : holder.Next; target.Value = 1;", true)]
        public async Task AnalyzePreservesObjectSelectionAcrossSourceAndDll(string body, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public int Value; public Data Next; } public static class Calls { private static void Write(Data target, bool flag) { if (flag) target.Value = 1; } private static void Pick(Data a, Data b, bool flag) { (flag ? a : b).Value = 1; } private static Data Choose(Data a, Data b, bool flag) => flag ? a : b; public static void Entry(bool flag, Data old) { " + body + " } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (setter ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 引用转换不产生另一对象，字段条件与接收对象必须使用相同来源。
        /// <summary>转换后的旧对象与新对象混合时，源码和 DLL 都保留合法修改。</summary>
        [TestMethod]
        public async Task AnalyzePreservesCastReceiverIdentityInFieldConditions()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public bool Enabled; public int Value; } public static class Calls { public static void Entry(object input, bool flag) { var d = (Data)input; var target = flag ? new Data() : d; if (d.Enabled) target.Value = 1; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 同一对象不能同时满足不相容的类型，后续失败也不能抹去前序修改。
        /// <summary>类型转换的正常完成条件与原执行位置一同决定写入是否可达。</summary>
        [TestMethod]
        [DataRow("((A)(object)input).Value = 1;", "B", false)]
        [DataRow("if (((A)input).Flag && ((B)input).Flag) state = 1;", "object", false)]
        [DataRow("if (input != null) { var a = (A)input; var b = (B)input; state = 1; }", "object", false)]
        [DataRow("var a = (A)input; var b = (B)input; state = 1;", "object", true)]
        [DataRow("state = 1; var a = (A)input; var b = (B)input;", "object", true)]
        [DataRow("if (input is int) ((A)input).Value = 1;", "object", false)]
        [DataRow("if (!(input is A)) input.Value = 1;", "A", false)]
        [DataRow("if (!(Pass(input) is A)) input.Value = 1;", "A", false)]
        [DataRow("if (input is IBox<int>) state = 1;", "object", true)]
        [DataRow("if (input is IBox<int> && input is IBox<string>) state = 1;", "object", false)]
        [DataRow("if (input is IMarker) state = 1;", "IBox<string>", false)]
        [DataRow("if (input is IMarker) state = 1;", "IBox<int>", true)]
        public async Task AnalyzeJoinsReferenceTypeRequirementsAtWritePoint(string body, string inputType, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public interface IBox<T> { } public interface IMarker { } public sealed class StructBox<T> : IBox<T>, IMarker where T : struct { } public sealed class Box<T> : IBox<T> { } public sealed class A { public bool Flag; public int Value; } public sealed class B { public bool Flag; } public static class Calls { private static int state; private static object Pass(A value) => value; public static void Entry(" + inputType + " input) { " + body + " } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 动态目标必须与实际调用分支一致，不能借用另一分支的写入实现。
        /// <summary>普通接口和反射字段回调都按同一条实际选择路径传播修改。</summary>
        [TestMethod]
        [DataRow(0, "", true)]
        [DataRow(0, "if (flag)", true)]
        [DataRow(0, "if (!flag)", false)]
        [DataRow(1, "", true)]
        [DataRow(1, "if (flag)", true)]
        [DataRow(1, "if (!flag)", false)]
        [DataRow(2, "", true)]
        [DataRow(2, "if (flag)", true)]
        [DataRow(2, "if (!flag)", false)]
        [DataRow(3, "", true)]
        [DataRow(3, "if (flag)", true)]
        [DataRow(3, "if (!flag)", false)]
        [DataRow(4, "", true)]
        [DataRow(4, "if (flag)", true)]
        [DataRow(4, "if (!flag)", false)]
        public async Task AnalyzeKeepsDispatchTargetOnItsActualBranch(int lookupMode, string guard, bool setter)
        {
            string selection = lookupMode != 0
                ? "var data = new Data { A = new Writer(), B = new Quiet() }; var field = flag ? typeof(Data).GetField(\"A\") : typeof(Data).GetField(\"B\"); GUARD ((IRun)field.GetValue(data)).Run();"
                : "IRun target = flag ? new Writer() : new Quiet(); GUARD target.Run();";
            if (lookupMode == 2)
            {
                selection = selection.Replace("flag ? typeof(Data).GetField(\"A\") : typeof(Data).GetField(\"B\")",
                    "typeof(Data).GetField(flag ? \"A\" : \"B\")");
            }
            if (lookupMode == 3)
            {
                selection = "IRun target = new Quiet(); Replace(ref target, new Writer(), flag); GUARD target.Run();";
            }
            if (lookupMode == 4)
            {
                selection = "var data = new Data { A = new Writer(), B = new Quiet() }; var type = flag ? typeof(Data) : typeof(Data); var field = type.GetField(flag ? \"A\" : \"B\"); GUARD ((IRun)field.GetValue(data)).Run();";
            }
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public interface IRun { void Run(); } public sealed class Writer : IRun { private static int state; public void Run() { state = 1; } } public sealed class Quiet : IRun { public void Run() { } } public sealed class Data { public IRun A; public IRun B; } public static class Calls { private static void Replace(ref IRun target, IRun next, bool flag) { if (flag) target = next; } public static void Entry(bool flag) { " + selection.Replace("GUARD", guard) + " } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 外层容器选择哪个内层容器的条件要一直保留到最里面的业务对象。
        /// <summary>两个分支分别填充未返回的内数组，不产生业务对象输出。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeKeepsNestedContainerSelection(bool throughHelper)
        {
            string fill = "if (flag) { outer[0] = a; b[0] = new Data(); } else { outer[0] = b; a[0] = new Data(); }";
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { } public static class Calls { private static void Fill(object[] outer, object[] a, object[] b, bool flag) { " + fill + " } public static object Entry(bool flag) { var outer = new object[1]; var a = new object[1]; var b = new object[1]; " + (throughHelper ? "Fill(outer, a, b, flag);" : fill) + " return outer; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Getter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 同一内层容器经不同槽传出时，分别保留对应分支，不以分配位置合并观察。
        /// <summary>交换分支出现顺序不改变第二个槽携带业务对象的真实输出。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeKeepsSeparateContainerObservations(bool reverse)
        {
            string first = "outer[0] = inner;";
            string second = "outer[1] = inner; inner[0] = new Data();";
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { } public static class Calls { public static object Entry(bool flag) { var outer = new object[2]; var inner = new object[1]; if (flag) { " + (reverse ? second : first) + " } else { " + (reverse ? first : second) + " } return outer; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 返回对象图中的回边只重复同一出口下已观察的对象，不阻止读取其它槽。
        /// <summary>有条件的自引用和相互引用容器均终止，并继续识别其它位置的业务对象。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task AnalyzeTerminatesCyclicReturnedContainers(bool mutual, bool business)
        {
            string link = mutual ? "var inner = new object[1]; outer[0] = inner; inner[0] = outer;" : "outer[0] = outer;";
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { } public static class Calls { public static object Entry(bool flag) { var outer = new object[2]; if (flag) { " + link + " } " + (business ? "if (!flag) outer[1] = new Data();" : string.Empty) + " return outer; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(business ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 返回槽里已有新对象，不代表退出前的清理代码允许它真正传出。
        /// <summary>finally 抛出条件与实际返回出口保持一致。</summary>
        [TestMethod]
        [DataRow("flag", false)]
        [DataRow("!flag", true)]
        public async Task AnalyzeRequiresActualReturnAfterFinally(string condition, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { } public static class Calls { public static object Entry(bool flag) { try { if (flag) return new Data(); return null; } finally { if (" + condition + ") throw null; } } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 静态反射读取没有实例接收对象，仍按公开静态存储的实际输入范围判断。
        /// <summary>静态字段 GetValue 的结果参与条件时不索引空接收对象列表。</summary>
        [TestMethod]
        public async Task AnalyzeReadsStaticReflectionFieldInCondition()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public int Value; } public static class Holder { public static object Value; } public static class Calls { public static void Entry(Data old) { object value = typeof(Holder).GetField(\"Value\").GetValue(null); if (value != null) old.Value = 1; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 容器被返回时，必须选择同一路径上实际留在容器里的业务对象。
        /// <summary>另一分支装入的业务对象不能与空容器出口拼接成修改证据。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeJoinsContainerWriteAndReturnConditions(bool throughHelper)
        {
            string assignment = throughHelper ? "Fill(values, flag);" : "if (flag) values[0] = new Data();";
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { } public static class Calls { private static void Fill(object[] values, bool flag) { if (flag) values[0] = new Data(); } public static object Entry(bool flag) { var values = new object[1]; " + assignment + " if (flag) return null; return values; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Getter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 递归回边不属于首轮正常返回见证，不能用截断后的路径证明后续写入。
        /// <summary>只有递归返回之后才可能发生的写入仍须取得真实完成证明。</summary>
        [TestMethod]
        [DataRow("Entry(flag); state = 1;")]
        [DataRow("if (flag) Entry(true); if (flag) state = 1;")]
        public async Task AnalyzeDoesNotInventRecursiveContinuation(string body)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { private static int state; public static void Entry(bool flag) { " + body + " } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.Throws<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls));
            }
        }

        // 没有写入的子函数仍能通过抛出决定上层能否继续执行。
        /// <summary>正常返回条件只约束调用之后的实际写入。</summary>
        [TestMethod]
        [DataRow("Check(flag); if (flag) state = 1;", false)]
        [DataRow("Check(flag); state = 1;", true)]
        [DataRow("state = 1; Check(flag);", true)]
        public async Task AnalyzeJoinsNormalContinuationAtWritePoint(string body, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { private static int state; private static void Check(bool flag) { if (flag) throw null; } public static void Entry(bool flag) { " + body + " } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 子调用的单一强写值仍须满足该子调用实际正常返回的条件。
        /// <summary>参数已经替换但随后抛出的分支不能到达调用者的写入。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsStrongStorageCallReturnCondition()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public int Value; } public static class Calls { private static void Set(ref Data target, Data old, bool flag) { target = old; if (flag) throw null; } public static void Entry(Data old, bool flag) { var target = new Data(); Set(ref target, old, flag); if (flag) target.Value = 1; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Getter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 创建对象的动态实现只有在实际返回分支上被选中才构成业务对象输出。
        /// <summary>返回值与虚调用接收对象的选择使用同一条路径。</summary>
        [TestMethod]
        [DataRow("", true)]
        [DataRow("if (flag)", true)]
        [DataRow("if (!flag)", false)]
        public async Task AnalyzeKeepsReturnedObjectOnItsDispatchBranch(string guard, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { } public interface IGet { Data Get(); } public sealed class Maker : IGet { public Data Get() => new Data(); } public sealed class Quiet : IGet { public Data Get() => null; } public static class Calls { public static Data Entry(bool flag) { IGet target = flag ? new Maker() : new Quiet(); " + guard + " return target.Get(); return null; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 先检查的后续返回条件不能倒过来排除已经发生的写入。
        /// <summary>无环跳转改变指令布局后，源码和 DLL 都保留异常前写入。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsWriteBeforeLaterReturnedCondition()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { private static int state; private static int Pass(bool flag, int n) { if (flag) throw null; return n; } public static void Entry(bool flag, int n) { goto Write; After: if (Pass(flag, n) == 1) return; return; Write: if (flag) state = 1; goto After; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 同一返回函数的未知出口不能因没有候选编号而消失。
        /// <summary>独立新对象出口不掩盖仍可能写入旧对象的未闭合调用。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeKeepsUnclosedReturnedObjectContribution(bool separateReturns)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public int Value; } public interface IBarrier { void Run(); } public sealed class Go : IBarrier { public void Run() {} } public sealed class Stop : IBarrier { public void Run() { throw null; } } public static class Calls { private static Data Choose(Data old, bool flag, IBarrier barrier) { if (flag) return new Data(); barrier.Run(); return old; } public static void Entry(Data old, bool flag, IBarrier barrier) { Choose(old, flag, barrier).Value = 1; } }");
            if (separateReturns)
            {
                using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                    new Mono.Cecil.ReaderParameters { InMemory = true });
                Mono.Cecil.MethodDefinition choose = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Choose");
                choose.Body.Instructions.Clear();
                choose.Body.Variables.Clear();
                Mono.Cecil.Cil.ILProcessor writer = choose.Body.GetILProcessor();
                Mono.Cecil.Cil.Instruction oldBranch = writer.Create(Mono.Cecil.Cil.OpCodes.Ldarg_2);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Brfalse, oldBranch);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Newobj, module.GetType("ExternalSamples.Data").Methods.Single(method => method.IsConstructor));
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                writer.Append(oldBranch);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Callvirt, module.GetType("ExternalSamples.IBarrier").Methods.Single(method => method.Name == "Run"));
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                AnalysisException error = Assert.Throws<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls));
                Assert.Contains("函数真实行为缺少实现证明", error.Message);
            }
        }

        // 实际 DLL 能直接观察布尔字段的非零字节，不能只保留规范化的零和一。
        /// <summary>布尔存储值为二的可执行路径不能被错误排除。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsNonCanonicalBooleanInput()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public bool Flag; public int Value; } public static class Calls { private static bool Test(Data old) => old.Flag; public static void Entry(Data old) { if (Test(old)) old.Value = 1; } }");
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition test = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Test");
                test.Body.Instructions.Clear();
                test.Body.Variables.Clear();
                Mono.Cecil.Cil.ILProcessor writer = test.Body.GetILProcessor();
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldfld, module.GetType("ExternalSamples.Data").Fields.Single(field => field.Name == "Flag"));
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_2);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ceq);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 类型操作数为整数不代表指令栈上的装箱结果也是整数。
        /// <summary>实际 DLL 直接按装箱引用跳转时，必不发生的旧对象写入仍被排除。</summary>
        [TestMethod]
        public async Task AnalyzeKeepsBoxedStackReferenceOutOfNumericConditions()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Data { public int Value; } public static class Calls { public static void Entry(Data old) { object boxed = 0; if (boxed == null) old.Value = 1; } }");
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition entry = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Entry");
                entry.Body.Instructions.Clear();
                entry.Body.Variables.Clear();
                Mono.Cecil.Cil.ILProcessor writer = entry.Body.GetILProcessor();
                Mono.Cecil.Cil.Instruction write = writer.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Box, module.TypeSystem.Int32);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Brfalse, write);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                writer.Append(write);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                writer.Emit(Mono.Cecil.Cil.OpCodes.Stfld, module.GetType("ExternalSamples.Data").Fields.Single(field => field.Name == "Value"));
                writer.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots.OrderBy(root => root.SourceSymbol == null ? 0 : 1))
            {
                Assert.AreEqual(MethodEffectKind.Getter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 被调函数的第一项写入可能只命中新对象，不能省略后续外部参数写入。
        /// <summary>只有无调用者的根或静态效果能提前结束，其余写入映射必须完整。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeRetainsOtherParametersAfterTemporaryWrite(bool externalSecond)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Box { public int Value; }
                public static class Calls
                {
                    public static void Entry(Box existing) => Write(new Box(), SECOND);
                    private static void Write(Box temporary, Box other) { temporary.Value = 1; other.Value = 2; }
                }
                """.Replace("SECOND", externalSecond ? "existing" : "new Box()"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                method.Kind == (externalSecond ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 深层对象图只重复引用同一批读取事实，不能每次查询都重新展开相同前缀。
        /// <summary>沿多个已有节点写入，源码和 DLL 都必须稳定得到 Setter。</summary>
        [TestMethod]
        public async Task AnalyzeFollowsDeepSharedObjectPrefixes()
        {
            string reads = string.Join(" ", Enumerable.Range(1, 12).Select(index => $"Node n{index} = n{index - 1}.Next;"));
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Node { public Node Next; public int Value; }
                public static class Calls { public static void Entry(Node n0) { READS n12.Value = 1; } }
                """.Replace("READS", reads));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 首个返回分支暂未闭合时，仍检查后续分支是否已经返回新业务对象。
        /// <summary>未知实现操作的数组不能变成 Getter，也不能遮挡独立的新对象传出证据。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeKeepsIndependentReturnProofAfterUnclosedArray(bool returnsBusinessObject)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Runtime.CompilerServices;
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static object Entry(bool unknown, int count)
                    {
                        if (unknown) { var values = new Data[count]; Populate(values); return values; }
                        return FINAL;
                    }
                    private static object Fresh() => new Data();
                    [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Populate(Data[] values);
                }
                """.Replace("FINAL", returnsBusinessObject ? "Fresh()" : "new Data[0]"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            if (returnsBusinessObject)
            {
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, result).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            }
            else
            {
                AnalysisException failure = Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result));
                Assert.Contains("Populate", failure.Message);
            }
        }

        // 复刻 pasteOnlySoundScript 的循环临时容器，其他确定写入不能被未闭合来源遮挡。
        /// <summary>只有独立外部写入证明才能完成 Setter；去掉证明后仍必须准确失败。</summary>
        [TestMethod]
        [DataRow(true, false, false)]
        [DataRow(true, true, false)]
        [DataRow(false, false, false)]
        [DataRow(true, false, true)]
        [DataRow(false, false, true)]
        public async Task AnalyzeKeepsIndependentProofBesideUnclosedLoopStorage(bool writesState, bool writeFirst, bool throughHelper)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public Holder Child; public int Value; }
                public static class Calls
                {
                    private static int state;
                    public static void Entry(int count)
                    {
                        BEFORE
                        for (int index = 0; index < count; index++)
                        {
                            var temporary = new Holder { Child = new Holder() };
                            temporary.Child.Value = 1;
                        }
                        AFTER
                    }
                }
                """.Replace("BEFORE", writesState && writeFirst ? "state++;" : string.Empty)
                .Replace("AFTER", writesState && !writeFirst ? "state++;" : string.Empty)
                .Replace("public static void Entry(int count)", throughHelper
                    ? "public static void Entry(int count) => Work(count); private static void Work(int count)"
                    : "public static void Entry(int count)"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            if (!writesState)
            {
                AnalysisException failure = await Assert.ThrowsExactlyAsync<AnalysisException>(async () =>
                {
                    CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
                    new EffectAnalyzer().Analyze(catalog, roots, calls);
                });
                Assert.Contains("循环创建对象的存储身份尚未闭合", failure.Message);
                return;
            }
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, result);
            Assert.HasCount(2, effects.Methods);
            Assert.IsTrue(effects.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 已转成运行时值的反射读取没有普通方法体，递归不能永远等待读取它。
        /// <summary>不执行返回的成员，仅重复查找时仍可完成实际调用与行为分析。</summary>
        [TestMethod]
        [DataRow("GetMethod(\"Quiet\")")]
        [DataRow("GetField(\"Data\")")]
        public async Task AnalyzeClosesRecursiveDiscardedReflectionLookup(string lookup)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public static class Calls
                {
                    public static int Data;
                    public static void Entry() => Walk(typeof(Calls));
                    private static void Walk(Type type) { type.LOOKUP; Walk(type); }
                    public static void Quiet() { }
                }
                """.Replace("LOOKUP", lookup));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 复刻 KHItemActor.GetRootFollowParent 沿父对象递归查找已有对象的写法。
        /// <summary>整个调用闭包只有读取且没有对象创建时，无须把每层父对象当成不同副作用。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeClosesReadOnlyRecursiveParentLookup(bool writingOverride)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Node
                {
                    public Node Parent; public Node RootParent; public Node FollowParent; public int Kind;
                    public virtual Node Root { get { Node current = this; while (current.FollowParent != null) current = current.FollowParent; return current; } }
                }
                public class Item : Node
                {
                    public override Node Root => GetRoot();
                    public Node GetRoot()
                    {
                        if (!IsNull(RootParent)) return RootParent.Root;
                        if (!IsNull(Parent))
                        {
                            if (Parent.Kind == 1 && Parent is Item) return (Parent as Item).GetRoot();
                            return Parent.Root;
                        }
                        return this;
                    }
                    private static bool IsNull(Node value) => value == null;
                }
                EXTRA
                """.Replace("EXTRA", writingOverride
                    ? "public sealed class Writer : Node { private static int state; public override Node Root { get { state++; return this; } } }" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Item").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "GetRoot").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (writingOverride ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 通过普通辅助函数返回的委托仍须在下一轮读取修改后的字段。
        /// <summary>不能把第一轮 Read 的历史返回值重复当成每一轮的结果。</summary>
        [TestMethod]
        public async Task AnalyzeFollowsRecursiveDelegateReturnedByHelper()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action Current; public Action Next; }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() => Walk(new Holder { Current = new Action(Quiet), Next = new Action(Write) });
                    private static Action Read(Holder holder) => holder.Current;
                    private static void Walk(Holder holder) { Read(holder)(); holder.Current = holder.Next; Walk(holder); }
                    private static void Quiet() { }
                    private static void Write() { state++; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
            string[] targets = calls.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Delegate).SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId).Name).Distinct().ToArray();
            CollectionAssert.AreEquivalent(new[] { "Quiet", "Write" }, targets);
        }

        // 参数不变但对象里的当前节点在每轮变化，递归仍须重读下一轮的真实接收对象。
        /// <summary>直接或经辅助函数改写 current 后，父节点的 Setter 不得遗漏。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task AnalyzeFollowsRecursiveStoredReceiverChanges(bool writerParent, bool throughHelper)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public abstract class Node { public Node Parent; public abstract void Run(); }
                public sealed class Quiet : Node { public override void Run() { } }
                public sealed class Writer : Node { private static int state; public override void Run() { state++; } }
                public sealed class Runner
                {
                    private Node current;
                    public Runner(Node value) { current = value; }
                    public void Walk() { current.Run(); CHANGE Walk(); }
                    private void Store(Node value) { current = value; }
                }
                public static class Calls
                {
                    public static void Entry()
                    {
                        Node parent = new PARENT();
                        parent.Parent = parent;
                        new Runner(new Quiet { Parent = parent }).Walk();
                    }
                }
                """.Replace("PARENT", writerParent ? "Writer" : "Quiet")
                .Replace("CHANGE", throughHelper ? "Store(current.Parent);" : "current = current.Parent;"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (writerParent ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
            string[] invokedTypes = calls.Calls.SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId))
                .Where(method => method.Name == "Run").Select(method => method.TypeName.Split('.').Last()).Distinct().ToArray();
            CollectionAssert.AreEquivalent(writerParent ? new[] { "Quiet", "Writer" } : new[] { "Quiet" }, invokedTypes);
        }

        // 同一数组参数中的槽位更新也会改变下一轮委托目标，不能只比较数组对象身份。
        /// <summary>第一轮纯回调被另一回调覆盖后，下一轮必须检查实际写入目标。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeFollowsRecursiveStoredDelegateChanges(bool writer)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry() => Walk(new Action[] { new Action(Quiet), new Action(SECOND) });
                    private static void Walk(Action[] callbacks) { callbacks[0](); callbacks[0] = callbacks[1]; Walk(callbacks); }
                    private static void Quiet() { }
                    private static void Write() { state++; }
                }
                """.Replace("SECOND", writer ? "Write" : "Quiet"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (writer ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 递归数组索引与接收对象一样会改变真实目标，不能作为无关整数丢弃。
        /// <summary>第一次调用第零项，再沿第一项循环，两个委托目标都必须进入结论。</summary>
        [TestMethod]
        public async Task AnalyzeFollowsRecursiveArrayIndexChanges()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry() => Walk(new Action[] { new Action(Quiet), new Action(Write) }, 0);
                    private static void Walk(Action[] callbacks, int index) { callbacks[index](); Walk(callbacks, 1); }
                    private static void Quiet() { }
                    private static void Write() { state++; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
            string[] targets = calls.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Delegate).SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId).Name).Distinct().ToArray();
            CollectionAssert.AreEquivalent(new[] { "Quiet", "Write" }, targets);
        }

        // 同类型的两个对象也可能保存不同回调，递归状态必须按对象而不是类型区分。
        /// <summary>父节点和子节点同为 Node 时仍追到父节点的 Setter 委托。</summary>
        [TestMethod]
        public async Task AnalyzeSeparatesSameTypeRecursiveObjects()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Node { public Node Parent; public Action Callback; }
                public static class Calls
                {
                    private static int state;
                    public static void Entry()
                    {
                        var parent = new Node { Callback = new Action(Write) };
                        parent.Parent = parent;
                        Walk(new Node { Parent = parent, Callback = new Action(Quiet) });
                    }
                    private static void Walk(Node current) { current.Callback(); Walk(current.Parent); }
                    private static void Quiet() { }
                    private static void Write() { state++; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
            string[] targets = calls.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Delegate).SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId).Name).Distinct().ToArray();
            CollectionAssert.AreEquivalent(new[] { "Quiet", "Write" }, targets);
        }

        // 一个返回分支已证明业务对象传出时，另一容器分支不能推翻 Setter 证据。
        /// <summary>返回分支交换顺序不改变已证明的业务对象传出。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeRetainsEscapeProofAcrossOtherReturnedContainers(bool reverse)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static object Entry(bool flag, int size) => flag ? FIRST : SECOND;
                }
                """.Replace("FIRST", reverse ? "new Data[size]" : "new Data()")
                .Replace("SECOND", reverse ? "new Data()" : "new Data[size]"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 只读静态数组的长度事实不能改变数组属于已有共享对象的事实。
        /// <summary>空数组分支不可达；非空静态数组的元素写入仍然需要追踪。</summary>
        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        public async Task AnalyzeKeepsStaticOwnershipWhenReadingInitializedArrayLength(int length)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static readonly int[] Values = new int[LENGTH];
                    public static void Entry()
                    {
                        if (Values.Length > 0) Values[0] = 1;
                    }
                }
                """.Replace("LENGTH", length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (length == 0 ? MethodEffectKind.Getter : MethodEffectKind.Setter)));
        }

        // 按 GetMoveToVkeys 的 List 创建、添加和返回流程复刻业务对象传出。
        /// <summary>返回列表中包含新业务对象时是 Setter，只包含已有对象时是 Getter。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeTracksBusinessObjectsInsideReturnedList(bool create)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Collections.Generic;
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static List<Data> Entry(Data existing)
                    {
                        var result = new List<Data>();
                        result.Add(ITEM);
                        return result;
                    }
                }
                """.Replace("ITEM", create ? "new Data()" : "existing"),
                "D:/Unity21KH/Data/MonoBleedingEdge/lib/mono/unityjit-win32/mscorlib.dll");
            File.AppendAllLines(project.RootResponsePath, new[] { "-define:UNITY_EDITOR_WIN" }, encoding: new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (create ? MethodEffectKind.Setter : MethodEffectKind.Getter)),
                string.Join(Environment.NewLine, result.Methods));
        }

        // 容器在被调函数创建并被更深一层改写，上游仍读取返回时的实际内容。
        /// <summary>跨调用返回的新业务对象被覆盖后不再算传出。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeReadsReturnedArrayAtCallerPosition(bool overwrite)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { }
                public static class Calls
                {
                    public static Data[] Entry(Data existing) => Build(existing);
                    private static Data[] Build(Data existing)
                    {
                        var result = new Data[1];
                        result[0] = new Data();
                        OVERWRITE
                        return result;
                    }
                    private static void Replace(Data[] result, Data existing) { result[0] = existing; }
                }
                """.Replace("OVERWRITE", overwrite ? "Replace(result, existing);" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (overwrite ? MethodEffectKind.Getter : MethodEffectKind.Setter)));
        }

        // 复刻 RunNodeInvert 沿 Parent 递归后更换实际虚函数接收对象。
        /// <summary>递归到新的对象须补齐它的真实重写；沿同一纯对象循环不能混入其他实现。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeFollowsRecursiveReceiverChanges(bool writerParent)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public abstract class Node { public Node Parent; public abstract void Run(); }
                public sealed class Quiet : Node { public override void Run() { } }
                public sealed class Writer : Node { private static int state; public override void Run() { state++; } }
                public static class Calls
                {
                    public static void Entry()
                    {
                        Node parent = new PARENT();
                        parent.Parent = parent;
                        var child = new Quiet { Parent = parent };
                        Walk(child);
                    }
                    private static void Walk(Node current) { current.Run(); Walk(current.Parent); }
                }
                """.Replace("PARENT", writerParent ? "Writer" : "Quiet"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (writerParent ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
            string[] invokedTypes = calls.Calls.SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId))
                .Where(method => method.Name == "Run").Select(method => method.TypeName.Split('.').Last()).Distinct().ToArray();
            CollectionAssert.AreEquivalent(writerParent ? new[] { "Quiet", "Writer" } : new[] { "Quiet" }, invokedTypes);
        }

        // 复刻 GetMoveToVkeys 将新建业务对象装入容器再返回的行为。
        /// <summary>容器内的新业务对象传出需要记录；已有对象及被覆盖的新对象不需要。</summary>
        [TestMethod]
        [DataRow(false, false, false, false)]
        [DataRow(true, false, false, false)]
        [DataRow(true, true, false, false)]
        [DataRow(true, true, true, false)]
        [DataRow(true, false, false, true)]
        [DataRow(true, true, false, true)]
        public async Task AnalyzeTracksBusinessObjectsInsideReturnedArray(bool create, bool overwrite, bool conditional, bool reference)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static Data[] Entry(Data existing, bool replace)
                    {
                        var result = new Data[1];
                        SLOT = ITEM;
                        OVERWRITE
                        return result;
                    }
                }
                """.Replace("ITEM", create ? "new Data()" : "existing")
                .Replace("OVERWRITE", overwrite ? (conditional ? "if (replace) " : string.Empty) + "SLOT = existing;" : string.Empty)
                .Replace("var result = new Data[1];", "var result = new Data[1];" + (reference ? " ref Data slot = ref result[0];" : string.Empty))
                .Replace("SLOT", reference ? "slot" : "result[0]"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (create && (!overwrite || conditional)
                ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 后续告警需要完整调用时，在已有值来源和实例上继续，不重建第二份调用图。
        /// <summary>恢复暂停项得到完整目标，并保留已分析函数与具体调用对象。</summary>
        [TestMethod]
        [DataRow(1, false)]
        [DataRow(4, false)]
        [DataRow(1, true)]
        [DataRow(4, true)]
        public async Task AnalyzeResumesPendingCallsOnSameGraph(int jobs, bool hasResolvedCall)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Sample
                {
                    private int value;
                    public void Entry() { ENTRY }
                    private void Intermediate() { value = 1; Write(); }
                    private void Write() { value = 2; }
                }
                """.Replace("ENTRY", hasResolvedCall ? "Intermediate();" : "value = 1; Write();"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Sample").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult partial = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs,
                requireCompleteCalls: false);
            Assert.HasCount(2, partial.PendingCalls);
            Assert.HasCount(hasResolvedCall ? 2 : 0, partial.Calls);
            CallTargetResolutionResult complete = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs,
                previous: partial);
            Assert.AreSame(partial.ValueSources, complete.ValueSources);
            Assert.IsEmpty(complete.PendingCalls);
            Assert.HasCount(hasResolvedCall ? 4 : 2, complete.Calls);
            Assert.HasCount(hasResolvedCall ? 6 : 4, complete.Methods);
            foreach (ResolvedCall previous in partial.Calls)
            {
                Assert.AreSame(previous, complete.Calls.Single(call => call.CallerInstanceId == previous.CallerInstanceId
                    && call.Call.Point.BlockId == previous.Call.Point.BlockId));
            }
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, complete).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 已证明入口暂停展开后，恢复审计仍须重新计算分支并补齐告警调用过程。
        /// <summary>其他根补读共享函数体不修改冻结根；恢复时排除原生死分支并找到裸 NLT 告警。</summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(4)]
        public async Task AnalyzeResumesSetterConditionsAndWarnings(int jobs)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                using System;
                using System.Runtime.CompilerServices;
                namespace KH { public sealed class NoLogTrackAttribute : Attribute { } }
                public sealed class Data { }
                public static class Calls
                {
                    private static int state;
                    [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown();
                    public static void Entry() { state = 1; if (Gate()) Unknown(); Warn(); }
                    public static Data ReturnNew() => new Data();
                    public static void Driver() { Step(); }
                    private static void Step() { Next(); }
                    private static void Next() { Next2(); }
                    private static void Next2() { Next3(); }
                    private static void Next3() { Gate(); }
                    private static bool Gate() => false;
                    [KH.NoLogTrack] public static void Warn() { state = 2; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            MethodEntry[] roots = catalog.Methods.Where(method => method.Name is "Entry" or "Driver" or "Warn" or "ReturnNew").ToArray();
            MethodEntry entry = roots.Single(method => method.Name == "Entry");
            MethodEntry warning = roots.Single(method => method.Name == "Warn");
            MethodEntry returning = roots.Single(method => method.Name == "ReturnNew");
            List<MethodEffect> returnedSnapshots = new();
            CallTargetResolutionResult partial = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, requireCompleteCalls: false,
                reportProgress: (_, effects) =>
                {
                    MethodEffect? current = effects.Methods.SingleOrDefault(method => method.MethodId == returning.Id);
                    if (returnedSnapshots.Count != 0)
                    {
                        Assert.AreEqual(MethodEffectKind.Setter, current?.Kind);
                        Assert.AreEqual(returnedSnapshots[0].Evidence!.Detail, current!.Evidence!.Detail);
                        CollectionAssert.AreEqual(returnedSnapshots[0].Evidence!.MethodPath.ToArray(), current.Evidence.MethodPath.ToArray());
                    }
                    if (current?.Kind == MethodEffectKind.Setter)
                    {
                        returnedSnapshots.Add(current);
                    }
                });
            Assert.IsTrue(returnedSnapshots.Count >= 2);
            Assert.IsTrue(partial.PendingCalls.Any(call => call.CallerMethodId == entry.Id));
            Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, roots, partial).Methods.Single(method => method.MethodId == entry.Id).Kind);
            MethodEffect returned = new EffectAnalyzer().Analyze(catalog, roots, partial).Methods.Single(method => method.MethodId == roots.Single(root => root.Name == "ReturnNew").Id);
            Assert.AreEqual(MethodEffectKind.Setter, returned.Kind);
            Assert.Contains("返回新建业务对象", returned.Evidence!.Detail);
            CallTargetResolutionResult complete = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, previous: partial);
            Assert.IsEmpty(complete.PendingCalls);
            Assert.IsEmpty(complete.Calls.Where(call => call.Call.Target.Name == "Unknown").ToArray());
            AnnotationResult annotations = new AnnotationEvaluator().Evaluate(catalog, roots, new EffectAnalyzer().Analyze(catalog, roots, complete), complete);
            Assert.IsTrue(annotations.Complete);
            Assert.AreEqual(MethodEffectKind.Setter, annotations.Methods.Single(method => method.Id == entry.Id).Actual);
            Assert.IsTrue(annotations.Methods.Single(method => method.Id == warning.Id).WarningPaths.Any(path => path.SequenceEqual(new[] { entry.Id, warning.Id })));
        }

        // 跨类型静态调用包含初始化，不可只看空方法的 ret 就输出 Getter 或后序 Setter。
        /// <summary>普通静态状态写入未纳入初始化约定；必抛初始化不能支持其后的写入，之前写入仍有效。</summary>
        [TestMethod]
        [DataRow("state = 1;", false, false, false)]
        [DataRow("throw null;", false, true, false)]
        [DataRow("Box box = null; box.Value = 1;", false, true, false)]
        [DataRow("throw null;", true, false, false)]
        [DataRow("throw null;", false, false, true)]
        [DataRow("throw null;", true, false, true)]
        public async Task AnalyzeKeepsStaticInitializationAtItsTrigger(string initializer, bool writeBefore, bool writeAfter, bool writeInside)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Box { public int Value; }
                public static class Mutator
                {
                    private static int state;
                    static Mutator() { INITIALIZER }
                    public static void Touch() { INSIDE }
                }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { BEFORE Mutator.Touch(); AFTER }
                }
                """.Replace("INITIALIZER", initializer).Replace("BEFORE", writeBefore ? "state = 1;" : "")
                    .Replace("AFTER", writeAfter ? "state = 2;" : "").Replace("INSIDE", writeInside ? "state = 3;" : ""));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            if (writeBefore)
            {
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            }
            else
            {
                Assert.Contains(writeAfter ? "正常返回" : "初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, calls)).Message);
            }
        }

        // 根所属类型已初始化的约定必须包括完整类型实参，不能外推到同名的另一构造类型。
        /// <summary>同根静态调用不重复初始化，另一组泛型实参的静态写入仍保留边界。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeSeparatesRootAndConstructedTypeInitialization(bool otherConstruction)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls<T>
                {
                    private static int state;
                    static Calls() { state = 1; }
                    public static void Entry() { TARGET.Touch(); }
                    private static void Touch() { }
                }
                """.Replace("TARGET", otherConstruction ? "Calls<int>" : "Calls<T>"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            if (otherConstruction)
            {
                Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, calls)).Message);
            }
            else
            {
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
            }
        }

        // 写入在未知调用之前才是独立证据，反序时必须先证明未知调用能正常返回。
        /// <summary>保留两个调用次序的差异及原始待处理原因。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeDefersSiblingCallFailureUntilWritesPropagate(bool reverseOrder)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public sealed class Box { public int Value; }
                public static class Calls
                {
                    public static void Entry(Box box, MethodInfo method) { BODY }
                    private static void Write(Box box) { box.Value = 1; }
                }
                """.Replace("BODY", reverseOrder ? "method.Invoke(null, null); Write(box);" : "Write(box); method.Invoke(null, null);"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.Name == "Entry")
                .Concat(catalog.Types.Where(type => type.FullName == "ExternalSamples.Calls").SelectMany(catalog.GetMethods).Where(method => method.Name == "Entry")).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2,
                requireCompleteCalls: false);
            Assert.HasCount(2, calls.PendingCalls);
            Assert.IsTrue(calls.PendingCalls.All(call => call.Failure?.Contains("来源尚未闭合", StringComparison.Ordinal) == true));
            if (reverseOrder)
            {
                Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, calls));
            }
            else
            {
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            }
        }

        // 复刻 ConfigItem 先执行字段回调再写入 isLoaded 的写法。
        /// <summary>对照前序写入，未知回调之后的写入不得单独成为修改证明。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeKeepsPendingCallAfterSetterProof(bool writeBefore)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public sealed class Item
                {
                    public MethodInfo Release;
                    private bool loaded;
                    public void Entry(object target)
                    {
                        BEFORE
                        if (Release == null) return;
                        Release.Invoke(target, null);
                        loaded = false;
                    }
                }
                """.Replace("BEFORE", writeBefore ? "loaded = true;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Item").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2,
                requireCompleteCalls: false);
            Assert.HasCount(2, roots);
            Assert.HasCount(2, calls.PendingCalls.Where(call => call.Call.Target.Name == "Invoke"));
            Assert.IsTrue(roots.All(root => calls.PendingCalls.Count(call => call.CallerMethodId == root.Id && call.Call.Target.Name == "Invoke") == 1));
            if (writeBefore)
            {
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            }
            else
            {
                Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, calls));
            }
            foreach (MethodEntry root in roots)
            {
                CollectionAssert.AreEquivalent(calls.Behaviors.MethodsById[root.Id].Calls.Select(call => call.Point).ToArray(),
                    calls.PendingCalls.Where(call => call.CallerMethodId == root.Id).Select(call => call.Call.Point)
                        .Concat(calls.Calls.Where(call => call.CallerMethodId == root.Id).Select(call => call.Call.Point)).ToArray());
            }
            await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
        }

        // 被调函数修改实参时，只有写入映射到根函数外部对象后才能暂停余下调用。
        /// <summary>同一函数修改临时对象不足以证明上游 Setter，未解析调用也不能被丢掉成为 Getter。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzePausesOnlyAfterMappingWritesToRoot(bool temporary)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public sealed class Box { public int Value; }
                public static class Calls
                {
                    public static void Entry(Box box, MethodInfo method) => Write(OBJECT, method);
                    private static void Write(Box box, MethodInfo method)
                    {
                        box.Value = 1;
                        method.Invoke(null, null);
                    }
                }
                """.Replace("OBJECT", temporary ? "new Box()" : "box"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "Entry" or "Write").ToArray();
            Assert.HasCount(4, roots);
            if (temporary)
            {
                CallTargetResolutionResult partial = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2,
                    requireCompleteCalls: false);
                Assert.IsTrue(partial.PendingCalls.Any(call => call.Failure != null));
                Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, partial));
                return;
            }
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2,
                requireCompleteCalls: false);
            Assert.HasCount(4, calls.PendingCalls);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 同一虚函数实现只需要一个合法接收类型证明，候选顺序不改变目标集合。
        /// <summary>无关自由参数不能阻断已被具体类型证明的共同实现，独特重写仍须证明。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeSharesWitnessOnlyForIdenticalTarget(bool distinctOverride)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public abstract class Constraint<T> where T : Constraint<T>, new() { }
                public abstract class Base { public virtual void Run() { } }
                public sealed class AOpen<T> : Base where T : Constraint<T>, new() { OVERRIDE }
                public sealed class ZConcrete : Base { }
                public static class Calls { public static void Entry(Base value) => value.Run(); }
                """.Replace("OVERRIDE", distinctOverride ? "private static int count; public override void Run() { count++; }" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            if (distinctOverride)
            {
                AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
                StringAssert.Contains(failure.Message, "泛型候选的合法构造尚未闭合");
                return;
            }
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
            Assert.HasCount(2, calls.Calls.SelectMany(call => call.Targets).ToArray());
        }

        // 复刻 GlobalConfig 的 T 继承 GlobalConfig<T> 并要求公开构造函数的约束。
        /// <summary>类型自引用约束可由材料中真正符合的具体派生类型证明存在。</summary>
        [TestMethod]
        public async Task AnalyzeSelfReferencingCandidateConstraint()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IRun { void Run(); }
                public abstract class Config<T> where T : Config<T>, new()
                {
                    public sealed class Helper : IRun
                    {
                        private static int count;
                        public void Run() { count++; }
                    }
                }
                public sealed class Current : Config<Current> { }
                public static class Calls { public static void Entry(IRun value) => value.Run(); }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 两个分支分别选择函数和参数时，不能把不同行的组合拼在一起。
        /// <summary>调用与参数的对应尚未证明时必须拒绝，不能产生不存在的 Setter 路径。</summary>
        [TestMethod]
        public async Task AnalyzeRejectsUncorrelatedReflectedArguments()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                using System.Reflection;
                namespace Samples;
                public static class Targets
                {
                    private static int count;
                    public static void Call(Action action) => action();
                    public static void Ignore(Action action) { }
                    public static void Quiet() { }
                    public static void Write() { count++; }
                }
                public static class Calls
                {
                    public static void Entry(bool flag)
                    {
                        MethodInfo method;
                        var arguments = new object[1];
                        if (flag) { method = typeof(Targets).GetMethod("Call"); arguments[0] = new Action(Targets.Quiet); }
                        else { method = typeof(Targets).GetMethod("Ignore"); arguments[0] = new Action(Targets.Write); }
                        method.Invoke(null, arguments);
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
            StringAssert.Contains(failure.Message, "反射分支关联尚未闭合");
        }

        // 反射接收对象来自工厂返回值时，等待该值真正确定后再选重写。
        /// <summary>工厂只返回安静实现，其他合法派生 Setter 不能混入本次调用。</summary>
        [TestMethod]
        public async Task AnalyzeReflectedReceiverReturnedByFactory()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base { public virtual int Value { set { } } }
                public sealed class Quiet : Base { public override int Value { set { } } }
                public sealed class Writer : Base { private static int count; public override int Value { set { count++; } } }
                public static class Calls
                {
                    private static Base Make() => new Quiet();
                    public static void Entry() => typeof(Base).GetProperty("Value").SetValue(Make(), 7);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
            MethodEntry[] targets = calls.Calls.SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId)).Where(method => method.Name == "set_Value").ToArray();
            Assert.HasCount(2, targets);
            Assert.IsTrue(targets.All(method => method.TypeName.EndsWith(".Quiet", StringComparison.Ordinal)));
        }

        // 复刻反射读取派生类覆盖或隐藏属性的过程。
        /// <summary>相同签名属性选择最派生声明，不能同时保留被隐藏的基类属性。</summary>
        [TestMethod]
        [DataRow("override")]
        [DataRow("new")]
        public async Task AnalyzeReflectedInheritedProperty(string modifier)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base { private static int count; public virtual int Value { get { count++; return 0; } } }
                public sealed class Derived : Base { public MODIFIER int Value { get => 7; } }
                public static class Calls
                {
                    public static object Entry(Derived value) => typeof(Derived).GetProperty("Value").GetValue(value);
                }
                """.Replace("MODIFIER", modifier));
            Type runtimeType = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath)).GetType("ExternalSamples.Derived")!;
            Assert.AreEqual(runtimeType, runtimeType.GetProperty("Value")!.DeclaringType);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, roots);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
            MethodEntry[] targets = calls.Calls.SelectMany(call => call.Targets)
                .Select(target => calls.Methods.Single(method => method.Id == target.MethodId)).Where(method => method.Name == "get_Value").ToArray();
            Assert.HasCount(2, targets);
            Assert.IsTrue(targets.All(method => method.TypeName.EndsWith(".Derived", StringComparison.Ordinal)));
        }

        // 属性名字相同但调用约定或返回类型不同，运行时不能唯一选择。
        /// <summary>保留静态隐藏实例属性及不同返回类型的真实歧义，不擅自合并。</summary>
        [TestMethod]
        [DataRow("public new static int Value { get => 7; }")]
        [DataRow("public new string Value { get => null; }")]
        public async Task AnalyzeRejectsAmbiguousReflectedProperty(string property)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base { public int Value { get => 0; } }
                public sealed class Derived : Base { PROPERTY }
                public static class Calls
                {
                    public static object Entry(Derived value) => typeof(Derived).GetProperty("Value").GetValue(value);
                }
                """.Replace("PROPERTY", property));
            Type runtimeType = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath)).GetType("ExternalSamples.Derived")!;
            Assert.Throws<System.Reflection.AmbiguousMatchException>(() => runtimeType.GetProperty("Value"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
            StringAssert.Contains(failure.Message, "反射属性查找或索引实参尚未闭合");
        }

        // 复刻 ImmutableList 的集合类型依赖元素类型约束。
        /// <summary>为多个自由参数构造存在性见证时，尚未选择的参数不能按旧约束提前拒绝。</summary>
        [TestMethod]
        public async Task AnalyzeCandidateWithDependentFreeParameters()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IRun { void Run(); }
                public interface IStore<T> { }
                public sealed class Open<TStore, T> : IRun where TStore : IStore<T>
                {
                    private static int count;
                    public void Run() { count++; }
                }
                public static class Calls { public static void Entry(IRun value) => value.Run(); }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 即使真实访问函数不可再重写，反射仍必须检查接收对象。
        /// <summary>实现接口的最终属性函数不能在空对象或不相容对象上执行。</summary>
        [TestMethod]
        public async Task AnalyzeChecksFinalReflectedPropertyReceiver()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IData { int Value { set; } }
                public sealed class Data : IData { public int Value { get; set; } }
                public sealed class Other { }
                public static class Calls
                {
                    public static void Right(Data data) => typeof(Data).GetProperty("Value").SetValue(data, 7);
                    public static void Missing() => typeof(Data).GetProperty("Value").SetValue(null, 7);
                    public static void Wrong(Other data) => typeof(Data).GetProperty("Value").SetValue(data, 7);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Right" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 复刻反射包装函数用对象数组传入普通函数参数的写法。
        /// <summary>反射实参数组按调用时的内容读取，并映射到真实接收对象与参数。</summary>
        [TestMethod]
        public async Task AnalyzeReflectedMethodArguments()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data
                {
                    public int Value;
                    public void Write(int value) { Value = value; }
                    public static int Read(int value) => value;
                }
                public static class Calls
                {
                    public static object Read() => typeof(Data).GetMethod("Read").Invoke(null, new object[] { 7 });
                    public static void Write(Data data) => typeof(Data).GetMethod("Write").Invoke(data, new object[] { 7 });
                    public static int Local()
                    {
                        var data = new Data();
                        var arguments = new object[] { 3 };
                        arguments[0] = 7;
                        typeof(Data).GetMethod("Write").Invoke(data, arguments);
                        return data.Value;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(6, result.Methods);
            ResolvedCallTarget[] writes = calls.Calls.SelectMany(call => call.Targets)
                .Where(target => calls.Methods.Single(method => method.Id == target.MethodId).Name == "Write").ToArray();
            Assert.HasCount(4, writes);
            foreach (ResolvedCallTarget target in writes)
            {
                ValueOrigin[] values = target.Arguments[0].SelectMany(argument => calls.ValueSources.GetCallOrigins(argument)).ToArray();
                Assert.HasCount(1, values);
                Assert.AreEqual(BehaviorValueKind.Constant, values[0].Value.Kind);
                Assert.AreEqual("7", values[0].Value.Reference);
            }
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Write" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 反射赋值必须先符合访问函数的真实参数类型。
        /// <summary>字符串不能被直接当作整数 Setter 参数，转换未证明时必须明确失败。</summary>
        [TestMethod]
        public async Task AnalyzeRejectsUnprovedPropertyArgumentConversion()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value { get; set; } }
                public static class Calls
                {
                    public static void Entry(Data data) => typeof(Data).GetProperty("Value").SetValue(data, "wrong");
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
            StringAssert.Contains(failure.Message, "反射实参转换尚未闭合");
        }

        // 复刻通用反射工具取得属性后读取或设置属性的过程。
        /// <summary>属性读写继续分析真实访问函数，名称为 getter 的函数也可能修改状态。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeReflectedPropertyAccessors(bool explicitIndexArguments)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data
                {
                    private int value;
                    private static int count;
                    public int Value { get => value; set => this.value = value; }
                    public int ChangedRead { get { count++; return value; } }
                }
                public static class Calls
                {
                    public static object Read(Data data) => typeof(Data).GetProperty("Value").GetValue(data INDEX);
                    public static void Write(Data data) => typeof(Data).GetProperty("Value").SetValue(data, 7 INDEX);
                    public static object Changed(Data data) => typeof(Data).GetProperty("ChangedRead").GetValue(data INDEX);
                    public static int Local()
                    {
                        var data = new Data();
                        typeof(Data).GetProperty("Value").SetValue(data, 7 INDEX);
                        return data.Value;
                    }
                }
                """.Replace("INDEX", explicitIndexArguments ? ", null" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.HasCount(8, result.Methods);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name is "Read" or "Local" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 复刻受击组件沿 getParentTarget 逐层读取父对象的循环。
        /// <summary>循环读取已有链表不产生写入，也不应因值来源回到循环头而递归失败。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeParentTraversal(bool hasWriter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Node
                {
                    public Node Parent;
                    public Node GetParent() => Parent;
                    public virtual void Read() { }
                    public static void Entry(Node node) { while (node != null) { node.Read(); node = node.GetParent(); } }
                }
                WRITER
                """.Replace("WRITER", hasWriter
                    ? "public sealed class Writer : Node { private static int value; public override void Read() { value++; } }" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Node").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind ==
                (hasWriter ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
            Assert.HasCount(hasWriter ? 4 : 2, calls.Calls.SelectMany(call => call.Targets)
                .Where(target => calls.Methods.Single(method => method.Id == target.MethodId).Name == "Read").ToArray());
        }

        // 复刻 BaseHurtAction 在子类声明接口、使用 DynamicAction 已有属性的写法。
        /// <summary>接口可以由父类的公开成员实现，后续隐藏成员不重新分配接口槽。</summary>
        [TestMethod]
        [DataRow("plain")]
        [DataRow("hidden")]
        [DataRow("override")]
        public async Task AnalyzeInterfaceImplementedByBaseMember(string mode)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IAction { object Value { set; } }
                public class Base { private static int count; public VIRTUAL object Value { set { count++; } } }
                public class Introduction : Base, IAction { }
                public sealed class Derived : Introduction { HIDDEN }
                public static class Calls { public static void Entry(IAction action) { action.Value = null; } }
                """.Replace("HIDDEN", mode == "plain" ? string.Empty
                    : mode == "hidden" ? "public new object Value { set { } }" : "public override object Value { set { } }")
                    .Replace("VIRTUAL", mode == "override" ? "virtual" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            MethodEntry[] setters = calls.Calls.SelectMany(call => call.Targets).Select(target => calls.Methods.Single(method => method.Id == target.MethodId))
                .Where(method => method.Name == "set_Value").ToArray();
            Assert.HasCount(mode == "override" ? 4 : 2, setters);
            Assert.IsTrue(setters.All(method => method.TypeName.EndsWith(".Base", StringComparison.Ordinal)
                || mode == "override" && method.TypeName.EndsWith(".Derived", StringComparison.Ordinal)));
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 复刻 ImmutableList 的额外容器参数，从它自身的真实接口约束继续分析。
        /// <summary>未指定的候选参数保留独立作用域，不覆盖调用者已有类型参数的约束。</summary>
        [TestMethod]
        [DataRow(true, true)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(false, false)]
        public async Task AnalyzeCandidateParameterConstraints(bool hasImplementation, bool requiresConstructor)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IRun<T> { void Run(); }
                public interface IStore<T> { void Touch(); }
                IMPLEMENTATION
                public class Open<TStore,T> : IRun<T> where TStore : CONSTRAINT
                {
                    private static int count;
                    public TStore Value;
                    public void Run() { count++; Value.Touch(); }
                }
                public static class Calls { public static void Entry<T>(IRun<T> value) => value.Run(); }
                """.Replace("CONSTRAINT", requiresConstructor ? "class, IStore<T>, new()" : "IStore<T>")
                    .Replace("IMPLEMENTATION", hasImplementation
                    ? "public sealed class Store<T> : IStore<T> { private static int value; public void Touch() { value++; } }" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind ==
                (hasImplementation || !requiresConstructor ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 复刻 List.Clear 的 final 标记：调用基类实现不能改选隐藏它的派生函数。
        /// <summary>禁止重写的函数直接绑定，但通过接口调用仍选择合法的重新实现。</summary>
        [TestMethod]
        public async Task AnalyzeFinalVirtualMethod()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface ICache<T> { void Clear(); }
                public class Cache<T> : ICache<T> { public void Clear() { } }
                public sealed class IntCache : Cache<int>, ICache<int>
                {
                    private static int value;
                    public new void Clear() { value++; }
                }
                public static class Calls
                {
                    public static void Concrete<T>(Cache<T> value) => value.Clear();
                    public static void Interface(ICache<int> value) => value.Clear();
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(4, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Concrete" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind);
            }
        }

        // 字段地址和 ref 参数仍要保留实际类型限制及被修改的对象归属。
        /// <summary>约束调用不能混入非法类型，也不能丢失新容器中保存的外部对象。</summary>
        [TestMethod]
        [DataRow("field", MethodEffectKind.Getter)]
        [DataRow("array", MethodEffectKind.Getter)]
        [DataRow("ref", MethodEffectKind.Setter)]
        [DataRow("stored", MethodEffectKind.Setter)]
        [DataRow("replaced", MethodEffectKind.Setter)]
        [DataRow("derived", MethodEffectKind.Setter)]
        [DataRow("returned", MethodEffectKind.Setter)]
        [DataRow("out", MethodEffectKind.Setter)]
        [DataRow("conditional", MethodEffectKind.Setter)]
        [DataRow("alias", MethodEffectKind.Getter)]
        [DataRow("overwrite", MethodEffectKind.Getter)]
        [DataRow("wrapper", MethodEffectKind.Setter)]
        [DataRow("ref_field", MethodEffectKind.Setter)]
        [DataRow("ref_array", MethodEffectKind.Setter)]
        [DataRow("mixed_ref_field", MethodEffectKind.Getter)]
        [DataRow("index_result", MethodEffectKind.Getter)]
        [DataRow("reflected_fields", MethodEffectKind.Setter)]
        public async Task AnalyzeConstrainedStorageReceivers(string storage, MethodEffectKind expected)
        {
            string source = """
                namespace Samples;
                public interface IRun { void Run(); }
                public sealed class Writer : IRun { private int value; public void Run() { value++; } }
                public struct Quiet : IRun { public void Run() { } }
                BODY
                """;
            string body = storage switch
            {
                "field" => "public sealed class Calls<T> where T : struct, IRun { public T Value; public void Entry() => Value.Run(); }",
                "array" => "public sealed class Calls<T> where T : struct, IRun { public T[] Values; public void Entry() => Values[0].Run(); }",
                "ref" => "public static class Calls { public static void Entry<T>(ref T value) where T : IRun => value.Run(); }",
                "stored" => """
                    public sealed class Holder<T> where T : IRun { public T Value; public void Run() => Value.Run(); }
                    public static class Calls { public static void Entry(Writer value) { var holder = new Holder<Writer>(); holder.Value = value; holder.Run(); } }
                    """,
                "replaced" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun value = new Quiet(); ReplaceAndRun(ref value, new StaticWriter()); }
                        private static void ReplaceAndRun<T>(ref T value, T next) where T : IRun { value = next; Invoke(ref value); }
                        private static void Invoke<T>(ref T value) where T : IRun { value.Run(); }
                    }
                    """,
                "derived" => """
                    public class Base : IRun { public virtual void Run() { } }
                    public sealed class Derived : Base { private static int value; public override void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry(Base value) => Invoke(ref value);
                        private static void Invoke<T>(ref T value) where T : IRun { value.Run(); }
                    }
                    """,
                "returned" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun value = new Quiet(); Replace(ref value, new StaticWriter()); value.Run(); }
                        private static void Replace<T>(ref T value, T next) { value = next; }
                    }
                    """,
                "out" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { Create(out IRun value); value.Run(); }
                        private static void Create(out IRun value) { value = new StaticWriter(); }
                    }
                    """,
                "conditional" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry(bool flag) { IRun value = new Quiet(); Replace(ref value, new StaticWriter(), flag); value.Run(); }
                        private static void Replace(ref IRun value, IRun next, bool flag) { if (flag) value = next; }
                    }
                    """,
                "alias" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun value = new Quiet(); Replace(ref value, ref value); value.Run(); }
                        private static void Replace(ref IRun left, ref IRun right) { left = new StaticWriter(); right = new Quiet(); }
                    }
                    """,
                "overwrite" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun value = new Quiet(); Replace(ref value); value = new Quiet(); value.Run(); }
                        private static void Replace(ref IRun value) { value = new StaticWriter(); }
                    }
                    """,
                "wrapper" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun value = new Quiet(); Wrapper(ref value, new StaticWriter()); }
                        private static void Wrapper(ref IRun value, IRun next) { Replace(ref value, next); Invoke(ref value); }
                        private static void Replace(ref IRun value, IRun next) { value = next; }
                        private static void Invoke<T>(ref T value) where T : IRun { value.Run(); }
                    }
                    """,
                "ref_field" or "mixed_ref_field" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public sealed class Holder { public IRun Value; }
                    public static class Calls
                    {
                        public static void Entry() { var holder = new Holder(); holder.Value = new Quiet(); Replace(ref holder.Value, holder); holder.Value.Run(); }
                        private static void Replace(ref IRun value, Holder holder) { value = new StaticWriter(); FINAL_WRITE }
                    }
                    """.Replace("FINAL_WRITE", storage == "mixed_ref_field" ? "holder.Value = new Quiet();" : string.Empty),
                "ref_array" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun[] values = new IRun[1]; values[0] = new Quiet(); Replace(ref values[0]); values[0].Run(); }
                        private static void Replace(ref IRun value) { value = new StaticWriter(); }
                    }
                    """,
                "index_result" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public static class Calls
                    {
                        public static void Entry() { IRun[] values = new IRun[2]; values[0] = new StaticWriter(); values[1] = new Quiet(); values[One()].Run(); }
                        private static int One() => 1;
                    }
                    """,
                "reflected_fields" => """
                    public sealed class StaticWriter : IRun { private static int value; public void Run() { value++; } }
                    public sealed class Data { public IRun A; public IRun B; }
                    public static class Calls
                    {
                        public static void Entry(bool flag)
                        {
                            var data = new Data(); data.A = new StaticWriter(); data.B = new Quiet();
                            var field = flag ? typeof(Data).GetField("A") : typeof(Data).GetField("B");
                            ((IRun)field.GetValue(data)).Run();
                        }
                    }
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(storage)),
            };
            using TestProject project = TestProject.CreateWithCallTargets(source.Replace("BODY", body));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            if (storage == "stored")
            {
                foreach (ResolvedCallTarget target in calls.Calls.SelectMany(call => call.Targets).Where(target =>
                             calls.Methods.Single(method => method.Id == target.MethodId).TypeName.EndsWith(".Writer", StringComparison.Ordinal)
                             && calls.Methods.Single(method => method.Id == target.MethodId).Name == "Run"))
                {
                    ValueOrigin[] origins = target.Receiver.SelectMany(value => calls.ValueSources.GetCallOrigins(value)).ToArray();
                    Assert.IsTrue(origins.All(origin => origin.Value.Kind == BehaviorValueKind.Parameter),
                        string.Join("; ", origins.Select(origin => $"{origin.Value.Kind} {origin.Value.Reference} {origin.Reference} [{origin.Value.Member?.DeclaringTypeIdentity.Text}]"))
                        + string.Join("; ", calls.Behaviors.Methods.SelectMany(body => body.Writes).Where(write => write.Member?.Name == "Value")
                            .Select(write => $"write {write.Member!.DeclaringTypeIdentity.Text}")));
                }
            }
            if (storage is "replaced" or "returned" or "wrapper" or "ref_field" or "ref_array")
            {
                MethodEntry[] invoked = calls.Calls.SelectMany(call => call.Targets)
                    .Select(target => calls.Methods.Single(method => method.Id == target.MethodId))
                    .Where(method => method.Name == "Run").ToArray();
                Assert.HasCount(2, invoked);
                Assert.IsTrue(invoked.All(method => method.TypeName.EndsWith(".StaticWriter", StringComparison.Ordinal)));
            }
            if (storage == "index_result")
            {
                MethodEntry[] invoked = calls.Calls.SelectMany(call => call.Targets)
                    .Select(target => calls.Methods.Single(method => method.Id == target.MethodId)).Where(method => method.Name == "Run").ToArray();
                Assert.HasCount(2, invoked);
                Assert.IsTrue(invoked.All(method => method.TypeName.EndsWith(".Quiet", StringComparison.Ordinal)));
            }
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.IsTrue(effects.Methods.All(method => method.Kind == expected), string.Join("; ", effects.Methods
                .Select(method => $"{method.Kind} {method.Evidence?.Detail} {string.Join(" -> ", method.Evidence?.MethodPath ?? Array.Empty<string>())}"))
                + string.Join("; ", calls.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Virtual).SelectMany(call => call.Targets)
                    .SelectMany(target => target.Receiver).SelectMany(value => calls.ValueSources.GetCallOrigins(value, true))
                    .Select(origin => $"receiver {origin.Value.Kind}:{origin.Value.Type?.Id}:{origin.Value.Reference}")));
        }

        // class 与 struct 限制合法接收对象，不能把不适用实现的修改传播进来。
        /// <summary>同一个泛型接口调用按声明约束选择类或结构体实现。</summary>
        [TestMethod]
        [DataRow("class", MethodEffectKind.Getter)]
        [DataRow("struct", MethodEffectKind.Setter)]
        public async Task AnalyzeConstrainedReceiverCategory(string constraint, MethodEffectKind expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IValue { void Run(); }
                public sealed class Quiet : IValue { public void Run() { } }
                public struct Writer : IValue { private static int value; public void Run() { value++; } }
                public static class Calls
                {
                    public static void Entry<T>(T value) where T : CONSTRAINT, IValue => value.Run();
                }
                """.Replace("CONSTRAINT", constraint));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (ResolvedCall call in calls.Calls.Where(call => roots.Any(root => root.Id == call.CallerMethodId)))
            {
                Assert.HasCount(1, call.Targets);
            }
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == expected));
        }

        // 接收对象是受接口约束的类型参数时，约束限制候选但不是具体实现。
        /// <summary>复刻泛型容器的受限调用，必须包含约束允许的全部写入实现。</summary>
        [TestMethod]
        [DataRow("")]
        [DataRow("class, ")]
        public async Task AnalyzeInterfaceConstrainedReceiver(string constraint)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IValue { void Run(); }
                public sealed class Quiet : IValue { public void Run() { } }
                public sealed class Writer : IValue { private int value; public void Run() { value++; } }
                public static class Calls
                {
                    public static void Entry<T>(T value) where T : CONSTRAINT IValue => value.Run();
                }
                """.Replace("CONSTRAINT", constraint));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (ResolvedCall call in calls.Calls.Where(call => roots.Any(root => root.Id == call.CallerMethodId)))
            {
                CollectionAssert.AreEquivalent(new[] { "Quiet", "Writer" }, call.Targets.Select(target =>
                    catalog.TypesById[calls.Methods.Single(method => method.Id == target.MethodId).TypeId].Name).ToArray());
            }
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 基类和参数之间的约束同样可以证明引用类型，不能只认显式 class 关键字。
        /// <summary>根函数的直接、传递及接口调用约束进入同一验证流程。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeInheritedGenericConstraints(bool transitive)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base { public int Value; }
                public interface IWork { void Run(); }
                public sealed class Worker<T> : IWork where T : class
                {
                    private int value;
                    public void Run() { value++; }
                    public static void Read() { }
                }
                public sealed class Bounded<T> where T : Base { public static void Read() { } }
                public static class Calls
                {
                    public static void Entry<PARAMETERS>() CONSTRAINTS
                    {
                        Worker<T>.Read();
                        Bounded<T>.Read();
                        IWork work = new Worker<T>();
                        work.Run();
                    }
                }
                """.Replace("PARAMETERS", transitive ? "TBase, T" : "T")
                    .Replace("CONSTRAINTS", transitive ? "where TBase : Base where T : TBase" : "where T : Base"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 虚调用的类型参数可以沿接收对象原样传递，不需要先选一个具体元素类型。
        /// <summary>复刻 List.Clear 的泛型接口实现，依据真实方法关系找到写入。</summary>
        [TestMethod]
        public async Task AnalyzeVirtualCallOnGenericContainer()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface ICache<T> { void Clear(); }
                public sealed class Cache<T> : ICache<T>
                {
                    private int version;
                    public void Clear() { version++; }
                }
                public static class Calls
                {
                    public static void Concrete<T>(Cache<T> value) => value.Clear();
                    public static void Interface<T>(ICache<T> value) => value.Clear();
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(4, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 构造的类型定义已经确定时，不必替类型参数选择一个具体类型。
        /// <summary>复刻 DataFour.Make，泛型容器的本地初始化与返回仍按同一对象规则判断。</summary>
        [TestMethod]
        public async Task AnalyzeGenericContainerCreation()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data<T> { public T Value; public Data(T value) { Value = value; } }
                public static class Calls
                {
                    public static Data<T> Make<T>(T value) => new Data<T>(value);
                    public static void Local<T>(T value) { var data = new Data<T>(value); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            Assert.HasCount(4, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Make" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 同一个类型参数指向同一静态槽，不同参数则可能在运行时相等。
        /// <summary>复刻泛型委托缓存，既允许同槽写后读，也不漏掉可能重合的另一次写入。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeGenericStaticDelegateStorage(bool possibleAlias)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Cache<T> { public static System.Action Callback; }
                public static class Calls
                {
                    private static int value;
                    private static void Quiet() { }
                    private static void Write() { value++; }
                    public static void Entry<T>()
                    {
                        Cache<T>.Callback = new System.Action(Write);
                        EXTRA
                        Cache<T>.Callback();
                    }
                }
                """.Replace("EXTRA", possibleAlias ? "Cache<int>.Callback = new System.Action(Quiet);" : ""));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                if (possibleAlias)
                {
                    AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                        new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                    StringAssert.Contains(exception.Message, "泛型存储的重合关系尚未闭合");
                    continue;
                }

                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
                Assert.IsTrue(calls.Calls.SelectMany(call => call.Targets).Any(target =>
                    calls.Methods.Single(method => method.Id == target.MethodId).Name == "Write"));
                Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
            }
        }

        // 值类型装箱产生副本，不能把副本的字段写入归回原参数。
        /// <summary>同一反射写法在类对象和结构体装箱对象上具有不同归属。</summary>
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task AnalyzeSeparatesBoxedCopiesFromReferenceObjects(bool valueType)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public KIND Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data data) => typeof(Data).GetField("Value").SetValue(data, 7);
                }
                """.Replace("KIND", valueType ? "struct" : "sealed class"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (valueType ? MethodEffectKind.Getter : MethodEffectKind.Setter)));
        }

        // 泛型类声明本身已经限定合法实参，独立分析不应否认自身约束。
        /// <summary>复刻 KHSafeRef 的受约束泛型容器，区分成员读取和成员替换。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnalyzeConstrainedGenericRootByItsDeclaredContract(bool throughCall)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Calls<T> where T : class
                {
                    private T value;
                    private T ReadValue() => value;
                    private void WriteValue(T next) { value = next; }
                    public T Read() => READ;
                    public void Write(T next) { WRITE }
                }
                """.Replace("READ", throughCall ? "ReadValue()" : "value")
                    .Replace("WRITE", throughCall ? "WriteValue(next);" : "value = next;"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "Read" or "Write").ToArray();
            Assert.HasCount(4, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(4, result.Methods);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Read" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 类型参数未具体化不妨碍判断清零、读取和写入的存储归属。
        /// <summary>泛型指令在真正影响目标选择时才需要具体类型，不能统一拒绝。</summary>
        [TestMethod]
        public async Task AnalyzeGenericStorageWithoutChoosingAType()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    public static T Read<T>(T[] values) => values[0];
                    public static void Write<T>(T[] values, T value) { values[0] = value; }
                    public static void Copy<T>(ref T target, T value) { target = value; }
                    public static T[] Array<T>() => new T[1];
                    public static object Type<T>() => typeof(T);
                    public static object Default<T>() => default(T);
                    public static object Box<T>(T value) => value;
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name is "Write" or "Copy" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 非法字段实参不能成为已经发生写入的证据。
        /// <summary>未实现反射赋值转换时准确失败，不把不兼容值当作 Setter。</summary>
        [TestMethod]
        [DataRow("new Data()")]
        [DataRow("\"7\"")]
        public async Task AnalyzeRejectsUnclosedFieldConversion(string value)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data data) => typeof(Data).GetField("Value").SetValue(data, VALUE);
                }
                """.Replace("VALUE", value));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods).ToArray();
            foreach (MethodEntry root in roots)
            {
                AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(exception.Message, "反射字段赋值转换尚未闭合");
            }
        }

        // 反射字段操作应产生普通字段事实，而不是按函数名字直接分类。
        /// <summary>区分反射读取、已有对象写入、临时对象写入和静态写入。</summary>
        [TestMethod]
        public async Task AnalyzeReflectionFieldsByWrittenObject()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; public static int Shared; }
                public static class Calls
                {
                    public static object Read(Data data) => typeof(Data).GetField("Value").GetValue(data);
                    public static void Write(Data data) => typeof(Data).GetField("Value").SetValue(data, 7);
                    public static void Local() => typeof(Data).GetField("Value").SetValue(new Data(), 7);
                    public static void Static() => typeof(Data).GetField("Shared").SetValue(null, 7);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name is "Read" or "Local" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 反射操作必须正常返回，后续写入才能作为修改证据。
        /// <summary>区分空实例调用必抛、合法静态操作和查找为空的正常返回。</summary>
        [TestMethod]
        [DataRow("typeof(Data).GetField(\"Value\").SetValue(null, 7)", false)]
        [DataRow("typeof(Data).GetField(\"Value\").GetValue(null)", false)]
        [DataRow("typeof(Data).GetMethod(\"Read\").Invoke(null, null)", false)]
        [DataRow("typeof(Data).GetProperty(\"Property\").GetValue(null)", false)]
        [DataRow("typeof(Data).GetProperty(\"Property\").SetValue(null, 7)", false)]
        [DataRow("typeof(Data).GetField(\"Shared\").GetValue(null)", true)]
        [DataRow("typeof(Data).GetField(\"Shared\").SetValue(null, 7)", true)]
        [DataRow("typeof(Data).GetMethod(\"StaticRead\").Invoke(null, null)", true)]
        [DataRow("typeof(Data).GetField(\"Missing\")", true)]
        public async Task AnalyzeRequiresReflectionToReturnBeforeLaterWrites(string operation, bool completes)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data
                {
                    public int Value;
                    public static int Shared;
                    public int Property { get => Value; set => Value = value; }
                    public int Read() => Value;
                    public static int StaticRead() => Shared;
                }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { OPERATION; state = 1; }
                }
                """.Replace("OPERATION", operation));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.Types.Where(type => type.AssemblyPath == project.ExternalAssemblyPath
                    && type.FullName == "ExternalSamples.Calls").SelectMany(catalog.GetMethods)).ToArray();

            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(completes ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 源码和实际 DLL 都保留变量赋值与分支的对应关系。
        /// <summary>互斥分支不能借用彼此的整数值，也不能删除合法的写入对照。</summary>
        [TestMethod]
        [DataRow("int y = x == 0 ? 0 : 1; if (x == 0 && y == 1) state = 1;", false)]
        [DataRow("int y = 0; if (x == 0) y = 1; if (x == 0 && y == 0) state = 1;", false)]
        [DataRow("int y = x == 0 ? 0 : 1; if (x != 0 && y == 0) state = 1;", false)]
        [DataRow("int y = x == 0 ? 0 : 1; if (x == 0 && y == 0) state = 1;", true)]
        [DataRow("int y = 0; if (x == 0) y = 1; if (x == 0 && y == 1) state = 1;", true)]
        [DataRow("int y = x == 0 ? x : x + 1; if (x == 1 && y == 2) state = 1;", true)]
        public async Task AnalyzeKeepsMergedIntegerConditions(string body, bool setter)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { private static int state; public static void Entry(int x) { " + body + " } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.IsTrue(result.Methods.All(method => method.Kind == (setter ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 第二次经另一个函数返回递归入口时，不能套用第一次的参数条件。
        /// <summary>有限间接递归在下一次进入时才写入，源码和 DLL 都必须保留该效果。</summary>
        [TestMethod]
        public async Task AnalyzePreservesFiniteIndirectRecursion()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { A(1); }
                    private static void A(int value)
                    {
                        if (value == 2) { state = 1; return; }
                        B();
                    }
                    private static void B() { A(2); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 同一函数经不同查找路径出现时仍只代表一个合法目标。
        /// <summary>重复的类型来源不能导致函数与接收对象的虚假关联错误。</summary>
        [TestMethod]
        public async Task AnalyzeMergesRepeatedReflectionTargets()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; public void Foo() { Value = 7; } }
                public static class Calls
                {
                    private static System.Type LeftType() => typeof(Data);
                    private static System.Type RightType() => typeof(Data);
                    public static void Entry(bool flag, bool otherFlag, Data left, Data right)
                    {
                        var type = flag ? LeftType() : RightType();
                        var method = type.GetMethod("Foo");
                        method.Invoke(otherFlag ? left : right, null);
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 同一反射表达式查到多个函数时不能按值编号丢掉其中一项。
        /// <summary>调换类型分支顺序后仍会找到会写静态数据的合法目标。</summary>
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task AnalyzeKeepsEveryReflectedMethod(bool reversed)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class A { public static void Foo() { } }
                public sealed class B { public static int Value; public static void Foo() { Value = 7; } }
                public static class Calls
                {
                    public static void Entry(bool flag)
                    {
                        var type = flag ? typeof(FIRST) : typeof(SECOND);
                        type.GetMethod("Foo").Invoke(null, null);
                    }
                }
                """.Replace("FIRST", reversed ? "B" : "A").Replace("SECOND", reversed ? "A" : "B"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 尚未保留成组的分支条件时必须拒绝拼接类型和名称。
        /// <summary>不能把两条只读分支混成运行时不会发生的 Setter 调用。</summary>
        [TestMethod]
        public async Task AnalyzeRejectsUnclosedReflectionCorrelation()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class A
                {
                    public static int Value;
                    public static void Read() { }
                    public static void OtherRead() { Value = 1; }
                }
                public sealed class B
                {
                    public static int Value;
                    public static void OtherRead() { }
                    public static void Read() { Value = 1; }
                }
                public static class Calls
                {
                    public static void Entry(bool flag)
                    {
                        System.Type type;
                        string name;
                        if (flag) { type = typeof(A); name = "Read"; }
                        else { type = typeof(B); name = "OtherRead"; }
                        type.GetMethod(name).Invoke(null, null);
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            foreach (MethodEntry root in roots)
            {
                AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(exception.Message, "反射分支关联尚未闭合");
            }
        }

        // 反射只能执行与目标对象类型相容的实例函数。
        /// <summary>空对象和不相关对象都不能使查到的实例函数实际运行。</summary>
        [TestMethod]
        public async Task AnalyzeChecksReflectionReceiver()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Data { public int Value; public void Foo() { Value = 7; } }
                public sealed class Other { public int Value; }
                public static class Calls
                {
                    public static void Missing() => typeof(Data).GetMethod("Foo").Invoke(null, null);
                    public static void Wrong(Other data) => typeof(Data).GetMethod("Foo").Invoke(data, null);
                    public static void Right(Data data) => typeof(Data).GetMethod("Foo").Invoke(data, null);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Right" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 类型和函数经局部变量或普通函数传递后仍保留真实反射目标。
        /// <summary>反射值不依赖 IL 是否直接把调用结果放在下一条调用中。</summary>
        [TestMethod]
        public async Task AnalyzePreservesReflectionValuesAcrossCalls()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; public void Foo() { Value = 7; } }
                public static class Calls
                {
                    private static System.Type FindType() => typeof(Data);
                    public static void Entry(Data data)
                    {
                        var type = FindType();
                        var method = type.GetMethod("Foo");
                        method.Invoke(data, null);
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 反射取得的方法继续进入普通函数调用和实际参数传播。
        /// <summary>GetMethod("Foo") 调用 Setter 会修改传入对象，调用只读函数不会。</summary>
        [TestMethod]
        public async Task AnalyzeResolvesNamedReflectionMethods()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data
                {
                    public int Value;
                    public void Foo() { Value = 7; }
                    public int Read() => Value;
                }
                public static class Calls
                {
                    public static void Write(Data data) => typeof(Data).GetMethod("Foo").Invoke(data, null);
                    public static void Read(Data data) => typeof(Data).GetMethod("Read").Invoke(data, null);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Read" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // default(T) 清零不运行用户构造函数，区别仅在写入位置。
        /// <summary>未指定 T 仍可准确判断本地清零与 out 参数清零。</summary>
        [TestMethod]
        public async Task AnalyzeGenericDefaultValuesByTheirStorage()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    public static T Local<T>() => default(T);
                    public static void Output<T>(out T data) { data = default(T); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Local" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 委托构造只绑定对象和函数，不在构造时执行回调。
        /// <summary>同一运行时委托构造规则同时供存储来源和真实效果使用。</summary>
        [TestMethod]
        public async Task AnalyzeClosesRuntimeDelegateConstruction()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int Read() => 7;
                    public static int Entry() { var read = new System.Func<int>(Read); return read(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 外壳是新对象不代表其内部引用的对象也是新对象。
        /// <summary>跨函数字段来源保留调用者实际保存的对象，不重复代入参数。</summary>
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task AnalyzePreservesParentObjectsStoredInsideFreshReceivers(bool externalChild)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public sealed class Box
                {
                    public Data Child;
                    public void Change() { Child.Value = 1; }
                }
                public static class Calls
                {
                    public static void Entry(Data external)
                    {
                        var box = new Box();
                        box.Child = CHILD;
                        box.Change();
                    }
                }
                """.Replace("CHILD", externalChild ? "external" : "new Data()"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == (externalChild ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
        }

        // 业务对象身份必须区分编译器生成类型，不能只比较所属程序集。
        /// <summary>根据真实 CompilerGenerated 特性区分辅助对象和显式业务对象。</summary>
        [TestMethod]
        public async Task AnalyzeDoesNotTreatCompilerGeneratedObjectsAsBusinessObjects()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                [System.Runtime.CompilerServices.CompilerGenerated]
                public sealed class Generated { }
                public sealed class Data { }
                public static class Calls
                {
                    public static object Auxiliary() => new Generated();
                    public static object Business() => new Data();
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Auxiliary" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 递归交换实参时必须按递归边重新映射被修改的参数。
        /// <summary>不能复用第一次递归调用的临时对象来源而漏掉外部对象写入。</summary>
        [TestMethod]
        public async Task AnalyzePropagatesWritesAcrossRecursiveArgumentPermutation()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data external, int depth) => Change(new Data(), external, depth);
                    private static void Change(Data left, Data right, int depth)
                    {
                        if (depth > 0) Change(right, left, depth - 1);
                        else left.Value = 1;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 泛型参数不参与写入目标时按函数定义证明实际修改。
        /// <summary>读取类型参数的约束不应阻断与该参数无关的字段写入。</summary>
        [TestMethod]
        public async Task AnalyzeGenericRootWithTypeIndependentWrite()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static T Change<T>(Data data, T item) where T : class { data.Value = 1; return item; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(2, result.Methods);
            Assert.IsTrue(result.Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 对照引用传出与仅修改值参数的本地副本。
        /// <summary>引用参数、静态字段地址和数组元素写入均保留归属。</summary>
        [TestMethod]
        public async Task AnalyzeDistinguishesReferenceWritesFromParameterCopies()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int value;
                    private static void Change(ref int target) { target = 7; }
                    public static void Copy(int target) => Change(ref target);
                    public static void Reference(ref int target) => Change(ref target);
                    public static void Static() => Change(ref value);
                    public static void Array(int[] target) => Change(ref target[0]);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).Where(method => method.IsPublic).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Copy" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 对照新建业务对象被直接或间接返回的日志要求。
        /// <summary>返回新业务对象是 Setter，返回已有对象和临时对象的普通字段不是。</summary>
        [TestMethod]
        public async Task AnalyzeTracksBusinessObjectsReturnedThroughCalls()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    public static Data Create() => new Data();
                    public static Data Forward() => Create();
                    public static Data Existing(Data value) => value;
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Existing" ? MethodEffectKind.Getter : MethodEffectKind.Setter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }

        // 对照修改传入对象与修改函数内临时对象的区别。
        /// <summary>同一个写入函数不能令所有调用者都成为 Setter。</summary>
        [TestMethod]
        public async Task AnalyzeMapsWritesToTheActualReceiver()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data
                {
                    public int Value;
                    public void Change() { Value = 7; }
                }
                public static class Calls
                {
                    public static int Read(Data data) => data.Value;
                    public static void Existing(Data data) => data.Change();
                    public static int Temporary() { var data = new Data(); data.Change(); return data.Value; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.Calls");
            MethodEntry[] roots = catalog.Methods.Where(method => method.TypeName == "SourceSamples.Calls")
                .Concat(catalog.GetMethods(external)).Where(method => method.Name is "Read" or "Existing" or "Temporary").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            EffectAnalysisResult result = new EffectAnalyzer().Analyze(catalog, roots, calls);

            Assert.HasCount(6, result.Methods);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(root.Name == "Existing" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                    result.Methods.Single(method => method.MethodId == root.Id).Kind, root.Id);
            }
        }
    }
}
