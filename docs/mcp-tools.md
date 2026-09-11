# MCP 工具契约

公开契约以tools/list及本文件为准；当前15个工具（原13工具保持兼容，阶段4新增debug_evaluate与debug_configure_exceptions）、操作跟踪、资源限流和生命周期清理均复用共享Host/Engine。实现验收状态以ROADMAP及对应验证报告为准。

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
| debug_set_breakpoint | file+line、condition?、hitCondition?，或 breakpointId+enabled | 新建line≥1、enabled=true；condition≤4096字符；hitCondition为=N或>=N。更新仅breakpointId/显式enabled，不混入位置/条件 | 断点及请求/绑定位置、条件、hitCount、conditionDiagnostic，活动会话 |
| debug_remove_breakpoint | breakpointId* | 已返回的 ID | removed=true，活动会话 |
| debug_continue | waitForStop | 默认 true | 停止/退出或异步操作，只能 stopped |
| debug_step | threadId*、kind*、waitForStop | threadId≥1；kind=into/over/out；waitForStop=true | 同 continue，只能 stopped |
| debug_pause | 无 | 正在运行的会话 | userPause 停止信息 |
| debug_status | operationId | 可选操作查询，允许运行中查询 | 状态、最近停止、符号/断点快照、序号/时间及可选操作结果 |
| debug_threads | 无 | stopped | 托管线程数组 |
| debug_stack | threadId*、start、count | start=0（0～100000），count=32（1～1024） | 托管栈数组，stopped |
| debug_variables | frameId*、referenceId、start、count、maxDepth、maxStringLength | start=0，count=100（1～1024），maxDepth=1（0～8），maxStringLength=256（1～32768） | 参数/局部/对象字段数组，stopped；展开对象时再传 referenceId |
| debug_evaluate | frameId*、expression*、evaluationTimeoutMs、count、maxDepth、maxStringLength、appDomainId | expression非空且≤4096字符；evaluationTimeoutMs=250（1～1000），其余显示上限同variables | 单个VariableInfo，stopped；对象结果referenceId可交给variables分页 |
| debug_configure_exceptions | firstChance*、rules | rules可省略；最多64项，每项kind为exact/namespace/derived，typeName非空且≤1024字符 | configured、firstChance、rules；运行中或停止态动态更新 |
| debug_detach | 无 | 安全分离，launch/attach 均适用 | 终态会话；目标继续运行 |
| debug_terminate | 无 | 仅 launch 目标 | 终态会话；附加目标返回 invalid_request |

每次工具响应同时包含 structuredContent 对象和等价的 JSON text content。成功为 `{"ok":true,"sessionId":"...","result":...}`；失败为 `{"ok":false,"sessionId":"...","error":{"code":"session_not_found","message":"..."}}`，创建前的 sessionId 可为 null。失败设置 MCP isError=true。结果中枚举使用 Core camelCase，错误码使用 Core snake_case。没有源码时位置字段可空，不捏造源码信息。

工具业务错误包括 invalid_request、session_not_found、invalid_session_state、already_debugged、architecture_mismatch、core_clr_not_supported、symbols_missing/mismatch/read_failed、frame_not_found、value_unavailable、operation_timed_out/cancelled、operation_not_found、transport_disconnected、engine_exited、rate_limited（error.retryAfterMs=1000）。JSON-RPC 封装错误、未知方法和未知工具是协议错误；输入不合法及执行失败为工具错误。

## 调用与恢复

### 受限表达式（阶段4-1）

debug_evaluate在指定当前帧解释表达式，线程由frameId确定；可选appDomainId须与帧一致。仅读取参数、局部、this、实例/静态字段及数组元素，支持括号、null/bool/字符/字符串/数值字面量、`+ - * / %`、`== != < <= > >=`、`! && ||`（短路）、字段和数组/字符串索引。多维索引使用逗号，遵守真实下界。String/Array.Length读取原生长度；未初始化静态字段不触发类型初始化。

固有操作白名单为Math.Abs/Min/Max基础数值参数、String.IsNullOrEmpty、String.Equals(string,string)（ordinal）、String.Concat(string,string)、Array.GetLength(array,dimension)。名称区分大小写，在调试器内执行，不调用目标mscorlib；目标Getter执行、ToString、方法、运算符、隐式转换、构造器、反射、赋值、自增减、循环和脚本均拒绝。对象支持字段读取、null/引用身份比较与变量树展示。

这是C#的明确子集：小整数/char算术提升int，uint与有符号整数按long提升，ulong与有符号整数混合拒绝（不实现常量隐式转换），float/double按基础提升，整数检查溢出，整数除法向零截断。十进制整数字面量无后缀依次选择int/long/ulong，支持u/l/ul、浮点f/d与指数；不支持decimal、指针或完整C#重载选择。字符串字面量支持引号、反斜杠及n/r/t/0转义；计算使用完整原始值，不用显示截断值。

Math固有操作优先保留已支持的精确小整数重载：Abs(sbyte/short)保留返回类型且最小值溢出；Min/Max的同型sbyte/byte/short/ushort保留类型。其余使用上述基础提升规则，Abs(byte/ushort/char)返回int，Min/Max(char,char)及混合小整数按基础提升。这是解释器明确的子集，不声称与完整C#重载解析相同。

ADR-005语法扩展：三元`?:`右结合且仅计算选中的分支，返回该分支的运行时类型（不做未执行分支的静态类型统一）；两边仍须通过语法及禁止调用检查。`& | ^`支持整数和bool（bool不短路），`~`仅整数，`<< >>`右操作数须可提升为int，移位次数按32/64位掩码，右移按左操作数符号扩展。优先级从高到低为一元、乘除、加减、移位、关系、相等、`&`、`^`、`|`、`&&`、`||`、`?:`。十六进制`0x`/`0X`无后缀依次选int/uint/long/ulong，支持u/l/ul/lu。

显式转换仅支持`(sbyte/byte/short/ushort/char/int/uint/long/ulong/float/double)x`，采用C# unchecked数值转换：整数窄化保留低位，浮点到整数向零截断；超范围/NaN/无穷到整数的值由Engine CLR决定，C#对此不规定固定结果，不作为跨架构恒等保证。普通整数算术仍检查溢出。见[Microsoft数值转换说明](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/numeric-conversions)。裸名先匹配参数/局部/this，未命中再读取this同名成员；参数优先于同名实例字段。

新增固有操作全部用静态白名单名称调用：String.Substring(s,start[,count])、IndexOf(s,value[,start])、Contains/StartsWith/EndsWith(s,value)使用ordinal、Trim(s)、CompareOrdinal(a,b)、IsNullOrWhiteSpace(s)。仅后两者接受null；越界返回expression_index_out_of_range。Math.Round(x[,digits])用ToEven、digits为0～15；Floor/Ceiling/Truncate/Sqrt返回double，Sign返回int，Clamp(x,min,max)沿用基础数值提升且同型小整数保留类型；min>max与Sign(NaN)返回expression_arithmetic_error。Array.IndexOf(array,value)仅一维数组，从真实下界起扫描，未找到返回下界减一；primitive按精确装箱类型和值比较（NaN相等）、对象按引用身份比较，绝不调用目标Equals，复杂值类型不做内容相等。扫描消耗既有步骤/读取预算，超限即拒绝。所有新语法与固有操作自动用于断点条件。

ADR-005属性代读：先按精确名查字段，再从接收者运行时类型沿基类查零参数属性。getter从不执行；仅接受精确IL `ldarg.0; ldfld; ret`、`ldsfld; ret`、`ldc.*/ldnull; ret`，单方法≤16字节。普通Desktop CLR方法通过已加载模块RVA及代码地址核对tiny/fat方法头，不接受异常/附加段、额外指令、调用、分支、显式接口实现、带参索引器、抽象/无IL、动态或内存模块、歧义字段。虚属性按运行时覆盖实现证明；字段同名时优先。常量按声明的基础返回类型展示，字段沿用既有原生读取；类型初始化器仍不触发，未初始化静态字段可能为默认值或不可用。

FieldDef按模块+token匹配；MemberRef验证声明类型（包括同模块泛型实例参数）、名字和字段签名后唯一匹配。不用名称猜测跨模块TypeRef：固定ClrDebug 0.4.2未提供可用ResolveTypeRef，不能证明的引用保守拒绝。类型属性元数据和证明缓存只在当前停止有效，不缓存字段值；隐藏条件命中各自使用新缓存。每表达式新增≤256次元数据探针、≤256字节累计IL，根变量元数据枚举与建缓存均计入预算，同一表达式惰性复用根描述，短路分支不读取；命中缓存避免重复IL读取；超过资源预算仍为expression_limit_exceeded。头部只读内存访问计入既有读取预算。

常见通过：List/Queue/Stack.Count、auto-property、表达式体纯字段属性、纯字段虚覆盖、静态字段属性、基础常量和值类型字段属性。常见拒绝：Dictionary.Count在当前Framework实现为多字段相减，计算属性仍拒绝；Debug带nop/分支的手写块体也不会被归一化放行。属性失败为expression_name_not_found，附不能证明只读的稳定原因，七个错误分类不变。证明检查没有调用目标方法或新增Continue。

输入4096个UTF-16代码单元，最多1024语法节点、32层、10000解释步骤、1024次值读取；单字符串32768、累计中间字符串65536个代码单元，超限拒绝，不能截断后继续计算。evaluationTimeoutMs包含Engine排队、解析、读取和计算；timeoutMs仍是Host调用期限。取消观察请求可先返回，后台只读工作在原期限内收尾；不会为求值Continue或使帧失效。不可中断原生COM故障沿用隔离清理，不能承诺此类故障后继续或强杀Engine后目标存活。

错误分类为expression_syntax_error、expression_forbidden、expression_type_error、expression_name_not_found（包括未找到字段的Getter请求）、expression_index_out_of_range、expression_arithmetic_error、expression_limit_exceeded；上下文/读取/期限继续使用invalid_session_state、frame_not_found、value_unavailable、value_optimized_away、operation_timed_out/cancelled。错误不回显表达式、值或内部堆栈。正常结果固定name=result，对象referenceId仅在当前停止有效；恢复后重新取帧。求值失败先查status，修正表达式或预算后重试，无需额外Continue。

示例：`debug_evaluate({sessionId,frameId,expression:"number + matrix[1,1]"})`；读取字段为node.Label，比较为node.Self == node，字符串比较为String.Equals(node.Label,"node-label")。

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

## 会话异常类型过滤（阶段4-2）

`debug_configure_exceptions({sessionId,firstChance:true,rules:[{kind:"derived",typeName:"System.Exception"}]})` 配置会话过滤。规则之间为 OR：exact 区分大小写匹配实际完整类型名；namespace 匹配命名空间前缀及其子命名空间，按点边界匹配（A.B 不匹配 A.Beta）；derived 匹配自身或真实基类链，不把接口当作基类，不执行反射或用户代码。名称禁止控制字符、首尾空白和通配符 `*`；最多64项，基类遍历最多128层。

`firstChance:true` 且省略 rules 保持旧行为（全部 first-chance）；显式 rules:[] 或 firstChance:false 恢复默认仅未处理异常。所有规则先验证再替换；非法配置返回 invalid_request 并保留之前配置。未处理异常始终停止。未知类型读取失败时保守停止，在 exception.diagnostic 给出脱敏诊断，便于检查、关闭过滤后继续。

运行中更新经同一调度线程串行执行，不构成停止，也不完成当前运行操作；更新后处理的回调使用新规则。未匹配的 first-chance 内部继续，不产生停止事件或新的用户停止代次。高频回调与命令公平调度；原生回调只入队，每次原生停止仍恰好一次 Continue。停止信息沿用类型、原始 `_message`、线程、源码位置与栈，禁止调用自定义 Message/ToString。

## 条件断点（阶段4-3）

`debug_set_breakpoint({sessionId,file,line,hitCondition:">=3",condition:"iteration % 2 == 0"})`：条件均为可选项，命中次数与表达式为 AND。hitCondition 只接受 `=N`（恰好第N次）或 `>=N`（从第N次起），N为1～2147483647的十进制整数，不带空白、正负号或前导零。condition使用阶段4-1的受限只读语法，必须产生bool；不做数值、字符串或对象的隐式真假转换。无条件保持原行为。

仅在原生断点实际命中且该逻辑断点启用时递增hitCount；先计数并判断次数，再按需解释表达式。模块重载、符号重绑定、启停均保留计数；删除后新建才从0开始。计数为非负Int64，极限处饱和，绝不溢出。breakpoint的condition/hitCondition/hitCount/conditionDiagnostic通过debug_status断点列表及创建/启停响应可见，状态机仍仅pending/verified/moved/unresolved。条件只能随新断点创建；按ID更新仍只支持enabled，改条件须删除重建。

条件为false时沿既有Continue配对路径内部继续，无用户停止代次、stopped事件或运行操作完成；不创建变量引用或调用目标代码。判断在原单调度线程进行，每次表达式250ms期限、阶段4-1相同语法/资源预算；不等待CLR Eval。无法读取、非bool、除零、预算耗尽、超时均保守停止，conditionDiagnostic含稳定错误分类且不包含表达式、变量值或目标堆栈。修正/删除或禁用断点后可继续，不自动重试当前停止。原生COM不可中断限制沿用ADR-004。
