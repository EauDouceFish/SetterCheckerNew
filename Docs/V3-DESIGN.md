# SetterChecker V3 设计：按战斗状态判定

> 状态：已定稿，待施工。2026-09-28。
> 依据：用户 2026-09-28 的要求："Setter 按需求应该是修改战斗状态"；战斗程序集范围由审阅方按仓库规律决定；表现字段一律算战斗状态。

## 1 要解决的三个根本问题

1. **Setter 定义过宽。** 现在只要修改任何已有对象就算 Setter，结果写 `StringBuilder`、序列化缓冲区、日志器、标准库缓存都算修改，只能靠逐条加规则豁免（R3、R8）。
2. **标准库和 Unity 的内部实现也逐条读 IL。** 读取的函数里约 47%（1.6 万个）来自 DLL，这带来 2,212 条经过库内部实现的无关证据链，也占了一半的工作量。
3. **调用解析是按需求值、边算边剪枝，不是整体的指针分析。** 结果依赖处理顺序；26 个精度测试失败（委托、迭代器、工厂按创建位置区分）；很难并行。

第 1、2 条在阶段一解决，第 3 条在阶段二解决。

## 2 判定定义

### 2.1 战斗程序集

以下 5 个源码程序集里**声明**的字段算战斗状态：

| 程序集 | 依据 |
|---|---|
| `khengine.runtime` | 日志注入器的目标（`LogTrackInjector.dllNames`），CheckSum 生成器只扫描它 |
| `khengine.define` | 日志注入器的目标 |
| `morefun.lockstep.Runtime` | 确定性定点数、`LList`/`LDictionary`/`PriorityQueue`、`FRandom`、对象池 |
| `kihan.common.Runtime` | 战斗实体使用的事件分发器（`KHEventDispatcher`、`EventDispatcherImpl`）和对象池 |
| `kihan.proto` | 战斗初始化和角色数据等协议对象，由战斗对象持有 |

以下程序集的对象状态**不算**战斗状态，调用照常追进去，因为它们可能回调 khengine：

- 全部 DLL（标准库、UnityEngine、ILRuntime、`Debuger.dll` 等）
- `Xlua.Core`、`kihan.litjson`、`protobuf-net`、`com.morefun.KFBuffers`、`ICSharpCode.SharpZipLib.GZip`、`FSPDebuger.Runtime`、`KexiuDefine`、`kihan.inspector.runtime`、`kihan.unity.proxy`
- `Assembly-CSharp` 等客户端程序集

表中的程序集名是配置常量，写在 `EffectAnalyzer` 中并注明来源。它与 R3 的诊断类型表同属用户授权的例外。

### 2.2 一次写入是否写了战斗状态（归属规则）

每次写入都要找到被写存储的**归属字段**：

| 被写的存储 | 归属字段 |
|---|---|
| 引用类型对象 `o` 的字段 `f` | `f` 本身 |
| 结构体值内的字段（例如 `this.m_pos.x`） | 容纳这个结构体值的存储的归属字段，按同一规则向外递推，结构体成员不改变归属 |
| 数组元素、集合内容（`List.Add`、`dict[k] = v`、`Array.Clear` 等） | 这个数组或集合对象**是从哪个字段读出来的**，那个字段就是归属字段 |
| `ref`/`out` 变量 | 被引用的存储的归属字段 |
| 静态字段 | 它本身 |

判定：

- 归属字段在战斗程序集中声明，这次写入就是战斗写入。
- 归属字段在其他程序集中声明，这次写入不计。
- 被写对象就是本函数的当前对象或参数，函数内没有经过任何字段：归属未定，交给调用方，按调用方实参的来源用同一规则判定。
- 本函数新建的对象（R5）、局部变量：不计，与现在相同。

示例：

- `m_list.Add(x)`，`m_list` 在 khengine 中声明：计入。
- `m_sb.Append(s)`：`StringBuilder` 的内部字段在标准库中声明，不计。
- `writer.buffer[i] = b`，`buffer` 在 KFBuffers 中声明：不计。
- `Fill(List<int> list)` 修改参数：归属未定。调用方传 `m_list` 就计入，传新建的局部列表就不计。
- `this.m_unityVec.x = 1`：`m_unityVec` 是 khengine 字段，里面存的是 Unity 结构体，计入。
- `Interlocked.Increment(ref s_counter)`，`s_counter` 在 khengine 中声明：计入（R6）。

### 2.3 保留、取消的规则

| 规则 | 处理 |
|---|---|
| R1 | 日志决定只看真实行为，不变 |
| R2 | 懒加载，不变（单例字段在 khengine 中声明，仍需要这条规则） |
| R3 | 诊断类型，保留。`KHProfiler` 在 `kihan.common.Runtime` 中，属于战斗程序集，仍需豁免 |
| R4 | 只分析真机程序集，不变 |
| R5 | 创建并返回对象不算修改，不变 |
| R6 | 静态计数器是 Setter，不变 |
| R7 | 列出编辑器宏隐藏的代码，不变 |
| R8 | **删除**：被 2.2 节的归属规则完全覆盖 |
| R9 | 带参数的空函数是日志打点，不变 |

### 2.4 报告范围

报告程序集为 `khengine.runtime` 和 `khengine.define`，两者都会被注入日志。

## 3 阶段一：战斗状态判定加外部库效果模型

### 3.1 只读源码函数体

- 只读取源码函数的函数体，任何 DLL 函数都不再读 IL，改用 3.2 节的通用库模型。
- 现有的 `RuntimeOperations` 规则继续优先使用，例如反射、构造、集合读写、诊断。
- "运行时原生实现"和"函数体读取失败"只可能出现在源码函数上。DLL 函数因为不再需要函数体，不再以此为由判为未证明。

### 3.2 通用库模型（DLL 函数，没有专门规则时使用）

**效果**：

- **集合内容**：接收对象是标准集合时（`List`、`Dictionary`、`HashSet`、`Queue`、`Stack`、`LinkedList`、`SortedList`、`SortedDictionary`、`SortedSet`、`Concurrent*`，以及实现了 `ICollection<T>`/`IList`/`IDictionary` 的其他 DLL 类型），除只读成员外，其余成员都视为写接收对象的内容，归属未定。
  - 只读成员清单：`Contains*`、`TryGetValue`（写 out 参数）、`get_*`、`IndexOf`、`LastIndexOf`、`Find*`、`Exists`、`TrueForAll`、`BinarySearch`、`GetEnumerator`、`ToArray`、`CopyTo`（写数组实参）、`Equals`、`GetHashCode`、`ToString`、`GetType`。
- **数组参数**：DLL 函数的数组类型参数（含 `params`）视为可能被写内容，归属未定，由调用方判定。
  - 只读白名单：`string.Join`、`string.Concat`、`string.Format`、`Array.IndexOf`/`LastIndexOf`/`BinarySearch`/`Exists`/`Find*`、`Encoding.*.GetString`/`GetCharCount`、`new string(char[])`。
  - 调用方传入的新建数组（含编译器生成的 `params` 数组）按 R5 不计，所以这条规则基本只在传入字段中数组时起作用。
- **`ref`/`out` 参数**：视为写被引用的存储。
- **其余状态**：DLL 自身的状态（静态字段、内部缓存、Unity 引擎对象）不算战斗状态，按 2.1 节。

**回调**：DLL 函数可能调用业务代码，调用目标只包括两类：

- 作为实参传入的委托：按委托实际的目标函数。
- 实参或接收对象的实际类型（源码类型）中，所有**重写或实现了 DLL 中声明的虚函数或接口成员**的方法，例如 `ToString`、`Equals`、`GetHashCode`、`CompareTo`、`IComparer.Compare`、`IEqualityComparer.*`、`IEnumerable.GetEnumerator`、`IEnumerator.MoveNext`/`get_Current`、`IDisposable.Dispose`、`IFormattable.ToString`。

框架只能通过它自己声明的虚成员调用业务代码，所以这是完整的上界。唯一例外是反射，由现有的反射规则处理。

**返回值**：

- 可能是新对象，也可能是接收对象或实参所含内容中的任一对象（深）。
- 用于分派时，按声明的返回类型取候选实现。

### 3.3 验收

1. 编译、全部测试（因规则改变而改预期的测试注明规则号）、`-j1`/`-j8` 逐字节一致。
2. 新增测试，每个都要源码与 DLL 两种来源：
   - 写 `StringBuilder` 字段为 Getter。
   - 写非战斗程序集类型的字段为 Getter；写战斗程序集字段为 Setter。
   - `m_list.Add` 为 Setter。
   - 修改参数列表：调用方传字段为 Setter，传局部变量为 Getter。
   - 战斗字段里的结构体写入为 Setter。
   - `Interlocked.Increment(ref 战斗静态字段)` 为 Setter。
   - DLL 函数经委托实参回调业务 lambda 写战斗状态，为 Setter。
   - `string.Format` 调用的业务 `ToString` 写战斗状态为 Setter，不写则为 Getter。
   - 测试夹具需要能声明"战斗程序集"，用测试工程的程序集名配置。
3. khengine 对比（与 `refactor-v2` 当前版本相比）：
   - 所有"Setter 变 Getter"的函数都要自动归类：只写非战斗存储（列出被写字段）、证据链只经过 DLL 内部等。每类抽 10 个核对源码。
   - "Getter 变 Setter"的逐条说明。
4. 结合 8 个标签审查的人工判定（`%TEMP%/label-review/*-result.csv`）：
   - B2（工具漏记）必须全部修正，或逐条说明。
   - A2（改的不是战斗状态）应大部分变为 Getter。
   - A3（工具误判）应大部分消失。
5. 报告耗时和函数体读取数。这不作为门槛，但读取数应明显下降。

## 4 阶段二：用整体指针分析替换按需解析

阶段一验收后启动，要求如下：

- 对象按创建位置区分，字段分开追踪，写成约束后求不动点，调用图在求解中得出。
- 对委托、迭代器、工厂和容器，按需要加一层接收对象上下文。
- 外部库使用 3.2 节的模型。
- 注册位置用标识符索引找到写入者，作为额外的根，迭代到不动点。
- 每个函数的局部数据流（`ReadLocalOrigins`）和摘要引擎（`EffectAnalyzer.SummaryEngine`）保留，只替换跨函数的值来源追踪和调用目标解析。
- 验收：
  - 26 个精度测试转为通过（或逐条说明）。
  - 结果与处理顺序无关，`-j1`/`-j8` 逐字节一致。
  - 与阶段一的结果逐函数对比，每条差异都有解释。
  - Core 不超过 13,000 行。
  - 按"只保留一种实现"的规定，旧的按需解析整体删除。
