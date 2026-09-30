# SetterCheckerNew

分析 khengine 函数是否修改数据，对照 NoLogTrack 标签，输出差异、逐函数结果、补标提醒与修改预览。除显式使用 `--apply-nlt` 外，游戏工程只读。

源码直接用 Roslyn 读取，不生成游戏程序集；外部托管 DLL 按需读取函数。每个函数保留一份事实和固定调用关系，存在合法 Setter 目标即可传播，不按调用路径复制分析环境。可信 NoLogTrack 隔断日志影响，但仍独立检查真实行为。

## 运行

当前唯一维护版本为本目录“重构版本SetterChecker”；`New/SetterChecker` 已废弃。

日常运行固定使用仓库根目录入口（PowerShell 7）：

```powershell
pwsh -File .\Run-Khengine.ps1
```

入口先编译工具，再扫描 khengine.runtime 与 khengine.define；默认 8 个工作线程，输出到 `%TEMP%/SetterChecker-khengine`。可用 `-Jobs 4 -Output C:/Temp/khengine-report -ProjectRoot D:/KiHan` 调整线程、输出位置和工程位置，`-Previous` 与 `-ApplyNlt` 对应下文的 `--previous` 与 `--apply-nlt`。首次使用先执行 `dotnet restore SetterChecker.slnx`。

锁定规则：必须启用 `--reflection-baseline`，反射按当前源码人工标签处理；原生边界沿用已有人工标签规则。日常运行不加载会随源码、工具变化失效的历史 `--baseline` 文件。不得省略反射参数后与本配置比较耗时。

验收看 `LogDecisionsComplete` 和退出码：退出码 0 表示最终标签全部就绪。已有人工标签、可信豁免或无实现废弃声明覆盖的边界计为已处理，不反复提示为待办；仅影响最终标签的未知或标签冲突阻塞验收。JSON 保留原始行为证据，不把人工确认伪装成已证明的 Getter。此规则按状态生效，不按固定“12 个”或函数名写死。

以下为底层命令示例；日常使用上述固定入口，避免遗漏参数。

```powershell
dotnet build SetterChecker.slnx -c Release --artifacts-path "$env:TEMP/SetterChecker-build"
dotnet "$env:TEMP/SetterChecker-build/bin/SetterChecker.Cli/release/SetterChecker.Cli.dll" analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef -j8 --reflection-baseline --output "$env:TEMP/SetterChecker-report"
```

输出以下文件；报告、缓存及构建目录不提交 Git：

- `report.md`、`report.json`：完整报告与证据。
- `functions.csv`、`functions.json`：统一格式的逐函数结果，每个可报告函数一行，按文件和行号排序。列为文件、行、类、函数、源码标签、真实行为、最终决定、需要处理、依据、函数标识；CSV 带 BOM，便于 Excel 打开。
- `notify.md`、`notify.json`：判定为 NoLogTrack、源码却没有标签的函数。加 `--previous <上次的 functions.json>` 时只列出相比上次新增的，否则列出全部。
- `preview.patch`：补标预览，内容与 `--apply-nlt` 写入的相同。

退出码：0 表示最终标签全部就绪；1 表示仍有未确定项，报告照常完整写出；2 表示分析失败。

`-j4`、`-j 4`、`--jobs 4` 设置整个工具的最大并行数，接受任意正整数，默认 4。

## 补标与提醒

`--apply-nlt` 在报告写出后，给判定为 NoLogTrack、源码却没有标签的函数补标：

- 在声明正上方另起一行 `[NoLogTrack]`，缩进和换行沿用该声明，放在 XML 注释之后、已有特性之前，保留文件 BOM。
- 该位置的短名称不指向 `KH.NoLogTrackAttribute` 时（文件没有 `using KH;` 且不在 KH 命名空间内），写 `[KH.NoLogTrack]`。
- 写入前核对全部目标文件仍是分析时的内容且为有效 UTF-8，任一不符则不写任何文件。报告中的行号是补标前的位置。
- 声明上不能直接加特性的函数（如表达式属性）在 report.md 单列，需手工处理。

```powershell
pwsh -File .\Run-Khengine.ps1 -ApplyNlt
```

## 流水线

`Run-Pipeline.ps1` 供蓝盾脚本步骤调用：可选 SVN 更新（`-SvnUpdate`）与 Unity 批处理编译（`-UnityPath`），然后扫描，并与 `-StateDir` 中上一次的 `functions.json` 对比生成 `notify.md` 与 `notify.json`。首次运行只保存对比基准、不提醒；之后每次运行都更新基准，同一函数只提醒一次。加 `-FailOnNotify` 时有新增即以退出码 3 结束，便于流水线标红；具体通知渠道尚未接入，读取 `notify.json` 即可。

```powershell
pwsh -NoProfile -File .\Run-Pipeline.ps1 -ProjectRoot D:/KiHan -StateDir D:/SetterChecker-state -Output D:/SetterChecker-report -SvnUpdate -FailOnNotify
```

构建机需要 .NET 10 SDK、Windows PowerShell 5.1 或 PowerShell 7、SVN 命令行，以及一份由同版本 Unity 编译过的 KiHan 工作副本（读取 `Library/Bee` 中的编译参数）。普通源码增删不需要重新编译；asmdef 或引用变化后加 `-UnityPath` 让 Unity 先编译一遍。该工作副本不能同时被另一个 Unity 编辑器打开。

## 人工基线

对当前无法证明的函数，可显式保存源码标签：类级 NLT 为 NLTClass、方法 NLT 为 NoLogTrack、没有 NLT 为 ShouldTrack。配置不保存推算出的 Getter/Setter。

```powershell
dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef -j8 --no-report --save-baseline khengine-manual-baseline.json
dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef -j8 --no-report --baseline khengine-manual-baseline.json
```

- 只有显式指定 `--baseline` 才采用基线；新增函数不自动加入。保存基线是一次明确的人工标签确认，不代表分析验收通过。
  - 命中的未知项标记 `UsesManualBaseline`，保留原有 `Actual` 与失败原因（包括真实行为已知、日志决定未确定的情况），不生成补标建议、不掩盖标签冲突。
- `LogDecisionsComplete` 表示日志决定已就绪；`Complete` 仍表示真实分析完成。显式采用基线且日志决定就绪时 CLI 返回 0，但仍打印未确定真实行为数量。
- 此版不保存编译图、不增加调用分析缓存，也不保证新增代码仅分析一次局部。当前分析流程保持不变。
- 源码、编译输入、候选 DLL 内容或工具版本变化时，整份基线失效，必须复查后重新保存。当前未完整掌握动态依赖，不能仅凭函数自身未改就继续信任旧结果。

## 测试与验收

```powershell
dotnet "$env:TEMP/SetterChecker-build/bin/SetterChecker.Core.Tests/release/SetterChecker.Core.Tests.dll" --timeout 30s --progress off
```

只保留一个关键案例文件和一个小夹具：

- ConfigItem 加载、释放、字段读取及完整报告，核对单线程与四线程结果。
- KHScriptData 的反射字段复制，核对源码与真实 DLL。
- ConfigProxy 的接口实现和裸 NoLogTrack 豁免。
- KHScriptManager 的三个外部注册槽、字典工厂及真实 DLL 调用。
- 反射包装调用、缓存豁免、注册未知时不猜结论。

样本保留相关语句和必要依赖，文件内注明游戏来源；隔离出来的样本不能代替完整游戏验收。旧的调用路径、指令布局和大量语言边界组合不再作为默认验收矩阵。

主要验收固定为完整 khengine 一次运行，目标约 15 秒，包含准备源码、分析及写报告；工具构建、关键测试和游戏运行分别计时。关键测试通过不表示全部游戏差异已解释或性能已达标。

## 当前限制

- `.NET` 基础操作统一通过 `RuntimeOperations.cs` 接入。枚举描述操作类别，不代表每类已覆盖全部函数；当前登记数组维度读取、对象身份哈希和对象浅复制的完整签名及依据，报告列出实际使用的说明。浅复制保留共享引用字段；数组浅复制及类型未确定的复制来源仍明确报未支持。其他函数继续读取源码或 IL，无实现且无行为说明时保留未确定，不能保证自动判定任意原生函数。

- 尚未通过当前 khengine 的完整扫描与 15 秒验收。
- 材料加载仍读取当前 Unity 构建图的全部源码上下文。外围声明查找范围偏宽，尚不能承诺冷启动时间。
- Unity 清单提供现有程序集的编译选项及引用；Assets 和本轮包内的源码按当前文件与 asmdef/asmref 归属重新收集，支持普通源码增删及改名，不要求重新生成 DLL。尚无构建节点的程序集不自动猜测编译配置；生成源码和外部引用缺失仍明确报错。
- 会话内可复用源码上下文并接收已有文件的未保存文本；没有持久化分析结果，当前不依赖磁盘缓存提速。
- 对照实验可传 `--cross-method-origins false`：关闭跨函数来源需求及读取，保留函数内来源和 Setter 传播。默认 `true`；因关闭而缺失的信息明确记为未确定，报告记录开关值。此开关不是关闭全部对象来源计算。
- 泛型分派、迭代器及部分复杂对象传递尚有待真实项目验证的缺陷。缩减测试不代表这些边界已经修复。
- 不能证明的动态调用和原生影响明确单列，不猜成 Getter 或 Setter，不生成相应标签补丁。

原始目标和模块职责见 [锁定计划](Docs/PROJECT-PLAN.md)。后续明确确认的函数级 Setter 传播、关键测试范围和约 15 秒目标以 [当前规则](AGENTS.md) 为准；锁定计划本身不改。
