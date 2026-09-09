using System.Text.Json;

namespace SetterChecker.Core.Tests
{
    /// <summary>通过整个流水线验证报告、预览和明确失败。</summary>
    [TestClass]
    public sealed class ReportWriterTests
    {
        // 未读取条件函数时，进度报告不能先把稍后会被排除的写入报成确定 Setter。
        /// <summary>写入路径与接收对象来源都必须等待条件真实结果。</summary>
        [TestMethod]
        [DataRow(false, "int")]
        [DataRow(true, "int")]
        [DataRow(false, "long")]
        [DataRow(true, "long")]
        public async Task RunNeverPublishesSetterFromAnUnresolvedFalseCondition(bool conditionalReceiver, string numberType)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public sealed class Data { public int Value; }
                public static class Calls
                {
                    private static int state;
                    private static NUMBER Flag() => 0;
                    public static void Entry(Data external) { BODY }
                }
                """.Replace("NUMBER", numberType).Replace("BODY", conditionalReceiver ? "var target = Flag() != 0 ? external : new Data(); target.Value = 1;" : "if (Flag() != 0) state = 1;"));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 条件来自接口返回时，随后委托不能保留已经被排除的修改目标。
        /// <summary>同轮发现条件与委托后，最终和中途结果都不应误报静态写入。</summary>
        [TestMethod]
        public async Task RunExcludesDelegateTargetSelectedByFalseInterfaceResult()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                using System;
                public interface IFlag { bool Read(); }
                public sealed class AlwaysFalse : IFlag { public bool Read() => false; }
                public static class Calls
                {
                    private static int state;
                    private static void Set() { state = 1; }
                    private static void Get() { }
                    public static void Entry()
                    {
                        IFlag flag = new AlwaysFalse();
                        Action action = flag.Read() ? new Action(Set) : new Action(Get);
                        action();
                    }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 第二个条件的接收对象也可能由第一个条件选择，必须按依赖先后确定目标。
        /// <summary>两个条件生产者不能因都在等待集合中就同时固定各自目标。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunOrdersDependentInterfaceConditions(bool useHelper)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public interface IFlag { bool Read(); }
                public sealed class FalseFlag : IFlag { public bool Read() => false; }
                public sealed class WritingFalseFlag : IFlag { public bool Read() { Calls.State = 1; return false; } }
                public static class Calls
                {
                    public static int State;
                    private static IFlag Choose(IFlag first) => first.Read() ? new WritingFalseFlag() : new FalseFlag();
                    public static void Entry()
                    {
                        IFlag first = new FalseFlag();
                        IFlag second = SELECT;
                        if (second.Read()) State = 1;
                    }
                }
                """.Replace("SELECT", useHelper ? "Choose(first)" : "first.Read() ? new WritingFalseFlag() : new FalseFlag()"));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 最后一次反射绑定也要触发路径更新。
        /// <summary>最后一个反射调用只补充值事实时，也必须据此排除不可达写入。</summary>
        [TestMethod]
        public async Task RunRefinesAfterLastReflectionBinding()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public sealed class Box { public int Flag; } public static class Calls { private static int state; public static void Entry() { var box = new Box(); int value = (int)typeof(Box).GetField(\"Flag\").GetValue(box); if (value != 0) state = 1; } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 装箱后的零是非空对象，不能当成数值零排除真实写入。
        /// <summary>保留类型转换与整数宽度，按运行时算术结果决定写入路径。</summary>
        [TestMethod]
        [DataRow("object value = 0; if (value != null) state = 1;", true)]
        [DataRow("int value = Zero(); if (value + 1 <= 0) state = 1;", false)]
        [DataRow("int value = Zero(); if (unchecked(value + int.MaxValue + 1) >= 0) state = 1;", false)]
        [DataRow("long value = Zero(); if (value * 4 - 1 >= 0) state = 1;", false)]
        [DataRow("int[] value = new int[Zero()]; if (value.Length != 0) state = 1;", false)]
        [DataRow("int value = 1; if (Zero() == 0) value = 0; if (value != 0) state = 1;", false)]
        [DataRow("int count = state; var current = new Holder(); current.Next = current; while (count-- > 0) current = current.Next; if (current.Flag != 0) state = 1;", false)]
        public async Task RunPreservesNumericWidthAndBoxedReferences(string body, bool setter)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public sealed class Holder { public Holder Next; public int Flag; } public static class Calls { private static int state; private static int Zero() => 0; public static void Entry() { " + body + " } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
            {
                if (!setter)
                {
                    Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
                }
            });
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(setter ? MethodEffectKind.Setter : MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 确定不返回的调用阻断后续普通执行路径。
        /// <summary>没有正常返回出口的辅助调用之后，不应继续传播修改。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunExcludesWritesAfterNonReturningCall(bool nested)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public static class Calls { private static int state; private static void Stop() { throw null; } private static void Gate(bool flag) { if (flag) Stop(); } public static void Entry() { " + (nested ? "Gate(true);" : "Stop();") + " state = 1; } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 区分循环内的引用变量与它指向的数据。
        /// <summary>循环内写引用所指数据不等于改写引用变量本身。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunClosesManagedReferenceLoop(bool valueType)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource(valueType
                ? "public struct Data { public int Value; public void Entry(int count) { while (count-- > 0) this = default; } }"
                : "public static class Calls { public static void Entry(ref int output, int count) { ref int slot = ref output; while (count-- > 0) slot++; } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsTrue(run.Complete, run.Failure + string.Join(Environment.NewLine, run.Annotations.Methods));
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 原生指针的循环别名缺少证明时明确失败，不套用托管引用规则。
        /// <summary>未实现的原生指针别名循环保留失败，不递归到进程栈溢出。</summary>
        [TestMethod]
        public async Task RunReportsNativePointerAliasCycle()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public static class Calls { public static unsafe void Entry(int* pointer, int count) { while (count-- > 0) (*pointer)++; } }");
            File.AppendAllLines(project.RootResponsePath, new[] { "-unsafe+" }, new System.Text.UTF8Encoding(false));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsFalse(run.Complete);
            Assert.Contains("地址来源循环尚未闭合", run.Annotations.Methods.Single(method => method.Name == "Entry").Failure!);
            Assert.IsNull(run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 后续指针条件缺少证明，不应抹掉进入该循环前已经发生的静态修改。
        /// <summary>独立写入证明按真实先后顺序保留，条件之后的写入仍需闭合条件。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunKeepsWritesBeforeUnclosedPointerCondition(bool writeFirst)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public static class Calls { private static int state; public static unsafe void Entry(int* pointer, int count) { "
                + (writeFirst ? "state = 1; " : string.Empty) + "while (count-- > 0) (*pointer)++; "
                + (writeFirst ? string.Empty : "state = 1;") + " } }");
            File.AppendAllLines(project.RootResponsePath, new[] { "-unsafe+" }, new System.Text.UTF8Encoding(false));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.AreEqual(writeFirst ? MethodEffectKind.Setter : (MethodEffectKind?)null,
                run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 空委托和空接口接收对象都无法正常进入后续写入。
        /// <summary>空接收对象不能借空目标集合伪造正常返回。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunExcludesWritesAfterNullCall(bool useDelegate)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("using System; public interface IWork { void Run(); } public static class Calls { private static int state; public static void Entry() { "
                + (useDelegate ? "Action call = null; call();" : "IWork call = null; call.Run();") + " state = 1; } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 不借临时对象所在路径，为另一条不会正常到达的外部对象路径发布修改证据。
        /// <summary>正常执行路径和写入对象来源必须相容。</summary>
        [TestMethod]
        public async Task RunKeepsReceiverAndContinuingPathTogether()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("public sealed class Data { public int Value; } public interface IStop { void Run(); } public sealed class Stop : IStop { public void Run() { throw null; } } public static class Calls { public static void Entry(Data external, bool flag) { IStop stop = new Stop(); Data target = external; if (flag) stop.Run(); else target = new Data(); target.Value = 1; } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 返回证据必须来自同一条正常传出对象的路径。
        /// <summary>创建过对象不代表已正常传出，未知调用不能被返回槽或 finally 绕过。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunDoesNotBorrowAnotherReturnPath(bool useFinally)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("using System.Runtime.CompilerServices; public sealed class Data { } public static class Calls { [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(); public static Data Entry(bool flag) { "
                + (useFinally ? "try { return new Data(); } finally { Unknown(); }" : "var value = new Data(); if (flag) { Unknown(); return value; } return null;") + " } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsFalse(run.Complete);
            Assert.IsNull(run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 辅助函数的另一个返回出口不能替未知调用后的对象来源作证。
        /// <summary>调用者返回或修改辅助函数结果时，保留尚未证明的返回支路。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunDoesNotBorrowHelperReturnPath(bool write)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("using System.Runtime.CompilerServices; public sealed class Data { public int Value; } public static class Calls { [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(); "
                + (write
                    ? "private static Data Pick(Data external, bool flag) { if (flag) { Unknown(); return external; } return new Data(); } public static void Entry(Data external, bool flag) { Pick(external, flag).Value = 1; }"
                    : "private static Data Pick(bool flag) { var value = new Data(); if (flag) { Unknown(); return value; } return null; } public static Data Entry(bool flag) { return Pick(flag); }") + " }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsFalse(run.Complete);
            Assert.IsNull(run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 接口通过对象字段写回条件时，应先读取该调用而非等待它并不存在的返回值。
        /// <summary>返回 void 的条件生产者也进入同一调用推进流程。</summary>
        [TestMethod]
        public async Task RunResolvesVoidInterfaceWritingCondition()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public class Box { public int Flag; }
                public interface IReset { void Run(Box box); }
                public sealed class Reset : IReset { public void Run(Box box) { box.Flag = 0; } }
                public static class Calls
                {
                    private static int state;
                    public static void Entry()
                    {
                        var box = new Box(); IReset reset = new Reset();
                        reset.Run(box);
                        if (box.Flag != 0) state = 1;
                    }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
                Assert.AreNotEqual(MethodEffectKind.Setter, current.Annotations.Methods.Single(method => method.Name == "Entry").Actual));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 递归实参改变分支结果时，不能复用上一层被排除的写入路径。
        /// <summary>先不写、随后递归改为写入的参数变化必须保留。</summary>
        [TestMethod]
        public async Task RunKeepsChangedRecursiveBranchInput()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public class Data { public int Value; }
                public static class Calls
                {
                    public static void Entry(Data external) => Repeat(external, false);
                    private static void Repeat(Data target, bool flag)
                    {
                        if (flag) target.Value = 1;
                        else Repeat(target, true);
                    }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsTrue(run.Complete);
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 找到原生声明不等于知道返回值，未知条件不得被写入本身掩盖。
        /// <summary>条件尚未证明时保留缺口，不生成任何确定日志决定。</summary>
        [TestMethod]
        public async Task RunKeepsNativeBranchConditionUnproved()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public static class Calls
                {
                    private static int state;
                    [System.Runtime.InteropServices.DllImport("missing_native")] private static extern bool Flag();
                    public static void Entry() { if (Flag()) state = 1; }
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.IsFalse(run.Complete);
            Assert.IsNull(run.Annotations.Methods.Single(method => method.Name == "Entry").Actual);
        }

        // 相同源码调用在不同上层入口重复出现时，只展示一份问题并保留次数。
        /// <summary>合并完全相同的待处理说明，不丢失待处理调用总数。</summary>
        [TestMethod]
        public async Task ReportGroupsRepeatedPendingCallsWithoutLosingCounts()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public class Calls
                {
                    [System.Runtime.InteropServices.DllImport("missing_native")] private static extern System.Action Native();
                    public void First() => Helper();
                    public void Second() => Helper();
                    private void Helper() => Native()();
                }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string directory = Path.Combine(project.RootPath, "reports");
            new ReportWriter().Write(run, directory);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
            JsonElement[] pending = json.RootElement.GetProperty("Pending").EnumerateArray().ToArray();
            Assert.IsTrue(pending.Any(item => item.GetProperty("Count").GetInt32() > 1));
            Assert.AreEqual(run.Calls!.PendingCalls.Count, pending.Sum(item => item.GetProperty("Count").GetInt32()));
        }

        // 运行中的报告只发布已证明事实，随后正常生成最终报告。
        /// <summary>进度快照不能冒充结束，未证明函数不得进入补丁。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunPublishesProgressWithoutChangingFinalOutcome(bool nativeBoundary)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                public class Calls
                {
                    private int state;
                    public int Read() => 1;
                    public void Write() { state = 1; Helper(); }
                    private void Helper() { }
                    BOUNDARY
                }
                """.Replace("BOUNDARY", nativeBoundary ? "[System.Runtime.InteropServices.DllImport(\"missing_native\")] private static extern void Native(); public void Unknown() => Native();" : ""));
            string directory = Path.Combine(project.RootPath, "reports");
            int snapshots = 0;
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2), reportProgress: current =>
            {
                snapshots++;
                Assert.IsTrue(current.IsInProgress);
                Assert.IsFalse(current.Complete);
                new ReportWriter().Write(current, directory);
                Assert.StartsWith("# 分析进行中", File.ReadAllText(Path.Combine(directory, "report.md")));
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
                Assert.IsTrue(json.RootElement.GetProperty("IsInProgress").GetBoolean());
                Assert.IsFalse(json.RootElement.GetProperty("Complete").GetBoolean());
                Assert.DoesNotContain("[global::KH.NoLogTrack] public void Unknown", File.ReadAllText(Path.Combine(directory, "preview.patch")));
            });
            Assert.IsGreaterThan(0, snapshots);
            Assert.IsFalse(run.IsInProgress);
            Assert.AreEqual(!nativeBoundary, run.Complete);
            new ReportWriter().Write(run, directory);
            Assert.DoesNotContain("# 分析进行中", File.ReadAllText(Path.Combine(directory, "report.md")));
            using JsonDocument final = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
            Assert.IsFalse(final.RootElement.GetProperty("IsInProgress").GetBoolean());
        }

        // 独立 Setter 证明不能让未读取的依赖函数体被误算为整轮审计完成。
        /// <summary>统计外 DLL 的失败单列输出，不扩大函数统计或抹掉根的确定写入。</summary>
        [TestMethod]
        public async Task RunKeepsSetterButFailsCompletionForUnreadDependency()
        {
            using TestProject project = TestProject.CreateWithCallTargets("namespace Samples; public static class Library { public static int Bad() => 0; }");
            using (Mono.Cecil.AssemblyDefinition assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(project.ExternalAssemblyPath,
                       new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                Mono.Cecil.Cil.ILProcessor il = assembly.MainModule.Types.Single(type => type.Name == "Library").Methods.Single(method => method.Name == "Bad").Body.GetILProcessor();
                il.Body.Instructions.Clear();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                il.Emit(Mono.Cecil.Cil.OpCodes.Localloc);
                il.Emit(Mono.Cecil.Cil.OpCodes.Pop);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                assembly.Write(project.ExternalAssemblyPath);
            }
            project.WriteRootSource("public class Calls { private static int state; public void Write() { state = 1; ExternalSamples.Library.Bad(); } }");
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            Assert.HasCount(1, run.Annotations.Methods);
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single().Actual);
            Assert.IsFalse(run.Complete);
            string directory = Path.Combine(project.RootPath, "reports");
            new ReportWriter().Write(run, directory);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
            Assert.IsFalse(json.RootElement.GetProperty("Complete").GetBoolean());
            Assert.AreEqual(1, json.RootElement.GetProperty("UnreadBodies").GetArrayLength());
            Assert.Contains("localloc", json.RootElement.GetProperty("UnreadBodies")[0].GetProperty("Failure").GetString()!);
        }

        // String.Replace 的 localloc 读取失败不能抹去同批其它根的独立证明。
        /// <summary>逐方法保存原始失败，调用方仍为未证明，独立 Getter/Setter 正常进入报告。</summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(4)]
        public async Task RunKeepsIndependentMethodsWhenOneBodyCannotBeRead(int jobs)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                public class Calls
                {
                    private static int state;
                    public int Read() => 1;
                    public void Write() { state++; }
                    public int Trouble(int size) => Unsupported(size);
                    private unsafe int Unsupported(int size) { byte* data = stackalloc byte[size]; return data[0]; }
                }
                """);
            File.AppendAllLines(project.RootResponsePath, new[] { "-unsafe+" }, new System.Text.UTF8Encoding(false));
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
            Assert.IsFalse(run.Complete);
            Assert.AreEqual(MethodEffectKind.Getter, run.Annotations.Methods.Single(method => method.Name == "Read").Actual);
            Assert.AreEqual(MethodEffectKind.Setter, run.Annotations.Methods.Single(method => method.Name == "Write").Actual);
            foreach (AnnotationMethod failed in run.Annotations.Methods.Where(method => method.Name is "Trouble" or "Unsupported"))
            {
                Assert.IsNull(failed.Actual);
                Assert.Contains("localloc", failed.Failure!);
                Assert.IsNotNull(failed.Evidence);
            }
        }

        // 实际应用补丁后只允许增加标签，不得顺带改变任何一种原始换行。
        /// <summary>LF、CRLF、混合换行、CR 和无末尾换行均保持源码字节内容。</summary>
        [TestMethod]
        [DataRow("\n", true)]
        [DataRow("\r\n", true)]
        [DataRow("\r", true)]
        [DataRow("\n", false)]
        [DataRow("mixed", true)]
        public async Task PreviewAppliesWithoutChangingSourceNewlines(string newline, bool trailing)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            string[] lines = { "namespace KH { public class NoLogTrackAttribute : System.Attribute { } }",
                "public class Sample", "{", "    public int Read() => 1;", "}" };
            string source = string.Concat(lines.Select((line, index) => line + (index == lines.Length - 1 && !trailing ? ""
                : newline == "mixed" ? index % 2 == 0 ? "\n" : "\r\n" : newline)));
            project.WriteRootSource(source);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string directory = Path.Combine(project.RootPath, "reports");
            new ReportWriter().Write(run, directory);
            System.Diagnostics.ProcessStartInfo start = new("git")
            {
                WorkingDirectory = project.RootPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "-c", "core.autocrlf=false", "apply", "--whitespace=nowarn", Path.Combine(directory, "preview.patch") })
            {
                start.ArgumentList.Add(argument);
            }
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, error);
            Assert.AreEqual(source.Replace("public int Read", "[global::KH.NoLogTrack] public int Read"), File.ReadAllText(project.RootSourcePath));
        }

        // 不参与标签统计的真实 Tss 写法，类级豁免仍须独立审计。
        /// <summary>排除统计不等于漏掉可信豁免复查，且不为排除项输出补丁。</summary>
        [TestMethod]
        public async Task RunAuditsExcludedClassWithoutChangingReportScope()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                namespace Tss
                {
                    [KH.NoLogTrack] public class Manager
                    {
                        private int value;
                        public void Initialize() { value = 1; }
                        public int Value { get => value; set { this.value = value; } }
                    }
                }
                namespace Samples { public class Calls { public int Read() => 1; } }
                """);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            AnnotationMethod audit = run.Annotations.Methods.Single(method => method.Name == "Initialize");
            Assert.IsFalse(audit.IsReportable);
            Assert.IsTrue(audit.ReviewExemption);
            string directory = Path.Combine(project.RootPath, "reports");
            new ReportWriter().Write(run, directory);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
            Assert.AreEqual(1, json.RootElement.GetProperty("Total").GetInt32());
            Assert.Contains("Initialize", File.ReadAllText(Path.Combine(directory, "report.md")));
            Assert.DoesNotContain("[global::KH.NoLogTrack] public void Initialize", File.ReadAllText(Path.Combine(directory, "preview.patch")));
        }

        // Setter 提前证明以后继续完成可解析调用，不能把正常 helper 留成待处理项。
        /// <summary>同图恢复完成审计，j1/j2/j4/j8 的结论、告警与补丁完全一致。</summary>
        [TestMethod]
        public async Task RunCompletesKnownCallsAndKeepsJobResultsStable()
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH { public class NoLogTrackAttribute : System.Attribute { } }
                namespace Samples
                {
                    public class Calls
                    {
                        private int value;
                        public void Entry() { value = 1; Helper(); }
                        private void Helper() { }
                        public int Read() => value;
                    }
                }
                """);
            string? expected = null;
            foreach (int jobs in new[] { 1, 2, 4, 8 })
            {
                AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, jobs));
                Assert.IsTrue(run.Complete);
                string result = JsonSerializer.Serialize(run.Annotations.Methods);
                expected ??= result;
                Assert.AreEqual(expected, result);
            }
        }

        // 成功和含原生边界的项目都必须得到可核对的报告，不修改原始源码。
        /// <summary>全部模块接通，未知不算作 Getter，补丁只来自已证明函数。</summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RunWritesConsistentReportsAndReadOnlyPreview(bool nativeBoundary)
        {
            using TestProject project = TestProject.CreateSingleAssembly();
            project.WriteRootSource("""
                namespace KH;
                public class NoLogTrackAttribute : System.Attribute { }
                public class Sample
                {
                    private int value;
                    public int Read() => value;
                    public void Write() { value = 1; }
                    BOUNDARY
                }
                """.Replace("BOUNDARY", nativeBoundary ? "[System.Runtime.InteropServices.DllImport(\"missing_native\")] private static extern void Native(); public void Unknown() => Native();" : ""));
            string source = File.ReadAllText(project.RootSourcePath);
            AnalysisRun run = await new SetterChecker().AnalyzeAsync(new MaterialRequest(project.AssemblyDefinitionPath, 2));
            string directory = Path.Combine(project.RootPath, "reports");
            new ReportWriter().Write(run, directory);
            Assert.AreEqual(!nativeBoundary, run.Complete);
            Assert.AreEqual(nativeBoundary ? 3 : 2, run.Annotations.Methods.Count);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
            Assert.AreEqual(run.Complete, json.RootElement.GetProperty("Complete").GetBoolean());
            Assert.AreEqual(nativeBoundary ? 1 : 0, json.RootElement.GetProperty("Unproved").GetInt32());
            Assert.AreEqual(1, json.RootElement.GetProperty("DetectedNoLogTrack").GetInt32());
            Assert.AreEqual(1, json.RootElement.GetProperty("DetectedShouldTrack").GetInt32());
            Assert.Contains("Read", File.ReadAllText(Path.Combine(directory, "report.md")));
            string patch = File.ReadAllText(Path.Combine(directory, "preview.patch"));
            Assert.Contains("[global::KH.NoLogTrack] public int Read()", patch);
            Assert.DoesNotContain("[global::KH.NoLogTrack] public void Unknown()", patch);
            Assert.AreEqual(source, File.ReadAllText(project.RootSourcePath));
            foreach (string file in Directory.GetFiles(directory))
            {
                CollectionAssert.AreNotEqual(new byte[] { 239, 187, 191 }, File.ReadAllBytes(file).Take(3).ToArray());
            }
        }
    }
}
