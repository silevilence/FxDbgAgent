# MCP 工具契约

阶段 2 的公开契约。当前实现进度以 ROADMAP 勾选项和验证报告为准；13 个工具、操作跟踪、资源限流和生命周期清理均复用共享 Host/Engine；独立 Agent 证据和完整回归结论见 docs/validation 下的阶段 2 报告。

运行 `./eng/publish-mcp.ps1 -Configuration Debug`，用 `dotnet <发布目录>/fxdbg-mcp.dll` 作为 MCP command/args。发布目录默认 `artifacts/mcp/Debug`，其旁 `engines/` 放置完整 x86/x64 Engine 与 DIA/ClrDebug 等依赖。可使用 `--engine-dir <绝对路径>` 覆盖，不依赖当前工作目录。仅本机 stdio，无网络监听。

协议固定为 `2025-11-25`，顺序为 initialize → notifications/initialized → tools/list 或 tools/call；ping 可用于探测。未知客户端版本得到本服务支持的版本，客户端决定是否兼容。外部逐行 UTF-8 JSON-RPC 与内部 Named Pipe 长度帧分离。stdout 只输出协议，启动错误写 stderr 并非零退出；SDK 原始消息日志关闭。

使用官方 `ModelContextProtocol` / `ModelContextProtocol.Core` **2.2.0**（Apache-2.0），NuGet 源提交 `6fa3825973949a9c4f0cd8af344e15a8db09dc35`，精确依赖且不引用 AspNetCore/HTTP 包。该 SDK 支持 2025-11-25；应用显式固定此版本，不启用更新协议的 discover/Tasks 功能。来源：[官方 SDK](https://github.com/modelcontextprotocol/csharp-sdk)、[协议](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)。

## 公共输入与输出

输入为 JSON 对象，字段区分大小写，拒绝未知字段、类型错误和 null（除明确允许的值）。所有工具可选 `timeoutMs`：整数 1～240000，默认 10000。除 launch/attach 外必填 `sessionId`（非空 GUID 字符串），由创建结果取得，不由调用方编造。帧、变量引用和断点 ID 是不透明字符串。

`tools/list` 的 inputSchema/outputSchema 为机器契约；输入契约与执行校验共用 `ToolCatalog`，`OutputSchemas` 定义每个工具的具体结果类型、可空字段和递归变量。字符串不可空白，布尔值不能用字符串代替，整数不能传小数。

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

工具业务错误包括 invalid_request、session_not_found、invalid_session_state、already_debugged、architecture_mismatch、core_clr_not_supported、symbols_missing/mismatch/read_failed、frame_not_found、value_unavailable、operation_timed_out/cancelled、operation_not_found、transport_disconnected、engine_exited、rate_limited（error.retryAfterMs=1000）。JSON-RPC 封装错误、未知方法和未知工具是协议错误；输入不合法及执行失败为工具错误。

## 调用与恢复

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"debug_status","arguments":{"sessionId":"00000000-0000-0000-0000-000000000001"}}}
```

不存在会话返回 session_not_found；缺 sessionId 或传 `timeoutMs:0` 返回 invalid_request；未知工具返回协议 InvalidParams。每个工具上表输入可以放入同一 tools/call 封装。配套 Agent 工作流提供完整调试示例、安装和错误恢复步骤。

continue/step 默认等待新的停止，也可 `waitForStop:false` 返回 operationId，由 status 查询。轮询不重复执行运行命令，不延长操作期限。每会话一个运行操作；停止或恢复运行后必须重新取有效 threadId/frameId/referenceId。运行命令、launch/attach 状态不确定时先查 status，禁止盲重试。

两种模式的 `result` 都是操作对象，含 operationId、state、createdAtUtc；终态增加 finishedAtUtc，正常完成含 stop（包括 reason=processExit）。状态为 running / completed / timedOut / cancelled / failed。异步请求可能返回前就已停止，因此不要假设初次返回一定是 running。`debug_status` 的 `result.operation` 返回指定操作的一次性终态；失败操作包含 error，等待模式同时将工具的 ok 置为 false。正常 pause 以 userPause 完成操作；detach 取消操作并结束会话，terminate 确认退出后完成操作。正在等待的请求不占用 Engine 调度线程，其他会话和本会话状态/暂停/清理仍可调用。

每会话保留至多 128 条已完成操作，终态保存 10 分钟。结束会话同样保存 10 分钟，总会话记录至多 1024，超限先淘汰最早终态。运行中的操作不因缓存裁剪消失；未知、跨会话和过期操作返回 operation_not_found。Host 重启后无历史恢复。status 的 stop 只表示当前停止，lastStop 是最近观察，运行期间不可拿 lastStop 的帧引用读取变量。

取消通知仅针对未返回请求；已返回异步操作用 pause 停止或 detach 结束。协作超时尝试暂停，传输失败安全分离；具体实际状态随结果返回。附加目标不能 terminate。异常默认仅未处理异常，读取变量不调用 Getter、ToString 或函数求值。对象读取不可获取、优化掉与 null 分别返回，不合并为 null。

取消须在 stdio 上实际发送 `{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"待取消请求的 id"}}`，取消通知自身无响应，数字/字符串 ID 类型应与原请求相同。不能把客户端本地不再等待当成服务端已取消的证据。SDK 2.2.0 的本地令牌取消在本轮测试中未可靠发出通知，专项验收通过官方客户端显式发送带 ID 的通知。取消与成功竞争只保留一个终态；收到迟到响应时客户端应忽略。状态查询取消或短期限不关闭正在运行操作的会话，且控制槽保留到实际底层查询完成，执行中取消会关闭不确定会话并尝试 Detach，status 在清理中可返回 closing=true，结束后再确认目标状态。尚未进入 Host 的取消不启动 Engine。

## 资源边界

默认每 Host 最多 8 个活动或启动中会话、32 个在途普通调用，status/pause/detach/terminate 另有 8 个有界控制槽；超限立即返回 rate_limited，不排队。可通过 `--max-sessions 1..8`、`--max-calls 1..32`、`--max-control-calls 1..8` 降低上限，非法/重复启动参数非零退出。调试操作等待占用普通槽；轮询不增加新的运行操作。

MCP 单行输入上限 4 MiB，读取过程中即检查，没有换行也不能无界增长；深度至多 64，拒绝重复 JSON 字段。逐行传输实现官方 SDK 的 ITransport 接口，MCP 协议和类型仍由固定 SDK 处理。输入通道 16 条、尚未处理完的消息 64 条、输出通道 8 条，stdout 写入期限 3 秒；不可恢复的畸形消息、溢出或传输堵塞关闭连接并进入清理，不截断 JSON。

输出在 structuredContent/text 双重封装后计算体积，为 JSON-RPC 外壳留出空间；超出 4 MiB 返回 invalid_request，变量可缩小 count/maxDepth/maxStringLength 后在原会话重试。Engine 事件队列和操作/会话缓存继续使用各自有界保留策略。continue/step 的 timeoutMs 从命令受理前开始计时，异步返回后仍有效；到期尝试暂停，返回 timedOut 和实际 StopInfo，暂停失败则关闭不确定会话。Engine RPC 传输有额外 2 秒收尾，安全暂停/分离也需有限时间，因此 timeoutMs 是操作期限而非进程清理总耗时承诺。

本阶段验收按用户 2026-09-05 最新指示使用独立子代理：子代理读取安装后的技能与本文件，自主选择并执行真实 MCP 工具调用，保留请求和结果。禁止使用 Claude Code；固定脚本回归单独记录，不替代自主 Agent 验收。

连接关闭应先关闭 MCP stdin，让服务完成安全分离。客户端不得把“断开”实现为终止整个进程树：固定 SDK 2.2.0 的 `StdioClientTransport` 在清理路径可能调用 KillTree，不能以该行为证明本服务的正常 EOF 清理。集成测试使用官方 `McpClient` + `StreamClientTransport` 连接实际子进程 stdio，由测试框架显式关闭 stdin 并等待 Host 退出；不修改 SDK。任意外部工具直接强杀 Engine/目标的边界见 `engine-protocol.md`。

## 生命周期与诊断

stdin EOF、输出断开或 Host 退出均结束所属会话，默认尝试 Detach，包括 launch 目标。MCP 输入关闭立即取消未结束处理器，避免等待四分钟运行期限才开始清理；多个 Engine 并行收尾，正常场景要求 15 秒内退出。启动阶段也接收取消。仅显式 debug_terminate 可终止 launch 目标，attach 目标一律拒绝。自然退出、detach、terminate 后及时释放 Engine，缓存的终态仍可查询。

服务初始化后每 3 秒发送标准 MCP ping，5 秒未响应则关闭会话；接收方应立即用同 ID 的 `result:{}` 响应。此机制识别 stdin 仍开着但客户端已关闭输出读取端的情况，符合 [MCP Ping 协议](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/ping)。官方客户端会自动响应。Windows 标准输入的待处理读取可能不接受取消，传输关闭后的读取任务最多再等待 1 秒，随后结束 Host 进程，不让客户端永久拖住清理。

正常 EOF/Host 被强杀后，真实 x86/x64 launch/attach 目标均验证存活和可重附加。强杀 Engine 本身时，已实测 Desktop CLR 目标也退出；Host 隔离故障并继续服务。这是独立边界，不能将其称为安全分离成功。

诊断参数：`--log-level off|error|info|debug`（默认 info）、`--log-file <本地路径>`（省略则 stderr）、`--value-logs off`（唯一允许值，任何级别都禁止值日志）。日志 API 仅接收完成状态、稳定错误码、命令、合法 sessionId、可用 PID 及 debug 级 Host 线程 ID，不接受原始请求、目标变量、环境/参数内容或异常消息；SDK 原始帧日志不启用。文件为最多约 1 MiB 的单个日志，满后清空最老记录重新写入。日志写入失败不改变已执行工具的结果。stdout 始终只有协议，业务响应仍可包含调用方请求的变量与异常数据。

阶段3分页补充：count预算涵盖本次全部顶层与递归子项；超过totalMembers返回空页。每次停止最多保留10000个对象引用，达到上限会明确报告不可获取；恢复运行后重新获取frameId/referenceId。数组直接读取请求页，不遍历完整对象图。status/threads/stack/variables只读调用取消会立即结束调用方等待，后台读取保留原期限和并发名额直到收尾，不因取消过时刷新而关闭调试连接；取消后仍应查询status，不能假定目标或旧停止保持有效。执行控制与写入类命令保持既有安全恢复语义。

阶段3源码映射：debug_launch/debug_attach可选sourceMappings数组（最多128项），每项为buildRoot、localRoot及可选module。根目录必须是绝对Windows路径；module为模块文件名或完整模块路径。匹配完整路径段、忽略大小写，匹配当前路径的模块规则优先，同级最长前缀优先；冲突明确报错。断点file用本地路径，栈/停止/绑定位置返回本地filePath及可空originalFilePath（原PDB路径）。省略配置保持原行为；配置在会话创建时固定，延迟模块共享配置。

示例sourceMappings：[{"buildRoot":"C:\\build\\src","localRoot":"D:\\workspace\\src"},{"buildRoot":"C:\\build\\src","localRoot":"D:\\workspace\\plugin","module":"Plugin.dll"}]。源码映射不会复制文件或代替PDB匹配校验。

阶段3 AppDomain：status返回appDomains数组，字段appDomainId/name/runtimeId。status、threads、stack、variables以及创建set_breakpoint可选appDomainId，省略观察全部域。未知/卸载/跨会话ID返回invalid_request。线程按当前域筛选，栈按帧所属模块的域筛选再分页；变量携带读取帧的域上下文，所选域必须匹配frameId。status仅筛选域/模块/适用断点，保留进程级停止与操作信息。断点附boundAppDomainIds；限定域卸载后回pending，不自动绑定同名新域。用新ID创建新断点，更新enabled不能改变域范围。变量引用按域隔离，但所有域共用每次停止10000引用上限；恢复运行或域卸载使旧引用失效。

阶段3-4继续使用现有13工具，无新增必填字段。真实SCM服务与完整IIS按PID附加，跨身份权限不足返回access_denied，未加载CLR的native worker返回not_managed_process（预热后重新定位）。Host/Engine按需启用已有SeDebugPrivilege并恢复，不自动提权。status.modules报告实际影子路径和相邻PDB候选；readFailed每五秒重试，缺失/失配/就绪按真实状态报告并自动重绑定。回收后的旧session不转移到新PID，显式重新attach。详细可执行流程在随包workflow的“Windows Service与IIS”章节；源码仓库另有docs/service-iis.md。
