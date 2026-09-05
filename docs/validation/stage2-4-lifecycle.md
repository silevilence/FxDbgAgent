# 阶段 2-4 生命周期与安全清理

2026-09-05，Windows Desktop CLR，Debug。

命令：`./eng/verify-stage2-4.ps1 -Configuration Debug`，证据 `artifacts/stage2-validation/stage2-4-Debug.log`。包含协议/四组合观察/双架构执行及资源回归、70 项单元测试，并新增真实 stdio 故障矩阵。构建零警告/错误，未跳过测试。

生命周期结果：

- Console x86/x64 × launch/attach × EOF-running、EOF-stopped、EOF-waiting、Host-kill-running、Host-kill-stopped、Host-kill-waiting、output-waiting、Engine-crash 共 32 组合通过。普通 EOF/Host 强杀约 0.8～1.1 秒完成退出和重新附加；输出断开约 9.1～9.4 秒，全部在 15 秒内。正常路径目标存活；Engine 硬崩溃时目标退出，明确记录既有 CLR 限制，Host 仍可创建并终止新会话。
- 8 个混合架构附加会话，包含停止和长等待，并发重复 EOF / Host 强杀后全部 Engine 退出、目标存活且可重附加；含重附加确认总计约 3.9 秒。
- 双架构 launch/attach 均在启动响应返回前关闭输入；Engine 在期限内退出。该次 launch 中断发生在目标尚未创建时；attach 目标保持存活并可重新附加，不把未创建的目标记作“存活”。
- 12 次双架构交替 launch/terminate 后无所属 Engine 遗留，Host 句柄基线 307、最终 309（阈值允许初始化后增加至多 32）。普通清理和故障测试只清理本测试持有的目标 PID，不终止进程树。
- debug 级文件日志与 stderr 扫描通过：唯一环境标记、变量文本、目标 stdout/stderr 标记、未处理异常消息不进入诊断；文件逐行解析为元数据 JSON。off 级和禁用值日志配置另有原始 stdio 断言。

快速审核阻塞修复：SDK 在 EOF 时会等待仍在执行的处理器，现传输断开直接联动取消；输出读取端单独关闭可能暂时没有业务响应，使用标准 MCP 双向 ping 检测；Windows 取消后仍挂起的标准输入读取不能阻挡退出，收尾等待限定 1 秒；多 Engine 并行清理与可重入会话 Dispose；启动回调等待传入取消。日志结构白名单避免调试级别误输出数据，日志 I/O 失败不把已执行调试命令伪装为可重试失败。

最后日志 PID 字段/关闭日志断言补充后，重跑 2-2c 全前序回归及双架构原生执行控制，证据 `artifacts/stage2-validation/stage2-4-final-quick.log`。Release、WinForms 和全阶段审核仍由阶段末任务执行。
