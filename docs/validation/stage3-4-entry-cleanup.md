# 整体回归中的启动入口分离修复

2026-09-07完整验收批次`7b8ffb23e99a448a9c1cd655650dbea2`在Service/IIS专项全部通过后，于阶段1的入口停止测试失败。目标在Host.Dispose后没有产生业务文件；监督器最终清理了该批次自建Console。该轮完整验收为失败，不作为完成证据。

针对冻结实现`85bec38`的独立实验两次复现：第13次和第48次运行中Engine正常退出0，目标仍存活且额外等待15秒仍无业务进展。另一次100轮通过；隔离的实施前Engine也有100轮通过。这些结果证明失败间歇发生，不能仅凭旧版一次通过断言引入原因；未匹配符号的原生栈不作为CLR内部根因证据。

修复仅在launch仍持有最初CreateProcess入口停止时，从CLR原进程句柄取得主映像完整路径。分离清理先按既有回调配对继续执行，直到匹配主模块LoadModule的停止成功Continue，再进入Stop/Detach。等待与分离共用原4秒期限，不额外睡眠或增加无配对Continue。附加Service/IIS不进入这一启动等待。回调失败保留当前停止及启动标记，统一在重试成功后清除；ExitProcess清理标记并结束等待。独立Standards发现的这两项错误路径遗漏已采纳修复并复核，Spec复核无剩余需求阻塞。

独立真实压力命令（普通用户、每架构200次）为：

```powershell
dotnet artifacts/stage3-4-validation/entry-audit/bin/Debug/net10.0-windows/EntryAudit.dll (Get-Location).Path x86
dotnet artifacts/stage3-4-validation/entry-audit/bin/Debug/net10.0-windows/EntryAudit.dll (Get-Location).Path x64
```

两轮均200/200通过，日志分别为`entry-fixed-x86.log`、`entry-fixed-x64.log`；混合`entry-audit/cycles.jsonl`最后400条对应上述顺序，两组cycle=0～199，Engine均正常退出0，业务文件均产生。实验启动的是本轮源码目录的Engine和对应Console；Host直接引用未改变的Debug发布程序集。失败清理仅涉及实验自建目标。

常规入口测试同步增强为x86/x64各20次，保留原2秒业务完成断言，额外要求目标自然退出，失败诊断包含进程退出码。

覆盖采集中还观察到另一种失败：Engine退出0、目标已退出，Windows Application事件1026记录Console.Main的FileLoadException。它不同于目标仍存活的挂起；不据此断言原生根因。采集范围改用[coverage.runsettings](../../eng/coverage.runsettings)，排除Fx40夹具探针，保留全部生产程序集与Engine子进程采集。随后连续三轮完整Debug单元测试均103/103通过，日志为`unit-scoped-1/2/3.log`；配置文件纳入两层完整验收源码哈希。

配置依据为Microsoft的[覆盖率ModulePaths及子进程采集文档](https://learn.microsoft.com/en-us/visualstudio/test/customizing-code-coverage-analysis?view=visualstudio)。完整原始日志、压力实验与事件快照保存在`artifacts/stage3-4-validation/`及其中的`entry-regression-evidence/`。最终完整Debug/Release结论仍以[整体审核](stage3-4-final-review.md)的新批次结果为准。
