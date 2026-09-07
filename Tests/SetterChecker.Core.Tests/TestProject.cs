using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SetterChecker.Core.Tests
{
    /// <summary>
    /// 建立与 khengine 编译参数结构一致的最小 Unity 工程。
    /// </summary>
    internal sealed class TestProject : IDisposable
    {
        // 保存测试工程路径，供测试直接断言公开结果。
        private TestProject(
            string rootPath,
            string assemblyDefinitionPath,
            string rootSourcePath,
            string rootResponsePath,
            string externalAssemblyPath,
            string externalReferencePath,
            string forwardTargetPath,
            string analyzerPath)
        {
            this.RootPath = rootPath;
            this.AssemblyDefinitionPath = assemblyDefinitionPath;
            this.RootSourcePath = rootSourcePath;
            this.RootResponsePath = rootResponsePath;
            this.ExternalAssemblyPath = externalAssemblyPath;
            this.ExternalReferencePath = externalReferencePath;
            this.ForwardTargetPath = forwardTargetPath;
            this.AnalyzerPath = analyzerPath;
        }

        public string RootPath { get; }

        public string AssemblyDefinitionPath { get; }

        public string RootSourcePath { get; }

        public string RootResponsePath { get; }

        public string ExternalAssemblyPath { get; }

        public string ExternalReferencePath { get; }

        public string ForwardTargetPath { get; }

        public string AnalyzerPath { get; }

        // 建立包含一个源码依赖和一个外部文件的最小工程。
        public static TestProject Create()
        {
            return Create(includeDependency: true);
        }

        // 建立只含根程序集的最小工程。
        public static TestProject CreateSingleAssembly()
        {
            return Create(includeDependency: false);
        }

        // 建立带真实源码生成器的最小工程。
        public static TestProject CreateWithGenerator()
        {
            TestProject project = Create(includeDependency: false);
            string additionalFilePath = Path.Combine(project.RootPath, "GeneratorInput.txt");

            File.Delete(project.AnalyzerPath);
            WriteGenerator(project.AnalyzerPath);
            File.WriteAllText(
                additionalFilePath,
                "public sealed class GeneratedFromAdditionalFile { }");
            File.AppendAllLines(
                project.RootResponsePath,
                new[] { $"/additionalfile:\"{Relative(project.RootPath, additionalFilePath)}\"" });

            return project;
        }

        // 建立文件名正确但程序集身份错误的源码依赖参考文件。
        public static TestProject CreateWithMismatchedSourceReference()
        {
            TestProject project = Create(includeDependency: true);
            string referencePath = Path.Combine(
                project.RootPath,
                "Library",
                "Bee",
                "artifacts",
                "build",
                "Dependency.ref.dll");

            File.Delete(referencePath);
            WriteAssembly(referencePath, "WrongDependency", "public sealed class WrongType { }");

            return project;
        }

        // 建立包含可读取函数的真实外部程序集。
        /// <summary>
        /// 建立覆盖托管函数、类型关系和元数据边界的外部程序集。
        /// </summary>
        public static TestProject CreateWithExternalMethods()
        {
            TestProject project = Create(includeDependency: false);

            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                """
                public interface IExternal<T>
                {
                    // 声明外部接口函数。
                    void Apply(T value);
                }

                public class ExternalBase : IExternal<int>
                {
                    // 声明普通外部函数。
                    public virtual void Change(int value) { }
                    // 实现外部接口函数。
                    public virtual void Apply(int value) { }
                }

                public sealed class ExternalType : ExternalBase
                {
                    // 重写普通外部函数。
                    public override void Change(int value) { }
                    // 重写外部接口函数。
                    public override void Apply(int value) { }
                    // 声明名称类似访问器但实际是普通函数的反例。
                    public void get_Fake() { }
                }

                public sealed class ExplicitExternal : IExternal<int>
                {
                    // 显式实现构造泛型接口函数。
                    void IExternal<int>.Apply(int value) { }
                }

                public class NewSlotExternal : ExternalBase
                {
                    // 隐藏基类函数但不重新实现继承得到的接口。
                    public new virtual void Apply(int value) { }
                }

                public sealed class ReimplementedExternal : ExternalBase, IExternal<int>
                {
                    // 当前类重新声明接口后重新实现函数。
                    public new void Apply(int value) { }
                }

                public sealed class Outer<T>
                {
                    public sealed class Inner<TValue> : IExternal<TValue>
                    {
                        // 实现使用嵌套泛型参数编号的接口函数。
                        public void Apply(TValue value) { }
                        // 返回使用两层开放泛型参数的嵌套类型。
                        public Outer<T>.Inner<TValue> Echo(Outer<T>.Inner<TValue> value) => value;
                        // 返回只保留外层开放参数的嵌套类型。
                        public Outer<T>.Inner<int> CaptureOuter(Outer<T>.Inner<int> value) => value;

                        public sealed class Deep<TItem>
                        {
                            // 返回使用三层开放泛型参数的嵌套类型。
                            public Outer<T>.Inner<TValue>.Deep<TItem> Echo(
                                Outer<T>.Inner<TValue>.Deep<TItem> value) => value;
                        }
                    }
                }

                public sealed class GenericSelf<T>
                {
                    // 返回使用当前开放泛型参数的类型。
                    public GenericSelf<T> Echo(GenericSelf<T> value) => value;
                }

                public interface IAccessors
                {
                    int Value { get; set; }
                    event System.Action Changed;
                }

                public sealed class ExplicitAccessors : IAccessors
                {
                    int IAccessors.Value { get => 0; set { } }
                    event System.Action IAccessors.Changed { add { } remove { } }
                }

                public interface IInContract
                {
                    // 声明只读引用参数函数。
                    void Accept(in int value);
                }

                public sealed class InImplementation : IInContract
                {
                    // 隐式实现只读引用参数函数。
                    public void Accept(in int value) { }
                }

                public readonly struct Arithmetic
                {
                    // 声明普通显式转换。
                    public static explicit operator int(Arithmetic value) => 0;
                    // 声明 checked 转换要求的匹配普通版本。
                    public static explicit operator long(Arithmetic value) => 0;
                    // 声明 checked 显式转换。
                    public static explicit operator checked long(Arithmetic value) => 0;
                }

                public sealed class ReturnShapes
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

                public sealed class NestedUser
                {
                    // 返回外层和内层都已构造的嵌套泛型类型。
                    public Outer<int>.Inner<string> Echo(Outer<int>.Inner<string> value) => value;
                }

                public interface IStore<T>
                {
                    // 声明返回泛型枚举器的接口函数。
                    System.Collections.Generic.IEnumerable<T> EnumerateMatches();
                }

                public sealed class StoreImpl<T> : IStore<T>
                {
                    // 用迭代器显式实现接口并生成名称含尖括号的嵌套类型。
                    System.Collections.Generic.IEnumerable<T> IStore<T>.EnumerateMatches()
                    {
                        yield break;
                    }
                }

                public class MultiVirtualBase
                {
                    // 声明第一个同签名可重写函数。
                    public virtual void First() { }
                    // 声明第二个同签名可重写函数。
                    public virtual void Second() { }
                }

                public sealed class MultiVirtualDerived : MultiVirtualBase
                {
                    // 只重写第一个函数。
                    public override void First() { }
                }

                public interface IReturnsInt
                {
                    // 声明整数返回函数。
                    int Read();
                }

                public interface IReturnsString
                {
                    // 声明字符串返回函数。
                    string Read();
                }

                public sealed class ReturnImplementation : IReturnsInt, IReturnsString
                {
                    // 隐式实现整数接口函数。
                    public int Read() => 0;
                    // 显式实现字符串接口函数。
                    string IReturnsString.Read() => string.Empty;
                }
                """);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立源码与真实动态链接库各含一组相同行为写法的工程。
        /// <summary>
        /// 建立用于核对源码和托管函数行为事实一致性的工程。
        /// </summary>
        public static TestProject CreateWithBehaviorMethods()
        {
            TestProject project = Create(includeDependency: false);

            File.WriteAllText(project.RootSourcePath, BuildBehaviorSource("SourceSamples"));
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                BuildBehaviorSource("ExternalSamples"));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 把源码依赖移入目标包以验证统一纳入规则。
        /// <summary>
        /// 建立目标包内部还包含源码依赖的工程。
        /// </summary>
        public static TestProject CreateWithTargetPackageDependency()
        {
            TestProject project = Create(includeDependency: true);
            string oldPath = Path.Combine(project.RootPath, "Packages", "dependency", "Dependency.cs");
            string targetDirectory = Path.Combine(project.RootPath, "Packages", "khengine", "Define");
            string targetPath = Path.Combine(targetDirectory, "Dependency.cs");
            string responsePath = Path.Combine(
                project.RootPath,
                "Library",
                "Bee",
                "artifacts",
                "build",
                "Dependency.rsp");

            Directory.CreateDirectory(targetDirectory);
            File.Delete(oldPath);
            File.WriteAllText(
                targetPath,
                "public sealed class DependencyType { public void DependencyMethod() { } }");
            File.WriteAllText(
                responsePath,
                File.ReadAllText(responsePath).Replace(
                    Relative(project.RootPath, oldPath),
                    Relative(project.RootPath, targetPath),
                    StringComparison.Ordinal));

            return project;
        }

        // 建立外部参考文件与真实文件分离的最小工程。
        /// <summary>
        /// 建立外部参考文件与真实实现文件分离的工程。
        /// </summary>
        public static TestProject CreateWithExternalReference()
        {
            TestProject project = Create(includeDependency: false);

            File.Copy(project.ExternalAssemblyPath, project.ExternalReferencePath);
            string response = File.ReadAllText(project.RootResponsePath)
                .Replace("External.dll", "External.ref.dll", StringComparison.Ordinal);

            File.WriteAllText(project.RootResponsePath, response);

            return project;
        }

        // 建立参考文件与候选实现身份不同的最小工程。
        /// <summary>
        /// 建立参考程序集身份与候选实现身份不同的工程。
        /// </summary>
        public static TestProject CreateWithMismatchedExternalReference()
        {
            TestProject project = CreateWithExternalReference();

            File.Delete(project.ExternalReferencePath);
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalReferencePath,
                "ExpectedAssembly",
                "public sealed class ExpectedType { }");
            WriteAssembly(
                project.ExternalAssemblyPath,
                "DifferentAssembly",
                "public sealed class DifferentType { }");
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立把类型转交给同目录目标程序集的最小工程。
        /// <summary>
        /// 建立包含类型转交与同名未转交类型的工程。
        /// </summary>
        public static TestProject CreateWithForwardedType()
        {
            TestProject project = Create(includeDependency: false);

            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ForwardTargetPath,
                "ForwardTarget",
                """
                namespace ForwardedNamespace
                {
                    public sealed class ForwardedType { }
                    public sealed class CollisionType { }
                }
                """);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "Forwarder",
                """
                using System.Runtime.CompilerServices;
                using ForwardedNamespace;

                [assembly: TypeForwardedTo(typeof(ForwardedType))]

                namespace ForwardedNamespace
                {
                    public sealed class CollisionType { }
                }
                """,
                project.ForwardTargetPath);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立外部程序集通过普通类型关系引用同目录程序集的工程。
        /// <summary>
        /// 建立间接基类程序集只在闭合层级时才载入的测试工程。
        /// </summary>
        public static TestProject CreateWithTransitiveExternalDependency()
        {
            TestProject project = Create(includeDependency: false);

            WriteAssembly(
                project.ForwardTargetPath,
                "ForwardTarget",
                "namespace TransitiveSamples { public class BaseType { "
                    + "public virtual void Touch() { } } "
                    + "public class GenericBase<T> { "
                    + "public virtual TValue Echo<TValue>(TValue value) => value; } }");
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace TransitiveSamples { public sealed class DerivedType : BaseType { "
                    + "public override void Touch() { } "
                    + "public void Call(BaseType target) { target.Touch(); } } "
                    + "public sealed class GenericDerived : GenericBase<int> { "
                    + "public override TValue Echo<TValue>(TValue value) => value; } }",
                project.ForwardTargetPath);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 写入同一门面身份可以落到两个真实类型的错误材料。
        /// <summary>
        /// 写入会产生歧义类型转交身份的托管文件。
        /// </summary>
        public void WriteAmbiguousForwardedAssemblies()
        {
            string secondFacadePath = Path.Combine(this.RootPath, "FacadeCopy.dll");
            string secondTargetPath = Path.Combine(this.RootPath, "ForwardTargetB.dll");

            WriteAssembly(
                this.ForwardTargetPath,
                "ImplA",
                "namespace N { public sealed class T { } }");
            WriteAssembly(
                secondTargetPath,
                "ImplB",
                "namespace N { public sealed class T { } }");
            File.Delete(this.ExternalAssemblyPath);
            WriteAssembly(
                this.ExternalAssemblyPath,
                "Facade",
                "using System.Runtime.CompilerServices; [assembly: TypeForwardedTo(typeof(N.T))]",
                this.ForwardTargetPath);
            WriteAssembly(
                secondFacadePath,
                "Facade",
                "using System.Runtime.CompilerServices; [assembly: TypeForwardedTo(typeof(N.T))]",
                secondTargetPath);
        }

        // 建立转交记录与目标程序集身份不一致的最小工程。
        public static TestProject CreateWithMismatchedForwardTarget()
        {
            TestProject project = CreateWithForwardedType();

            File.Delete(project.ForwardTargetPath);
            WriteAssembly(
                project.ForwardTargetPath,
                "WrongForwardTarget",
                "public sealed class WrongType { }");

            return project;
        }

        // 按测试场景建立最小 Unity 工程文件。
        private static TestProject Create(bool includeDependency)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                $"SetterChecker-{Guid.NewGuid():N}");
            string sourceDirectory = Path.Combine(rootPath, "Packages", "khengine", "Runtime");
            string dependencyDirectory = Path.Combine(rootPath, "Packages", "dependency");
            string artifactsDirectory = Path.Combine(
                rootPath,
                "Library",
                "Bee",
                "artifacts",
                "build");

            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(dependencyDirectory);
            Directory.CreateDirectory(artifactsDirectory);

            string assemblyDefinitionPath = Path.Combine(
                sourceDirectory,
                "khengine.runtime.asmdef");
            string rootSourcePath = Path.Combine(sourceDirectory, "Root.cs");
            string dependencySourcePath = Path.Combine(dependencyDirectory, "Dependency.cs");
            string dependencyReferencePath = Path.Combine(
                artifactsDirectory,
                "Dependency.ref.dll");
            string externalAssemblyPath = Path.Combine(rootPath, "External.dll");
            string externalReferencePath = Path.Combine(rootPath, "External.ref.dll");
            string forwardTargetPath = Path.Combine(rootPath, "ForwardTarget.dll");
            string analyzerPath = Path.Combine(rootPath, "Analyzer.dll");

            File.WriteAllText(assemblyDefinitionPath, "{\"name\":\"khengine.runtime\"}");
            File.WriteAllText(
                Path.Combine(rootPath, "Packages", "khengine", "package.json"),
                "{\"name\":\"khengine\"}");
            File.WriteAllText(rootSourcePath, "public sealed class RootType { }");
            File.WriteAllText(dependencySourcePath, "public sealed class DependencyType { }");
            if (includeDependency)
            {
                WriteAssembly(
                    dependencyReferencePath,
                    "Dependency",
                    "public sealed class DependencyType { }");
            }

            File.Copy(typeof(object).Assembly.Location, externalAssemblyPath);
            File.Copy(typeof(object).Assembly.Location, analyzerPath);

            if (includeDependency)
            {
                WriteResponse(
                    rootPath,
                    Path.Combine(artifactsDirectory, "Dependency.rsp"),
                    "Dependency",
                    dependencySourcePath,
                    new[] { externalAssemblyPath },
                    Array.Empty<string>());
            }

            string rootResponsePath = Path.Combine(
                artifactsDirectory,
                "khengine.runtime.rsp");

            WriteResponse(
                rootPath,
                rootResponsePath,
                "khengine.runtime",
                rootSourcePath,
                includeDependency
                    ? new[] { dependencyReferencePath, externalAssemblyPath }
                    : new[] { externalAssemblyPath },
                new[] { analyzerPath });

            return new TestProject(
                rootPath,
                assemblyDefinitionPath,
                rootSourcePath,
                rootResponsePath,
                externalAssemblyPath,
                externalReferencePath,
                forwardTargetPath,
                analyzerPath);
        }

        // 生成覆盖基础写入、调用、创建和返回事实的测试源码。
        private static string BuildBehaviorSource(string namespaceName)
        {
            return $$"""
                namespace {{namespaceName}}
                {
                    public class Box<T>
                    {
                        public T Value = default!;

                        public T Echo(T value) => value;
                    }

                    public interface IBehavior
                    {
                        void Apply();
                    }

                    public sealed class EffectResource : System.IDisposable
                    {
                        public static int Changes;

                        public void Dispose()
                        {
                            Changes++;
                        }
                    }

                    public sealed class OperatorValue
                    {
                        public static int Changes;

                        public static OperatorValue operator +(
                            OperatorValue left,
                            OperatorValue right)
                        {
                            Changes++;
                            return left;
                        }
                    }

                    public sealed class EffectSequence
                    {
                        public Enumerator GetEnumerator() => new();

                        public struct Enumerator : System.IDisposable
                        {
                            public int Current => 1;

                            public bool MoveNext() => false;

                            public void Dispose()
                            {
                                EffectResource.Changes++;
                            }
                        }
                    }

                    public static class StaticDefaults
                    {
                        public static readonly BehaviorSample Instance = new();

                        public static readonly System.Collections.Generic.List<int> Values =
                            new() { 1 };
                    }

                    public static class InterleavedInitializers
                    {
                        public static int FieldFirst = Mark(1);

                        public static int PropertySecond { get; } = Mark(2);

                        public static int FieldThird = Mark(3);

                        private static int Mark(int value) => value;
                    }

                    public class BaseInitialized
                    {
                        public static int Changes;

                        public BaseInitialized()
                        {
                            Changes++;
                        }
                    }

                    public sealed class ImplicitDerived : BaseInitialized
                    {
                    }

                    public class BehaviorSample
                    {
                        private int m_value;
                        private static int s_value;
                        private BehaviorSample? m_child;
                        private object m_marker = new object();

                        public int Value { get; set; }

                        public int ExpressionProperty => this.m_value;

                        public int this[int index] => this.m_value + index;

                        public event System.Action? Changed;

                        public BehaviorSample()
                        {
                            this.m_value = 1;
                        }

                        public virtual int Read(int input) => input;

                        public int ExpressionMethod() => this.m_value;

                        public override string ToString()
                        {
                            return base.ToString()!;
                        }

                        public void Apply() { }

                        public void WriteField(int input)
                        {
                            this.m_value = input;
                        }

                        public int AssignAndReturn(BehaviorSample target, int input)
                        {
                            return target.m_value = input;
                        }

                        public int AddAndReturn(BehaviorSample target, int input)
                        {
                            return target.m_value += input;
                        }

                        public int IncrementAndReturn(BehaviorSample target)
                        {
                            return ++target.m_value;
                        }

                        public static void WriteStatic(int input)
                        {
                            s_value = input;
                        }

                        public void WriteArray(int[] values, int input)
                        {
                            values[0] = input;
                        }

                        public void WriteReference(ref int target, int input)
                        {
                            target = input;
                        }

                        public int Call(BehaviorSample target, int input)
                        {
                            return target.Read(input);
                        }

                        public BehaviorSample CreateAndReturn()
                        {
                            BehaviorSample result = new();
                            result.m_value = 1;
                            return result;
                        }

                        public BehaviorSample ChooseAndWrite(
                            BehaviorSample first,
                            BehaviorSample second,
                            bool chooseFirst)
                        {
                            BehaviorSample result;
                            if (chooseFirst)
                            {
                                result = first;
                            }
                            else
                            {
                                result = second;
                            }

                            result.m_value = this.m_value;
                            return result;
                        }

                        public void WriteAfterNewOverwrite(BehaviorSample external)
                        {
                            BehaviorSample target = external;
                            target = new BehaviorSample();
                            target.m_value = 1;
                        }

                        public void WriteAfterExternalOverwrite(BehaviorSample external)
                        {
                            BehaviorSample target = new BehaviorSample();
                            target = external;
                            target.m_value = 1;
                        }

                        public void WriteAfterBranch(
                            BehaviorSample external,
                            bool useExternal)
                        {
                            BehaviorSample target;
                            if (useExternal)
                            {
                                target = external;
                            }
                            else
                            {
                                target = new BehaviorSample();
                            }

                            target.m_value = 1;
                        }

                        private static void ConsumePair(
                            BehaviorSample first,
                            BehaviorSample second)
                        {
                        }

                        private static void Observe(BehaviorSample value)
                        {
                        }

                        private static void Replace(out BehaviorSample value)
                        {
                            value = new BehaviorSample();
                        }

                        public void ConsumeOldThenOverwrite(BehaviorSample external)
                        {
                            BehaviorSample target = external;
                            ConsumePair(target, target = new BehaviorSample());
                        }

                        public void ReadAroundOutput(BehaviorSample external)
                        {
                            BehaviorSample target = external;
                            Observe(target);
                            Replace(out target);
                            Observe(target);
                        }

                        public void CaptureThenOverwrite(BehaviorSample external)
                        {
                            BehaviorSample target = external;
                            System.Action captured = () => Observe(target);
                            Observe(target);
                            target = new BehaviorSample();
                            Observe(target);
                            Register(captured);
                        }

                        public void SetProperty(BehaviorSample target, int input)
                        {
                            target.Value = input;
                        }

                        public void AddEvent(BehaviorSample target, System.Action handler)
                        {
                            target.Changed += handler;
                        }

                        public void AddCollection(
                            System.Collections.Generic.List<int> values,
                            int input)
                        {
                            values.Add(input);
                        }

                        public int UseBox(Box<int> box, int input)
                        {
                            box.Value = input;
                            return box.Echo(input);
                        }

                        public static T Identity<T>(T value) => value;

                        public int UseGeneric(int input) => Identity<int>(input);

                        public static T CreateGeneric<T>() where T : new()
                        {
                            return new T();
                        }

                        public (int Left, int Right) Pair(int left, int right)
                        {
                            return (left, right);
                        }

                        public System.Collections.Generic.IEnumerable<int> YieldWrite(
                            BehaviorSample target,
                            int input)
                        {
                            target.m_value = input;
                            yield return target.m_value;
                        }

                        private static void Register(System.Action action)
                        {
                        }

                        public void RegisterCaptured(int input)
                        {
                            int local = 1;
                            Register(() => this.m_value = input + local);
                        }

                        public void WriteChosen(
                            BehaviorSample first,
                            BehaviorSample second,
                            bool chooseFirst,
                            int input)
                        {
                            (chooseFirst ? first : second).m_value = input;
                        }

                        private static void Assign(ref int target, int input)
                        {
                            target = input;
                        }

                        public void PassFieldByReference(int input)
                        {
                            Assign(ref this.m_value, input);
                        }

                        public void PassArrayByReference(int[] values, int input)
                        {
                            Assign(ref values[0], input);
                        }

                        public int ReadArray(int[] values, int index)
                        {
                            return values[index];
                        }

                        public int InvokeDelegate(System.Func<int, int> action, int input)
                        {
                            return action(input);
                        }

                        public System.Func<int, int> BindDelegate()
                        {
                            return this.Read;
                        }

                        public System.Func<int> BindCaptured(int input, bool first)
                        {
                            System.Func<int> left = () => input;
                            System.Func<int> right = () => input + 1;
                            return first ? left : right;
                        }

                        public System.Func<int> BindLocal(int input)
                        {
                            int ReadCaptured() => input;
                            return ReadCaptured;
                        }

                        public BehaviorSample CastSample(object value)
                        {
                            return (BehaviorSample)value;
                        }

                        public BehaviorSample? TryCastSample(object value)
                        {
                            return value as BehaviorSample;
                        }

                        public void SkipConstantFalse(BehaviorSample target)
                        {
                            if (false)
                            {
                                target.m_value = 1;
                                target.Apply();
                            }
                        }

                        public string Interpolate(int input)
                        {
                            return $"Value:{input}";
                        }

                        public int ReturnMinValue()
                        {
                            return int.MinValue;
                        }

                        public string CatchMessage()
                        {
                            try
                            {
                                throw new System.InvalidOperationException();
                            }
                            catch (System.Exception exception)
                            {
                                return exception.Message;
                            }
                        }

                        public void CatchInsideFinally()
                        {
                            try
                            {
                                try
                                {
                                    throw new System.InvalidOperationException();
                                }
                                catch (System.InvalidOperationException)
                                {
                                    Apply();
                                }
                            }
                            finally
                            {
                                WriteStatic(1);
                            }
                        }

                        public void LeaveNestedFinally()
                        {
                            try
                            {
                                try
                                {
                                    Apply();
                                }
                                finally
                                {
                                    WriteStatic(1);
                                }
                            }
                            finally
                            {
                                WriteStatic(2);
                            }
                        }

                        public static void SetOutput(out int value)
                        {
                            value = 1;
                        }

                        public void DisposeBoxed(EffectSequence.Enumerator enumerator)
                        {
                            ((System.IDisposable)enumerator).Dispose();
                        }

                        public int ConditionalTryGet(
                            System.Collections.Generic.Dictionary<int, int>? values,
                            int key)
                        {
                            int result;
                            return values?.TryGetValue(key, out result) == true ? result : 0;
                        }

                        public void RegisterType(
                            System.Collections.Generic.Dictionary<int, System.Type> types,
                            int key)
                        {
                            types[key] = typeof(BehaviorSample);
                        }

                        public int ReadMatrix(int[,] values)
                        {
                            return values[0, 0];
                        }

                        public void WriteMatrix(int[,] values, int input)
                        {
                            values[0, 0] = input;
                        }

                        public int[,] CreateMatrix()
                        {
                            return new int[2, 3];
                        }

                        public ref int MatrixAddress(int[,] values)
                        {
                            return ref values[0, 0];
                        }

                        public object[] CreateArray(int length)
                        {
                            return new object[length];
                        }

                        public System.Type ReadType()
                        {
                            return typeof(BehaviorSample);
                        }

                        public string ReturnText()
                        {
                            return "Foo";
                        }

                        public BehaviorSample EnsureChild()
                        {
                            return this.m_child ??= new BehaviorSample();
                        }

                        public void DeconstructWrite(
                            BehaviorSample first,
                            BehaviorSample second,
                            int left,
                            int right)
                        {
                            (first.m_value, second.m_value) = (left, right);
                        }

                        public BehaviorSample CreateInitialized(int input)
                        {
                            return new BehaviorSample { m_value = input };
                        }

                        public System.Collections.Generic.List<int> CreateCollection(int input)
                        {
                            return new System.Collections.Generic.List<int> { input };
                        }

                        public int[] CreateInitializedArray(int input)
                        {
                            return new[] { input };
                        }

                        public int ConditionalRead(BehaviorSample? target, int input)
                        {
                            return target?.Read(input) ?? input;
                        }

                        public void ConditionalCall(BehaviorSample? target)
                        {
                            target?.Apply();
                        }

                        public int ReadPropertyInCondition(BehaviorSample target)
                        {
                            return target.Value > 0 ? 1 : 0;
                        }

                        public bool IsBehavior(object value)
                        {
                            return value is BehaviorSample;
                        }

                        public void MatchBehavior(object value)
                        {
                            if (value is BehaviorSample sample)
                            {
                                sample.WriteField(1);
                            }
                        }

                        public OperatorValue Add(OperatorValue left, OperatorValue right)
                        {
                            return left + right;
                        }

                        public void UseResource()
                        {
                            using (new EffectResource())
                            {
                            }
                        }

                        public void UseResourceDeclaration()
                        {
                            using EffectResource resource = new();
                        }

                        public void UseLock(object gate)
                        {
                            lock (gate)
                            {
                            }
                        }

                        public int UseSequence(EffectSequence sequence)
                        {
                            int total = 0;
                            foreach (int item in sequence)
                            {
                                total += item;
                            }

                            return total;
                        }

                        public int UseArray(int[] values)
                        {
                            int total = 0;
                            foreach (int item in values)
                            {
                                total += item;
                            }

                            return total;
                        }

                        public void MatchRecursive(object value)
                        {
                            if (value is BehaviorSample { Value: > 0 } sample)
                            {
                                sample.WriteField(1);
                            }
                        }

                        public BehaviorSample ReturnOrThrow(
                            BehaviorSample value,
                            bool succeeds)
                        {
                            return succeeds ? value : throw new System.InvalidOperationException();
                        }

                        [System.Runtime.InteropServices.DllImport(
                            "behavior-native",
                            EntryPoint = "behavior_entry")]
                        public static extern int Native(ref int value);
                    }
                }
                """;
        }

        // 复制根响应文件以构造当前编译不唯一的工程。
        public string CopyRootResponseToSecondBuild()
        {
            string directory = Path.Combine(
                this.RootPath,
                "Library",
                "Bee",
                "artifacts",
                "second-build");
            string path = Path.Combine(directory, "khengine.runtime.rsp");

            Directory.CreateDirectory(directory);
            File.Copy(this.RootResponsePath, path);

            return path;
        }

        // 替换根程序集源码以建立当前模块的最小写法。
        /// <summary>
        /// 用指定源码替换测试工程的根程序集源码。
        /// </summary>
        public void WriteRootSource(string source)
        {
            File.WriteAllText(this.RootSourcePath, source);
        }

        // 删除单个测试创建的临时工程。
        public void Dispose()
        {
            Directory.Delete(this.RootPath, recursive: true);
        }

        // 写入与 Unity 当前格式一致的编译响应文件。
        private static void WriteResponse(
            string rootPath,
            string responsePath,
            string assemblyName,
            string sourcePath,
            IReadOnlyList<string> references,
            IReadOnlyList<string> analyzers)
        {
            List<string> lines = new()
            {
                "-target:library",
                $"-out:\"{Relative(rootPath, Path.Combine(Path.GetDirectoryName(responsePath)!, $"{assemblyName}.dll"))}\"",
                $"-refout:\"{Relative(rootPath, Path.Combine(Path.GetDirectoryName(responsePath)!, $"{assemblyName}.ref.dll"))}\"",
            };

            lines.AddRange(references.Select(path => $"-r:\"{Relative(rootPath, path)}\""));
            lines.AddRange(analyzers.Select(path => $"-analyzer:\"{Relative(rootPath, path)}\""));
            lines.Add($"\"{Relative(rootPath, sourcePath)}\"");
            File.WriteAllLines(responsePath, lines);
        }

        // 把测试路径转换成 Unity 响应文件使用的工程相对路径。
        private static string Relative(string rootPath, string path)
        {
            return Path.GetRelativePath(rootPath, path).Replace('\\', '/');
        }

        // 编译一个会生成 GeneratedType 的真实源码生成器。
        private static void WriteGenerator(string path)
        {
            const string source = """
                using System.Linq;
                using Microsoft.CodeAnalysis;

                [Generator]
                public sealed class FixtureGenerator : ISourceGenerator
                {
                    // 完成源码生成器的初始化。
                    public void Initialize(GeneratorInitializationContext context)
                    {
                    }

                    // 为测试项目生成一个类型。
                    public void Execute(GeneratorExecutionContext context)
                    {
                        AdditionalText input = context.AdditionalFiles.Single();
                        context.AddSource("GeneratedType.g.cs", input.GetText(context.CancellationToken)!);
                    }
                }
                """;
            string[] platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator);
            PortableExecutableReference[] references = platformPaths
                .Append(typeof(ISourceGenerator).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(
                "FixtureGenerator",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using FileStream stream = File.Create(path);
            Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);

            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
            }
        }

        // 编译类型定义或类型转交测试程序集。
        private static void WriteAssembly(
            string path,
            string assemblyName,
            string source,
            string? additionalReference = null,
            bool allowUnsafe = false)
        {
            IEnumerable<string> platformPaths = ((string)AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

            if (additionalReference != null)
            {
                platformPaths = platformPaths.Append(additionalReference);
            }

            PortableExecutableReference[] references = platformPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(item => MetadataReference.CreateFromFile(item))
                .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: allowUnsafe));

            using FileStream stream = File.Create(path);
            Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);

            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
            }
        }

        // 给会替换默认核心库副本的测试工程补上真实核心库引用。
        private static void AppendCoreLibraryReference(TestProject project)
        {
            File.AppendAllLines(
                project.RootResponsePath,
                new[] { $"-r:\"{typeof(object).Assembly.Location}\"" });
        }
    }
}
