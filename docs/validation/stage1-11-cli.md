# 阶段 1-11 CLI 验收

日期：2026-09-05。依据需求 §9 和 ROADMAP 1-11。

已实现 launch/attach/break/continue/step/stack/variables/detach，以及所需 pause/wait/threads、模块/事件和断点管理命令。独立命令经本机控制管道连接同一后台 Host，复用 EngineProcessHost 和既有 Engine RPC，不包含 ICorDebug 或独立调试逻辑。

`eng/verify-stage1-11.ps1 -Configuration Debug` 与 `Release` 均通过：双架构完整命令链，自动位数路由、入口停止、源码断点、变量结果、Step Over、事件轮询、Detach 后 Host/Engine/自结束样例全部退出；附加外部启动的测试样例后暂停、读线程、Detach，目标存活。5 项 CLI 参数测试覆盖引号、空参数、重复参数/环境和非法命令，构建零警告、零错误。

快速审核修复阻塞项：普通后台 Process.Start 在 PowerShell 输出捕获场景仍保留了启动命令的输出句柄，导致 launch 管道不结束；改为 CreateProcess 的 DETACHED_PROCESS、inheritHandles=false，显式释放启动线程/进程句柄后批量测试通过。增加命令选项适用范围检查，拒绝静默忽略的参数。

结论：阶段 1-11 可原位勾选。按目标开始前确认的边界，MCP 外壳和实际 MCP 入口验收留在阶段 2；当前语义由共同 Host/Engine 保证同源。
