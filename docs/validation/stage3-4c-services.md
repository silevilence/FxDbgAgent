# 阶段3-4c：真实 Windows Service 调试

使用 SCM 启动的 LocalService/Session 0 双架构服务，所有产品调用经真实 MCP stdio、CLI 或 DAP 入口。没有新增服务专用调试后端，复用 3-4b 的权限范围和共享 Engine。Host 补齐终态请求拦截：状态 RPC 已返回 terminated/failed、退出事件尚未排到时，也拒绝旧会话命令，避免旧帧读取偶发变成传输错误。

## 验证范围

- MCP 每配置 14 个场景：两种架构各覆盖业务源码断点、参数和局部变量、托管栈、暂停/继续、over/into/out 三种单步、旧帧拒绝、附加目标禁止 terminate、显式 detach；另覆盖运行/停止状态下 EOF 和 Host 强杀、SCM 正常停止/重启，以及 CLI 冒烟。
- EOF/Host 强杀使用同一个 15 秒期限等待 Host/Engine 退出；随后核对同一服务 PID、SCM RUNNING、清理完成后的下一次业务心跳，并通过新 MCP 连接重新附加/分离。没有强杀 Engine 充当安全分离证据。
- SCM 停止后旧会话达到 terminated，旧帧读取被拒绝；SCM 确认 STOPPED 后才重新启动，新 PID 必须显式创建新会话。
- CLI 与 DAP 各在 x86/x64 服务中完成附加→业务断点→源码栈→分离。DAP 使用真实 Content-Length stdio 协议，验证 stdout 帧格式；分离后再等待新的业务心跳。
- 所有服务控制仅允许独立 `FxDbgStage34-services-*` 夹具，部署/清理由既有清单负责；不修改服务恢复策略，不覆盖或停止默认用户环境。

## 快速审核

| 项 | 判断与修复 |
| --- | --- |
| 心跳进度可能发生在分离之前，导致存活断言过弱 | 采纳：分离/异常清理完成后重新读取基线，再等待下一次业务写入；同时核查 PID、结果公式及 SCM 状态 |
| SCM 停止与退出通知存在竞态 | 采纳：确认旧会话结束及 SCM STOPPED 后才重新启动 |
| 测试期限取消后 CLI finally 无法执行清理 | 采纳：清理命令使用独立 15 秒期限 |
| 清理时间包含重新附加时间 | 采纳：Host/Engine 退出后立即停止计时，业务与重新附加另行验证 |
| 源码断言混合两种 Windows 路径分隔符 | 采纳：先规范化绝对路径，再核对实际 PDB 文档及行号，不放宽源码匹配要求 |
| 终态状态快照与事件队列之间存在读取竞态 | 采纳：Host 的活动会话检查同时识别已报告的终态；3 个确定性反例修复前均失败，修复后与既有竞态回归共 9 项通过 |

本项仅快速审核，整体完成审核和覆盖率分析仍留到 3-4f。

## 证据

管理员命令：`./eng/verify-stage3-4c.ps1`，默认 Debug/Release，Node 可用 `-NodePath` 指定。机器结果在 `artifacts/stage3-4-validation/services-result.json`、`services-{配置}.json`、`dap-services-{配置}.json`，各步骤日志附有进程监督清单。确定性竞态证据为 `services-terminal-race-red.log` 和 `services-terminal-race-green.log`。环境清单位于 `artifacts/stage3-4-environment-services/`。

2026-09-07 02:52:45Z 最终 Debug/Release 通过：各 101 项单元、14 个 MCP/CLI 服务场景、2 个 DAP 场景。EOF/Host 强杀下 Host/Engine 实测 58～89 毫秒退出（Debug 为 69～85 毫秒），均在 15 秒期限内，随后服务继续工作并可重新附加。全部 16 个监督步骤退出码为 0，无超时或强制进程清理；独立服务/IIS夹具已移除。快速审核无遗留阻塞，可勾选 3-4c。
