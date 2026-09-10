# SetterCheckerNew

SetterCheckerNew 用于完整分析 `D:/KiHan/Packages/khengine` 的函数行为，区分 Getter 与 Setter，再判断 ShouldTrack、现有 NoLogTrack 标签差异和可信豁免。

最终交付包含真实 Getter/Setter 结论、日志追踪决定、文本与 JSON 报告，以及标签修改预览。工具只读取 KiHan，不自动修改游戏项目。

七个模块已接通，可生成成功或失败报告，但真实 khengine 验收尚未通过。未证明的行为不计为 Getter 或 Setter；即使真实行为已知，追踪决定未证明时也不生成标签修改。可信 NoLogTrack 会切断内部写入对上层的追踪影响，但不改变真实行为记录。Core 手写代码上限为 13,000 个物理行；只有全部模块通过测试、独立审计和真实 khengine 验收，才算完成。

编译和运行：

```powershell
dotnet build SetterChecker.slnx -c Release
dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef -j4 --output reports
```

输出 `report.md`、`report.json` 和 `preview.patch`；验收未通过时退出码为 1。报告不提交 Git，补丁仅供人工预览。

全部需求、模块组织、边界规则与实时进度见[设计与进度](./Docs/PROJECT-PLAN.md)。
