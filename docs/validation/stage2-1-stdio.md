# 阶段 2-1：MCP stdio 骨架与快速审核

日期：2026-09-05。任务基线 75f505b。只进行本任务阻塞项快速审核；阶段二整体审核留到全部任务完成。

交付：官方 ModelContextProtocol 2.2.0（Apache-2.0）宿主、2025-11-25 协议固定、13 工具 schema 和共用输入验证、结构化/文本双输出、发布目录及 Engine 依赖清单。Host 依赖图不新增 Interop/ClrDebug；没有调用 CLI 子进程；SDK 原始报文日志未启用，启动错误仅写 stderr。用户指定的子代理自主验收口径已记录，禁止 Claude Code。

验证命令：`./eng/verify-stage2-1.ps1 -Configuration Debug`，退出码 0。包含完整解决方案构建（0 警告/错误）、官方 C# MCP 客户端和独立 Node 原始协议客户端。日志：`artifacts/stage2-validation/stage2-1-Debug.log`。

- 官方客户端：完成 initialize/initialized，验证恰好 13 个输入/输出 schema；实际调用 debug_status 得到 session_not_found；未知字段为工具 invalid_request；未知工具为协议 InvalidParams。
- 原始客户端：未初始化 tools/list 拒绝，未知版本协商至 2025-11-25，ping 正常，未知方法、缺少参数、未知请求取消、畸形 JSON、EOF、缺少 Engine 目录均有自动检查；从任意工作目录启动，stdout 每行必须为完整 JSON-RPC。
- 快速审核发现并修复阻塞：SDK 自身允许未初始化 tools/list。新增入口检查；最初采用通知订阅存在合法 initialized 紧跟请求时的竞态，改为入站消息过滤器同步记录初始化完成，原始客户端回归通过。
- 快速审核补充：发布清单覆盖所有 Engine 文件，启动时检查清单内依赖，避免仅检查两个 exe 就误报发布完整。

结论：阶段 2-1 可标记完成，无已知未修复阻塞项。真实目标输出的进程级验证及工具调试能力属于后继 2-2a/2-4；本项通过不代表 13 个工具功能已实现。Release 与全量矩阵在阶段 2-5 汇总回归。
