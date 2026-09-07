# SetterCheckerNew

SetterCheckerNew 用于完整分析 `D:/KiHan/Packages/khengine` 的函数行为，区分 Getter 与 Setter，再判断 ShouldTrack、现有 NoLogTrack 标签差异和可信豁免。

最终交付包含真实 Getter/Setter 结论、日志追踪决定、文本与 JSON 报告，以及标签修改预览。工具只读取 KiHan，不自动修改游戏项目。

当前仍在开发调用目标分析，尚不能生成有效的最终标签结论。Core 手写代码上限为 10,000 个物理行；只有全部模块通过测试、独立审计和真实 khengine 验收，才算完成。

全部需求、模块组织、边界规则与实时进度见[设计与进度](./Docs/PROJECT-PLAN.md)。
