# SetterChecker V2 重构施工方案

> 状态：S1–S3 已由审阅方亲自实施（施工方版本未通过验收），S4a 待施工。编写日期 2026-09-24。
> 依据：r5 报告（`%TEMP%/SetterChecker-unifiedrules-20260924-r5/report.json`，`-j8`）、同一 dll 的 `-j1` 对照运行、源码审查，以及用户 2026-09-24 确认的规则。
> 施工方：执行模型。每个阶段结束后必须停下，交付审阅方审查，通过后才能进入下一阶段。

---

## 0 施工须知

### 0.1 阅读顺序与优先级

1. 先读 `AGENTS.md`、`Docs/PROJECT-PLAN.md`，再读本文件。
2. 本文件第 2 章是用户 2026-09-24 确认的新规则。它与 PROJECT-PLAN 冲突时，以第 2 章为准，并在 S0 阶段按 2.8 节把这些规则同步写入文档。除 2.8 节列出的条款外，不得修改 PROJECT-PLAN 和 AGENTS 的其他内容。
3. 本文件没写到的地方，按 AGENTS 和 PROJECT-PLAN 执行。仍不确定时停下来提问，不要自行扩展规则。

### 0.2 阶段纪律

- 阶段按 S0 → S1 → S2 → S3 → S4a →（评审后才做 S4b）→ S5 的顺序进行，见第 6 章。
- 从当前 HEAD 新建分支 `refactor-v2`，每个阶段至少一个提交，提交信息写明阶段号。
- 每个阶段结束时依次完成：格式检查、编译、全部测试、khengine 完整运行（`-j1` 一次、`-j8` 三次）、对比脚本（第 8 章），并按第 9 章模板写《阶段交付说明》。写完后停止，等待审阅。
- 不得提前实现后续阶段的内容，也不得把多个阶段合并成一个提交。

### 0.3 硬性边界（沿用 AGENTS）

- `D:/KiHan` 只读。
- 仍然只保留 Core、Cli、Tests 三个项目，七个模块各一份主文件。
- Core 不超过 13,000 个物理行。
- 源码文件一律 UTF-8 无 BOM。每个函数前写一行中文 `//` 注释说明用途。
- 禁止新增任何 SHA-256 相关代码。需要摘要时沿用现有的 SHA-512 / `IncrementalHash`。
- 禁止捕获全部异常后继续运行；禁止按业务函数名或类名写死结论。R3 的诊断类型表是用户授权的唯一例外。
- 只保留一种实现，替换掉的旧逻辑必须删除，不能用开关让新旧两套并存。

### 0.4 关于函数清单

- 第 5 章列出的类型名和函数名是约定接口，必须照此实现。
- 允许新增私有辅助函数，但要在交付说明里列出。
- 标为"删除"的函数必须真正删掉，不能留作死代码。
- 标为"不动"的函数，只允许做编译所需的最小调整。

---

## 1 现状与缺陷

### 1.1 基线数据（r5，`-j8`）

| 项目 | 数值 |
|---|---|
| 可报告函数 | 15,299 |
| 源码标签 NoLogTrack / ShouldTrack | 3,311 / 11,988 |
| 工具结论 NoLogTrack / ShouldTrack | 4,499 / 10,749 |
| 未证明 / 待定调用 | 12 / 15 |
| 分析的不同函数 / 函数体读取次数 | 22,536 / 22,074 |
| 固定调用位置 | 46,578 |
| 材料 | 145 个源码上下文，23,318 个源码文件 |
| 材料及源码上下文 | 4.87 秒（其中编译参数读取 1.85 秒，源码读取解析 2.37 秒） |
| 函数总表 | 3.16 秒 |
| 行为、调用、效果 | 8.57 秒（其中 Setter 判断与传播只有 0.48 秒） |
| 分析期间分配 / GC 暂停 | 4.93 GB / 1.0 秒 |
| 注册扫描 | 23,318 个文件，2,402,197 个语法候选，这一项分配 1.46 GB |
| `-j1` 完整运行 | 37.5 秒 |

### 1.2 缺陷清单

| 编号 | 缺陷 | 证据 | 影响 | 修复阶段 |
|---|---|---|---|---|
| D1 | 结论随线程数变化 | 同一 dll 下 `-j1` 与 `-j8` 相比，有 25 个函数的 Actual、Decision 或 Failure 不同，462 个证据不同。例如 `KHRTDataManager.clearWriteByteStream` 在 `-j8` 为 Setter、`-j1` 为 Getter | 违反 PLAN 第九章，结论不可复现 | S1 |
| D2 | 效果摘要只有一个布尔值，不按调用处的实参换算 | `EffectAnalyzer.Bind` 注释写明"不按实际参数复制行为"；违反 PLAN 第二章第 2 条、第五章 5，以及 AGENTS"不得只缓存一个布尔值" | 构造函数约 699 个、out/ref 参数约 557 个、foreach 枚举器约 110 个、局部集合操作若干被误判为 Setter，合计约 1,400–2,000 个（上限） | S2 |
| D3 | 构造函数豁免只对结构体生效，而且依赖执行顺序 | `IsNewObjectConstruction` 第 221–231 行；`Bind` 第 191–196 行在检查豁免前就登记了调用者；`Propagate` 第 332–343 行传播时不再检查豁免；`m_newObjectReturns` 被移除时不会重新通知调用者 | D1 的主要来源；`FVector3`、`FScalar` 这类结构体的构造也会传染 | S1 修顺序问题，S2 彻底替换 |
| D4 | 追踪分析每轮都完整计算一遍，但最终决定不用它 | `AnnotationEvaluator` 第 38–40 行只用真实结果 `actual` | 白白多算一半效果；与 PLAN 第三章字面冲突，按 R1 删除 | S2 |
| D5 | 懒加载单例和缓存被判为 Setter | 证据链终点是 `get_Instance :: ms_instance` 等 | 约 800 个（上限） | S2（R2） |
| D6 | 调试日志和 Profiler 被当作写入 | `Debuger.InitLogFile :: LogFileWriter`、`Profiler.EndSample` 原生函数 | 259 个 | S2（R3） |
| D7 | 材料包含编辑器和测试程序集 | 66 个程序集，5,972 个文件 | 材料、函数总表和注册扫描都变慢；Editor 里的实现会冒充真机调用目标 | S3（R4） |
| D8 | 注册扫描要遍历全部语法 | 见 1.1 | 约 1.4 秒，分配 1.46 GB | S3 |
| D9 | 热路径上用长字符串做字典键 | 函数身份字符串有上百个字符；`TargetIdentity` 每次都拼接字符串 | 分配 4.9 GB，GC 暂停 1 秒 | S4a |
| D10 | 按需调度、增量失效的机制过于复杂 | `ResolveAsync` 主循环加上 `ValueSourceIndex` 的需求登记，`CallTargetResolver.cs` 共 3,631 行 | 维护困难，也是顺序问题的温床 | S4b（需先评审） |
| D11 | 编辑器宏隐藏的真机代码对工具不可见 | 工具使用编辑器编译参数（定义了 `UNITY_EDITOR`）；khengine 有 10 处 `#if !UNITY_EDITOR`、7 处 `#if UNITY_EDITOR … #else` | 潜在的第二类风险；目前规模很小 | S2（R7，报告单列） |

---

## 2 已确认规则（用户 2026-09-24）

### R1 日志决定只看真实行为

- 删除"标签生效后的追踪分析"这一整轮计算，以及它的全部证据字段（`TrackingMethods`、`TrackingFailures`、`TrackingEvidence`、`tracking` 参数）。
- 可信 NLT（方法级带 Reason、裸 NLT、类级 NLT）只决定函数自身的日志决定为 NoLogTrack 或 NLTClass。它内部的写入照常让调用它的上层函数成为 Setter。
- 原因：如果 NLT 函数内部的写入不让上层记录，这次写入在日志里就彻底消失了，会造成第二类错误。
- 解析器的剪枝条件从"真实结果和追踪结果都已是 Setter"改为"TOP"，定义见 4.6。

### R2 懒加载初始化不算修改

只认源码中的以下三种写法；IL 读取的函数不适用。

1. `F ??= new T(...)` 或 `F ??= new T[...]`。
2. `F ?? (F = new T(...))`。两处 `F` 必须是同一个字段。
3. `if (F == null) F = new T(...);`，允许用花括号包住这一句。条件还可以写成 `null == F`、`F is null`、`ReferenceEquals(F, null)`、`object.ReferenceEquals(F, null)`。要求这个 `if` 没有 `else`，分支里除空语句外只有这一条赋值。

三种写法都还要满足：

- `F` 是静态字段，或者是经由 `this`（显式或隐式）访问的实例字段。`other.F` 这种访问不适用。
- 赋的值直接就是对象创建或数组创建表达式，不能是函数调用的结果。
- 被赋值的属性不适用，只认字段。自动属性的后备字段也不适用。

满足条件的写入会打上 `BehaviorWrite.IsLazyInitialization = true` 标记，在效果计算中忽略。被创建对象的构造函数照常分析：构造函数写的是新对象自身，不计入；构造函数若写了静态数据或参数，照常计入。

不满足条件的一律按普通写入处理。例如 `if (F == null) { F = new D(); F.Add(1); }` 仍然是 Setter，这是用户的明确选择。

### R3 诊断边界不算修改

下表中类型的**全部成员**（静态和实例方法、属性访问器、构造函数）都视为没有效果：

| 完整类型名 | 程序集简单名 | 用途 |
|---|---|---|
| `KH.Debuger` | `Debuger` | 日志（`Assets/Plugin/MyTool/Debuger.dll`） |
| `KH.KHProfiler` | `kihan.common.runtime` | 性能采样包装 |
| `KH.KHProfilerExt` | `Assembly-CSharp` | 性能采样实现 |
| `UnityEngine.Debug` | `UnityEngine.CoreModule` | 日志 |
| `UnityEngine.Profiling.Profiler` | `UnityEngine.CoreModule` | 性能采样 |
| `UnityEngine.Profiling.CustomSampler` | `UnityEngine.CoreModule` | 性能采样 |

实现要求：

- 必须同时比对完整类型名和程序集简单名，两者都一致才算命中。
- 在 `RuntimeOperations` 中新增 `RuntimeOperation.Diagnostics`。命中的调用不读取函数体、不生成调用目标，返回值视为计算值。传给它们的委托也不会被调用。
- 调用方自己为参数求值时产生的调用（例如插值字符串里调用的 `ToString()`）照常分析。
- `Find()` 必须在"只处理 `System.*`"和"`SourceSymbol != null` 就返回"这两个过滤条件之前完成诊断类型的判断，因为 `KHProfiler` 和 `KHProfilerExt` 是源码类型。

### R4 只分析会进入真机包的程序集

从材料中排除的源码程序集：

- asmdef 的 `includePlatforms` 只包含 `Editor`。
- asmdef 的 `defineConstraints` 包含 `UNITY_INCLUDE_TESTS`。
- Unity 预定义的 `Assembly-CSharp-Editor` 和 `Assembly-CSharp-Editor-firstpass`（由 `Assets/**/Editor/` 目录归属产生）。

例外：如果一个保留下来的程序集引用了被排除的程序集，被引用方仍然参与编译（否则编译不过），但标记 `IsCandidateSource = false`，不作为实现、重写、注册的候选来源，也不进入报告。

khengine 本身如果被判为编辑器程序集，直接报错停止。

元数据引用（例如 `UnityEditor.dll`）保持原样。本规则只影响源码上下文。

**不做：仅编译的编辑器程序集改用构建产物 DLL 引用（2026-09-28 实测后放弃，用户确认）。** khengine 上有 60 个编辑器程序集因被 `Assembly-CSharp` 等在编辑器构建图中引用，仍需参与编译。曾试验改为引用它们在 `Library` 下的构建产物 DLL，结果：

- 解析的源码文件从 19,167 个降到 17,318 个，但源码解析本来就是并行的，材料耗时没有下降（4.2 秒对 4.9 秒，在噪声范围内）。
- 编辑器 DLL 的 IL 函数体被当作外部实现读入，读取的函数多出约 1.2 万个。这些 DLL 的依赖只写在已丢弃的编辑器编译参数里，大量报"托管程序集依赖不存在"：未证明从 8 个变为 503 个，分析耗时从约 15 秒涨到 47 秒。
- Bee 的 `.ref.dll` 能对应到三份内容相同的实现文件，还得放宽"实现文件唯一"的校验。

结论：没有性能收益，结论反而变差，继续按源码编译。

### R5 创建对象并返回，本身不算修改（用户 2026-09-24 确认）

规则：

1. **新对象**只认本函数内真正创建的对象：`new` 表达式、新数组、`MemberwiseClone`，以及调用一个 `Return.Fresh` 为真的函数得到的结果。从对象池、缓存、列表、字典里取出的对象**不是**新对象；取出这个动作本身通常就会修改池子，照常判为 Setter。
2. 只写新对象自己的字段不算修改，包括构造函数、对象初始化器、`Clone` 里给副本赋值。这与注入器 `LogTrackInjector.CheckFilterMethod` 对构造函数不注入日志的做法一致。
3. 创建过程中对已有状态的任何写入，照常判为 Setter，与是否返回新对象无关。包括：写静态数据（编号计数器、注册表）、写参数对象、把新对象放进自身字段或管理器。
4. 新对象里存着已有对象的引用时，之后经由新对象修改这些已有对象，必须追回原对象算写入（不变式 I4）。
5. 把返回的新对象保存或注册到世界状态的调用方是 Setter，日志会在那一步记录。

推论：返回值来源未知时，函数自身不因此变成"未证明"。只有调用方要通过这个返回值写东西时，这次写入才记为 Unknown。

调查依据（r5 报告、`D:/KiHan` 源码，按函数体正则识别，数字为近似值）：

- 可报告函数中有 894 个返回新建对象。源码打了 NLT 的 168 个，没打的 726 个。
- 打了 NLT 的多数是"查询后返回新列表或结构体"的函数，例如 `getLiveMonsterList`、`getChildrenByName`、`XMLValue.StringToVector2`、`KHExplicitCommonScriptDataXML.Decode*`。其中工具判为 Setter 的 62 个，逐组核对后**全部是工具误判**（枚举器、构造函数、out 局部变量）。
- 没打 NLT 的最大一组是 `Clone`：357 个，打了 NLT 的只有 1 个。全项目 70 处 `[LogTrack]` 都在 CheckSum 自动生成的 setter 上，没有任何工厂或 `Clone` 被强制记录。所以没打 NLT 的原因是手动补标不全，而不是有意要记日志。
- 注入器对构造函数、属性 getter、`getInstance` 一律不注入日志。
- 创建时顺带修改已有状态的真 Setter 确实存在，规则 3 必须抓住它们：`KHBattleScene.DoCreatePlayerActor`（放进场景）、`BattleInvokeLater.Invoke`（注册定时器）、`CustomTypeRegister.RegisterFColor`（注册静态表）、`KFLLocalBlackboardPool.AllocScope`（递增 `m_nextScopeId`）、`OldAIRuntime.CreatAI`（写入参角色的 `m_btId`）。
- 相关发现：`KHRTUtils.GetSid()` 打了裸 `[NoLogTrack]`，函数体却是 `return _sid++;`，属于手动 NLT 盖住真 Setter，应由"裸 NLT 缺少 Reason"告警报出。

### R6 静态计数器仍然是 Setter

例如 `Poolable` 构造函数里的 `Interlocked.Increment(ref s_poolOnlyIDCounter)`，不在 R2、R3 的范围内。

### R7 报告单独列出被编辑器宏隐藏的真机代码

- 对 khengine 的可报告函数，如果函数体内有因 `UNITY_EDITOR` 条件编译而未启用的代码（`#if !UNITY_EDITOR` 分支，或 `#if UNITY_EDITOR` 的 `#else`/`#elif` 分支），在报告中单列函数名和行号。
- 这一条不改变判断结果，只提示人工复核。

### R8 标准库自身的静态字段不是战斗状态（用户 2026-09-28 确认）

- 声明类型属于 `mscorlib`、`netstandard`、`System.Private.CoreLib`、`System`、`System.*`、`Microsoft.*` 程序集的静态字段（区域设置、资源字符串等运行时内部缓存），对它们的读写不算写静态数据，也不触发 TOP。
- 实例字段、业务程序集的静态字段不受影响。

### R9 带参数的空函数是日志打点（用户 2026-09-28 确认）

- 日志注入器特意不过滤空函数（`LogTrackInjector.CheckFilterMethod` 注释："空函数，不可以直接过滤，有些需要注入打印语句"），`LogTrack_ClockTick(int actorId)` 这类函数就是为了记录参数而存在的。
- 源码中声明了参数、函数体没有任何语句、且没有 NLT 标签的函数，日志决定为 ShouldTrack，不建议 NoLogTrack。真实行为仍按 Getter 报告。
- 无参数的空函数（如可重写的空虚函数 `OnLateTick`）不受影响。

### 2.8 文档同步清单（S0 执行，用户已授权）

1. PROJECT-PLAN 第三章"可信豁免内部的写入不使上层需要追踪，但上层自己的写入仍单独判断"改为：
   > 日志决定只看真实行为。可信豁免只决定函数自身不追踪；其内部写入照常使上层成为 Setter（用户 2026-09-24 确认）。
2. PROJECT-PLAN 第七章追加一条：
   > 只分析会进入真机包的源码程序集；编辑器和测试程序集仅在被引用时参与编译，不作为实现或注册候选（用户 2026-09-24 确认）。
3. PROJECT-PLAN 第八章第 1 条后追加：
   > 例外：R3 诊断类型表与 R2 懒加载写法是用户授权的通用规则，定义见 `Docs/REFACTOR-V2.md`。
4. AGENTS.md"语义与标签"一节中关于"可信 NLT 内部的写入不再使上层需要追踪…真实分析与标签生效后的追踪决定保留不同证据"的两句，替换为 R1 的文本；并追加一句"懒加载与诊断边界规则见 `Docs/REFACTOR-V2.md` R2、R3"。
5. PROJECT-PLAN 第十二章末尾追加一个小节"V2 重构"，只写一句"按 `Docs/REFACTOR-V2.md` 分阶段实施，进度见该文件第 6 章"，不贴过程记录。
6. PROJECT-PLAN 第二章第 3 条"创建 khengine 业务对象并将它返回、保存、注册或传给外部"改为：
   > 创建对象并返回，本身不算修改；创建过程中写入已有对象、静态数据或参数，以及把新对象保存、注册到已有状态或传给会保存它的外部函数，属于 Setter（用户 2026-09-24 确认，细则见 `Docs/REFACTOR-V2.md` R5）。

   AGENTS.md"语义与标签"一节中"创建并返回、保存、注册或传出 khengine 业务对象为 Setter"一句，同步改为上面的文本。

---

## 3 验收总标准

### 3.1 确定性

- `-j1`、`-j4`、`-j8` 生成的 `report.json`，删除 `Timings`、`TimingEntries`、`OperationCounts` 三个键，以及 `AnalysisCost` 下所有计时和分配字段后，必须逐字节一致。
- `preview.patch` 必须逐字节一致。

### 3.2 第二类错误护栏

- 第 4.10 节列出的安全不变式 I1–I10 必须全部满足。
- 每个阶段的对比报告中，所有"旧版 Setter、新版 Getter"的函数都必须被自动归入第 8 章的某个类别。不能归类的，要逐条写明源码位置和理由，由审阅方抽查。
- 不允许出现"旧版 Setter、新版 Getter"而且新版没有任何证据的情况。

### 3.3 第一类错误（精度）

- S2 完成后，ShouldTrack 至少减少 1,000 个，预期 1,500–2,500 个。达不到要写明原因。
- 所有"旧版 Getter、新版 Setter"的变化都要逐条说明原因。

### 3.4 性能

性能指标取完整 `analyze` 命令的墙钟时间（不含工具编译），`-j8` 连续三次取中位数：

| 阶段 | 目标 |
|---|---|
| S2 | ≤ 19 秒（允许比基线多 2 秒，因为剪枝变弱） |
| S3 | ≤ 12 秒 |
| S4a | ≤ 9.5 秒，分配 ≤ 2.5 GB，GC 暂停 ≤ 400 毫秒 |
| S4b（如实施） | ≤ 7 秒 |

### 3.5 代码量

- S5 完成时 Core ≤ 11,500 个物理行；如果实施了 S4b，≤ 9,500 行。
- 统计方式与 AGENTS 相同。

### 3.6 测试

- 现有测试全部通过。因规则变化而改变预期的测试，逐条写明依据的规则编号（第 7.1 节）。
- 第 7.2 节列出的新增测试全部通过，其中要求源码和 DLL 两种来源的，两种都要跑。

---

## 4 终态核心设计

### 4.1 流水线

```text
M1 MaterialLoader   读取编译参数，按 R4 筛选程序集，解析源码，并在同一趟遍历中建立标识符索引
M2 MethodCatalog    函数总表；类型和函数带连续编号；实现候选只来自 IsCandidateSource 的程序集
M3 BehaviorReader   读取函数行为事实（新增 IsLazyInitialization、HiddenPlayerCode）
M4 CallTargetResolver
                    解析调用目标、值来源和注册关系；用 TopTracker 剪枝（S2 起）；
                    S3 起用标识符索引查找注册位置；S4b 起改为分批闭包（需评审）
M5 EffectAnalyzer   解析完成后，在调用图上按强连通分量从被调用方到调用方计算函数摘要（槽位 × 深浅）
M6 AnnotationEvaluator
                    只用真实行为做日志决定（R1）；标记 R7
M7 ReportWriter     输出报告，新增三个小节
```

### 4.2 名词

- **槽位（slot）**：0 表示当前对象（receiver），1 到 n 依次表示第 0 到第 n−1 个参数，63 表示返回值（只出现在别名关系里）。参数超过 62 个的函数直接记为 Unknown。
- **根（root）**：一个值在本函数内最终来自哪里，取值为 `Local`、`Receiver`、`Parameter(i)`、`Static`、`Fresh(site)`、`Foreign`、`Unknown(reason)` 之一，另外带一个"深"标记。
- **浅写与深写**：
  - 浅写是直接改槽位对象本身的字段或元素，或者改 ref/out 变量本身。
  - 深写是改"从槽位对象出发，至少经过一次引用类型成员才到达的对象"。
  - 经过值类型成员（在结构体内部原地写）不算加深。
- **新对象站点（site）**：本函数内 `NewObject`、`NewArray`、`Iterator`、`ShallowCopy`、`StackAllocation` 值的编号，或者"返回新对象的调用"的结果值编号。
- **别名根（AliasRoots(site)）**：新对象里可能存着的、本函数已有对象的根。
- **Foreign**：值来自另一个函数，例如委托绑定对象、闭包捕获的外层变量、迭代器捕获的实参。
- **TOP**：与调用者无关、一定是 Setter 的状态，包括直接写静态数据、调用 TOP 函数、调用未固定目标的委托参数。

### 4.3 数据结构（放在 `EffectAnalyzer.cs` 内）

```csharp
// 值在本函数内的最终来源。
internal readonly record struct EffectRoot(EffectRootKind Kind, int Slot, int Site, bool Deep, string? Reason);

internal enum EffectRootKind { Local, Receiver, Parameter, Static, Fresh, Foreign, Unknown }

// 返回值（或 out/ref 参数写出值）的来源说明。
internal struct ValueSummary
{
    internal ulong Slots;      // 可能是哪些槽位的对象本身
    internal ulong DeepSlots;  // 可能是从哪些槽位经引用成员到达的对象
    internal bool Fresh;       // 可能是新对象
    internal bool Static;      // 可能来自静态数据
    internal string? Unknown;  // 来源不明的原因；为空表示已知
}

// 一个函数的完整效果摘要。
internal sealed class MethodSummary
{
    internal ulong Shallow;                  // 浅写了哪些槽位
    internal ulong Deep;                     // 深写了哪些槽位
    internal bool Static;                    // 写了静态数据，或为 TOP
    internal string? Unknown;                // 首个未确定原因
    internal Dictionary<int, ulong>? Into;   // 目标槽位（含 63=返回值）→ 被存进其中的来源槽位
    internal ValueSummary Return;
    internal Dictionary<int, ValueSummary>? Outs;  // ref/out 参数下标 → 写出值的来源
    internal SummaryCause?[] Causes = new SummaryCause?[130];  // 每个标志位的首个原因，用于证据
    internal bool IsEmpty => Shallow == 0 && Deep == 0 && !Static;
}

// 某个标志位的首个原因：直接写入、调用某函数或未确定。
internal sealed record SummaryCause(BehaviorFlowPoint Point, string? CalleeId, int CalleeBit, string Detail);
```

`Causes` 下标：0–63 对应 `Shallow` 位，64–127 对应 `Deep` 位，128 对应 `Static`，129 对应 `Unknown`。

### 4.4 根来源 `ReadRoots(method, value)`

从现有的 `EffectAnalyzer.ReadSubjects` 改写而来，保留它的遍历结构和 `MemberAccess` 链处理。先用 `ValueSourceIndex.ReadLocalOrigins`（它已经处理了 `SlotRead`、`Merge`、`Conversion`、`CapturedVariable` 和解析器写入的特殊结果），再对得到的每个来源按下表处理：

| 来源 | 结果 |
|---|---|
| 来源值属于其他函数（`origin.Reference.MethodId != method`） | 值是 `Local`、`Constant`（被捕获的值类型局部变量）时为空；其余一律为 `Foreign`，包括其他函数里的 `NewObject`，因为那个对象可能已经存进了字段。旧代码把这种情况当作无影响，是一个漏洞 |
| `CurrentInstance` | `Receiver`，深标记继承 |
| `Parameter` | `Parameter(ParameterIndex)`，深标记继承 |
| `Local`、`Constant`、`Computation`、`Type`、`Function`、`StackAllocation` | 空 |
| `NewObject`、`NewArray`、`Iterator` | `Fresh(site = 该值编号)`，深标记继承 |
| `ShallowCopy` | `Fresh(site)`；该站点的别名根加上被复制对象的根（深） |
| `FieldRead`，没有输入（静态字段） | `Static` |
| `FieldRead` 或 `ArrayElementRead`，有容器输入 | 容器的根。成员是引用类型（`IsReferenceStorage == true`）时设深标记；为 `null` 时记为 `Unknown` |
| `Address`，指向局部变量或参数槽 | 有成员链时按旧的 `ReadMemberValues` 继续，否则为空 |
| `Address`，指向字段 | 与 `FieldRead` 相同，但写的是地址所在的存储本身，不加深 |
| `ValueCopy` | 与旧逻辑相同：成员链经过引用成员时继续追输入（设深），为 `null` 时 `Unknown`，否则为空 |
| `CallResult`（普通返回） | 用 `ValueSourceIndex` 找到该调用对应的 `ResolvedCall`，对每个目标用被调函数的 `Return` 摘要换算，规则见表后 |
| `CallResult`（带 `ParameterIndex` 的 out 结果） | 用被调函数 `Outs[index]` 按同样方式换算 |
| 调用未解析、有失败、没有目标 | `Unknown(failure ?? "调用目标尚未确定")` |
| 其他（`Exception` 等） | `Unknown("写入对象来源尚未确定")` |

`CallResult` 用被调函数的 `Return` 摘要换算：

- `Slots` 的每个位：取对应实参的根，深标记继承。
- `DeepSlots` 的每个位：取对应实参的根，并设深标记。
- `Fresh`：得到 `Fresh(site = 调用结果值编号)`；该站点的别名根加上被调函数 `Into[63]` 中各来源槽位所对应实参的根（深）。
- `Static`：得到 `Static`。
- `Unknown`：得到 `Unknown`。

实现要求：

- 结果按"方法 + 值 + 深标记"缓存。
- 用访问集合防止无限递归：遇到正在计算的项时返回空，并在同一强连通分量的迭代中补全。
- 同一强连通分量内调用的函数，使用它当前的近似摘要。

### 4.5 摘要计算 `Summarize(method)`

```text
先处理没有可执行函数体的情况：
  body 读取失败，或 BodyKind 不是 Executable：
    有 RuntimeOperations 规则 → 用 4.9 节的规则效果
    否则                      → Unknown = 原失败文本（保持现有原因文本不变，报告和人工基线依赖它）

1. 写入：来源包括 body.Writes、ValueSourceIndex.ReadReflectionWrites(method)，以及各 ResolvedCall.Writes
   对每个写入 w：
     跳过 w.IsLazyInitialization 的写入（R2）
     targets = w.ReceiverValueId 有值 ? ReadRoots(receiver) : { Static }
     values  = 写入的值是引用类型或未知 ? ReadRoots(w.ValueId, deep: true) : 空
     对 targets 中每个根 t 调用 Contribute(t, 本次写入是深写 = t.Deep)
     记录别名：
       t 是 Receiver 或 Parameter(j)：values 中每个 Receiver / Parameter(i) → Into[j 的槽位] |= i 的槽位
       t 是 Fresh(site)：把 values 加入 FreshAlias[site]

2. 调用：遍历 body.Calls 中的每个 call，找到对应的 ResolvedCall
   没有 ResolvedCall：
     本函数是 TOP → 跳过（这是被剪枝的调用）
     否则         → Unknown("调用目标尚未确定")
   有 PendingCall 失败 → Unknown(失败文本)
   有 RuntimeRule      → 按 4.9 节规则效果，用本调用的实参换算
   InvokesUnboundParameter → Static，Detail 写"调用未固定目标的委托参数，合法回调允许修改状态"
   ValuesOnly（迭代器 get_Current）→ 不计效果
   对每个目标 target，取 S = summary[target]：
     S.Static         → Static（原因记为调用 target 的 Static 位）
     S.Unknown        → Unknown（沿用被调函数的原因文本）
     S.Shallow 的每个位 b：
       actual = b == 0 ? target.Receiver : target.Arguments[b - 1]
       actual 中任何一个值不属于本函数（Foreign 绑定）→ Static，Detail 写"修改委托或迭代器绑定对象"
       否则对 actual 的每个根 r：Contribute(r, 深写 = r.Deep)
     S.Deep 的每个位 b：同上，但对每个根都按深写：Contribute(r, 深写 = true)
     S.Into：对每个 (目标槽位 d, 来源槽位集合)，把来源实参的根存进目标实参的根：
       目标根是 Fresh(site) → 加入 FreshAlias[site]
       目标根是 Receiver / Parameter → 加入本函数的 Into
       d == 63             → 加入本次调用结果站点的别名根
   构造调用（ObjectCreation）：接收对象就是新建值，按 Fresh(site) 处理，所以构造函数的浅写自然被忽略

3. 返回：对每个返回值 v，把 ReadRoots(v) 合并进 Return：
     Receiver / Parameter → Slots 或 DeepSlots
     Static               → Static
     Fresh(site)          → Fresh = true，并把 AliasRoots(site) 中的槽位加入 Into[63]
     Foreign / Unknown    → Unknown
4. ref/out 参数：对目标根为 Parameter(i)（且参数 i 是 ref/out）的浅写，把写入值的根合并进 Outs[i]

Contribute(root, 是否深写)：
  Receiver      → 深写 ? Deep |= bit0 : Shallow |= bit0
  Parameter(i)  → 按同样方式设置 bit(i + 1)
  Static        → Static = true
  Fresh(site)   → 深写时：对 AliasRoots(site) 中每个根 a 调用 Contribute(a, 深写 = true)
                  浅写时：不计
  Local         → 不计
  Foreign       → Static = true，Detail 写"修改其他函数中的对象"
  Unknown       → 仅当 Unknown 为空时记录原因

AliasRoots(site)：在本函数内迭代求解
  FreshAlias[site] 中的每个根，其中若是 Fresh(site2)，再并入 AliasRoots(site2)；用访问集合防环
```

结论判定：

- Setter：`Static` 为真，或 `Shallow | Deep != 0`。Setter 的结论优先于 Unknown。
- 未证明：不满足 Setter 条件，但 `Unknown` 不为空。
- Getter：两者都不满足。

### 4.6 TOP 与剪枝（`EffectAnalyzer.TopTracker`，S2 引入，替换 `FunctionEffects`）

解析器仍然需要"这个函数已经确定是 Setter，不必继续展开它的其他调用"这种剪枝来控制耗时。R1 之后只允许按 TOP 剪枝：

- 直接写入的根里有 `Static`（跳过 R2 的懒加载写入；R3 的诊断调用不计）→ TOP。
- 调用了 TOP 函数 → TOP。TOP 与调用者无关，所以传播时不需要检查任何豁免，与执行顺序无关。
- `InvokesUnboundParameter` → TOP。
- 调用带运行时规则的函数，规则效果的写入根里有 `Static` → TOP（例如往静态列表里 `Add`）。
- 写 Receiver 或参数**不算** TOP，Unknown 也不算。

`TopTracker` 计算根时不使用被调函数的摘要：`CallResult` 一律视为 `Unknown`，也就是不会因此变成 TOP。这是保守做法，只会少剪枝，不会影响正确性。

原来调用 `FunctionEffects` 的地方逐一替换：

- `effects.IsSettled` → `top.IsTop`
- `effects.Bind` → `top.Bind`
- `effects.ReadDirect` → `top.ReadDirect`
- `effects.Settled` 事件 → `top.Settled`

`stopAfterTarget` 的写法不变。

### 4.7 强连通分量求解与确定性

1. 调用图的节点是 `CallTargetResolutionResult.Methods`，边来自每个 `ResolvedCall` 的所有目标。邻接表按目标的 `MethodId` 用 `StringComparer.Ordinal` 排序。
2. 用迭代版 Tarjan 算法求强连通分量，起点按 `MethodId` 顺序遍历。按"被调函数所在分量先处理"的顺序求解。
3. 对每个分量反复循环：分量内的函数按 `MethodId` 顺序逐个 `Summarize`，摘要有变化就再来一轮，直到一轮内没有任何变化。摘要只增不减，所以一定会结束。每次重新计算都计入 `SummaryUpdates`。
4. 内部编号（例如 S4a 的整数序号）只能用作字典键，**不得**影响任何输出顺序或证据选择。所有排序和打破平局都用字符串 `Id` 做 Ordinal 比较。

### 4.8 证据

- 每个标志位只保留一个原因：`Point` 较小（先比 `BlockId`，再比 `Order`）的优先；`Point` 相同时，比 `CalleeId` 的 Ordinal 顺序。
- 证据链从根函数的 Setter 原因开始：优先取 `Static`，其次 `Shallow`、`Deep` 的最低位，沿 `CalleeId` 和 `CalleeBit` 逐层向下追到直接写入为止，遇到环就停。
- 输出格式保持现在的 `EffectEvidence(MethodPath, Position, Detail)`：`Position` 为链尾写入的 `BlockId`，`Detail` 为字段名、写入类型或原因文本。
- 未证明函数的证据改用 `Unknown` 位的链。

### 4.9 运行时规则的效果（`RuntimeOperations.ReadEffect`）

| RuntimeOperation | 效果 | 返回值 |
|---|---|---|
| `ComputeValue`、`ConvertValue`、`CompareValues`、`CompareReferences`、`ReadIdentityHash`、`BoxValue`、`UnboxValue`、`ReadArrayShape`、`ReadObjectType`、`ReadTypeMetadata`、`ReadMemberMetadata`、`CompareTypes`、`FindType`、`FindMember`、`AtomicRead`、`Diagnostics`（新增） | 无 | 计算值（空） |
| `ConvertValue` 中的 `TryParse` | 浅写 out 参数所在的槽位 | 计算值 |
| `ReadCollection` 中的 `get_Item`、`get_Keys`、`get_Values` | 无 | `DeepSlots = 接收对象` |
| `ReadCollection` 中的 `TryGetValue` | 浅写 out 参数所在的槽位；`Outs[out] = DeepSlots 接收对象` | 计算值 |
| `ReadCollection` 中的其他成员 | 无 | 计算值 |
| `CreateObject` | 无（构造的接收对象是新对象） | 有返回值时为 `Fresh` |
| `CreateArray` | 无 | `Fresh` |
| `ShallowCopy` | 无 | `Fresh`，`Into[63] = 接收对象` |
| `WriteCollection`（新增，见 5.8） | 浅写接收对象；`Into[0] \|= 所有引用类型实参` | 同类 `ReadCollection` 的规则 |
| 其余尚未建模的类别 | `Unknown("运行时规则尚未建模效果：<类别>")` | `Unknown` |

S2 必须用一个测试确认：现有 `Find()` 返回的每一种规则都在上表中有明确的效果。

### 4.10 安全不变式（第二类错误护栏）

- **I1** `Unknown` 永远不能变成 Getter。
- **I2** 实参或接收对象属于其他函数的绑定（委托、迭代器、闭包）时，被调函数的任何对象写入都按 `Static` 处理。
- **I3** `Fresh` 只能来自 4.4 节列出的来源，以及被调函数 `Return.Fresh` 的调用结果；其他情况不得推断为新对象。
- **I4** 深写经过新对象时，必须传到它的别名根上。`Into` 必须记录参数到自身对象、参数到参数、参数到返回值三种存储关系。
- **I5** 懒加载只认 R2 的三种写法，只适用于源码。
- **I6** 诊断边界只认 R3 表中的类型，而且类型名和程序集名都要一致。
- **I7** 参数超过 62 个的函数记为 `Unknown`。
- **I8** 函数体读取失败、原生函数或运行时实现，没有运行时规则时一律为 `Unknown`，沿用现有原因文本。
- **I9** `InvokesUnboundParameter` 按 `Static` 处理。
- **I10** 解析器只能按 TOP 剪枝。被剪枝的调用只允许出现在 TOP 函数里。

---

## 5 模块规范与函数清单

说明：以下"删除 / 新增 / 修改"表中未提到的函数一律不动。

### 5.1 `SetterChecker.cs`（总入口）

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 修改 | `SetterChecker.AnalyzeAsync` | 顺序改为：材料 → 函数总表 → 根函数 → 解析 → `new EffectAnalyzer().Analyze(catalog, analysisRoots, calls, ct)` → 标签。删除 `AnalyzeAvailable(..., false, ...)` 的调用。`Timings` 新增"函数摘要"一项。 |
| 修改 | `AnalysisTiming.Part` 和 `ReadSeconds` | 新增 `Summaries`，对应名称"函数摘要"。S4b 若删除了某些阶段，同步删除对应的枚举值和名称。 |
| 修改 | `MaterialSet` | S3：新增 `IReadOnlyDictionary<string, int[]> IdentifierIndex`（值为文件序号，升序）和 `IReadOnlyList<string> ExcludedEditorAssemblies`。 |
| 修改 | `SourceAssemblyMaterial` | S3：新增 `bool IsCandidateSource`，默认 true，R4 例外时为 false。 |
| 不动 | `AnalysisRun`、`AnalysisException`、`MaterialRequest`、`ExternalAssemblyMaterial`、`CompilationOrigin` | — |

### 5.2 `MaterialLoader.cs`（M1，S3）

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 修改 | `LoadAsync` | 在 `ReadResponseClosure` 之后调用 `SelectPlayerAssemblies`，只对保留下来的响应解析和编译源码；把 `IdentifierIndex` 和 `ExcludedEditorAssemblies` 写入 `MaterialSet`；`Timings` 新增"编辑器程序集排除"和"标识符索引"两项。 |
| 修改 | `ReadBuildOutputs` | 不再用 `File.ReadAllText` 加 `JsonDocument.Parse` 解析整个构建图，改为调用 `ReadCscNodes` 流式读取。输出和错误信息保持不变。 |
| 修改 | `ReadAssemblyDefinition` | 返回值扩展为 `(string Name, bool EditorOnly, bool TestOnly)`。`TestOnly` 表示 `defineConstraints` 包含 `UNITY_INCLUDE_TESTS`。所有调用处同步修改。 |
| 修改 | `RefreshSourcePaths` | 在已有的 asmdef 遍历中顺便收集每个程序集的 `AssemblyScope`，作为第二个返回值输出。**不要**另外再遍历一次目录。 |
| 修改 | `ParseSourcesAsync` | 在同一个并行循环里，对每棵语法树收集标识符：遍历所有 `IdentifierToken` 的 `ValueText`；再加两个伪标识符：`new` 关键字后面紧跟 `(` 时加 `"new()"`，`this` 或 `base` 关键字后面紧跟 `(` 时加 `".ctorinit"`。每棵树的结果去重，作为 `ParsedSource.Identifiers` 缓存（会话复用时一并复用）。 |
| 删除 | `ReadEditorOnlyReportAssemblies` | 由 `AssemblyScope` 代替。 |
| 新增 | `record AssemblyScope(bool EditorOnly, bool TestOnly)` | — |
| 新增 | `SelectPlayerAssemblies(IReadOnlyList<CompilerResponse> responses, IReadOnlyDictionary<string, AssemblyScope> scopes, string rootAssemblyName)` | 按 R4 返回 `(保留的响应, 仅用于编译的程序集名称集合, 排除的程序集名称)`。root 程序集被判为编辑器程序集时抛出 `AnalysisException`。结果按程序集名的 Ordinal 顺序排序。 |
| 新增 | `ReadCscNodes(string graphPath, string projectRoot)` | 用 `Utf8JsonReader` 流式读取 `Nodes` 数组，只保留 `Annotation` 以 `"Csc "` 开头的节点的 `Inputs` 和 `Outputs`。结果与旧实现完全一致（S3 要为此加一个对照测试）。 |
| 新增 | `BuildIdentifierIndex(IReadOnlyList<SourceAssemblyMaterial> assemblies, IReadOnlyDictionary<string, ParsedSource> trees)` | 生成"标识符 → 文件序号数组"。文件序号 = 在所有候选源码程序集中，按程序集名、再按源码路径（都用 Ordinal 比较）排序后的下标。只收录 `IsCandidateSource` 的程序集。 |
| 修改 | `ParsedSource` | 新增 `string[] Identifiers`。 |
| 不动 | 其余函数，包括 `ReadCompilationKey`、`ResolveExternalAssembliesAsync`、`BuildCompilation`、`LoadGenerators` 以及各路径和身份校验 | — |

### 5.3 `MethodCatalog.cs`（M2）

| 操作 | 函数或类型 | 阶段 | 要求 |
|---|---|---|---|
| 修改 | `TypeEntry` | S3 | 新增 `bool IsCandidate`，按所在源码程序集的 `IsCandidateSource` 设置；DLL 类型一律为 true。 |
| 修改 | `ReadChildIndex(bool interfaces)`、`ReadInheritedTypes`、`RequireClosedDispatchIndex` | S3 | 实现和重写候选里排除 `IsCandidate == false` 的类型。 |
| 修改 | `MethodEntry`、`TypeEntry` | S4a | 新增 `int Ordinal`，创建时通过目录内的计数器分配（线程安全）。只能用作内部字典键（4.7 第 4 条）。 |
| 修改 | `MethodCatalogResult` | S4a | 新增 `MethodEntry MethodAt(int ordinal)`、`TypeEntry TypeAt(int ordinal)`。 |
| 检查 | 所有惰性缓存，包括 `ReadSourceOverrides`、`ReadCachedMethodDefinition`、`ReadSourceSymbols`、`ReadReflectionMembers`、`GetManagedModule` 等 | S1 | 它们会被 `BehaviorReader` 的并行读取同时访问，必须是线程安全的：用 `ConcurrentDictionary.GetOrAdd`，或者加锁；并且缓存的值必须与计算顺序无关。发现非线程安全的缓存时，按 D1 修复并在交付说明中列出。 |
| 不动 | 其余函数 | — | — |

### 5.4 `BehaviorReader.cs`（M3，S2）

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 修改 | `BehaviorWrite` | 新增 `public bool IsLazyInitialization { get; init; }`。 |
| 修改 | `MethodBehavior` | 新增 `public IReadOnlyList<int> HiddenPlayerCodeLines { get; init; } = Array.Empty<int>();`。 |
| 新增 | `SourceBehaviorBuilder.IsLazyInitialization(IOperation assignment)` | 按 R2 判断：输入是 `ISimpleAssignmentOperation` 或 `ICoalesceAssignmentOperation`，沿 `Parent` 检查是否处在三种写法之一中。字段比对用 `SymbolEqualityComparer.Default`；实例字段还要求实例是 `IInstanceReferenceOperation`。 |
| 修改 | `SourceBehaviorBuilder.Write` 及处理 `ICoalesceAssignmentOperation` 的分支 | 生成 `BehaviorWrite` 时写入 `IsLazyInitialization`。 |
| 新增 | `SourceBehaviorBuilder.ReadHiddenPlayerCode(SyntaxNode declaration)` | 遍历声明范围内的 `DirectiveTrivia`。对 `#if`/`#elif`/`#else` 链：若链中 `#if` 或 `#elif` 的条件文本包含 `UNITY_EDITOR`，而某个分支 `BranchTaken == false`，并且该分支后面有 `DisabledTextTrivia`，就记录该段首行行号（从 1 开始）。只对报告程序集的函数计算。 |
| 修改 | `SourceBehaviorBuilder.Read` | 调用 `ReadHiddenPlayerCode`，写入 `HiddenPlayerCodeLines`。 |
| 确认 | `ReadAvailableAsync` | 结果已按 `MethodId` 排序，保持不变。 |
| 不动 | `ManagedBehaviorBuilder`（IL）及其余函数 | — |

### 5.5 `CallTargetResolver.cs`（M4）

**S1 阶段：**

- 如果 D1 的对分定位到本文件的问题（例如对共享字典的并发访问、依赖枚举顺序的集合），按最小改动修复，并在交付说明中写明。

**S2 阶段：**

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 修改 | `ResolveAsync` | 把 `EffectAnalyzer.FunctionEffects effects` 替换为 `EffectAnalyzer.TopTracker top`，替换关系见 4.6。循环结束后的"对未确定函数补一次 `ReadDirect`"（第 449–452 行）改为 `top.ReadDirect`。`requireCompleteCalls` 的判断改为 `!top.IsTop(...)`。 |
| 修改 | `CallTargetResolutionResult` | 删除 `Effects` 属性。 |
| 修改 | `ResolveCall.ReadReflection` | 删除 `List<T>.Add` 特例（约第 761–779 行），改由 `RuntimeOperation.WriteCollection` 处理。 |
| 修改 | `ValueSourceIndex.ReadReturnedOrigins` | 删除 `tracking` 参数和 `"trusted-return"` 分支。 |
| 修改 | `ValueSourceIndex.UpdateReturns`、`UpdateReturn`、`CompleteReturns`，以及 `m_returns` 的键 | 键从 `(method, tracking)` 改为 `method`，删除 tracking 相关分支。 |
| 新增 | `ValueSourceIndex.ReadResolvedCall(BehaviorValueReference callResult)` | 给 `ReadRoots` 使用：输入一个 `CallResult` 值，返回 `(BehaviorCall, ResolvedCall?)`。 |

**S3 阶段：**

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 删除 | `RegistrationIndex.Build()` 的全量扫描 | 不再对全部文件执行 `RegistrationScanner`。 |
| 修改 | `RegistrationIndex.ReadLocations(string name, bool calls)` | 先从 `material.IdentifierIndex` 取候选文件：普通名字取该名字；`".ctor:X"` 取 `X`，外加 `"new()"` 和 `".ctorinit"`；隐式构造 `".ctor"` 取 `"new()"` 和 `".ctorinit"`。只对这些文件运行 `RegistrationScanner`，只保留名字相同的条目。结果按"文件序号、再按 `Span.Start`"排序后缓存。 |
| 修改 | `RegistrationIndex.m_files` | 只包含 `IsCandidateSource` 的程序集。 |
| 修改 | `RegistrationScanner` | 新增过滤名字的参数，只收集匹配的条目。 |
| 修改 | `IndexImplicitConstructors` | 只遍历上述伪标识符对应的文件。 |
| 修改 | 实现和重写候选（`ReadImplementations`、`VirtualTargets`） | 排除 `IsCandidate == false` 的类型。 |

**S4a 阶段（纯机械优化，结果必须零差异）：**

| 操作 | 位置 | 要求 |
|---|---|---|
| 修改 | `ResolveAsync` 中以 `string` 为键的 `Dictionary` 和 `HashSet`（`methods`、`bodies`、`queued`、`unread`、`behaviorNeeded`、`liveBehavior`、`behaviorEdges`、`behaviorCallers`、`demandParent`、`demandChildren`、`registrationTargets`） | 改成以 `MethodEntry.Ordinal` 为键：数组、`List<int>` 或位集合。 |
| 修改 | `ValueSourceIndex` 中以 `string` 为键的字典 | 统一改用 `BehaviorValueReference.MethodOrdinal`，删除 `Normalize` 的重复调用。 |
| 修改 | `TargetIdentity` 和 `ReadTargetDescription` | 不再拼接字符串，改为预先计算的结构体键（目标函数序号，加上接收对象和实参的值编号序列）。第 385–390 行的比较改为比较这个键。 |
| 修改 | 热路径上的 LINQ | 改成普通循环。以 `dotnet-counters` 或 `dotnet-trace` 采样分配排名前 20 的位置为准，逐项处理，并在交付说明中附改前改后的排名。 |

**S4b 阶段（先评审）：**

S4b 用"分批闭包"替换按需调度：

1. 读取全部未读的可达函数体（并行，结果排序）。
2. 求解值流直到不动点（单线程）。
3. 解析调用，得到新的可达函数和注册所有者。
4. 回到第 1 步，直到不再有新函数。

届时要删除 `NeedsBehavior`、`IsLive`、`ActivateBehavior`、`Settled` 回调、`deferred`/`pausedRegistrations` 逻辑，以及 `ValueSourceIndex` 中的 `CaptureInputs`、`CaptureScope`、`SourceKey`、`Subscribe`、`Observe`、`Notify`、`RequireBody`、`RequireCall`、`NeedsValues`、`NeedsCall`、`AddValueUser`、`InvalidateValueReaders`、`HasValueReader`、`TakeChangedReaders`、`ObserveCallDemand`、`SaveCallInputs`、`DeferCall`、`ReadPendingCalls`、`IsBehaviorNeeded`、`RequestMember`、`RequestIncoming`、`NeedsRegistration`、`CompleteRegistration`、`TakeRegistrationMembers`、`TakeRegistrationMethods`、`RequestRegistrationCall`。

**S4b 开工前必须先提交《S4b 设计与测量》，审阅通过后才能施工**，内容包括：

- 值流的节点、边和抽象对象的定义；对象按分配位置区分，以保住 `FactoryInstancesKeepSeparateCallbacks`、`FieldKeepsReceiverTypeBoundary` 等测试的精度。
- 在 khengine 上用原型测得的函数体读取数、值流节点数和耗时。
- 继续进行（go）的条件：总耗时 ≤ 7 秒，而且结果与 S4a 零差异，或者每条差异都有解释。

### 5.6 `EffectAnalyzer.cs`（M5，S2 整体重写）

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 删除 | `FunctionEffects` 整个类：`IsSettled`、`ReadDirect`、`ReadReturns`、`CanReturnBusinessObject`、`Bind`、`IsNewObjectReturn`、`IsNewObjectConstruction`、两个 `IsStandardDictionaryTryGetValue`、`IsStandardCollectionType`、`Seed`、`Add`、`Propagate`、`ReadResults`、`Settled` | 它们的特例由统一的摘要规则取代，**不得**以任何形式保留。 |
| 删除 | `ReadSubjects`、`WriteSubjectKind`、`ModificationEvidence` | 由 `ReadRoots`、`EffectRoot`、`SummaryCause` 取代。`HasReferenceMember`、`ContainsMember`、`MemberAccess` 可以保留供 `ReadRoots` 使用。 |
| 删除 | `AnalyzeAvailable(…, bool, …)` 与 `EffectAnalysisResult.TrackingMethods`、`TrackingFailures` | 按 R1 删除。 |
| 新增 | `public EffectAnalysisResult Analyze(MethodCatalogResult catalog, IReadOnlyList<MethodEntry> roots, CallTargetResolutionResult calls, CancellationToken cancellationToken = default)` | 按 4.7 求解，返回每个根函数的 `MethodEffect` 和 `Failures`。`calls.Failure`（解析被中断）存在时，所有尚未得出 Setter 的根函数都记为该失败。 |
| 新增 | `EffectRoot`、`EffectRootKind`、`ValueSummary`、`MethodSummary`、`SummaryCause` | 定义见 4.3。 |
| 新增 | `private MethodSummary Summarize(string methodId)` | 见 4.5。 |
| 新增 | `private IReadOnlyList<EffectRoot> ReadRoots(string methodId, int valueId, bool deep)` | 见 4.4。 |
| 新增 | `private IReadOnlyList<EffectRoot> ReadAliasRoots(string methodId, int site)` | 见 4.5 的 AliasRoots。 |
| 新增 | `private static List<string[]> ReadComponents(...)` | 迭代版 Tarjan，按 4.7 返回有序的分量列表。 |
| 新增 | `private EffectEvidence ReadEvidence(string methodId, int bit)` | 见 4.8。 |
| 新增 | `internal sealed class TopTracker`，成员包括 `bool IsTop(string)`、`void ReadDirect(MethodBehavior, IEnumerable<BehaviorWrite>?)`、`void Bind(ResolvedCall)`、`Action<IReadOnlyList<string>>? Settled` | 见 4.6。内部用 `m_top` 集合加 `m_callers` 反向表传播，只传播 TOP，不做豁免判断。 |
| 修改 | `EffectAnalysisResult` | 保留 `Methods`、`Elapsed`、`Failures`、`SummaryUpdates`；新增 `IgnoredLazyWrites`、`IgnoredDiagnosticCalls`（计数，供报告使用）和 `HiddenPlayerCode`（函数 Id → 行号）。 |
| 不动 | `MethodEffectKind`、`EffectEvidence`、`MethodEffect` | — |

`Analyze` 的调用方只有 `SetterChecker.AnalyzeAsync`。

### 5.7 `AnnotationEvaluator.cs`（M6，S2）

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 修改 | `Evaluate` | 删除 `tracking` 和 `trackingFailures` 两个变量（第 18–19 行）、第 36–37 行"尚未取得标签生效后的修改说明"的失败分支，以及第 57 行的 `TrackingEvidence`。决定公式（第 38–40 行）保持不变。新增：`HiddenPlayerCodeLines = effects.HiddenPlayerCode.GetValueOrDefault(method.Id)`。 |
| 修改 | `AnnotationMethod` | 删除 `TrackingEvidence`；新增 `IReadOnlyList<int> HiddenPlayerCodeLines`。 |
| 修改 | `ManualBaseline.ApplyNative` | 第 159 行改为只用 `method.Evidence`。 |
| 不动 | `FindAttribute`、`IsTrustedClassNoLogTrack`、`ManualBaseline` 的其余成员 | — |

### 5.8 `RuntimeOperations.cs`（S2）

| 操作 | 函数或类型 | 要求 |
|---|---|---|
| 修改 | `RuntimeOperation` | 新增 `Diagnostics` 和 `WriteCollection`。 |
| 修改 | `Find` | ① 最先判断 R3 诊断类型（在 `SourceSymbol` 和 `System.` 过滤之前），命中则返回 `Diagnostics`，`Source` 写"用户 2026-09-24 确认：调试日志与性能采样不改变战斗状态"。② 新增 `WriteCollection` 规则，适用于 `List<T>` 的 `Add`、`AddRange`、`Insert`、`InsertRange`、`Remove`、`RemoveAt`、`RemoveRange`、`Clear`、`set_Item`、`Reverse()`（无参）；`Dictionary<TKey, TValue>` 的 `Add`、`TryAdd`、`Remove(key)`、`Clear`、`set_Item`；`HashSet<T>` 的 `Add`、`Remove`、`Clear`；`Queue<T>` 的 `Enqueue`、`Dequeue`、`Clear`；`Stack<T>` 的 `Push`、`Pop`、`Clear`。签名中带委托、`IComparer`、`IEqualityComparer` 参数的重载，以及 `Sort`、`BinarySearch`、`RemoveAll`，**不适用**，照常读取 IL。 |
| 新增 | `internal static RuntimeEffect ReadEffect(RuntimeOperationRule rule, MethodEntry method)` | 按 4.9 返回效果（浅写槽位、`Into`、返回值、`Outs`），供 `EffectAnalyzer` 和 `TopTracker` 使用。 |
| 新增 | `internal sealed record RuntimeEffect(ulong Shallow, IReadOnlyDictionary<int, ulong> Into, ValueSummary Return, IReadOnlyDictionary<int, ValueSummary> Outs, string? Unknown)` | — |

### 5.9 `ReportWriter.cs`（M7）

| 操作 | 函数 | 阶段 | 要求 |
|---|---|---|---|
| 修改 | `Write` | S2 | 第 116 行改为只用 `method.Evidence`。report.md 新增两个小节：①"编辑器宏隐藏的真机代码"（R7），按类汇总函数和行号；②"规则忽略统计"，列出 R2 忽略的写入数和 R3 忽略的调用数。JSON 中对应新增 `HiddenPlayerCode` 和 `IgnoredLazyWrites`、`IgnoredDiagnosticCalls`。 |
| 修改 | `Write` | S3 | report.md 新增"已排除的编辑器与测试程序集"小节，JSON 新增 `ExcludedEditorAssemblies`。 |
| 不动 | `WriteGroups`、`ReadPreview`、`AppendLines` | — | — |

### 5.10 `Program.cs`（Cli）

- 不新增参数，现有参数全部保留。

---

## 6 分阶段施工计划

### S0 基线、文档同步与对比工具

1. 在当前 HEAD 编译 Release。用 `-j1` 跑一次、`-j8` 跑两次，输出到 `%TEMP%/SetterChecker-v2/baseline/{j1,j8a,j8b}/`。
2. 按第 8 章实现对比脚本 `Compare-Reports.ps1`（放置位置见第 10 章 Q2），并用它输出：
   - `j8a` 与 `j8b` 的差异：若不为零，说明线程调度本身就会让结果变化。
   - `j1` 与 `j8a` 的差异：应能复现 D1，约 25 个函数结论不同。
3. 按 2.8 节第 1–6 条同步文档。
4. 交付：基线目录、两份对比报告、文档改动的 diff。

### S1 确定性修复（最小改动）

1. `EffectAnalyzer.Bind`：`m_callers` 的值从 `Dictionary<string, int>` 改为 `Dictionary<string, (int Position, bool NewObjectConstruction)>`。登记调用边时，同时记下 `IsNewObjectConstruction(target)` 的结果。**边照常登记，不能删**，因为 `ReadResults` 里"未确定原因"的传播还需要这条边。
2. `EffectAnalyzer.Propagate`：Setter 事实沿边传播时，跳过 `NewObjectConstruction == true` 的边；如果 `IsNewObjectReturn(fact.Method, fact.Tracking)` 为真，也不传播。`ReadResults` 中"未确定原因"的传播仍然走所有边，保持不变。
3. `EffectAnalyzer.Add`：当 `newObjectReturn == false` 而且原来已经因"返回新对象"登记过时，除了从 `m_newObjectReturns` 删除，还要把 `(method, tracking)` 重新放入 `m_pending`，保证调用者能收到"真 Setter"的通知。**这一条关系到第二类错误，必须有测试覆盖。**
4. S1 只修顺序问题，旧的豁免语义保持不变（例如结构体构造函数即使写了静态数据也被豁免）。彻底的修复留给 S2。
5. 重跑 `-j1` 和 `-j8`。仍有差异时，按以下顺序把各处并行度临时固定为 1 做对分定位，找到后修复：
   - `MaterialLoader` 的编译
   - `MethodCatalog.BuildAsync`
   - `BehaviorReader.ReadAvailableAsync`
   - `RegistrationIndex` 的并行扫描和 `PrepareSymbols`
   
   用于定位的临时开关**不得提交**。重点检查 5.3 节列出的惰性缓存是否线程安全。
6. 测试：新增 T21（先因返回新对象登记、后出现真写入时，调用者必须变为 Setter）和 T17（不同 `-j` 结果一致）。T1、T2 留到 S2，因为它们依赖新的摘要规则。
7. 验收：
   - 3.1 确定性成立。
   - 与基线相比的变化只能出现在构造函数和返回新对象相关的函数上，而且每条都有分类。
8. 交付：阶段交付说明、对比报告、定位过程的简要记录。

### S2 函数摘要重写，加上 R1、R2、R3、R5、R7

1. 按 5.4 节实现 R2 标记和 R7 行号。
2. 按 5.8 节扩展 `RuntimeOperations`。
3. 按 5.6 节重写 `EffectAnalyzer`，在 `CallTargetResolver` 中接入 `TopTracker`（5.5 节 S2 部分）。
4. 按 5.7、5.9 节修改标签和报告。
5. 测试：按第 7 章改写现有测试，新增 T1–T20。
6. 验收：
   - 3.1、3.2、3.3 成立，性能满足 3.4 的 S2 预算。
   - 函数体读取次数与基线相比增加不超过 40%，超过时要附原因分析。
   - 对比报告中"旧 Setter → 新 Getter"按第 8 章分类，每类随机抽 10 个写入交付说明，附源码行。
7. 交付：阶段交付说明、对比报告及 CSV、抽样表。

### S3 材料范围与注册索引（性能）

1. 按 5.2 节实现 R4 和标识符索引，按 5.3 节实现候选过滤，按 5.5 节 S3 部分改造注册索引。
2. 新增 T18（编辑器程序集中的实现不参与分派）和 `ReadCscNodes` 对照测试。
3. 验收：
   - 3.1、3.2 成立，3.4 的 S3 目标达成。
   - 与 S2 结果相比的差异只允许来自"编辑器或测试程序集中的实现或注册被排除"，每条都要标出被排除的目标。
   - 如果因此出现新的"接口或重写没有合法实现"失败，单独列出，交审阅方判断。
4. 交付：阶段交付说明、分项耗时对比（材料、函数总表、注册）、对比报告。

### S4a 编号化与分配优化（零差异）

1. 按 5.3、5.5 节 S4a 部分实施。
2. 验收：
   - 与 S3 的 `report.json` 相比，去掉计时字段后逐字节一致。
   - 满足 3.4 的 S4a 目标；附分配排名前 20 位置的前后对比。
3. 交付：阶段交付说明、性能对比。

### S4b 分批闭包调度（可选，先评审）

- 只有 S4a 之后总耗时仍大于 7 秒，而且用户要求继续时才启动。
- 先交《S4b 设计与测量》（见 5.5 节），审阅通过后才能施工。

### S5 收尾

1. 删除所有死代码和不再使用的 using，统计代码行数，满足 3.5。
2. 更新 README"当前限制"一节，写入以下内容：
   - R4 的范围。
   - R7 的提示。
   - 诊断类型表的位置。
   - 委托参数按上下文无关的方式绑定：例如 `List<T>.Find(predicate)`，只要全工程任何一处传入的 lambda 会写数据，所有调用 `Find` 的函数都会判为 Setter。这属于第一类误差，本轮不处理。
3. 按 AGENTS 完成最终审查、提交并推送。
4. 交付：最终交付说明，包括全部阶段的耗时和精度汇总表。

---

## 7 测试清单

所有测试都写在 `KhengineTests.cs`，夹具写法参照现有的 `ConfigItemProducesCompleteReport`、`ProxyChecksAllImplementationsAndHonorsLabel`。标 ◆ 的测试要同时用源码和 DLL 两种来源运行（参数 `external`）。

### 7.1 预期会变的现有测试

以下测试如果失败，先判断是否属于规则变化：属于的话，修改预期，并在测试上方加一行 `// 规则 Rx：…`；不属于的话，就是实现缺陷，修代码而不是改测试。

- `CacheExemptionSeparatesBehaviorAndLogging`（R1：不再有追踪结果）
- `ReflectionOutputSurvivesExemption`（R1）
- `ProxyChecksAllImplementationsAndHonorsLabel`（R1）
- `BusinessObjectReturnSurvivesFactoryExemption`（R5）
- 涉及 `TrackingEvidence` 字段的断言（R1）

### 7.2 新增测试

| 编号 | 名称 | 内容与预期 |
|---|---|---|
| T1 ◆ | `ClassConstructorOfNewObjectIsGetter` | 类的构造函数只写 `this` 的字段；调用 `new C(x)` 的函数判为 Getter |
| T2 ◆ | `ConstructorWritingStaticIsSetter` | 构造函数里 `s_counter++`；调用 `new C()` 的函数判为 Setter（R6） |
| T3 ◆ | `StructConstructorReturnIsGetter` | `return new V(a, b);` 判为 Getter |
| T4 ◆ | `OutParameterIntoLocalIsGetter` | 仿照 `toCoordKey`：把局部变量作为 out 实参传入，判为 Getter |
| T5 ◆ | `OutParameterIntoFieldIsSetter` | `Split(x, out m_a, out m_b)` 判为 Setter |
| T6 ◆ | `ForeachOverFieldListIsGetter` | `foreach (var e in m_list) if (e.id == id) return true;` 判为 Getter |
| T7 | `LocalListMutationIsGetter` | 新建列表，`Add`、`RemoveAt` 后返回 `Count`，判为 Getter |
| T8 | `FieldListMutationIsSetter` | `m_list.RemoveAt(0)` 判为 Setter |
| T9 ◆ | `ReturnedFieldMutationIsSetter` | `GetList().Clear()`，其中 `GetList` 返回 `m_list`，判为 Setter |
| T10 ◆ | `AliasThroughNewObjectIsSetter` | `new Wrapper(m_list).Items.Add(1)` 判为 Setter（I4 护栏） |
| T11 ◆ | `AliasThroughParameterLinkIsSetter` | `Link(fresh, m_b); fresh.next.x = 1;`，其中 `Link` 执行 `a.next = b`，判为 Setter（I4 护栏） |
| T12 | `LazySingletonIsGetter` | R2 的三种写法各写一个 `get_Instance`，调用方判为 Getter |
| T13 | `LazyInitWithExtraStatementIsSetter` | `if (m == null) { m = new D(); m.Add(1); }` 判为 Setter |
| T14 | `LazyInitOnOtherObjectIsSetter` | `if (o.F == null) o.F = new X();` 判为 Setter |
| T15 | `DiagnosticsCallIsGetter` | 在夹具里声明与 R3 同名的 `KH.Debuger` 和 `UnityEngine.Profiling.Profiler` 类型；程序集名不同时照常分析，名称一致时判为 Getter。两种情况都要测 |
| T16 | `NltCalleeKeepsCallerSetter` | 调用带 NLT 的 Setter 的上层函数判为 ShouldTrack（R1） |
| T17 | `JobsProduceIdenticalReport` | 同一个夹具分别用 `-j1` 和 `-j4` 运行，比较完整 JSON（去掉计时字段后一致） |
| T18 | `EditorOnlyImplementationIsNotCandidate` | 接口的唯一 Setter 实现放在 `includePlatforms: ["Editor"]` 的程序集里，runtime 侧的调用不连接到它（S3） |
| T19 | `HiddenPlayerCodeIsReported` | 函数里有 `#if !UNITY_EDITOR` 写入，报告列出行号，判断结果不变 |
| T20 ◆ | `NewObjectReturnIsGetter` | 工厂函数 `return new Item(id);`、以及"新建局部对象、给字段赋值后返回"的 `Clone`，都判为 Getter（R5 规则 1、2） |
| T23 ◆ | `CreateAndRegisterIsSetter` | 新建对象后执行 `m_items.Add(obj)` 再返回，判为 Setter（R5 规则 3，仿照 `DoCreatePlayerActor`） |
| T24 ◆ | `CreateWritingParameterIsSetter` | 新建对象并写入参对象的字段，例如 `actor.m_btId = id`，判为 Setter（R5 规则 3，仿照 `CreatAI`） |
| T25 | `PoolTakeIsNotFresh` | 从对象池的列表中取出并返回（`var o = m_pool[last]; m_pool.RemoveAt(last); return o;`），判为 Setter（R5 规则 1） |
| T26 | `IdAllocationIsSetter` | `return s_next++;` 判为 Setter；加了裸 `[NoLogTrack]` 时，报告出现"缺少 Reason"告警（仿照 `GetSid`） |
| T21 | `LateRealWriteAfterNewObjectReturnNotifiesCaller` | S1：被调函数先因返回新对象登记，之后又出现真写入，调用方必须判为 Setter |
| T22 | `RuntimeRulesAllHaveEffects` | 遍历 `Find()` 的全部规则类别，`ReadEffect` 不得返回"尚未建模"（S2） |

---

## 8 对比脚本规格（`Compare-Reports.ps1`，PowerShell 7）

参数：`-Old <report.json> -New <report.json> -Out <目录>`，另有开关 `-Determinism`。

输出：

1. `summary.md`，包括：
   - 两边的总数、Getter/Setter/未证明数、NoLogTrack/ShouldTrack 数。
   - 转移矩阵：旧 Actual 到新 Actual、旧 Decision 到新 Decision。
   - 各自动分类的计数。
2. `changes.csv`，列为：`Id, Class, Name, File, Line, OldActual, NewActual, OldDecision, NewDecision, OldFailure, NewFailure, OldEvidenceEnd, NewEvidenceEnd, Category`。
3. 自动分类规则，用于"旧 Setter → 新 Getter"，按旧证据链判断，从上到下取第一个命中：

   | 类别 | 旧证据链特征 |
   |---|---|
   | `Constructor` | 某一节以 `::5:.ctor(` 结尾 |
   | `OutRef` | Detail 为 `Indirect`，且链尾函数签名含 `&` |
   | `Enumerator` | 链尾含 `Enumerator::8:MoveNext` |
   | `LocalCollection` | 链尾属于 `MSCORLIB` 或 `NETSTANDARD` 的集合类型 |
   | `LazySingleton` | 链尾为 `get_Instance`、`getInstance` 或 `GetInstance` |
   | `LazyField` | 链尾函数名以 `get_`、`init`、`Init`、`Rebuild`、`INIT_` 开头 |
   | `Diagnostics` | 链中含 `KH.Debuger`、`KHProfiler` 或 `UnityEngine.Profiling` |
   | `NewObjectReturn` | 旧 Detail 为空，而且新版本函数返回新对象（需由新报告提供线索时，写入 Category 备注） |
   | `EditorExcluded` | S3 使用：旧证据链经过被排除的程序集 |
   | `Unclassified` | 以上都不是（必须人工说明） |

4. 使用 `-Determinism` 时：两边删除第 3.1 节列出的计时字段后逐字节比较，输出第一处差异的 JSON 路径。

---

## 9 阶段交付说明模板

```markdown
# 阶段 Sx 交付说明
- 分支 / 提交：
- 对应章节：本文件 5.x、6.Sx
- 实际改动：按文件列出新增、删除、修改的函数（与第 5 章逐项对照，偏离之处单独说明原因）
- 新增的私有辅助函数：
- 测试：总数、通过数、新增测试列表、修改预期的测试及其规则依据
- 确定性：-j1、-j4、-j8 对比结果
- khengine 结果：各项总数，与上一阶段的转移矩阵，各分类计数，Unclassified 明细
- 性能：-j8 三次运行的中位数和分项耗时；分配字节；GC 暂停；函数体读取次数
- Core 行数
- 已知问题与待确认项
```

---

## 10 待用户确认的问题

- **Q1（已确认，2026-09-24）**：确认 R5，并按 2.8 节第 6 条改写 PLAN 第二章第 3 条和 AGENTS 对应句子，S0 阶段执行。
- **Q2**：`Compare-Reports.ps1` 放在哪里？一种是放在仓库根目录并提交，与 `Run-Khengine.ps1` 并列；另一种是放在仓库外，不提交。默认放仓库外：`%USERPROFILE%/Desktop/SetterChecker-v2-tools/`。
- **Q3**：S4b 是否实施？在 S4a 验收之后再决定。

---

## 11 修订目标（2026-09-28，按实测重定）

### 11.1 为什么要改目标

第 3 章的指标是在实施前按 r5 估算的，有三处估错了：

- 只按 TOP 剪枝不可行（实测 440 秒以上）。现在的剪枝条件是"TOP，或者自身结论已定、且没有待定调用者需要它的摘要"，读取的函数体约 3.2 万个，比基线多 46%。
- R4 排除编辑器程序集后，仍有 60 个要参与编译；少解析文件也不省时间，因为源码解析本来就是并行的（见 R4 末尾的实测）。
- 精度指标"ShouldTrack 至少减少 1,000 个"用的是正则统计的上限，实际减少 565 个。其余函数除了构造调用等之外，还有别的真写入。

### 11.2 现状（`-j8`，墙钟）

约 26–28 秒。其中：

- 分析阶段 16–17 秒
- 材料读取 4.8 秒，其中编译参数读取 2.0 秒、源码解析 2.2 秒
- 函数总表 3.0 秒
- 标签检查和报告写出约 1 秒
- 进程启动约 2 秒

一次运行分配约 11GB，GC 暂停约 3.1 秒。

### 11.3 新目标

| 项目 | 目标 |
|---|---|
| 第二类错误 | 零容忍（不变） |
| 测试 | 全部通过；因规则改变而改预期的，逐条注明规则编号 |
| 未证明 | 不超过 8 个，每个都有原因 |
| 已知第一类残留 | 写入 README"当前限制"。例如参数分派与上下文无关：`SR.Format` 这类库函数的 object 参数没有来源时，会连接全部业务 `ToString` |
| 速度 A（S4a + 流式读取构建图） | `-j8` 中位数不超过 20 秒；分配不超过 7GB；GC 暂停不超过 2 秒 |
| 速度 B（S4b 分批闭包，先评审） | 不超过 12 秒 |
| Core 行数 | 不超过 13,000 行（硬上限），S5 时尽量压到 11,500 行 |

S4a 的每一步都要求：`-j8` 报告中每个函数的 Actual、Decision、Failure 和证据链，与改前完全一致。

### 11.4 待办（按顺序）

1. **S4a 收尾与合并**：解析器热路径改为整数和结构体键，去掉 LINQ。已把分析阶段从约 16 秒降到约 9.5 秒。
2. **S4b 分批闭包**：先交《S4b 设计与测量》，审阅通过后施工。目标是把主循环中可以并行的调用解析放到多个核心上。
3. **常驻 / 增量模式（用户 2026-09-28 列入计划）**：
   - 进程保留已解析的语法树、编译和函数总表，重复运行时只重新解析改过的文件，并按依赖失效相关的程序集编译和函数事实。
   - 目标：重复运行时，材料加函数总表从约 7 秒降到 1 秒以内。
   - 需要先确定使用方式（常驻进程接收命令，或监视文件变化），交用户决定后施工。
4. **外部库函数摘要缓存（用户 2026-09-28 列入计划）**：
   - 约 1.6 万个外部 DLL 函数（占读取总数约 47%）的摘要按 DLL 身份（SHA-512）跨运行缓存。
   - 只缓存调用范围不出库的函数；会回调业务代码的函数（如 `List.Sort` 的比较器、`SR.Format` 调用的 `ToString`）不缓存。
   - 与第 3 项配合，目标是重复运行整体不超过 10 秒。
5. **精度**：修复 26 个解析器精度测试（委托、迭代器、工厂按分配位置收窄；泛型经委托后的反射目标），处理 `SR.Format` 类参数分派的过度近似。
6. **收尾**：Core 行数压回 13,000 行以内；README"当前限制"补上已知的第一类残留；报告补"规则忽略统计"小节。
