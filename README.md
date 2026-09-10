# SetterCheckerNew

SetterCheckerNew 用于完整分析 `D:/KiHan/Packages/khengine` 的函数行为，区分 Getter 与 Setter，再判断 ShouldTrack、现有 NoLogTrack 标签差异和可信豁免。

最终交付包含真实 Getter/Setter 结论、日志追踪决定、文本与 JSON 报告，以及标签修改预览。工具只读取 KiHan，不自动修改游戏项目。

七个模块已接通，可生成成功或失败报告，但真实 khengine 验收尚未通过。未证明的行为不计为 Getter 或 Setter；即使真实行为已知，追踪决定未证明时也不生成标签修改。可信 NoLogTrack 会切断内部写入对上层的追踪影响，但不改变真实行为记录。Core 尽量不超过 13,000 行，额外额度须按设计文档独立审计批准；只有全部模块通过测试、独立审计和真实 khengine 验收，才算完成。

编译和运行：

```powershell
dotnet build SetterChecker.slnx -c Release
dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef -j4 --output reports
```

输出 `report.md`、`report.json` 和 `preview.patch`；验收未通过时退出码为 1。报告不提交 Git，补丁仅供人工预览。

实验分支支持同会话编译上下文和本工具生成的程序集缓存。命令行默认保存到本机用户数据目录 `SetterChecker/Compilation`；`--cache <目录>` 可指定位置，`--no-cache` 关闭磁盘复用。缓存不能放在游戏工程内。每次核对实际源码、编译输入、生成结果、引用内容及工具版本，不依赖文件时间；不使用旧 Unity DLL 冒充当前源码。报告分别列出仅保留编译上下文、实际编译、会话复用、磁盘复用。

同一个 `SetterChecker` 实例可重复调用，完整分析请求依次执行，文本在排队前固定；独立材料加载器也有自己的请求队列。`MaterialRequest.SourceTexts` 接收已列入本轮编译输入的文件当前文本，包括尚未保存的新文件，不要求 Unity 先重新编译。当前编译清单更新后，新增、删除和重命名会更新目录，不继承旧文件或旧函数。请求取消后整份材料失效，重新加载取得新材料，不继续使用已取消请求的延迟编译任务。编辑器自动同步文件清单、程序集定义与分析结果增量复用仍在任务清单中，不能把这一步当作完整编辑器接入。

材料阶段只生成可报告程序集，普通外部调用命中所属程序集后才生成其完整内容。需要找齐接口、虚函数等动态候选时，目前仍会生成剩余源码程序集，确保不遗漏编译器自动生成的实现；尚未达到全流程仅编译少量程序集的目标。后续只读取实际需要的函数体。编译准备、生成或复用完整程序集的累计工作时间与分析各阶段耗时分别报告，累计工作时间不能再次加进总耗时。

全部需求、模块组织、边界规则与实时进度见[设计与进度](./Docs/PROJECT-PLAN.md)。
