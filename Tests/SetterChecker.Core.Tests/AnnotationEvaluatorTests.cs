namespace SetterChecker.Core.Tests
{
    /// <summary>检查真实行为与人工日志规则保持分离。</summary>
    [TestClass]
    public sealed class AnnotationEvaluatorTests
    {
        // 豁免只改变追踪影响，调用者仍读取真实写回、返回对象以及其他调用。
        /// <summary>源码和托管 DLL 的标签都沿真实目标生效，不能遮住外层修改。</summary>
        [TestMethod]
        [DataRow(false, "bare")]
        [DataRow(true, "bare")]
        [DataRow(false, "reason")]
        [DataRow(true, "reason")]
        [DataRow(false, "class")]
        [DataRow(true, "class")]
        public async Task RunPreservesValuesAcrossExemptCalls(bool external, string annotation)
        {
            string library = """
                namespace KH { public class NoLogTrackAttribute : System.Attribute { public NoLogTrackAttribute(string reason = "") { } } }
                namespace Samples
                {
                    public class Box { public int Flag; }
                    CLASS public static class Cache
                    {
                        METHOD public static void Reset(Box value) { value.Flag = 1; }
                        METHOD public static Box Fresh() => new Box();
                        METHOD public static Box Same(Box value) => value;
                        METHOD public static void Wrapped(Box value) => Changes.Change(value);
                    }
                    public static class Changes { public static void Change(Box value) { value.Flag = 2; } }
                }
                """.Replace("CLASS", annotation == "class" ? "[KH.NoLogTrack]" : string.Empty)
                .Replace("METHOD", annotation == "bare" ? "[KH.NoLogTrack]" : annotation == "reason" ? "[KH.NoLogTrack(\"缓存\")]" : string.Empty);
            using TestProject project = external ? TestProject.CreateWithCallTargets(library) : TestProject.CreateSingleAssembly();
            project.WriteRootSource((external ? string.Empty : library) + """
                public static class Calls
                {
                    private static int state;
                    private static Samples.Box saved;
                    public static void Exempt(Samples.Box value) => Samples.Cache.Wrapped(value);
                    public static void Conditional(Samples.Box value) { Samples.Cache.Reset(value); if (value.Flag == 1) state = 1; }
                    public static void Old(Samples.Box value) { Samples.Cache.Same(value).Flag = 3; }
                    public static void Local() { Samples.Cache.Fresh().Flag = 3; }
                    public static void Save() { saved = Samples.Cache.Fresh(); }
                    public static void Independent(Samples.Box value) { Samples.Cache.Wrapped(value); Samples.Changes.Change(value); }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 4));
            Assert.IsTrue(run.Complete);
            foreach (AnnotationMethod method in run.Annotations.Methods.Where(method => method.Class == "Calls"))
            {
                Assert.AreEqual(method.Name == "Local" ? MethodEffectKind.Getter : MethodEffectKind.Setter, method.Actual, method.Name);
                Assert.AreEqual(method.Name is "Local" or "Exempt" ? "NoLogTrack" : "ShouldTrack", method.Decision, method.Name);
            }
            Assert.IsTrue(run.Annotations.Methods.All(method => method.File == project.RootSourcePath));
        }

        // 工厂的内部写入可以豁免，调用者自己返回业务对象仍属于需要追踪的动作。
        /// <summary>不把工厂标签当成返回对象不存在，也不把临时创建无条件判为 Setter。</summary>
        [TestMethod]
        public async Task RunTracksBusinessObjectsReturnedFromExemptFactory()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                public class Data { public int Value; }
                public static class Calls
                {
                    [KH.NoLogTrack] public static Data Factory() => new Data();
                    public static Data Returned() => Factory();
                    public static void Discarded() { Factory(); }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsTrue(run.Complete);
            AnnotationMethod returned = run.Annotations.Methods.Single(method => method.Name == "Returned");
            Assert.AreEqual(MethodEffectKind.Setter, returned.Actual);
            Assert.AreEqual("ShouldTrack", returned.Decision);
            Assert.AreEqual("NoLogTrack", run.Annotations.Methods.Single(method => method.Name == "Discarded").Decision);
        }

        // 第二次只检查部分函数时，业务对象的范围仍来自本轮全部分析入口。
        /// <summary>另一个 khengine 程序集的可信工厂不能让返回对象漏掉追踪。</summary>
        [TestMethod]
        public async Task RunKeepsBusinessScopeWhenTrackingOnlySubset()
        {
            using TestProject project = TestProject.CreateWithTargetPackageDependency();
            string dependency = Path.Combine(project.RootPath, "Packages", "khengine", "Define", "Dependency.cs");
            File.WriteAllText(dependency, """
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                public class Data { }
                public static class Factory { [KH.NoLogTrack] public static Data Create() => new Data(); }
                """, new System.Text.UTF8Encoding(false));
            project.WriteRootSource("public static class Calls { public static Data Entry() => Factory.Create(); }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsTrue(run.Complete);
            AnnotationMethod factory = run.Annotations.Methods.Single(method => method.Name == "Create");
            AnnotationMethod entry = run.Annotations.Methods.Single(method => method.Name == "Entry");
            Assert.AreNotEqual(entry.File, factory.File);
            Assert.IsTrue(factory.IsReportable);
            Assert.AreEqual(MethodEffectKind.Setter, entry.Actual);
            Assert.AreEqual("ShouldTrack", entry.Decision);
            Assert.AreEqual("NoLogTrack", factory.Decision);
        }

        // 真正的写入证据不等于标签生效后已有确定结论，未知原生路径也不是标签冲突。
        /// <summary>独立保留真实写入和追踪失败证据，报告计数不混淆二者。</summary>
        [TestMethod]
        public async Task RunReportsUnprovedTrackingSeparatelyFromTagConflict()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH
                {
                    public class NoLogTrackAttribute : System.Attribute { }
                    public class LogTrackAttribute : System.Attribute { }
                }
                public static class Calls
                {
                    private static int state;
                    public static void Entry() => Cache();
                    [KH.LogTrack] public static void Forced() => Cache();
                    [KH.NoLogTrack] public static void Cache() { state = 1; Unknown(); }
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                    private static extern void Unknown();
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsFalse(run.Complete);
            AnnotationMethod entry = run.Annotations.Methods.Single(method => method.Name == "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, entry.Actual);
            Assert.IsNull(entry.Decision);
            Assert.IsNotNull(entry.Evidence);
            Assert.IsNotNull(entry.TrackingEvidence);
            Assert.AreNotEqual(entry.Evidence.Detail, entry.TrackingEvidence.Detail);
            Assert.AreEqual("NoLogTrack", run.Annotations.Methods.Single(method => method.Name == "Cache").Decision);
            Assert.AreEqual("ShouldTrack", run.Annotations.Methods.Single(method => method.Name == "Forced").Decision);
            string directory = Path.Combine(project.RootPath, "reports");
            new ReportWriter().Write(run, directory);
            Assert.Contains("尚未证明：1；标签冲突：0", File.ReadAllText(Path.Combine(directory, "report.md")));
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
            Assert.AreEqual(1, json.RootElement.GetProperty("Unproved").GetInt32());
            Assert.DoesNotContain("[global::KH.NoLogTrack] public static void Entry", File.ReadAllText(Path.Combine(directory, "preview.patch")));
        }

        // 可信豁免只隔断对应调用的追踪影响，不能遮住调用者自己的修改或另一条调用。
        /// <summary>三种标签保留真实 Setter、独立告警及标签生效后的上游结论。</summary>
        [TestMethod]
        [DataRow("bare", 1)]
        [DataRow("reason", 1)]
        [DataRow("class", 1)]
        [DataRow("bare", 4)]
        [DataRow("reason", 4)]
        [DataRow("class", 4)]
        public async Task RunStopsOnlyExemptTrackingEffects(string annotation, int jobs)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH
                {
                    public class NoLogTrackAttribute : System.Attribute { public NoLogTrackAttribute(string reason = "") { } }
                    public class LogTrackAttribute : System.Attribute { }
                }
                CLASS public static class Cache
                {
                    private static int value;
                    METHOD public static void Write() { value = 1; }
                }
                public static class Calls
                {
                    private static int state;
                    public static void OnlyExempt() => Cache.Write();
                    public static void OwnWrite() { Cache.Write(); state = 1; }
                    public static void OtherCall() { Cache.Write(); Change(); }
                    public static void Wrapped() => OnlyExempt();
                    [KH.LogTrack] public static void Forced() => Cache.Write();
                    private static void Change() { state = 2; }
                }
                """.Replace("CLASS", annotation == "class" ? "[KH.NoLogTrack]" : string.Empty)
                .Replace("METHOD", annotation == "bare" ? "[KH.NoLogTrack]" : annotation == "reason" ? "[KH.NoLogTrack(\"缓存\")]" : string.Empty));
            bool deferred = false;
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs), reportProgress: current =>
            {
                AnnotationMethod caller = current.Annotations.Methods.Single(method => method.Name == "OnlyExempt");
                if (caller.Actual == MethodEffectKind.Setter)
                {
                    deferred = true;
                    Assert.IsTrue(current.IsInProgress);
                    Assert.IsNull(caller.Decision);
                    Assert.AreEqual("等待完成标签影响检查", caller.Failure);
                }
            });
            Assert.IsTrue(deferred);
            Assert.IsTrue(run.Complete);
            foreach (AnnotationMethod method in run.Annotations.Methods.Where(method => method.Class == "Calls"))
            {
                Assert.AreEqual(MethodEffectKind.Setter, method.Actual, method.Name);
                bool exempt = method.Name is "OnlyExempt" or "Wrapped";
                Assert.AreEqual(exempt ? "NoLogTrack" : "ShouldTrack", method.Decision, method.Name);
                Assert.AreEqual(exempt, method.SuggestNoLogTrack, method.Name);
            }
            AnnotationMethod cache = run.Annotations.Methods.Single(method => method.Name == "Write");
            Assert.AreEqual(MethodEffectKind.Setter, cache.Actual);
            Assert.AreEqual(annotation == "class" ? "NLTClass" : "NoLogTrack", cache.Decision);
            Assert.AreEqual(annotation == "bare", cache.MissingReason);
            Assert.AreEqual(annotation != "bare", cache.ReviewExemption);
            Assert.IsNull(cache.TrackingEvidence);
            if (annotation == "bare")
            {
                string wrapped = run.Annotations.Methods.Single(method => method.Name == "Wrapped").Id;
                Assert.IsTrue(cache.WarningPaths.Any(path => path[0] == wrapped && path.Length == 3));
            }
        }

        // 多个告警共用一次搜索时，递归不能变成捷径，等长路径仍按函数顺序稳定选择。
        /// <summary>不同入口保留真正最短的告警过程。</summary>
        [TestMethod]
        public async Task EvaluateKeepsShortestWarningAcrossMultipleSeedsAndRecursion()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                public static class Calls
                {
                    private static int state;
                    public static void Root() { Long(); Short(); }
                    public static void Tie() { Alpha(); Beta(); }
                    public static void Cycle(bool again) { if (again) Cycle(again); Beta(); }
                    private static void Long() => Middle();
                    private static void Middle() => Alpha();
                    private static void Short() => Beta();
                    [KH.NoLogTrack] public static void Alpha() { state++; }
                    [KH.NoLogTrack] public static void Beta() { state++; }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable).ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            AnnotationResult result = new AnnotationEvaluator().Evaluate(catalog, roots, new EffectAnalyzer().Analyze(catalog, roots, calls), calls);
            Dictionary<string, string> names = calls.Methods.ToDictionary(method => method.Id, method => method.Name);
            string[][] paths = result.Methods.SelectMany(method => method.WarningPaths).ToArray();
            CollectionAssert.AreEqual(new[] { "Root", "Short", "Beta" }, paths.Single(path => names[path[0]] == "Root").Select(id => names[id]).ToArray());
            CollectionAssert.AreEqual(new[] { "Cycle", "Beta" }, paths.Single(path => names[path[0]] == "Cycle").Select(id => names[id]).ToArray());
            string first = result.Methods.Where(method => method.MissingReason).OrderBy(method => method.Id, StringComparer.Ordinal).First().Name;
            CollectionAssert.AreEqual(new[] { "Tie", first }, paths.Single(path => names[path[0]] == "Tie").Select(id => names[id]).ToArray());
        }

        // 同一个委托包装函数被不同根调用时，告警只能沿实际绑定回到对应根。
        /// <summary>纯回调的上层不能串到另一调用的 Setter，多个告警也只保留每个根的最短过程。</summary>
        [TestMethod]
        public async Task EvaluateKeepsWarningBindingsSeparate()
        {
            using TestProject project = TestProject.CreateWithCallTargets("""
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                namespace Samples
                {
                    public delegate void Callback();
                    public static class Calls
                    {
                        private static int state;
                        public static void A() => Helper(new Callback(Safe));
                        public static void B() { Helper(new Callback(Change)); Helper(new Callback(ChangeAgain)); }
                        private static void Helper(Callback callback) => callback();
                        private static void Safe() { }
                        [KH.NoLogTrack] public static void Change() { state++; }
                        [KH.NoLogTrack] public static void ChangeAgain() { state++; }
                    }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable && method.Name is "A" or "B" or "Change" or "ChangeAgain").ToArray();
            CallTargetResolutionResult calls = await new CallTargetResolver().ResolveAsync(material, catalog, roots, 2);
            AnnotationResult result = new AnnotationEvaluator().Evaluate(catalog, roots, new EffectAnalyzer().Analyze(catalog, roots, calls), calls);
            string a = roots.Single(method => method.Name == "A").Id;
            string b = roots.Single(method => method.Name == "B").Id;
            string[][] paths = result.Methods.SelectMany(method => method.WarningPaths).ToArray();
            Assert.IsFalse(paths.Any(path => path[0] == a));
            Assert.HasCount(1, paths.Where(path => path[0] == b));
            Assert.HasCount(3, paths.Single(path => path[0] == b));
        }

        // 原样使用项目的原因字符串与数值标记构造形式验证标签决定。
        /// <summary>只有原因字符串和类标签属于有理由的豁免，未知行为仍不得生成补标。</summary>
        [TestMethod]
        public async Task EvaluateSeparatesEvidenceExemptionsAndFailures()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH
                {
                    public class NoLogTrackAttribute : System.Attribute
                    {
                        public NoLogTrackAttribute(long flag = 0, string reason = "") { }
                        public NoLogTrackAttribute(string reason) { }
                    }
                    public class LogTrackAttribute : System.Attribute { }
                    public class Example
                    {
                        [NoLogTrack] public void Bare() { }
                        [NoLogTrack(8)] public void Flag() { }
                        [NoLogTrack("Tool")] public void Reason() { }
                        [NoLogTrack(reason: "Tool")] public void NamedReason() { }
                        [NoLogTrack(0, "")] public void EmptyReason() { }
                        [NoLogTrack] public void PureBare() { }
                        public void Pure() { }
                        public void Unknown() { }
                        [LogTrack] public void Forced() { }
                        [LogTrack, NoLogTrack] public void Conflict() { }
                    }
                    [NoLogTrack] public class Exempt { public void ClassSetter() { } }
                }
                """);
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 2);
            MethodEntry[] roots = catalog.Methods.Where(method => method.IsReportable).ToArray();
            MethodEffect[] facts = roots.Where(method => method.Name != "Unknown").Select(method => new MethodEffect(method.Id,
                method.Name is "Pure" or "PureBare" or "Forced" ? MethodEffectKind.Getter : MethodEffectKind.Setter, null)).ToArray();
            AnnotationResult result = new AnnotationEvaluator().Evaluate(catalog, roots, new EffectAnalysisResult(facts, TimeSpan.Zero), null);
            CollectionAssert.AreEquivalent(new[] { "Bare", "Flag", "EmptyReason" }, result.Methods
                .Where(method => method.MissingReason).Select(method => method.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Reason", "NamedReason", "ClassSetter" }, result.Methods
                .Where(method => method.ReviewExemption).Select(method => method.Name).ToArray());
            Assert.AreEqual("NLTClass", result.Methods.Single(method => method.Name == "ClassSetter").Decision);
            Assert.AreEqual("ShouldTrack", result.Methods.Single(method => method.Name == "Forced").Decision);
            Assert.AreEqual(MethodEffectKind.Getter, result.Methods.Single(method => method.Name == "Forced").Actual);
            Assert.IsNull(result.Methods.Single(method => method.Name == "Conflict").Decision);
            Assert.IsNotNull(result.Methods.Single(method => method.Name == "Conflict").Failure);
            Assert.IsNull(result.Methods.Single(method => method.Name == "Unknown").Decision);
            Assert.IsFalse(result.Complete);
            Assert.AreEqual(1, result.Methods.Count(method => method.SuggestNoLogTrack));
            Assert.AreEqual("Pure", result.Methods.Single(method => method.SuggestNoLogTrack).Name);
        }
    }
}
