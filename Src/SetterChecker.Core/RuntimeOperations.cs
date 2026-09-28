namespace SetterChecker.Core
{
    /// <summary>基础操作的行为分类，不是 Getter/Setter 白名单；未登记的方法仍读取真实实现。</summary>
    public enum RuntimeOperation
    {
        /// <summary>未建模，不据此推断纯读或写入。</summary>
        Unmodeled = 0,
        /// <summary>数值计算。</summary>
        ComputeValue,
        /// <summary>值转换。</summary>
        ConvertValue,
        /// <summary>值比较。</summary>
        CompareValues,
        /// <summary>引用身份比较。</summary>
        CompareReferences,
        /// <summary>读取对象身份哈希。</summary>
        ReadIdentityHash,
        /// <summary>创建对象。</summary>
        CreateObject,
        /// <summary>创建数组。</summary>
        CreateArray,
        /// <summary>创建浅复制外壳。</summary>
        ShallowCopy,
        /// <summary>装箱。</summary>
        BoxValue,
        /// <summary>拆箱。</summary>
        UnboxValue,
        /// <summary>读取数组维度。</summary>
        ReadArrayShape,
        /// <summary>读取标准集合内容或比较器，不修改集合。</summary>
        ReadCollection,
        /// <summary>读取数组元素。</summary>
        ReadArrayElement,
        /// <summary>写入数组元素。</summary>
        WriteArrayElement,
        /// <summary>复制数组内容。</summary>
        CopyArray,
        /// <summary>清空数组内容。</summary>
        ClearArray,
        /// <summary>读取内存。</summary>
        ReadMemory,
        /// <summary>写入内存。</summary>
        WriteMemory,
        /// <summary>复制内存。</summary>
        CopyMemory,
        /// <summary>清空内存。</summary>
        ClearMemory,
        /// <summary>读取对象类型。</summary>
        ReadObjectType,
        /// <summary>读取类型信息。</summary>
        ReadTypeMetadata,
        /// <summary>读取成员信息。</summary>
        ReadMemberMetadata,
        /// <summary>比较类型。</summary>
        CompareTypes,
        /// <summary>查找类型。</summary>
        FindType,
        /// <summary>查找成员。</summary>
        FindMember,
        /// <summary>读取字段。</summary>
        ReadField,
        /// <summary>写入字段。</summary>
        WriteField,
        /// <summary>执行属性读取。</summary>
        ReadProperty,
        /// <summary>执行属性写入。</summary>
        WriteProperty,
        /// <summary>执行方法。</summary>
        InvokeMethod,
        /// <summary>执行构造函数。</summary>
        InvokeConstructor,
        /// <summary>创建委托。</summary>
        CreateDelegate,
        /// <summary>组合委托。</summary>
        CombineDelegates,
        /// <summary>移除委托目标。</summary>
        RemoveDelegate,
        /// <summary>执行委托。</summary>
        InvokeDelegate,
        /// <summary>执行类型初始化。</summary>
        InitializeType,
        /// <summary>原子读取。</summary>
        AtomicRead,
        /// <summary>原子写入。</summary>
        AtomicWrite,
        /// <summary>线程同步。</summary>
        Synchronize,
        /// <summary>调度执行。</summary>
        ScheduleExecution,
        /// <summary>管理对象生命周期。</summary>
        ManageLifetime,
        /// <summary>读取外部状态。</summary>
        ReadExternalState,
        /// <summary>写入外部状态。</summary>
        WriteExternalState,
        /// <summary>加载代码。</summary>
        LoadCode,
        /// <summary>生成代码。</summary>
        GenerateCode,
        /// <summary>调试日志或性能采样，不改变业务状态。</summary>
        Diagnostics,
        /// <summary>修改标准集合自身内容；是否影响业务对象由接收对象来源决定。</summary>
        WriteCollection,
    }

    /// <summary>一条经过核对的基础操作说明；适用当前材料对应的实际核心库。</summary>
    public sealed record RuntimeOperationRule(RuntimeOperation Operation, string Description, string Source);

    /// <summary>外部库函数的通用模型：可能被写内容的槽位（0 为接收对象）及写入原因，存入接收对象的实参槽位。</summary>
    internal sealed record LibraryEffect(ulong Written, IReadOnlyDictionary<int, string> Reasons, ulong StoredIntoReceiver);

    /// <summary>按实际类型和完整签名识别基础操作；其余方法交还普通源码或 IL 分析。</summary>
    internal static class RuntimeOperations
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<TypeEntry>, ExceptionTypeCache> s_exceptionTypes = new();

        // 诊断类型必须同时匹配完整类型名和程序集简单名，避免误伤业务同名类型。
        internal static RuntimeOperationRule? Find(MethodCatalogResult catalog, MethodEntry method)
        {
            if (catalog.TypesById.TryGetValue(method.TypeId, out TypeEntry? diagnosticType)
                && IsDiagnostic(diagnosticType))
            {
                return new(RuntimeOperation.Diagnostics, "调试日志与性能采样不改变战斗状态。",
                    "用户确认的诊断边界类型");
            }
            if (method.SourceSymbol != null || method.GenericArity != 0
                || !catalog.TypesById.TryGetValue(method.TypeId, out TypeEntry? declaringType)
                || !declaringType.FullName.StartsWith("System.", StringComparison.Ordinal))
            {
                return null;
            }
            string owner = declaringType.FullName;
            string[] parameters = method.Parameters.Select(parameter => parameter.TypeId).ToArray();
            bool hasRefParameter = method.Parameters.Any(parameter => parameter.RefKind != Microsoft.CodeAnalysis.RefKind.None);

            if (IsCollectionMutation(owner, method.Name, parameters, hasRefParameter))
            {
                return new(RuntimeOperation.WriteCollection, "标准集合修改自身内容；接收对象来源仍由统一效果分析判断。",
                    "System.Collections.Generic 标准成员语义");
            }

            // 标准库异常只初始化新建异常对象；ThrowHelper 只选择并创建异常，不修改业务对象。
            if (owner == "System.ThrowHelper")
            {
                return new(RuntimeOperation.CreateObject, "标准库失败路径只创建异常对象，不修改业务状态。",
                    "https://learn.microsoft.com/dotnet/standard/exceptions");
            }
            if (method.Name == ".ctor" && IsExceptionType(catalog, declaringType))
            {
                return new(RuntimeOperation.CreateObject, "异常构造只初始化新建异常对象，不修改调用者对象。",
                    "https://learn.microsoft.com/dotnet/api/system.exception");
            }

            if (owner == "System.Array" && !method.IsStatic && parameters.SequenceEqual(new[] { "System.Int32" })
                && method.Name is "GetLength" or "GetLongLength" or "GetLowerBound" or "GetUpperBound"
                && method.ReturnTypeId == (method.Name == "GetLongLength" ? "System.Int64" : "System.Int32"))
            {
                return new(RuntimeOperation.ReadArrayShape, "读取数组维度信息，不修改数组；数值未求得不影响行为判断。",
                    "https://learn.microsoft.com/dotnet/api/system.array");
            }
            if (owner == "System.Object" && method.Name == "MemberwiseClone" && !method.IsStatic
                && parameters.Length == 0 && method.ReturnTypeId == "System.Object")
            {
                return new(RuntimeOperation.ShallowCopy, "创建同类型的新外壳；值字段复制，引用字段仍指向原对象；不调用构造函数。",
                    "https://learn.microsoft.com/dotnet/api/system.object.memberwiseclone");
            }
            if (method.IsStatic && method.ReturnTypeId == "System.Int32" && parameters.SequenceEqual(new[] { "System.Object" })
                && (owner == "System.Runtime.CompilerServices.RuntimeHelpers" && method.Name == "GetHashCode"
                    || owner == "System.Object" && method.Name == "InternalGetHashCode"))
            {
                return new(RuntimeOperation.ReadIdentityHash, "读取对象身份哈希，不调用业务 GetHashCode 重写；运行时对象头维护不计为业务状态修改。",
                    "https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.runtimehelpers.gethashcode");
            }
            if (owner.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal) && !method.IsStatic
                && (method.Name is "ContainsKey" or "get_Item" or "get_Keys" or "get_Values" or "get_Count"
                    || method.Name == "TryGetValue" && parameters.Length == 2 && method.Parameters[1].RefKind == Microsoft.CodeAnalysis.RefKind.Out)
                && (method.Name == "get_Item" ? parameters.Length == 1
                    : method.Name is "get_Keys" or "get_Values" or "get_Count" ? parameters.Length == 0
                    : method.Name == "TryGetValue" ? parameters.Length == 2 : parameters.Length == 1)
                && (method.Name == "TryGetValue" || !hasRefParameter))
            {
                return new(RuntimeOperation.ReadCollection, "读取字典内容，不修改字典本身。",
                    "https://learn.microsoft.com/dotnet/api/system.collections.generic.dictionary-2");
            }
            if (owner.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) && !method.IsStatic
                && method.Name is "get_Count" or "get_Item" or "Contains" or "IndexOf"
                && (method.Name == "get_Count" ? parameters.Length == 0 : parameters.Length >= 1)
                && !hasRefParameter)
            {
                return new(RuntimeOperation.ReadCollection, "读取列表内容或长度，不修改列表本身。",
                    "https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1");
            }
            if (owner.StartsWith("System.Collections.Generic.HashSet<", StringComparison.Ordinal)
                && !method.IsStatic && method.Name == "Contains" && parameters.Length == 1 && !hasRefParameter)
            {
                return new(RuntimeOperation.ReadCollection, "读取集合内容，不修改集合本身。",
                    "https://learn.microsoft.com/dotnet/api/system.collections.generic.hashset-1");
            }
            if (owner.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal)
                && method.Name is "GetHashCode" or "Equals" && !hasRefParameter)
            {
                return new(RuntimeOperation.CompareValues, "比较字典键，不修改字典或业务对象。",
                    "https://learn.microsoft.com/dotnet/api/system.object.equals");
            }
            if (owner.StartsWith("System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal)
                && method.Name == ".ctor")
            {
                return new(RuntimeOperation.CreateObject, "创建值类型键值对，不修改外部对象。",
                    "https://learn.microsoft.com/dotnet/api/system.collections.generic.keyvaluepair-2");
            }
            if (owner.StartsWith("System.Span<", StringComparison.Ordinal)
                || owner.StartsWith("System.ReadOnlySpan<", StringComparison.Ordinal))
            {
                if (method.Name == ".ctor")
                {
                    return new(RuntimeOperation.CreateObject, "创建栈上视图，不修改外部对象。",
                        "https://learn.microsoft.com/dotnet/api/system.span-1");
                }
            }
            if (owner.StartsWith("System.Collections.Generic.EqualityComparer<", StringComparison.Ordinal) && method.IsStatic
                && method.Name == "get_Default" && parameters.Length == 0 && !hasRefParameter)
            {
                return new(RuntimeOperation.ReadCollection, "读取标准相等比较器，不修改业务状态。",
                    "https://learn.microsoft.com/dotnet/api/system.collections.generic.equalitycomparer-1");
            }
            if (owner == "System.Object" && method.IsStatic && method.Name == "Equals"
                && parameters.SequenceEqual(new[] { "System.Object", "System.Object" })
                && method.ReturnTypeId == "System.Boolean")
            {
                return new(RuntimeOperation.CompareReferences, "比较两个对象引用，不修改业务状态。",
                    "https://learn.microsoft.com/dotnet/api/system.object.equals");
            }
            if (owner == "System.Object" && method.IsStatic && method.Name == "ReferenceEquals"
                && parameters.SequenceEqual(new[] { "System.Object", "System.Object" })
                && method.ReturnTypeId == "System.Boolean")
            {
                return new(RuntimeOperation.CompareReferences, "比较对象身份，不修改业务状态。",
                    "https://learn.microsoft.com/dotnet/api/system.object.referenceequals");
            }
            if (owner == "System.String" && method.Name is "Format" or "Concat" or "Join" or "Replace"
                && method.ReturnTypeId == "System.String")
            {
                return new(RuntimeOperation.ConvertValue, "创建新的字符串值，不修改业务对象。",
                    "https://learn.microsoft.com/dotnet/api/system.string");
            }
            if (method.IsStatic && method.Name == "TryParse" && method.ReturnTypeId == "System.Boolean"
                && method.Parameters.Any(parameter => parameter.RefKind == Microsoft.CodeAnalysis.RefKind.Out))
            {
                return new(RuntimeOperation.ConvertValue, "解析值并写入局部输出，不修改业务对象。",
                    "https://learn.microsoft.com/dotnet/api/system.int32.tryparse");
            }
            if (declaringType.IsValueType && method.Name == "ToString" && method.ReturnTypeId == "System.String" && !hasRefParameter
                && parameters.All(parameter => parameter is "System.String" or "System.IFormatProvider"))
            {
                return new(RuntimeOperation.ConvertValue, "把值转换为新字符串，不修改业务对象。",
                    "https://learn.microsoft.com/dotnet/api/system.object.tostring");
            }
            return null;
        }

        // 只按标准集合完整类型名和不带比较器/委托的修改签名建模。
        private static bool IsCollectionMutation(string owner, string name, string[] parameters, bool hasRefParameter)
        {
            if (hasRefParameter || owner.StartsWith("System.Collections.Generic.", StringComparison.Ordinal) == false)
            {
                return false;
            }
            if (owner.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal))
            {
                // AddRange/InsertRange 会枚举传入序列，序列可能是业务迭代器，必须读取真实实现。
                return name is "Add" or "Insert" or "Remove" or "RemoveAt" or "RemoveRange" or "Clear" or "Reverse" or "set_Item"
                    && (name is "Clear" or "Reverse" ? parameters.Length == 0 : parameters.Length <= 2);
            }
            if (owner.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal))
            {
                return name is "Add" or "TryAdd" or "Remove" or "Clear" or "set_Item"
                    && (name == "Clear" ? parameters.Length == 0 : parameters.Length <= 2);
            }
            if (owner.StartsWith("System.Collections.Generic.HashSet<", StringComparison.Ordinal))
            {
                return name is "Add" or "Remove" or "Clear" && (name == "Clear" ? parameters.Length == 0 : parameters.Length == 1);
            }
            if (owner.StartsWith("System.Collections.Generic.Queue<", StringComparison.Ordinal))
            {
                return name is "Enqueue" or "Dequeue" or "Clear" && (name is "Dequeue" or "Clear" ? parameters.Length == 0 : parameters.Length == 1);
            }
            if (owner.StartsWith("System.Collections.Generic.Stack<", StringComparison.Ordinal))
            {
                return name is "Push" or "Pop" or "Clear" && (name is "Pop" or "Clear" ? parameters.Length == 0 : parameters.Length == 1);
            }
            return false;
        }

        private static readonly string[] s_collectionTypeNames =
        {
            "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.HashSet`1",
            "System.Collections.Generic.Queue`1", "System.Collections.Generic.Stack`1", "System.Collections.Generic.LinkedList`1",
            "System.Collections.Generic.SortedList`2", "System.Collections.Generic.SortedDictionary`2", "System.Collections.Generic.SortedSet`1",
            "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IList`1", "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.ISet`1", "System.Collections.ArrayList", "System.Collections.Hashtable", "System.Collections.Queue",
            "System.Collections.Stack", "System.Collections.SortedList", "System.Collections.IList", "System.Collections.IDictionary",
        };

        // V3 设计 3.2 节的通用库模型：集合接收对象、数组与集合实参、ref/out 实参可能被写；值类型接收对象按原地修改处理。
        internal static LibraryEffect ReadLibraryEffect(MethodCatalogResult catalog, MethodEntry method)
        {
            ulong written = 0;
            ulong stored = 0;
            Dictionary<int, string> reasons = new();
            TypeEntry owner = catalog.TypesById[method.TypeId];
            if (!method.IsStatic && !IsReadOnlyMember(method.Name))
            {
                string? reason = IsCollectionType(catalog, owner) ? "库函数模型：修改集合内容"
                    : owner.IsValueType && !catalog.IsReadOnlyStructMember(method) ? "库函数模型：原地修改值类型接收对象" : null;
                if (reason != null)
                {
                    written |= 1UL;
                    reasons.Add(0, reason);
                    for (int index = 0; index < method.Parameters.Count && index < 62; index++)
                    {
                        stored |= 1UL << (index + 1);
                    }
                }
            }
            string ownerName = ReadMetadataName(owner.LogicalId);
            bool readsArrays = ownerName == "System.String" && method.Name is "Join" or "Concat" or "Format" or ".ctor"
                || ownerName == "System.Array" && (method.Name is "IndexOf" or "LastIndexOf" or "BinarySearch" or "Exists" or "TrueForAll"
                    || method.Name.StartsWith("Find", StringComparison.Ordinal))
                || ownerName.StartsWith("System.Text.", StringComparison.Ordinal) && ownerName.EndsWith("Encoding", StringComparison.Ordinal)
                    && method.Name is "GetString" or "GetCharCount";
            for (int index = 0; index < method.Parameters.Count && index < 62; index++)
            {
                ParameterEntry parameter = method.Parameters[index];
                string? reason = parameter.RefKind is Microsoft.CodeAnalysis.RefKind.Ref or Microsoft.CodeAnalysis.RefKind.Out ? "库函数模型：写 ref/out 实参"
                    : readsArrays ? null
                    : parameter.TypeId.EndsWith(']') ? "库函数模型：可能写数组实参内容"
                    : method.Kind != Microsoft.CodeAnalysis.MethodKind.Constructor && IsCollectionName(ReadMetadataName(parameter.TypeId))
                        ? "库函数模型：可能写集合实参内容" : null;
                if (reason != null)
                {
                    written |= 1UL << (index + 1);
                    reasons.Add(index + 1, reason);
                }
            }
            return new LibraryEffect(written, reasons, stored);
        }

        // 集合与值类型接收对象上不修改内容的成员（V3 设计 3.2 节只读成员清单）。
        private static bool IsReadOnlyMember(string name) => name.StartsWith("Contains", StringComparison.Ordinal)
            || name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("Find", StringComparison.Ordinal)
            || name is "TryGetValue" or "IndexOf" or "LastIndexOf" or "Exists" or "TrueForAll" or "BinarySearch" or "GetEnumerator"
                or "ToArray" or "CopyTo" or "Equals" or "GetHashCode" or "ToString" or "GetType";

        // 标准集合或实现了 ICollection<T>、IList、IDictionary 的外部类型。
        private static bool IsCollectionType(MethodCatalogResult catalog, TypeEntry type)
        {
            string name = ReadMetadataName(type.LogicalId);
            return IsCollectionName(name) || !type.IsInterface && catalog.ReadInheritedTypes(type)
                .Any(relation => IsCollectionName(ReadMetadataName(relation.Definition.LogicalId)));
        }

        // 按完整元数据名识别标准集合及集合接口。
        private static bool IsCollectionName(string name) => s_collectionTypeNames.Contains(name)
            || name.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal);

        // 从类型身份 A{n}:{程序集}T{m}:{元数据名} 取出最外层元数据名；原始类型、泛型参数身份原样截取。
        private static string ReadMetadataName(string identity)
        {
            if (identity.Length > 1 && identity[0] == 'A' && char.IsAsciiDigit(identity[1]))
            {
                int colon = identity.IndexOf(':');
                int assemblyLength = int.Parse(identity.AsSpan(1, colon - 1), System.Globalization.CultureInfo.InvariantCulture);
                int typeStart = colon + 1 + assemblyLength;
                int typeColon = identity.IndexOf(':', typeStart);
                int nameLength = int.Parse(identity.AsSpan(typeStart + 1, typeColon - typeStart - 1), System.Globalization.CultureInfo.InvariantCulture);
                return identity.Substring(typeColon + 1, nameLength);
            }
            int end = identity.IndexOfAny(new[] { '<', '[', '&', '*' });
            return end < 0 ? identity : identity[..end];
        }

        // 诊断边界仅针对用户确认的完整类型和程序集简单名。
        private static bool IsDiagnostic(TypeEntry type)
        {
            return (type.FullName, type.AssemblyName) switch
            {
                ("KH.Debuger", "Debuger")
                    or ("KH.KHProfiler", "kihan.common.runtime")
                    or ("KH.KHProfilerExt", "Assembly-CSharp")
                    or ("UnityEngine.Debug", "UnityEngine.CoreModule")
                    or ("UnityEngine.Profiling.Profiler", "UnityEngine.CoreModule")
                    or ("UnityEngine.Profiling.CustomSampler", "UnityEngine.CoreModule") => true,
                _ => false,
            };
        }

        // 沿继承关系识别所有标准异常类型，不枚举具体异常类名称；载入新类型会更换类型数组，结果按数组快照缓存。
        private static bool IsExceptionType(MethodCatalogResult catalog, TypeEntry type)
        {
            ExceptionTypeCache cache = s_exceptionTypes.GetValue(catalog.Types, types => new(types));
            return cache.Results.GetOrAdd(type.Id, _ =>
            {
                HashSet<string> visited = new(StringComparer.Ordinal);
                TypeEntry? current = type;
                while (current != null && visited.Add(current.Id))
                {
                    if (current.FullName == "System.Exception")
                    {
                        return true;
                    }
                    if (current.BaseType == null)
                    {
                        current = null;
                    }
                    else if (catalog.TypesById.TryGetValue(current.BaseType.DefinitionId, out TypeEntry? baseType))
                    {
                        current = baseType;
                    }
                    else
                    {
                        int position = Math.Min(cache.Positions.Value.ById.GetValueOrDefault(current.BaseType.DefinitionId, int.MaxValue),
                            cache.Positions.Value.ByFullName.GetValueOrDefault(current.BaseType.FullName, int.MaxValue));
                        current = position == int.MaxValue ? null : cache.Types[position];
                    }
                }
                return false;
            });
        }

        /// <summary>同一类型数组快照内的异常判断结果，以及按身份或完整名称首次出现的位置。</summary>
        private sealed class ExceptionTypeCache(IReadOnlyList<TypeEntry> types)
        {
            internal IReadOnlyList<TypeEntry> Types { get; } = types;
            internal System.Collections.Concurrent.ConcurrentDictionary<string, bool> Results { get; } = new(StringComparer.Ordinal);
            internal Lazy<(Dictionary<string, int> ById, Dictionary<string, int> ByFullName)> Positions { get; } = new(() =>
            {
                (Dictionary<string, int> ById, Dictionary<string, int> ByFullName) positions = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                for (int index = 0; index < types.Count; index++)
                {
                    positions.ById.TryAdd(types[index].Id, index);
                    positions.ByFullName.TryAdd(types[index].FullName, index);
                }
                return positions;
            });
        }
    }
}
