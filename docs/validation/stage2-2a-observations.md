# 阶段 2-2a：会话、断点与观察工具

日期 2026-09-05；任务基线 8388a7b。快速审核仅检查本任务阻塞问题，完整双轴审核在阶段二结束后进行。

共享 `FxDbg.Host.Sessions.DebugSessionService` 负责会话创建、命令映射与持续事件消费，MCP 入口只验证输入和映射结构化结果。八个工具 launch/attach/set_breakpoint/remove_breakpoint/status/threads/stack/variables 已接通实际 Engine。status 同一次调度读取状态、断点与模块，带事件水位/快照时间，运行时当前 stop 为空，历史 stop 单列。

验证：`./eng/verify-stage2-2a.ps1 -Configuration Debug`，退出码 0；日志 `artifacts/stage2-validation/stage2-2a-Debug.log`。包含阶段 2-1 回归、x86/x64 × launch/attach 的真实 stdio 矩阵及 64 项单元测试（0 失败/跳过）。

矩阵断言：含空格发布/目标/工作目录、字符串参数和 env；自动位数及实际 CLR 文件版本；重复附加/强制跨位数拒绝；运行时观察非法状态；禁用创建与启用、pending 后自动绑定、删除；Windows PDB、实际停止线程及至少十层栈；参数 42、局部 84、字符串、一层字段；循环引用标识与无递归、null、不可获取；129 个 32 KiB 字符串超出内部 4 MiB 后缩页成功且原会话有效；无效帧与断点错误；EOF 后目标正常完成，目标输出/变量/env 不进入 MCP 日志。

快速审核与修复：

- 禁用断点若分两次 RPC 创建再禁用，会存在短暂启用窗口。改为 Core 默认 enabled=true 的兼容参数，经既有 `WithSynchronizedTarget` 在单次目标同步中创建；单元测试断言所有发布状态和实际绑定从未启用。
- status 增加调度线程事件水位与历史停止分离；修正 stack 起始帧公开上限为现有 Engine 的 100000。
- SDK 2.2.0 的 StdioClientTransport 清理使用 KillTree，曾使 launch 目标直接退出而未完成。本项目不修改 SDK；验收保留官方 McpClient + StreamClientTransport，由测试框架先关闭实际 stdin 并等待 Host 退出，从而验证真实 EOF 而非客户端进程树强杀。四组合均正常完成。
- 附加返回时模块回调可能仍在路上，测试/文档按契约轮询 pending 直到绑定，才放行样例；路径先规范化，避免 Windows 分隔符差异导致错误断言。

结论：本项快速审核无未修复阻塞，可原地勾选。执行控制、操作缓存、限流与完整故障矩阵依 ROADMAP 后续任务实施；Release、优化变量场景和完整 CLI/阶段零回归纳入最终矩阵，不由此报告声称已经运行。
