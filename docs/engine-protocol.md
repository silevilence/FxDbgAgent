# Engine 协议 v1

Host 创建随机命名的本机 Named Pipe，限当前用户访问；连接后校验客户端计算机与预期 Engine PID。Engine 使用 `.` 连接；Host 不引用 Interop/ClrDebug，也不加载 mscordbi。JSON 依赖固定为 Newtonsoft.Json 13.0.4（MIT），TypeNameHandling 为 None。

## 帧和消息

每帧为 4 字节无符号小端 UTF-8 字节长度，再跟一个 JSON 对象。长度范围 1～4 MiB，JSON 最大深度 64；拒绝重复字段、残缺帧、尾随 JSON 和非对象消息。单管道复用三类消息：

- 请求：`{"jsonrpc":"2.0","id":"唯一字符串","method":"state","params":{"sessionId":"会话 GUID","timeoutMs":12000,"commandTimeoutMs":10000}}`
- 响应：同一 id，包含且仅包含 result 或 error；error 使用 JSON-RPC 数值 code，`error.data.code` 为 Core 稳定错误名。
- 事件通知：method 为 `event`，params 带 sessionId、单调 sequence、kind 和 data。kind 为 stateChanged、stopped、breakpointChanged、moduleChanged。

Core 标识序列化为字符串，枚举为 camelCase。`start` 要求 protocolVersion=1，响应返回相同版本和 target；之后每个命令必须携带对应 sessionId。启动参数经管道传递，不放入 Engine 进程命令行。

## 命令

`start`、`state`、`break.set/list/remove/enable`、`continue`、`pause`、`step`、`wait`、`threads`、`stack`、`variables`、`exceptions.configure`、`modules`、`symbols.refresh`、`detach`、`terminate`。

阶段 2 兼容扩展：`state` 返回调度线程上生成的 `eventSequence` 水位；可选 `includeDetails=true` 同时读取断点与模块符号快照，供共享 Host 的观察入口使用。`break.set` 可选 `enabled`（默认 true）在同一次同步目标操作中创建禁用断点，避免先激活再跨请求禁用的命中竞态。既有 CLI 参数与默认行为不变。

超出 4 MiB 的出站结果在写入前返回 `invalid_request`，提示缩小变量页数、深度或字符串长度；连接与当前变量引用保持可用，调用方可以缩页重试。目标已退出但会话未释放时，`wait` 返回保存的 `processExit`；断点修改与符号刷新返回 `invalid_session_state`。

目标信息中 `runtimeVersion` 为 ICLRRuntimeInfo 实际返回的 CLR 承载标识（4.x 通常仍为 `v4.0.30319`），`runtimeFileVersion` 为目标已加载 `clr.dll` 的文件版本，包含实际更新构建信息；两者不能与 .NET Framework 产品版本混为一谈。附加已有调试器的目标返回 `already_debugged`，提示先分离现有调试器；失败不会替换或终止原调试会话。

命令调度只进入现有 Engine 单一线程，所有 ICorDebug 行为继续复用 Interop。Host InvokeAsync 返回统一 JSON 结果，未来 CLI/MCP 只映射入口参数，不实现独立调试逻辑。

## 超时、取消与生命周期

- 独立 I/O 任务每秒发送 `$heartbeat`；6 秒没有收到任何帧则断开。心跳不排队到调试命令线程。写入有 3 秒期限，关闭流作为不可取消 I/O 的退出手段。
- 请求和响应通过字符串 id 关联；最多 128 个并行请求，执行仍串行。`$cancel` 通知取消对应远端请求令牌。
- Host 的命令期限最多 4 分钟，传输等待额外给 2 秒收尾。协作超时返回明确错误，可保留 Pause 后的会话；客户端取消或传输期限耗尽则关闭状态不确定的会话，Engine 尝试安全 Detach。
- Engine 出站事件队列 1024、Host 未消费事件 2048；溢出时断开并清理，禁止无限积压或默默丢事件。Core 保留最近至多 10000 个事件，裁剪后序号不复用。
- Host 断开/崩溃导致管道 EOF，Engine 在调度线程尝试 Detach，并给清理有限期限。Host 不对目标调用终止；必要时只回收 Engine 本体，不使用进程树终止。重复会话 ID 在启动新 Engine 前拒绝。
- Engine 硬崩溃会被 Host 隔离、移除活动会话并报告进程/传输错误；Host 可继续建立会话。**实测直接强杀 Engine 时，Desktop CLR 目标也退出（x86/x64，退出码 0）**。因此不保证 Engine 本体硬崩溃后的目标存活；正常 Detach、客户端取消和 Host 崩溃均验证附加目标存活且可重新附加。

旧 Engine launch/attach 命令入口仅用于已有阶段 1-2/1-3 验证，仍复用相同 bootstrap、调度器和 Interop。产品 Host 使用 serve + Named Pipe。Engine serve 的 stdout 不承担日志，帧与诊断日志隔离；不记录参数帧或变量值。
