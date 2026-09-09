namespace SetterChecker.Core.Tests
{
    /// <summary>检查真实行为与人工日志规则保持分离。</summary>
    [TestClass]
    public sealed class AnnotationEvaluatorTests
    {
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
            AnnotationResult result = new AnnotationEvaluator().Evaluate(roots, new EffectAnalyzer().Analyze(catalog, roots, calls), calls);
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
            AnnotationResult result = new AnnotationEvaluator().Evaluate(roots, new EffectAnalyzer().Analyze(catalog, roots, calls), calls);
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
            AnnotationResult result = new AnnotationEvaluator().Evaluate(roots, new EffectAnalysisResult(facts, TimeSpan.Zero), null);
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
