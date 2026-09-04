# SetterCheckerNew

SetterCheckerNew 用于完整分析 `D:/KiHan/Packages/khengine` 的函数行为，区分 Getter 与 Setter，再判断 ShouldTrack、现有 NoLogTrack 标签差异和可信豁免。

工具按七个模块顺序完成，从找齐源码和动态链接库开始，最终生成文本报告、JSON 报告和标签修改预览。中间模块的测试命令不是残缺版交付；完整工具一定输出 Getter/ShouldTrack 结论。

全部需求、模块组织、边界规则与实时进度见[设计与进度](./Docs/PROJECT-PLAN.md)。
