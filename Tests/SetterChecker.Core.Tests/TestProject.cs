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

        public string MissingForwardTargetPath => Path.Combine(this.RootPath, "MissingForwardTarget.dll");

        public string MismatchedForwardConsumerPath => Path.Combine(this.RootPath, "MismatchedForwardConsumer.dll");

        public string AnalyzerPath { get; }

        public string UnityReferencePath => Path.Combine(
            this.RootPath,
            "Data",
            "NetStandard",
            "ref",
            "2.1.0",
            "UnityFacade.dll");

        public string UnityRuntimeFacadePath => Path.Combine(
            this.RootPath,
            "Data",
            "MonoBleedingEdge",
            "lib",
            "mono",
            "unityjit-win32",
            "Facades",
            "UnityFacade.dll");

        public string UnityRuntimeTargetPath => Path.Combine(
            this.RootPath,
            "Data",
            "MonoBleedingEdge",
            "lib",
            "mono",
            "unityjit-win32",
            "UnityFacadeTarget.dll");

        public string UnityReferenceTargetFacadePath => Path.Combine(
            this.RootPath,
            "Data",
            "UnityReferenceAssemblies",
            "unity-4.8-api",
            "Facades",
            "TargetFacade.dll");

        public string UnityRuntimeTargetFacadePath => Path.Combine(
            this.RootPath,
            "Data",
            "MonoBleedingEdge",
            "lib",
            "mono",
            "unityjit-win32",
            "Facades",
            "TargetFacade.dll");

        public string UnityReferenceSiblingPath => Path.Combine(
            Path.GetDirectoryName(this.UnityReferencePath)!,
            "ReferenceOnlyTarget.dll");

        public string ConvergingReferenceAliasPath => Path.Combine(
            Path.GetDirectoryName(this.UnityReferencePath)!,
            "ConvergingAlias.dll");

        public string ConvergingRuntimeAliasPath => Path.Combine(
            Path.GetDirectoryName(this.UnityRuntimeFacadePath)!,
            "ConvergingAlias.dll");

        public string ConvergingRootShimPath => Path.Combine(
            Path.GetDirectoryName(this.UnityReferencePath)!,
            "ConvergingRootShim.dll");

        public string ConvergingMiddleAPath => Path.Combine(
            Path.GetDirectoryName(this.UnityRuntimeTargetPath)!,
            "ConvergingMiddleA.dll");

        public string ConvergingMiddleBPath => Path.Combine(
            Path.GetDirectoryName(this.UnityRuntimeTargetPath)!,
            "ConvergingMiddleB.dll");

        public string ConvergingAlternateFinalPath => Path.Combine(
            Path.GetDirectoryName(this.UnityRuntimeTargetPath)!,
            "ConvergingAlternateFinal.dll");

        public string UnityFrameworkReferencePath => Path.Combine(
            this.RootPath,
            "Data",
            "UnityReferenceAssemblies",
            "unity-4.8-api",
            "UnityFramework.dll");

        public string UnityRuntimeFrameworkPath => Path.Combine(
            this.RootPath,
            "Data",
            "MonoBleedingEdge",
            "lib",
            "mono",
            "unityjit-win32",
            "UnityFramework.dll");

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

        // 分开直接调用的源码依赖与完全无关的构建节点。
        public static TestProject CreateWithDirectDependencyAndUnrelatedSource()
        {
            TestProject project = Create();
            project.WriteRootSource("public static class Caller { public static void Entry() => DependencyType.Change(); }");
            File.WriteAllText(Path.Combine(project.RootPath, "Packages", "dependency", "Dependency.cs"),
                "public static class DependencyType { public static int Count; public static void Change() { Count++; } }", new System.Text.UTF8Encoding(false));
            string unrelated = Path.Combine(project.RootPath, "Unrelated.cs");
            File.WriteAllText(unrelated, "public sealed class Unrelated { }", new System.Text.UTF8Encoding(false));
            WriteResponse(project.RootPath, Path.Combine(Path.GetDirectoryName(project.RootResponsePath)!, "Unrelated.rsp"), "Unrelated",
                unrelated, new[] { project.ExternalAssemblyPath }, Array.Empty<string>());
            return project;
        }

        // 复刻 Assets 实现 khengine 接口但 khengine 不引用 Assets 程序集的构建关系。
        public static TestProject CreateWithIncomingImplementation(bool sharedContract = false, bool precompiledContract = false)
        {
            TestProject project = CreateSingleAssembly();
            const string contract = "namespace KH { public interface IUI { bool Visible(); } public static class Calls { public static bool Entry(IUI ui) => ui.Visible(); } }";
            project.WriteRootSource(contract);
            string directory = Path.GetDirectoryName(project.RootResponsePath)!;
            string rootReference = Path.Combine(directory, "khengine.runtime.ref.dll");
            WriteAssembly(rootReference, "khengine.runtime", contract);
            string consumerReference = rootReference;
            if (sharedContract)
            {
                string contractPath = Path.Combine(project.RootPath, "Contracts.cs");
                File.WriteAllText(contractPath, "namespace KH { public interface IUI { bool Visible(); } }", encoding: new System.Text.UTF8Encoding(false));
                consumerReference = Path.Combine(directory, precompiledContract ? "Contracts.dll" : "Contracts.ref.dll");
                WriteAssembly(consumerReference, "Contracts", File.ReadAllText(contractPath));
                if (!precompiledContract)
                {
                    WriteResponse(project.RootPath, Path.Combine(directory, "Contracts.rsp"), "Contracts", contractPath,
                        new[] { project.ExternalAssemblyPath }, Array.Empty<string>());
                }
                project.WriteRootSource("namespace KH { public static class Calls { public static bool Entry(IUI ui) => ui.Visible(); } }");
                WriteResponse(project.RootPath, project.RootResponsePath, "khengine.runtime", project.RootSourcePath,
                    new[] { consumerReference, project.ExternalAssemblyPath }, new[] { project.AnalyzerPath });
            }
            string sourcePath = Path.Combine(project.RootPath, "Consumer.cs");
            File.WriteAllText(sourcePath,
                "public sealed class Consumer : KH.IUI { private static int count; public bool Visible() { count++; return true; } }", encoding: new System.Text.UTF8Encoding(false));
            WriteResponse(project.RootPath, Path.Combine(directory, "Consumer.rsp"), "Consumer", sourcePath,
                new[] { consumerReference, project.ExternalAssemblyPath }, Array.Empty<string>());
            string unrelated = Path.Combine(project.RootPath, "Unrelated.cs");
            File.WriteAllText(unrelated, "public sealed class Unrelated { }", encoding: new System.Text.UTF8Encoding(false));
            WriteResponse(project.RootPath, Path.Combine(directory, "Unrelated.rsp"), "Unrelated", unrelated,
                new[] { project.ExternalAssemblyPath }, Array.Empty<string>());
            return project;
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
                "public sealed class GeneratedFromAdditionalFile { }", encoding: new System.Text.UTF8Encoding(false));
            File.AppendAllLines(
                project.RootResponsePath,
                new[] { $"/additionalfile:\"{Relative(project.RootPath, additionalFilePath)}\"" }, encoding: new System.Text.UTF8Encoding(false));

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
                public static class DispatchSamples
                {
                    public static void First() { MultiVirtualBase receiver = new MultiVirtualDerived(); receiver.First(); }
                    public static void Second() { MultiVirtualBase receiver = new MultiVirtualDerived(); receiver.Second(); }
                    public static void Integer() { IReturnsInt receiver = new ReturnImplementation(); receiver.Read(); }
                    public static void Text() { IReturnsString receiver = new ReturnImplementation(); receiver.Read(); }
                    public static void NewSlot() { IExternal<int> receiver = new NewSlotExternal(); receiver.Apply(1); }
                    public static void BaseSlot() { ExternalBase receiver = new NewSlotExternal(); receiver.Apply(1); }
                    public static void DeclaredSlot() { NewSlotExternal receiver = new NewSlotExternal(); receiver.Apply(1); }
                    public static void Reimplemented() { IExternal<int> receiver = new ReimplementedExternal(); receiver.Apply(1); }
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

            File.WriteAllText(project.RootSourcePath, BuildBehaviorSource("SourceSamples"), encoding: new System.Text.UTF8Encoding(false));
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                BuildBehaviorSource("ExternalSamples"));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 把同一份调用写法分别作为源码与真实 DLL 纳入材料。
        /// <summary>
        /// 建立包含同一组调用写法的源码与托管对照材料。
        /// </summary>
        public static TestProject CreateWithCallTargets(string source, string? coreLibraryPath = null)
        {
            TestProject project = Create(includeDependency: false);
            File.WriteAllText(project.RootSourcePath, source.Replace("namespace Samples;", "namespace SourceSamples;"), encoding: new System.Text.UTF8Encoding(false));
            File.Delete(project.ExternalAssemblyPath);
            string externalSource = source.Replace("namespace Samples;", "namespace ExternalSamples;");
            if (coreLibraryPath == null)
            {
                WriteAssembly(project.ExternalAssemblyPath, "External", externalSource);
                AppendCoreLibraryReference(project);
            }
            else
            {
                WriteAssemblyFromReferences(project.ExternalAssemblyPath, "External", externalSource, new[] { coreLibraryPath });
                WriteResponse(project.RootPath, project.RootResponsePath, "khengine.runtime", project.RootSourcePath,
                    new[] { project.ExternalAssemblyPath, coreLibraryPath }, Array.Empty<string>());
            }
            return project;
        }

        // 复刻当前编译同时引用两份同身份实现但只使用其中独立类型的情况。
        /// <summary>保留两份不同 DLL 及其派生类型，已知接收对象不依赖冲突类型。</summary>
        public static TestProject CreateWithDuplicateRuntimeDefinitions(int relatedCarriers = 0, int referencedCarriers = 2, bool missingSecondType = false)
        {
            TestProject project = CreateWithCallTargets("""
                namespace Samples;
                public interface IRun { void Run(); }
                public sealed class Known : IRun { public void Run() { } }
                public static class Calls
                {
                    public static void Entry(Known target) => ((IRun)target).Run();
                    public static void Unknown(IRun target) => target.Run();
                }
                """);
            string first = Path.Combine(project.RootPath, "FirstCarrier.dll");
            string second = Path.Combine(project.RootPath, "SecondCarrier.dll");
            string consumer = Path.Combine(project.RootPath, "CarrierConsumer.dll");
            WriteAssembly(first, "SharedCarrier", "namespace Shared { public class Base"
                + (relatedCarriers > 0 ? " : ExternalSamples.IRun" : string.Empty)
                + " { public virtual void Run() { } } }", project.ExternalAssemblyPath);
            WriteAssembly(second, "SharedCarrier", "namespace Shared { public class " + (missingSecondType ? "Other" : "Base")
                + (relatedCarriers > 1 ? " : ExternalSamples.IRun" : string.Empty)
                + " { public static int Count; public virtual void Run() { Count++; } } }", project.ExternalAssemblyPath);
            WriteAssembly(consumer, "CarrierConsumer", "public sealed class Unrelated : Shared.Base { }", first);
            File.AppendAllLines(project.RootResponsePath, new[] { first, second }.Take(referencedCarriers)
                .Append(consumer).Select(path => $"-r:\"{path}\""), encoding: new System.Text.UTF8Encoding(false));
            return project;
        }

        // 复刻类参数重排后通过元数据指定改名虚槽的写法。
        /// <summary>构造带类参数重排和改名 MethodImpl 的真实托管对照文件。</summary>
        public static TestProject CreateWithGenericClassMethodImplementation()
        {
            TestProject project = CreateWithCallTargets("""
                namespace Samples;
                public class Base<T> { public virtual void Run(T value) { } }
                public sealed class Derived<A, B> : Base<B> { public override void Run(B value) { } }
                public static class Calls { public static void Entry() { Base<int> target = new Derived<string, int>(); target.Run(3); } }
                """);
            using MemoryStream stream = new(File.ReadAllBytes(project.ExternalAssemblyPath));
            using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(stream);
            Mono.Cecil.TypeDefinition derived = module.GetType("ExternalSamples.Derived`2");
            Mono.Cecil.TypeDefinition baseType = module.GetType("ExternalSamples.Base`1");
            Mono.Cecil.MethodReference declaration = new("Run", module.TypeSystem.Void, derived.BaseType) { HasThis = true };
            declaration.Parameters.Add(new Mono.Cecil.ParameterDefinition(baseType.GenericParameters[0]));
            Mono.Cecil.MethodDefinition implementation = derived.Methods.Single(method => method.Name == "Run");
            implementation.Overrides.Add(declaration);
            implementation.Name = "Implementation";
            implementation.IsNewSlot = true;
            module.Write(project.ExternalAssemblyPath);
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
                "public sealed class DependencyType { public void DependencyMethod() { } }", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(
                responsePath,
                File.ReadAllText(responsePath).Replace(
                    Relative(project.RootPath, oldPath),
                    Relative(project.RootPath, targetPath),
                    StringComparison.Ordinal), encoding: new System.Text.UTF8Encoding(false));

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

            File.WriteAllText(project.RootResponsePath, response, encoding: new System.Text.UTF8Encoding(false));

            return project;
        }

        // 建立Unity参考文件与当前运行目录候选版本不同的最小工程。
        /// <summary>
        /// 建立可切换门面形态、位置和版本的Unity运行目录。
        /// </summary>
        public static TestProject CreateWithUnityRuntimeCandidate(
            bool runtimeIsForwardingFacade,
            bool placeInFacadeDirectory = true,
            string referenceVersion = "2.0.0.0",
            string runtimeVersion = "2.1.0.0",
            string runtimeAssemblyName = "UnityFacade")
        {
            TestProject project = Create(includeDependency: false);
            string referencePath = project.UnityReferencePath;
            string targetPath = project.UnityRuntimeTargetPath;
            string facadePath = placeInFacadeDirectory
                ? project.UnityRuntimeFacadePath
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetDirectoryName(project.UnityRuntimeFacadePath)!)!,
                    "UnityFacade.dll");

            Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(project.UnityRuntimeFacadePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(facadePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Data", "Managed"));
            WriteUnityRuntimeCoreLibrary(project);
            WriteUnityHostEvidence(project);
            WriteAssembly(
                referencePath,
                "UnityFacade",
                $"using System.Reflection; [assembly: AssemblyVersion(\"{referenceVersion}\")] "
                    + "namespace UnityFacadeTypes { public class BaseType { } }");
            WriteAssembly(
                targetPath,
                "UnityFacadeTarget",
                "namespace UnityFacadeTypes { public class BaseType { } }");
            WriteAssembly(
                facadePath,
                runtimeAssemblyName,
                runtimeIsForwardingFacade
                    ? $"using System.Reflection; using System.Runtime.CompilerServices; "
                        + $"[assembly: AssemblyVersion(\"{runtimeVersion}\")] "
                        + "[assembly: TypeForwardedTo(typeof(UnityFacadeTypes.BaseType))]"
                    : $"using System.Reflection; [assembly: AssemblyVersion(\"{runtimeVersion}\")] "
                        + "namespace UnityFacadeTypes { public class BaseType { } }",
                runtimeIsForwardingFacade ? new[] { targetPath } : Array.Empty<string>());
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace UnityFacadeConsumer { "
                    + "public sealed class Derived : UnityFacadeTypes.BaseType { } }",
                referencePath);
            File.AppendAllLines(
                project.RootResponsePath,
                new[]
                {
                    "-define:UNITY_EDITOR_WIN",
                    $"-r:\"{Relative(project.RootPath, referencePath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立参考和运行目录同时含同身份类型转交目标的工程。
        /// <summary>
        /// 两份目标都是纯转交文件，但只有当前Unity运行目录中的文件是运行载体。
        /// </summary>
        public static TestProject CreateWithUnityForwardingTargetCollision(bool includeRuntimeCarrier = true)
        {
            TestProject project = Create(includeDependency: false);
            string referencePath = project.UnityReferencePath;
            string referenceTargetPath = project.UnityReferenceTargetFacadePath;
            string runtimeTargetPath = project.UnityRuntimeTargetFacadePath;
            string implementationPath = project.UnityRuntimeTargetPath;

            Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(referenceTargetPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(runtimeTargetPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(implementationPath)!);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Data", "Managed"));
            WriteUnityRuntimeCoreLibrary(project);
            WriteUnityHostEvidence(project);
            WriteAssembly(implementationPath, "UnityFacadeTarget",
                "namespace UnityFacadeTypes { public class BaseType { } }");
            WriteAssembly(referenceTargetPath, "TargetFacade",
                "namespace UnityFacadeTypes { public class BaseType { } }");
            WriteAssembly(referencePath, "UnityFacade",
                "using System.Runtime.CompilerServices; using UnityFacadeTypes; "
                    + "[assembly: TypeForwardedTo(typeof(BaseType))]",
                referenceTargetPath);
            File.Delete(referenceTargetPath);
            WriteAssembly(referenceTargetPath, "TargetFacade",
                "using System.Runtime.CompilerServices; using UnityFacadeTypes; "
                    + "[assembly: TypeForwardedTo(typeof(BaseType))]",
                implementationPath);
            if (includeRuntimeCarrier)
            {
                WriteAssembly(runtimeTargetPath, "TargetFacade",
                    "using System.Runtime.CompilerServices; using UnityFacadeTypes; "
                        + "[assembly: TypeForwardedTo(typeof(BaseType))]",
                    implementationPath);
            }
            WriteAssembly(project.ExternalAssemblyPath, "External",
                "namespace UnityFacadeConsumer { public sealed class Derived "
                    + ": UnityFacadeTypes.BaseType { } }",
                referencePath,
                referenceTargetPath,
                implementationPath);
            File.AppendAllLines(project.RootResponsePath,
                new[]
                {
                    "-define:UNITY_EDITOR_WIN",
                    $"-r:\"{Relative(project.RootPath, referencePath)}\"",
                    $"-r:\"{Relative(project.RootPath, referenceTargetPath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立同时含已承载与缺失类型转交分支的工程。
        /// <summary>
        /// 根源码只使用UsedType，独立消费者动态链接到已删除载体中的MissingType。
        /// </summary>
        public static TestProject CreateWithPartiallyMissingForwarders()
        {
            TestProject project = Create(includeDependency: false);
            string facadePath = project.ExternalReferencePath;
            string usedTargetPath = project.ForwardTargetPath;
            string missingTargetPath = project.MissingForwardTargetPath;

            WriteAssembly(usedTargetPath, "UsedForwardTarget",
                "namespace PartialForwarding { public class UsedType { public virtual int Read() => 1; } }");
            WriteAssembly(missingTargetPath, "MissingForwardTarget",
                "namespace PartialForwarding { public class MissingType { public virtual int Read() => 1; } }");
            WriteAssembly(facadePath, "PartialFacade",
                "using System.Runtime.CompilerServices; using PartialForwarding; "
                    + "[assembly: TypeForwardedTo(typeof(UsedType))] "
                    + "[assembly: TypeForwardedTo(typeof(MissingType))]",
                usedTargetPath,
                missingTargetPath);
            WriteAssembly(project.ExternalAssemblyPath, "External",
                "namespace PartialForwardingConsumer { public sealed class MissingConsumer "
                    + ": PartialForwarding.MissingType { public override int Read() => 2; } }",
                facadePath,
                missingTargetPath);
            File.Delete(missingTargetPath);
            File.WriteAllText(project.RootSourcePath,
                "namespace PartialForwardingConsumer { public sealed class RootType "
                    + ": PartialForwarding.UsedType { public override int Read() => 2; } }", encoding: new System.Text.UTF8Encoding(false));
            File.AppendAllLines(project.RootResponsePath,
                new[]
                {
                    $"-r:\"{Relative(project.RootPath, facadePath)}\"",
                    $"-r:\"{Relative(project.RootPath, usedTargetPath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立参考门面同目录只有参考目标副本的Unity工程。
        /// <summary>
        /// 门面转交到同目录的普通参考定义，当前运行目录故意没有该身份。
        /// </summary>
        public static TestProject CreateWithReferenceSiblingForwarderStub()
        {
            TestProject project = Create(includeDependency: false);
            string facadePath = project.UnityReferencePath;
            string referenceTargetPath = project.UnityReferenceSiblingPath;

            Directory.CreateDirectory(Path.GetDirectoryName(facadePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(project.UnityRuntimeTargetPath)!);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Data", "Managed"));
            WriteUnityRuntimeCoreLibrary(project);
            WriteUnityHostEvidence(project);
            WriteAssembly(referenceTargetPath, "ReferenceOnlyTarget",
                "using System.Runtime.CompilerServices; [assembly: ReferenceAssembly] "
                    + "namespace ReferenceOnlyTypes { public class BaseType { "
                    + "public virtual int Read() => 1; } }");
            WriteAssembly(facadePath, "UnityFacade",
                "using System.Runtime.CompilerServices; using ReferenceOnlyTypes; "
                    + "[assembly: TypeForwardedTo(typeof(BaseType))]",
                referenceTargetPath);
            WriteAssembly(project.ExternalAssemblyPath, "External",
                "namespace ReferenceOnlyConsumer { public sealed class Derived "
                    + ": ReferenceOnlyTypes.BaseType { public override int Read() => 2; } }",
                facadePath,
                referenceTargetPath);
            File.AppendAllLines(project.RootResponsePath,
                new[]
                {
                    "-define:UNITY_EDITOR_WIN",
                    $"-r:\"{Relative(project.RootPath, facadePath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立两条同身份类型转交分支会合或分歧的Unity工程。
        /// <summary>
        /// 参考Alias和运行Alias具有同一完整身份，分别经过MiddleA与MiddleB到达最终定义。
        /// </summary>
        public static TestProject CreateWithConvergingForwardingBranches(bool differentEndpoints = false)
        {
            TestProject project = Create(includeDependency: false);
            string referenceAliasPath = project.ConvergingReferenceAliasPath;
            string runtimeAliasPath = project.ConvergingRuntimeAliasPath;
            string rootShimPath = project.ConvergingRootShimPath;
            string middleAPath = project.ConvergingMiddleAPath;
            string middleBPath = project.ConvergingMiddleBPath;
            string finalAPath = project.UnityRuntimeTargetPath;
            string finalBPath = project.ConvergingAlternateFinalPath;

            Directory.CreateDirectory(Path.GetDirectoryName(referenceAliasPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(runtimeAliasPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(finalAPath)!);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Data", "Managed"));
            WriteUnityRuntimeCoreLibrary(project);
            WriteUnityHostEvidence(project);
            WriteAssembly(finalAPath, "UnityFacadeTarget",
                "namespace Converging { public class ForwardedType { public virtual int Touch() => 1; } }");
            if (differentEndpoints)
            {
                WriteAssembly(finalBPath, "ConvergingAlternateFinal",
                    "namespace Converging { public class ForwardedType { public virtual int Touch() => 1; } }");
            }

            WriteAssembly(middleAPath, "ConvergingMiddleA",
                "namespace Converging { public class ForwardedType { public virtual int Touch() => 1; } }");
            WriteAssembly(middleBPath, "ConvergingMiddleB",
                "namespace Converging { public class ForwardedType { public virtual int Touch() => 1; } }");
            WriteAssembly(referenceAliasPath, "ConvergingAlias",
                "using System.Runtime.CompilerServices; using Converging; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                middleAPath);
            WriteAssembly(runtimeAliasPath, "ConvergingAlias",
                "namespace Converging { public class ForwardedType { public virtual int Touch() => 1; } }");
            WriteAssembly(rootShimPath, "ConvergingRootShim",
                "using System.Runtime.CompilerServices; using Converging; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                runtimeAliasPath);
            File.Delete(runtimeAliasPath);
            WriteAssembly(runtimeAliasPath, "ConvergingAlias",
                "using System.Runtime.CompilerServices; using Converging; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                middleBPath);
            WriteAssembly(project.ExternalAssemblyPath, "External",
                "namespace ConvergingConsumer { public sealed class Derived "
                    + ": Converging.ForwardedType { public override int Touch() => 2; } }",
                referenceAliasPath,
                middleAPath);
            RewriteBaseTypeAssemblyScope(
                project.ExternalAssemblyPath,
                "ConvergingConsumer.Derived",
                referenceAliasPath);
            File.Delete(middleAPath);
            WriteAssembly(middleAPath, "ConvergingMiddleA",
                "using System.Runtime.CompilerServices; using Converging; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                finalAPath);
            File.Delete(middleBPath);
            WriteAssembly(middleBPath, "ConvergingMiddleB",
                "using System.Runtime.CompilerServices; using Converging; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                differentEndpoints ? finalBPath : finalAPath);
            File.AppendAllLines(project.RootResponsePath,
                new[]
                {
                    "-define:UNITY_EDITOR_WIN",
                    $"-r:\"{Relative(project.RootPath, referenceAliasPath)}\"",
                    $"-r:\"{Relative(project.RootPath, rootShimPath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立一条会合分支缺失实际中间载体的错误工程。
        /// <summary>
        /// 保留同身份门面的两条分支，并让派生类型按门面身份触发完整链解析。
        /// </summary>
        public static TestProject CreateWithMissingConvergingForwardingBranch()
        {
            TestProject project = CreateWithConvergingForwardingBranches();
            File.Delete(project.ConvergingMiddleBPath);
            return project;
        }

        // 建立纯参考门面与同身份真实运行定义并存的Unity工程。
        /// <summary>
        /// 编译仍使用参考门面，运行材料必须只选择当前Unity的真实定义。
        /// </summary>
        public static TestProject CreateWithUnityPureReferenceAndRuntimeDefinition()
        {
            TestProject project = CreateWithUnityRuntimeCandidate(
                runtimeIsForwardingFacade: false,
                referenceVersion: "2.0.0.0",
                runtimeVersion: "2.0.0.0");
            File.Delete(project.UnityReferencePath);
            WriteAssembly(
                project.UnityReferencePath,
                "UnityFacade",
                "using System.Reflection; using System.Runtime.CompilerServices; "
                    + "[assembly: AssemblyVersion(\"2.0.0.0\")] "
                    + "[assembly: TypeForwardedTo(typeof(UnityFacadeTypes.BaseType))]",
                project.UnityRuntimeTargetPath);
            return project;
        }

        // 建立只有普通字段引用带入接口实现程序集的工程。
        /// <summary>间接实现没有出现在任何已加载类型的继承链，必须沿程序集引用找到。</summary>
        public static TestProject CreateWithLookupOnlyImplementation()
        {
            TestProject project = Create(includeDependency: false);
            WriteAssembly(project.ExternalReferencePath, "LookupContract",
                "namespace LookupContract { public interface IRun { void Run(); } }");
            WriteAssembly(project.ForwardTargetPath, "LookupImplementation",
                "namespace LookupImplementation { public sealed class Worker : LookupContract.IRun { "
                    + "public static int State; public void Run() { State++; } } }", project.ExternalReferencePath);
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(project.ExternalAssemblyPath, "External",
                "public class Carrier { public LookupImplementation.Worker Value; } "
                    + "public static class Caller { public static void Call(LookupContract.IRun target) { target.Run(); } }",
                project.ForwardTargetPath, project.ExternalReferencePath);
            WriteAssembly(project.MissingForwardTargetPath, "UnreferencedPlugin",
                "public sealed class Plugin : LookupContract.IRun { public void Run() { } }", project.ExternalReferencePath);
            string contractPath = Path.Combine(project.RootPath, "LookupContract.dll");
            File.Move(project.ExternalReferencePath, contractPath);
            project.WriteRootSource("public static class SourceCaller { public static void Call(LookupContract.IRun target) { target.Run(); } }");
            File.AppendAllLines(project.RootResponsePath, new[] { $"-r:\"{contractPath}\"" }, encoding: new System.Text.UTF8Encoding(false));
            WriteAssembly(Path.Combine(Path.GetDirectoryName(project.RootResponsePath)!, "khengine.runtime.dll"), "khengine.runtime",
                "public static class SourceCaller { public static void Call() { } }");
            AppendCoreLibraryReference(project);
            return project;
        }

        // 建立接口契约经候选基类传递到派生实现的工程。
        /// <summary>
        /// 接口和抽象基类只存在于查找目录，启动材料只直接包含派生类程序集。
        /// </summary>
        public static TestProject CreateWithTransitiveInterfaceHierarchy(bool missingUnrelatedBase = false)
        {
            TestProject project = Create(includeDependency: false);
            string entrySource = """
                namespace ExternalSamples
                {
                    public sealed class Known : LookupContract.IRun { void LookupContract.IRun.Run() { } }
                    public sealed class Holder { public Known Value; }
                    public static class Calls
                    {
                        public static void Entry()
                        {
                            LookupContract.IRun target = new Known();
                            target.Run();
                        }
                        public static void Unknown(LookupContract.IRun target) => target.Run();
                        public static void KnownParameter(Known target) => ((LookupContract.IRun)target).Run();
                        public static void KnownField(Holder holder) => ((LookupContract.IRun)holder.Value).Run();
                        public static void Mixed(Known known, LookupContract.IRun open, bool selectKnown)
                            => (selectKnown ? (LookupContract.IRun)known : open).Run();
                    }
                }
                """;

            WriteAssembly(
                project.ExternalReferencePath,
                "LookupContract",
                "namespace LookupContract { public interface IRun { void Run(); } }");
            WriteAssembly(
                project.ForwardTargetPath,
                "LookupBase",
                "namespace LookupBase { public abstract class Base : LookupContract.IRun { "
                    + "public abstract void Run(); } }",
                project.ExternalReferencePath);
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace LookupConsumer { public sealed class Derived : LookupBase.Base"
                    + (missingUnrelatedBase ? ", LookupContract.IRun" : string.Empty) + " { "
                    + "public override void Run() { } } }" + (missingUnrelatedBase ? entrySource
                        : "public static class Caller { public static void Call(LookupContract.IRun target) { target.Run(); } }"),
                project.ForwardTargetPath,
                project.ExternalReferencePath);
            AppendCoreLibraryReference(project);
            if (missingUnrelatedBase)
            {
                File.Delete(project.ForwardTargetPath);
                string contractPath = Path.Combine(project.RootPath, "LookupContract.dll");
                File.Move(project.ExternalReferencePath, contractPath);
                File.AppendAllLines(project.RootResponsePath, new[] { $"-r:\"{contractPath}\"" }, encoding: new System.Text.UTF8Encoding(false));
                File.WriteAllText(project.RootSourcePath, entrySource.Replace("ExternalSamples", "SourceSamples", StringComparison.Ordinal), encoding: new System.Text.UTF8Encoding(false));
            }

            return project;
        }

        // 建立使用已审计 Unity 宿主、编译参考和实际运行库的工程。
        /// <summary>
        /// 可切换公钥、宿主身份和参考版本。
        /// </summary>
        public static TestProject CreateWithUnityFrameworkVersionRemapping(
            bool changeRuntimeToken = false,
            bool includeHost = true,
            string hostFileVersion = "2021.3.16.0",
            bool includeOldVersionReference = true)
        {
            TestProject project = Create(includeDependency: false);
            string referencePath = project.UnityFrameworkReferencePath;
            string runtimePath = project.UnityRuntimeFrameworkPath;

            Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Data", "Managed"));
            WriteAssembly(referencePath, "UnityFramework",
                "using System.Reflection; using System.Runtime.CompilerServices; "
                    + "[assembly: AssemblyVersion(\"4.2.0.0\")] [assembly: ReferenceAssembly] "
                    + "namespace UnityFrameworkTypes { public class BaseType { "
                    + "public virtual int Read() => 1; } }");
            WriteAssembly(runtimePath, "UnityFramework",
                "using System.Reflection; [assembly: AssemblyVersion(\"4.0.0.0\")] "
                    + "namespace UnityFrameworkTypes { public class BaseType { "
                    + "public virtual int Read() => 1; } }");
            if (changeRuntimeToken)
            {
                RewriteAssemblyPublicKey(runtimePath, typeof(object).Assembly.GetName().GetPublicKey()!);
            }

            WriteAssembly(project.ExternalAssemblyPath, "External",
                "namespace UnityFrameworkConsumer { public sealed class Derived "
                    + ": UnityFrameworkTypes.BaseType { public override int Read() => 2; } }",
                referencePath);
            File.AppendAllLines(project.RootResponsePath,
                new[]
                {
                    "-define:UNITY_EDITOR_WIN",
                    $"-r:\"{Relative(project.RootPath, includeOldVersionReference ? referencePath : runtimePath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(project);
            WriteUnityRuntimeCoreLibrary(project);
            if (includeHost)
            {
                WriteUnityHostEvidence(project, hostFileVersion);
            }

            return project;
        }

        // 建立参考文件只向不同名真实程序集转交类型的 Unity 输入。
        /// <summary>
        /// 同名运行候选版本故意不同，实际实现必须由参考转交表决定。
        /// </summary>
        public static TestProject CreateWithForwardingUnityReference()
        {
            TestProject project = CreateWithUnityRuntimeCandidate(runtimeIsForwardingFacade: true);
            File.Delete(project.UnityReferencePath);
            WriteAssembly(project.UnityReferencePath, "UnityFacade",
                "using System.Reflection; using System.Runtime.CompilerServices; "
                    + "[assembly: AssemblyVersion(\"4.1.3.0\")] "
                    + "[assembly: TypeForwardedTo(typeof(UnityFacadeTypes.BaseType))]",
                project.UnityRuntimeTargetPath);
            File.AppendAllLines(project.RootResponsePath,
                new[] { $"-r:\"{Relative(project.RootPath, project.UnityRuntimeTargetPath)}\"" }, encoding: new System.Text.UTF8Encoding(false));
            return project;
        }

        // 建立同名但版本不同的普通外部参考和实现。
        /// <summary>
        /// 建立不属于Unity门面规则的外部程序集版本反例。
        /// </summary>
        public static TestProject CreateWithVersionedExternalReference()
        {
            TestProject project = CreateWithExternalReference();

            File.Delete(project.ExternalReferencePath);
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalReferencePath,
                "External",
                "using System.Reflection; [assembly: AssemblyVersion(\"1.0.0.0\")] "
                    + "public sealed class ExternalType { }");
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "using System.Reflection; [assembly: AssemblyVersion(\"2.0.0.0\")] "
                    + "public sealed class ExternalType { }");
            AppendCoreLibraryReference(project);

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
        public static TestProject CreateWithForwardedType(bool unmatchedFullPublicKey = false)
        {
            TestProject project = Create(includeDependency: false);

            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ForwardTargetPath,
                "ForwardTarget",
                """
                namespace ForwardedNamespace
                {
                    public class ForwardedType { public virtual int Read() => 1; }
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
            if (unmatchedFullPublicKey)
            {
                using Mono.Cecil.AssemblyDefinition assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(
                    project.ExternalAssemblyPath, new Mono.Cecil.ReaderParameters { InMemory = true });
                Mono.Cecil.AssemblyNameReference reference = assembly.MainModule.AssemblyReferences.Single(item => item.Name == "ForwardTarget");
                reference.PublicKey = typeof(object).Assembly.GetName().GetPublicKey()!;
                assembly.Write(project.ExternalAssemblyPath);
            }
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立同一运行文件既保留本地定义又按名称转交该类型的工程。
        /// <summary>
        /// 本地元数据标记继续指向原定义，外部类型引用按 Unity Mono 的名称表转到目标文件。
        /// </summary>
        public static TestProject CreateWithDefinitionAndSameFileForwarder()
        {
            TestProject project = Create(includeDependency: false);
            string delegateFacadePath = Path.Combine(project.RootPath, "DelegateFacade.dll");

            File.Delete(project.ExternalReferencePath);
            WriteAssembly(
                project.ExternalReferencePath,
                "SameFileFacade",
                """
                using System;
                namespace SameFileForwarding
                {
                    public delegate void ForwardedHandler();

                    public class Entry
                    {
                        public Action Callback;
                        public Entry() { }
                        public int Marker() => 1;
                        public virtual int Touch() => 1;
                        public void Invoke() => Callback();
                    }

                    public sealed class LocalDerived : Entry
                    {
                        public override int Touch() => 2;
                        public int CallLocal() => Marker();
                        public int CreateAndCall() => new Entry().Touch();
                        public void FieldLocal()
                        {
                            Entry value = new Entry();
                            value.Callback = LocalCallback;
                            value.Invoke();
                        }
                        public void DelegateLocal()
                        {
                            ForwardedHandler handler = LocalCallback;
                            handler();
                        }
                        private void LocalCallback() { }
                    }
                }
                """);
            WriteAssembly(delegateFacadePath, "DelegateFacade", "public sealed class Marker { }");
            AddForwardedType(
                delegateFacadePath,
                "System",
                "MulticastDelegate",
                typeof(MulticastDelegate).Assembly.Location);
            RewriteBaseTypeAssemblyScope(
                project.ExternalReferencePath,
                "SameFileForwarding.ForwardedHandler",
                delegateFacadePath);
            WriteAssembly(
                project.ForwardTargetPath,
                "SameFileTarget",
                """
                using System;
                namespace SameFileForwarding
                {
                    public class Entry
                    {
                        public Action Callback;
                        public Entry() { }
                        public int Marker() => 3;
                        public virtual int Touch() => 3;
                        public void Invoke() => Callback();
                    }
                }
                """);
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                """
                namespace SameFileForwardingConsumer
                {
                    public sealed class ExternalDerived : SameFileForwarding.Entry
                    {
                        public override int Touch() => 4;
                        public int CallExternal(SameFileForwarding.Entry target) => target.Marker();
                        public void FieldExternal()
                        {
                            SameFileForwarding.Entry value = new SameFileForwarding.Entry();
                            value.Callback = ExternalCallback;
                            value.Invoke();
                        }
                        private void ExternalCallback() { }
                    }
                }
                """,
                project.ExternalReferencePath);
            AddForwardedType(
                project.ExternalReferencePath,
                "SameFileForwarding",
                "Entry",
                project.ForwardTargetPath);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立返回值和参数均引用转交泛型嵌套类型的真实调用。
        /// <summary>
        /// 复刻参考库的集合遍历器签名映射到运行库的写法。
        /// </summary>
        public static TestProject CreateWithForwardedMethodSignature(bool includeFieldCollision = false, bool includeReflectedFields = false)
        {
            TestProject project = Create(includeDependency: false);
            string definition = """
                namespace ForwardedSignature
                {
                    public class Value { }
                    public interface IUse { Value Convert(Value value); }
                    public interface IGenericUse<T> { T Convert(T value); }
                    public class Base { public virtual Value Convert(Value value) => value; }
                    public class GenericBase<T> { public virtual T Convert(T value) => value; }
                    public class Bag<T>
                    {
                        public struct Enumerator { }
                        public Enumerator GetEnumerator() => default;
                        public void Accept(Enumerator value) { }
                        public void Pick(T value) { }
                        public void Pick(int value) { }
                    }
                }
                """;
            if (includeReflectedFields)
            {
                definition = definition.Replace("public class Value { }", "public class Value { public int Number; }")
                    .Replace("public struct Enumerator", "public T Current; public struct Enumerator");
            }
            WriteAssembly(project.ForwardTargetPath, "SignatureImplementation", definition);
            WriteAssembly(project.ExternalReferencePath, "SignatureFacade", definition);
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(project.ExternalAssemblyPath, "External", """
                using ForwardedSignature;
                public sealed class Worker : IUse
                {
                    public Value Convert(Value value) => value;
                }
                public sealed class ExplicitWorker : IUse
                {
                    Value IUse.Convert(Value value) => value;
                }
                public sealed class GenericWorker : IGenericUse<Value>
                {
                    public Value Convert(Value value) => value;
                }
                public sealed class ExplicitGenericWorker : IGenericUse<Value>
                {
                    Value IGenericUse<Value>.Convert(Value value) => value;
                }
                public sealed class DerivedWorker : Base
                {
                    public override Value Convert(Value value) => value;
                }
                public sealed class NestedWorker<T> : IGenericUse<Bag<Value>>
                {
                    public Bag<Value> Convert(Bag<Value> value) => value;
                }
                public sealed class ExplicitNestedWorker<T> : IGenericUse<Bag<Value>>
                {
                    Bag<Value> IGenericUse<Bag<Value>>.Convert(Bag<Value> value) => value;
                }
                public class Middle<T> : GenericBase<Bag<T>> { }
                public sealed class DerivedGenericWorker<T> : Middle<Value>
                {
                    public override Bag<Value> Convert(Bag<Value> value) => value;
                }
                public sealed class SignatureConsumer
                {
                    public void DispatchWorker(Value value) { IUse receiver = new Worker(); receiver.Convert(value); }
                    public void DispatchExplicitWorker(Value value) { IUse receiver = new ExplicitWorker(); receiver.Convert(value); }
                    public void DispatchGenericWorker(Value value) { IGenericUse<Value> receiver = new GenericWorker(); receiver.Convert(value); }
                    public void DispatchExplicitGenericWorker(Value value) { IGenericUse<Value> receiver = new ExplicitGenericWorker(); receiver.Convert(value); }
                    public void DispatchDerivedWorker(Value value) { Base receiver = new DerivedWorker(); receiver.Convert(value); }
                    public void DispatchNestedWorker(Bag<Value> value) { IGenericUse<Bag<Value>> receiver = new NestedWorker<int>(); receiver.Convert(value); }
                    public void DispatchExplicitNestedWorker(Bag<Value> value) { IGenericUse<Bag<Value>> receiver = new ExplicitNestedWorker<int>(); receiver.Convert(value); }
                    public void DispatchDerivedGenericWorker(Bag<Value> value) { GenericBase<Bag<Value>> receiver = new DerivedGenericWorker<int>(); receiver.Convert(value); }
                    public IGenericUse<Value> Receiver;
                    public Value ReadField() => Receiver.Convert(default);
                    public void RunOverload() => new Bag<int>().Pick(1);
                    public void Run()
                    {
                        ForwardedSignature.Bag<int> bag = new ForwardedSignature.Bag<int>();
                        ForwardedSignature.Bag<int>.Enumerator value = bag.GetEnumerator();
                        bag.Accept(value);
                    }
                }
                """ + (includeReflectedFields ? """
                public static class ReflectedFields
                {
                    public static void Set(ForwardedSignature.Bag<ForwardedSignature.Value> bag, ForwardedSignature.Value value)
                    { typeof(ForwardedSignature.Bag<ForwardedSignature.Value>).GetField("Current").SetValue(bag, value); }
                    public static void Read(ForwardedSignature.Value value)
                    {
                        var bag = new ForwardedSignature.Bag<ForwardedSignature.Value>();
                        bag.Current = value;
                        ((ForwardedSignature.Value)typeof(ForwardedSignature.Bag<ForwardedSignature.Value>).GetField("Current").GetValue(bag)).Number = 1;
                    }
                }
                """ : string.Empty), project.ExternalReferencePath);
            File.Delete(project.ExternalReferencePath);
            WriteAssembly(project.ExternalReferencePath, "SignatureFacade", """
                using System.Runtime.CompilerServices;
                [assembly: TypeForwardedTo(typeof(ForwardedSignature.Value))]
                [assembly: TypeForwardedTo(typeof(ForwardedSignature.IUse))]
                [assembly: TypeForwardedTo(typeof(ForwardedSignature.IGenericUse<>))]
                [assembly: TypeForwardedTo(typeof(ForwardedSignature.Base))]
                [assembly: TypeForwardedTo(typeof(ForwardedSignature.GenericBase<>))]
                [assembly: TypeForwardedTo(typeof(ForwardedSignature.Bag<>))]
                """, project.ForwardTargetPath);
            if (includeFieldCollision)
            {
                string wrongPath = Path.Combine(project.RootPath, "WrongSignatureImplementation.dll");
                WriteAssembly(wrongPath, "SignatureImplementation", """
                    using System.Reflection;
                    [assembly: AssemblyVersion("9.0.0.0")]
                    namespace ForwardedSignature
                    {
                        public class Value { }
                        public interface IGenericUse<T> { T Convert(T value); }
                        public sealed class WrongWorker : IGenericUse<Value>
                        {
                            public Value Convert(Value value) => value;
                        }
                    }
                    """);
                File.AppendAllLines(project.RootResponsePath,
                    new[] { $"-r:\"{Relative(project.RootPath, wrongPath)}\"" }, encoding: new System.Text.UTF8Encoding(false));
            }
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立两层门面连续转交到真实类型的工程。
        /// <summary>
        /// 建立外层门面、中间门面和最终类型的三段材料。
        /// </summary>
        public static TestProject CreateWithTwoForwardingFacades()
        {
            TestProject project = Create(includeDependency: false);
            string middleFacadePath = Path.Combine(project.RootPath, "MiddleFacade.dll");

            WriteAssembly(
                project.ForwardTargetPath,
                "FinalImplementation",
                "namespace ForwardedNamespace { public class ForwardedType { "
                    + "public virtual void Touch() { } } }");
            WriteAssembly(
                middleFacadePath,
                "MiddleFacade",
                "namespace ForwardedNamespace { public sealed class ForwardedType { } }");
            WriteAssembly(
                project.ExternalReferencePath,
                "OuterFacade",
                "namespace ForwardedNamespace { public class ForwardedType { "
                    + "public virtual void Touch() { } } }");
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace ForwardedConsumer { public sealed class Derived "
                    + ": ForwardedNamespace.ForwardedType { "
                    + "public override void Touch() { } "
                    + "public void Call(ForwardedNamespace.ForwardedType target) { "
                    + "target.Touch(); } } }",
                project.ExternalReferencePath);
            File.Delete(project.ExternalReferencePath);
            WriteAssembly(
                project.ExternalReferencePath,
                "OuterFacade",
                "using System.Runtime.CompilerServices; using ForwardedNamespace; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                middleFacadePath);
            File.Delete(middleFacadePath);
            WriteAssembly(
                middleFacadePath,
                "MiddleFacade",
                "using System.Runtime.CompilerServices; using ForwardedNamespace; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                project.ForwardTargetPath);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立外层和中间门面相互转交的错误工程。
        /// <summary>
        /// 建立可由真实继承关系触发的两节点转交循环。
        /// </summary>
        public static TestProject CreateWithForwardingCycle()
        {
            TestProject project = CreateWithTwoForwardingFacades();
            string middleFacadePath = Path.Combine(project.RootPath, "MiddleFacade.dll");
            string outerDefinitionPath = Path.Combine(project.RootPath, "OuterDefinition.dll");

            WriteAssembly(
                outerDefinitionPath,
                "OuterFacade",
                "namespace ForwardedNamespace { public sealed class ForwardedType { } }");
            File.Delete(middleFacadePath);
            WriteAssembly(
                middleFacadePath,
                "MiddleFacade",
                "using System.Runtime.CompilerServices; using ForwardedNamespace; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                outerDefinitionPath);
            File.Delete(outerDefinitionPath);

            return project;
        }

        // 建立外层和最终程序集同名但版本不同的转交链。
        /// <summary>
        /// 建立需要同时使用类型逻辑身份和完整程序集身份的三段材料。
        /// </summary>
        public static TestProject CreateWithSameNameVersionedForwardingFacades()
        {
            TestProject project = Create(includeDependency: false);
            string middleFacadePath = Path.Combine(project.RootPath, "MiddleFacade.dll");

            WriteAssembly(
                project.ForwardTargetPath,
                "VersionedFacade",
                "using System.Reflection; [assembly: AssemblyVersion(\"4.0.0.0\")] "
                    + "namespace ForwardedNamespace { public class ForwardedType { "
                    + "public virtual void Touch() { } } }");
            WriteAssembly(
                middleFacadePath,
                "MiddleFacade",
                "namespace ForwardedNamespace { public sealed class ForwardedType { } }");
            WriteAssembly(
                project.ExternalReferencePath,
                "VersionedFacade",
                "using System.Reflection; [assembly: AssemblyVersion(\"4.1.3.0\")] "
                    + "namespace ForwardedNamespace { public class ForwardedType { "
                    + "public virtual void Touch() { } } }");
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace ForwardedConsumer { public sealed class Derived "
                    + ": ForwardedNamespace.ForwardedType { "
                    + "public override void Touch() { } public void Call(ForwardedNamespace.ForwardedType target) { target.Touch(); } } }",
                project.ExternalReferencePath);
            File.Delete(project.ExternalReferencePath);
            WriteAssembly(
                project.ExternalReferencePath,
                "VersionedFacade",
                "using System.Reflection; using System.Runtime.CompilerServices; "
                    + "using ForwardedNamespace; [assembly: AssemblyVersion(\"4.1.3.0\")] "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                middleFacadePath);
            File.Delete(middleFacadePath);
            WriteAssembly(
                middleFacadePath,
                "MiddleFacade",
                "using System.Runtime.CompilerServices; using ForwardedNamespace; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
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
                    + "public virtual void Touch() { } public void BaseOnly() { } } "
                    + "public class GenericBase<T> { "
                    + "public virtual TValue Echo<TValue>(TValue value) => value; } }");
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace TransitiveSamples { public sealed class DerivedType : BaseType { "
                    + "public override void Touch() { } "
                    + "public void Call(BaseType target) { target.Touch(); } "
                    + "public void CallInherited(DerivedType target) { target.BaseOnly(); } } "
                    + "public sealed class GenericDerived : GenericBase<int> { "
                    + "public override TValue Echo<TValue>(TValue value) => value; } }",
                project.ForwardTargetPath);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立直接动态链接库引用间接类型转交门面的工程。
        /// <summary>
        /// 建立只有实际命中函数时才载入门面及其转交目标的测试工程。
        /// </summary>
        public static TestProject CreateWithTransitiveForwardedDependency()
        {
            TestProject project = Create(includeDependency: false);

            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalReferencePath,
                "Facade",
                "namespace ForwardedNamespace { public sealed class ForwardedType { "
                    + "public void Touch() { } } }");
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace TransitiveForwarderSamples { public sealed class Consumer { "
                    + "public void Call(ForwardedNamespace.ForwardedType target) { "
                    + "target.Touch(); } } }",
                project.ExternalReferencePath);
            WriteAssembly(
                project.ForwardTargetPath,
                "ForwardTarget",
                "namespace ForwardedNamespace { public sealed class ForwardedType { "
                    + "public void Touch() { } } }");
            File.Delete(project.ExternalReferencePath);
            WriteAssembly(
                project.ExternalReferencePath,
                "Facade",
                "using System.Runtime.CompilerServices; using ForwardedNamespace; "
                    + "[assembly: TypeForwardedTo(typeof(ForwardedType))]",
                project.ForwardTargetPath);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立已索引错版本目标的延迟类型转交工程。
        /// <summary>
        /// 验证类型转交按完整程序集身份选择真实目标。
        /// </summary>
        public static TestProject CreateWithVersionedForwarderCollision()
        {
            TestProject project = CreateWithTransitiveForwardedDependency();
            AddVersionedAssemblyReference(
                project,
                "WrongForwardTarget.dll",
                "namespace ForwardedNamespace { public sealed class ForwardedType { "
                    + "public void Touch() { } } }");

            return project;
        }

        // 建立已索引错版本基类的延迟继承工程。
        /// <summary>
        /// 验证继承关系按完整程序集身份选择真实基类。
        /// </summary>
        public static TestProject CreateWithVersionedInheritanceCollision()
        {
            TestProject project = CreateWithTransitiveExternalDependency();
            RewriteCallDeclaringType(
                project.ExternalAssemblyPath,
                "TransitiveSamples.DerivedType",
                "CallInherited",
                "BaseOnly",
                "TransitiveSamples.DerivedType");
            AddVersionedAssemblyReference(
                project,
                "WrongForwardTarget.dll",
                "namespace TransitiveSamples { public class BaseType { "
                    + "public virtual void Touch() { } public void BaseOnly() { } } }");

            return project;
        }

        // 建立类成员引用同时存在基类和接口的工程。
        /// <summary>
        /// 建立普通类继承查找不应受接口影响的真实 DLL。
        /// </summary>
        public static TestProject CreateWithInheritedClassCall(bool removeInterface, bool virtualCall = false,
            bool explicitInterface = false, bool explicitClassOverride = false)
        {
            TestProject project = Create(includeDependency: false);
            string basePath = Path.Combine(project.RootPath, "BaseLibrary.dll");
            string interfacePath = Path.Combine(project.RootPath, "InterfaceLibrary.dll");

            WriteAssembly(
                basePath,
                "BaseLibrary",
                "namespace InheritedSamples { public class BaseType { public "
                    + (virtualCall ? "virtual " : string.Empty) + "void Run() { } } }");
            WriteAssembly(
                interfacePath,
                "InterfaceLibrary",
                "namespace InheritedSamples { public interface IRun { void Run(); } }");
            File.Delete(project.ExternalAssemblyPath);
            WriteAssembly(
                project.ExternalAssemblyPath,
                "External",
                "namespace InheritedSamples { public sealed class DerivedType : BaseType, IRun { "
                    + (explicitClassOverride ? "public override void Run() { } " : string.Empty)
                    + (explicitInterface ? "void IRun.Run() { } " : string.Empty) + "} "
                    + "public static class Caller { public static void Call(DerivedType target) { "
                    + "target.Run(); } } }",
                basePath,
                interfacePath);
            RewriteCallDeclaringType(
                project.ExternalAssemblyPath,
                "InheritedSamples.Caller",
                "Call",
                "Run",
                "InheritedSamples.DerivedType");
            if (explicitClassOverride)
            {
                using MemoryStream stream = new(File.ReadAllBytes(project.ExternalAssemblyPath));
                using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(stream);
                Mono.Cecil.TypeDefinition derived = module.GetType("InheritedSamples.DerivedType");
                Mono.Cecil.MethodDefinition implementation = derived.Methods.Single(method => method.Name == "Run");
                implementation.Overrides.Add(new Mono.Cecil.MethodReference("Run", module.TypeSystem.Void, derived.BaseType) { HasThis = true });
                implementation.Name = "Implementation";
                module.Write(project.ExternalAssemblyPath);
            }
            if (removeInterface)
            {
                File.Delete(interfacePath);
            }

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
            File.AppendAllLines(
                this.RootResponsePath,
                new[]
                {
                    $"-r:\"{Relative(this.RootPath, secondFacadePath)}\"",
                    $"-r:\"{Relative(this.RootPath, secondTargetPath)}\"",
                }, encoding: new System.Text.UTF8Encoding(false));
            AppendCoreLibraryReference(this);
        }

        // 建立转交记录与目标程序集身份不一致的最小工程。
        public static TestProject CreateWithMismatchedForwardTarget()
        {
            TestProject project = CreateWithForwardedType();

            WriteAssembly(
                project.MismatchedForwardConsumerPath,
                "MismatchedForwardConsumer",
                "namespace ForwardedConsumer { public sealed class Derived "
                    + ": ForwardedNamespace.ForwardedType { public override int Read() => 2; } }",
                project.ExternalAssemblyPath,
                project.ForwardTargetPath);
            File.Delete(project.ForwardTargetPath);
            WriteAssembly(
                project.ForwardTargetPath,
                "WrongForwardTarget",
                "public sealed class WrongType { }");
            File.AppendAllLines(project.RootResponsePath,
                new[] { $"-r:\"{Relative(project.RootPath, project.MismatchedForwardConsumerPath)}\"" }, encoding: new System.Text.UTF8Encoding(false));

            return project;
        }

        // 为目录公开输入增加另一编译核心库及材料阶段已证明的实际载体对应。
        public async Task<MaterialSet> LoadWithAlternateCoreLibraryAsync(bool shareRuntime)
        {
            MaterialSet material = await new MaterialLoader().LoadAsync(new MaterialRequest(this.AssemblyDefinitionPath, 2));
            string referencePath = Path.Combine(this.RootPath, "AlternateCore.dll");
            string referenceIdentity;
            using (Mono.Cecil.AssemblyDefinition assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(object).Assembly.Location))
            {
                assembly.Name.Version = new Version(90, 0, 0, 0);
                assembly.MainModule.Attributes |= Mono.Cecil.ModuleAttributes.ILOnly;
                referenceIdentity = assembly.Name.FullName;
                assembly.Write(referencePath);
            }

            string sourcePath = Path.Combine(this.RootPath, "AlternateSource.cs");
            string source = "public sealed class AlternateRoot { }";
            File.WriteAllText(sourcePath, source, encoding: new System.Text.UTF8Encoding(false));
            CSharpCompilation compilation = CSharpCompilation.Create("AlternateSource",
                new[] { CSharpSyntaxTree.ParseText(source, path: sourcePath) },
                new[] { MetadataReference.CreateFromFile(referencePath) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using MemoryStream image = new();
            Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
            if (!emitted.Success)
            {
                throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics));
            }

            byte[] bytes = image.ToArray();
            SourceAssemblyMaterial additional = new("AlternateSource", false, compilation, new[] { sourcePath },
                Array.Empty<string>(), Path.Combine(this.RootPath, "AlternateSource.dll"), new(() => new(bytes, CompilationOrigin.Built, TimeSpan.Zero)));
            Dictionary<string, string> redirects = new(material.AssemblyRedirects, StringComparer.OrdinalIgnoreCase);
            if (shareRuntime)
            {
                redirects.Add(referenceIdentity, typeof(object).Assembly.Location);
            }

            return material with
            {
                SourceAssemblies = material.SourceAssemblies.Append(additional).ToArray(),
                AssemblyRedirects = redirects,
                ExternalAssemblies = shareRuntime ? material.ExternalAssemblies
                    : material.ExternalAssemblies.Append(new ExternalAssemblyMaterial(referencePath, new[] { referencePath })).ToArray(),
            };
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

            File.WriteAllText(assemblyDefinitionPath, "{\"name\":\"khengine.runtime\"}", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(rootPath, "Packages", "khengine", "package.json"),
                "{\"name\":\"khengine\"}", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(rootSourcePath, "public sealed class RootType { }", encoding: new System.Text.UTF8Encoding(false));
            File.WriteAllText(dependencySourcePath, "public sealed class DependencyType { }", encoding: new System.Text.UTF8Encoding(false));
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
            WriteBuildGraph(this.RootPath, directory);

            return path;
        }

        // 替换根程序集源码以建立当前模块的最小写法。
        /// <summary>
        /// 用指定源码替换测试工程的根程序集源码。
        /// </summary>
        public void WriteRootSource(string source)
        {
            File.WriteAllText(this.RootSourcePath, source, encoding: new System.Text.UTF8Encoding(false));
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
            File.WriteAllLines(responsePath, lines, encoding: new System.Text.UTF8Encoding(false));
            WriteBuildGraph(rootPath, Path.GetDirectoryName(responsePath)!);
        }

        // 夹具按实际响应文件生成同一次 Unity Csc 构建节点，不混入其他任务的响应文件。
        private static void WriteBuildGraph(string rootPath, string directory)
        {
            var nodes = Directory.EnumerateFiles(directory, "*.rsp").Select(response =>
            {
                CSharpCommandLineArguments arguments = CSharpCommandLineParser.Default.Parse(new[] { "@" + response }, rootPath, null);
                string output = Path.Combine(arguments.OutputDirectory, arguments.OutputFileName!);
                return new
                {
                    Annotation = "Csc " + Relative(rootPath, output),
                    Inputs = arguments.MetadataReferences.Select(reference => reference.Reference).Append(response).ToArray(),
                    Outputs = new[] { output, Path.ChangeExtension(output, ".ref.dll") },
                };
            }).ToArray();
            File.WriteAllText(Path.Combine(rootPath, "Library", "Bee", Path.GetFileName(directory) + ".json"),
                System.Text.Json.JsonSerializer.Serialize(new { Nodes = nodes }), encoding: new System.Text.UTF8Encoding(false));
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

        // 把一份错版本同名程序集加入根编译引用。
        private static void AddVersionedAssemblyReference(
            TestProject project,
            string fileName,
            string source)
        {
            string path = Path.Combine(project.RootPath, fileName);
            WriteAssembly(
                path,
                "ForwardTarget",
                "using System.Reflection; [assembly: AssemblyVersion(\"9.0.0.0\")] " + source);
            File.AppendAllLines(
                project.RootResponsePath,
                new[] { $"-r:\"{Relative(project.RootPath, path)}\"" }, encoding: new System.Text.UTF8Encoding(false));
        }

        // 同时纳入改名的显式项目库和另一版本的 Unity 运行库，复刻 Newtonsoft 竞争载体。
        public static TestProject CreateWithLegacyCarrierCollision(bool explicitReference = true)
        {
            TestProject project = CreateWithUnityFrameworkVersionRemapping();
            string projectLibrary = Path.Combine(project.RootPath, "ProjectFramework.dll");
            WriteAssembly(projectLibrary, "UnityFramework", """
                using System.Reflection;
                [assembly: AssemblyVersion("4.2.0.0")]
                namespace UnityFrameworkTypes { public class BaseType { public virtual int Read() => 9; } }
                """);
            if (explicitReference)
            {
                File.AppendAllLines(project.RootResponsePath, new[] { $"-r:shadow=\"{projectLibrary}\"" }, encoding: new System.Text.UTF8Encoding(false));
            }
            return project;
        }

        // 当前构建中的源码程序集与 Unity 运行库使用同名，但不从根程序集引用它。
        public static TestProject CreateWithLegacySourceCollision()
        {
            TestProject project = CreateWithUnityFrameworkVersionRemapping();
            string directory = Path.GetDirectoryName(project.RootResponsePath)!;
            string source = Path.Combine(project.RootPath, "UnityFramework.cs");
            File.WriteAllText(source, "namespace UnityFrameworkTypes { public class BaseType { public virtual int Read() => 3; } }", encoding: new System.Text.UTF8Encoding(false));
            WriteResponse(project.RootPath, Path.Combine(directory, "UnityFramework.rsp"), "UnityFramework", source,
                new[] { typeof(object).Assembly.Location }, Array.Empty<string>());
            return project;
        }

        // 实际转交目标改名放在项目旁边；仅显式引用时才允许按元数据名预载。
        public static TestProject CreateWithLegacyRenamedForwarder(bool explicitReference)
        {
            TestProject project = CreateWithUnityRuntimeCandidate(runtimeIsForwardingFacade: true);
            string renamed = Path.Combine(project.RootPath, "Renamed.dll");
            File.Move(project.UnityRuntimeTargetPath, renamed);
            if (explicitReference)
            {
                File.AppendAllLines(project.RootResponsePath, new[] { $"-r:\"{renamed}\"" }, encoding: new System.Text.UTF8Encoding(false));
            }
            return project;
        }

        // 显式改名库仍可通过其物理文件名被运行时查找。
        public static TestProject CreateWithLegacyFileNameRequest()
        {
            TestProject project = CreateWithLegacyCarrierCollision();
            string rewritten = project.ExternalAssemblyPath + ".rewritten";
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(project.ExternalAssemblyPath,
                       new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                module.AssemblyReferences.Single(reference => reference.Name == "UnityFramework").Name = "ProjectFramework";
                module.Write(rewritten);
            }
            File.Move(rewritten, project.ExternalAssemblyPath, overwrite: true);
            return project;
        }

        // 用真实版本资源复刻已审计的 Unity 安装身份，不执行测试宿主。
        private static void WriteUnityHostEvidence(TestProject project, string fileVersion = "2021.3.16.0")
        {
            WriteAssemblyFromReferences(Path.Combine(project.RootPath, "Unity.exe"), "Unity", $$"""
                using System.Reflection;
                [assembly: AssemblyFileVersion("{{fileVersion}}")]
                [assembly: AssemblyInformationalVersion("2021.3.16f1_0")]
                public sealed class UnityHost { }
                """, new[] { typeof(object).Assembly.Location }, includeVersionResource: true);
            string runtimeDirectory = Path.Combine(project.RootPath, "Data", "MonoBleedingEdge", "EmbedRuntime");
            Directory.CreateDirectory(runtimeDirectory);
            File.WriteAllBytes(Path.Combine(runtimeDirectory, "mono-2.0-bdwgc.dll"), Array.Empty<byte>());
        }

        // 写入用于选中当前Mono运行版本的核心程序集。
        private static void WriteUnityRuntimeCoreLibrary(TestProject project)
        {
            string path = Path.Combine(project.RootPath, "Data", "MonoBleedingEdge", "lib", "mono",
                "unityjit-win32", "mscorlib.dll");
            WriteAssembly(path, "mscorlib",
                "using System.Reflection; [assembly: AssemblyVersion(\"4.0.0.0\")] public sealed class RuntimeMarker { }");
        }

        // 改写测试程序集的公钥以构造身份反例。
        private static void RewriteAssemblyPublicKey(string path, byte[] publicKey)
        {
            string outputPath = path + ".token";
            using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(path,
                new Mono.Cecil.ReaderParameters { InMemory = true });
            module.Assembly.Name.PublicKey = publicKey;
            module.Assembly.Name.HasPublicKey = true;
            module.Write(outputPath);
            File.Move(outputPath, path, overwrite: true);
        }

        // 把派生类型的基类引用改回指定程序集的完整身份。
        private static void RewriteBaseTypeAssemblyScope(
            string path,
            string derivedTypeName,
            string assemblyPath)
        {
            string outputPath = path + ".rewritten";
            using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                path,
                new Mono.Cecil.ReaderParameters { InMemory = true });
            using Mono.Cecil.ModuleDefinition assemblyModule = Mono.Cecil.ModuleDefinition.ReadModule(
                assemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true });
            Mono.Cecil.AssemblyNameDefinition identity = assemblyModule.Assembly.Name;
            Mono.Cecil.AssemblyNameReference? scope = module.AssemblyReferences.SingleOrDefault(
                reference => reference.FullName == identity.FullName);
            if (scope == null)
            {
                scope = new Mono.Cecil.AssemblyNameReference(identity.Name, identity.Version)
                {
                    Culture = identity.Culture,
                    PublicKeyToken = identity.PublicKeyToken,
                };
                module.AssemblyReferences.Add(scope);
            }

            Mono.Cecil.TypeDefinition derivedType = module.GetType(derivedTypeName);
            Mono.Cecil.TypeReference originalBaseType = derivedType.BaseType;
            derivedType.BaseType = new Mono.Cecil.TypeReference(
                originalBaseType.Namespace,
                originalBaseType.Name,
                module,
                scope);
            module.Write(outputPath);
            File.Move(outputPath, path, overwrite: true);
        }

        // 向指定程序集注入一条真实类型转交记录。
        private static void AddForwardedType(
            string path,
            string typeNamespace,
            string typeName,
            string targetAssemblyPath)
        {
            string outputPath = path + ".rewritten";
            using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                path,
                new Mono.Cecil.ReaderParameters { InMemory = true });
            using Mono.Cecil.ModuleDefinition targetModule = Mono.Cecil.ModuleDefinition.ReadModule(
                targetAssemblyPath,
                new Mono.Cecil.ReaderParameters { InMemory = true });
            Mono.Cecil.AssemblyNameDefinition identity = targetModule.Assembly.Name;
            Mono.Cecil.AssemblyNameReference scope = new(identity.Name, identity.Version)
            {
                Culture = identity.Culture,
                PublicKeyToken = identity.PublicKeyToken,
            };
            module.AssemblyReferences.Add(scope);
            module.ExportedTypes.Add(new Mono.Cecil.ExportedType(
                typeNamespace,
                typeName,
                module,
                scope)
            {
                Attributes = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Forwarder,
            });
            module.Write(outputPath);
            File.Move(outputPath, path, overwrite: true);
        }

        // 把指定调用点改为由派生类身份引用继承成员。
        private static void RewriteCallDeclaringType(
            string path,
            string callerTypeName,
            string callerMethodName,
            string targetMethodName,
            string declaringTypeName)
        {
            string outputPath = path + ".rewritten";
            using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                path,
                new Mono.Cecil.ReaderParameters { InMemory = true });
            Mono.Cecil.MethodDefinition caller = module.GetType(callerTypeName).Methods.Single(method =>
                method.Name == callerMethodName);
            Mono.Cecil.Cil.Instruction instruction = caller.Body.Instructions.Single(item =>
                item.Operand is Mono.Cecil.MethodReference method && method.Name == targetMethodName);
            Mono.Cecil.MethodReference original = (Mono.Cecil.MethodReference)instruction.Operand;
            Mono.Cecil.MethodReference replacement = new(
                original.Name,
                original.ReturnType,
                module.GetType(declaringTypeName))
            {
                CallingConvention = original.CallingConvention,
                ExplicitThis = original.ExplicitThis,
                HasThis = original.HasThis,
            };
            foreach (Mono.Cecil.ParameterDefinition parameter in original.Parameters)
            {
                replacement.Parameters.Add(new Mono.Cecil.ParameterDefinition(
                    parameter.Name,
                    parameter.Attributes,
                    parameter.ParameterType));
            }

            instruction.Operand = replacement;
            module.Write(outputPath);
            File.Move(outputPath, path, overwrite: true);
        }

        // 编译类型定义或类型转交测试程序集。
        private static void WriteAssembly(
            string path,
            string assemblyName,
            string source,
            params string[] additionalReferences)
        {
            IEnumerable<string> platformPaths = ((string)AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            WriteAssemblyFromReferences(path, assemblyName, source, platformPaths.Concat(additionalReferences));
        }

        // 使用指定编译环境的真实引用生成测试 DLL，不混入测试进程的另一套核心库。
        private static void WriteAssemblyFromReferences(string path, string assemblyName, string source,
            IEnumerable<string> referencePaths, bool includeVersionResource = false)
        {
            PortableExecutableReference[] references = referencePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(item => MetadataReference.CreateFromFile(item))
                .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using FileStream stream = File.Create(path);
            using Stream? resources = includeVersionResource ? compilation.CreateDefaultWin32Resources(true, true, null, null) : null;
            Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream, win32Resources: resources);

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
                new[] { $"-r:\"{typeof(object).Assembly.Location}\"" }, encoding: new System.Text.UTF8Encoding(false));
        }
    }
}
