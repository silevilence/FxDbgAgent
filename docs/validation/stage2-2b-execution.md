# 阶段 2-2b 执行控制验收

2026-09-05，Windows Desktop CLR，Debug。

共享 Host 跟踪 continue/step 的同步等待与异步 operationId，五个执行工具全部接通现有 Engine。停止序号水位避免历史事件完成新操作；终态只写一次，过期/容量裁剪不删除运行操作，结束会话保留查询。

验证命令：`./eng/verify-stage2-2b.ps1 -Configuration Debug`；随后分别运行 `tests/FxDbg.IntegrationTests/bin/Debug/net48/FxDbg.IntegrationTests.x86.exe <repo> Debug execution` 与 x64 入口。

证据：`artifacts/stage2-validation/stage2-2b-Debug.log`。完整构建零警告/错误；标准与原始 stdio 协议回归通过；x86/x64 launch/attach 观察四组合通过；66 项单元测试通过、无跳过；双架构三种源码单步、等待/轮询断点与自然退出、未处理异常、各 100 次 continue/pause、并发会话与等待中暂停全部通过；既有双架构原生执行控制回归通过。执行套件只清理自己创建的目标，EOF 后目标存活单独由观察套件及后续生命周期矩阵断言。

快速审核并修复的阻塞项：自然退出原先只有状态事件，导致操作错误超时，现附带实际 processExit StopInfo；捕获已完成操作引用后再等待查询，避免并发裁剪引发查找异常；终态快照不遗留上一次所查 operation；快速完成及时取消期限计时器。立即继续/暂停后的 Terminate 暴露旧回调入队问题：原生 Terminate 成功后只等待 ExitProcess，不再对之前排队的回调重复记录停止或 Continue。该分支在双架构真实目标及既有执行回归通过。

原生 Terminate 是终止进程的操作（[Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-terminate-method)）；现有 Desktop CLR 路径仍以真实 ExitProcess 确认，不凭命令受理宣告完成。

期限/取消/限流专项在 2-2c，生命周期专项在 2-4，Release 与全量审核在阶段末执行。本记录不把尚未执行的后继验收标为通过。
