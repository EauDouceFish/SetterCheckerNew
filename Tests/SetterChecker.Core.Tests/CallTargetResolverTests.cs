namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 验证源码与真实托管调用找到同样准确的函数和传值关系。
    /// </summary>
    [TestClass]
    public sealed class CallTargetResolverTests
    {
        // 保留普通连接不等于完成其初始化证明，递归回边也仍须重新绑定。
        /// <summary>剪枝后的初始化失败不被隐藏，递归关系在恢复分析后保持完整。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task ResolveAsyncRetainedBindingsKeepInitializationAndRecursionChecks(bool recursive, bool construction)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Initialized
                {
                    public static int Value;
                    static Initialized() { Value=Read(); }
                    private static int Read() => 1;
                    public static void Touch() { }
                }
                public static class Calls
                {
                    public static void Entry()
                    {
                        QuietA();
                        if (GateA()) return;
                        ACTION
                    }
                    private static void QuietA() => QuietB();
                    private static void QuietB() { }
                    private static bool GateA() => GateB();
                    private static bool GateB() => false;
                    private static void Cycle() { Cycle(); }
                }
                """.Replace("ACTION", recursive ? "Cycle();" : construction ? "new Initialized();" : "Initialized.Touch();"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            foreach (int jobs in new[] { 1, 4 })
            {
                List<string> progress = new();
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, progress: progress.Add);
                Assert.IsTrue(progress.Any(message => message.StartsWith("路径收缩后保留 ", StringComparison.Ordinal)
                    && !message.StartsWith("路径收缩后保留 0 ", StringComparison.Ordinal)), string.Join("\n", progress));
                foreach (bool resume in new[] { false, true })
                {
                    if (resume)
                    {
                        calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, previous: calls);
                    }
                    Assert.AreEqual(calls.Calls.Count, calls.Calls.Select(call => (call.CallerInstanceId, call.Call.Point)).Distinct().Count());
                    if (recursive)
                    {
                        Assert.AreEqual(2, calls.Calls.Count(call => call.Targets.Any(target => target.MethodId == call.CallerMethodId)));
                        Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
                    }
                    else
                    {
                        foreach (MethodEntry root in roots)
                        {
                            AnalysisException failure = Assert.Throws<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, new[] { root }, calls));
                            StringAssert.Contains(failure.Message, "初始化");
                        }
                    }
                }
            }
        }

        // 固定目标可以保留，分支收缩后的新旧对象来源和反射名称必须重新求取。
        /// <summary>相同调用点分别传入旧对象和新对象，或选择不同反射目标，不得沿用过期效果。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task ResolveAsyncRecomputesInputsOfRetainedBindings(bool writes, bool reflection)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Targets
                {
                    public static void Read(Data value) { }
                    public static void Write(Data value) { value.Value=1; }
                }
                public static class Calls
                {
                    public static void Entry(Data outside)
                    {
                        QuietA();
                        OPERATION
                    }
                    private static void Forward(Data value) => Targets.Write(value);
                    private static void QuietA() => QuietB();
                    private static void QuietB() { }
                    private static bool GateA() => GateB();
                    private static bool GateB() => GateC();
                    private static bool GateC() => GATE;
                }
                """.Replace("GATE", writes ? "true" : "false")
                .Replace("OPERATION", reflection ? "typeof(Targets).GetMethod(GateA() ? \"Write\" : \"Read\").Invoke(null, new object[] { outside });"
                    : "Data selected = outside; if (!GateA()) selected=new Data(); Forward(selected);"));
            System.Reflection.Assembly runtime = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath));
            object outside = Activator.CreateInstance(runtime.GetType("ExternalSamples.Data")!)!;
            runtime.GetType("ExternalSamples.Calls")!.GetMethod("Entry")!.Invoke(null, new[] { outside });
            Assert.AreEqual(writes ? 1 : 0, outside.GetType().GetField("Value")!.GetValue(outside));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            foreach (int jobs in new[] { 1, 4 })
            {
                List<string> progress = new();
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, progress: progress.Add);
                Assert.IsTrue(progress.Any(message => message.StartsWith("路径收缩后保留 ", StringComparison.Ordinal)
                    && !message.StartsWith("路径收缩后保留 0 ", StringComparison.Ordinal)), string.Join("\n", progress));
                foreach (MethodEffect result in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
                {
                    Assert.AreEqual(writes ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Kind);
                }
                Assert.AreEqual(calls.Calls.Count, calls.Calls.Select(call => (call.CallerInstanceId, call.Call.Point)).Distinct().Count());
                if (reflection)
                {
                    foreach (ResolvedCall call in calls.Calls.Where(call => call.Call.Target.Name == "Invoke"))
                    {
                        Assert.AreEqual(writes ? "Write" : "Read", calls.Methods.Single(method => method.Id == call.Targets.Single().MethodId).Name);
                    }
                }
            }
        }

        // 延迟条件收缩一条分支时，无关直调的固定目标不应整树重复连接。
        /// <summary>保留仍可达的普通绑定，删除不可达写入，恢复分析及不同并行额度的结果一致。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncRetainsDirectBindingsWhenAPathShrinks(bool construction)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public sealed class Quiet
                {
                    public Quiet() { First(); }
                    private void First() { Second(); }
                    private void Second() { }
                }
                public static class Calls
                {
                    public static void Entry(Data outside)
                    {
                        QUIET
                        if (GateA()) outside.Value=1;
                    }
                    private static void QuietA() => QuietB();
                    private static void QuietB() => QuietC();
                    private static void QuietC() { }
                    private static bool GateA() => GateB();
                    private static bool GateB() => GateC();
                    private static bool GateC() => GateD();
                    private static bool GateD() => false;
                }
                """.Replace("QUIET", construction ? "new Quiet();" : "QuietA();"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            foreach (int jobs in new[] { 1, 4 })
            {
                List<string> progress = new();
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs,
                    requireCompleteCalls: false, progress: progress.Add, reportProgress: (snapshot, _) =>
                        Assert.AreEqual(snapshot.Calls.Count, snapshot.Calls.Select(call => (call.CallerInstanceId, call.Call.Point)).Distinct().Count()));
                Assert.IsTrue(progress.Any(message => message.StartsWith($"路径收缩后保留 {(construction ? 16 : 14)} ", StringComparison.Ordinal)), string.Join("\n", progress));
                foreach (bool resume in new[] { false, true })
                {
                    if (resume)
                    {
                        calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, previous: calls);
                    }
                    Assert.IsEmpty(calls.PendingCalls);
                    Assert.AreEqual(construction ? 16 : 14, calls.Calls.Count);
                    Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Getter));
                }
            }
        }

        // 保留构造绑定不保留其字段内容或委托目标，构造参数仍按收缩后的路径重新求取。
        /// <summary>不同输入快照保有独立对象，普通创建和显式委托创建均核对真实 DLL 运行。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task ResolveAsyncRecomputesRetainedConstructorInputs(bool writes, bool useDelegate)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Data { public int Value; public void Write() { Value=1; } }
                public sealed class Holder { public Data Item; public Holder(Data item) { Item=item; } }
                public static class Calls
                {
                    public static void Entry(Data outside)
                    {
                        Data selected = outside;
                        if (!GateA()) selected = new Data();
                        OPERATION
                    }
                    private static bool GateA() => GateB();
                    private static bool GateB() => GateC();
                    private static bool GateC() => GATE;
                }
                """.Replace("GATE", writes ? "true" : "false").Replace("OPERATION", useDelegate
                    ? "var call = new Action(selected.Write); call();"
                    : "var first = new Holder(selected); var second = new Holder(new Data()); second.Item.Value=2; first.Item.Value=1;"));
            System.Reflection.Assembly runtime = System.Reflection.Assembly.Load(File.ReadAllBytes(project.ExternalAssemblyPath));
            object outside = Activator.CreateInstance(runtime.GetType("ExternalSamples.Data")!)!;
            runtime.GetType("ExternalSamples.Calls")!.GetMethod("Entry")!.Invoke(null, new[] { outside });
            Assert.AreEqual(writes ? 1 : 0, outside.GetType().GetField("Value")!.GetValue(outside));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            foreach (int jobs in new[] { 1, 4 })
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
                foreach (bool resume in new[] { false, true })
                {
                    if (resume)
                    {
                        calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, previous: calls);
                    }
                    Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method =>
                        method.Kind == (writes ? MethodEffectKind.Setter : MethodEffectKind.Getter)));
                    Assert.AreEqual(calls.Calls.Count, calls.Calls.Select(call => (call.CallerInstanceId, call.Call.Point)).Distinct().Count());
                }
            }
        }

        // 已闭合的只读入口与多轮补读入口并存，不必每轮重新证明前者。
        /// <summary>只复用未变化的 Getter，后续发现的写入、恢复分析及并行额度不改变结论。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncReusesUnchangedGetterProofs(bool writes)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static int ReadOnly() => 1;
                    public static void Delayed() => First();
                    private static void First() => Second();
                    private static void Second() => Third();
                    private static void Third() { WRITE }
                }
                """.Replace("WRITE", writes ? "state=1;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "ReadOnly" or "Delayed").ToArray();
            Assert.HasCount(4, roots);
            foreach (int jobs in new[] { 1, 4 })
            {
                List<string> progress = new();
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs,
                    requireCompleteCalls: false, progress: progress.Add, reportProgress: (_, proofs) =>
                    {
                        Assert.AreEqual(proofs.Methods.Count, proofs.Methods.Select(method => method.MethodId).Distinct().Count());
                        foreach (MethodEntry root in roots.Where(root => root.Name == "ReadOnly"))
                        {
                            Assert.AreEqual(MethodEffectKind.Getter, proofs.Methods.Single(method => method.MethodId == root.Id).Kind);
                        }
                    });
                Assert.IsTrue(progress.Any(message => message == "本轮复用 2 个未变化的只读结论。"), string.Join("\n", progress));
                foreach (bool resume in new[] { false, true })
                {
                    if (resume)
                    {
                        calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs, previous: calls);
                    }
                    Assert.IsEmpty(calls.PendingCalls);
                    foreach (MethodEffect effect in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
                    {
                        Assert.AreEqual(writes && roots.Single(root => root.Id == effect.MethodId).Name == "Delayed"
                            ? MethodEffectKind.Setter : MethodEffectKind.Getter, effect.Kind);
                    }
                }
            }
        }

        // 筛掉已经证明的入口时，业务对象所属程序集仍来自完整的原始输入。
        /// <summary>另一个程序集的只读根被复用后，返回该程序集新对象仍有创建证据。</summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsBusinessAssembliesWhenReusingGetterProofs()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public int Value; }
                public static class Helper
                {
                    public static int ReadOnly() => 1;
                    public static Data Create() => new Data();
                }
                """);
            project.WriteRootSource("""
                public static class Calls
                {
                    public static object Return() => Make();
                    private static object Make() => ExternalSamples.Helper.Create();
                    public static void Slow() => A();
                    private static void A() => B();
                    private static void B() => C();
                    private static void C() => D();
                    private static void D() => E();
                    private static void E() => F();
                    private static void F() => G();
                    private static void G() => H();
                    private static void H() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name is "Calls" or "Helper").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "Return" or "Slow" or "ReadOnly").ToArray();
            Assert.HasCount(3, roots);
            foreach (int jobs in new[] { 1, 4 })
            {
                bool reportedCreation = false;
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs,
                    requireCompleteCalls: false, reportProgress: (_, proofs) =>
                    {
                        MethodEffect? returned = proofs.Methods.SingleOrDefault(method => method.MethodId == roots.Single(root => root.Name == "Return").Id);
                        if (returned != null)
                        {
                            Assert.AreEqual(MethodEffectKind.Setter, returned.Kind);
                            reportedCreation = true;
                        }
                    });
                Assert.IsTrue(reportedCreation);
                Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, roots, calls).Methods
                    .Single(method => method.MethodId == roots.Single(root => root.Name == "Return").Id).Kind);
            }
        }

        // 代理实现由独立启动入口注册，使用入口仍需沿统一接口关系找到真实方法。
        /// <summary>复刻 SetImpl 注册与后续代理调用，读取和写入实现分别对照源码与 DLL。</summary>
        [TestMethod]
        [DataRow(false, 0)]
        [DataRow(true, 0)]
        [DataRow(false, 1)]
        [DataRow(true, 1)]
        [DataRow(false, 2)]
        [DataRow(true, 2)]
        public async Task ResolveAsyncConnectsProxyRegisteredBySeparateEntry(bool writes, int shape)
        {
            string source = """
                namespace Samples;
                public interface IProxy { void Run(); }
                public sealed class Implementation : IProxy
                {
                    private int state;
                    public void Run() { WRITE }
                }
                public static class Proxy
                {
                    private static IProxy implementation;
                    public static void SetImpl(IProxy value) { implementation = value; }
                    public static void Run() => implementation.Run();
                }
                public static class Calls
                {
                    public static void Initialize() => Proxy.SetImpl(new Implementation());
                    public static void Entry() => Proxy.Run();
                }
                """;
            if (shape != 0)
            {
                source = """
                    namespace Samples;
                    public interface IRuntimeMethodProxy { }
                    public interface IProxy : IRuntimeMethodProxy { void Run(); }
                    public sealed class Implementation : IProxy
                    {
                        private int state;
                        public void Run() { WRITE }
                    }
                    public class RuntimeMethodProxy<TInterface, TInstance> where TInterface : IRuntimeMethodProxy where TInstance : new()
                    {
                        protected static TInterface ms_impl;
                        public static void SetImpl(TInterface impl) { ms_impl = impl; }
                        private static TInstance ms_instance;
                        public static TInstance Instance
                        {
                            get { if (ms_instance == null) ms_instance = new TInstance(); return ms_instance; }
                        }
                    }
                    public class Proxy : RuntimeMethodProxy<IProxy, Proxy>, IProxy
                    {
                        private bool IsNull() => ms_impl == null;
                        public void Run() { if (!IsNull()) ms_impl.Run(); }
                    }
                    public static class Calls
                    {
                        public static void Initialize() => Proxy.SetImpl(new Implementation());
                        public static void Entry(Proxy value) => value.Run();
                    }
                    """.Replace(">, IProxy", shape == 2 ? ">, IProxy" : ">");
            }
            using TestProject project = TestProject.CreateWithCallTargets(source.Replace("WRITE", writes ? "state = 1;" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            Assert.HasCount(2, roots);
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEffect method in new EffectAnalyzer().Analyze(catalog, roots, calls).Methods)
            {
                Assert.AreEqual(writes ? MethodEffectKind.Setter : MethodEffectKind.Getter, method.Kind, method.MethodId);
            }
        }

        // 递归接收对象仍是旧静态快照时，途中覆盖的字段也必须进入下一次调用环境。
        /// <summary>检查覆盖静态代理后的具体调用目标，不以静态赋值的 Setter 结论代替验证。</summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsChangedStorageDuringStaticProxyRecursion()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IProxy { void Run(); }
                public sealed class Quiet : IProxy { public void Run() { } }
                public sealed class Proxy : IProxy
                {
                    private static IProxy current, next;
                    public static void SetNext(IProxy value) { next = value; }
                    public static void Dispatch() => current.Run();
                    public void Run() { var old = current; current = next; old.Run(); }
                }
                public static class Calls
                {
                    public static void Entry(Quiet quiet) { Proxy.SetNext(quiet); Proxy.Dispatch(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCall dispatch = result.Calls.Single(call => call.CallerMethodId == root.Id && call.Call.Target.Name == "Dispatch");
                ResolvedCall first = result.Calls.Single(call => call.CallerInstanceId == dispatch.Targets.Single().InstanceId);
                ResolvedCallTarget proxy = first.Targets.Single(target => catalog.TypesById[result.Methods.Single(method => method.Id == target.MethodId).TypeId].Name == "Proxy");
                ResolvedCall second = result.Calls.Single(call => call.CallerInstanceId == proxy.InstanceId);
                ResolvedCallTarget recursive = second.Targets.Single(target => target.MethodId == proxy.MethodId);
                Assert.AreNotEqual(proxy.InstanceId, recursive.InstanceId);
                ResolvedCall third = result.Calls.Single(call => call.CallerInstanceId == recursive.InstanceId);
                Assert.AreEqual("Quiet", catalog.TypesById[result.Methods.Single(method => method.Id == third.Targets.Single().MethodId).TypeId].Name);
            }
        }

        // 一个入口的剪枝或普通调用补读不能丢掉另一个入口尚未尝试的绑定。
        /// <summary>源码与 DLL 的普通、虚函数和委托入口在单路及四路下均完整闭合。</summary>
        [TestMethod]
        [DataRow("direct")]
        [DataRow("virtual")]
        [DataRow("delegate")]
        public async Task ResolveAsyncRetainsUnattemptedRootsAcrossGlobalDeferral(string kind)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IUse { void Touch(); }
                public sealed class Target : IUse { public int Value; public void Touch() { Value=1; } }
                public static class Helper { public static int Read() => 2; }
                public static class Calls
                {
                    public static void First(PARAMETER input) { FIRST }
                    public static void Second(bool flag) { SECOND Helper.Read(); }
                    private static void Never() { state=1; }
                    private static int state;
                }
                """.Replace("PARAMETER", kind == "virtual" ? "IUse" : "Target")
                .Replace("FIRST", kind == "direct" ? "Helper.Read();" : kind == "virtual" ? "input.Touch();" : "new System.Action(input.Touch)();")
                .Replace("SECOND", kind == "direct" ? "if (flag && !flag) Never();" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name is "First" or "Second").OrderBy(method => method.Id, StringComparer.Ordinal).ToArray();
            Assert.HasCount(4, roots);
            foreach (int jobs in new[] { 1, 4 })
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
                Assert.IsEmpty(calls.PendingCalls);
                foreach (MethodEntry root in roots)
                {
                    Assert.IsTrue(calls.Calls.Any(call => call.CallerMethodId == root.Id), root.Id);
                    Assert.AreEqual(root.Name == "First" && kind != "direct" ? MethodEffectKind.Setter : MethodEffectKind.Getter,
                        new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
                }
                Assert.IsFalse(calls.Calls.Any(call => call.Call.Target.Name == "Never"));
            }
        }

        // 根引用参数的对象初值进入原别名查询，不能用负编号绕过另一个可能相同的对象。
        /// <summary>两个根对象的重合尚未证明时仍明确报缺口，不越界或固定旧值。</summary>
        [TestMethod]
        [DataRow("other", "两个根参数")]
        [DataRow("input.Next", "根对象字段路径")]
        public async Task ResolveAsyncKeepsRootReferenceObjectAliasBoundary(string written, string reason)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Data { public Data Next; public int Value; }
                public static class Calls
                {
                    public static void Entry(ref Data input, Data other)
                    {
                        WRITTEN.Value = 0;
                        if (input.Value > 0) Observe(input.Value);
                    }
                    private static void Observe(int value) { }
                }
                """.Replace("WRITTEN", written));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = calls.Calls.Single(call => call.CallerMethodId == root.Id && call.Call.Target.Name == "Observe");
                AnalysisException failure = Assert.ThrowsExactly<AnalysisException>(() =>
                    calls.ValueSources.GetCallOrigins(call.Targets.Single().Arguments[0].Single()));
                Assert.Contains(reason, failure.Message);
            }
        }

        // 复刻 InitPvpConf 的引用参数与多分支循环，首次报告必须能完成，后续库闭合另行验收。
        /// <summary>读写引用对象字段不应使首轮对象来源查询指数膨胀。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncReportsKhenginePvpConfigurationBeforeFollowingLibraries(bool typedLoad)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class XmlNode { public Attributes Attributes; }
                public sealed class Attributes { public int Count; public XmlAttribute this[int index] => null; }
                public sealed class XmlAttribute { public string Name, Value; }
                public sealed class Ids { public void Clear() { } public void Add(uint id) { } }
                public static class ai
                {
                    public enum PlayerType { model, bt, human }
                    public sealed class StartArgs
                    {
                        public int p1_char_id, p2_char_id;
                        public PlayerType p1_type, p2_type;
                        public uint main_view_agent_side, p1_general_id, p2_general_id;
                        public Ids p1_friend_id, p2_friend_id;
                    }
                }
                public sealed class Calls
                {
                    public void Entry(XmlNode xmlNode, ref ai.StartArgs args)
                    {
                        if (xmlNode.Attributes == null)
                        {
                            return;
                        }
                        for (int i = 0; i < xmlNode.Attributes.Count; i++)
                        {
                            XmlAttribute attr = xmlNode.Attributes[i];
                            if (attr.Name == "p1_char_id")
                            {
                                int.TryParse(attr.Value, out args.p1_char_id);
                            }
                            else if (attr.Name == "p2_char_id")
                            {
                                int.TryParse(attr.Value, out args.p2_char_id);
                            }
                            else if (attr.Name == "p1_type")
                            {
                                int type = 2;
                                int.TryParse(attr.Value, out type);
                                if (type == 2) args.p1_type = ai.PlayerType.human;
                                else if (type == 1) args.p1_type = ai.PlayerType.bt;
                                else if (type == 0) args.p1_type = ai.PlayerType.model;
                            }
                            else if (attr.Name == "p2_type")
                            {
                                int type = 2;
                                int.TryParse(attr.Value, out type);
                                if (type == 2) args.p2_type = ai.PlayerType.human;
                                else if (type == 1) args.p2_type = ai.PlayerType.bt;
                                else if (type == 0) args.p2_type = ai.PlayerType.model;
                            }
                            else if (attr.Name == "main_view_agent_side")
                            {
                                uint.TryParse(attr.Value, out args.main_view_agent_side);
                            }
                            else if (attr.Name == "p1_friend_id")
                            {
                                uint p1FriendID = 0;
                                uint.TryParse(attr.Value, out p1FriendID);
                                args.p1_friend_id.Clear();
                                args.p1_friend_id.Add(p1FriendID);
                            }
                            else if (attr.Name == "p2_friend_id")
                            {
                                uint p2FriendID = 0;
                                uint.TryParse(attr.Value, out p2FriendID);
                                args.p2_friend_id.Clear();
                                args.p2_friend_id.Add(p2FriendID);
                            }
                            else if (attr.Name == "p1_general_id")
                            {
                                uint.TryParse(attr.Value, out args.p1_general_id);
                            }
                            else if (attr.Name == "p2_general_id")
                            {
                                uint.TryParse(attr.Value, out args.p2_general_id);
                            }
                        }
                    }
                }
                """);
            if (typedLoad)
            {
                using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                    new Mono.Cecil.ReaderParameters { InMemory = true });
                Mono.Cecil.MethodDefinition entry = module.GetType("ExternalSamples.Calls").Methods.Single(method => method.Name == "Entry");
                Mono.Cecil.Cil.Instruction[] loads = entry.Body.Instructions.Where(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldind_Ref).ToArray();
                Assert.IsNotEmpty(loads);
                foreach (Mono.Cecil.Cil.Instruction instruction in loads)
                {
                    instruction.OpCode = Mono.Cecil.Cil.OpCodes.Ldobj;
                    instruction.Operand = ((Mono.Cecil.ByReferenceType)entry.Parameters[1].ParameterType).ElementType;
                }
                module.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            using CancellationTokenSource cancellation = new();
            bool reported = false;
            await Assert.ThrowsAsync<OperationCanceledException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2,
                cancellationToken: cancellation.Token, requireCompleteCalls: false, reportProgress: (calls, effects) =>
                {
                    Assert.IsEmpty(effects.Methods);
                    Assert.HasCount(2, effects.Failures);
                    Assert.IsNotEmpty(calls.PendingCalls);
                    reported = true;
                    cancellation.Cancel();
                }));
            Assert.IsTrue(reported);
        }

        // 条件计算中途取消后必须收拢独立求解任务，同一材料可以重新发起完整分析。
        /// <summary>单路与四路均不把取消返回成已完成的调用结果。</summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(4)]
        public async Task ResolveAsyncCancelsConditionWorkAndRetries(int jobs)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    private static int state;
                    public static void Entry(int value) { CONDITIONS }
                }
                """.Replace("CONDITIONS", string.Concat(Enumerable.Range(0, 128)
                    .Select(index => $"if (value > {index} && value <= {index}) state = {index};"))));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            using CancellationTokenSource cancellation = new();
            bool started = false;
            await Assert.ThrowsAsync<OperationCanceledException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs,
                cancellation.Token, progress: message =>
                {
                    if (!started && message.StartsWith("本轮重新检查", StringComparison.Ordinal))
                    {
                        started = true;
                        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
                    }
                }));
            Assert.IsTrue(started);
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, result).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 数组地址快排只能移除不相干存储，不能跳过真正写到字段或局部变量的引用。
        /// <summary>未知数组下标不污染字段与局部值，直接和混合引用仍保留字段写入。</summary>
        [TestMethod]
        [DataRow("array", "7", "2")]
        [DataRow("field", "9", "2")]
        [DataRow("local", "7", "9")]
        [DataRow("mixed", "7,9", "2")]
        public async Task ResolveAsyncSeparatesArrayElementsFromObjectAndLocalSlots(string target, string field, string local)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder { public int Value; }
                public static class Calls
                {
                    public static void Entry(int[] array, int index, bool choose)
                    {
                        var holder = new Holder();
                        holder.Value = 7;
                        int number = 2;
                        WRITE
                        Observe(holder.Value, number);
                    }
                    private static void Observe(int field, int local) { }
                }
                """.Replace("WRITE", target switch
            {
                "array" => "array[index] ^= 1;",
                "field" => "ref int slot = ref holder.Value; slot = 9;",
                "local" => "ref int slot = ref number; slot = 9;",
                _ => "ref int slot = ref (choose ? ref array[0] : ref holder.Value); slot = 9;",
            }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsEmpty(result.PendingCalls);
            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == root.Id && call.Call.Target.Name == "Observe");
                foreach ((int index, string expected) in new[] { (0, field), (1, local) })
                {
                    ValueOrigin[] values = result.ValueSources.GetCallOrigins(call.Targets.Single().Arguments[index].Single()).ToArray();
                    Assert.IsTrue(values.All(value => value.Value.Kind == BehaviorValueKind.Constant), target);
                    CollectionAssert.AreEquivalent(expected.Split(','), values.Select(value => value.Value.Reference).Distinct().ToArray(), target);
                }
            }
        }

        // 复刻 khengine Crypt.cs:575 的函数体，后续解密算法不参与循环计数存储的读取。
        /// <summary>实例计数和字节数组地址写入不能互相扩大来源查询。</summary>
        [TestMethod]
        public async Task ResolveAsyncHandlesKhengineDecryptStoragePattern()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Calls
                {
                    private int pos, contextStart, crypt;
                    private byte[] prePlain;
                    private byte[] Decipher(byte[] input) => input;
                    public bool Entry(byte[] input, int offset, int len)
                    {
                        for (pos = 0; pos < 8; pos++)
                        {
                            if (contextStart + pos >= len)
                                return true;
                            prePlain[pos] ^= input[offset + crypt + pos];
                        }
                        prePlain = Decipher(prePlain);
                        if (prePlain == null)
                            return false;
                        contextStart += 8;
                        crypt += 8;
                        pos = 0;
                        return true;
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsEmpty(calls.PendingCalls);
            foreach (MethodEntry root in roots)
            {
                Assert.AreEqual(MethodEffectKind.Setter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind, root.Id);
            }
        }

        // 循环字段来源不应迫使同一段无环父链反复展开。
        /// <summary>交换循环与普通字段链的选择顺序，保留相同实际调用。</summary>
        [TestMethod]
        [DataRow(6, false)]
        [DataRow(12, false)]
        [DataRow(12, true)]
        public async Task ResolveAsyncHandlesDeepFieldsBesideLoopOrigins(int depth, bool reverse)
        {
            string field = "root" + string.Concat(Enumerable.Repeat(".Next", depth));
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public sealed class Node { public Node Next; public int Value; } public static class Calls { public static void Entry(Node root, bool choose, int count) { Node p = root; for (int i = 0; i < count; i++) p = p.Next; Node q = choose ? " + (reverse ? field + " : p" : "p : " + field) + "; if (q.Value != 0) Observe(); } private static void Observe() { } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsEmpty(calls.PendingCalls);
            Assert.HasCount(2, calls.Calls);
            Assert.IsTrue(calls.Calls.All(call => call.Call.Target.Name == "Observe"));
        }

        // 一个入口继续发现子调用时，没有新事实的另一个入口无需重复检查执行条件。
        /// <summary>只缩小本轮重算范围，完整调用和最终行为仍保持一致。</summary>
        [TestMethod]
        public async Task ResolveAsyncRechecksOnlyChangedRoots()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public static class Calls { private static int state; public static void Entry() => A(); public static void Quiet() { } private static void A() => B(); private static void B() => C(); private static void C() { state = 1; } }");
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.Name is "Entry" or "Quiet").ToArray();
            List<string> progress = new();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, progress: progress.Add);
            Assert.Contains("本轮重新检查 1 个入口的执行条件。", progress);
            EffectAnalysisResult effects = new EffectAnalyzer().Analyze(catalog, roots, calls);
            Assert.AreEqual(MethodEffectKind.Setter, effects.Methods.Single(method => method.MethodId == roots.Single(root => root.Name == "Entry").Id).Kind);
            Assert.AreEqual(MethodEffectKind.Getter, effects.Methods.Single(method => method.MethodId == roots.Single(root => root.Name == "Quiet").Id).Kind);
        }

        // 两个字段可以占用同一块内存，不能按字段名把另一个字段的写入当作无关。
        /// <summary>未实现重叠字段关系时明确指出缺口，绝不把默认空值当成没有 Setter。</summary>
        [TestMethod]
        public async Task ResolveAsyncDoesNotTreatOverlappingFieldAsDefaultNull()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                using System.Runtime.InteropServices;
                namespace Samples;
                [StructLayout(LayoutKind.Explicit)] public sealed class Holder
                {
                    [FieldOffset(0)] public Action First;
                    [FieldOffset(0)] public Action Second;
                }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { var holder = new Holder(); holder.Second = new Action(Change); holder.First(); }
                    private static void Change() { state++; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            foreach (MethodEntry root in ReadRoots(catalog, project, "Calls", "Entry"))
            {
                AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                Assert.Contains("显式布局", failure.Message);
            }
        }

        // 字段的类型属于实际使用点，不能套用分配函数中顺序不同的泛型参数。
        /// <summary>跨函数返回的新对象仍有默认值，条件写入保留默认和实际写入两种来源。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncKeepsDefaultFieldTypeAcrossReorderedGenericFactories(bool conditionalWrite)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Holder<T> { public T Value; }
                public static class Calls
                {
                    private static Holder<B> Make<A, B>(bool flag, B value)
                    {
                        var holder = new Holder<B>();
                        WRITE
                        return holder;
                    }
                    private static T Read<T, U>(bool flag, T value) => Make<U, T>(flag, value).Value;
                    public static void Entry(bool flag) => Observe(Read<string, int>(flag, "value"));
                    private static void Observe(string value) { }
                }
                """.Replace("WRITE", conditionalWrite ? "if (flag) holder.Value = value;" : ""));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == root.Id && call.Call.Target.Name == "Observe");
                IReadOnlyList<ValueOrigin> values = result.ValueSources.GetCallOrigins(call.Targets.Single().Arguments.Single().Single());
                Assert.IsTrue(values.All(value => value.Value.Kind == BehaviorValueKind.Constant));
                CollectionAssert.AreEquivalent(conditionalWrite ? new string?[] { null, "value" } : new string?[] { null },
                    values.Select(value => value.Value.Reference).Distinct().ToArray());
            }
        }

        // 新对象未赋值的字段有确定默认值，但构造和随后写入必须覆盖默认值。
        /// <summary>源码与 DLL 的继承泛型字段、整数和空引用遵守相同分配顺序。</summary>
        [TestMethod]
        [DataRow("int", "9", "0", false)]
        [DataRow("long", "9000000000L", "0", false)]
        [DataRow("bool", "true", "0", false)]
        [DataRow("string", "\"after\"", null, false)]
        [DataRow("int", "9", "9", true)]
        public async Task ResolveAsyncReadsAllocatedFieldDefaultsBeforeConstructorAndLaterWrites(string type, string assigned, string? initial, bool constructorWrites)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base<T> { public T Value; }
                public sealed class Holder : Base<TYPE> { public Holder() { CONSTRUCTOR } }
                public static class Calls
                {
                    public static void Entry()
                    {
                        var holder = new Holder();
                        Observe(holder.Value);
                        holder.Value = ASSIGNED;
                        Observe(holder.Value);
                    }
                    private static void Observe(TYPE value) { }
                }
                """.Replace("TYPE", type).Replace("CONSTRUCTOR", constructorWrites ? "Value = " + assigned + ";" : "").Replace("ASSIGNED", assigned));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCall[] observations = result.Calls.Where(call => call.CallerMethodId == root.Id && call.Call.Target.Name == "Observe")
                    .OrderBy(call => call.Call.Point.BlockId).ToArray();
                ValueOrigin first = result.ValueSources.GetCallOrigins(observations[0].Targets.Single().Arguments.Single().Single()).Single();
                ValueOrigin last = result.ValueSources.GetCallOrigins(observations[1].Targets.Single().Arguments.Single().Single()).Single();
                Assert.AreEqual(BehaviorValueKind.Constant, first.Value.Kind);
                Assert.AreEqual(initial, first.Value.Reference);
                Assert.AreEqual(BehaviorValueKind.Constant, last.Value.Kind);
                Assert.AreEqual(type == "string" ? "after" : type == "bool" ? "1" : type == "long" ? "9000000000" : "9", last.Value.Reference);
            }
        }

        // 长串无关读取与最后强覆盖分别检查，规模变化不能改变最终委托目标。
        /// <summary>只记录公开解析耗时，不以依赖机器负载的时间阈值决定测试成功。</summary>
        [TestMethod]
        [DataRow(32, false)]
        [DataRow(128, false)]
        [DataRow(512, false)]
        [DataRow(32, true)]
        [DataRow(128, true)]
        [DataRow(512, true)]
        public async Task ResolveAsyncScalesStoragePastReadonlyCalls(int count, bool overwrite)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public int Value; public Action Callback; }
                public static class Calls
                {
                    public static void Entry() { var holder = new Holder(); holder.Callback = new Action(Before); Use(holder); }
                    private static void Use(Holder holder) { READS OVERWRITE holder.Callback(); }
                    private static int Read(Holder holder) => holder.Value;
                    private static void Before() { }
                    private static void After() { }
                }
                """.Replace("READS", string.Concat(Enumerable.Repeat("Read(holder);", count)))
                .Replace("OVERWRITE", overwrite ? "holder.Callback = new Action(After);" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            long allocated = GC.GetTotalAllocatedBytes(true);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            stopwatch.Stop();
            Console.WriteLine($"storage-benchmark count={count} overwrite={overwrite} resolutionMs={stopwatch.Elapsed.TotalMilliseconds:F3} allocatedBytes={GC.GetTotalAllocatedBytes(true) - allocated}");
            foreach (MethodEntry root in roots)
            {
                string helper = result.Methods.Single(method => method.AssemblyName == root.AssemblyName && method.Name == "Use").Id;
                ResolvedCall use = result.Calls.Single(call => call.CallerMethodId == helper && call.Call.Kind == BehaviorCallKind.Delegate);
                Assert.AreEqual(overwrite ? "After" : "Before", result.Methods.Single(method => method.Id == use.Targets.Single().MethodId).Name);
            }
        }

        // 已被后续确定覆盖的未知旧值不能阻止识别当前委托。
        /// <summary>跳过旧存储查询不等于忽略原生调用对整个函数的未知影响。</summary>
        [TestMethod]
        [DataRow("none", false)]
        [DataRow("always", false)]
        [DataRow("conditional", false)]
        [DataRow("finally", false)]
        [DataRow("reflection", false)]
        [DataRow("none", true)]
        [DataRow("always", true)]
        [DataRow("conditional", true)]
        [DataRow("finally", true)]
        [DataRow("reflection", true)]
        public async Task ResolveAsyncReadsOnlyWritesAfterLastDefiniteOverwrite(string overwrite, bool sameMethod)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                using System.Runtime.CompilerServices;
                namespace Samples;
                public sealed class Holder { public Action Callback; }
                public static class Calls
                {
                    [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(Holder holder);
                    public static void Entry(bool flag)
                    {
                        var holder = new Holder();
                        BEFORE
                        Use(holder, flag);
                    }
                    private static void Use(Holder holder, bool flag)
                    {
                        LOCAL
                        WRITE
                        holder.Callback();
                    }
                    private static void After() { }
                }
                """.Replace("BEFORE", sameMethod ? string.Empty : "Unknown(holder);")
                .Replace("LOCAL", sameMethod ? "Unknown(holder);" : string.Empty).Replace("WRITE", overwrite switch
                {
                    "always" => "holder.Callback = new Action(After);",
                    "conditional" => "if (flag) holder.Callback = new Action(After);",
                    "finally" => "try { } finally { holder.Callback = new Action(After); }",
                    "reflection" => "typeof(Holder).GetField(\"Callback\").SetValue(holder, new Action(After));",
                    _ => string.Empty,
                }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            if (overwrite is "none" or "conditional")
            {
                Assert.Contains("Unknown", (await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, roots, 2))).Message);
                return;
            }

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                string helper = result.Methods.Single(method => method.AssemblyName == root.AssemblyName && method.Name == "Use").Id;
                ResolvedCall use = result.Calls.Single(call => call.CallerMethodId == helper && call.Call.Kind == BehaviorCallKind.Delegate);
                Assert.AreEqual("After", result.Methods.Single(method => method.Id == use.Targets.Single().MethodId).Name);
            }
            Assert.Contains("Unknown", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result)).Message);
        }

        // 字段查找选项必须作用于实际元数据，不能退回枚举 Type 的所有实现。
        /// <summary>覆盖可见性、隐藏成员、继承静态字段、大小写和只查本级。</summary>
        [TestMethod]
        [DataRow("Public | Instance", "Number", "Base")]
        [DataRow("Public | NonPublic | Instance", "Number", "Derived")]
        [DataRow("NonPublic | Instance | DeclaredOnly", "Number", "Derived")]
        [DataRow("NonPublic | Instance | IgnoreCase", "number", "Derived")]
        [DataRow("Public | Static | FlattenHierarchy", "Shared", "Base")]
        [DataRow("Public | Static", "Shared", null)]
        [DataRow("Public | Instance | DeclaredOnly", "Number", null)]
        [DataRow("Public | Static | FlattenHierarchy", "Value", "Derived")]
        [DataRow("Public | Static", "InterfaceValue", null)]
        [DataRow("Public | Static | DeclaredOnly | FlattenHierarchy", "InterfaceValue", null)]
        [DataRow("Public | Static | FlattenHierarchy", "Absent", null)]
        public async Task ResolveAsyncUsesReflectionFieldLookupOptions(string options, string name, string? expectedOwner)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public class Base { public int Number; public static int Shared; }
                public interface I { const int Value = 1; const int InterfaceValue = 2; }
                public sealed class Derived : Base, I { private new int Number; public const int Value = 3; }
                public static class Calls
                {
                    public static FieldInfo Entry() => typeof(Derived).GetField("NAME", OPTIONS);
                }
                """.Replace("NAME", name).Replace("OPTIONS", string.Join(" | ", options.Split(" | ").Select(option => "BindingFlags." + option))));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            ResolvedCall[] lookups = result.Calls.Where(call => call.Call.Target.Name == "GetField").ToArray();
            Assert.HasCount(2, lookups);
            foreach (ResolvedCall lookup in lookups)
            {
                ValueOrigin value = result.ValueSources.GetCallOrigins(new(lookup.CallerMethodId, lookup.Call.ResultValueId!.Value, lookup.CallerInstanceId)).Single();
                if (expectedOwner == null)
                {
                    Assert.AreEqual(BehaviorValueKind.Constant, value.Value.Kind);
                    Assert.IsNull(value.Value.Reference);
                }
                else
                {
                    Assert.AreEqual(expectedOwner, catalog.TypesById[value.Value.Member!.KnownDeclaringTypeId!].Name);
                }
            }
        }

        // 接口自己声明的常量字段有明确元数据，不属于继承查找分歧。
        /// <summary>直接查找接口字段仍连接实际声明，不将全部接口反射拒绝。</summary>
        [TestMethod]
        public async Task ResolveAsyncFindsDeclaredInterfaceField()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public interface I { const int Value = 1; }
                public static class Calls
                {
                    public static FieldInfo Entry() => typeof(I).GetField("Value", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            ResolvedCall[] lookups = result.Calls.Where(call => call.Call.Target.Name == "GetField").ToArray();
            Assert.HasCount(2, lookups);
            foreach (ResolvedCall lookup in lookups)
            {
                ValueOrigin value = result.ValueSources.GetCallOrigins(new(lookup.CallerMethodId, lookup.Call.ResultValueId!.Value, lookup.CallerInstanceId)).Single();
                Assert.AreEqual("I", catalog.TypesById[value.Value.Member!.KnownDeclaringTypeId!].Name);
            }
        }

        // 不同运行库对继承接口字段的查找结果不同，尚未证明时不能折成空值。
        /// <summary>覆盖单接口、多接口歧义和接口继承，并保留源码与 DLL 的相同失败证据。</summary>
        [TestMethod]
        [DataRow("Derived", "public", "Public")]
        [DataRow("Both", "public", "Public")]
        [DataRow("Child", "public", "Public")]
        [DataRow("Derived", "protected", "NonPublic")]
        [DataRow("Derived", "internal", "NonPublic")]
        public async Task ResolveAsyncDoesNotGuessInheritedInterfaceFieldLookup(string type, string visibility, string flags)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public interface I { VISIBILITY const int Value = 1; }
                public interface J { const int Value = 2; }
                public interface Child : I { }
                public sealed class Derived : I { }
                public sealed class Both : I, J { }
                public static class Calls
                {
                    private static int state;
                    public static void Entry()
                    {
                        if (typeof(TYPE).GetField("Value", BindingFlags.FLAGS | BindingFlags.Static | BindingFlags.FlattenHierarchy) != null) state = 1;
                    }
                }
                """.Replace("TYPE", type).Replace("VISIBILITY", visibility).Replace("FLAGS", flags));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            foreach (MethodEntry root in roots)
            {
                Assert.IsTrue(result.PendingCalls.Any(call => call.CallerMethodId == root.Id
                    && call.Failure?.Contains("继承接口字段", StringComparison.Ordinal) == true));
            }
            Assert.Contains("继承接口字段", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result)).Message);
        }

        // 方法类型参数先在调用方闭合，再用于另一构造类型的初始化事实。
        /// <summary>Cache 的 T 与 Length 的 U 不得在初始化环境里重复代入或丢失。</summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsConstructedStaticFieldInitializationIdentity()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Cache<T>
                {
                    private static readonly int[] Values = new int[0];
                    public static int Length<U>() => Cache<U>.Values.Length;
                }
                public static class Calls
                {
                    public static void Entry()
                    {
                        if (Cache<int>.Length<string>() > 0) Observe();
                        if (Cache<bool>.Length<int>() > 0) Observe();
                    }
                    private static void Observe() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsEmpty(result.Calls.Where(call => call.Call.Target.Name == "Observe").ToArray());
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, result).Methods.All(method => method.Kind == MethodEffectKind.Getter));
        }

        // 写入其他类型的静态字段会隐式触发初始化，不能仅因没有 call 就认定初始化无重入。
        /// <summary>跨类型初始化尚未接通时保留读取后的分支，不使用孤立读取出的零长度。</summary>
        [TestMethod]
        public async Task ResolveAsyncDoesNotAssumeIndependentCrossTypeInitialization()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Other
                {
                    public static int Flag;
                    static Other() { Calls.Entry(); }
                }
                public static class Calls
                {
                    private static readonly int[] Values;
                    static Calls() { Other.Flag = 1; Values = new int[0]; }
                    public static void Entry() { if (Values.Length > 0) Observe(); }
                    private static void Observe() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.HasCount(2, result.Calls.Where(call => call.Call.Target.Name == "Observe").ToArray());
        }

        // 普通静态调用可能先执行类型初始化器，其隐式写入不能被空方法体遮挡。
        /// <summary>未证明的前序初始化必须阻止沿用零长度，也不得产生 Getter 结论。</summary>
        [TestMethod]
        [DataRow("call")]
        [DataRow("field")]
        [DataRow("reflection")]
        public async Task ResolveAsyncKeepsInitializationBeforeStaticArrayRead(string form)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Reflection;
                namespace Samples;
                public static class Store
                {
                    private static readonly int[] Values = new int[0];
                    public static int Length() => Values.Length;
                    public static void Replace() => typeof(Store).GetField("Values", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, new int[1]);
                }
                public static class Mutator
                {
                    public static int Trigger;
                    static Mutator() { Store.Replace(); }
                    public static void Touch() { }
                }
                public static class Calls
                {
                    public static void Entry() { TRIGGER if (Store.Length() > 0) Observe(); }
                    private static void Observe() { }
                }
                """.Replace("TRIGGER", form switch
            {
                "field" => "_ = Mutator.Trigger;",
                "reflection" => "_ = typeof(Mutator).GetField(\"Trigger\").GetValue(null);",
                _ => "Mutator.Touch();",
            }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            Assert.HasCount(2, result.Calls.Where(call => call.Call.Target.Name == "Observe").ToArray());
            Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result)).Message);
        }

        // 初始化器存在语法出口，不代表之前的指令实际可以正常完成。
        /// <summary>未闭合的除零、检查溢出及非法数组长度都不能支持后续 Setter 正证。</summary>
        [TestMethod]
        [DataRow("int zero = 0; _ = 1 / zero;")]
        [DataRow("int maximum = int.MaxValue; _ = checked(maximum + 1);")]
        [DataRow("int length = -1; _ = new int[length];")]
        public async Task ResolveAsyncRejectsUnprovenInitializationCompletion(string initializer)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Store
                {
                    private static readonly int[] Values = new int[0];
                    public static int Length() => Values.Length;
                }
                public static class Mutator
                {
                    static Mutator() { INITIALIZER }
                    public static void Touch() { }
                }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() { Mutator.Touch(); if (Store.Length() == 0) state = 1; }
                }
                """.Replace("INITIALIZER", initializer));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            Assert.Contains("初始化", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result)).Message);
        }

        // 初始长度只能用于尚未被本条调用路径改写的只读静态字段。
        /// <summary>反射替换和未知原生写入不得沿用初始化时的零长度排除调用。</summary>
        [TestMethod]
        [DataRow("clean")]
        [DataRow("reflection")]
        [DataRow("unknown")]
        public async Task ResolveAsyncInvalidatesInitialLengthAfterPossibleWrite(string form)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                using System.Reflection;
                using System.Runtime.CompilerServices;
                namespace Samples;
                public static class Calls
                {
                    private static readonly int[] Values = new int[0];
                    [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown();
                    public static void Entry()
                    {
                        WRITE
                        if (Values.Length > 0) Observe();
                    }
                    private static void Observe() { }
                }
                """.Replace("WRITE", form switch
            {
                "reflection" => "typeof(Calls).GetField(\"Values\", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, new int[1]);",
                "unknown" => "Unknown();",
                _ => string.Empty,
            }), "D:/Unity21KH/Data/MonoBleedingEdge/lib/mono/unityjit-win32/mscorlib.dll");
            File.AppendAllLines(project.RootResponsePath, new[] { "-define:UNITY_EDITOR_WIN" }, encoding: new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            Assert.AreEqual(form == "clean" ? 0 : 2, result.Calls.Count(call => call.Call.Target.Name == "Observe"));
            if (form == "unknown")
            {
                Assert.Contains("Unknown", Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result)).Message);
            }
            if (form == "reflection")
            {
                Assert.IsTrue(result.PendingCalls.Any(call => call.Failure?.Contains("只读字段", StringComparison.Ordinal) == true));
            }
        }

        // 同一调用不修改第一个字段，不代表它也不修改随后查询的字段。
        /// <summary>按成员排除无关写入时，普通写入、引用写入和反射写入仍保持各自的实际目标。</summary>
        [TestMethod]
        [DataRow("direct")]
        [DataRow("ref")]
        [DataRow("reflection")]
        public async Task ResolveAsyncSeparatesReadOnlyStorageProofsByMember(string form)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action First; public Action Second; }
                public static class Calls
                {
                    public static void Entry()
                    {
                        var holder = new Holder { First = new Action(Before), Second = new Action(Before) };
                        Fill(holder, new Action(After));
                        holder.First(); holder.Second();
                    }
                    private static void Fill(Holder holder, Action next) { WRITE }
                    private static void Before() { }
                    private static void After() { }
                }
                """.Replace("WRITE", form switch
            {
                "ref" => "ref Action slot = ref holder.Second; slot = next;",
                "reflection" => "typeof(Holder).GetField(\"Second\").SetValue(holder, next);",
                _ => "holder.Second = next;",
            }));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall[] uses = result.Calls.Where(call => call.CallerMethodId == root.Id && call.Call.Kind == BehaviorCallKind.Delegate)
                    .OrderBy(call => call.Call.Point.BlockId).ToArray();
                Assert.HasCount(2, uses);
                CollectionAssert.AreEqual(new[] { "Before", "After" }, uses.Select(call => result.Methods
                    .Single(method => method.Id == call.Targets.Single().MethodId).Name).ToArray());
            }
        }

        // 先补读普通调用，再在同一批次连接所有独立的动态调用。
        /// <summary>发现一个动态目标后不会把本轮其他动态调用逐一延后。</summary>
        [TestMethod]
        public async Task ResolveAsyncSchedulesIndependentDynamicCallsTogether()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface ITarget { void First(); void Second(); void Third(); }
                public sealed class Target : ITarget
                {
                    public void First() { }
                    public void Second() { }
                    public void Third() { }
                }
                public static class Calls
                {
                    public static void Entry(ITarget target) { Prepare(); target.First(); target.Second(); target.Third(); }
                    private static void Prepare() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            List<string> batches = new();
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2, deadline.Token, progress: batches.Add);

            Assert.HasCount(2, roots);
            CollectionAssert.AreEqual(new[]
            {
                "已读取 2 个函数体，已连接 0 个调用，待处理 8 个调用。",
                "已读取 4 个函数体，已连接 2 个调用，待处理 6 个调用。",
                "已读取 10 个函数体，已连接 8 个调用，待处理 0 个调用。",
            }, batches.Where(message => message.StartsWith("已读取", StringComparison.Ordinal)).ToArray());
            Assert.IsEmpty(result.PendingCalls);
            foreach (MethodEntry root in roots)
            {
                CollectionAssert.AreEquivalent(new[] { "Prepare", "First", "Second", "Third" },
                    result.Calls.Where(call => call.CallerMethodId == root.Id).Select(call => call.Call.Target.Name).ToArray());
            }
        }

        // 条件跳转的唯一后继在真假两种情况下都执行。
        /// <summary>跳转和不跳转都到同一指令时，该唯一后继不能被条件裁剪。</summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsDegenerateConditionalSuccessor()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { private static int state; public static void Entry() { state = 1; } }");
            using (Mono.Cecil.AssemblyDefinition external = Mono.Cecil.AssemblyDefinition.ReadAssembly(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition method = external.MainModule.Types.Single(type => type.Name == "Calls").Methods.Single(method => method.Name == "Entry");
                Mono.Cecil.Cil.ILProcessor il = method.Body.GetILProcessor();
                Mono.Cecil.Cil.Instruction next = method.Body.Instructions[0];
                il.InsertBefore(next, il.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_0));
                il.InsertBefore(next, il.Create(Mono.Cecil.Cil.OpCodes.Brtrue, next));
                external.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 多个出口反向经过同一个子调用时，存储查询仍只连接实际保存的委托。
        /// <summary>pinned 和可选修饰不改变托管引用槽，非法再次取址必须明确失败。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task ResolveAsyncDistinguishesWrappedReferencesFromNestedAddresses(bool modifier, bool nestedAddress)
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Calls { public static void Entry(ref int output, int count) { ref int slot = ref output; while (count-- > 0) slot++; } }");
            using (Mono.Cecil.AssemblyDefinition external = Mono.Cecil.AssemblyDefinition.ReadAssembly(project.ExternalAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.MethodDefinition method = external.MainModule.Types.Single(type => type.Name == "Calls").Methods.Single(method => method.Name == "Entry");
                Mono.Cecil.Cil.VariableDefinition slot = method.Body.Variables.Single(variable => variable.VariableType.IsByReference);
                slot.VariableType = new Mono.Cecil.PinnedType(slot.VariableType);
                if (modifier)
                {
                    slot.VariableType = new Mono.Cecil.OptionalModifierType(external.MainModule.TypeSystem.Object, slot.VariableType);
                }
                if (nestedAddress)
                {
                    Mono.Cecil.Cil.ILProcessor il = method.Body.GetILProcessor();
                    Mono.Cecil.Cil.Instruction first = method.Body.Instructions[0];
                    il.InsertBefore(first, il.Create(Mono.Cecil.Cil.OpCodes.Ldloca, slot));
                    il.InsertBefore(first, il.Create(Mono.Cecil.Cil.OpCodes.Pop));
                }
                external.Write(project.ExternalAssemblyPath);
            }
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            if (nestedAddress)
            {
                AnalysisException failure = await Assert.ThrowsExactlyAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
                Assert.Contains("托管引用不能再次取托管地址", failure.Message);
            }
            else
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
                Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            }
        }

        // 多个出口反向经过同一个子调用时，存储查询仍只连接实际保存的委托。
        /// <summary>线性调用链不会因每层多个出口改变委托候选或重复展开为调用树。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task ResolveAsyncReadsStorageAcrossMultipleReturns(bool writes, bool bypass)
        {
            string chain = string.Join(Environment.NewLine, Enumerable.Range(1, 10).Select(index =>
                $"private static int Step{index}(Holder holder, int flag) {{ " + (bypass ? "if (flag < 0) return -1; " : string.Empty)
                    + $"Step{index - 1}(holder, flag); if (flag == 0) return 0; if (flag == 1) return 1; return 2; }}"));
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action Callback; }
                public static class Calls
                {
                    public static void Entry(int flag) { var holder = new Holder { Callback = new Action(Before) }; Step10(holder, flag); holder.Callback(); }
                    private static void Before() { }
                    private static void After() { }
                    private static void Step0(Holder holder, int flag) { WRITE }
                """.Replace("WRITE", writes ? "holder.Callback = new Action(After);" : string.Empty) + chain + "}");
            File.AppendAllLines(project.RootResponsePath, new[] { "-optimize+" }, new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            Assert.IsTrue(result.Behaviors.MethodsById.Values.Any(body => body.Returns.Count >= 3));
            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == root.Id && call.Call.Kind == BehaviorCallKind.Delegate);
                CollectionAssert.AreEquivalent(writes ? bypass ? new[] { "Before", "After" } : new[] { "After" } : new[] { "Before" },
                    call.Targets.Select(target => result.Methods.Single(method => method.Id == target.MethodId).Name).ToArray());
            }
        }

        // 真实无实现工厂返回的循环节点不能被当作本轮空路径而丢弃。
        /// <summary>循环固定点不会把必需但未知的虚调用提前绑定为空目标。</summary>
        [TestMethod]
        public async Task ResolveAsyncRetainsUnknownFactoryBeforeParentLoop()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System.Runtime.CompilerServices;
                namespace Samples;
                public class Node { public Node Parent; public virtual int Read() => 1; }
                public static class Calls
                {
                    [MethodImpl(MethodImplOptions.InternalCall)]
                    private static extern Node Create();
                    public static int Entry(bool again)
                    {
                        Node current = Create();
                        while (again) current = current.Parent;
                        return current.Read();
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Types.Where(type => type.Name == "Calls").SelectMany(catalog.GetMethods)
                .Where(method => method.Name == "Entry").ToArray();
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2, requireCompleteCalls: false);
            Assert.HasCount(2, roots);
            Assert.HasCount(2, result.PendingCalls.Where(call => call.Call.Target.Name == "Read").ToArray());
            Assert.IsFalse(result.Calls.Any(call => call.Call.Target.Name == "Read"));
            Assert.ThrowsExactly<AnalysisException>(() => new EffectAnalyzer().Analyze(catalog, roots, result));
        }

        // 复刻 ICollection 通过真实实现与转交门面同时出现在类型层级中的写法。
        /// <summary>同一真实接口契约仅计算一次，不能因两种引用名称重复建字典而崩溃。</summary>
        [TestMethod]
        public async Task ResolveAsyncMergesInterfaceAliasesAfterResolvingCarrier()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface ICall { void Run(); }
                public class Base : ICall { public int Value; public virtual void Run() { Value++; } }
                public sealed class Derived : Base { }
                public static class Calls { public static void Entry(ICall target) => target.Run(); }
                """);
            using (Mono.Cecil.AssemblyDefinition external = Mono.Cecil.AssemblyDefinition.ReadAssembly(project.ExternalAssemblyPath,
                       new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.AssemblyNameDefinition name = new("InterfaceFacade", new Version(1, 0, 0, 0));
                using Mono.Cecil.AssemblyDefinition facade = Mono.Cecil.AssemblyDefinition.CreateAssembly(name, "InterfaceFacade", Mono.Cecil.ModuleKind.Dll);
                Mono.Cecil.AssemblyNameReference targetAssembly = Mono.Cecil.AssemblyNameReference.Parse(external.Name.FullName);
                facade.MainModule.AssemblyReferences.Add(targetAssembly);
                string interfaceNamespace = external.MainModule.Types.Single(type => type.Name == "ICall").Namespace;
                facade.MainModule.ExportedTypes.Add(new Mono.Cecil.ExportedType(interfaceNamespace, "ICall", facade.MainModule, targetAssembly)
                {
                    Attributes = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Forwarder,
                });
                facade.Write(project.ForwardTargetPath);
                Mono.Cecil.AssemblyNameReference reference = Mono.Cecil.AssemblyNameReference.Parse(name.FullName);
                external.MainModule.AssemblyReferences.Add(reference);
                external.MainModule.Types.Single(type => type.Name == "Derived").Interfaces.Add(new Mono.Cecil.InterfaceImplementation(
                    new Mono.Cecil.TypeReference(interfaceNamespace, "ICall", external.MainModule, reference)));
                external.Write(project.ExternalAssemblyPath);
            }
            File.AppendAllLines(project.RootResponsePath, new[] { $"-reference:\"{project.ForwardTargetPath}\"" }, new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                Assert.HasCount(1, calls.Calls.Single(call => call.CallerMethodId == root.Id).Targets);
            }
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, calls).Methods.All(method => method.Kind == MethodEffectKind.Setter));
        }

        // 每轮递归创建的新容器不是同一个存储位置，不允许误复用或无限创建调用实例。
        /// <summary>相关存储地址自身无法有限闭合时输出明确失败证据。</summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsRecursiveTemporaryStorageIdentity()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action Callback; }
                public static class Calls
                {
                    public static void Entry() => Walk(new Action(Quiet));
                    private static void Walk(Action callback) { var holder = new Holder { Callback = callback }; holder.Callback(); Walk(callback); }
                    private static void Quiet() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            foreach (MethodEntry root in ReadRoots(catalog, project, "Calls", "Entry"))
            {
                AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                Assert.Contains("递归创建对象的存储身份", failure.Message);
            }
        }

        // 显式改名项目库既有预载元数据入口，也有实际文件名入口。
        /// <summary>通过物理文件名请求时，仍接回同一份真实基类函数。</summary>
        [TestMethod]
        public async Task ResolveAsyncUsesExplicitCarrierThroughFileName()
        {
            using TestProject project = TestProject.CreateWithLegacyFileNameRequest();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.FullName == "UnityFrameworkConsumer.Derived"))
                .Single(method => method.Name == ".ctor");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
            MethodEntry target = result.Methods.Single(method => method.Id == result.Calls.Single(call => call.CallerMethodId == root.Id).Targets.Single().MethodId);
            Assert.AreEqual(Path.Combine(project.RootPath, "ProjectFramework.dll"), target.AssemblyPath);
        }

        // Unity 非严格装载不能因为某份项目文件的完整版本刚好匹配就忽略另一合法载体。
        /// <summary>真正调用命中跨版本竞争载体时，须列出所有物理候选并明确失败。</summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsAmbiguousLegacyCarrierWhenCalled()
        {
            using TestProject project = TestProject.CreateWithLegacyCarrierCollision();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.FullName == "UnityFrameworkConsumer.Derived"))
                .Single(method => method.Name == ".ctor");
            AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
            Assert.Contains("ProjectFramework.dll", failure.Message);
            Assert.Contains(project.UnityRuntimeFrameworkPath, failure.Message);
        }

        // 相同 typeof 值跨递归调用仍代表同一类型，不能按每轮指令实例制造无限状态。
        /// <summary>类型从已有参数切换到确定类型后，只再展开一次实际调用。</summary>
        [TestMethod]
        public async Task ResolveAsyncReusesSemanticTypeInRecursiveArguments()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Node { }
                public static class Calls
                {
                    public static void Entry() => Walk(typeof(string));
                    private static void Walk(Type type) { Read(type); Walk(typeof(Node)); }
                    private static void Read(Type type) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                MethodEntry walk = result.Methods.Single(method => method.Name == "Walk" && method.AssemblyPath == root.AssemblyPath);
                Assert.HasCount(2, result.Calls.Where(call => call.CallerMethodId == walk.Id)
                    .Select(call => call.CallerInstanceId).Distinct());
            }
        }

        // 无限分配、相关数值计算和不能保存的参数关联必须失败，不能无限扩展或误合并。
        /// <summary>递归规划无法有限闭合时，源码和 DLL 都保留明确失败证据。</summary>
        [TestMethod]
        [DataRow("public static void Entry() => Walk(new Node()); private static void Walk(Node item) { item.Touch(); Walk(new Node()); }", "递归创建对象")]
        [DataRow("public static void Entry() => Walk(new Node[2], 0); private static void Walk(Node[] items, int index) { items[index] = new Node(); Walk(items, index + 1); }", "递归实参来源")]
        [DataRow("public static void Entry(bool flag) { var a = new Node(); var b = new Node(); Walk(flag ? a : b, flag ? b : a); } private static void Walk(Node left, Node right) { left.Touch(); Walk(right, left); }", "递归实参的分支对应")]
        public async Task ResolveAsyncRejectsUnboundedRecursiveInputStates(string members, string diagnostic)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Node { private int state; public void Touch() { state++; } }
                public static class Calls { MEMBERS }
                """.Replace("MEMBERS", members));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            foreach (MethodEntry root in ReadRoots(catalog, project, "Calls", "Entry"))
            {
                AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                Assert.Contains(diagnostic, failure.Message);
            }
        }

        // 仅实际相关的双定义阻断调用，无关继承结构只用于排除候选。
        /// <summary>两份物理定义均保留；任一分支相关时，真实继承仍必须唯一。</summary>
        [TestMethod]
        [DataRow(0, 2, false)]
        [DataRow(1, 2, false)]
        [DataRow(2, 2, false)]
        [DataRow(0, 0, false)]
        [DataRow(0, 1, false)]
        [DataRow(1, 0, false)]
        [DataRow(1, 1, false)]
        [DataRow(1, 0, true)]
        [DataRow(1, 1, true)]
        public async Task ResolveAsyncDefersUnneededAmbiguousHierarchy(int relatedCarriers, int referencedCarriers, bool missingSecondType)
        {
            using TestProject project = TestProject.CreateWithDuplicateRuntimeDefinitions(relatedCarriers, referencedCarriers, missingSecondType);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            Assert.HasCount(referencedCarriers, catalog.Types.Where(type => type.FullName == "Shared.Base"));
            _ = catalog.DerivedTypesByBaseId;
            Assert.HasCount(missingSecondType ? 1 : 2, catalog.Types.Where(type => type.FullName == "Shared.Base"));
            foreach (TypeEntry definition in catalog.Types.Where(type => type.FullName == "Shared.Base"))
            {
                Assert.IsTrue(catalog.DerivedTypesByBaseId[definition.Id].Any(type => type.Name == "Unrelated"));
            }
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCallTarget target = result.Calls.Single(call => call.CallerMethodId == root.Id).Targets.Single();
                Assert.EndsWith(".Known", result.Methods.Single(method => method.Id == target.MethodId).TypeName);
            }
            foreach (MethodEntry root in ReadRoots(catalog, project, "Calls", "Unknown"))
            {
                if (relatedCarriers == 0 || root.AssemblyPath != project.ExternalAssemblyPath)
                {
                    CallTargetResolutionResult openResult = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
                    ResolvedCallTarget target = openResult.Calls.Single(call => call.CallerMethodId == root.Id).Targets.Single();
                    Assert.EndsWith(".Known", openResult.Methods.Single(method => method.Id == target.MethodId).TypeName);
                    continue;
                }
                AnalysisException failure = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                Assert.Contains("FirstCarrier.dll", failure.Message);
                Assert.Contains("SecondCarrier.dll", failure.Message);
            }
        }

        // 改名的显式类实现仍按声明类参数重排到实际虚槽。
        /// <summary>Derived&lt;string,int&gt; 的第二个参数对应 Base&lt;int&gt;，newslot 不取消显式实现。</summary>
        [TestMethod]
        public async Task ResolveAsyncSubstitutesReorderedClassMethodImplementation()
        {
            using TestProject project = TestProject.CreateWithGenericClassMethodImplementation();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCallTarget target = result.Calls.Single(call => call.CallerMethodId == root.Id && call.Call.Kind == BehaviorCallKind.Virtual).Targets.Single();
                MethodEntry implementation = result.Methods.Single(method => method.Id == target.MethodId);
                Assert.AreEqual(root.AssemblyPath == project.ExternalAssemblyPath ? "Implementation" : "Run", implementation.Name);
                CollectionAssert.AreEqual(new[] { "System.String", "System.Int32" }, target.DeclaringTypeArguments.ToArray());
                Assert.AreEqual("!1", implementation.Parameters.Single().TypeId);
            }
        }

        // 多层门面和相同程序集名的不同版本都沿真实转交记录选择虚函数。
        /// <summary>实际调用同时命中唯一运行基类和当前派生类，不混入参考门面。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncFollowsLayeredForwardersForVirtualSlot(bool sameAssemblyName)
        {
            using TestProject project = sameAssemblyName ? TestProject.CreateWithSameNameVersionedForwardingFacades()
                : TestProject.CreateWithTwoForwardingFacades();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.FullName == "ForwardedConsumer.Derived"))
                .Single(method => method.Name == "Call");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
            MethodEntry[] targets = result.Calls.Single(call => call.CallerMethodId == root.Id).Targets
                .Select(target => result.Methods.Single(method => method.Id == target.MethodId)).ToArray();
            CollectionAssert.AreEquivalent(new[] { project.ExternalAssemblyPath, project.ForwardTargetPath }, targets.Select(method => method.AssemblyPath).ToArray());
            Assert.IsTrue(targets.All(method => method.Name == "Touch"));
        }

        // 只有派生程序集直接参与编译时，仍从真实间接基类找到继承的接口实现。
        /// <summary>接口与抽象基类按需加载，调用落到唯一实际派生函数。</summary>
        [TestMethod]
        public async Task ResolveAsyncFindsInterfaceInheritedFromLookupOnlyBase()
        {
            using TestProject project = TestProject.CreateWithTransitiveInterfaceHierarchy();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.Name == "Caller")).Single();
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
            ResolvedCallTarget target = result.Calls.Single(call => call.CallerMethodId == root.Id).Targets.Single();
            Assert.AreEqual("LookupConsumer.Derived", result.Methods.Single(method => method.Id == target.MethodId).TypeName);
            Assert.AreEqual(project.ForwardTargetPath, catalog.Types.Single(type => type.FullName == "LookupBase.Base").AssemblyPath);
        }

        // 间接 DLL 中的实现不能因未出现在现有继承链上而被漏掉。
        /// <summary>开放接口调用必须连接仅由普通字段类型带入的实现，并证明其静态写入。</summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(4)]
        public async Task ResolveAsyncFindsImplementationFromAssemblyReferenceClosure(int jobs)
        {
            using TestProject project = TestProject.CreateWithLookupOnlyImplementation();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, jobs);
            Assert.IsFalse(catalog.Types.Any(type => type.FullName == "LookupImplementation.Worker"));
            MethodEntry[] roots = catalog.Types.Where(type => type.Name is "Caller" or "SourceCaller")
                .SelectMany(type => catalog.GetMethods(type)).ToArray();
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, jobs);
            Assert.HasCount(2, roots);
            foreach (MethodEntry root in roots)
            {
                ResolvedCallTarget target = result.Calls.Single(call => call.CallerMethodId == root.Id).Targets.Single();
                Assert.AreEqual("LookupImplementation.Worker", result.Methods.Single(method => method.Id == target.MethodId).TypeName);
            }
            Assert.IsTrue(new EffectAnalyzer().Analyze(catalog, roots, result).Methods.All(method => method.Kind == MethodEffectKind.Setter));
            Assert.IsFalse(catalog.Types.Any(type => type.Name == "Plugin"));
            AnalysisException failure = Assert.ThrowsExactly<AnalysisException>(() => catalog.LoadAssemblyTypes(
                System.Reflection.AssemblyName.GetAssemblyName(project.MissingForwardTargetPath), project.ExternalAssemblyPath));
            Assert.Contains("派发预扫描后出现新程序集", failure.Message);
        }

        // 多个分析请求共用同一个目录时不能读到补载前的过期关系。
        /// <summary>并发解析真实虚调用，每次均取得同一物理基类和派生类目标。</summary>
        [TestMethod]
        public async Task ResolveAsyncClosesTransitiveVirtualSlotForConcurrentReaders()
        {
            using TestProject project = TestProject.CreateWithTransitiveExternalDependency();
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.Name == "DerivedType"))
                .Single(method => method.Name == "Call");
            Assert.IsFalse(catalog.Types.Any(type => type.FullName == "TransitiveSamples.BaseType"));
            CallTargetResolutionResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 1)));
            foreach (CallTargetResolutionResult result in results)
            {
                MethodEntry[] targets = result.Calls.Single(call => call.CallerMethodId == root.Id).Targets
                    .Select(target => result.Methods.Single(method => method.Id == target.MethodId)).ToArray();
                CollectionAssert.AreEquivalent(new[] { project.ExternalAssemblyPath, project.ForwardTargetPath }, targets.Select(method => method.AssemblyPath).ToArray());
                Assert.IsTrue(targets.All(method => method.Name == "Touch"));
            }
        }

        // 类的实参不能替换虚函数自身的泛型实参。
        /// <summary>源码和 DLL 的构造基类虚槽同时保留 int 类型实参与 string 函数实参。</summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsMethodParameterInConstructedVirtualSlot()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base<T> { public virtual U Echo<U>(U value) => value; }
                public sealed class Derived : Base<int> { public override U Echo<U>(U value) => value; }
                public static class Calls { public static string Entry(Base<int> target) => target.Echo<string>("value"); }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == root.Id);
                Assert.HasCount(2, call.Targets);
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    MethodEntry method = result.Methods.Single(method => method.Id == target.MethodId);
                    CollectionAssert.AreEqual(new[] { "System.String" }, target.Reference.GenericArgumentTypeIds.ToArray());
                    CollectionAssert.AreEqual(method.TypeName.EndsWith(".Derived", StringComparison.Ordinal)
                        ? Array.Empty<string>() : new[] { "System.Int32" }, target.DeclaringTypeArguments.ToArray());
                    Assert.AreEqual("!!0", method.Parameters.Single().TypeId);
                }
            }
        }

        // 验证递归返回来源尚未求解时不会丢掉递归分支并冒称目标已完整。
        /// <summary>
        /// 统一调用环境以后仍保留递归返回的明确失败证据。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsUnclosedRecursiveReturnValues()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Worker { public virtual void Run() { } }
                public static class Calls
                {
                    public static void Entry(bool stop, Worker target) => Again(stop, target).Run();
                    private static Worker Again(bool stop, Worker target) => stop ? target : Again(stop, target);
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);

            foreach (MethodEntry root in ReadRoots(catalog, project, "Calls", "Entry"))
            {
                AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(error.Message, "递归返回值");
            }
        }

        // 验证接收对象明确由本函数创建时，不读取另一实现的无关缺失基类。
        /// <summary>
        /// 已知新对象只会调用自己的实现，不能先展开同一接口的全部无关类型。
        /// </summary>
        [TestMethod]
        [DataRow("Entry")]
        [DataRow("KnownParameter")]
        [DataRow("KnownField")]
        [DataRow("Mixed")]
        [DataRow("Unknown")]
        public async Task ResolveAsyncNarrowsCreatedReceiverBeforeClosingUnrelatedHierarchy(string entry)
        {
            using TestProject project = TestProject.CreateWithTransitiveInterfaceHierarchy(missingUnrelatedBase: true);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", entry);
            Assert.IsNotNull(catalog.Types.Single(type => type.FullName == "LookupConsumer.Derived").BaseType);

            foreach (MethodEntry root in roots)
            {
                if (entry is "Unknown" or "Mixed")
                {
                    AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                        new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                    StringAssert.Contains(error.Message, "LookupBase");
                }
                else
                {
                    CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
                    ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id && item.Call.Target.Name == "Run");
                    TypeEntry known = catalog.Types.Single(type => type.AssemblyPath == root.AssemblyPath && type.Name == "Known");
                    Assert.AreEqual(catalog.GetMethods(known).Single(method => method.Name.EndsWith("Run", StringComparison.Ordinal)).Id, call.Targets.Single().MethodId);
                }
            }
        }

        // 验证泛型转换代入具体类以后仍能找到该类真正的重写函数。
        /// <summary>
        /// 转换值保留实际类型身份，不生成当前程序集下的虚构类型。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncPreservesConcreteTypeOfGenericCast()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base { public virtual int Read() => 1; }
                public sealed class Actual : Base { public override int Read() => 2; }
                public static class Calls
                {
                    public static int Entry(object value) => Cast<Actual>(value).Read();
                    private static T Cast<T>(object value) => (T)value;
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                TypeEntry actual = catalog.Types.Single(type => type.AssemblyPath == root.AssemblyPath && type.Name == "Actual");
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id && item.Call.Target.Name == "Read");
                Assert.HasCount(1, call.Targets, string.Join("; ", call.Targets.Select(target => target.MethodId)));
                Assert.AreEqual(catalog.GetMethods(actual).Single(method => method.Name == "Read").Id, call.Targets.Single().MethodId);
                ValueOrigin conversion = result.ValueSources.GetCallOrigins(call.Targets.Single().Receiver.Single(), retainTypeChecks: true)
                    .Single(origin => origin.Value.Kind == BehaviorValueKind.Conversion);
                Assert.AreEqual(actual.Id, conversion.Value.Type!.Id);
                Assert.AreEqual(actual.Id, conversion.Value.Type.DefinitionId);
            }
        }

        // 验证未指定参数与已知参数可能相同时，不能只留下默认接口实现。
        /// <summary>
        /// 两个参数均为整数时类成员与接口签名相同，只有一个是整数时则不同。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsSignaturesDependingOnUnspecifiedParameters()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IValue<T> { int Read(T value) => 10; }
                public sealed class Value<TKey, TValue> : IValue<TValue> { public int Read(TKey value) => 20; }
                public static class Calls { public static int Entry(IValue<int> value) => value.Read(1); }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");

            foreach (MethodEntry root in roots)
            {
                AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(error.Message, "未指定类型参数参与类型比较");
            }
        }

        // 验证另一参数的约束也可能使用未指定的键类型，不能只检查键自己的约束。
        /// <summary>
        /// TValue 已知仍不代表其与未知 TKey 的接口关系已被证明。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsConstraintsDependingOnUnspecifiedParameters()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IKey<T> { }
                public sealed class Payload : IKey<string> { }
                public interface IValue<T> { T Read(); }
                public sealed class Value<TKey, TValue> : IValue<TValue> where TValue : IKey<TKey>
                {
                    public TValue Read() => default;
                }
                public static class Calls { public static Payload Entry(IValue<Payload> value) => value.Read(); }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");

            foreach (MethodEntry root in roots)
            {
                AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(error.Message, "泛型候选的合法构造尚未闭合");
            }
        }

        // 类型参数不改变具体操作时，沿已经确定的定义继续判断。
        /// <summary>
        /// 接口候选中的开放类型同样区分纯读取、静态写入和业务对象返回。
        /// </summary>
        [TestMethod]
        [DataRow("type", MethodEffectKind.Getter)]
        [DataRow("array", MethodEffectKind.Getter)]
        [DataRow("default", MethodEffectKind.Getter)]
        [DataRow("static", MethodEffectKind.Setter)]
        [DataRow("creation", MethodEffectKind.Setter)]
        public async Task ResolveAsyncChecksOnlyTypeSensitiveGenericOperations(string operation, MethodEffectKind expected)
        {
            string body = operation switch
            {
                "type" => "return typeof(TKey);",
                "array" => "return new TKey[1];",
                "default" => "return default(TKey);",
                "static" => "Cache<TKey>.Value = 1; return null;",
                "creation" => "return new Cache<TKey>();",
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IValue { object Read(); }
                public sealed class Value<TKey> : IValue { public object Read() { BODY } }
                public sealed class Cache<TKey> { public static int Value; }
                public static class Calls { public static object Entry(IValue value) => value.Read(); }
                """.Replace("BODY", body, StringComparison.Ordinal));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");

            foreach (MethodEntry root in roots)
            {
                CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
                Assert.AreEqual(expected, new EffectAnalyzer().Analyze(catalog, new[] { root }, result).Methods.Single().Kind);
            }
        }

        // 验证接口没有使用的类类型参数可保留，而不是随便选择一种具体类型。
        /// <summary>
        /// 复刻 Grouping 的键类型不影响元素读取；两个声明的剩余参数不能串用。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRetainsUnusedGenericParametersWithoutMixingDeclarations()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IValue<T> { T Read(); }
                public sealed class First<TKey, TValue> : IValue<TValue>
                {
                    private TValue[] m_values;
                    public TValue Read() => Forwarder<TKey>.Read(m_values);
                }
                public sealed class Second<TKey, TValue> : IValue<TValue>
                {
                    private TValue[] m_values;
                    public TValue Read() => Forwarder<TKey>.Read(m_values);
                }
                public static class Forwarder<TKey> { public static TValue Read<TValue>(TValue[] values) => values[0]; }
                public static class Calls { public static int Entry(IValue<int> value) => value.Read(); }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                Assert.HasCount(2, call.Targets);
                string[] retained = call.Targets.Select(target => target.DeclaringTypeArguments[0]).ToArray();
                Assert.AreNotEqual(retained[0], retained[1]);
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    Assert.AreEqual("System.Int32", target.DeclaringTypeArguments[1]);
                    ResolvedCall forwarding = result.Calls.Single(entry => entry.CallerInstanceId == target.InstanceId);
                    CollectionAssert.AreEqual(new[] { target.DeclaringTypeArguments[0] }, forwarding.Targets.Single().DeclaringTypeArguments.ToArray());
                    CollectionAssert.AreEqual(new[] { "System.Int32" }, forwarding.Targets.Single().Reference.GenericArgumentTypeIds.ToArray());
                }
            }
        }

        // 验证具名类型作为函数泛型参数传递时，逐层调用仍使用同一个真实类型。
        /// <summary>
        /// 源码和 DLL 均从普通入口经过泛型转发再调用接口，不能把名称与真实定义串用。
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncKeepsNamedMethodArgumentsThroughGenericForwarding(bool useDelegate)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Payload { }
                public interface IWork<T> { T Run(T value); }
                public sealed class Worker<T> : IWork<T> { public T Run(T value) => value; }
                public static class Calls
                {
                    public static Payload Entry(IWork<Payload> target, Payload value) => ENTRY;
                    private static T Forward<T>(IWork<T> target, T value) => Invoke(target, value);
                    private static T Invoke<T>(IWork<T> target, T value) => target.Run(value);
                }
                """.Replace("ENTRY", useDelegate
                    ? "new Func<IWork<Payload>, Payload, Payload>(Forward<Payload>)(target, value)"
                    : "Forward(target, value)", StringComparison.Ordinal));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string payload = catalog.Types.Single(type => type.AssemblyPath == root.AssemblyPath
                    && type.Name == "Payload").Id;
                ResolvedCall first = result.Calls.Single(call => call.CallerMethodId == root.Id
                    && call.Targets.Any(target => target.Reference.Name == "Forward"));
                ResolvedCallTarget forward = first.Targets.Single();
                CollectionAssert.AreEqual(new[] { payload }, forward.Reference.GenericArgumentTypeIds.ToArray());
                ResolvedCall second = result.Calls.Single(call => call.CallerInstanceId == forward.InstanceId);
                ResolvedCallTarget invoke = second.Targets.Single();
                CollectionAssert.AreEqual(new[] { payload }, invoke.Reference.GenericArgumentTypeIds.ToArray());
                ResolvedCall third = result.Calls.Single(call => call.CallerInstanceId == invoke.InstanceId);
                ResolvedCallTarget implementation = third.Targets.Single();
                CollectionAssert.AreEqual(new[] { payload }, implementation.DeclaringTypeArguments.ToArray());
                Assert.AreEqual("Worker<T>", result.Methods.Single(method => method.Id == implementation.MethodId).TypeName.Split('.').Last());
            }
        }

        // 验证同类同时存在公开索引器和指定接口的索引器时，接口调用选择后者。
        /// <summary>
        /// 复刻 Unity 实际 ArraySegment 的三个索引读取入口，不按名字任选一个。
        /// </summary>
        [TestMethod]
        [DataRow("sealed class")]
        [DataRow("struct")]
        public async Task ResolveAsyncPrefersExplicitInterfaceImplementation(string kind)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IMutable<T> { T this[int index] { get; } }
                public interface IReadOnly<T> { T this[int index] { get; } }
                public KIND Segment<T> : IMutable<T>, IReadOnly<T>
                {
                    public T this[int index] => default;
                    T IMutable<T>.this[int index] => default;
                    T IReadOnly<T>.this[int index] => default;
                }
                public static class Calls
                {
                    public static int Mutable(IMutable<int> target) => target[0];
                    public static int ReadOnly(IReadOnly<int> target) => target[0];
                    public static int Direct(Segment<int> target) => target[0];
                }
                """.Replace("KIND", kind));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Mutable", "ReadOnly", "Direct");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                Assert.HasCount(1, call.Targets);
                MethodEntry target = result.Methods.Single(method => method.Id == call.Targets[0].MethodId);
                Assert.AreEqual(root.Name == "Direct", target.IsPublic);
                Assert.IsTrue(root.Name == "Direct" ? target.Name == "get_Item"
                    : target.Name.Contains("I" + root.Name + "<T>.get_Item", StringComparison.Ordinal));
            }
        }

        // 验证构造接口的类型实参即使不出现在函数参数里，也仍决定唯一实现。
        /// <summary>
        /// 两个 Run 的参数和返回完全相同，必须用方法实现表区分整数和字符串接口。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsExplicitTargetsWithoutGenericMethodParametersSeparate()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork<T> { void Run(); }
                public sealed class Both : IWork<int>, IWork<string>
                {
                    void IWork<int>.Run() { IntegerImplementation(); }
                    void IWork<string>.Run() { TextImplementation(); }
                    private static void IntegerImplementation() { }
                    private static void TextImplementation() { }
                }
                public static class Calls
                {
                    public static void Integer(IWork<int> target) { target.Run(); }
                    public static void Text(IWork<string> target) { target.Run(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Integer", "Text");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                Assert.HasCount(1, call.Targets);
                ResolvedCall implementation = result.Calls.Single(entry => entry.CallerMethodId == call.Targets[0].MethodId);
                Assert.AreEqual(root.Name + "Implementation", implementation.Call.Target.Name);
            }
        }

        // 验证同一默认接口函数在不同类型实参下仍分别选择正确实例。
        /// <summary>
        /// 默认 Run 的参数和返回不包含 T，也不能把整数和字符串实例合并。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncSeparatesConstructedDefaultInterfaceMethods()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IDefault<T> { void Run() { Marker<T>.Touch(); } }
                public static class Marker<T> { public static void Touch() { } }
                public sealed class Both : IDefault<int>, IDefault<string> { }
                public static class Calls
                {
                    public static void Integer(IDefault<int> target) { target.Run(); }
                    public static void Text(IDefault<string> target) { target.Run(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Integer", "Text");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                Assert.HasCount(1, call.Targets);
                CollectionAssert.AreEqual(new[] { root.Name == "Integer" ? "System.Int32" : "System.String" },
                    call.Targets[0].DeclaringTypeArguments.ToArray());
            }
        }

        // 验证共享公开实现、部分显式实现及派生类重新声明接口的选择互不干扰。
        /// <summary>
        /// 同一公开方法可实现两个构造接口；派生类只有重新声明接口才改变原来的对应。
        /// </summary>
        [TestMethod]
        [DataRow("Shared")]
        [DataRow("Mixed")]
        [DataRow("Inherited")]
        [DataRow("Reimplemented")]
        [DataRow("GenericInherited")]
        [DataRow("PartialReimplemented")]
        public async Task ResolveAsyncKeepsImplicitAndInheritedInterfaceMappings(string mode)
        {
            string declaration = mode switch
            {
                "Shared" => "public sealed class Both : IWork<int>, IWork<string> { public void Run() { Markers.Shared(); } }",
                "Mixed" => "public sealed class Both : IWork<int>, IWork<string> { void IWork<int>.Run() { Markers.Integer(); } public void Run() { Markers.Text(); } }",
                "Inherited" => "public sealed class Both : Base { public void Run() { Markers.Wrong(); } }",
                "Reimplemented" => "public sealed class Both : Base, IWork<int>, IWork<string> { public void Run() { Markers.Shared(); } }",
                "GenericInherited" => "public abstract class GenericBase<T> : IWork<T> { void IWork<T>.Run() { Markers.Text(); } } public sealed class Both : GenericBase<string> { public void Run() { Markers.Wrong(); } }",
                "PartialReimplemented" => "public sealed class Both : Base, IWork<int> { public void Run() { Markers.Shared(); } }",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork<T> { void Run(); }
                public abstract class Base : IWork<int>, IWork<string>
                {
                    void IWork<int>.Run() { Markers.Integer(); }
                    void IWork<string>.Run() { Markers.Text(); }
                }
                DECLARATION
                public static class Markers
                {
                    public static void Integer() { }
                    public static void Text() { }
                    public static void Shared() { }
                    public static void Wrong() { }
                }
                public static class Calls
                {
                    public static void Integer(IWork<int> target) { target.Run(); }
                    public static void Text(IWork<string> target) { target.Run(); }
                }
                """.Replace("DECLARATION", declaration, StringComparison.Ordinal));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", mode == "GenericInherited" ? new[] { "Text" } : new[] { "Integer", "Text" });

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                Assert.HasCount(1, call.Targets);
                ResolvedCall[] implementationCalls = result.Calls.Where(entry => entry.CallerMethodId == call.Targets[0].MethodId).ToArray();
                Assert.IsNotEmpty(implementationCalls);
                string expected = mode is "Shared" or "Reimplemented" || mode == "PartialReimplemented" && root.Name == "Integer" ? "Shared" : root.Name;
                Assert.IsTrue(implementationCalls.All(entry => entry.Call.Target.Name == expected));
            }
        }

        // 验证不同编译核心库先按材料中的已证明对应还原，再判断原始类型是否唯一。
        /// <summary>
        /// 此测试从目录公开材料入口开始，不代替材料模块对运行文件对应关系的独立验证。
        /// </summary>
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task ResolveAsyncUnifiesPrimitiveTypesThroughRuntimeMappings(bool shareRuntime)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Item { }
                public interface IIn<in T> { void Put(T value); }
                public sealed class Consumer<T> : IIn<T> { public void Put(T value) { } }
                public static class Calls { public static void Run(IIn<Item> target) { target.Put(null); } }
                """);
            MaterialSet material = await project.LoadWithAlternateCoreLibraryAsync(shareRuntime);
            Assert.HasCount(2, material.SourceAssemblies.Select(assembly => assembly.Compilation
                .GetTypeByMetadataName("System.Object")!.ContainingAssembly.Identity.GetDisplayName()).Distinct().ToArray());
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Run");
            if (!shareRuntime)
            {
                AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
                StringAssert.Contains(error.Message, "原始类型存在不同运行定义：System.Object");
                return;
            }

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string item = catalog.Types.Single(type => type.FullName == root.TypeName.Split('.')[0] + ".Item").Id;
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                CollectionAssert.AreEquivalent(new[] { item, "System.Object" },
                    call.Targets.Select(target => target.DeclaringTypeArguments.Single()).ToArray());
            }
        }

        // 验证嵌套模板按真实可赋值范围绑定，不提前拒绝，也不只取精确代表值。
        /// <summary>
        /// 内层不变参数只取 Item，内层协变参数同时保留 Item 与 Child。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncBindsNestedVariantTemplates()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Item { }
                public sealed class Child : Item { }
                public sealed class Box<T> { }
                public interface IOut<out T> { T Get(); }
                public sealed class Invariant<T> : IOut<Box<T>> { public Box<T> Get() => default; }
                public sealed class Variant<T> : IOut<IOut<T>> { public IOut<T> Get() => default; }
                public static class Calls
                {
                    public static void Exact(IOut<Box<Item>> target) { target.Get(); }
                    public static void Both(IOut<IOut<Item>> target) { target.Get(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Exact", "Both");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string prefix = root.TypeName.Split('.')[0];
                string item = catalog.Types.Single(type => type.FullName == prefix + ".Item").Id;
                string child = catalog.Types.Single(type => type.FullName == prefix + ".Child").Id;
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                CollectionAssert.AreEquivalent(root.Name == "Exact" ? new[] { item } : new[] { item, child },
                    call.Targets.Select(target => target.DeclaringTypeArguments.Single()).ToArray());
                Assert.IsTrue(call.Targets.All(target => result.Methods.Single(method => method.Id == target.MethodId)
                    .TypeName == prefix + (root.Name == "Exact" ? ".Invariant<T>" : ".Variant<T>")));
            }
        }

        // 验证变体类型自身的约束也被检查，不能制造源码根本无法声明的构造类型。
        /// <summary>
        /// ICov&lt;object&gt; 不满足约束，但普通 object 仍是合法上界。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncChecksConstraintsOfExpandedVariantTypes()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Item { }
                public sealed class Child : Item { }
                public interface ICov<out T> where T : Item { }
                public interface IIn<in T> { void Put(T value); }
                public sealed class Consumer<T> : IIn<T> { public void Put(T value) { } }
                public static class Calls { public static void Run(IIn<ICov<Child>> target) { target.Put(null); } }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string prefix = root.TypeName.Split('.')[0];
                string item = catalog.Types.Single(type => type.FullName == prefix + ".Item").Id;
                string child = catalog.Types.Single(type => type.FullName == prefix + ".Child").Id;
                string covariant = catalog.Types.Single(type => type.FullName == prefix + ".ICov<T>").Id;
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                CollectionAssert.AreEquivalent(new[] { covariant + "<" + item + ">", covariant + "<" + child + ">", "System.Object" },
                    call.Targets.Select(target => target.DeclaringTypeArguments.Single()).ToArray());
            }
        }

        // 验证已经闭合的泛型边界能继续反推出派生定义的全部类型参数。
        /// <summary>
        /// 泛型边界自身、固定派生类和可以唯一构造的泛型派生类均为合法候选。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncExpandsClosedGenericVarianceBounds()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Item { }
                public class Base<T> { }
                public sealed class Fixed : Base<Item> { }
                public sealed class Derived<T> : Base<T> { }
                public sealed class Wrong : Base<string> { }
                public interface IOut<out T> { T Get(); }
                public sealed class Producer<T> : IOut<T> { public T Get() => default; }
                public static class Calls { public static void Run(IOut<Base<Item>> target) { target.Get(); } }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string prefix = root.TypeName.Split('.')[0];
                string item = catalog.Types.Single(type => type.FullName == prefix + ".Item").Id;
                string baseType = catalog.Types.Single(type => type.FullName == prefix + ".Base<T>").Id;
                string derived = catalog.Types.Single(type => type.FullName == prefix + ".Derived<T>").Id;
                string fixedType = catalog.Types.Single(type => type.FullName == prefix + ".Fixed").Id;
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                CollectionAssert.AreEquivalent(new[] { baseType + "<" + item + ">", fixedType, derived + "<" + item + ">" },
                    call.Targets.Select(target => target.DeclaringTypeArguments.Single()).ToArray());
            }
        }

        // 验证字符串的已闭合泛型接口不会使反向类型范围中断。
        /// <summary>
        /// 使用实际运行库的公开反射结果对照字符串可赋值的全部直接上界。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncIncludesClosedGenericInterfaceBounds()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IIn<in T> { void Put(T value); }
                public sealed class Consumer<T> : IIn<T> { public void Put(T value) { } }
                public static class Calls { public static void Run(IIn<string> target) { target.Put(null); } }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            // 用运行时类型标记对照真实目录，不复用待测的类型推导算法。
            string ReadExpectedType(Type type)
            {
                if (type == typeof(string) || type == typeof(object) || type.IsPrimitive)
                {
                    return type.FullName!;
                }

                Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                string identity = catalog.Types.Single(entry => entry.AssemblyPath == definition.Assembly.Location
                    && entry.MetadataToken == definition.MetadataToken).Id;
                return type.IsGenericType ? identity + "<" + string.Join(",", type.GenericTypeArguments.Select(ReadExpectedType)) + ">" : identity;
            }

            string[] expected = typeof(string).GetInterfaces().Select(ReadExpectedType)
                .Concat(new[] { "System.String", "System.Object" }).ToArray();
            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                CollectionAssert.AreEquivalent(expected, call.Targets.Select(target => target.DeclaringTypeArguments.Single()).ToArray());
            }
        }

        // 验证接口允许派生或基类参数时，开放实现枚举所有满足关系的实际类型。
        /// <summary>
        /// 复刻 HitArea 从只读列表参数调用成员时遇到的开放泛型实现。
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncInfersVariantReceiverArgumentsFromDeclaredBounds(bool interfaceBound)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public ITEM_DECLARATION Item { }
                public sealed class Child : Item { }
                public sealed class Unrelated { }
                public interface IOut<out T> { T Get(); }
                public interface IIn<in T> { void Put(T value); }
                public sealed class Producer<T> : IOut<T> { public T Get() => default; }
                public sealed class Consumer<T> : IIn<T> { public void Put(T value) { } }
                public static class Calls
                {
                    public static void Produce(IOut<Item> target) { target.Get(); }
                    public static void Consume(IIn<Item> target) { target.Put(null); }
                }
                """.Replace("ITEM_DECLARATION", interfaceBound ? "interface" : "class", StringComparison.Ordinal));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Produce", "Consume");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string prefix = root.TypeName.Split('.')[0];
                string item = catalog.Types.Single(type => type.FullName == prefix + ".Item").Id;
                string child = catalog.Types.Single(type => type.FullName == prefix + ".Child").Id;
                ResolvedCall call = result.Calls.Single(entry => entry.CallerMethodId == root.Id);
                CollectionAssert.AreEquivalent(root.Name == "Produce" ? new[] { item, child } : new[] { item, "System.Object" },
                    call.Targets.Select(target => target.DeclaringTypeArguments.Single()).ToArray());
                Assert.IsTrue(call.Targets.All(target => result.Methods.Single(method => method.Id == target.MethodId)
                    .TypeName == prefix + (root.Name == "Produce" ? ".Producer<T>" : ".Consumer<T>")));
            }
        }

        // 验证字段声明经类型转交后，错版本的同名接口与参数不能混入候选。
        /// <summary>
        /// 原始字段引用、调用契约和实现签名必须共同指向真实运行类型。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsForwardedFieldTypesSeparateFromOtherVersions()
        {
            using TestProject project = TestProject.CreateWithForwardedMethodSignature(includeFieldCollision: true);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry consumer = catalog.Types.Single(type => type.FullName == "SignatureConsumer");
            MethodEntry root = catalog.GetMethods(consumer).Single(method => method.Name == "ReadField");
            Assert.IsTrue(catalog.Types.Any(type => type.FullName == "ForwardedSignature.WrongWorker"));

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
            ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);

            CollectionAssert.AreEquivalent(new[] { "GenericWorker", "ExplicitGenericWorker" },
                call.Targets.Select(target => result.Methods.Single(method => method.Id == target.MethodId).TypeName).ToArray());
            TypeEntry actualValue = catalog.Types.Single(type => type.FullName == "ForwardedSignature.Value"
                && type.AssemblyPath == project.ForwardTargetPath);
            CollectionAssert.AreEqual(new[] { actualValue.Id }, call.Call.Target.Identity.Parameters.Select(type => type.Text).ToArray());
            Assert.AreEqual(actualValue.Id, call.Call.Target.Identity.ReturnType.Text);
            Assert.IsTrue(call.Targets.All(target => result.Methods.Single(method => method.Id == target.MethodId)
                .AssemblyPath == project.ExternalAssemblyPath));
        }

        // 验证字段的声明类型约束调用候选，并沿所属类代入实际类型参数。
        /// <summary>
        /// 独立读取实例或静态字段时，只连接声明类型允许的接口实现。
        /// </summary>
        [TestMethod]
        [DataRow("int")]
        [DataRow("Payload")]
        public async Task ResolveAsyncUsesDeclaredFieldTypesForVirtualReceivers(string argumentType)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Payload { }
                public interface IWork<T> { void Run(T value); }
                public sealed class Worker<T> : IWork<T>
                {
                    public void Run(T value) { }
                }
                public sealed class TextWorker : IWork<string>
                {
                    public void Run(string value) { }
                }
                public sealed class ValueWorker : IWork<Payload>
                {
                    public void Run(Payload value) { }
                }
                public sealed class Holder<T>
                {
                    public IWork<T> Work;
                    public void Invoke(T value) { this.Work.Run(value); }
                }
                public static class Calls
                {
                    public static IWork<ARGUMENT> Work;
                    public static void FromInstance(Holder<ARGUMENT> holder) { holder.Invoke(default); }
                    public static void FromStatic() { Work.Run(default); }
                }
                """.Replace("ARGUMENT", argumentType, StringComparison.Ordinal));
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "FromInstance", "FromStatic");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            ResolvedCall[] calls = result.Calls.Where(call => call.Call.Target.Name == "Run").ToArray();

            Assert.HasCount(4, calls);
            foreach (ResolvedCall call in calls)
            {
                MethodEntry caller = result.Methods.Single(method => method.Id == call.CallerMethodId);
                string prefix = caller.TypeName.Split('.')[0];
                CollectionAssert.AreEquivalent(argumentType == "int"
                    ? new[] { prefix + ".Worker<T>" }
                    : new[] { prefix + ".Worker<T>", prefix + ".ValueWorker" },
                    call.Targets.Select(target => result.Methods.Single(method => method.Id == target.MethodId).TypeName).ToArray());
                string expectedType = argumentType == "int" ? "System.Int32"
                    : catalog.Types.Single(type => type.FullName == prefix + ".Payload").Id;
                ResolvedCallTarget genericTarget = call.Targets.Single(target => result.Methods.Single(method =>
                    method.Id == target.MethodId).TypeName == prefix + ".Worker<T>");
                CollectionAssert.AreEqual(new[] { expectedType }, genericTarget.DeclaringTypeArguments.ToArray());
            }
        }

        // 验证泛型实参代入后看似同名的重载仍按原始声明区分。
        /// <summary>
        /// 实参为 int 时，Pick(T) 与 Pick(int) 不能因参数替换被合并。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncDistinguishesOpenGenericOverloadSignatures()
        {
            using TestProject project = TestProject.CreateWithForwardedMethodSignature();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry consumer = catalog.Types.Single(type => type.FullName == "SignatureConsumer");
            MethodEntry root = catalog.GetMethods(consumer).Single(method => method.Name == "RunOverload");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, new[] { root }, 2);
            ResolvedCall call = result.Calls.Single(item => item.Call.Target.Name == "Pick");

            Assert.HasCount(1, call.Targets);
            MethodEntry target = result.Methods.Single(method => method.Id == call.Targets[0].MethodId);
            Assert.AreEqual(project.ForwardTargetPath, target.AssemblyPath);
            Assert.AreEqual("System.Int32", target.Parameters.Single().TypeId);
        }

        // 验证本地定义标记与同名外部类型引用分别连接各自的真实函数。
        /// <summary>
        /// 对照 Unity Mono 中本地直接引用和外部按名称转交的调用结果。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncDistinguishesLocalDefinitionsFromForwardedNameLookups()
        {
            using TestProject project = TestProject.CreateWithDefinitionAndSameFileForwarder();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "SameFileForwardingConsumer.ExternalDerived");
            catalog.RequireClosedHierarchy(external);
            TypeEntry local = catalog.Types.Single(type => type.AssemblyPath == project.ExternalReferencePath
                && type.FullName == "SameFileForwarding.LocalDerived");
            MethodEntry[] roots = catalog.GetMethods(local).Where(method =>
                    method.Name is "CallLocal" or "CreateAndCall")
                .Append(catalog.GetMethods(external).Single(method => method.Name == "CallExternal"))
                .ToArray();

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall[] calls = result.Calls.Where(call => call.CallerMethodId == root.Id).ToArray();
                Assert.HasCount(root.Name == "CreateAndCall" ? 2 : 1, calls);
                string expectedPath = root.Name == "CallExternal"
                    ? project.ForwardTargetPath : project.ExternalReferencePath;
                foreach (ResolvedCall call in calls)
                {
                    Assert.HasCount(1, call.Targets);
                    MethodEntry target = result.Methods.Single(method => method.Id == call.Targets[0].MethodId);
                    Assert.AreEqual(expectedPath, target.AssemblyPath, root.Name);
                }
            }
        }

        // 验证跨文件写入和读取同一字段时不丢失来源，也不混入同名本地字段。
        /// <summary>
        /// 本地类型与转交类型分别保存并调用各自的唯一回调。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncSeparatesLocalAndForwardedFieldStorage()
        {
            using TestProject project = TestProject.CreateWithDefinitionAndSameFileForwarder();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "SameFileForwardingConsumer.ExternalDerived");
            catalog.RequireClosedHierarchy(external);
            TypeEntry local = catalog.Types.Single(type => type.AssemblyPath == project.ExternalReferencePath
                && type.FullName == "SameFileForwarding.LocalDerived");
            MethodEntry[] roots =
            {
                catalog.GetMethods(local).Single(method => method.Name == "FieldLocal"),
                catalog.GetMethods(external).Single(method => method.Name == "FieldExternal"),
            };

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);
            ResolvedCall[] callbacks = result.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Delegate).ToArray();

            Assert.HasCount(2, callbacks);
            foreach (ResolvedCall call in callbacks)
            {
                MethodEntry caller = result.Methods.Single(method => method.Id == call.CallerMethodId);
                bool isLocal = caller.AssemblyPath == project.ExternalReferencePath;
                Assert.HasCount(1, call.Targets);
                MethodEntry target = result.Methods.Single(method => method.Id == call.Targets[0].MethodId);
                Assert.AreEqual(isLocal ? "LocalCallback" : "ExternalCallback", target.Name);
                Assert.AreEqual(isLocal ? project.ExternalReferencePath : project.ExternalAssemblyPath, target.AssemblyPath);
            }
        }

        // 验证委托基类经过真实转交文件后仍能连接绑定函数。
        /// <summary>
        /// 委托类型按最终基类定义识别，不依赖引用处的程序集名称。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRecognizesDelegateBaseThroughForwarder()
        {
            using TestProject project = TestProject.CreateWithDefinitionAndSameFileForwarder();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry external = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "SameFileForwardingConsumer.ExternalDerived");
            catalog.RequireClosedHierarchy(external);
            TypeEntry local = catalog.Types.Single(type => type.AssemblyPath == project.ExternalReferencePath
                && type.FullName == "SameFileForwarding.LocalDerived");
            MethodEntry root = catalog.GetMethods(local).Single(method => method.Name == "DelegateLocal");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, new[] { root }, 2);
            ResolvedCall call = result.Calls.Single(item => item.Call.Kind == BehaviorCallKind.Delegate);

            Assert.HasCount(1, call.Targets);
            MethodEntry target = result.Methods.Single(method => method.Id == call.Targets[0].MethodId);
            Assert.AreEqual("LocalCallback", target.Name);
            Assert.AreEqual(project.ExternalReferencePath, target.AssemblyPath);
        }

        // 验证回调存进字段或数组后，读取的是同一对象同一位置的最后赋值。
        /// <summary>
        /// 覆盖旧回调、对象隔离和数组下标隔离在源码与动态链接库中一致。
        /// </summary>
        [TestMethod]
        [DataRow("Field")]
        [DataRow("Array")]
        [DataRow("SameReceiver")]
        public async Task ResolveAsyncReadsStoredCallbacksAtTheirUsePosition(string name)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action Callback; }
                public sealed class Calls
                {
                    // 对不同对象赋值不能混入，已覆盖的值也不能进入调用。
                    public void Field()
                    {
                        Holder first = new Holder();
                        Holder second = new Holder();
                        first.Callback = Old;
                        second.Callback = Other;
                        first.Callback = Current;
                        first.Callback();
                    }
                    // 不同下标与不同数组分别保存，最后一次赋值覆盖旧值。
                    public void Array()
                    {
                        Action[] first = new Action[2];
                        Action[] second = new Action[2];
                        first[0] = Old;
                        first[1] = Other;
                        second[0] = Other;
                        first[0] = Current;
                        first[0]();
                    }
                    // 同一变量没有被重新赋值时，写后读取必然命中刚保存的回调。
                    public void SameReceiver(bool condition)
                    {
                        Holder first = new Holder();
                        Holder second = new Holder();
                        first.Callback = Old;
                        second.Callback = Other;
                        Holder target = condition ? first : second;
                        target.Callback = Current;
                        target.Callback();
                    }
                    // 提供已经覆盖的旧目标。
                    public void Old() { }
                    // 提供另一个存储位置的目标。
                    public void Other() { }
                    // 提供实际被调用的目标。
                    public void Current() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", name);

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id && item.Call.Kind == BehaviorCallKind.Delegate);
                Assert.HasCount(1, call.Targets);
                Assert.AreEqual("Current", result.Methods.Single(method => method.Id == call.Targets[0].MethodId).Name);
            }
        }

        // 验证不同调用实例保存的字段值可以跨函数读取而不串线。
        /// <summary>
        /// 同一创建、保存和调用函数各执行两次时，每个对象只调用自己最后保存的回调。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncSeparatesStoredCallbacksAcrossMethodCalls()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder
                {
                    private Action m_callback;
                    public Holder(Action callback) { this.m_callback = callback; }
                    public void Save(Action callback) { this.m_callback = callback; }
                    public void Invoke() { this.m_callback(); }
                }
                public sealed class Calls
                {
                    // 同一个创建函数的两次调用必须产生不同对象。
                    private Holder Create(Action callback) { return new Holder(callback); }
                    public void Root()
                    {
                        Holder first = Create(Old);
                        Holder second = Create(Other);
                        first.Invoke();
                        second.Invoke();
                        first.Save(Current);
                        second.Save(FinalOther);
                        first.Invoke();
                        second.Invoke();
                    }
                    public void Old() { }
                    public void Other() { }
                    public void Current() { }
                    public void FinalOther() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Root");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (string prefix in new[] { "SourceSamples", "ExternalSamples" })
            {
                ResolvedCall[] calls = result.Calls.Where(call =>
                        call.Call.Kind == BehaviorCallKind.Delegate
                        && result.Methods.Single(method => method.Id == call.CallerMethodId)
                            .TypeName == prefix + ".Holder")
                    .ToArray();
                Assert.HasCount(4, calls);
                Assert.IsTrue(calls.All(call => call.Targets.Count == 1));
                CollectionAssert.AreEquivalent(
                    new[] { "Old", "Other", "Current", "FinalOther" },
                    calls.Select(call => result.Methods.Single(method =>
                        method.Id == call.Targets[0].MethodId).Name).ToArray());
            }
        }

        // 验证明确定义为空的委托没有执行目标，也不会抹掉同一合流中的真实目标。
        /// <summary>
        /// 局部空值、空值合流和字段清空在源码与动态链接库中得到准确目标。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncSkipsExplicitNullDelegateOrigins()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder
                {
                    private Action m_callback;
                    public Holder(Action callback) { this.m_callback = callback; }
                    public void Clear() { this.m_callback = null; }
                    public void Invoke() { if (this.m_callback != null) { this.m_callback(); } }
                }
                public sealed class Calls
                {
                    // 明确为空的委托即使进入调用指令也没有函数目标。
                    public void DirectNull(bool invoke)
                    {
                        Action callback = null;
                        if (invoke) { callback(); }
                    }
                    // 空值分支不影响另一条分支中的真实函数目标。
                    public void Mixed(bool condition)
                    {
                        Action callback = condition ? Current : null;
                        if (callback != null) { callback(); }
                    }
                    // 已经清空的字段不能重新读到构造时保存的旧目标。
                    public void FieldClear()
                    {
                        Holder holder = new Holder(Current);
                        holder.Clear();
                        holder.Invoke();
                    }
                    public void Current() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(
                catalog,
                project,
                "Calls",
                "DirectNull",
                "Mixed",
                "FieldClear");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (string prefix in new[] { "SourceSamples", "ExternalSamples" })
            {
                MethodEntry direct = result.Methods.Single(method =>
                    method.TypeName == prefix + ".Calls" && method.Name == "DirectNull");
                MethodEntry mixed = result.Methods.Single(method =>
                    method.TypeName == prefix + ".Calls" && method.Name == "Mixed");
                MethodEntry invoke = result.Methods.Single(method =>
                    method.TypeName == prefix + ".Holder" && method.Name == "Invoke");
                Assert.IsEmpty(result.Calls.Single(call =>
                    call.CallerMethodId == direct.Id && call.Call.Kind == BehaviorCallKind.Delegate).Targets);
                Assert.IsEmpty(result.Calls.Where(call => call.CallerMethodId == invoke.Id && call.Call.Kind == BehaviorCallKind.Delegate));
                Assert.IsTrue(result.Behaviors.MethodsById[invoke.Id].Calls.Any(call => call.Kind == BehaviorCallKind.Delegate));
                ResolvedCall mixedCall = result.Calls.Single(call =>
                    call.CallerMethodId == mixed.Id && call.Call.Kind == BehaviorCallKind.Delegate);
                Assert.HasCount(1, mixedCall.Targets);
                Assert.AreEqual("Current", result.Methods.Single(method =>
                    method.Id == mixedCall.Targets[0].MethodId).Name);
            }
        }

        // 验证被调函数通过实例字段找到真实存储对象，无关委托构造不影响结果。
        /// <summary>
        /// 源码和动态链接库均跟踪外层对象字段中的内层回调存储。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncFollowsStoredObjectsAcrossCalls()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder
                {
                    public Action Callback;
                    public Holder(Action callback) { this.Callback = callback; }
                    public void Invoke() { this.Callback(); }
                }
                public sealed class Outer
                {
                    public Holder Child;
                    public Outer(Holder child) { this.Child = child; }
                    public void Save(Action callback) { this.Child.Callback = callback; }
                }
                public sealed class Calls
                {
                    public void Nested()
                    {
                        Holder holder = new Holder(Old);
                        Outer outer = new Outer(holder);
                        outer.Save(Current);
                        _ = new Action(Other);
                        holder.Invoke();
                    }
                    public void Old() { }
                    public void Current() { }
                    public void Other() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Nested");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (string prefix in new[] { "SourceSamples", "ExternalSamples" })
            {
                MethodEntry invoke = result.Methods.Single(method =>
                    method.TypeName == prefix + ".Holder" && method.Name == "Invoke");
                ResolvedCall call = result.Calls.Single(item =>
                    item.CallerMethodId == invoke.Id && item.Call.Kind == BehaviorCallKind.Delegate);
                Assert.HasCount(1, call.Targets);
                Assert.AreEqual("Current", result.Methods.Single(method =>
                    method.Id == call.Targets[0].MethodId).Name);
            }
        }

        // 原生函数可以通过参数对象图写入，未证明时不能沿用旧值。
        /// <summary>
        /// 源码和动态链接库均明确拒绝尚未闭合的原生对象图效果。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsNativeObjectGraphEffects()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                using System.Runtime.InteropServices;
                namespace Samples;
                public sealed class Holder
                {
                    public Action Callback;
                    public Holder(Action callback) { this.Callback = callback; }
                    public void Invoke() { this.Callback(); }
                }
                public sealed class Outer
                {
                    public Holder Child;
                    public Outer(Holder child) { this.Child = child; }
                }
                public sealed class Calls
                {
                    [DllImport("native")]
                    private static extern void Native(Outer value);
                    public void NativeBoundary()
                    {
                        Holder holder = new Holder(Current);
                        Outer outer = new Outer(holder);
                        Native(outer);
                        holder.Invoke();
                    }
                    public void Current() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(
                project.AssemblyDefinitionPath,
                2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);

            foreach (MethodEntry root in ReadRoots(catalog, project, "Calls", "NativeBoundary"))
            {
                AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                    new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2));
                StringAssert.Contains(exception.Message, "跨函数存储效果尚未闭合");
            }
        }

        // 尚未完整支持的相关分支、循环对象与静态初值不能被误报为已经闭合。
        /// <summary>
        /// 回归审计反例的明确失败，后续逐项实现后改为准确目标断言。
        /// </summary>
        [TestMethod]
        [DataRow("Correlated", "分支对应尚未闭合")]
        [DataRow("Reallocated", "循环创建对象的存储身份尚未闭合")]
        [DataRow("ReallocatedFinally", "循环创建对象的存储身份尚未闭合")]
        [DataRow("Static", "调用目标来源尚未闭合")]
        public async Task ResolveAsyncRejectsUnclosedStoredCallbackRelations(string name, string message)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action Callback; public Holder Next; }
                public sealed class Calls
                {
                    // 对象和值由同一分支成对产生，不能独立组合。
                    public void Correlated(bool condition)
                    {
                        Holder first = new Holder(); Holder second = new Holder();
                        first.Callback = Old;
                        Holder target; Action callback;
                        if (condition) { target = first; callback = Current; }
                        else { target = second; callback = Other; }
                        target.Callback = callback;
                        first.Callback();
                    }
                    // 同一创建位置在循环中产生不同实例。
                    public void Reallocated()
                    {
                        Holder first = new Holder(); first.Callback = Old;
                        for (int index = 0; index < 2; index++)
                        {
                            Holder current = new Holder();
                            if (index == 0) { first = current; current.Callback = Current; }
                            else { current.Callback = Other; }
                        }
                        first.Callback();
                    }
                    // finally 会返回循环，不能把其中反复创建的对象当作唯一一次分配。
                    public void ReallocatedFinally()
                    {
                        Holder first = new Holder(); first.Callback = Old;
                        for (int index = 0; index < 2; index++)
                        {
                            try { }
                            finally
                            {
                                Holder current = new Holder();
                                if (index == 0) { first = current; current.Callback = Current; }
                                else { current.Callback = Other; }
                            }
                        }
                        first.Callback();
                    }
                    // 字段回指自身且参与循环赋值时不能递归到进程崩溃。
                    public void Cycle(bool condition)
                    {
                        Holder current = new Holder(); current.Next = current; current.Callback = Current;
                        while (condition) { current = current.Next; }
                        current.Callback();
                    }
                    // 静态方法组产生的缓存初值仍需跨函数存储闭合。
                    public static void Static()
                    {
                        Holder holder = new Holder(); holder.Callback = StaticTarget; holder.Callback();
                    }
                    // 提供三个彼此不同的绑定目标。
                    public void Old() { }
                    public void Current() { }
                    public void Other() { }
                    // 提供静态缓存的目标。
                    public static void StaticTarget() { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", name);

            AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() =>
                new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));

            StringAssert.Contains(exception.Message, message);
        }

        // 新对象与入口参数必不相同，两个入口参数则可能指向同一对象。
        /// <summary>按真实对象身份处理字段弱写，不通过参数名称排除别名。</summary>
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task ResolveAsyncDistinguishesNewObjectFromPossibleRootAliases(bool allocate)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Action Callback; }
                public sealed class Calls
                {
                    public void Entry(bool condition, Holder input, Holder external)
                    {
                        Holder first = INITIAL;
                        first.Callback = Old;
                        Holder target = TARGET;
                        target.Callback = Current;
                        first.Callback();
                    }
                    public void Old() { }
                    public void Current() { }
                    public void Other() { }
                }
                """.Replace("INITIAL", allocate ? "new Holder()" : "input")
                .Replace("TARGET", allocate ? "condition ? first : external" : "external"));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            if (!allocate)
            {
                AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
                StringAssert.Contains(exception.Message, "两个根参数是否指向同一对象尚未闭合");
                return;
            }
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            foreach (MethodEntry root in roots)
            {
                MethodEntry[] targets = result.Calls.Where(call => call.CallerMethodId == root.Id && call.Call.Kind == BehaviorCallKind.Delegate)
                    .SelectMany(call => call.Targets).Select(target => result.Methods.Single(method => method.Id == target.MethodId)).ToArray();
                CollectionAssert.AreEquivalent(new[] { "Old", "Current" }, targets.Select(method => method.Name).ToArray());
                Assert.IsTrue(targets.All(method => method.TypeId == root.TypeId));
            }
        }

        // 根字段路径先应用真实写入，既不能沿用被替换初值，也不能假设不同路径不别名。
        /// <summary>通过具体委托目标检验字段输入身份与读取快照，不借先前写入掩盖错误。</summary>
        [TestMethod]
        [DataRow("var fresh = new Holder(); old.Left = fresh; fresh.Callback = Current; old.Left.Callback();", "Current")]
        [DataRow("old.Left.Callback = Old; old.Right.Callback = Current; old.Left.Callback();", null)]
        [DataRow("var snapshot = old.Left; old.Left = new Holder(); old.Left.Callback = Current; snapshot.Callback = Old; snapshot.Callback();", "Old")]
        [DataRow("old.Left.Callback = Current; old.Left.Callback();", "Current")]
        public async Task ResolveAsyncPreservesRootFieldInputsAndSnapshots(string body, string? expected)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Holder Left; public Holder Right; public Action Callback; }
                public sealed class Calls
                {
                    public void Entry(Holder old) { BODY }
                    public void Old() { }
                    public void Current() { }
                }
                """.Replace("BODY", body));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            if (expected == null)
            {
                AnalysisException exception = await Assert.ThrowsAsync<AnalysisException>(() => new CallTargetResolver().ResolveAsync(material, catalog, roots, 2));
                StringAssert.Contains(exception.Message, "根对象字段路径之间的别名关系尚未闭合");
                return;
            }
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            ResolvedCallTarget[] targets = result.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Delegate).SelectMany(call => call.Targets).ToArray();
            Assert.HasCount(2, targets);
            Assert.IsTrue(targets.All(target => result.Methods.Single(method => method.Id == target.MethodId).Name == expected));
        }

        // 循环字段回指同一对象时，稳定后的委托来源仍只有实际保存的函数。
        /// <summary>不因遇到循环拒绝分析，也不把尚未赋给字段的其他函数列为目标。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ResolveAsyncClosesSelfReferencingFieldLoop(bool throughHelpers)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Holder { public Holder Next; public Action Callback; }
                public sealed class Calls
                {
                    public void Entry(bool condition)
                    {
                        var current = new Holder(); current.Next = current; current.Callback = Current;
                        while (condition) { current = NEXT; }
                        COPY
                        current.Callback();
                    }
                    private Holder Follow(Holder node) => node.Next;
                    private void Copy(Holder node) { node.Callback = node.Next.Callback; }
                    public void Current() { }
                    public void Other() { }
                }
                """.Replace("NEXT", throughHelpers ? "Follow(current)" : "current.Next")
                .Replace("COPY", throughHelpers ? "Copy(current);" : string.Empty));
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            ResolvedCallTarget[] targets = result.Calls.Where(call => call.Call.Kind == BehaviorCallKind.Delegate)
                .SelectMany(call => call.Targets).ToArray();
            Assert.HasCount(2, targets);
            Assert.IsTrue(targets.All(target => result.Methods.Single(method => method.Id == target.MethodId).Name == "Current"));
        }

        // 验证参考版本里的虚调用连接到已证明对应的运行库及其派生实现。
        /// <summary>
        /// 参考与运行版本不同不会丢失普通调用或真实重写目标。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncUsesProvenUnityFrameworkRedirect()
        {
            using TestProject project = TestProject.CreateWithUnityFrameworkVersionRemapping();
            project.WriteRootSource("""
                using UnityFrameworkTypes;
                public static class Calls
                {
                    // 从参考声明调用实际运行库的虚函数。
                    public static int Root(BaseType value) { return value.Read(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable && method.Name == "Root").ToArray();

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            Assert.HasCount(1, roots);
            ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == roots[0].Id);
            MethodEntry[] targets = call.Targets.Select(target => result.Methods.Single(method => method.Id == target.MethodId)).ToArray();
            CollectionAssert.AreEquivalent(new[] { "UnityFrameworkTypes.BaseType", "UnityFrameworkConsumer.Derived" },
                targets.Select(method => method.TypeName).ToArray());
            Assert.IsTrue(targets.All(method => method.Name == "Read"));
            Assert.AreEqual(project.UnityRuntimeFrameworkPath, targets.Single(method => method.TypeName == "UnityFrameworkTypes.BaseType").AssemblyPath);
        }

        // 验证方差转换使用对象已经确定的类型，必不调用的空值或失败转换保留零目标。
        /// <summary>
        /// 协变、逆变和空值在源码及托管输入下得到相同的可执行目标集合。
        /// </summary>
        [TestMethod]
        [DataRow("Covariant", "Get", "System.String")]
        [DataRow("Contravariant", "Accept", "System.Object")]
        [DataRow("Invalid", "Get", null)]
        [DataRow("Null", "Run", null)]
        [DataRow("Outer", "Get", "System.String")]
        [DataRow("InvalidOuter", "Get", null)]
        public async Task ResolveAsyncChecksClosedVarianceAndImpossibleCalls(string name, string invokedName, string? argument)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IOut<out T> { T Get(); }
                public interface IIn<in T> { void Accept(T value); }
                public class Work<T> : IOut<T> { public virtual T Get() { return default; } }
                public sealed class Consumer<T> : IIn<T> { public void Accept(T value) { } }
                public class Base { public virtual void Run() { } }
                public static class Calls
                {
                    // 字符串结果允许转成对象结果。
                    public static object Covariant() { return ((IOut<object>)(object)new Work<string>()).Get(); }
                    // 泛型接口方差不允许将整数实参转换成对象实参。
                    public static object Invalid() { return ((IOut<object>)(object)new Work<int>()).Get(); }
                    // 能接收任意对象的函数可以接收字符串。
                    public static void Contravariant() { ((IIn<string>)(object)new Consumer<object>()).Accept("hello"); }
                    // 最外层转换可以补足内层方差条件尚未确定的实参。
                    public static object Outer(object value) { return ((Work<string>)(IOut<object>)value).Get(); }
                    // 补足实参后仍需检查内层方差转换能否成立。
                    public static object InvalidOuter(object value) { return ((Work<int>)(IOut<object>)value).Get(); }
                    // 返回空值时不会发生后续调用。
                    public static void Null() { None()?.Run(); }
                    // 明确提供空对象来源。
                    private static Base None() { return null; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", name);

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                if (name == "Null")
                {
                    Assert.IsEmpty(result.Calls.Where(item => item.CallerMethodId == root.Id && item.Call.Target.Name == invokedName));
                    Assert.IsTrue(result.Behaviors.MethodsById[root.Id].Calls.Any(call => call.Target.Name == invokedName));
                    continue;
                }
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id && item.Call.Target.Name == invokedName);
                Assert.HasCount(argument == null ? 0 : 1, call.Targets);
                if (argument != null)
                {
                    CollectionAssert.AreEqual(new[] { argument }, call.Targets[0].DeclaringTypeArguments.ToArray());
                }
            }
        }

        // 验证转换后的对象仍保留原始泛型实参，空对象不会贡献虚调用目标。
        /// <summary>
        /// 泛型创建与含空值的循环分别从源码和真实动态链接库保持同样的精确目标。
        /// </summary>
        [TestMethod]
        [DataRow("Generic")]
        [DataRow("Loop")]
        [DataRow("Parameter")]
        [DataRow("CastParameter")]
        public async Task ResolveAsyncRetainsGenericObjectsAndExcludesNullReceivers(string name)
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IRun { void Run(); }
                public sealed class Work<T> : IRun
                {
                    // 提供泛型对象的实例实现。
                    public void Run() { }
                }
                public interface IGenericRun<T> { void Run(); }
                public sealed class GenericWork<T> : IGenericRun<T>
                {
                    // 提供可由接口实参确定的实现。
                    public void Run() { }
                }
                public class Base { public virtual void Run() { } }
                public sealed class Child : Base { public override void Run() { } }
                public sealed class Sibling : Base { public override void Run() { } }
                public static class Calls
                {
                    // 连续转换不会抹掉真实对象的类型实参。
                    public static void Generic() { ((IRun)(object)new Work<int>()).Run(); }
                    // 已声明的对象类型实参也不能被转换抹掉。
                    public static void Parameter(Work<int> value) { ((IRun)(object)value).Run(); }
                    // 只有转换结果提供实参时，按结果反推出实现类型。
                    public static void CastParameter(object value) { ((IGenericRun<int>)value).Run(); }
                    // 空值分支不可能成功调用，循环赋值才贡献目标。
                    public static void Loop(bool condition)
                    {
                        Base value = null;
                        while (condition) { value = new Child(); }
                        value?.Run();
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", name);

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id && item.Call.Target.Name == "Run");
                Assert.HasCount(1, call.Targets);
                MethodEntry target = result.Methods.Single(method => method.Id == call.Targets[0].MethodId);
                StringAssert.EndsWith(target.TypeName, root.Name == "Loop" ? ".Child"
                    : root.Name == "CastParameter" ? ".GenericWork<T>" : ".Work<T>");
                if (root.Name != "Loop")
                {
                    CollectionAssert.AreEqual(new[] { "System.Int32" }, call.Targets[0].DeclaringTypeArguments.ToArray());
                }
            }
        }

        // 验证转换前后的接收类型限制同时生效，不把转换必然失败的对象纳入目标。
        /// <summary>
        /// 普通强制转换、经过返回值的转换和连续接口转换均只保留合法实现。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncPreservesReceiverCastRestrictions()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base
                {
                    // 提供基类实现。
                    public virtual void Run() { }
                }
                public class Selected : Base { }
                public sealed class Child : Selected
                {
                    // 提供允许的派生实现。
                    public override void Run() { }
                }
                public sealed class Sibling : Base
                {
                    // 该兄弟类型不能转换成 Selected。
                    public override void Run() { }
                }
                public interface IFirst { }
                public interface ISecond { void Run(); }
                public sealed class Both : IFirst, ISecond
                {
                    // 同时满足两次接口转换。
                    public void Run() { }
                }
                public sealed class SecondOnly : ISecond
                {
                    // 第一次转换已经会失败。
                    public void Run() { }
                }
                public static class Calls
                {
                    // 仅允许 Selected 及其派生类型。
                    public static void Direct(Base value) { ((Selected)value).Run(); }
                    // 返回转换结果后仍需保留类型限制。
                    public static void Returned(Base value) { Select(value).Run(); }
                    // 安全转换成功后同样只能调用该类型及其派生实现。
                    public static void Optional(Base value) { (value as Selected)?.Run(); }
                    // 转换不会改变一个已经确定的对象的实际类型。
                    public static void Exact() { ((Selected)new Child()).Run(); }
                    // 两次转换条件必须同时成立。
                    public static void Intersection(object value) { ((ISecond)(IFirst)value).Run(); }
                    // 把参数转换成更具体的类型。
                    private static Selected Select(Base value) { return (Selected)value; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Direct", "Returned", "Optional", "Exact", "Intersection");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id && item.Call.Target.Name == "Run");
                string prefix = root.TypeName.Split('.')[0];
                CollectionAssert.AreEquivalent(root.Name == "Intersection"
                        ? new[] { prefix + ".Both" } : root.Name == "Exact" ? new[] { prefix + ".Child" }
                        : new[] { prefix + ".Base", prefix + ".Child" },
                    call.Targets.Select(target => result.Methods.Single(method => method.Id == target.MethodId).TypeName).ToArray());
            }
        }

        // 验证递归不断构造新的类型实参时会明确指出不能形成有限调用集合。
        /// <summary>
        /// 不使用任意深度上限，也不允许解析循环无限增长。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsExpandingGenericRecursion()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Box<T> { }
                public static class Calls
                {
                    // 从一个确定类型开始展开递归。
                    public static void Root() { Grow<int>(); }
                    // 每次递归都再包一层类型。
                    private static void Grow<T>() { Grow<Box<T>>(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));

            AnalysisException error = await Assert.ThrowsAsync<AnalysisException>(() =>
                new CallTargetResolver().ResolveAsync(material, catalog,
                    ReadRoots(catalog, project, "Calls", "Root"), 2, deadline.Token));

            StringAssert.Contains(error.Message, "递归类型实参");
            StringAssert.Contains(error.Message, "Grow");
        }

        // 验证多层普通调用保留各次构造类型与函数类型实参。
        /// <summary>
        /// 同一函数定义处理整数和字符串时，后续调用不能混成开放类型或互相串用。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsGenericArgumentsAcrossOrdinaryCalls()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Box<T>
                {
                    // 把当前构造类型的值传给下一层。
                    public void Outer(T value) { Inner(value); }
                    // 保留当前构造类型的调用参数。
                    private void Inner(T value) { }
                }
                public static class Calls
                {
                    // 同时使用同一泛型定义的两种构造形式。
                    public static void Root()
                    {
                        new Box<int>().Outer(1);
                        new Box<string>().Outer("text");
                        Forward(2);
                        Forward("other");
                    }
                    // 经函数泛型再传一层。
                    private static void Forward<T>(T value) { Observe(value); }
                    // 保留实际函数泛型参数。
                    private static void Observe<T>(T value) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Root");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string[] methodIds = result.Methods.Where(method => method.AssemblyName == root.AssemblyName)
                    .Select(method => method.Id).ToArray();
                ResolvedCallTarget[] inner = result.Calls.Where(call => methodIds.Contains(call.CallerMethodId)
                    && call.Call.Target.Name == "Inner").SelectMany(call => call.Targets).ToArray();
                CollectionAssert.AreEquivalent(new[] { "System.Int32", "System.String" },
                    inner.Select(binding => binding.DeclaringTypeArguments.Single()).ToArray());
                ResolvedCallTarget[] observe = result.Calls.Where(call => methodIds.Contains(call.CallerMethodId)
                    && call.Call.Target.Name == "Observe").SelectMany(call => call.Targets).ToArray();
                CollectionAssert.AreEquivalent(new[] { "System.Int32", "System.String" },
                    observe.Select(binding => binding.Reference.GenericArgumentTypeIds.Single()).ToArray());
                ResolvedCallTarget[] outerCalls = result.Calls.Where(call => call.CallerMethodId == root.Id
                    && call.Call.Target.Name is "Outer" or "Forward").SelectMany(call => call.Targets).ToArray();
                foreach (ResolvedCallTarget outer in outerCalls)
                {
                    ResolvedCall nested = result.Calls.Single(call => call.CallerInstanceId == outer.InstanceId);
                    BehaviorValueReference nestedArgument = nested.Targets.Single().Arguments.Single().Single();
                    ValueOrigin actual = result.ValueSources.GetCallOrigins(new BehaviorValueReference(outer.MethodId, nestedArgument.ValueId, outer.InstanceId)).Single();
                    ValueOrigin expected = result.ValueSources.GetCallOrigins(outer.Arguments.Single().Single()).Single();
                    Assert.AreEqual(expected, actual);
                    Assert.AreEqual(root.Id, actual.Reference.MethodId);
                    Assert.AreEqual(BehaviorValueKind.Constant, actual.Value.Kind);
                }
            }
        }

        // 验证 ref 与 out 参数所指向的值和参数槽里保存的地址不会混淆。
        /// <summary>
        /// 经引用别名写入后，解引用读取应得到新值，改写前的快照不受影响。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncReadsRefAndOutPointeesAfterAliasWrites()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    // 先读取旧值，再经引用别名修改引用参数的内容。
                    public static void RefWrite(ref object target, object replacement)
                    {
                        object oldValue = target;
                        ref object alias = ref target;
                        alias = replacement;
                        Observe(oldValue, target);
                    }
                    // 给输出参数写入确定值后读取。
                    public static void OutWrite(out object target, object replacement)
                    {
                        target = replacement;
                        Observe(target, target);
                    }
                    // 保留每个实际读取值。
                    private static void Observe(object first, object second) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "RefWrite", "OutWrite");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                ResolvedCallTarget binding = call.Targets.Single();
                ValueOrigin updated = result.ValueSources.GetOrigins(binding.Arguments[1].Single()).Single();
                Assert.AreEqual(BehaviorValueKind.Parameter, updated.Value.Kind);
                Assert.AreEqual(1, updated.Value.ParameterIndex);
                ValueOrigin before = result.ValueSources.GetOrigins(binding.Arguments[0].Single()).Single();
                if (root.Name == "OutWrite")
                {
                    Assert.AreEqual(updated.Reference, before.Reference);
                }
                else
                {
                    Assert.AreEqual("ldind.ref", before.Value.Reference);
                    Assert.IsTrue(before.Value.Point!.Value.BlockId < call.Call.Point.BlockId);
                }
            }
        }

        // 验证同一个委托工厂的两次调用各自保存自己的接收对象。
        /// <summary>
        /// 返回函数的来源必须按具体调用点代入参数，不合并所有调用点。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncSeparatesDelegateFactoryInvocations()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public sealed class Target
                {
                    public int Value;
                    // 修改当前绑定的对象。
                    public void Set(int value) { Value = value; }
                }
                public static class Calls
                {
                    // 从同一工厂取得绑定不同对象的两个委托。
                    public static void Use(Target first, Target second)
                    {
                        Action<int> left = Make(first);
                        Action<int> right = Make(second);
                        left(1);
                        right(2);
                    }
                    // 返回绑定本次参数的委托。
                    private static Action<int> Make(Target target) { return new Action<int>(target.Set); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Use");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall[] calls = result.Calls.Where(call => call.CallerMethodId == root.Id
                    && call.Call.Kind == BehaviorCallKind.Delegate).ToArray();
                Assert.HasCount(2, calls);
                for (int index = 0; index < calls.Length; index++)
                {
                    ValueOrigin origin = result.ValueSources.GetCallOrigins(calls[index].Targets.Single().Receiver.Single()).Single();
                    Assert.AreEqual(root.Id, origin.Reference.MethodId);
                    Assert.AreEqual(BehaviorValueKind.Parameter, origin.Value.Kind);
                    Assert.AreEqual(index, origin.Value.ParameterIndex);
                }
            }
        }

        // 验证泛型接口按实际类型参数筛选，而不是把同一泛型定义的实现混在一起。
        /// <summary>
        /// 整数接口不能命中字符串实现，泛型类的目标保留对应类型实参。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsConstructedInterfaceTargetsSeparate()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork<T> { void Run(T value); }
                public sealed class IntWork : IWork<int>
                {
                    // 提供整数实现。
                    public void Run(int value) { }
                }
                public sealed class StringWork : IWork<string>
                {
                    // 提供不合法的字符串候选。
                    public void Run(string value) { }
                }
                public sealed class GenericWork<T> : IWork<T>
                {
                    // 实现实参对应的接口。
                    public void Run(T value) { }
                }
                public static class Calls
                {
                    // 允许全部整数接口的合法实现。
                    public static void Input(IWork<int> target) { target.Run(1); }
                    // 实际创建类型携带确定的整数实参。
                    public static void Fresh() { IWork<int> target = new GenericWork<int>(); target.Run(1); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Input", "Fresh");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id
                    && item.Call.Target.Name == "Run");
                string prefix = root.TypeName.Split('.')[0];
                string genericName = catalog.Types.Single(type => type.AssemblyName == root.AssemblyName
                    && type.FullName.StartsWith(prefix + ".GenericWork", StringComparison.Ordinal)).FullName;
                CollectionAssert.AreEquivalent(root.Name == "Input"
                        ? new[] { prefix + ".IntWork", genericName }
                        : new[] { genericName },
                    call.Targets.Select(binding => result.Methods.Single(method =>
                        method.Id == binding.MethodId).TypeName).ToArray(), root.Id);
                ResolvedCallTarget generic = call.Targets.Single(binding => result.Methods.Single(method =>
                    method.Id == binding.MethodId).TypeName == genericName);
                CollectionAssert.AreEqual(new[] { "System.Int32" }, generic.DeclaringTypeArguments.ToArray());
            }
        }

        // 验证委托保存的实例与调用实参分别绑定到真正被调函数。
        /// <summary>
        /// 实例、静态与虚函数委托都应通过绑定来源接上实际实现。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncBindsDelegatesToActualFunctionsAndReceivers()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public class Target
                {
                    public int Value;
                    // 修改绑定的已有对象。
                    public void Set(int value) { Value = value; }
                    // 提供虚调用声明。
                    public virtual void VirtualSet(int value) { Value = value; }
                }
                public sealed class Derived : Target
                {
                    // 重写委托绑定的目标。
                    public override void VirtualSet(int value) { Value = value + 1; }
                }
                public static class Calls
                {
                    public static int Value;
                    // 创建实例委托后调用。
                    public static void Instance(Target target, int value)
                    {
                        Action<int> action = new Action<int>(target.Set);
                        action(value);
                    }
                    // 创建静态委托后调用。
                    public static void Static(int value)
                    {
                        Action<int> action = new Action<int>(Write);
                        action(value);
                    }
                    // 在绑定时根据实际类型选出重写函数。
                    public static void Virtual(int value)
                    {
                        Target target = new Derived();
                        Action<int> action = new Action<int>(target.VirtualSet);
                        action(value);
                    }
                    // 提供静态委托目标。
                    private static void Write(int value) { Value = value; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Instance", "Static", "Virtual");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id
                    && item.Call.Kind == BehaviorCallKind.Delegate);
                ResolvedCallTarget binding = call.Targets.Single();
                MethodEntry target = result.Methods.Single(method => method.Id == binding.MethodId);
                Assert.AreEqual(root.Name switch { "Instance" => "Set", "Static" => "Write", _ => "VirtualSet" },
                    target.Name);
                if (root.Name == "Static")
                {
                    Assert.IsEmpty(binding.Receiver);
                }
                else
                {
                    ValueOrigin receiver = result.ValueSources.GetOrigins(binding.Receiver.Single()).Single();
                    Assert.AreEqual(root.Name == "Instance" ? BehaviorValueKind.Parameter : BehaviorValueKind.NewObject,
                        receiver.Value.Kind);
                    if (root.Name == "Virtual")
                    {
                        Assert.AreEqual(root.TypeName.Split('.')[0] + ".Derived", target.TypeName);
                    }
                }

                ValueOrigin argument = result.ValueSources.GetOrigins(binding.Arguments.Single().Single()).Single();
                Assert.AreEqual(BehaviorValueKind.Parameter, argument.Value.Kind);
                Assert.AreEqual(root.Name == "Instance" ? 1 : 0, argument.Value.ParameterIndex);
            }
        }

        // 验证读取快照、分支合流和循环不会混淆变量在使用时的值。
        /// <summary>
        /// 两种输入均应提供逐调用实参的真实来源，且循环不丢掉初值。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncPreservesSnapshotsBranchesAndLoopSources()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class Calls
                {
                    // 保留实参读取顺序。
                    public static void Snapshot(object first, object second)
                    {
                        object value = first;
                        Observe(value, value = second);
                        Observe(value, value);
                    }
                    // 合并两条能到达使用位置的路径。
                    public static void Branch(object first, object second, bool choose)
                    {
                        object value = first;
                        if (choose) value = second;
                        Observe(value, value);
                    }
                    // 循环内第一次使用初值，后续使用新值。
                    public static void Loop(object first, object second, int count)
                    {
                        object value = first;
                        while (count-- > 0) { Observe(value, value); value = second; }
                    }
                    // 提供没有其他行为的明确调用目标。
                    private static void Observe(object first, object second) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Snapshot", "Branch", "Loop");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall[] calls = result.Calls.Where(call => call.CallerMethodId == root.Id).ToArray();
                foreach (ResolvedCall call in calls)
                {
                    ResolvedCallTarget target = call.Targets.Single();
                    for (int index = 0; index < target.Arguments.Count; index++)
                    {
                        int[] expected = root.Name == "Snapshot"
                            ? new[] { call == calls[0] && index == 0 ? 0 : 1 }
                            : new[] { 0, 1 };
                        IReadOnlyList<ValueOrigin> origins = result.ValueSources.GetOrigins(
                            target.Arguments[index].Single());
                        Assert.IsTrue(origins.All(origin => origin.Value.Kind == BehaviorValueKind.Parameter));
                        CollectionAssert.AreEquivalent(expected,
                            origins.Select(origin => origin.Value.ParameterIndex!.Value).ToArray(), root.Id);
                    }
                }
            }
        }

        // 验证确定新建类型的接口调用不混入其他实现。
        /// <summary>
        /// 复刻 khengine 创建具体上下文后经基类或接口调用的写法。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncNarrowsNewReceiverToItsActualImplementation()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork { void Run(); }
                public class Base : IWork
                {
                    // 提供未命中的基类实现。
                    public virtual void Run() { }
                }
                public sealed class Derived : Base
                {
                    public int Value;
                    // 修改确定新建的对象。
                    public override void Run() { Value = 1; }
                }
                public static class Calls
                {
                    // 实际接收对象只能是 Derived。
                    public static void Fresh() { IWork target = new Derived(); target.Run(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Fresh");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id
                    && item.Call.Target.Name == "Run");
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                Assert.AreEqual(root.TypeName.Split('.')[0] + ".Derived", target.TypeName);
            }
        }

        private const string DirectSource = """
            namespace Samples;
            public sealed class Calls
            {
                public int Value;
                // 初始化新对象。
                public Calls(int value) { Value = value; }
                // 提供不能被整数调用选中的构造重载。
                public Calls(string value) { }
                // 修改当前对象。
                public void Set(int value) { Value = value; }
                // 提供同名不同参数的反例。
                public void Set(string value) { }
                // 调用已有对象的整数重载。
                public static void Instance(Calls target, int value) { target.Set(value); }
                // 创建并返回新对象。
                public static Calls Create(int value) { return new Calls(value); }
                // 进入两层调用。
                public static int Start(int value) { return Middle(value); }
                // 保留中间函数。
                private static int Middle(int value) { return End(value); }
                // 返回参数。
                private static int End(int value) { return value; }
                // 进入相互递归。
                public static int Left(int value) { return value == 0 ? 0 : Right(value - 1); }
                // 调用循环另一端。
                private static int Right(int value) { return value == 0 ? 0 : Left(value - 1); }
            }
            """;

        // 验证实例与构造调用的实际接收对象、重载、递归闭包和并行顺序。
        /// <summary>
        /// 对照两种输入形式的普通调用，确保不重复读取递归函数。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsExactOverloadsReceiversAndRecursiveClosure()
        {
            using TestProject project = TestProject.CreateWithCallTargets(DirectSource);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "Instance", "Create", "Start", "Left");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 1);
            CallTargetResolutionResult parallel = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 4);
            Assert.AreEqual(
                System.Text.Json.JsonSerializer.Serialize(result.Calls),
                System.Text.Json.JsonSerializer.Serialize(parallel.Calls));
            Assert.HasCount(result.Methods.Count, result.Methods.Select(method => method.Id).Distinct());
            Assert.HasCount(result.Methods.Count, result.Behaviors.Methods);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == root.Id);
                ResolvedCallTarget binding = call.Targets.Single();
                MethodEntry target = result.Methods.Single(method => method.Id == binding.MethodId);
                if (root.Name is "Instance" or "Create")
                {
                    Assert.AreEqual("System.Int32", target.Parameters.Single().TypeId);
                    BehaviorValueReference receiver = binding.Receiver.Single();
                    Assert.AreEqual(root.Id, receiver.MethodId);
                    Assert.AreEqual(
                        root.Name == "Create" ? call.Call.ResultValueId : call.Call.ReceiverValueId,
                        receiver.ValueId);
                }
                else
                {
                    Assert.IsEmpty(binding.Receiver);
                }
            }

            foreach (string prefix in new[] { "SourceSamples", "ExternalSamples" })
            {
                MethodEntry[] closure = result.Methods.Where(method => method.TypeName == prefix + ".Calls").ToArray();
                CollectionAssert.AreEquivalent(
                    new[] { "Instance", "Set", "Create", ".ctor", "Start", "Middle", "End", "Left", "Right" },
                    closure.Select(method => method.Name).ToArray());
            }
        }

        // 验证接口与虚函数只选择继承关系中合法的实现，直接基类调用不扩散。
        /// <summary>
        /// 复刻 khengine 通过接口和可重写函数转发调用的写法。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncFindsInterfaceAndVirtualImplementations()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork
                {
                    // 声明接口调用。
                    void Run(int value);
                }
                public class Base : IWork
                {
                    // 提供可重写的接口实现。
                    public virtual void Run(int value) { }
                }
                public sealed class Derived : Base
                {
                    public int Value;
                    // 重写并修改对象。
                    public override void Run(int value) { Value = value; }
                    // 直接调用基类实现。
                    public void BaseCall(int value) { base.Run(value); }
                }
                public sealed class Inherited : Base { }
                public sealed class Unrelated
                {
                    // 不属于接口或继承关系的同名函数。
                    public void Run(int value) { }
                }
                public static class Calls
                {
                    // 接口接收对象可以是全部合法实现类型。
                    public static void ViaInterface(IWork target, int value) { target.Run(value); }
                    // 基类接收对象可以命中基类或重写。
                    public static void ViaVirtual(Base target, int value) { target.Run(value); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "Calls", "ViaInterface", "ViaVirtual")
                .Concat(ReadRoots(catalog, project, "Derived", "BaseCall")).ToArray();

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == root.Id);
                string prefix = root.TypeName.Split('.')[0];
                string[] expected = root.Name == "BaseCall"
                    ? new[] { prefix + ".Base" }
                    : new[] { prefix + ".Base", prefix + ".Derived" };
                CollectionAssert.AreEquivalent(expected,
                    call.Targets.Select(binding => result.Methods.Single(method =>
                        method.Id == binding.MethodId).TypeName).ToArray(), root.Id);
                foreach (ResolvedCallTarget binding in call.Targets)
                {
                    Assert.AreEqual(call.Call.ReceiverValueId, binding.Receiver.Single().ValueId);
                    Assert.AreEqual(call.Call.Arguments.Single().ValueId, binding.Arguments.Single().Single().ValueId);
                }
            }
        }

        // 验证已索引的错版本类型不会抢占类型转交目标。
        /// <summary>
        /// 通过公开调用结果核对门面声明指定的实际版本。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncUsesForwardedTargetWithMatchingAssemblyIdentity()
        {
            using TestProject project = TestProject.CreateWithVersionedForwarderCollision();

            MethodEntry target = await ResolveManagedTarget(
                project,
                "TransitiveForwarderSamples.Consumer",
                "Call");

            Assert.AreEqual(project.ForwardTargetPath, target.AssemblyPath);
        }

        // 验证已索引的错版本基类不会抢占继承声明。
        /// <summary>
        /// 通过公开调用结果核对基类关系指定的实际版本。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncUsesInheritedTypeWithMatchingAssemblyIdentity()
        {
            using TestProject project = TestProject.CreateWithVersionedInheritanceCollision();

            MethodEntry target = await ResolveManagedTarget(
                project,
                "TransitiveSamples.DerivedType",
                "CallInherited");

            Assert.AreEqual(project.ForwardTargetPath, target.AssemblyPath);
        }

        // 验证普通类继承查找不会把同签名接口当成第二个声明。
        /// <summary>
        /// 普通类成员应只沿基类链找到最近声明。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncInheritedClassLookupIgnoresSameSignatureInterface()
        {
            using TestProject project = TestProject.CreateWithInheritedClassCall(
                removeInterface: false);

            MethodEntry target = await ResolveManagedTarget(
                project,
                "InheritedSamples.Caller",
                "Call");

            Assert.AreEqual("InheritedSamples.BaseType", target.TypeName);
        }

        // 验证普通类继承查找不会要求无关接口必须存在。
        /// <summary>
        /// 基类成员可解析时，缺失接口不应阻断普通调用。
        /// </summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task ResolveAsyncInheritedClassLookupIgnoresMissingInterface(bool virtualCall, bool explicitInterface)
        {
            using TestProject project = TestProject.CreateWithInheritedClassCall(
                removeInterface: true, virtualCall, explicitInterface);

            MethodEntry target = await ResolveManagedTarget(
                project,
                "InheritedSamples.Caller",
                "Call");

            Assert.AreEqual("InheritedSamples.BaseType", target.TypeName);
        }

        // 从一个真实 DLL 根函数读取公开解析结果的唯一目标。
        /// <summary>类的显式虚槽实现允许不同名字，过滤接口关系时必须保留它。</summary>
        [TestMethod]
        public async Task ResolveAsyncRetainsExplicitClassOverride()
        {
            using TestProject project = TestProject.CreateWithInheritedClassCall(
                removeInterface: true, virtualCall: true, explicitInterface: true, explicitClassOverride: true);
            MethodEntry target = await ResolveManagedTarget(project, "InheritedSamples.Caller", "Call");
            Assert.AreEqual("InheritedSamples.DerivedType", target.TypeName);
            Assert.AreEqual("Implementation", target.Name);
        }

        // 从一个真实 DLL 根函数读取公开解析结果的唯一目标。
        private static async Task<MethodEntry> ResolveManagedTarget(
            TestProject project,
            string typeName,
            string methodName)
        {
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry type = catalog.Types.Single(item =>
                item.AssemblyPath == project.ExternalAssemblyPath && item.FullName == typeName);
            MethodEntry root = catalog.GetMethods(type).Single(method => method.Name == methodName);
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                new[] { root },
                2);
            ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
            string targetId = call.Targets.Single().MethodId;

            return result.Methods.Single(method => method.Id == targetId);
        }

        // 从两种材料选出同一类的指定根函数，其他函数必须由解析过程自行补齐。
        private static MethodEntry[] ReadRoots(
            MethodCatalogResult catalog,
            TestProject project,
            string typeName,
            params string[] names)
        {
            TypeEntry managed = catalog.Types.Single(type => type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples." + typeName);
            return catalog.Methods.Where(method => method.TypeName == "SourceSamples." + typeName)
                .Concat(catalog.GetMethods(managed))
                .Where(method => names.Contains(method.Name)).ToArray();
        }

        // 验证泛型普通调用补读真实函数，并保留实参而不是只记一个名称。
        /// <summary>
        /// 对照源码和托管文件的 Identity 泛型调用及逐调用点参数绑定。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncClosesDirectGenericCallsFromSourceAndManagedBodies()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry source = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.BehaviorSample" && method.Name == "UseGeneric");
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry managed = catalog.GetMethods(managedType).Single(method =>
                method.Name == "UseGeneric");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, new[] { source, managed }, 2);

            Assert.HasCount(4, result.Methods);
            Assert.HasCount(4, result.Behaviors.Methods);
            Assert.HasCount(2, result.Calls);
            foreach (MethodEntry caller in new[] { source, managed })
            {
                ResolvedCall call = result.Calls.Single(call => call.CallerMethodId == caller.Id);
                Assert.HasCount(1, call.Targets);
                ResolvedCallTarget target = call.Targets.Single();
                MethodEntry definition = result.Methods.Single(method => method.Id == target.MethodId);
                Assert.AreEqual("Identity", definition.Name);
                Assert.AreEqual(caller.TypeId, definition.TypeId);
                Assert.IsEmpty(target.Receiver);
                Assert.HasCount(1, target.Reference.GenericArgumentTypeIds);
                Assert.HasCount(1, target.Arguments);
                BehaviorValueReference argument = target.Arguments.Single().Single();
                Assert.AreEqual(caller.Id, argument.MethodId);
                Assert.AreEqual(call.Call.Arguments.Single().ValueId, argument.ValueId);
                Assert.IsTrue(result.Behaviors.MethodsById.ContainsKey(target.MethodId));
            }
        }

        // 验证类型泛型实参不会替换函数自身的泛型参数。
        /// <summary>
        /// 通过公开调用结果分别核对类型参数和函数参数。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsTypeAndMethodGenericArgumentsSeparate()
        {
            using TestProject project = TestProject.CreateWithBehaviorMethods();
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            TypeEntry managedType = catalog.Types.Single(type =>
                type.AssemblyPath == project.ExternalAssemblyPath
                && type.FullName == "ExternalSamples.BehaviorSample");
            MethodEntry[] roots = catalog.Methods.Where(method =>
                    method.TypeName == "SourceSamples.BehaviorSample"
                    && method.Name is "UseBox" or "UseGeneric")
                .Concat(catalog.GetMethods(managedType).Where(method =>
                    method.Name is "UseBox" or "UseGeneric"))
                .ToArray();
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (MethodEntry caller in roots)
            {
                ResolvedCallTarget binding = result.Calls.Single(call =>
                    call.CallerMethodId == caller.Id).Targets.Single();
                MethodEntry target = result.Methods.Single(method => method.Id == binding.MethodId);
                if (caller.Name == "UseBox")
                {
                    Assert.AreEqual("!0", target.Parameters.Single().TypeId);
                    CollectionAssert.AreEqual(
                        new[] { "System.Int32" },
                        binding.DeclaringTypeArguments.ToArray());
                    Assert.IsEmpty(binding.Reference.GenericArgumentTypeIds);
                }
                else
                {
                    Assert.AreEqual("!!0", target.Parameters.Single().TypeId);
                    Assert.IsEmpty(binding.DeclaringTypeArguments);
                    CollectionAssert.AreEqual(
                        new[] { "System.Int32" },
                        binding.Reference.GenericArgumentTypeIds.ToArray());
                }
            }
        }

        // 验证源码命名类型中的编译生成函数可沿真实调用解析。
        /// <summary>
        /// 局部函数不进入标签报告，但必须进入调用闭包。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncFindsGeneratedSourceMethodWithoutReportingIt()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class GeneratedCalls
                {
                    // 调用编译后才具有元数据名称的局部函数。
                    public static int Start(int value)
                    {
                        int Local(int input) => input;
                        return Local(value);
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry root = catalog.Methods.Single(method =>
                method.TypeName == "SourceSamples.GeneratedCalls" && method.Name == "Start");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                new[] { root },
                2);
            ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
            MethodEntry generated = result.Methods.Single(method =>
                method.Id == call.Targets.Single().MethodId);

            StringAssert.Contains(generated.Name, "Local");
            Assert.IsFalse(generated.IsReportable);
            Assert.AreEqual(material.SourceAssemblies.Single().AssemblyPath, generated.AssemblyPath);
            Assert.IsGreaterThan(0, generated.MetadataToken);
        }

        // 验证每个 leave 先执行 finally，然后回到它自己的后继。
        /// <summary>
        /// 源码和真实 DLL 都不应让两个 leave 出口的变量来源串线。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRunsFinallyBeforeEachDistinctLeaveTarget()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class FinallyCalls
                {
                    // 从 try 的两条路径离开并执行同一 finally。
                    public static void Run(object first, object second, bool choose)
                    {
                        object value = first;
                        try
                        {
                            if (choose)
                            {
                                value = second;
                                goto Changed;
                            }
                        }
                        finally
                        {
                            Observe(value);
                        }

                        Observe(value);
                        return;
                    Changed:
                        Observe(value);
                    }

                    // 保留变量的使用位置。
                    private static void Observe(object value) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "FinallyCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (MethodEntry root in roots)
            {
                string[] originSets = result.Calls.Where(call =>
                        call.CallerMethodId == root.Id && call.Call.Target.Name == "Observe")
                    .Select(call => string.Join(',', result.ValueSources.GetOrigins(
                            call.Targets.Single().Arguments.Single().Single())
                        .Select(origin => origin.Value.ParameterIndex!.Value)
                        .Order()))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                CollectionAssert.AreEqual(new[] { "0", "0,1", "1" }, originSets, root.Id);
            }
        }

        // 验证通过 ref 局部变量写入后能找到真正的新来源。
        /// <summary>
        /// 局部槽和参数槽的别名都不能保留已被覆盖的旧值。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncTracksIndirectWritesThroughRefLocals()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class RefCalls
                {
                    // 通过局部引用覆盖局部值。
                    public static void Local(object first, object second)
                    {
                        object value = first;
                        ref object alias = ref value;
                        alias = second;
                        Observe(value);
                    }

                    // 通过局部引用覆盖参数值。
                    public static void Parameter(object first, object second)
                    {
                        ref object alias = ref first;
                        alias = second;
                        Observe(first);
                    }

                    // 保留覆盖后的使用位置。
                    private static void Observe(object value) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "RefCalls", "Local", "Parameter");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                ValueOrigin origin = result.ValueSources.GetOrigins(
                    call.Targets.Single().Arguments.Single().Single()).Single();
                Assert.AreEqual(1, origin.Value.ParameterIndex, root.Id);
            }
        }

        // 验证条件选择的引用不会把两个槽都当成必然覆盖。
        /// <summary>
        /// 间接写入的新值和未被选中的旧值都必须保留。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsOldValuesForConditionalRefTargets()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class ConditionalRefCalls
                {
                    // 条件改变引用指向后只覆盖实际选中的槽。
                    public static void Run(object first, object second, object replacement, bool choose)
                    {
                        ref object alias = ref first;
                        if (choose)
                        {
                            alias = ref second;
                        }

                        alias = replacement;
                        Observe(first, second);
                    }

                    // 同时使用两个可能被覆盖的参数。
                    private static void Observe(object first, object second) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "ConditionalRefCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCallTarget call = result.Calls.Single(item =>
                    item.CallerMethodId == root.Id).Targets.Single();
                int[][] actual = call.Arguments.Select(argument => result.ValueSources
                        .GetOrigins(argument.Single())
                        .Select(origin => origin.Value.ParameterIndex!.Value)
                        .Order()
                        .ToArray())
                    .ToArray();
                CollectionAssert.AreEqual(new[] { 0, 2 }, actual[0], root.Id);
                CollectionAssert.AreEqual(new[] { 1, 2 }, actual[1], root.Id);
            }
        }

        // 验证正常执行的 finally 内部还可以再进入一层 finally。
        /// <summary>
        /// 每层 leave 都必须按自己的真实边继续，不能猜区域最后一块。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncFollowsNestedFinallyEdges()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public static class NestedFinallyCalls
                {
                    // 在外层 finally 内正常离开内层 try。
                    public static void Run(object first, object second)
                    {
                        object value = first;
                        try
                        {
                        }
                        finally
                        {
                            try
                            {
                                value = second;
                            }
                            finally
                            {
                                Observe(value);
                            }

                            Observe(value);
                        }

                        Observe(value);
                    }

                    // 保留每层 finally 前后的值使用。
                    private static void Observe(object value) { }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "NestedFinallyCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material,
                catalog,
                roots,
                2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall[] calls = result.Calls.Where(call =>
                    call.CallerMethodId == root.Id && call.Call.Target.Name == "Observe").ToArray();
                Assert.HasCount(3, calls);
                foreach (ResolvedCall call in calls)
                {
                    ValueOrigin origin = result.ValueSources.GetOrigins(
                        call.Targets.Single().Arguments.Single().Single()).Single();
                    Assert.AreEqual(1, origin.Value.ParameterIndex, root.Id);
                }
            }
        }

        // 验证类未重写接口函数时会执行合法的默认接口实现。
        /// <summary>
        /// 默认实现以及派生接口提供的更具体实现都必须成为真实目标。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncFindsMostSpecificDefaultInterfaceImplementation()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IDefault
                {
                    // 提供直接默认实现。
                    void Run() { }
                }
                public sealed class DefaultOnly : IDefault { }
                public interface IBase
                {
                    // 提供可由派生接口取代的默认实现。
                    void Run() { }
                }
                public interface IDerived : IBase
                {
                    // 为基接口槽提供更具体的实现。
                    void IBase.Run() { }
                }
                public sealed class Specific : IBase, IDerived { }
                public static class DefaultCalls
                {
                    // 调用直接默认实现。
                    public static void Direct(IDefault target) { target.Run(); }
                    // 调用派生接口提供的最具体实现。
                    public static void SpecificCall(IBase target) { target.Run(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "DefaultCalls", "Direct", "SpecificCall");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                string prefix = root.TypeName.Split('.')[0];
                Assert.AreEqual(
                    prefix + (root.Name == "Direct" ? ".IDefault" : ".IDerived"),
                    target.TypeName,
                    root.Id);
            }
        }

        // 验证同一类的两个构造接口只命中签名对应的显式实现。
        /// <summary>
        /// 开放接口函数身份相同也不能混淆整数与字符串实现。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncSeparatesExplicitImplementationsOfConstructedInterfaces()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork<T> { void Run(T value); }
                public sealed class Both : IWork<int>, IWork<string>
                {
                    // 实现整数构造接口。
                    void IWork<int>.Run(int value) { }
                    // 实现字符串构造接口。
                    void IWork<string>.Run(string value) { }
                }
                public static class ExplicitCalls
                {
                    // 调用整数构造接口。
                    public static void Integer(IWork<int> target) { target.Run(1); }
                    // 调用字符串构造接口。
                    public static void Text(IWork<string> target) { target.Run(string.Empty); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "ExplicitCalls", "Integer", "Text");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                Assert.AreEqual(
                    root.Name == "Integer" ? "System.Int32" : "System.String",
                    target.Parameters.Single().TypeId,
                    root.Id);
            }
        }

        // 验证嵌套构造接口能反推出实现类的真实类型实参。
        /// <summary>
        /// IWork&lt;Box&lt;int&gt;&gt; 必须命中 NestedWork&lt;int&gt;。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncInfersReceiverArgumentsInsideNestedTypes()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public sealed class Box<T> { }
                public interface IWork<T> { void Run(T value); }
                public sealed class NestedWork<T> : IWork<Box<T>>
                {
                    // 实现嵌套构造接口。
                    public void Run(Box<T> value) { }
                }
                public static class NestedCalls
                {
                    // 从嵌套接口参数调用实现。
                    public static void Run(IWork<Box<int>> target, Box<int> value) { target.Run(value); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "NestedCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                ResolvedCallTarget target = call.Targets.Single();
                CollectionAssert.AreEqual(new[] { "System.Int32" }, target.DeclaringTypeArguments.ToArray());
            }
        }

        // 验证 constrained 虚调用只使用指令指定的具体值类型。
        /// <summary>
        /// 两个结构体都重写同一函数时不能把另一结构体混入目标。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncNarrowsConcreteConstrainedReceiver()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public struct First
                {
                    // 提供实际受限目标。
                    public override string ToString() { return "first"; }
                }
                public struct Second
                {
                    // 提供不能混入的另一重写。
                    public override string ToString() { return "second"; }
                }
                public static class ConstrainedCalls
                {
                    // 编译为 constrained First 后调用虚函数。
                    public static string Run(First value) { return value.ToString(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "ConstrainedCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                Assert.AreEqual(root.TypeName.Split('.')[0] + ".First", target.TypeName, root.Id);
            }
        }

        // 验证接口方差保留 CLR 允许的具体实现。
        /// <summary>
        /// 协变输出与逆变输入都不能被错误的实参相等判断删掉。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncIncludesCovariantAndContravariantImplementations()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IProducer<out T> { T Get(); }
                public sealed class TextProducer : IProducer<string>
                {
                    // 返回更具体的引用类型。
                    public string Get() { return string.Empty; }
                }
                public interface IConsumer<in T> { void Put(T value); }
                public sealed class ObjectConsumer : IConsumer<object>
                {
                    // 接受更宽的引用类型。
                    public void Put(object value) { }
                }
                public static class VarianceCalls
                {
                    // 通过协变接口调用。
                    public static object Produce(IProducer<object> target) { return target.Get(); }
                    // 通过逆变接口调用。
                    public static void Consume(IConsumer<string> target) { target.Put(string.Empty); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "VarianceCalls", "Produce", "Consume");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                Assert.AreEqual(
                    root.TypeName.Split('.')[0]
                    + (root.Name == "Produce" ? ".TextProducer" : ".ObjectConsumer"),
                    target.TypeName,
                    root.Id);
            }
        }

        // 验证反推实现类实参时会检查类型参数约束。
        /// <summary>
        /// class 约束不允许把 ClassWork&lt;int&gt; 当作可实例化目标。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsReceiverArgumentsThatViolateConstraints()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork<T> { void Run(T value); }
                public sealed class AnyWork<T> : IWork<T>
                {
                    // 提供不受约束的合法实现。
                    public void Run(T value) { }
                }
                public sealed class ClassWork<T> : IWork<T> where T : class
                {
                    // 只能由引用类型构造。
                    public void Run(T value) { }
                }
                public static class ConstraintCalls
                {
                    // 整数实参只能保留无 class 约束的实现。
                    public static void Run(IWork<int> target) { target.Run(1); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "ConstraintCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                StringAssert.Contains(target.TypeName, ".AnyWork<", root.Id);
            }
        }

        // 验证原始值类型通过真实接口关系满足泛型类型约束。
        /// <summary>
        /// int 的 IComparable&lt;int&gt; 实现必须来自运行时类型目录，不能被静默剔除。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncAcceptsPrimitiveAndEnumInterfaceConstraints()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public interface IWork<T> { void Run(T value); }
                public sealed class ComparableWork<T> : IWork<T> where T : IComparable<T>
                {
                    // 接受实现自比较接口的类型。
                    public void Run(T value) { }
                }
                public sealed class EnumWork<T> : IWork<T> where T : struct, Enum
                {
                    // 接受真实枚举类型。
                    public void Run(T value) { }
                }
                public enum Choice { First }
                public static class PrimitiveConstraintCalls
                {
                    // int 满足 IComparable<int> 约束。
                    public static void Run(IWork<int> target) { target.Run(1); }
                    // Choice 满足 Enum 与 struct 约束。
                    public static void RunEnum(IWork<Choice> target) { target.Run(Choice.First); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(
                catalog,
                project,
                "PrimitiveConstraintCalls",
                "Run",
                "RunEnum");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                StringAssert.Contains(
                    target.TypeName,
                    root.Name == "Run" ? ".ComparableWork<" : ".EnumWork<",
                    root.Id);
            }
        }

        // 验证尚未代入的 constrained 类型不会退化成全部实现。
        /// <summary>
        /// 泛型方法的 struct 约束尚未结合调用实参时必须明确停止。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncUsesDeclaredConstrainedReceiver()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork { void Run(); }
                public struct ValueWork : IWork
                {
                    private int value;
                    // 提供值类型实现。
                    public void Run() { value++; }
                }
                public sealed class ClassWork : IWork
                {
                    // 不满足调用方法的 struct 约束。
                    public void Run() { }
                }
                public static class OpenConstrainedCalls
                {
                    // 受限类型仍是尚未代入的方法泛型参数。
                    public static void Run<T>(T target) where T : struct, IWork { target.Run(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "OpenConstrainedCalls", "Run");

            foreach (MethodEntry root in roots)
            {
                CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 2);
                ResolvedCall call = calls.Calls.Single(item => item.CallerMethodId == root.Id);
                Assert.HasCount(1, call.Targets);
                Assert.AreEqual("ValueWork", catalog.TypesById[calls.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId).TypeId].Name);
                Assert.AreEqual(MethodEffectKind.Getter, new EffectAnalyzer().Analyze(catalog, new[] { root }, calls).Methods.Single().Kind);
            }
        }

        // 验证 Nullable 值类型不能满足 CLR 的 struct 泛型约束。
        /// <summary>
        /// IWork&lt;int?&gt; 只能保留无约束实现，不能构造 StructWork&lt;int?&gt;。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncRejectsNullableReceiverForStructConstraint()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IWork<T> { void Run(T value); }
                public sealed class AnyWork<T> : IWork<T>
                {
                    // 提供 Nullable 的合法实现。
                    public void Run(T value) { }
                }
                public sealed class StructWork<T> : IWork<T> where T : struct
                {
                    // Nullable 不能作为本类型的实参。
                    public void Run(T value) { }
                }
                public static class NullableConstraintCalls
                {
                    // 通过 Nullable 构造接口调用。
                    public static void Run(IWork<int?> target) { target.Run(null); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "NullableConstraintCalls", "Run");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                StringAssert.Contains(target.TypeName, ".AnyWork<", root.Id);
            }
        }

        // 验证精确构造接口槽优先于其它方差兼容槽。
        /// <summary>
        /// 同一类显式实现两个兼容接口时，调用只能命中静态接口的精确槽。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncPrefersExactConstructedSlotBeforeVariance()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public interface IProducer<out T> { T Get(); }
                public sealed class BothProducer : IProducer<string>, IProducer<object>
                {
                    // 实现更具体的协变槽。
                    string IProducer<string>.Get() { return string.Empty; }
                    // 实现调用指定的精确槽。
                    object IProducer<object>.Get() { return new object(); }
                }
                public interface IConsumer<in T> { void Put(T value); }
                public sealed class BothConsumer : IConsumer<string>, IConsumer<object>
                {
                    // 实现调用指定的精确槽。
                    void IConsumer<string>.Put(string value) { }
                    // 实现更宽的逆变兼容槽。
                    void IConsumer<object>.Put(object value) { }
                }
                public static class ExactVarianceCalls
                {
                    // 精确调用 object 生产槽。
                    public static object Produce(IProducer<object> target) { return target.Get(); }
                    // 精确调用 string 消费槽。
                    public static void Consume(IConsumer<string> target) { target.Put(string.Empty); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "ExactVarianceCalls", "Produce", "Consume");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id);
                MethodEntry target = result.Methods.Single(method =>
                    method.Id == call.Targets.Single().MethodId);
                Assert.AreEqual(
                    root.Name == "Produce" ? "System.Object" : "System.String",
                    root.Name == "Produce" ? target.ReturnTypeId : target.Parameters.Single().TypeId,
                    root.Id);
            }
        }

        // 验证 this 与参数的静态类型会排除基类的兄弟派生实现。
        /// <summary>
        /// A 内部的直接调用、委托调用和 A 参数都只能考虑 A 及其派生类。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncNarrowsCurrentInstanceAndParameterStaticTypes()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                using System;
                namespace Samples;
                public class Base
                {
                    // 提供继承实现。
                    public virtual void Run() { }
                }
                public class A : Base
                {
                    // 通过当前实例直接调用。
                    public void Direct() { this.Run(); }
                    // 绑定当前实例后调用委托。
                    public void Delegate() { Action action = this.Run; action(); }
                }
                public sealed class ADerived : A
                {
                    // A 的派生类可以成为实际接收类型。
                    public override void Run() { }
                }
                public sealed class Sibling : Base
                {
                    // Base 的兄弟派生实现不属于 A 接收对象。
                    public override void Run() { }
                }
                public static class StaticReceiverCalls
                {
                    // 参数的静态类型同样限制运行时类型范围。
                    public static void Parameter(A target) { target.Run(); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "A", "Direct", "Delegate")
                .Concat(ReadRoots(catalog, project, "StaticReceiverCalls", "Parameter"))
                .ToArray();

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                ResolvedCall call = result.Calls.Single(item => item.CallerMethodId == root.Id
                    && (item.Call.Target.Name == "Run" || item.Call.Kind == BehaviorCallKind.Delegate));
                string prefix = root.TypeName.Split('.')[0];
                CollectionAssert.AreEquivalent(
                    new[] { prefix + ".Base", prefix + ".ADerived" },
                    call.Targets.Select(target => result.Methods.Single(method =>
                        method.Id == target.MethodId).TypeName).ToArray(),
                    root.Id);
            }
        }

        // 验证跨函数进入泛型类后保留 this 的构造类型实参。
        /// <summary>
        /// A&lt;int&gt; 内部调用只能产生 Base&lt;int&gt; 与 A 的派生实现，不能混入兄弟类。
        /// </summary>
        [TestMethod]
        public async Task ResolveAsyncKeepsGenericCurrentInstanceArguments()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace Samples;
                public class Base<T>
                {
                    // 提供泛型继承实现。
                    public virtual void Run(T value) { }
                }
                public class A<T> : Base<T>
                {
                    // 从构造后的当前实例调用基类槽。
                    public void Call(T value) { this.Run(value); }
                }
                public sealed class ADerived<T> : A<T>
                {
                    // 提供 A 的派生实现。
                    public override void Run(T value) { }
                }
                public sealed class Sibling<T> : Base<T>
                {
                    // 不能进入 A 接收对象的候选。
                    public override void Run(T value) { }
                }
                public static class GenericStaticReceiverCalls
                {
                    // 用确定的 int 类型环境进入 A.Call。
                    public static void Start(A<int> target, int value) { target.Call(value); }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(
                new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = ReadRoots(catalog, project, "GenericStaticReceiverCalls", "Start");

            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(
                material, catalog, roots, 2);

            foreach (MethodEntry root in roots)
            {
                string prefix = root.TypeName.Split('.')[0];
                ResolvedCall call = result.Calls.Single(item => item.Call.Target.Name == "Run"
                    && result.Methods.Single(method => method.Id == item.CallerMethodId).TypeName
                        .StartsWith(prefix + ".A<", StringComparison.Ordinal));
                CollectionAssert.AreEquivalent(
                    new[] { prefix + ".Base<T>", prefix + ".ADerived<T>" },
                    call.Targets.Select(target => result.Methods.Single(method =>
                        method.Id == target.MethodId).TypeName).ToArray(),
                    root.Id);
                foreach (ResolvedCallTarget target in call.Targets)
                {
                    CollectionAssert.AreEqual(new[] { "System.Int32" }, target.DeclaringTypeArguments.ToArray());
                }
            }
        }
    }
}
