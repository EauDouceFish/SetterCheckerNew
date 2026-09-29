using System.Text.Json;

namespace SetterChecker.Core.Tests
{
    /// <summary>只保留 khengine 真实写法的关键结果回归；完整游戏由命令行单独验收。</summary>
    [TestClass]
    public sealed class KhengineTests
    {
        private const string Attributes = """
            namespace KH
            {
                public sealed class NoLogTrackAttribute : System.Attribute
                {
                    public NoLogTrackAttribute(string reason = "") { }
                }
            }
            """;

        // 委托经实例字段和普通返回转发后，仍只属于原来的接收对象。
        /// <summary>另一个实例登记的 Setter 不得污染当前实例的 Getter 委托。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReturnedDelegateFieldKeepsItsReceiver(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box
                    {
                        public System.Action Handler;
                        public System.Action Read() => Handler;
                    }
                    public static class Calls
                    {
                        private static int s_state;
                        private static void Pure() { }
                        private static void Write() { s_state++; }
                        public static void Entry() { new Box { Handler = new System.Action(Pure) }.Read()(); }
                        public static void Other() { new Box { Handler = new System.Action(Write) }.Read()(); }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应 SVN 更新删除旧文件并新增源码，旧响应清单不能阻止静态分析。
        /// <summary>当前文件归属覆盖旧清单，并尊重子目录的程序集边界。</summary>
        [TestMethod]
        public async Task CurrentSourceFilesReplaceStaleResponseList()
        {
            using TestProject project = TestProject.Create("namespace KH { public class Original { } }");
            string directory = Path.GetDirectoryName(project.AssemblyDefinitionPath)!;
            string moved = Path.Combine(directory, "Moved.cs");
            File.Move(Path.Combine(directory, "Root.cs"), moved);
            string added = Path.Combine(directory, "Added.cs");
            File.WriteAllText(added, "namespace KH { public class Added { } }", new System.Text.UTF8Encoding(false));
            string excluded = Path.Combine(directory, "Separate");
            Directory.CreateDirectory(excluded);
            File.WriteAllText(Path.Combine(excluded, "Other.asmdef"), "{\"name\":\"Other\"}", new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(excluded, "Other.cs"), "invalid outside active assembly", new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(project.Request(4));
            CollectionAssert.AreEquivalent(new[] { moved, added }, material.SourceAssemblies.Single().SourcePaths.ToArray());
        }

        // 构建图只取顶层 Nodes 中的 Csc 节点；属性顺序、嵌套内容和其他节点不影响源码程序集之间的引用识别。
        /// <summary>流式读取合成构建图后，上层程序集仍以源码引用连接 khengine。</summary>
        [TestMethod]
        public async Task BuildGraphKeepsOnlyCscNodeInputsAndOutputs()
        {
            using TestProject project = TestProject.Create("namespace KH { public class Engine { } }",
                registrations: "public class Registration { public KH.Engine Engine; }");
            string graphPath = Path.Combine(project.RootPath, "Library/Bee/build.json");
            string cscNodes;
            using (JsonDocument original = JsonDocument.Parse(File.ReadAllText(graphPath)))
            {
                cscNodes = string.Join(",", original.RootElement.GetProperty("Nodes").EnumerateArray().Select(node =>
                    $$"""{"Inputs":{{node.GetProperty("Inputs").GetRawText()}},"Env":[{"Key":"A","Value":[1,{"B":null}]}],"Annotation":{{node.GetProperty("Annotation").GetRawText()}},"Outputs":{{node.GetProperty("Outputs").GetRawText()}},"ToBuildDependencies":[1,2]}"""));
            }
            File.WriteAllText(graphPath, $$"""
                {"Meta":{"Nodes":[{"Annotation":"Csc Fake"}]},"Nodes":[
                {"Annotation":"CopyFiles Library/Bee/artifacts/build/khengine.runtime.dll","Inputs":{"Nested":["x.rsp"]},"Outputs":[1]},
                {"Inputs":["a.txt"],"Outputs":["Library/Bee/artifacts/build/Other.dll"],"Annotation":"CscLike"},
                {{cscNodes}}],"Tail":[{"Annotation":"Csc Late"}]}
                """, new System.Text.UTF8Encoding(false));
            MaterialSet material = await new MaterialLoader().LoadAsync(project.Request(4));
            CollectionAssert.AreEqual(new[] { "Assembly-CSharp", "khengine.runtime" }, material.SourceAssemblies.Select(assembly => assembly.Name).ToArray());
            Assert.IsTrue(material.SourceAssemblies[0].Compilation.References.OfType<Microsoft.CodeAnalysis.CompilationReference>().Any());
        }

        // 对照关闭跨函数来源后的未知结果，确保不会把缺失来源当成 Getter。
        /// <summary>直接 Setter 不受实验开关影响，工厂返回来源则必须明确未证明。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CrossMethodOriginSwitchRetainsUnknown(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box { public int Value; }
                    public static class Calls
                    {
                        private static int[] Make() => new int[1];
                        public static void Read() { Make()[0] = 1; }
                        public static void Write(Box box) { box.Value = 1; }
                    }
                }
                """, external);
            var enabled = await project.AnalyzeAsync("KH.Calls", "Read");
            AnalysisException disabled = await Assert.ThrowsAsync<AnalysisException>(() => project.AnalyzeAsync("KH.Calls", "Read", crossMethodOrigins: false));
            var setter = await project.AnalyzeAsync("KH.Calls", "Write", crossMethodOrigins: false);
            Assert.AreEqual(MethodEffectKind.Getter, enabled.Effects.Methods.Single().Kind);
            StringAssert.Contains(disabled.Message, "尚未确定");
            Assert.AreEqual(MethodEffectKind.Setter, setter.Effects.Methods.Single().Kind);
        }

        // 对应 Lua 读取返回数组时，不能顺带追踪其他函数自己的反射实参数组。
        /// <summary>独立数组的写入不会因另一个数组被读取而继续展开。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task IndependentArraysDoNotRequestUnrelatedReturns(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public interface IProxy { void Run(); }
                    public class Pure : IProxy { public void Run() { } }
                    public static class Calls
                    {
                        private static int s_state;
                        private static object UnrelatedReturn() => new object();
                        [NoLogTrack("unrelated")]
                        private static void Unrelated()
                        {
                            s_state++;
                            object[] args = new object[1];
                            args[0] = UnrelatedReturn();
                        }
                        public static void Entry()
                        {
                            Unrelated();
                            object[] values = new object[] { new Pure() };
                            ((IProxy)values[0]).Run();
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
            Assert.IsFalse(result.Calls.Methods.Any(method => method.Name == "UnrelatedReturn"),
                "没有对象传递关系的另一个数组，不应触发返回值分析。");
        }

        // 对应 Lua 泛型包装被多种实参调用，目标身份与返回来源必须各自保留。
        /// <summary>同一声明只读一份函数体，但 int 与 string 是不同构造调用。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericCallsKeepMethodArguments(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Calls
                    {
                        private static T Read<T>(T value) => value;
                        public static object Entry(bool first)
                        {
                            int a = Read(1);
                            string b = Read("x");
                            if (first) return a;
                            return b;
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            ResolvedCall[] calls = result.Calls.Calls.Where(call => call.Call.Target.Name == "Read").ToArray();
            CollectionAssert.AreEquivalent(new[] { "System.Int32", "System.String" },
                calls.Select(call => call.Targets.Single().Reference.GenericArgumentTypeIds.Single()).ToArray());
            foreach (ResolvedCall call in calls)
            {
                ResolvedCallTarget target = call.Targets.Single();
                Assert.AreEqual(target.Reference.GenericArgumentTypeIds.Single(), target.Reference.Identity.ReturnType.Text);
                var origins = result.Calls.ValueSources.GetOrigins(new(call.CallerMethodId, call.Call.ResultValueId!.Value));
                CollectionAssert.AreEqual(new[] { target.Reference.Identity.ReturnType.Text == "System.Int32" ? "1" : "x" },
                    origins.Select(origin => origin.Value.Reference).ToArray());
            }
        }

        // 对应泛型包装返回类型，再反射选择方法；类与方法的两层实参不能串用。
        /// <summary>泛型类型值使用当前调用的实参，不混入另一构造调用。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericTypeReturnSelectsOnlyItsOwnTarget(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Pure { public static void Foo() { } }
                    public class Writer { private static int s_state; public static void Foo() { s_state++; } }
                    public class Wrapper<TClass>
                    {
                        public static System.Type Read<TMethod>() => typeof(TMethod);
                    }
                    public static class Calls
                    {
                        public static void Entry()
                        {
                            var ignored = Wrapper<Pure>.Read<Writer>();
                            Wrapper<Writer>.Read<Pure>().GetMethod("Foo").Invoke(null, null);
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应 Lua 包装将 object 转为 T，构造返回类型必须约束后续接口目标。
        /// <summary>不能把另一个 T 才能接收的对象混入本次返回。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericReturnCastExcludesOtherTypes(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public interface IProxy { void Run(); }
                    public class Pure : IProxy { public void Run() { } }
                    public class Writer : IProxy { private int m_state; public void Run() { m_state++; } }
                    public static class Calls
                    {
                        [NoLogTrack("factory")]
                        private static object Read(bool first) => first ? (object)new Pure() : new Writer();
                        private static T Cast<T>(object value) => (T)value;
                        public static void Entry(bool first)
                        {
                            ((IProxy)Cast<Pure>(Read(first))).Run();
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应泛型工厂返回对象并从成员取出目标，类型实参贯穿创建与字段读取。
        /// <summary>不同泛型对象的成员来源保持独立。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericFactoryFieldKeepsItsArgument(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public interface IProxy { void Run(); }
                    public class Pure : IProxy { public void Run() { } }
                    public class Writer : IProxy { private int m_state; public void Run() { m_state++; } }
                    public class Holder<T> { public T Value; }
                    public static class Calls
                    {
                        [NoLogTrack("factory")]
                        private static Holder<T> Create<T>(T value) => new Holder<T> { Value = value };
                        public static void Entry()
                        {
                            var first = Create(new Pure());
                            var second = Create(new Writer());
                            ((IProxy)first.Value).Run();
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应工厂返回泛型方法委托，方法实参必须随委托一起返回。
        /// <summary>泛型委托继续调用时仍使用创建处的类型实参。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericDelegatePreservesMethodArgument(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Pure { public static void Foo() { } }
                    public class Writer { private static int s_state; public static void Foo() { s_state++; } }
                    public static class Calls
                    {
                        private static System.Type TypeOf<T>() => typeof(T);
                        private static System.Func<System.Type> Create<T>() => new System.Func<System.Type>(TypeOf<T>);
                        public static void Entry()
                        {
                            var ignored = Create<Writer>();
                            Create<Pure>()().GetMethod("Foo").Invoke(null, null);
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应泛型工厂用 new T() 创建实例，具体构造函数的静态写入应传播到入口。
        /// <summary>泛型工厂调用会执行 Box 构造函数，因此入口为 Setter。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericFactoryRunsConstructor(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box
                    {
                        private static int s_state;
                        public Box() { s_state++; }
                    }
                    public static class Calls
                    {
                        private static T Make<T>() where T : new() => new T();
                        public static void Entry() => Make<Box>();
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应泛型 lambda 工厂延迟执行 new T()，实际构造函数的静态写入应传播到入口。
        /// <summary>调用泛型工厂返回的 lambda 会执行 Box 构造函数并成为 Setter。</summary>
        [TestMethod]
        public async Task GenericLambdaFactoryRunsConstructor()
        {
            using TestProject project = TestProject.Create("""
                using System;
                namespace KH
                {
                    public class Box
                    {
                        private static int s_state;
                        public Box() { s_state++; }
                    }
                    public static class Calls
                    {
                        private static Func<T> Make<T>() where T : new() => () => new T();
                        public static void Entry() { Make<Box>()(); }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应只创建迭代器而不枚举，yield 函数体尚未执行，入口应保持 Getter。
        /// <summary>保存 IEnumerable 不会执行 yield 迭代器的静态写入。</summary>
        [TestMethod]
        public async Task YieldSequenceCreationIsGetter()
        {
            using TestProject project = TestProject.Create("""
                using System.Collections.Generic;
                namespace KH
                {
                    public static class Calls
                    {
                        private static int s_state;
                        private static IEnumerable<int> Values()
                        {
                            s_state++;
                            yield return 1;
                        }
                        public static void Entry()
                        {
                            IEnumerable<int> sequence = Values();
                        }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应 foreach 枚举迭代器，yield 函数体执行后静态写入应传播到入口。
        /// <summary>枚举 IEnumerable 会执行 yield 迭代器的静态写入。</summary>
        [TestMethod]
        public async Task YieldSequenceEnumerationIsSetter()
        {
            using TestProject project = TestProject.Create("""
                using System.Collections.Generic;
                namespace KH
                {
                    public static class Calls
                    {
                        private static int s_state;
                        private static IEnumerable<int> Values()
                        {
                            s_state++;
                            yield return 1;
                        }
                        public static void Entry()
                        {
                            foreach (int item in Values()) { }
                        }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应 yield 结果经参数传入 Consume 后枚举，迭代器中的静态写入应传播到入口。
        /// <summary>参数传递的 IEnumerable 枚举仍执行 yield 的 Setter。</summary>
        [TestMethod]
        public async Task YieldSequenceThroughParameterIsSetter()
        {
            using TestProject project = TestProject.Create("""
                using System.Collections.Generic;
                namespace KH
                {
                    public static class Calls
                    {
                        private static int s_state;
                        private static IEnumerable<int> Values()
                        {
                            s_state++;
                            yield return 1;
                        }
                        private static void Consume(IEnumerable<int> sequence)
                        {
                            foreach (int item in sequence) { }
                        }
                        public static void Entry() { Consume(Values()); }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 插值与等价的显式格式化共用行为，不为运行库临时写入另加豁免。
        /// <summary>语法改写不改变当前函数级传播规则的结果。</summary>
        [TestMethod]
        public async Task PrimitiveInterpolationMatchesExplicitFormatting()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Calls
                    {
                        public static string Entry(int number, string text) => $"{number}:{text}";
                        public static string Explicit(int number, string text) => string.Format("{0}:{1}", number, text);
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            var explicitFormat = await project.AnalyzeAsync("KH.Calls", "Explicit");
            Assert.AreEqual(explicitFormat.Effects.Methods.Single().Kind, result.Effects.Methods.Single().Kind);
        }

        // 对应无格式说明的对象插值，重写 ToString 的静态写入应传播到入口。
        /// <summary>对象插值调用重写的 ToString 后为 Setter。</summary>
        [TestMethod]
        public async Task OverriddenToStringInterpolationIsSetter()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box
                    {
                        private static int s_state;
                        public override string ToString()
                        {
                            s_state++;
                            return "x";
                        }
                    }
                    public static class Calls
                    {
                        public static string Entry(Box box) => $"{box}";
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应带格式说明的对象插值，IFormattable.ToString 的静态写入不可漏掉。
        /// <summary>格式化插值应调用 IFormattable 实现并传播 Setter。</summary>
        [TestMethod]
        public async Task FormattableInterpolationIsSetter()
        {
            using TestProject project = TestProject.Create("""
                using System;
                namespace KH
                {
                    public class Box : IFormattable
                    {
                        private static int s_state;
                        public string ToString(string format, IFormatProvider formatProvider)
                        {
                            s_state++;
                            return "x";
                        }
                        public override string ToString() => "pure";
                    }
                    public static class Calls
                    {
                        public static string Entry(Box box) => $"{box:0}";
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应纯 yield 枚举的关键对照，接口候选没有任何写入时应保持 Getter。
        /// <summary>枚举纯迭代器不会引入 Setter。</summary>
        [TestMethod]
        public async Task PureYieldSequenceEnumerationIsGetter()
        {
            using TestProject project = TestProject.Create("""
                using System.Collections.Generic;
                namespace KH
                {
                    public static class Calls
                    {
                        private static IEnumerable<int> Values()
                        {
                            yield return 1;
                        }
                        public static void Entry()
                        {
                            foreach (int item in Values()) { }
                        }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind,
                JsonSerializer.Serialize(result.Effects.Methods.Single().Evidence));
        }

        // 对应 Lua 的原生返回边界，缺少托管实现必须保留未证明结果。
        /// <summary>停止无效返回值追踪不能把未知函数变成 Getter。</summary>
        [TestMethod]
        public async Task NativeReturnRemainsUnproven()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Calls
                    {
                        [System.Runtime.InteropServices.DllImport("GameNative")]
                        private static extern object Native();
                        public static object Entry() => Native();
                    }
                }
                """);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4), deadline.Token);
            Assert.IsFalse(run.Complete);
            Assert.IsNull(run.Annotations.Methods.Single().Actual);
            // 规则 R5：返回值来源本身不再使函数未证明；未知来自调用原生函数，按原生边界采用当前人工标签。
            Assert.IsTrue(run.Annotations.Methods.Single().UsesManualBaseline);
            Assert.AreEqual("ShouldTrack", run.Annotations.Methods.Single().Decision);
        }

        // 对应 Framework/WorldModule/Manager/Data/ConfigItem.cs 的加载、释放和字符串读取。
        /// <summary>已有字段写入足以确定 Setter，反射调用不妨碍报告；线程数不改变结果。</summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(4)]
        public async Task ConfigItemProducesCompleteReport(int jobs)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public class ConfigItem
                    {
                        public string Alian;
                        public System.Reflection.MethodInfo ParseMethod;
                        public System.Reflection.MethodInfo ReleaseMethod;
                        public object DataManager;
                        private bool isLoaded = false;
                        public bool IsLoaded { get { return isLoaded; } }
                        public void LoadData()
                        {
                            if (ParseMethod == null) return;
                            ParseMethod.Invoke(DataManager, null);
                            isLoaded = true;
                        }
                        public void ReleaseData()
                        {
                            if (ReleaseMethod == null) return;
                            ReleaseMethod.Invoke(DataManager, null);
                            isLoaded = false;
                        }
                        [NoLogTrack] public override string ToString() { return Alian; }
                    }
                }
                """);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(jobs), deadline.Token);
            Assert.IsTrue(run.Complete, run.Failure ?? string.Join("; ", run.Annotations.Methods.Select(method => method.Failure)));
            CollectionAssert.AreEqual(new[] { "LoadData", "ReleaseData" },
                run.Annotations.Methods.Where(method => method.Actual == MethodEffectKind.Setter).Select(method => method.Name).Order().ToArray());
            Assert.AreEqual("NoLogTrack", run.Annotations.Methods.Single(method => method.Name == "ToString").Decision);
            string output = Path.Combine(project.RootPath, "report");
            new ReportWriter().Write(run, output);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "report.json")));
            Assert.AreEqual(3, report.RootElement.GetProperty("Total").GetInt32());
            Assert.AreEqual(2, report.RootElement.GetProperty("DetectedShouldTrack").GetInt32());
            StringAssert.Contains(File.ReadAllText(Path.Combine(output, "report.md")), "生成源码程序集：0");
        }

        // 对应 Data/Script/Base/KHScriptData.cs:354，保留反射字段数组、循环和参数读取。
        /// <summary>源码和真实 DLL 中的字段复制都修改已有对象。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ScriptDataReflectionCopyIsSetter(bool external)
        {
            using TestProject project = TestProject.Create("""
                using System.Reflection;
                namespace KH
                {
                    public class KHScriptData
                    {
                        public int scriptType;
                        public void SetValue(KHScriptData value)
                        {
                            if (value == null) { return; }
                            FieldInfo[] fieldInfos = this.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                            for (int i = 0; i < fieldInfos.Length; i++)
                            {
                                fieldInfos[i].SetValue(this, fieldInfos[i].GetValue(value));
                            }
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.KHScriptData", "SetValue");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应 RuntimeMethodProxy/ConfigProxy.cs:GetBasePath；外围代理实现单独存放。
        /// <summary>接口有任意 Setter 实现即为 Setter，裸标签豁免仍生效并提示缺少原因。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ProxyChecksAllImplementationsAndHonorsLabel(bool writes)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public interface IRuntimeMethodProxy { }
                    public static class Store { public static int state; }
                    public class RuntimeMethodProxy<TInterface, TInstance>
                        where TInterface : IRuntimeMethodProxy where TInstance : new()
                    {
                        protected static TInterface ms_impl;
                        public static void SetImpl(TInterface impl) { ms_impl = impl; }
                    }
                    public interface IConfigProxy : IRuntimeMethodProxy { string GetBasePath(); }
                    public class ConfigProxy : RuntimeMethodProxy<IConfigProxy, ConfigProxy>, IConfigProxy
                    {
                        [NoLogTrack] public string GetBasePath()
                        {
                            if (ms_impl != null) { return ms_impl.GetBasePath(); }
                            return null;
                        }
                    }
                }
                """, registrations: """
                public class First : KH.IConfigProxy { public string GetBasePath() { return ""; } }
                public class Second : KH.IConfigProxy
                {
                    private static int count;
                    public string GetBasePath() { CHANGE return ""; }
                }
                public static class MethodProxyHelper
                {
                    public static void Init() { KH.ConfigProxy.SetImpl(new Second()); }
                }
                """.Replace("CHANGE", writes ? "KH.Store.state++;" : ""));
            // V3 归属规则：Assembly-CSharp 自身的静态字段不是战斗状态，实现改写 khengine 的 Store 以保持测试意图。
            var result = await project.AnalyzeAsync("KH.ConfigProxy", "GetBasePath");
            Assert.AreEqual(writes ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
            MethodEntry root = result.Calls.Methods.Single(method => method.Id == result.Effects.Methods.Single().MethodId);
            AnnotationMethod annotation = new AnnotationEvaluator().Evaluate(result.Catalog, new[] { root }, result.Effects, result.Calls).Methods.Single();
            Assert.AreEqual("NoLogTrack", annotation.Decision);
            Assert.AreEqual(writes, annotation.MissingReason);
        }

        // 对应 Script/KHScriptManager.cs:RegisterModeHook；三种委托不可混为一个候选列表。
        /// <summary>外部注册提供实际目标，初始化本身和其他槽的写入不污染当前调用。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ModeHooksStayInTheirOwnSlots(bool writes)
        {
            using TestProject project = TestProject.Create("""
                using ExternalLibrary;
                namespace KH
                {
                    public class KHScript { }
                    public static class Store { public static int state; }
                    public static class KHScriptManager
                    {
                        public delegate System.Type ExternalGetScriptType(int scriptType);
                        public delegate KHScript ExternalGetScript(int scriptType);
                        public delegate int ExternalGetScriptTypeID(System.Type clsType);
                        private static ExternalGetScriptType m_externalScriptTypeHook;
                        private static ExternalGetScript m_externalGetScriptHook;
                        private static ExternalGetScriptTypeID m_externalGetScriptIdHook;
                        public static bool HasScriptTypeHook() { return m_externalScriptTypeHook.IsPresent(); }
                        public static void RegisterModeHook(ExternalGetScriptType scriptTypeHook, ExternalGetScript getScriptHook,
                            ExternalGetScriptTypeID getScriptTypeID)
                        {
                            m_externalScriptTypeHook = scriptTypeHook;
                            m_externalGetScriptHook = getScriptHook;
                            m_externalGetScriptIdHook = getScriptTypeID;
                        }
                        public static System.Type ReadModeType(int scriptType)
                        {
                            if (m_externalScriptTypeHook != null)
                            {
                                var ret = m_externalScriptTypeHook(scriptType);
                                if (ret != null) { return ret; }
                            }
                            return null;
                        }
                    }
                }
                """, registrations: """
                using ExternalLibrary;
                public static class Mode
                {
                    public static void Init()
                    {
                        KH.Store.state++;
                        KH.KHScriptManager.RegisterModeHook(GetScriptType, GetScript, GetScriptId);
                    }
                    private static System.Type GetScriptType(int id) { CHANGE return typeof(KH.KHScript); }
                    private static KH.KHScript GetScript(int id) { KH.Store.state++; return null; }
                    private static int GetScriptId(System.Type type) { KH.Store.state++; return 1; }
                    // V3 归属规则：Assembly-CSharp 自身的静态字段不是战斗状态，各槽改写 khengine 的 Store 以保持测试意图。
                    public static void Unrelated() { "editor".RegisterModeHook<int>(_ => { }); }
                }
                """.Replace("CHANGE", writes ? "KH.Store.state++;" : ""), librarySource: """
                namespace ExternalLibrary
                {
                    public static class Extensions
                    {
                        public static void RegisterModeHook<T>(this string value, System.Action<T> action) { }
                        public static bool IsPresent<T>(this T value) { return value != null; }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.KHScriptManager", "ReadModeType");
            Assert.AreEqual(writes ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应 Script/KHScriptManager.cs:187，工厂通过字典查出委托再创建脚本。
        /// <summary>采用已确认的函数级规则：真实 TryGetValue 写 out，足以让上层为 Setter。</summary>
        [TestMethod]
        public async Task ScriptFactoryUsesRealLibraryBehavior()
        {
            using TestProject project = TestProject.Create("""
                using System;
                using System.Collections.Generic;
                namespace KH
                {
                    public class KHScript { }
                    public static class KHScriptManager
                    {
                        private static readonly Dictionary<int, Func<KHScript>> ms_scriptCreators = new();
                        public static void AddScriptCreator(int scriptType, Func<KHScript> createFunc)
                        { ms_scriptCreators.Add(scriptType, createFunc); }
                        public static KHScript CreateInstance(int scriptType)
                        {
                            if (ms_scriptCreators.TryGetValue(scriptType, out Func<KHScript> creator))
                            {
                                KHScript script = creator();
                                return script;
                            }
                            return null;
                        }
                    }
                    public class ConfigItem
                    {
                        public string Alian;
                        public override string ToString() { return Alian; }
                    }
                }
                """, registrations: """
                public static class KHScriptFactoryExt
                {
                    public static void Init() { KH.KHScriptManager.AddScriptCreator(1, () => new KH.KHScript()); }
                }
                """);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4), deadline.Token);
            Assert.IsTrue(run.Complete, run.Failure ?? string.Join("; ", run.Annotations.Methods.Select(method => method.Name + ": " + method.Failure)
                .Concat(run.Calls!.PendingCalls.Select(call => call.Failure)).Concat(run.Calls.Behaviors.Methods.Select(body => body.Failure))));
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single(method => method.Name == "CreateInstance").Actual);
            Assert.IsFalse(run.Calls!.Methods.Any(method => method.Name == "Init"),
                "已由真实调用证明 Setter，不应再纳入不影响结果的整个工厂注册函数。");
        }

        // 对应 KFL 的 GetMethod/Invoke 包装；选择函数再调用，修改按普通调用传播。
        /// <summary>反射命中的真实写入不能因为经过包装而丢失。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReflectionCallPropagatesSetter(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Target
                    {
                        private int state;
                        public void Foo() { state++; }
                    }
                    public static class Calls
                    {
                        private static void Call(object instance, string method)
                        { instance.GetType().GetMethod(method).Invoke(instance, null); }
                        public static void Entry(Target value) { Call(value, "Foo"); }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 同一反射包装函数接收多个名称，单名缓存不能漏掉后加入的 Setter。
        /// <summary>多个反射名称复用查询后仍保留各个候选的行为。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task ReflectionNamesKeepEachCandidate(bool external, bool setter)
        {
            using TestProject project = TestProject.Create($$"""
                namespace KH
                {
                    public class Target
                    {
                        private int state;
                        public void First() { }
                        public void Second() { {{(setter ? "state++;" : "")}} }
                    }
                    public static class Calls
                    {
                        private static void Call(Target target, string name)
                        { typeof(Target).GetMethod(name).Invoke(target, null); }
                        public static void Entry(Target target)
                        {
                            Call(target, "First");
                            Call(target, "Second");
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应 ReflectUtil.GetMethodInfo 的 out 传回成员，再由 Invoke 调用；加可信标签核对剪枝边界。
        /// <summary>辅助函数已确定 Setter 且获豁免时，仍须保留调用者真正需要的成员结果。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReflectionOutputSurvivesExemption(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public class Target
                    {
                        private int state;
                        public void Foo() { state++; }
                    }
                    public static class Calls
                    {
                        [NoLogTrack("helper")]
                        private static bool GetMethodInfo(System.Type type, string name, out System.Reflection.MethodInfo method)
                        {
                            method = type.GetMethod(name);
                            return method != null;
                        }
                        public static void Entry(Target value)
                        {
                            System.Reflection.MethodInfo method;
                            if (GetMethodInfo(value.GetType(), "Foo", out method)) { method.Invoke(value, null); }
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应集合或代理先转接口再调用；声明的接收类型已经排除了无关实现。
        /// <summary>非密封接收类型允许其派生类，但不能扩大到所有实现同一接口的类。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task InterfaceKeepsReceiverTypeBoundary(bool external, bool childWrites)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public interface IProxy { int Read(); }
                    public class Proxy : IProxy { public virtual int Read() { return 1; } }
                    public sealed class Child : Proxy { private int state; public override int Read() { CHANGE return 2; } }
                    public sealed class Unrelated : IProxy { private int state; public int Read() { return ++state; } }
                    public static class Calls { public static int Entry(Proxy proxy) { return ((IProxy)proxy).Read(); } }
                }
                """.Replace("CHANGE", childWrites ? "state++;" : ""), external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(childWrites ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应泛型代理接口的继承实现，同时覆盖属性重写和显式接口实现。
        /// <summary>源码编译器映射与 DLL 匹配必须识别同一个实际 Setter。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task GenericInterfaceMapsAccessorsAndExplicitMethods(bool external, bool explicitMethod)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public interface IProxy<T> { int Value { get; } int Read(T value); }
                    public class Proxy<T> : IProxy<T>
                    {
                        public virtual int Value => 0;
                        public virtual int Read(T value) => 0;
                    }
                    public sealed class Child<T> : Proxy<T>, IProxy<T>
                    {
                        private int state;
                        public override int Value => ++state;
                        int IProxy<T>.Read(T value) => ++state;
                    }
                    public static class Calls
                    {
                        public static int Entry(Child<string> proxy) => EXPRESSION;
                    }
                }
                """.Replace("EXPRESSION", explicitMethod ? "((IProxy<string>)proxy).Read(\"x\")" : "((IProxy<string>)proxy).Value"), external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应反射返回 object 后转为具体代理的写法，转换不能丢掉接收类型。
        /// <summary>具体类型已经排除的接口实现不能重新混入候选。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CastKeepsReceiverTypeBoundary(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public interface IProxy { int Read(); }
                    public class Proxy : IProxy { public int Read() => 1; }
                    public class Unrelated : IProxy
                    {
                        private int m_value;
                        public int Read() => ++m_value;
                    }
                    public static class Calls
                    {
                        public static int Entry(object value) => ((IProxy)(Proxy)value).Read();
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应代理对象保存在成员里的写法，DLL 字段读取必须保留声明类型。
        /// <summary>字段类型与源码一致，不能退回整个接口的实现范围。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FieldKeepsReceiverTypeBoundary(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public interface IProxy { int Read(); }
                    public class Proxy : IProxy { public int Read() => 1; }
                    public class Unrelated : IProxy
                    {
                        private int m_value;
                        public int Read() => ++m_value;
                    }
                    public class Holder { public Proxy Proxy; }
                    public static class Calls
                    {
                        public static int Entry(Holder holder) => ((IProxy)holder.Proxy).Read();
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应工厂返回接口后继续调用，尚未得到返回来源不代表可以使用所有接口实现。
        /// <summary>未完成的来源不能提前产生无法撤销的 Setter 结论。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task PendingFactoryReturnDoesNotIntroduceUnrelatedImplementation(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public interface IProxy { int Read(); }
                    public sealed class Proxy : IProxy { public int Read() => 1; }
                    public sealed class Unrelated : IProxy
                    {
                        private int m_value;
                        public int Read() => ++m_value;
                    }
                    public static class Calls
                    {
                        [NoLogTrack("factory")]
                        private static IProxy Create() => new Proxy();
                        public static int Entry() => Create().Read();
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应工厂创建不同配置对象并保存回调；共用工厂不能混合两个对象的回调。
        /// <summary>共享返回说明仍需保留每个调用处的参数对应。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FactoryInstancesKeepSeparateCallbacks(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public class Holder { public System.Action Callback; }
                    public static class Calls
                    {
                        private static int s_value;
                        private static void Pure() { }
                        private static void Write() { s_value++; }
                        [NoLogTrack("factory")]
                        private static Holder Create(System.Action callback)
                        {
                            var holder = new Holder();
                            holder.Callback = callback;
                            return holder;
                        }
                        public static void Entry()
                        {
                            var first = Create(new System.Action(Pure));
                            var second = Create(new System.Action(Write));
                            first.Callback();
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应空条件访问属性 getter 的调用，属性自身写入静态状态时应传播 Setter。
        /// <summary>空条件访问不能遮住属性 getter 对静态数据的修改。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NullConditionalPropertyWriteIsSetter(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box
                    {
                        private static int s_state;
                        public string Value
                        {
                            get
                            {
                                s_state++;
                                return "x";
                            }
                        }
                    }
                    public class Wrap
                    {
                        private Box m_box;
                        public string Value => m_box?.Value;
                    }
                    public static class Calls
                    {
                        public static string Entry(Wrap wrap) => wrap.Value;
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应空条件访问纯属性 getter 的调用，不应因条件分支本身误判为 Setter。
        /// <summary>空条件访问纯 getter 仍保持 Getter 结论。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NullConditionalPurePropertyIsGetter(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box
                    {
                        public string Value => "x";
                    }
                    public class Wrap
                    {
                        private Box m_box;
                        public string Value => m_box?.Value;
                    }
                    public static class Calls
                    {
                        public static string Entry(Wrap wrap) => wrap.Value;
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应外围注册通过构造函数传入回调，运行函数必须追踪实际注册目标。
        /// <summary>构造函数保存的外围 Setter 回调应传播到 Run。</summary>
        [TestMethod]
        public async Task ConstructorRegistrationMakesRunSetter()
        {
            using TestProject project = TestProject.Create("""
                using System;
                namespace KH
                {
                    public static class Store { public static int state; }
                    public class Handler
                    {
                        private Action _callback;
                        public Handler(Action callback) { _callback = callback; }
                        public void Run() { _callback(); }
                    }
                }
                """, registrations: """
                public static class Register
                {
                    private static void Write() { KH.Store.state++; }
                    public static void Setup() { new KH.Handler(Write); }
                }
                """);
            // V3 归属规则：Assembly-CSharp 自身的静态字段不是战斗状态，回调改写 khengine 的 Store 以保持测试意图。
            var result = await project.AnalyzeAsync("KH.Handler", "Run");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应外围注册通过属性 setter 传入回调，运行函数必须追踪实际注册目标。
        /// <summary>属性保存的外围 Setter 回调应传播到 Run。</summary>
        [TestMethod]
        public async Task PropertyRegistrationMakesRunSetter()
        {
            using TestProject project = TestProject.Create("""
                using System;
                namespace KH
                {
                    public static class Store { public static int state; }
                    public class Handler
                    {
                        private Action _callback;
                        public Action Callback { set { _callback = value; } }
                        public void Run() { _callback(); }
                    }
                }
                """, registrations: """
                public static class Register
                {
                    private static void Write() { KH.Store.state++; }
                    public static void Setup()
                    {
                        KH.Handler handler = new KH.Handler();
                        handler.Callback = Write;
                    }
                }
                """);
            // V3 归属规则：Assembly-CSharp 自身的静态字段不是战斗状态，回调改写 khengine 的 Store 以保持测试意图。
            var result = await project.AnalyzeAsync("KH.Handler", "Run");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应外围注册函数自身写静态状态但只绑定纯回调，Run 不应继承注册函数的写入。
        /// <summary>属性注册的纯回调保持 Handler.Run 为 Getter。</summary>
        [TestMethod]
        public async Task PropertyRegistrationPureCallbackKeepsRunGetter()
        {
            using TestProject project = TestProject.Create("""
                using System;
                namespace KH
                {
                    public class Handler
                    {
                        private Action _callback;
                        public Action Callback { set { _callback = value; } }
                        public void Run() { _callback(); }
                    }
                }
                """, registrations: """
                public static class Register
                {
                    private static int state;
                    private static void Pure() { }
                    public static void Setup()
                    {
                        state++;
                        KH.Handler handler = new KH.Handler();
                        handler.Callback = Pure;
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Handler", "Run");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应 KFL 的基类注册入口，其他实现先命中 Setter 也不能漏掉实际回调的绑定。
        /// <summary>仅需某个重写的注册参数时不混入其他重写的行为。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task VirtualRegistrationKeepsRequestedCallback(bool setter)
        {
            using TestProject project = TestProject.Create("""
                using System;
                namespace KH
                {
                    public abstract class Handler { public abstract void Register(Action callback); }
                    public class AOther : Handler
                    {
                        private static int state;
                        public override void Register(Action callback) { state++; }
                    }
                    public class ZHandler : Handler
                    {
                        private Action callback;
                        public override void Register(Action value) { callback = value; }
                        public void Run() { callback(); }
                    }
                }
                """, registrations: $$"""
                public static class Register
                {
                    private static int state;
                    private static void Callback() { {{(setter ? "state++;" : "")}} }
                    public static void Setup(KH.Handler handler) { handler.Register(Callback); }
                }
                """);
            var result = await project.AnalyzeAsync("KH.ZHandler", "Run");
            Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 循环中后续赋值可能改变下一轮接收对象，来源通知不能漏掉外部对象。
        /// <summary>来源查询消除重复通知后仍保留循环传入的外部写入。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CyclicAliasRetainsExternalWrite(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Box { public int Value; }
                    public static class Calls
                    {
                        public static void Entry(Box outside, bool repeat)
                        {
                            Box first = new Box();
                            Box second = new Box();
                            while (repeat)
                            {
                                first.Value++;
                                first = second;
                                second = outside;
                            }
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 返回数组可经过递归和共享工厂，必须保留每个实际元素及其回调。
        /// <summary>循环数组关系不按路径展开，也不丢掉循环中新增的 Setter 元素。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task RecursiveReturnedArrayKeepsCallback(bool external, bool setter)
        {
            using TestProject project = TestProject.Create($$"""
                using System;
                namespace KH
                {
                    public static class Calls
                    {
                        private static int state;
                        private static void Pure() { }
                        private static void Callback() { {{(setter ? "state++;" : "")}} }
                        private static Action[] Make(Action callback) => new[] { callback };
                        private static Action[] Left(bool next) => next ? Right(false) : Make(new Action(Pure));
                        private static Action[] Right(bool next) => next ? Left(false) : Make(new Action(Callback));
                        public static void Entry(bool next) { Left(next)[0](); }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 工厂返回多个候选数组时，共享查询仍须保留每个元素的延迟执行行为。
        /// <summary>交换数组候选顺序不改变迭代器 Setter 的识别结果。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task ReturnedIteratorArraysKeepAllCandidates(bool reverse, bool setter)
        {
            using TestProject project = TestProject.Create($$"""
                using System.Collections.Generic;
                namespace KH
                {
                    public static class Calls
                    {
                        private static int state;
                        private static IEnumerable<int> Pure() { yield return 1; }
                        private static IEnumerable<int> Candidate() { {{(setter ? "state++;" : "")}} yield return 2; }
                        private static IEnumerable<int>[] Wrap(IEnumerable<int> value) => new[] { value };
                        private static IEnumerable<int>[] Make(bool next) => next
                            ? Wrap({{(reverse ? "Candidate" : "Pure")}}()) : Wrap({{(reverse ? "Pure" : "Candidate")}}());
                        public static void Entry(bool next) { foreach (int value in Make(next)[0]) { } }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, result.Effects.Methods.Single().Kind,
                JsonSerializer.Serialize(result.Effects.Methods.Single().Evidence));
        }

        // 对应外围 Setup 把 yield 结果写入静态 IEnumerable 字段，入口枚举字段应执行迭代器 Setter。
        /// <summary>静态 IEnumerable 字段的注册来源仍传播 yield 的 Setter。</summary>
        [TestMethod]
        public async Task RegisteredYieldSequenceFieldIsSetter()
        {
            using TestProject project = TestProject.Create("""
                using System.Collections.Generic;
                namespace KH
                {
                    public static class Calls
                    {
                        private static int s_state;
                        public static IEnumerable<int> Sequence;
                        public static IEnumerable<int> Values()
                        {
                            s_state++;
                            yield return 1;
                        }
                        public static void Entry()
                        {
                            foreach (int item in Sequence) { }
                        }
                    }
                }
                """, registrations: """
                public static class Register
                {
                    public static void Setup() { KH.Calls.Sequence = KH.Calls.Values(); }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应 KFL 对对象类型的比较，比较本身不需要运行外部来源函数。
        /// <summary>可信来源的返回对象尚不可知，也不影响类型比较的追踪判断。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TypeComparisonDoesNotRequireObjectContents(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public static class Calls
                    {
                        private static int s_state;
                        [NoLogTrack("native")]
                        private static object Read() { s_state++; return Native(); }
                        [System.Runtime.InteropServices.DllImport("GameNative")]
                        private static extern object Native();
                        private static bool IsInt(System.Type type) => type == typeof(int);
                        public static void Entry()
                        {
                            if (IsInt(Read().GetType())) { }
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            // 规则 R1：Read 带可信 NLT 但确实写了静态数据，真实行为使 Entry 成为 Setter；类型比较本身仍不要求读取对象内容。
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
            Assert.AreEqual(0, result.Effects.Failures.Count);
        }

        // 对应包装函数返回运行时类型，再按名称反射调用的写法。
        /// <summary>延迟读取具体类型仍保留各调用处的对象对应。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RuntimeTypeThroughWrapperKeepsReceiver(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Pure { public void Foo() { } }
                    public class Writer { private int m_state; public void Foo() { m_state++; } }
                    public static class Calls
                    {
                        private static System.Type TypeOf(object value) => value.GetType();
                        public static void Entry()
                        {
                            var first = new Pure();
                            var second = new Writer();
                            var ignored = TypeOf(second);
                            TypeOf(first).GetMethod("Foo").Invoke(first, null);
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应脚本工厂返回业务对象；创建辅助函数的豁免不抹掉调用者自己的对象传出。
        /// <summary>以 object 返回新建业务对象，仍须保留其创建与传出事实。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task BusinessObjectReturnSurvivesFactoryExemption(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public class KHScript { }
                    public static class Calls
                    {
                        [NoLogTrack("factory")]
                        private static object Create() { return new KHScript(); }
                        public static object Entry() { return Create(); }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            // 规则 R5：创建对象并返回本身不算修改。
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应代理返回类型再查找配置回调；可信缓存的修改不能遮住另一个合法返回目标。
        /// <summary>实现列表提前找到 Setter 后，返回值查询仍能继续读取剩余实现。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task PrunedInterfaceRetainsOtherReturnTargets(bool external)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public interface ISelector { System.Type Read(); }
                    public sealed class First : ISelector
                    {
                        private static int count;
                        public System.Type Read() { count++; return typeof(ReadOnly); }
                    }
                    public sealed class Second : ISelector
                    { public System.Type Read() { return typeof(Changing); } }
                    public static class ReadOnly { public static void Foo() { } }
                    public static class Changing { private static int state; public static void Foo() { state++; } }
                    public static class Calls
                    {
                        [NoLogTrack("cache")]
                        private static System.Type Query(ISelector selector) { return selector.Read(); }
                        public static void Entry(ISelector selector) { Query(selector).GetMethod("Foo").Invoke(null, null); }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 对应 KHScriptManager 的 EnsureBlockScriptFilter 回调；缺少注册时不能猜结果。
        /// <summary>剪掉无用目标查询，不得把仍影响结果的未知回调伪装为已完成。</summary>
        [TestMethod]
        public async Task UnregisteredHookRemainsUnresolved()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class KHScriptManager
                    {
                        public static System.Action EnsureBlockScriptFilter;
                        public static void Ensure() { EnsureBlockScriptFilter?.Invoke(); }
                    }
                }
                """);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4), deadline.Token);
            Assert.IsFalse(run.Complete);
            AnnotationMethod method = run.Annotations.Methods.Single();
            Assert.IsNull(method.Actual);
            Assert.IsNull(method.Decision);
            Assert.IsFalse(string.IsNullOrEmpty(method.Failure));
        }

        // 复现扩展函数转委托后省略首参，再用其返回类型调用反射成员的参数对应关系。
        /// <summary>绑定的首参属于实际调用参数，不能丢失或误作实例函数的 this。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task BoundExtensionDelegateRetainsItsFirstArgument(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Target { private static int state; public static void Foo() { state++; } }
                    public static class Calls
                    {
                        private static System.Type SameType(this System.Type type) { return type; }
                        public static void Entry()
                        {
                            System.Func<System.Type> getter = typeof(Target).SameType;
                            getter().GetMethod("Foo").Invoke(null, null);
                        }
                    }
                }
                """, external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 类型名包含未填入的程序集部分时，诊断必须定位到调用处，不能使整个程序崩溃。
        /// <summary>未能解析的类型名保留失败证据，独立函数的确定结果仍可输出。</summary>
        [TestMethod]
        public async Task InvalidRuntimeTypeNameProducesDiagnostic()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Calls
                    {
                        private static int state;
                        public static System.Type Read() { return System.Type.GetType("KH.Calls,"); }
                        public static void Write() { state++; }
                    }
                }
                """);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4), deadline.Token);
            Assert.IsFalse(run.Complete);
            Assert.IsNull(run.Annotations.Methods.Single(method => method.Name == "Read").Actual);
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single(method => method.Name == "Write").Actual);
        }

        // 对应游戏中的 Newtonsoft.Json 与改名副本：同名程序集不能覆盖编译器实际绑定的文件。
        /// <summary>同目录发现另一份同名库时，源码仍分析其明确引用的实现。</summary>
        [TestMethod]
        public async Task SourceKeepsItsActualLibraryReference()
        {
            using TestProject project = TestProject.Create("""
                namespace KH { public static class Calls { public static int Entry() { return Library.Api.Read(); } } }
                """, librarySource: """
                namespace Library { public static class Api { public static int Read() { return 1; } } }
                """);
            project.AddLibrary("OtherExternal.dll", "External", """
                namespace Library { public static class Api { private static int state; public static int Read() { return ++state; } } }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 对应源码继承外部库时，另一份同名 DLL 不能污染已绑定的基类。
        /// <summary>虚调用沿编译器实际选定的基类解析。</summary>
        [TestMethod]
        public async Task SourceInheritanceKeepsItsActualLibraryReference()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Child : Library.Base { }
                    public static class Calls { public static int Entry() => new Child().Read(); }
                }
                """, librarySource: """
                namespace Library { public class Base { public virtual int Read() => 1; } }
                """);
            project.AddLibrary("OtherExternal.dll", "External", """
                namespace Library { public class Base { private static int state; public virtual int Read() => ++state; } }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 下层未知返回经过泛型强转时，类型参数属于包装函数而非下层来源。
        /// <summary>源码和 IL 的泛型转换都保留正确的类型参数归属。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericConversionKeepsItsOwnTypeContext(bool external)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public sealed class Box<T> { }
                    public static class Calls
                    {
                        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
                        private static extern object Boundary(System.Type type);
                        private static object Lower<T>() => Boundary(typeof(T));
                        private static Box<T> Wrap<T>() => (Box<T>)Lower<T>();
                        public static object Entry(bool first)
                        {
                            var a = Wrap<int>();
                            var b = Wrap<string>();
                            return first ? a : b;
                        }
                    }
                }
                """, external);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            MaterialSet material = await new MaterialLoader().LoadAsync(project.Request(4), deadline.Token);
            MethodCatalogResult catalog = await new MethodCatalog().BuildAsync(material, 4, deadline.Token);
            MethodEntry root = catalog.GetMethods(catalog.Types.Single(type => type.FullName == "KH.Calls"))
                .Single(method => method.Name == "Entry");
            CallTargetResolutionResult result = await new CallTargetResolver().ResolveAsync(material, catalog, new[] { root }, 4,
                deadline.Token, requireCompleteCalls: true);
            var calls = result.Calls.Where(call => call.Call.Target.Name == "Wrap").ToArray();
            Assert.HasCount(2, calls);
            foreach (ResolvedCall call in calls)
            {
                string expected = call.Targets.Single().Reference.Identity.ReturnType.Text;
                var origins = result.ValueSources.GetOrigins(new(call.CallerMethodId, call.Call.ResultValueId!.Value));
                Assert.IsNotEmpty(origins);
                string argument = call.Targets.Single().Reference.GenericArgumentTypeIds.Single();
                Assert.IsTrue(origins.All(origin => origin.Value.Type is { } type
                    && type.DefinitionIdentity.Text.EndsWith(":KH.Box`1", StringComparison.Ordinal)
                    && type.ArgumentIdentities.Select(item => item.Text).SequenceEqual(new[] { argument })),
                    $"expected={expected}; target={call.Targets.Single().MethodId}; " + string.Join("; ", origins.Select(origin =>
                        $"{origin.Value.Type?.Id}, reference={origin.Reference.MethodId}, kind={origin.Value.Kind}")));
            }
        }

        // 同一泛型注册表的不同构造类型拥有独立静态字段，不能互相污染回调列表。
        /// <summary>直接注册和泛型辅助函数注册都只连接对应类型的回调。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GenericStaticRegistrationsKeepTheirOwnCallbacks(bool genericWriter)
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public sealed class ReadTag { }
                    public sealed class WriteTag { }
                    public static class Cache<T> { public static System.Action Callback; }
                    public static class Calls
                    {
                        private static int state;
                        public static void Pure() { }
                        public static void Write() { state++; }
                        public static void Save<T>(System.Action action) { Cache<T>.Callback = action; }
                        public static void Entry() { Cache<ReadTag>.Callback(); }
                    }
                }
                """, registrations: genericWriter ? """
                public static class Registration
                {
                    public static void Seed()
                    {
                        KH.Calls.Save<KH.ReadTag>(KH.Calls.Pure);
                        KH.Calls.Save<KH.WriteTag>(KH.Calls.Write);
                    }
                }
                """ : """
                public static class Registration
                {
                    public static void Seed()
                    {
                        KH.Cache<KH.ReadTag>.Callback = KH.Calls.Pure;
                        KH.Cache<KH.WriteTag>.Callback = KH.Calls.Write;
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Getter, result.Effects.Methods.Single().Kind);
        }

        // 包装函数的同一返回位置可携带两组迭代器实参，不能只留下第一组回调。
        /// <summary>迭代器经过包装返回后仍保留可能执行的 Setter。</summary>
        [TestMethod]
        public async Task WrappedIteratorKeepsDistinctArguments()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Calls
                    {
                        private static int state;
                        private static void Pure() { }
                        private static void Write() { state++; }
                        private static System.Collections.Generic.IEnumerable<System.Action> Items(System.Action action) { yield return action; }
                        private static System.Collections.Generic.IEnumerable<System.Action> Wrap(bool first)
                        { if (first) return Items(Pure); return Items(Write); }
                        public static void Entry(bool first) { foreach (var action in Wrap(first)) action(); }
                    }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind);
        }

        // 同会话输入未变时复用上下文，编辑文本后必须重新建立，不能拿旧结论回答。
        /// <summary>会话复用发生在源码上下文建立之前，改动仍使缓存失效。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SessionCompilationReusesOnlyUnchangedInputs(bool withConsumer)
        {
            using TestProject project = TestProject.Create("namespace KH { public static class Calls { public static int Read() => 1; } }",
                registrations: withConsumer ? "public static class Consumer { public static int Read() => KH.Calls.Read(); }" : null);
            MaterialLoader loader = new();
            MaterialRequest request = project.Request(4);
            MaterialSet first = await loader.LoadAsync(request);
            MaterialSet second = await loader.LoadAsync(request);
            foreach (var pair in first.SourceAssemblies.Zip(second.SourceAssemblies))
            {
                Assert.AreSame(pair.First.Compilation, pair.Second.Compilation);
                Assert.AreEqual(CompilationOrigin.Session, pair.Second.CompilationOrigin);
            }
            string path = first.SourceAssemblies.Single(assembly => assembly.Name == "khengine.runtime").SourcePaths.Single();
            MaterialSet changed = await loader.LoadAsync(request with
            {
                SourceTexts = new Dictionary<string, string> { [path] = "namespace KH { public static class Calls { public static int Read() => 2; } }" },
            });
            foreach (var pair in first.SourceAssemblies.Zip(changed.SourceAssemblies))
            {
                Assert.AreNotSame(pair.First.Compilation, pair.Second.Compilation);
                Assert.AreEqual(CompilationOrigin.Built, pair.Second.CompilationOrigin);
            }
        }

        // T28（R10）：外层写入全部经由显式 [LogTrack] 的内层函数完成时，外层裸 NLT 不报缺少 Reason；直接写入的裸 NLT 仍报。
        /// <summary></summary>
        [TestMethod]
        public async Task LoggedInnerSetterKeepsOuterNoLogTrack()
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    public sealed class LogTrackAttribute : System.Attribute { public LogTrackAttribute(long flag = 0) { } }
                    public class Model
                    {
                        private int m_x;
                        [LogTrack(1)] private void x_checksum_setter(int value) { m_x = value; }
                        [NoLogTrack] public void SetX(int value) { x_checksum_setter(value); }
                        [NoLogTrack] public void SetDirect(int value) { m_x = value; }
                    }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4));
            AnnotationMethod wrapped = run.Annotations.Methods.Single(method => method.Name == "SetX");
            AnnotationMethod direct = run.Annotations.Methods.Single(method => method.Name == "SetDirect");
            Assert.AreEqual(MethodEffectKind.Setter, wrapped.Actual);
            Assert.IsFalse(wrapped.MissingReason);
            Assert.AreEqual(MethodEffectKind.Setter, direct.Actual);
            Assert.IsTrue(direct.MissingReason);
        }

        // T27（R9）：带参数的空函数是日志打点，保持记录；无参数的空函数照常建议 NoLogTrack。
        /// <summary></summary>
        [TestMethod]
        public async Task EmptyTraceHookKeepsLogging()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Calls
                    {
                        public void LogPoint(int actorId) { }
                        public void Hook() { }
                    }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4));
            AnnotationMethod point = run.Annotations.Methods.Single(method => method.Name == "LogPoint");
            AnnotationMethod hook = run.Annotations.Methods.Single(method => method.Name == "Hook");
            Assert.AreEqual(MethodEffectKind.Getter, point.Actual);
            Assert.AreEqual("ShouldTrack", point.Decision);
            Assert.IsFalse(point.SuggestNoLogTrack);
            Assert.AreEqual("NoLogTrack", hook.Decision);
            Assert.IsTrue(hook.SuggestNoLogTrack);
        }

        // T31（R11）：空虚函数、抛异常的基类函数被重写成 Setter 时不建议 NoLogTrack；重写都不写时照常建议。
        /// <summary></summary>
        [TestMethod]
        public async Task SetterOverrideKeepsBaseLogging()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Base
                    {
                        public virtual void OnInit() { }
                        public virtual void SetValue() { throw new System.NotSupportedException(); }
                        public virtual void OnShow() { }
                    }
                    public class Middle : Base { public override void OnShow() { } }
                    public class Derived : Middle
                    {
                        private int m_value;
                        public override void OnInit() { m_value = 1; }
                        public override void SetValue() { m_value = 2; }
                    }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4));
            AnnotationMethod ReadBase(string name) => run.Annotations.Methods.Single(method => method.Class == "KH.Base" && method.Name == name);
            foreach (string name in new[] { "OnInit", "SetValue" })
            {
                Assert.AreEqual(MethodEffectKind.Getter, ReadBase(name).Actual, name);
                Assert.AreEqual("ShouldTrack", ReadBase(name).Decision, name);
                Assert.IsFalse(ReadBase(name).SuggestNoLogTrack, name);
            }
            Assert.IsTrue(ReadBase("OnShow").SuggestNoLogTrack);
        }

        // T32（R11）：khengine 自己不创建的外部子类重写同样要分析，写战斗状态时基类不建议 NoLogTrack。
        /// <summary></summary>
        [TestMethod]
        public async Task ExternalSetterOverrideKeepsBaseLogging()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public static class Store { public static int state; }
                    public class Base
                    {
                        public virtual void OnInit() { }
                        public virtual void OnShow() { }
                    }
                }
                """, registrations: """
                public class Client : KH.Base
                {
                    private int m_shown;
                    public override void OnInit() { KH.Store.state = 1; }
                    public override void OnShow() { m_shown = 1; }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4));
            AnnotationMethod init = run.Annotations.Methods.Single(method => method.Class == "KH.Base" && method.Name == "OnInit");
            AnnotationMethod show = run.Annotations.Methods.Single(method => method.Class == "KH.Base" && method.Name == "OnShow");
            Assert.AreEqual("ShouldTrack", init.Decision);
            Assert.IsFalse(init.SuggestNoLogTrack);
            Assert.IsTrue(show.SuggestNoLogTrack);
        }

        // T33：string 形参的运行时类型只能是 string，库函数不会经它回调源码类型的 ToString。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task SealedLibraryParameterDoesNotReachSourceOverrides(bool external) => AssertEntry("""
            public class Box { private static int s_state; public override string ToString() { s_state++; return "1"; } }
            public static class Calls { public static int Entry(object[] args) { return int.Parse((string)args[0]); } }
            """, MethodEffectKind.Getter, external);

        // T34：object 形参仍可能是任何源码对象，库函数经它调用的 ToString 写入不可漏掉。
        /// <summary></summary>
        [TestMethod]
        public Task ObjectLibraryParameterReachesSourceOverrides() => AssertEntry("""
            public class Box { private static int s_state; public override string ToString() { s_state++; return "1"; } }
            public static class Calls { public static string Entry() { return System.Convert.ToString((object)new Box()); } }
            """, MethodEffectKind.Setter);

        // T35：接口形参收窄到实现类型后，实现类从源码基类继承的重写仍是库函数可调用的回调。
        /// <summary></summary>
        [TestMethod]
        public Task InterfaceLibraryParameterKeepsInheritedOverrides() => AssertEntry("""
            public class Base { private static int s_state; public override string ToString() { s_state++; return "1"; } }
            public class Order : Base, System.Collections.IComparer { public int Compare(object x, object y) { return 0; } }
            public static class Calls { public static void Entry() { new System.Collections.ArrayList().Sort(new Order()); } }
            """, MethodEffectKind.Setter);

        // V2 摘要规则：Entry 的真实行为必须与预期一致；源码与 DLL 两种来源各跑一次。
        private static async Task AssertEntry(string body, MethodEffectKind expected, bool external = false)
        {
            using TestProject project = TestProject.Create("namespace KH\n{\n" + body + "\n}\n", external);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(expected, result.Effects.Methods.Single().Kind,
                JsonSerializer.Serialize(result.Effects.Methods.Single().Evidence) + JsonSerializer.Serialize(result.Effects.Failures));
        }

        // T1：类构造函数只写新对象自身字段，调用方不修改已有状态。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ClassConstructorOfNewObjectIsGetter(bool external) => AssertEntry("""
            public class Item { private int m_id; private object m_owner; public Item(int id, object owner) { m_id = id; m_owner = owner; } }
            public static class Calls { public static object Entry(object owner) { return new Item(3, owner); } }
            """, MethodEffectKind.Getter, external);

        // T2：构造函数写静态计数器（对应 Poolable），调用方为 Setter。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ConstructorWritingStaticIsSetter(bool external) => AssertEntry("""
            public class Item { private static int s_next; public readonly int Id; public Item() { Id = ++s_next; } }
            public static class Calls { public static object Entry() { return new Item(); } }
            """, MethodEffectKind.Setter, external);

        // T3：结构体构造返回新值。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task StructConstructorReturnIsGetter(bool external) => AssertEntry("""
            public struct Vec { public int X; public int Y; public Vec(int x, int y) { X = x; Y = y; } }
            public static class Calls { public static Vec Entry(int a) { return new Vec(a, a + 1); } }
            """, MethodEffectKind.Getter, external);

        // T4/T5：out 实参是局部变量时不修改已有状态；是字段时为 Setter（对应 toCoordKey 与 TryGetValue(k, out m_state)）。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public Task OutParameterTargetDecidesEffect(bool external, bool field) => AssertEntry($$"""
            public static class Calls
            {
                private static int s_a;
                private static void Split(int value, out int a, out int b) { a = value / 2; b = value - a; }
                public static int Entry(int value) { {{(field ? "Split(value, out s_a, out int b); return b;" : "Split(value, out int a, out int b); return a + b;")}} }
            }
            """, field ? MethodEffectKind.Setter : MethodEffectKind.Getter, external);

        // T6：foreach 遍历字段列表只读取。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ForeachOverFieldListIsGetter(bool external) => AssertEntry("""
            public class Calls
            {
                private readonly System.Collections.Generic.List<Calls> m_list = new System.Collections.Generic.List<Calls>();
                private int m_id;
                public bool Entry(int id) { foreach (Calls item in m_list) { if (item.m_id == id) { return true; } } return false; }
            }
            """, MethodEffectKind.Getter, external);

        // T6b：foreach 取出字段集合中的元素并修改它（对应 SwitchCaseArg.ClearData、TSDamageDetectVirgation.OnUpdate），必须为 Setter。
        /// <summary>T6b：经 foreach 元素修改已有对象为 Setter。</summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public Task ForeachElementMutationIsSetter(bool external, bool dictionary) => AssertEntry($$"""
            public class Task2 { public int Frames; public System.Collections.Generic.List<int> Items = new System.Collections.Generic.List<int>(); }
            public class Calls
            {
                private readonly System.Collections.Generic.List<Task2> m_list = new System.Collections.Generic.List<Task2>();
                private readonly System.Collections.Generic.Dictionary<int, Task2> m_dict = new System.Collections.Generic.Dictionary<int, Task2>();
                public void Entry() { {{(dictionary ? "foreach (var task in m_dict.Values) { task.Frames--; }" : "foreach (var item in m_list) { item.Items.Clear(); }")}} }
            }
            """, MethodEffectKind.Setter, external);

        // T7/T8：修改局部新建列表不算修改，修改字段列表为 Setter。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task CollectionReceiverDecidesEffect(bool field) => AssertEntry($$"""
            public class Calls
            {
                private readonly System.Collections.Generic.List<int> m_list = new System.Collections.Generic.List<int>();
                public int Entry()
                {
                    System.Collections.Generic.List<int> list = {{(field ? "m_list" : "new System.Collections.Generic.List<int>()")}};
                    list.Add(1);
                    list.RemoveAt(0);
                    return list.Count;
                }
            }
            """, field ? MethodEffectKind.Setter : MethodEffectKind.Getter);

        // T9：经返回值取得的已有列表被修改。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ReturnedFieldMutationIsSetter(bool external) => AssertEntry("""
            public class Calls
            {
                private readonly System.Collections.Generic.List<int> m_list = new System.Collections.Generic.List<int>();
                private System.Collections.Generic.List<int> GetList() { return m_list; }
                public void Entry() { GetList().Clear(); }
            }
            """, MethodEffectKind.Setter, external);

        // T10：新对象保存了已有列表的引用，经新对象修改必须追回原对象。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task AliasThroughNewObjectIsSetter(bool external) => AssertEntry("""
            public class Wrapper { public readonly System.Collections.Generic.List<int> Items; public Wrapper(System.Collections.Generic.List<int> items) { Items = items; } }
            public class Calls
            {
                private readonly System.Collections.Generic.List<int> m_list = new System.Collections.Generic.List<int>();
                public void Entry() { new Wrapper(m_list).Items.Add(1); }
            }
            """, MethodEffectKind.Setter, external);

        // T11：参数之间的链接关系必须保留：Link(fresh, m_b) 后经 fresh 修改 m_b。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task AliasThroughParameterLinkIsSetter(bool external) => AssertEntry("""
            public class Node { public Node Next; public int X; }
            public class Calls
            {
                private readonly Node m_b = new Node();
                private static void Link(Node a, Node b) { a.Next = b; }
                public void Entry() { Node fresh = new Node(); Link(fresh, m_b); fresh.Next.X = 1; }
            }
            """, MethodEffectKind.Setter, external);

        // T12：R2 三种懒加载写法都不算修改。
        /// <summary></summary>
        [TestMethod]
        [DataRow("if (s_instance == null) s_instance = new Calls(); return s_instance;")]
        [DataRow("if (null == s_instance) { s_instance = new Calls(); } return s_instance;")]
        [DataRow("return s_instance ??= new Calls();")]
        [DataRow("return s_instance ?? (s_instance = new Calls());")]
        public Task LazySingletonIsGetter(string body) => AssertEntry($$"""
            public class Calls { private static Calls s_instance; public static Calls Entry() { {{body}} } }
            """, MethodEffectKind.Getter);

        // T13/T14：懒加载分支里还有其他写入，或写的是其他对象的字段，仍为 Setter。
        /// <summary></summary>
        [TestMethod]
        [DataRow("if (s_list == null) { s_list = new System.Collections.Generic.List<int>(); s_count++; } return s_list;")]
        [DataRow("if (s_list == null) s_list = new System.Collections.Generic.List<int>(); else s_count++; return s_list;")]
        [DataRow("if (s_count > 0) s_list = new System.Collections.Generic.List<int>(); return s_list;")]
        [DataRow("if (s_other.s_list2 == null) s_other.s_list2 = new System.Collections.Generic.List<int>(); return s_list;")]
        public Task LazyInitBoundaryIsSetter(string body) => AssertEntry($$"""
            public class Calls
            {
                private static System.Collections.Generic.List<int> s_list;
                private static int s_count;
                private static Calls s_other = new Calls();
                private System.Collections.Generic.List<int> s_list2;
                public static System.Collections.Generic.List<int> Entry() { {{body}} }
            }
            """, MethodEffectKind.Setter);

        // T30：客户端实现经自己的字典取出 khengine 已有对象再修改其战斗字段，调用方为 Setter（对应 LTCTempProxy.TriggerProxyHelperExt_RemovePoolItemUseChannels）。
        /// <summary></summary>
        [TestMethod]
        public async Task CombatWriteThroughClientLookupIsSetter()
        {
            using TestProject project = TestProject.Create("""
                namespace KH
                {
                    public class Item
                    {
                        public System.Collections.Generic.List<int> channels = new System.Collections.Generic.List<int>();
                        public long sid;
                        public void Clear() { channels.Clear(); }
                    }
                    public class Pool
                    {
                        private System.Collections.Generic.List<Item> m_items = new System.Collections.Generic.List<Item>();
                        public Item Get(long sid) { for (int i = 0; i < m_items.Count; i++) { if (m_items[i].sid == sid) { return m_items[i]; } } return null; }
                    }
                    public interface IProxy { void Remove(long pool, long item); }
                    public static class Calls
                    {
                        private static IProxy ms_impl;
                        public static void SetImpl(IProxy impl) { ms_impl = impl; }
                        public static void Entry() { if (ms_impl != null) { ms_impl.Remove(1, 2); } }
                    }
                }
                """, registrations: """
                public class Helper : KH.IProxy
                {
                    private System.Collections.Generic.Dictionary<long, KH.Pool> m_pools = new System.Collections.Generic.Dictionary<long, KH.Pool>();
                    private KH.Pool GetPool(long id) { KH.Pool pool; if (m_pools.TryGetValue(id, out pool)) { return pool; } return null; }
                    public void Remove(long pool, long item)
                    {
                        KH.Pool found = GetPool(pool);
                        if (found != null) { KH.Item target = found.Get(item); if (target != null) { target.Clear(); } }
                    }
                    public static void Init() { KH.Calls.SetImpl(new Helper()); }
                }
                """);
            var result = await project.AnalyzeAsync("KH.Calls", "Entry");
            Assert.AreEqual(MethodEffectKind.Setter, result.Effects.Methods.Single().Kind,
                JsonSerializer.Serialize(result.Effects.Methods.Single().Evidence) + JsonSerializer.Serialize(result.Effects.Failures));
        }

        // T29（R2 放宽）：懒加载分支中新建后只填充这个新对象不算修改；分支内把 F 改指向已有对象再修改，仍为 Setter。
        /// <summary></summary>
        [TestMethod]
        [DataRow("if (s_list == null) { s_list = new System.Collections.Generic.List<int>(); for (int i = 0; i < 3; i++) { s_list.Add(0); } }", false)]
        [DataRow("if (s_list == null) { s_list = new System.Collections.Generic.List<int>(); s_list = s_shared; s_list.Add(0); }", true)]
        public Task LazyFillOnlyTouchesNewObject(string body, bool setter) => AssertEntry($$"""
            public class Calls
            {
                private static System.Collections.Generic.List<int> s_list;
                private static System.Collections.Generic.List<int> s_shared = new System.Collections.Generic.List<int>();
                public static System.Collections.Generic.List<int> Entry() { {{body}} return s_list; }
            }
            """, setter ? MethodEffectKind.Setter : MethodEffectKind.Getter);

        // T15：与诊断类型同名但程序集不同的类型仍按普通代码分析。
        /// <summary></summary>
        [TestMethod]
        public Task DiagnosticsRequiresExactAssembly() => AssertEntry("""
            public static class Debuger { private static int s_count; public static void Log(string text) { s_count++; } }
            public static class Calls { public static void Entry() { Debuger.Log("x"); } }
            """, MethodEffectKind.Setter);

        // T23/T24：新建对象后注册到已有列表或写入参对象，均为 Setter（对应 DoCreatePlayerActor、CreatAI）。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public Task CreateWithSideEffectIsSetter(bool external, bool parameter) => AssertEntry($$"""
            public class Actor { public int BtId; }
            public class Calls
            {
                private readonly System.Collections.Generic.List<Actor> m_items = new System.Collections.Generic.List<Actor>();
                public Actor Entry(Actor owner) { Actor created = new Actor(); {{(parameter ? "owner.BtId = 1;" : "m_items.Add(created);")}} return created; }
            }
            """, MethodEffectKind.Setter, external);

        // T25：从对象池取出不是新对象，取出动作修改了池。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task PoolTakeIsSetter(bool external) => AssertEntry("""
            public class Calls
            {
                private readonly System.Collections.Generic.List<object> m_pool = new System.Collections.Generic.List<object>();
                public object Entry() { int last = m_pool.Count - 1; object item = m_pool[last]; m_pool.RemoveAt(last); return item; }
            }
            """, MethodEffectKind.Setter, external);

        // T26：编号递增（对应 GetSid）为 Setter。
        /// <summary></summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task IdAllocationIsSetter(bool external) => AssertEntry("""
            public static class Calls { private static long s_next = 100000; public static long Entry() { return s_next++; } }
            """, MethodEffectKind.Setter, external);

        // 对应可信 NLT 缓存函数；用同一写法核对裸标签、原因标签和类标签。
        /// <summary>豁免只隔断日志影响，不能抹掉真实 Setter。</summary>
        [TestMethod]
        [DataRow("method")]
        [DataRow("reason")]
        [DataRow("class")]
        public async Task CacheExemptionSeparatesBehaviorAndLogging(string label)
        {
            using TestProject project = TestProject.Create(Attributes + """
                namespace KH
                {
                    CLASS
                    public static class Cache
                    {
                        private static int value;
                        METHOD
                        public static int Read()
                        {
                            if (value == 0) { value = 1; }
                            return value;
                        }
                    }
                    public static class Calls { public static int Entry() { return Cache.Read(); } }
                }
                """.Replace("CLASS", label == "class" ? "[NoLogTrack]" : "")
                .Replace("METHOD", label == "class" ? "" : label == "reason" ? "[NoLogTrack(\"cache\")]" : "[NoLogTrack]"));
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(project.Request(4), deadline.Token);
            Assert.IsTrue(run.Complete, run.Failure);
            AnnotationMethod entry = run.Annotations.Methods.Single(method => method.Name == "Entry");
            AnnotationMethod cache = run.Annotations.Methods.Single(method => method.Name == "Read");
            Assert.AreEqual(MethodEffectKind.Setter, entry.Actual);
            // 规则 R1：日志决定只看真实行为，可信 NLT 内部的写入照常使上层需要追踪。
            Assert.AreEqual("ShouldTrack", entry.Decision);
            Assert.AreEqual(label == "class" ? "NLTClass" : "NoLogTrack", cache.Decision);
            Assert.AreEqual(label == "method", cache.MissingReason);
        }
    }
}
