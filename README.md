# SetterCheckerNew

分析 khengine 函数是否修改数据，对照现有 NoLogTrack 标签，输出差异与修改预览。只读取游戏，不自动改源码。

七个模块已接通；当前代码基线 `a7a0a49` 的 1332 项测试通过，但完整 khengine 扫描和 5–10 秒性能目标尚未达到。不能判断的函数单列原因，不猜成 Getter 或 Setter，不生成相应补丁。

## 运行

```powershell
dotnet build SetterChecker.slnx -c Release
dotnet Src/SetterChecker.Cli/bin/Release/net10.0/SetterChecker.Cli.dll analyze --project D:/KiHan/Packages/khengine/Runtime/khengine.runtime.asmdef -j4 --output reports
```

输出 `report.md`、`report.json` 和 `preview.patch`。存在未完成项时退出码为 1；报告不提交 Git。

- `-j4`、`-j 4`、`--jobs 4`：指定全程序最大并行数，接受任意正整数，默认 4。
- `--cache <目录>`：指定本工具编译缓存；默认位于本机用户数据目录 `SetterChecker/Compilation`。以内容核验有效性，不使用过期 Unity DLL。
- `--no-cache`：关闭磁盘复用。缓存不能放进游戏工程。

## 当前限制与下一步

同一工具实例支持复用编译上下文；`MaterialRequest.SourceTexts` 接收当前编译清单内文件的未保存文本。编辑器自动更新文件清单尚未接入，没有持久化最终行为结论。

普通外部调用按需生成程序集、读取函数；完整动态候选查询仍可能生成全部源码程序集，调用环境也存在重复展开。并非已经实现全流程高性能按需分析。

后续每个模块先提出具体改法、与用户讨论明确后实施；优先交付真实 khengine 差异和未覆盖情况，再逐类完善。

完整需求、七个模块的职责、待确认方案及待办见 [项目计划](./Docs/PROJECT-PLAN.md)。
