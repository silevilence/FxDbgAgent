# 阶段 1-12 端到端验收

日期：2026-09-05。依据 ROADMAP 1-12 和需求 §13。实际 MCP 外壳与同一 MCP 入口验收按用户在目标开始前确认的边界留到阶段 2；本阶段通过共同 Host/Engine 接口及 CLI 验收调试能力。

一键入口：`./eng/verify-stage1.ps1`；单配置：`./eng/verify-stage1-12.ps1 -Configuration Debug|Release`。汇总日志在 `artifacts/stage1-validation/Debug.log`、`Release.log`，详细原生验证与 CDB/SOS 对照仍在对应阶段的 artifacts 目录。

| 验收项 | 自动化证据 |
| --- | --- |
| §13.1 的双架构自动路由 | Host 以 auto 启动 Console/WinForms × x86/x64；MCP 入口部分待阶段 2 |
| §13.2 源码断点 | Windows PDB 断点标识与命中标识一致；延迟模块先 pending 后实际命中；原生套件覆盖缺失、错配、损坏 PDB 和动态重绑定 |
| §13.3 停止与栈 | 检查停止原因、线程、源码行；递归栈至少 14 层；独立 x86/x64 CDB/SOS 对照 |
| §13.4 变量 | 参数、基础局部变量、字符串、一层字段；循环字段返回相同引用且不递归；Getter/ToString 调用计数始终为零 |
| §13.5 执行控制 | Continue、Over 到调用行、Into 进入辅助方法、Out 返回调用者，再 Over；原生套件补充暂停、超时与取消 |
| §13.6 异常 | Console 与 WinForms 后台工作线程真实未处理异常，验证存储的类型/消息、抛出行和栈；原生套件覆盖 first-chance 策略 |
| §13.8 拒绝路径 | 阶段 1-2 覆盖 CoreCLR、位数不匹配等；模块/符号套件覆盖 PDB 失败状态 |
| §13.9 应用矩阵 | 两种配置 × Console/WinForms × x86/x64 × 观察/延迟模块/异常，共 24 场景 |

另包含全部 62 项单元测试、原生双架构断点/执行/变量/异常/模块套件、跨进程 RPC 与 Host/Engine 故障清理、CLI 独立命令进程链，以及仓库规定的阶段 0-3/0-4/0-6 Debug/Release 回归。

结果：一键入口的 Debug 与 Release 全部通过，退出码 0；阶段 1-12 可原位勾选。

后续整体审核新增终态 wait、终态非法断点操作与超大变量页缩页重试，单元测试增至 63 项，见 [整体审核与修复](stage1-final-review.md)。

快速审核修复：目标自然退出后，协议 detach 改为释放终态会话资源；CDB 使用本地符号路径与 30 秒硬超时，避免无人值守验证被远程符号查询挂起。Release x64 的 JIT 确实将验收样例局部变量优化掉，产品正确返回 optimizedAway；仅基础局部变量验收方法指定 NoOptimization，优化状态仍由独立变量套件覆盖。WinForms 异常样例使用工作线程，避免 UI BeginInvoke 将原异常包成 TargetInvocationException。

生命周期边界沿用阶段 1-10 的实测结论：正常 Detach、Host 异常退出均保护附加目标；强制杀死 Engine 时 Desktop CLR 可能连带结束目标，不能承诺目标存活。Host 本身保持可用并可建立新会话。
