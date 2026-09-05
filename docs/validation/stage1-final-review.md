# 阶段一整体审核与修复

日期：2026-09-05。固定基线 `da027a0d4e82b19581f880717de4e4b4a0e6b6de`，审核终点 `3735f3c`，比较命令 `git diff da027a0...3735f3c`；共 9 项任务提交、101 个变更文件。阶段 1-4～1-12 均先完成快速审核、原位勾选并分别提交，再由两个独立子代理分别完成以下两轴整体审核。

依据：AGENTS.md、CONTEXT.md、docs/architecture.md、ROADMAP.md、需求文档。实际 MCP 外壳及入口按用户确认留在阶段二；没有把阶段一完成宣称为完整 MCP MVP 交付。

## Standards

硬规范：未确认违反。产品路径符合 AGENTS.md 的单线程回调/命令纪律、只读变量与异常观察、断点状态、正常附加 Detach、Host/Engine 隔离及单一逻辑源要求。强杀 Engine 导致 CLR 目标退出属于已记录限制，不重复列为发现。

以下为启发式维护性建议，不影响阶段验收：

- **S1：可能的 Duplicated Code（低）**。NativeVariableValue.cs:110、FrameworkDebugSession.Exceptions.cs:59、MetadataNames.cs:46 重复字段枚举的初始化、分批读取、预算检查与 finally CloseEnum。建议提取带预算和确定性释放的辅助方法，各调用方保留字段选择逻辑。
- **S2：可能的 Speculative Generality（低）**。WindowsModuleSymbols.cs:152 的 GetMethodName 和递归 GetTypeName 没有调用方；产品栈实际走 DebugModule.GetMethodName → MetadataNames.Method。建议删除未使用 API，避免两套名称解析。

其余 smell 未发现需要报告的实例。规范要求的薄适配和 Host 转发边界不按 Middle Man 处理。

## Spec

- **P1：终态断点命令未返回状态错误（P2）**。FrameworkDebugSession.Breakpoints.cs:71 的同步包装没有验证 terminated，随后调用 process.Stop(0)。CLI 实测自然退出目标后 break 返回 internal_error。影响 set/remove/enable/refresh-symbols；应在 COM 前返回 invalid_session_state。依据 ROADMAP“非法状态下调用返回明确错误”和需求 §7.5。
- **P2：合法变量分页可能断开整个会话（P2）**。VariableReader 允许 count=129、maxStringLength=32768，129 个长 ASCII 字符串超出 RpcFrame 的 4 MiB 上限；RpcPeer.Send 原先关闭连接，Host 移除会话，调用方无法缩小分页重试。应限制总字节或返回可操作错误并保留连接。依据阶段 1-7、需求“每次展开设置…限制”“大对象和数组采用分页读取”。

未发现确认的额外开发范围或独立缺失项。MCP 入口留待阶段二，不列为缺失。

两轴统计：Standards 2 项低优先级建议、0 项硬规范违反；Spec 2 项 P2，最严重为 P2。

## 逐条决策与修复

| 编号 | 决策 | 实际处理与理由 |
| --- | --- | --- |
| S1 | 不采纳 | 当前枚举均有独立预算和 finally，原始 HRESULT 兼容已收敛至 MetadataNames.EnumFields；各路径分别有 1024/10000 等不同限额和筛选逻辑。没有确认行为缺陷，本轮不扩大 COM 资源生命周期重构范围。 |
| S2 | 完全采纳 | 删除未使用的公开 GetMethodName 及其私有递归助手；保留生产实际调用的唯一方法名解析路径。 |
| P1 | 完全采纳 | WithSynchronizedTarget 使用 RequireActive；公开 Host E2E 验证退出后设置断点返回 InvalidSessionState。修复前回归因错误码不正确失败。 |
| P2 | 完全采纳 | RpcFrame 分离编码与写入；RpcPeer 在写入任何字节之前检查大小，超限返回 invalid_request，并提示缩小页数、深度或字符串长度。真实 I/O 故障仍关闭连接。RpcPeer 新测试先复现 TransportDisconnected，再验证错误和下一请求成功；E2E 再验证 129 个 32 KiB 字符串的真实变量页拒绝后，同一引用以 count=1 成功读取。 |
| R1（主代理补充） | 完全采纳 | wait 到达时 ExitProcess 可能已被空闲调度处理，原实现返回状态错误，而较早到达会返回退出观察。回归先稳定失败；改为未释放会话返回已保存的 ProcessExit，仍检查线程、释放状态、超时与取消。24 个 E2E 场景均增加退出后的 wait。 |

共处理 5 项：4 项完全采纳、0 项部分采纳、1 项不采纳。Spec 子代理再次只读复核修复，确认其两项问题关闭，终态 wait 修复正确，未发现直接回归。

## 验证与边界

逐任务验收和整体审核前，`eng/verify-stage1.ps1` 已完整通过 Debug/Release。修复后仍使用同一入口执行最终回归，单元测试增至 63 项，并包含新增的终态与真实超大变量页检查。结果日志位于 `artifacts/stage1-validation/`。

最终结果：修复后的 Debug、Release 全量通过，汇总脚本退出码 0。包括每配置 63 项单元测试、两配置合计 24 个 Console/WinForms 端到端场景、双架构原生功能、跨进程故障与取消清理、全部 CLI 命令链和规定的阶段零回归。取消回归还捕获并纠正了处理中一次多余的提前取消检查，保留原有“先暂停再返回取消错误”的语义。所有成立的功能发现已关闭，无未修复阻塞项。

正常 Detach、Host 断开/崩溃已验证附加目标存活；直接硬杀 Engine 会使本机 Desktop CLR 目标退出，Host 保持可用。这个实测限制没有被修复或隐去，详见 [协议和生命周期](../engine-protocol.md)。
