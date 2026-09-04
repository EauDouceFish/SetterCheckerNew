using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Metadata = System.Reflection.Metadata;

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
                """);
            AppendCoreLibraryReference(project);

            return project;
        }

        // 建立包含双自定义修饰符的真实托管基类工程。
        /// <summary>
        /// 建立用于核对源码与托管签名修饰符顺序的工程。
        /// </summary>
        public static TestProject CreateWithCustomModifiers()
        {
            TestProject project = Create(includeDependency: false);
            string modifierPath = Path.Combine(project.RootPath, "ModifierLib.dll");
            string basePath = Path.Combine(project.RootPath, "CustomBase.dll");
            string pointerPath = Path.Combine(project.RootPath, "PointerSignatures.dll");
            string purePointerPath = Path.Combine(project.RootPath, "PurePointerContract.dll");
            string callConventionFacadePath = Path.Combine(
                project.RootPath,
                "CallConventionFacade.dll");
            string bangAssemblyPath = Path.Combine(project.RootPath, "Bang!0.dll");
            string delimiterTargetPath = Path.Combine(project.RootPath, "DelimiterTarget.dll");
            string delimiterFacadePath = Path.Combine(project.RootPath, "DelimiterFacade.dll");

            WriteAssembly(
                modifierPath,
                "ModifierLib",
                "namespace Mods { public sealed class First { } public sealed class Second { } "
                    + "public sealed class RefFirst { } public sealed class RefSecond { } "
                    + "public sealed class TypeFirst { } public sealed class TypeSecond { } }");
            WriteAssembly(
                callConventionFacadePath,
                "CallConventionFacade",
                "[assembly: System.Runtime.CompilerServices.TypeForwardedTo("
                    + "typeof(System.Runtime.CompilerServices.CallConvSuppressGCTransition))]");
            WriteAssembly(
                bangAssemblyPath,
                "Bang!0",
                "namespace Bang { public sealed class Value { } }");
            WriteDelimiterAssemblies(delimiterTargetPath, delimiterFacadePath);
            WriteCustomModifierBase(basePath);
            WriteAssembly(
                pointerPath,
                "PointerSignatures",
                "public unsafe sealed class ManagedPointerSignatures { "
                    + "public delegate* unmanaged[Cdecl]<void> Echo("
                    + "delegate* unmanaged[Cdecl]<void> value) => value; "
                    + "public delegate* unmanaged[SuppressGCTransition]<void> Suppress("
                    + "delegate* unmanaged[SuppressGCTransition]<void> value) => value; }",
                allowUnsafe: true);
            WriteAssembly(
                purePointerPath,
                "PurePointerContract",
                "public unsafe interface IPurePointerContract { "
                    + "void Accept(delegate* unmanaged[SuppressGCTransition]<void> value); }",
                allowUnsafe: true);
            File.AppendAllLines(
                project.RootResponsePath,
                new[]
                {
                    "-unsafe",
                    $"-r:\"{Relative(project.RootPath, modifierPath)}\"",
                    $"-r:\"{Relative(project.RootPath, basePath)}\"",
                    $"-r:\"{Relative(project.RootPath, pointerPath)}\"",
                    $"-r:\"{Relative(project.RootPath, purePointerPath)}\"",
                    $"-r:\"{Relative(project.RootPath, callConventionFacadePath)}\"",
                    $"-r:\"{Relative(project.RootPath, bangAssemblyPath)}\"",
                    $"-r:\"{Relative(project.RootPath, delimiterFacadePath)}\"",
                    $"-r:\"{Relative(project.RootPath, delimiterTargetPath)}\"",
                });

            return project;
        }

        // 建立程序集名与类型名分界可能产生相同裸字符串的真实托管工程。
        /// <summary>
        /// 建立两个合法命名组合及只实现其中一个接口的工程。
        /// </summary>
        public static TestProject CreateWithTypeIdentityCollision()
        {
            TestProject project = Create(includeDependency: false);
            string firstContractPath = Path.Combine(project.RootPath, "FirstContract.dll");
            string secondContractPath = Path.Combine(project.RootPath, "SecondContract.dll");
            string implementationPath = Path.Combine(project.RootPath, "CollisionImplementation.dll");

            WriteTypeIdentityCollisionAssemblies(
                firstContractPath,
                secondContractPath,
                implementationPath);
            File.AppendAllLines(
                project.RootResponsePath,
                new[]
                {
                    $"-r:\"{Relative(project.RootPath, firstContractPath)}\"",
                    $"-r:\"{Relative(project.RootPath, secondContractPath)}\"",
                    $"-r:\"{Relative(project.RootPath, implementationPath)}\"",
                });

            return project;
        }

        // 建立两条物理路径指向同一程序集身份的外部文件。
        /// <summary>
        /// 建立两条物理路径指向同一程序集身份的工程。
        /// </summary>
        public static TestProject CreateWithDuplicateExternalIdentity()
        {
            TestProject project = CreateWithExternalMethods();
            string secondPath = Path.Combine(project.RootPath, "ExternalCopy.dll");

            File.Copy(project.ExternalAssemblyPath, secondPath);
            File.AppendAllLines(
                project.RootResponsePath,
                new[] { $"-r:\"{Relative(project.RootPath, secondPath)}\"" });

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

        // 写入程序集简单名称只差大小写的两个真实托管文件。
        /// <summary>
        /// 建立都声明同一类型和函数、但程序集定义名称分别为 A 与 a 的文件。
        /// </summary>
        /// <returns>返回大写定义文件和小写定义文件的路径。</returns>
        public (string UpperPath, string LowerPath) WriteAssemblyCaseCollisionFiles()
        {
            string upperPath = Path.Combine(this.RootPath, "UpperCaseAssembly.dll");
            string lowerPath = Path.Combine(this.RootPath, "LowerCaseAssembly.dll");
            const string source = """
                namespace CaseIdentity
                {
                    public sealed class SharedType
                    {
                        // 声明两个物理程序集共有的普通函数。
                        public void Touch() { }
                    }
                }
                """;

            WriteAssembly(upperPath, "A", source);
            WriteAssembly(lowerPath, "a", source);

            return (upperPath, lowerPath);
        }

        // 写入 MethodBody 使用当前实现类型成员引用的合法 MethodImpl 文件。
        /// <summary>
        /// 建立接口声明仍为函数定义、实现函数通过成员引用填写 MethodBody 的托管文件。
        /// </summary>
        /// <returns>返回生成的托管文件路径。</returns>
        public string WriteMemberReferenceMethodBodyAssembly()
        {
            string path = Path.Combine(this.RootPath, "MemberReferenceMethodBody.dll");
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("MemberReferenceMethodBody", new Version(1, 0, 0, 0)),
                "MemberReferenceMethodBody",
                ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition contract = new(
                string.Empty,
                "IContract",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            MethodDefinition declaration = new(
                "Describe",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Abstract
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.NewSlot,
                module.TypeSystem.String);
            contract.Methods.Add(declaration);
            module.Types.Add(contract);
            TypeDefinition implementation = new(
                string.Empty,
                "Implementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            implementation.Interfaces.Add(new InterfaceImplementation(contract));
            MethodDefinition body = new(
                "Body",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.Final
                    | Mono.Cecil.MethodAttributes.NewSlot,
                module.TypeSystem.String);
            MethodReference toString = module.ImportReference(
                typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
            ILProcessor processor = body.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Callvirt, toString);
            processor.Emit(OpCodes.Ret);
            body.Overrides.Add(declaration);
            implementation.Methods.Add(body);
            module.Types.Add(implementation);
            assembly.Write(path);

            byte[] image = File.ReadAllBytes(path);
            using (FileStream stream = File.OpenRead(path))
            using (PEReader portableExecutable = new(stream))
            {
                Metadata.MetadataReader reader = Metadata.PEReaderExtensions.GetMetadataReader(
                    portableExecutable);
                Metadata.TypeDefinitionHandle implementationHandle = reader.TypeDefinitions.Single(
                    handle => reader.GetString(reader.GetTypeDefinition(handle).Name) == "Implementation");
                Metadata.MethodImplementation methodImplementation = reader.GetMethodImplementation(
                    reader.GetTypeDefinition(implementationHandle).GetMethodImplementations().Single());
                Metadata.MethodDefinitionHandle bodyHandle =
                    (Metadata.MethodDefinitionHandle)methodImplementation.MethodBody;
                Metadata.MemberReferenceHandle memberHandle = reader.MemberReferences.Single(handle =>
                    reader.GetString(reader.GetMemberReference(handle).Name) == nameof(ToString));
                PatchMethodImplementationBody(
                    image,
                    reader,
                    implementationHandle,
                    methodImplementation,
                    memberHandle);
                ushort classIndex = checked((ushort)MetadataTokens.GetRowNumber(implementationHandle));
                Metadata.MemberReference member = reader.GetMemberReference(memberHandle);
                Metadata.TypeReferenceHandle objectHandle =
                    (Metadata.TypeReferenceHandle)member.Parent;
                ushort objectParent = checked((ushort)(
                    (MetadataTokens.GetRowNumber(objectHandle) << 3) | 1));
                ushort oldName = checked((ushort)MetadataTokens.GetHeapOffset(member.Name));
                ushort signature = checked((ushort)MetadataTokens.GetHeapOffset(member.Signature));
                byte[] memberRow =
                {
                    (byte)objectParent,
                    (byte)(objectParent >> 8),
                    (byte)oldName,
                    (byte)(oldName >> 8),
                    (byte)signature,
                    (byte)(signature >> 8),
                };
                int memberOffset = Enumerable.Range(0, image.Length - memberRow.Length + 1)
                    .Single(index => image.AsSpan(index, memberRow.Length).SequenceEqual(memberRow));
                ushort implementationParent = checked((ushort)(classIndex << 3));
                ushort bodyName = checked((ushort)MetadataTokens.GetHeapOffset(
                    reader.GetMethodDefinition(bodyHandle).Name));
                image[memberOffset] = (byte)implementationParent;
                image[memberOffset + 1] = (byte)(implementationParent >> 8);
                image[memberOffset + 2] = (byte)bodyName;
                image[memberOffset + 3] = (byte)(bodyName >> 8);
            }

            File.WriteAllBytes(path, image);

            return path;
        }

        // 写入泛型当前类型成员引用及错误外部类型成员引用的 MethodImpl 文件。
        /// <summary>
        /// 建立泛型 TypeSpec MethodBody 的合法文件和父级不是当前类型的负例文件。
        /// </summary>
        /// <returns>返回合法文件和错误父级文件的路径。</returns>
        public (string ValidPath, string ForeignParentPath)
            WriteGenericMemberReferenceMethodBodyAssemblies()
        {
            string originalPath = Path.Combine(this.RootPath, "GenericMemberReferenceOriginal.dll");
            string validPath = Path.Combine(this.RootPath, "GenericMemberReferenceBody.dll");
            string foreignParentPath = Path.Combine(
                this.RootPath,
                "GenericForeignMemberReferenceBody.dll");
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("GenericMemberReferenceBody", new Version(1, 0, 0, 0)),
                "GenericMemberReferenceBody",
                ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition contract = new(
                string.Empty,
                "IContract`1",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            GenericParameter contractParameter = new("T", contract);
            contract.GenericParameters.Add(contractParameter);
            MethodDefinition declaration = new(
                "Run",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Abstract
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.NewSlot,
                contractParameter);
            declaration.Parameters.Add(new ParameterDefinition(contractParameter));
            contract.Methods.Add(declaration);
            module.Types.Add(contract);
            TypeDefinition foreignType = new(
                string.Empty,
                "Foreign`1",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            GenericParameter foreignParameter = new("T", foreignType);
            foreignType.GenericParameters.Add(foreignParameter);
            MethodDefinition foreignBody = new(
                "ForeignBody",
                Mono.Cecil.MethodAttributes.Public,
                foreignParameter);
            foreignBody.Parameters.Add(new ParameterDefinition(foreignParameter));
            ILProcessor foreignProcessor = foreignBody.Body.GetILProcessor();
            foreignProcessor.Emit(OpCodes.Ldarg_1);
            foreignProcessor.Emit(OpCodes.Ret);
            foreignType.Methods.Add(foreignBody);
            module.Types.Add(foreignType);
            TypeDefinition implementation = new(
                string.Empty,
                "Implementation`1",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            GenericParameter implementationParameter = new("T", implementation);
            implementation.GenericParameters.Add(implementationParameter);
            GenericInstanceType contractInstance = new(contract);
            contractInstance.GenericArguments.Add(implementationParameter);
            implementation.Interfaces.Add(new InterfaceImplementation(contractInstance));
            MethodDefinition body = new(
                "Body",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.Final
                    | Mono.Cecil.MethodAttributes.NewSlot,
                implementationParameter);
            body.Parameters.Add(new ParameterDefinition(implementationParameter));
            ILProcessor bodyProcessor = body.Body.GetILProcessor();
            bodyProcessor.Emit(OpCodes.Ldarg_1);
            bodyProcessor.Emit(OpCodes.Ret);
            MethodReference declarationReference = new(
                "Run",
                implementationParameter,
                contractInstance)
            {
                HasThis = true,
            };
            declarationReference.Parameters.Add(new ParameterDefinition(implementationParameter));
            body.Overrides.Add(declarationReference);
            implementation.Methods.Add(body);
            GenericInstanceType selfInstance = new(implementation);
            selfInstance.GenericArguments.Add(implementationParameter);
            MethodReference selfBodyReference = new(
                "Body",
                implementationParameter,
                selfInstance)
            {
                HasThis = true,
            };
            selfBodyReference.Parameters.Add(new ParameterDefinition(implementationParameter));
            MethodDefinition selfCaller = new(
                "CallSelf",
                Mono.Cecil.MethodAttributes.Public,
                implementationParameter);
            selfCaller.Parameters.Add(new ParameterDefinition(implementationParameter));
            ILProcessor selfCallerProcessor = selfCaller.Body.GetILProcessor();
            selfCallerProcessor.Emit(OpCodes.Ldarg_0);
            selfCallerProcessor.Emit(OpCodes.Ldarg_1);
            selfCallerProcessor.Emit(OpCodes.Callvirt, selfBodyReference);
            selfCallerProcessor.Emit(OpCodes.Ret);
            implementation.Methods.Add(selfCaller);
            GenericInstanceType foreignInstance = new(foreignType);
            foreignInstance.GenericArguments.Add(implementationParameter);
            MethodReference foreignBodyReference = new(
                "ForeignBody",
                implementationParameter,
                foreignInstance)
            {
                HasThis = true,
            };
            foreignBodyReference.Parameters.Add(new ParameterDefinition(implementationParameter));
            MethodDefinition foreignCaller = new(
                "CallForeign",
                Mono.Cecil.MethodAttributes.Public,
                implementationParameter);
            foreignCaller.Parameters.Add(new ParameterDefinition(implementationParameter));
            ILProcessor foreignCallerProcessor = foreignCaller.Body.GetILProcessor();
            foreignCallerProcessor.Emit(OpCodes.Ldarg_0);
            foreignCallerProcessor.Emit(OpCodes.Ldarg_1);
            foreignCallerProcessor.Emit(OpCodes.Callvirt, foreignBodyReference);
            foreignCallerProcessor.Emit(OpCodes.Ret);
            implementation.Methods.Add(foreignCaller);
            module.Types.Add(implementation);
            assembly.Write(originalPath);

            byte[] originalImage = File.ReadAllBytes(originalPath);
            byte[] validImage = (byte[])originalImage.Clone();
            byte[] foreignImage = (byte[])originalImage.Clone();
            using (FileStream stream = File.OpenRead(originalPath))
            using (PEReader portableExecutable = new(stream))
            {
                Metadata.MetadataReader reader = Metadata.PEReaderExtensions.GetMetadataReader(
                    portableExecutable);
                Metadata.TypeDefinitionHandle implementationHandle = reader.TypeDefinitions.Single(
                    handle => reader.GetString(reader.GetTypeDefinition(handle).Name)
                        == "Implementation`1");
                Metadata.MethodImplementation methodImplementation = reader.GetMethodImplementation(
                    reader.GetTypeDefinition(implementationHandle).GetMethodImplementations().Single());
                Metadata.MemberReferenceHandle selfBodyHandle = reader.MemberReferences.Single(handle =>
                {
                    Metadata.MemberReference member = reader.GetMemberReference(handle);

                    return reader.GetString(member.Name) == "Body"
                        && member.Parent.Kind == Metadata.HandleKind.TypeSpecification;
                });
                Metadata.MemberReferenceHandle foreignBodyHandle = reader.MemberReferences.Single(handle =>
                {
                    Metadata.MemberReference member = reader.GetMemberReference(handle);

                    return reader.GetString(member.Name) == "ForeignBody"
                        && member.Parent.Kind == Metadata.HandleKind.TypeSpecification;
                });
                PatchMethodImplementationBody(
                    validImage,
                    reader,
                    implementationHandle,
                    methodImplementation,
                    selfBodyHandle);
                PatchMethodImplementationBody(
                    foreignImage,
                    reader,
                    implementationHandle,
                    methodImplementation,
                    foreignBodyHandle);
            }

            File.WriteAllBytes(validPath, validImage);
            File.WriteAllBytes(foreignParentPath, foreignImage);

            return (validPath, foreignParentPath);
        }

        // 写入同名同签名实例函数和静态函数共存的 MethodImpl 文件。
        /// <summary>
        /// 建立 MethodImpl 明确指向实例函数定义、旁边存在同签名静态函数的托管文件。
        /// </summary>
        /// <returns>返回生成的托管文件路径。</returns>
        public string WriteStaticAndInstanceMethodBodyAssembly()
        {
            string path = Path.Combine(this.RootPath, "StaticAndInstanceMethodBody.dll");
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("StaticAndInstanceMethodBody", new Version(1, 0, 0, 0)),
                "StaticAndInstanceMethodBody",
                ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition contract = new(
                string.Empty,
                "IContract",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            MethodDefinition declaration = new(
                "Describe",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Abstract
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.NewSlot,
                module.TypeSystem.String);
            contract.Methods.Add(declaration);
            module.Types.Add(contract);
            TypeDefinition implementation = new(
                string.Empty,
                "Implementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            implementation.Interfaces.Add(new InterfaceImplementation(contract));
            MethodDefinition instanceBody = new(
                "Body",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.Final
                    | Mono.Cecil.MethodAttributes.NewSlot,
                module.TypeSystem.String);
            ILProcessor instanceProcessor = instanceBody.Body.GetILProcessor();
            instanceProcessor.Emit(OpCodes.Ldstr, "instance");
            instanceProcessor.Emit(OpCodes.Ret);
            instanceBody.Overrides.Add(declaration);
            implementation.Methods.Add(instanceBody);
            MethodDefinition staticBody = new(
                "Body",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.String);
            ILProcessor staticProcessor = staticBody.Body.GetILProcessor();
            staticProcessor.Emit(OpCodes.Ldstr, "static");
            staticProcessor.Emit(OpCodes.Ret);
            implementation.Methods.Add(staticBody);
            module.Types.Add(implementation);
            assembly.Write(path);

            return path;
        }

        // 把最小夹具中唯一 MethodImpl 行的 MethodBody 改为指定成员引用。
        private static void PatchMethodImplementationBody(
            byte[] image,
            Metadata.MetadataReader reader,
            Metadata.TypeDefinitionHandle implementationHandle,
            Metadata.MethodImplementation methodImplementation,
            Metadata.MemberReferenceHandle memberHandle)
        {
            if (methodImplementation.MethodBody.Kind != Metadata.HandleKind.MethodDefinition
                || methodImplementation.MethodDeclaration.Kind is not Metadata.HandleKind.MethodDefinition
                    and not Metadata.HandleKind.MemberReference)
            {
                throw new InvalidOperationException("测试夹具的原始 MethodImpl 句柄种类不符合预期。");
            }

            ushort classIndex = checked((ushort)MetadataTokens.GetRowNumber(implementationHandle));
            ushort bodyIndex = checked((ushort)(
                MetadataTokens.GetRowNumber(methodImplementation.MethodBody) << 1));
            ushort declarationIndex = checked((ushort)(
                (MetadataTokens.GetRowNumber(methodImplementation.MethodDeclaration) << 1)
                | (methodImplementation.MethodDeclaration.Kind == Metadata.HandleKind.MemberReference
                    ? 1
                    : 0)));
            byte[] methodImplementationRow =
            {
                (byte)classIndex,
                (byte)(classIndex >> 8),
                (byte)bodyIndex,
                (byte)(bodyIndex >> 8),
                (byte)declarationIndex,
                (byte)(declarationIndex >> 8),
            };
            int methodImplementationOffset = Enumerable.Range(
                    0,
                    image.Length - methodImplementationRow.Length + 1)
                .Single(index => image.AsSpan(index, methodImplementationRow.Length)
                    .SequenceEqual(methodImplementationRow));
            ushort memberIndex = checked((ushort)(
                (MetadataTokens.GetRowNumber(memberHandle) << 1) | 1));
            image[methodImplementationOffset + 2] = (byte)memberIndex;
            image[methodImplementationOffset + 3] = (byte)(memberIndex >> 8);
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

        // 写入同时包含类型修饰符和引用修饰符的真实托管基类。
        private static void WriteCustomModifierBase(string path)
        {
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("CustomBase", new Version(1, 0, 0, 0)),
                "CustomBase",
                ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            AssemblyNameReference modifierScope = new("ModifierLib", new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(modifierScope);
            AssemblyNameReference callConventionFacadeScope = new(
                "callconventionfacade",
                new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(callConventionFacadeScope);
            AssemblyNameReference bangAssemblyScope = new("Bang!0", new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(bangAssemblyScope);
            AssemblyNameReference delimiterFacadeScope = new(
                "DelimiterFacade",
                new Version(1, 0, 0, 0));
            AssemblyNameReference delimiterTargetScope = new(
                "DelimiterTarget",
                new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(delimiterFacadeScope);
            module.AssemblyReferences.Add(delimiterTargetScope);

            // 建立一个指向修饰符程序集的类型引用。
            TypeReference Modifier(string name) => new("Mods", name, module, modifierScope, false);
            // 按真实元数据嵌套顺序为类型附加两个普通修饰符。
            TypeReference TypeModifiers(TypeReference type, string first, string second) =>
                new OptionalModifierType(
                    Modifier(first),
                    new OptionalModifierType(Modifier(second), type));
            // 在引用符号两侧分别附加普通修饰符和引用修饰符。
            TypeReference MixedModifiers(TypeReference type) => TypeModifiers(
                new ByReferenceType(TypeModifiers(type, "TypeFirst", "TypeSecond")),
                "RefFirst",
                "RefSecond");
            // 建立使用指定调用约定的无参数函数指针。
            FunctionPointerType FunctionPointer(MethodCallingConvention convention)
            {
                return new FunctionPointerType
                {
                    ReturnType = module.TypeSystem.Void,
                    CallingConvention = convention,
                };
            }
            // 建立函数指针接口及其隐式实现类型。
            void AddFunctionPointerContract(
                string contractName,
                string implementationName,
                TypeReference contractPointer,
                TypeReference implementationPointer)
            {
                TypeDefinition contractType = new(
                    string.Empty,
                    contractName,
                    Mono.Cecil.TypeAttributes.Public
                        | Mono.Cecil.TypeAttributes.Interface
                        | Mono.Cecil.TypeAttributes.Abstract);
                module.Types.Add(contractType);
                MethodDefinition contractMethod = new(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.Virtual
                        | Mono.Cecil.MethodAttributes.HideBySig
                        | Mono.Cecil.MethodAttributes.NewSlot
                        | Mono.Cecil.MethodAttributes.Abstract,
                    module.TypeSystem.Void);
                contractMethod.Parameters.Add(new ParameterDefinition(
                    "value",
                    Mono.Cecil.ParameterAttributes.None,
                    contractPointer));
                contractType.Methods.Add(contractMethod);
                TypeDefinition implementationType = new(
                    string.Empty,
                    implementationName,
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                    module.ImportReference(typeof(object)));
                implementationType.Interfaces.Add(new InterfaceImplementation(contractType));
                module.Types.Add(implementationType);
                MethodDefinition implementationMethod = new(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                    module.TypeSystem.Void);
                implementationMethod.Parameters.Add(new ParameterDefinition(
                    "value",
                    Mono.Cecil.ParameterAttributes.None,
                    implementationPointer));
                implementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);
                implementationType.Methods.Add(implementationMethod);
            }
            // 建立开放泛型接口，验证名字中的感叹号不会被当成泛型参数。
            void AddLiteralNameContract(
                string contractName,
                string implementationName,
                TypeReference valueType,
                TypeReference? implementationValueType = null)
            {
                TypeDefinition contractType = new(
                    string.Empty,
                    $"{contractName}`1",
                    Mono.Cecil.TypeAttributes.Public
                        | Mono.Cecil.TypeAttributes.Interface
                        | Mono.Cecil.TypeAttributes.Abstract);
                contractType.GenericParameters.Add(new GenericParameter("T", contractType));
                module.Types.Add(contractType);
                MethodDefinition contractMethod = new(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.Virtual
                        | Mono.Cecil.MethodAttributes.HideBySig
                        | Mono.Cecil.MethodAttributes.NewSlot
                        | Mono.Cecil.MethodAttributes.Abstract,
                    module.TypeSystem.Void);
                contractMethod.Parameters.Add(new ParameterDefinition(
                    "value",
                    Mono.Cecil.ParameterAttributes.None,
                    valueType));
                contractType.Methods.Add(contractMethod);
                GenericInstanceType implementedContract = new(contractType);
                implementedContract.GenericArguments.Add(module.TypeSystem.Int32);
                TypeDefinition implementationType = new(
                    string.Empty,
                    implementationName,
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                    module.ImportReference(typeof(object)));
                implementationType.Interfaces.Add(new InterfaceImplementation(implementedContract));
                module.Types.Add(implementationType);
                MethodDefinition implementationMethod = new(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                    module.TypeSystem.Void);
                implementationMethod.Parameters.Add(new ParameterDefinition(
                    "value",
                    Mono.Cecil.ParameterAttributes.None,
                    implementationValueType ?? valueType));
                implementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);
                implementationType.Methods.Add(implementationMethod);
            }

            TypeDefinition type = new(
                string.Empty,
                "CustomBaseType",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(type);
            MethodDefinition constructor = new(
                ".ctor",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.SpecialName
                    | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            type.Methods.Add(constructor);
            ILProcessor constructorBody = constructor.Body.GetILProcessor();
            constructorBody.Emit(OpCodes.Ldarg_0);
            constructorBody.Emit(
                OpCodes.Call,
                module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            constructorBody.Emit(OpCodes.Ret);
            Mono.Cecil.MethodAttributes methodAttributes = Mono.Cecil.MethodAttributes.Public
                | Mono.Cecil.MethodAttributes.Virtual
                | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.NewSlot;

            MethodDefinition read = new(
                "Read",
                methodAttributes,
                TypeModifiers(module.TypeSystem.Int32, "First", "Second"));
            type.Methods.Add(read);
            ILProcessor readBody = read.Body.GetILProcessor();
            readBody.Emit(OpCodes.Ldc_I4_0);
            readBody.Emit(OpCodes.Ret);
            MethodDefinition write = new("Write", methodAttributes, module.TypeSystem.Void);
            write.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                TypeModifiers(module.TypeSystem.Int32, "First", "Second")));
            type.Methods.Add(write);
            write.Body.GetILProcessor().Emit(OpCodes.Ret);

            MethodDefinition mixedReturn = new(
                "MixedReturn",
                methodAttributes,
                MixedModifiers(module.TypeSystem.Int32));
            type.Methods.Add(mixedReturn);
            VariableDefinition local = new(module.TypeSystem.Int32);
            mixedReturn.Body.Variables.Add(local);
            ILProcessor mixedReturnBody = mixedReturn.Body.GetILProcessor();
            mixedReturnBody.Emit(OpCodes.Ldloca_S, local);
            mixedReturnBody.Emit(OpCodes.Ret);
            MethodDefinition mixedParameter = new(
                "MixedParameter",
                methodAttributes,
                module.TypeSystem.Void);
            mixedParameter.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                MixedModifiers(module.TypeSystem.Int32)));
            type.Methods.Add(mixedParameter);
            mixedParameter.Body.GetILProcessor().Emit(OpCodes.Ret);

            TypeDefinition closingModifier = new(
                string.Empty,
                "Closing)Modifier",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(closingModifier);
            TypeDefinition closingContract = new(
                string.Empty,
                "IClosingContract",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            module.Types.Add(closingContract);
            MethodDefinition closingContractMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.NewSlot
                    | Mono.Cecil.MethodAttributes.Abstract,
                module.TypeSystem.Void);
            closingContractMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                new OptionalModifierType(closingModifier, module.TypeSystem.Int32)));
            closingContract.Methods.Add(closingContractMethod);
            TypeDefinition closingImplementation = new(
                string.Empty,
                "ClosingImplementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            closingImplementation.Interfaces.Add(new InterfaceImplementation(closingContract));
            module.Types.Add(closingImplementation);
            MethodDefinition closingImplementationMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            closingImplementationMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                module.TypeSystem.Int32));
            closingImplementation.Methods.Add(closingImplementationMethod);
            closingImplementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);

            FunctionPointerType functionPointer = new() { ReturnType = module.TypeSystem.Void };
            TypeDefinition functionContract = new(
                string.Empty,
                "IFunctionModifierContract",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            module.Types.Add(functionContract);
            MethodDefinition functionContractMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.NewSlot
                    | Mono.Cecil.MethodAttributes.Abstract,
                module.TypeSystem.Void);
            functionContractMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                new OptionalModifierType(functionPointer, module.TypeSystem.Int32)));
            functionContract.Methods.Add(functionContractMethod);
            TypeDefinition functionImplementation = new(
                string.Empty,
                "FunctionModifierImplementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            functionImplementation.Interfaces.Add(new InterfaceImplementation(functionContract));
            module.Types.Add(functionImplementation);
            MethodDefinition functionImplementationMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            functionImplementationMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                module.TypeSystem.Int32));
            functionImplementation.Methods.Add(functionImplementationMethod);
            functionImplementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);

            TypeDefinition plain = new(
                string.Empty,
                "Plain",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            TypeDefinition misleading = new(
                string.Empty,
                "Plain modopt(X)",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(plain);
            module.Types.Add(misleading);
            TypeDefinition falseContract = new(
                string.Empty,
                "IFalseContract",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            module.Types.Add(falseContract);
            MethodDefinition falseContractMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.NewSlot
                    | Mono.Cecil.MethodAttributes.Abstract,
                module.TypeSystem.Void);
            falseContractMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                misleading));
            falseContract.Methods.Add(falseContractMethod);
            TypeDefinition falseImplementation = new(
                string.Empty,
                "FalseImplementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            falseImplementation.Interfaces.Add(new InterfaceImplementation(falseContract));
            module.Types.Add(falseImplementation);
            MethodDefinition falseImplementationMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            falseImplementationMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                plain));
            falseImplementation.Methods.Add(falseImplementationMethod);
            falseImplementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);

            TypeDefinition box = new(
                string.Empty,
                "Box`1",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            box.GenericParameters.Add(new GenericParameter("T", box));
            module.Types.Add(box);
            GenericInstanceType modifiedBox = new(box);
            modifiedBox.GenericArguments.Add(new OptionalModifierType(
                Modifier("First"),
                module.TypeSystem.Int32));
            TypeDefinition nestedBase = new(
                string.Empty,
                "NestedModifierBase",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(nestedBase);
            MethodDefinition nestedEcho = new(
                "Echo",
                methodAttributes,
                modifiedBox);
            nestedEcho.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                modifiedBox));
            nestedBase.Methods.Add(nestedEcho);
            ILProcessor nestedEchoBody = nestedEcho.Body.GetILProcessor();
            nestedEchoBody.Emit(OpCodes.Ldarg_1);
            nestedEchoBody.Emit(OpCodes.Ret);

            TypeDefinition genericContract = new(
                string.Empty,
                "IGenericFunctionContract`1",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            GenericParameter genericContractParameter = new("T", genericContract);
            genericContract.GenericParameters.Add(genericContractParameter);
            module.Types.Add(genericContract);
            MethodDefinition genericContractMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.NewSlot
                    | Mono.Cecil.MethodAttributes.Abstract,
                module.TypeSystem.Void);
            genericContractMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                genericContractParameter));
            genericContract.Methods.Add(genericContractMethod);
            TypeDefinition genericChildContract = new(
                string.Empty,
                "IGenericFunctionChild`1",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            GenericParameter genericChildParameter = new("T", genericChildContract);
            genericChildContract.GenericParameters.Add(genericChildParameter);
            GenericInstanceType inheritedGenericContract = new(genericContract);
            inheritedGenericContract.GenericArguments.Add(genericChildParameter);
            genericChildContract.Interfaces.Add(new InterfaceImplementation(
                inheritedGenericContract));
            module.Types.Add(genericChildContract);
            FunctionPointerType modifiedGenericPointer = FunctionPointer(
                MethodCallingConvention.Default);
            modifiedGenericPointer.ReturnType = new OptionalModifierType(
                Modifier("First"),
                module.TypeSystem.Void);
            modifiedGenericPointer.Parameters.Add(new ParameterDefinition(
                new OptionalModifierType(Modifier("Second"), module.TypeSystem.Int32)));
            modifiedGenericPointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            FunctionPointerType plainGenericPointer = FunctionPointer(
                MethodCallingConvention.Default);
            plainGenericPointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            plainGenericPointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            GenericInstanceType implementedGenericContract = new(genericChildContract);
            implementedGenericContract.GenericArguments.Add(modifiedGenericPointer);
            TypeDefinition genericImplementation = new(
                string.Empty,
                "GenericFunctionImplementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            genericImplementation.Interfaces.Add(new InterfaceImplementation(
                implementedGenericContract));
            module.Types.Add(genericImplementation);
            MethodDefinition genericImplementationMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            genericImplementationMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                plainGenericPointer));
            genericImplementation.Methods.Add(genericImplementationMethod);
            genericImplementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);

            FunctionPointerType cdeclPointer = FunctionPointer(MethodCallingConvention.C);
            FunctionPointerType stdcallPointer = FunctionPointer(MethodCallingConvention.StdCall);
            TypeDefinition callConventionContract = new(
                string.Empty,
                "ICallConventionContract",
                Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Interface
                    | Mono.Cecil.TypeAttributes.Abstract);
            module.Types.Add(callConventionContract);
            MethodDefinition callConventionContractMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.NewSlot
                    | Mono.Cecil.MethodAttributes.Abstract,
                module.TypeSystem.Void);
            callConventionContractMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                cdeclPointer));
            callConventionContract.Methods.Add(callConventionContractMethod);
            TypeDefinition wrongCallConvention = new(
                string.Empty,
                "WrongCallConventionImplementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            wrongCallConvention.Interfaces.Add(new InterfaceImplementation(callConventionContract));
            module.Types.Add(wrongCallConvention);
            MethodDefinition wrongCallConventionMethod = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            wrongCallConventionMethod.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                stdcallPointer));
            wrongCallConvention.Methods.Add(wrongCallConventionMethod);
            wrongCallConventionMethod.Body.GetILProcessor().Emit(OpCodes.Ret);
            TypeDefinition callConventionOverloads = new(
                string.Empty,
                "CallConventionOverloads",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(callConventionOverloads);
            MethodDefinition cdeclOverload = new(
                "Pick",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            cdeclOverload.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                cdeclPointer));
            cdeclOverload.Body.GetILProcessor().Emit(OpCodes.Ret);
            callConventionOverloads.Methods.Add(cdeclOverload);
            MethodDefinition stdcallOverload = new(
                "Pick",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            stdcallOverload.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                stdcallPointer));
            stdcallOverload.Body.GetILProcessor().Emit(OpCodes.Ret);
            callConventionOverloads.Methods.Add(stdcallOverload);

            FunctionPointerType ordinaryPointer = FunctionPointer(MethodCallingConvention.Default);
            FunctionPointerType genericPointer = FunctionPointer(MethodCallingConvention.Generic);
            genericPointer.GenericParameters.Add(new GenericParameter("T", genericPointer));
            TypeDefinition functionPointerArityOverloads = new(
                string.Empty,
                "FunctionPointerArityOverloads",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(functionPointerArityOverloads);
            MethodDefinition ordinaryPointerOverload = new(
                "Pick",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            ordinaryPointerOverload.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                ordinaryPointer));
            ordinaryPointerOverload.Body.GetILProcessor().Emit(OpCodes.Ret);
            functionPointerArityOverloads.Methods.Add(ordinaryPointerOverload);
            MethodDefinition genericPointerOverload = new(
                "Pick",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            genericPointerOverload.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                genericPointer));
            genericPointerOverload.Body.GetILProcessor().Emit(OpCodes.Ret);
            functionPointerArityOverloads.Methods.Add(genericPointerOverload);

            TypeReference suppressConvention = module.ImportReference(
                typeof(System.Runtime.CompilerServices.CallConvSuppressGCTransition));
            TypeReference forwardedSuppressConvention = new(
                "System.Runtime.CompilerServices",
                "CallConvSuppressGCTransition",
                module,
                callConventionFacadeScope);
            TypeReference callConventionBase = new(
                "System.Runtime.CompilerServices",
                "CallConv",
                module,
                suppressConvention.Scope);
            FunctionPointerType nonUnmanagedModified = FunctionPointer(
                MethodCallingConvention.Default);
            nonUnmanagedModified.ReturnType = new OptionalModifierType(
                suppressConvention,
                module.TypeSystem.Void);
            AddFunctionPointerContract(
                "INonUnmanagedModifierContract",
                "NonUnmanagedModifierImplementation",
                nonUnmanagedModified,
                FunctionPointer(MethodCallingConvention.Default));
            FunctionPointerType unmanagedRequired = FunctionPointer(
                MethodCallingConvention.Unmanaged);
            unmanagedRequired.ReturnType = new RequiredModifierType(
                suppressConvention,
                module.TypeSystem.Void);
            FunctionPointerType unmanagedOptional = FunctionPointer(
                MethodCallingConvention.Unmanaged);
            unmanagedOptional.ReturnType = new OptionalModifierType(
                suppressConvention,
                module.TypeSystem.Void);
            AddFunctionPointerContract(
                "IRequiredCallConventionContract",
                "RequiredCallConventionImplementation",
                unmanagedRequired,
                unmanagedOptional);
            FunctionPointerType duplicateConvention = FunctionPointer(
                MethodCallingConvention.Unmanaged);
            duplicateConvention.ReturnType = new OptionalModifierType(
                suppressConvention,
                new OptionalModifierType(suppressConvention, module.TypeSystem.Void));
            AddFunctionPointerContract(
                "IDuplicateCallConventionContract",
                "DuplicateCallConventionImplementation",
                duplicateConvention,
                unmanagedOptional);
            TypeDefinition fakeConvention = new(
                "System.Runtime.CompilerServices",
                "CallConvFake",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(fakeConvention);
            // 建立带反引号但没有泛型参数的普通类型及其普通函数签名。
            TypeDefinition literalBacktickConventionName = new(
                "System.Runtime.CompilerServices",
                "CallConv`Literal",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(literalBacktickConventionName);
            TypeDefinition literalBacktickConventionUser = new(
                string.Empty,
                "LiteralBacktickConventionUser",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(literalBacktickConventionUser);
            MethodDefinition literalBacktickConventionEcho = new(
                "Echo",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                literalBacktickConventionName);
            literalBacktickConventionEcho.Parameters.Add(new ParameterDefinition(
                "value",
                Mono.Cecil.ParameterAttributes.None,
                literalBacktickConventionName));
            literalBacktickConventionEcho.Body.GetILProcessor().Emit(OpCodes.Ldarg_1);
            literalBacktickConventionEcho.Body.GetILProcessor().Emit(OpCodes.Ret);
            literalBacktickConventionUser.Methods.Add(literalBacktickConventionEcho);
            FunctionPointerType fakeConventionPointer = FunctionPointer(
                MethodCallingConvention.Unmanaged);
            fakeConventionPointer.ReturnType = new OptionalModifierType(
                fakeConvention,
                module.TypeSystem.Void);
            AddFunctionPointerContract(
                "IFakeCallConventionContract",
                "FakeCallConventionImplementation",
                fakeConventionPointer,
                FunctionPointer(MethodCallingConvention.Unmanaged));
            FunctionPointerType baseConventionPointer = FunctionPointer(
                MethodCallingConvention.Unmanaged);
            baseConventionPointer.ReturnType = new OptionalModifierType(
                callConventionBase,
                module.TypeSystem.Void);
            AddFunctionPointerContract(
                "ICallConventionBaseContract",
                "CallConventionBaseImplementation",
                baseConventionPointer,
                FunctionPointer(MethodCallingConvention.Unmanaged));
            FunctionPointerType forwardedConventionPointer = FunctionPointer(
                MethodCallingConvention.Unmanaged);
            forwardedConventionPointer.ReturnType = new OptionalModifierType(
                forwardedSuppressConvention,
                module.TypeSystem.Void);
            AddFunctionPointerContract(
                "IForwardedCallConventionContract",
                "ForwardedCallConventionImplementation",
                forwardedConventionPointer,
                unmanagedOptional);
            TypeDefinition bangTypeName = new(
                string.Empty,
                "Real!0",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            TypeDefinition bangNamespace = new(
                "Scope!0",
                "Real",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(bangTypeName);
            module.Types.Add(bangNamespace);
            AddLiteralNameContract(
                "IBangTypeNameContract",
                "BangTypeNameImplementation",
                bangTypeName);
            AddLiteralNameContract(
                "IBangNamespaceContract",
                "BangNamespaceImplementation",
                bangNamespace);
            AddLiteralNameContract(
                "IBangAssemblyContract",
                "BangAssemblyImplementation",
                new TypeReference("Bang", "Value", module, bangAssemblyScope));
            AddLiteralNameContract(
                "IForwardedDelimiterContract",
                "ForwardedDelimiterImplementation",
                new TypeReference(string.Empty, "Real)", module, delimiterFacadeScope),
                new TypeReference(string.Empty, "Real)", module, delimiterTargetScope));
            FunctionPointerType sentinelPointer = FunctionPointer(MethodCallingConvention.VarArg);
            sentinelPointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            sentinelPointer.Parameters.Add(new ParameterDefinition(
                new SentinelType(module.TypeSystem.String)));
            FunctionPointerType requiredPointer = FunctionPointer(MethodCallingConvention.VarArg);
            requiredPointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            requiredPointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            AddFunctionPointerContract(
                "IVarArgSentinelContract",
                "VarArgRequiredImplementation",
                sentinelPointer,
                requiredPointer);
            TypeDefinition varArgOverloads = new(
                string.Empty,
                "VarArgOverloads",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(varArgOverloads);
            foreach (FunctionPointerType pointer in new[] { sentinelPointer, requiredPointer })
            {
                MethodDefinition overload = new(
                    "Pick",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                    module.TypeSystem.Void);
                overload.Parameters.Add(new ParameterDefinition(
                    "value",
                    Mono.Cecil.ParameterAttributes.None,
                    pointer));
                overload.Body.GetILProcessor().Emit(OpCodes.Ret);
                varArgOverloads.Methods.Add(overload);
            }
            TypeDefinition collisionA = new(
                string.Empty,
                "A",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            TypeDefinition collisionB = new(
                string.Empty,
                "B",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            TypeDefinition combinedCollision = new(
                string.Empty,
                "A,CustomBase|B",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            TypeDefinition collisionOverloads = new(
                string.Empty,
                "CollisionOverloads",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(collisionA);
            module.Types.Add(collisionB);
            module.Types.Add(combinedCollision);
            module.Types.Add(collisionOverloads);
            MethodDefinition splitCollision = new(
                "Pick",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            splitCollision.Parameters.Add(new ParameterDefinition(collisionA));
            splitCollision.Parameters.Add(new ParameterDefinition(collisionB));
            splitCollision.Body.GetILProcessor().Emit(OpCodes.Ret);
            collisionOverloads.Methods.Add(splitCollision);
            MethodDefinition combinedCollisionMethod = new(
                "Pick",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            combinedCollisionMethod.Parameters.Add(new ParameterDefinition(combinedCollision));
            combinedCollisionMethod.Body.GetILProcessor().Emit(OpCodes.Ret);
            collisionOverloads.Methods.Add(combinedCollisionMethod);

            TypeDefinition instancePointerOverloads = new(
                string.Empty,
                "InstancePointerOverloads",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(instancePointerOverloads);
            FunctionPointerType instancePointer = FunctionPointer(MethodCallingConvention.Default);
            instancePointer.HasThis = true;
            FunctionPointerType explicitPointer = FunctionPointer(MethodCallingConvention.Default);
            explicitPointer.HasThis = true;
            explicitPointer.ExplicitThis = true;
            foreach (FunctionPointerType pointer in new[]
                     {
                         FunctionPointer(MethodCallingConvention.Default),
                         instancePointer,
                         explicitPointer,
                     })
            {
                MethodDefinition overload = new(
                    "Pick",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                    module.TypeSystem.Void);
                overload.Parameters.Add(new ParameterDefinition(
                    "value",
                    Mono.Cecil.ParameterAttributes.None,
                    pointer));
                overload.Body.GetILProcessor().Emit(OpCodes.Ret);
                instancePointerOverloads.Methods.Add(overload);
            }

            assembly.Write(path);
        }

        // 写入类型名包含右括号的真实定义文件和类型转交文件。
        private static void WriteDelimiterAssemblies(string targetPath, string facadePath)
        {
            using (AssemblyDefinition target = AssemblyDefinition.CreateAssembly(
                       new AssemblyNameDefinition("DelimiterTarget", new Version(1, 0, 0, 0)),
                       "DelimiterTarget",
                       ModuleKind.Dll))
            {
                target.MainModule.Types.Add(new TypeDefinition(
                    string.Empty,
                    "Real)",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                    target.MainModule.ImportReference(typeof(object))));
                target.Write(targetPath);
            }

            using AssemblyDefinition facade = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("DelimiterFacade", new Version(1, 0, 0, 0)),
                "DelimiterFacade",
                ModuleKind.Dll);
            AssemblyNameReference targetScope = new(
                "DelimiterTarget",
                new Version(1, 0, 0, 0));
            facade.MainModule.AssemblyReferences.Add(targetScope);
            ExportedType forwardedType = new(string.Empty, "Real)", facade.MainModule, targetScope)
            {
                Attributes = Mono.Cecil.TypeAttributes.Public
                    | Mono.Cecil.TypeAttributes.Forwarder,
            };
            facade.MainModule.ExportedTypes.Add(forwardedType);
            facade.Write(facadePath);
        }

        // 写入裸拼接会碰撞的两个接口以及只实现第一个接口的类型。
        private static void WriteTypeIdentityCollisionAssemblies(
            string firstContractPath,
            string secondContractPath,
            string implementationPath)
        {
            Version version = new(1, 0, 0, 0);
            using (AssemblyDefinition first = AssemblyDefinition.CreateAssembly(
                       new AssemblyNameDefinition("A", version),
                       "FirstContract",
                       ModuleKind.Dll))
            {
                TypeDefinition contract = new(
                    string.Empty,
                    "B|C",
                    Mono.Cecil.TypeAttributes.Public
                        | Mono.Cecil.TypeAttributes.Interface
                        | Mono.Cecil.TypeAttributes.Abstract);
                contract.Methods.Add(new MethodDefinition(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.Abstract
                        | Mono.Cecil.MethodAttributes.Virtual
                        | Mono.Cecil.MethodAttributes.NewSlot,
                    first.MainModule.TypeSystem.Void));
                first.MainModule.Types.Add(contract);
                TypeDefinition topLevelWithPlus = new(
                    string.Empty,
                    "Outer+Inner",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class);
                TypeDefinition outer = new(
                    string.Empty,
                    "Outer",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class);
                outer.NestedTypes.Add(new TypeDefinition(
                    string.Empty,
                    "Inner",
                    Mono.Cecil.TypeAttributes.NestedPublic | Mono.Cecil.TypeAttributes.Class));
                first.MainModule.Types.Add(topLevelWithPlus);
                first.MainModule.Types.Add(outer);
                first.MainModule.Types.Add(new TypeDefinition(
                    "N.X",
                    "Y",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class));
                first.MainModule.Types.Add(new TypeDefinition(
                    "N",
                    "X.Y",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class));
                TypeDefinition weird = new(
                    string.Empty,
                    "Weird&",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class);
                TypeDefinition weirdContract = new(
                    string.Empty,
                    "IWeirdContract",
                    Mono.Cecil.TypeAttributes.Public
                        | Mono.Cecil.TypeAttributes.Interface
                        | Mono.Cecil.TypeAttributes.Abstract);
                MethodDefinition weirdContractMethod = new(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.Abstract
                        | Mono.Cecil.MethodAttributes.Virtual
                        | Mono.Cecil.MethodAttributes.NewSlot,
                    first.MainModule.TypeSystem.Void);
                weirdContractMethod.Parameters.Add(new ParameterDefinition(weird));
                weirdContract.Methods.Add(weirdContractMethod);
                TypeDefinition weirdImplementation = new(
                    string.Empty,
                    "WeirdImplementation",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class);
                weirdImplementation.Interfaces.Add(new InterfaceImplementation(weirdContract));
                MethodDefinition weirdImplementationMethod = new(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.Virtual
                        | Mono.Cecil.MethodAttributes.Final
                        | Mono.Cecil.MethodAttributes.NewSlot,
                    first.MainModule.TypeSystem.Void);
                weirdImplementationMethod.Parameters.Add(new ParameterDefinition(weird));
                weirdImplementationMethod.Body.GetILProcessor().Emit(OpCodes.Ret);
                weirdImplementation.Methods.Add(weirdImplementationMethod);
                first.MainModule.Types.Add(weird);
                first.MainModule.Types.Add(weirdContract);
                first.MainModule.Types.Add(weirdImplementation);
                first.MainModule.Types.Add(new TypeDefinition(
                    string.Empty,
                    "Plain`Name",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class));
                // 建立命名空间中与模块伪类型同名的合法类型。
                first.MainModule.Types.Add(new TypeDefinition(
                    "X",
                    "<Module>",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class));
                first.Write(firstContractPath);
            }

            using (AssemblyDefinition second = AssemblyDefinition.CreateAssembly(
                       new AssemblyNameDefinition("A|B", version),
                       "SecondContract",
                       ModuleKind.Dll))
            {
                TypeDefinition contract = new(
                    string.Empty,
                    "C",
                    Mono.Cecil.TypeAttributes.Public
                        | Mono.Cecil.TypeAttributes.Interface
                        | Mono.Cecil.TypeAttributes.Abstract);
                contract.Methods.Add(new MethodDefinition(
                    "Accept",
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.Abstract
                        | Mono.Cecil.MethodAttributes.Virtual
                        | Mono.Cecil.MethodAttributes.NewSlot,
                    second.MainModule.TypeSystem.Void));
                second.MainModule.Types.Add(contract);
                second.Write(secondContractPath);
            }

            using AssemblyDefinition implementation = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("CollisionImplementation", version),
                "CollisionImplementation",
                ModuleKind.Dll);
            ModuleDefinition module = implementation.MainModule;
            AssemblyNameReference contractAssembly = new("a", version);
            module.AssemblyReferences.Add(contractAssembly);
            TypeReference contractReference = new(string.Empty, "B|C", module, contractAssembly);
            TypeDefinition implementationType = new(
                string.Empty,
                "Implementation",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            implementationType.Interfaces.Add(new InterfaceImplementation(contractReference));
            MethodDefinition accept = new(
                "Accept",
                Mono.Cecil.MethodAttributes.Public
                    | Mono.Cecil.MethodAttributes.Virtual
                    | Mono.Cecil.MethodAttributes.Final
                    | Mono.Cecil.MethodAttributes.NewSlot,
                module.TypeSystem.Void);
            accept.Body.GetILProcessor().Emit(OpCodes.Ret);
            implementationType.Methods.Add(accept);
            module.Types.Add(implementationType);
            implementation.Write(implementationPath);
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
