# MCP 工具契约

阶段 2 的公开契约。当前实现进度以 ROADMAP 勾选项和验证报告为准；13 个工具均已接通共享 Host/Engine，资源限流和完整生命周期验收按后续任务实施。

运行 `./eng/publish-mcp.ps1 -Configuration Debug`，用 `dotnet <发布目录>/fxdbg-mcp.dll` 作为 MCP command/args。发布目录默认 `artifacts/mcp/Debug`，其旁 `engines/` 放置完整 x86/x64 Engine 与 DIA/ClrDebug 等依赖。可使用 `--engine-dir <绝对路径>` 覆盖，不依赖当前工作目录。仅本机 stdio，无网络监听。

协议固定为 `2025-11-25`，顺序为 initialize → notifications/initialized → tools/list 或 tools/call；ping 可用于探测。未知客户端版本得到本服务支持的版本，客户端决定是否兼容。外部逐行 UTF-8 JSON-RPC 与内部 Named Pipe 长度帧分离。stdout 只输出协议，启动错误写 stderr 并非零退出；SDK 原始消息日志关闭。

使用官方 `ModelContextProtocol` / `ModelContextProtocol.Core` **2.2.0**（Apache-2.0），NuGet 源提交 `6fa3825973949a9c4f0cd8af344e15a8db09dc35`，精确依赖且不引用 AspNetCore/HTTP 包。该 SDK 支持 2025-11-25；应用显式固定此版本，不启用更新协议的 discover/Tasks 功能。来源：[官方 SDK](https://github.com/modelcontextprotocol/csharp-sdk)、[协议](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)。

## 公共输入与输出

输入为 JSON 对象，字段区分大小写，拒绝未知字段、类型错误和 null（除明确允许的值）。所有工具可选 `timeoutMs`：整数 1～240000，默认 10000。除 launch/attach 外必填 `sessionId`（非空 GUID 字符串），由创建结果取得，不由调用方编造。帧、变量引用和断点 ID 是不透明字符串。

`tools/list` 的 inputSchema/outputSchema 为机器契约；契约定义与执行校验共用 `ToolCatalog`。字符串不可空白，布尔值不能用字符串代替，整数不能传小数。

| 工具 | 额外输入（* 为必填） | 默认值 / 约束 | 成功 result / 合法状态 |
|---|---|---|---|
| debug_launch | exe*、args、cwd、env、arch、stopAtEntry | exe/cwd 字符串；args 字符串数组；env 字符串字典；arch=auto/x86/x64，默认 auto；stopAtEntry=false | 目标信息（PID、实际架构、CLR 标识/文件版本、状态），创建新会话 |
| debug_attach | pid*、arch | pid 正整数；arch 同上 | 目标信息，创建新会话；不得终止目标 |
| debug_set_breakpoint | file+line，或 breakpointId+enabled | 新建 file/line 必填，line≥1、enabled=true；更新仅 breakpointId/显式 enabled，不允许混入 file/line | 断点及请求/绑定位置，活动会话 |
| debug_remove_breakpoint | breakpointId* | 已返回的 ID | removed=true，活动会话 |
| debug_continue | waitForStop | 默认 true | 停止/退出或异步操作，只能 stopped |
| debug_step | threadId*、kind*、waitForStop | threadId≥1；kind=into/over/out；waitForStop=true | 同 continue，只能 stopped |
| debug_pause | 无 | 正在运行的会话 | userPause 停止信息 |
| debug_status | operationId | 可选操作查询，允许运行中查询 | 状态、最近停止、符号/断点快照、序号/时间及可选操作结果 |
| debug_threads | 无 | stopped | 托管线程数组 |
| debug_stack | threadId*、start、count | start=0（0～100000），count=32（1～1024） | 托管栈数组，stopped |
| debug_variables | frameId*、referenceId、start、count、maxDepth、maxStringLength | start=0，count=100（1～1024），maxDepth=1（0～8），maxStringLength=256（1～32768） | 参数/局部/对象字段数组，stopped；展开对象时再传 referenceId |
| debug_detach | 无 | 安全分离，launch/attach 均适用 | 终态会话；目标继续运行 |
| debug_terminate | 无 | 仅 launch 目标 | 终态会话；附加目标返回 invalid_request |

每次工具响应同时包含 structuredContent 对象和等价的 JSON text content。成功为 `{"ok":true,"sessionId":"...","result":...}`；失败为 `{"ok":false,"sessionId":"...","error":{"code":"session_not_found","message":"..."}}`，创建前的 sessionId 可为 null。失败设置 MCP isError=true。结果中枚举使用 Core camelCase，错误码使用 Core snake_case。没有源码时位置字段可空，不捏造源码信息。

工具业务错误包括 invalid_request、session_not_found、invalid_session_state、already_debugged、architecture_mismatch、core_clr_not_supported、symbols_missing/mismatch/read_failed、frame_not_found、value_unavailable、operation_timed_out/cancelled、operation_not_found、transport_disconnected、engine_exited。资源限流任务补充 rate_limited（含 retryAfterMs）。JSON-RPC 封装错误、未知方法和未知工具是协议错误；输入不合法及执行失败为工具错误。

## 调用与恢复

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"debug_status","arguments":{"sessionId":"00000000-0000-0000-0000-000000000001"}}}
```

不存在会话返回 session_not_found；缺 sessionId 或传 `timeoutMs:0` 返回 invalid_request；未知工具返回协议 InvalidParams。每个工具上表输入可以放入同一 tools/call 封装。阶段 2-3 将补充可直接执行的完整调试示例。

continue/step 默认等待新的停止，也可 `waitForStop:false` 返回 operationId，由 status 查询。轮询不重复执行运行命令，不延长操作期限。每会话一个运行操作；停止或恢复运行后必须重新取有效 threadId/frameId/referenceId。运行命令、launch/attach 状态不确定时先查 status，禁止盲重试。

两种模式的 `result` 都是操作对象，含 operationId、state、createdAtUtc；终态增加 finishedAtUtc，正常完成含 stop（包括 reason=processExit）。状态为 running / completed / timedOut / cancelled / failed。异步请求可能返回前就已停止，因此不要假设初次返回一定是 running。`debug_status` 的 `result.operation` 返回指定操作的一次性终态；失败操作包含 error，等待模式同时将工具的 ok 置为 false。正常 pause 以 userPause 完成操作；detach 取消操作并结束会话，terminate 确认退出后完成操作。正在等待的请求不占用 Engine 调度线程，其他会话和本会话状态/暂停/清理仍可调用。

每会话保留至多 128 条已完成操作，终态保存 10 分钟。结束会话同样保存 10 分钟，总会话记录至多 1024，超限先淘汰最早终态。运行中的操作不因缓存裁剪消失；未知、跨会话和过期操作返回 operation_not_found。Host 重启后无历史恢复。status 的 stop 只表示当前停止，lastStop 是最近观察，运行期间不可拿 lastStop 的帧引用读取变量。

取消通知仅针对未返回请求；已返回异步操作用 pause 停止或 detach 结束。协作超时尝试暂停，传输失败安全分离；具体实际状态随结果返回。附加目标不能 terminate。异常默认仅未处理异常，读取变量不调用 Getter、ToString 或函数求值。对象读取不可获取、优化掉与 null 分别返回，不合并为 null。

本阶段验收按用户 2026-09-05 最新指示使用独立子代理：子代理读取安装后的技能与本文件，自主选择并执行真实 MCP 工具调用，保留请求和结果。禁止使用 Claude Code；固定脚本回归单独记录，不替代自主 Agent 验收。

连接关闭应先关闭 MCP stdin，让服务完成安全分离。客户端不得把“断开”实现为终止整个进程树：固定 SDK 2.2.0 的 `StdioClientTransport` 在清理路径可能调用 KillTree，不能以该行为证明本服务的正常 EOF 清理。集成测试使用官方 `McpClient` + `StreamClientTransport` 连接实际子进程 stdio，由测试框架显式关闭 stdin 并等待 Host 退出；不修改 SDK。任意外部工具直接强杀 Engine/目标的边界见 `engine-protocol.md`。
