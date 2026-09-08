# FxDbg Agent 架构与技术验证记录

> 状态：阶段 0 报告已获用户确认，阶段 1 产品实现进行中。汇总结论见 [阶段 0 技术验证报告](stage0-validation-report.md)。

## 已定架构约束

- `FxDbg.McpHost` 负责统一入口、会话管理与目标架构路由，不加载 `mscordbi.dll`。
- `FxDbg.Engine.x86.exe` 与 `FxDbg.Engine.x64.exe` 分进程承载 ICorDebug，Engine 与目标进程位数必须一致。
- Engine 与 Host 使用 Named Pipe + JSON-RPC；CLI、MCP 与未来 DAP 共享同一 Host/Engine 逻辑。
- MVP 只观察目标状态，不执行函数求值、属性 Getter、`ToString()` 或变量修改。

## Engine 命令调度与回调桥（阶段1-3）

- `FxDbg.Engine.Runtime` 提供每个 Engine 会话独占的单线程命令调度器；Engine 主线程只处理 stdio 生命周期，不调用 ICorDebug。
- 启动、附加、回调消费、`Continue`、Detach 与 ICorDebug 终止均在同一调度线程执行。`FxDbg.Interop` 会校验会话操作线程，跨线程调用立即失败。
- ClrDebug 回调处理器只把最小回调信封写入并发队列；调度线程空闲时消费信封并执行对应的 `Continue(false)`，回调线程不执行调试命令或 I/O。
- 命令从入队时开始计算超时；排队中的取消会立即完成且不会执行，执行中的超时/取消通过令牌协作生效。命令必须在不可重复副作用前检查令牌，成功返回是提交点，返回后到达的取消不得把成功改报失败；工作项结束后调度器仍可串行接受后续命令。
- `ContinueStopCoordinator` 将每个停止计数与一次 Continue 原子配对：原生 Continue 成功后才提交 Running 状态，失败时保留未消费停止；重复 Continue 与重复/倒序停止序列均返回明确错误。

## 源码断点生命周期（阶段1-4）

- `FxDbg.Core.Breakpoints.BreakpointManager` 是断点状态的单一来源；每个逻辑断点可绑定多个模块加载实例。相同 DLL 在不同 AppDomain 的加载实例使用不同身份，卸载其中一个不会丢失其他实例的绑定。
- `FxDbg.Symbols.Windows` 是产品 Windows PDB 读取层，与技术 Probe 无运行时依赖。读取本地同名 PDB、验证 CodeView GUID/stamp/age，使用真实 PE 元数据 provider；在分配原生返回数组前检查计数，限制输入大小及托管处理时间。
- 未找到含目标源码的已加载符号时保持 `pending`；存在源码但无可绑定 IL 或 CLR 报告绑定失败时为 `unresolved`。成功绑定为 `verified`，最近可执行行与请求行不同时为 `moved`，保留请求行、实际行和诊断。
- 模块 Load/Unload、UpdateModuleSymbols 仅在回调线程入队，由命令线程处理。已加载模块的本地 PDB 文件时间/大小每 500 ms 检查一次，变化后在同步停止内重新读取及绑定，启用状态保持不变。
- 断点删除会停用其全部原生绑定并发布删除通知；模块卸载时丢弃失效绑定及符号缓存，不对已经失效的 COM 对象调用 Activate。Detach 前停用仍有效的原生断点。
- `eng/verify-stage1-4.ps1 -Configuration Debug|Release` 使用同一产品 Interop/Core/Symbols 验证 x86/x64 延迟模块、延迟 PDB、禁用/启用、命中、AppDomain 卸载重载与删除后不再绑定。执行控制和跨进程请求入口仍按后续任务实施。

## 执行控制（阶段1-5）

- 产品会话复用 Core 状态机；启动、可观察停止、继续、Detach、退出及清理失败产生有序状态事件。入口处尚无托管线程、或进程级暂停无法确定托管线程时，`threadId = 0` 明确表示未知；断点与单步停止仍要求真实线程 ID。
- Continue 消费当前唯一停止；Step 必须指定存在且具有活动 IL 帧的线程。Into/Over 使用 PDB 下一可执行源码行构造 IL 范围，Out 使用当前帧的 Stepper。断点/暂停打断单步时停用 Stepper，Detach 前同样清理 Stepper 和有效断点。
- 等待运行结果可超时或取消；届时尝试暂停目标，返回 `operation_timed_out` / `operation_cancelled`，保留可检查且可再次继续的停止状态。底层不可中断 COM 调用的进程级超时约束由阶段 1-10 完成。
- Detach 支持启动与附加目标，均让目标继续运行。Terminate 拒绝附加目标；对启动目标先同步停止，再请求终止并等待真实 ExitProcess。当前 Desktop CLR 实测 Terminate 后停止已失效，额外 Continue 会返回 `CORDBG_E_SUPERFLOUS_CONTINUE`，因此终止不走正常 Resume 路径，ExitProcess 也不 Continue。
- 复现：`eng/verify-stage1-5.ps1 -Configuration Debug|Release`，覆盖双架构源码单步、非法状态/线程、超时/取消、活动断点清理、启动/附加 Detach、停止/运行中 Terminate，并回归阶段 1-4。
- 单步范围语义参考 [Microsoft ICorDebugStepper::StepRange](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugstepper-steprange-method)。

## 托管线程与调用栈（阶段1-6）

- 线程及栈读取要求可观察停止，均在命令线程执行。线程返回 CLR 调试线程 ID、AppDomain、停止状态；名称直接读取 Thread 对象的 `m_Name` / `_name` 字段，不求值 Getter，无可读名称时返回 null。
- 只枚举指定线程的托管链和 IL 帧，使用原生枚举器逐项读取，跳过纯原生及内部帧；每次返回最多 1024 帧。模块缺少 PDB 时仍从 CLR 元数据取得方法全名，源位置允许为空。
- 帧 ID 包含停止代次、线程及托管帧深度，同一停止中分页稳定；继续运行、退出或 Detach 清理帧引用。栈帧 AppDomain 使用帧所属模块的 Assembly/AppDomain，避免把跨域调用的所有帧误标为活动线程的域。
- 停止摘要仅读取当前线程前 5 个托管帧，完整栈按请求读取。`eng/verify-stage1-6.ps1 -Configuration Debug|Release` 对 x86/x64 14 层托管栈进行 CDB/SOS 独立逐帧对照；CDB 使用本地符号路径，30 秒超时后清理测试进程树。

## 只读变量观察（阶段1-7）

- Core VariableReader 对单次停止维护引用表、循环检测及总成员预算；Interop 薄适配只读取参数、局部槽、实例/静态字段和数组元素，Windows PDB 提供当前 IL 作用域的变量名。
- 默认深度 1、成员 100、字符串 256；上限分别为 8、1024、32768。成员预算覆盖根节点及后代，数组分页直接按索引取值；继续、退出和 Detach 清理引用表。引用 ID 仅在当前停止有效。
- Null、OptimizedAway、Unavailable 明确区分；只有 CLR 的 IL_VAR_NOT_AVAILABLE 映射为优化掉，读取失败保留错误码，不凭 Release 配置猜测。字符串读取使用有界缓冲区，不通过 Getter 或任何目标格式化方法。
- `eng/verify-stage1-7.ps1 -Configuration Debug|Release` 覆盖双架构参数/局部变量、静态字段、循环引用、十万元素数组尾页及目标格式化副作用为零。

## 异常观察（阶段1-8）

- 默认仅未处理托管异常停止；会话可显式配置 first-chance，关闭后恢复默认。忽略 USER_FIRST_CHANCE 与 CATCH_HANDLER_FOUND 的重复通知。
- Exception2 回调只入队，命令线程读取 CurrentException、准确类型、最多 32 帧和抛出位置。消息仅读取 mscorlib 中 System.Exception 的 `_message` 字段，不执行 Message Getter 或 ToString；缺失消息保持 null，读取错误带诊断。
- [Microsoft 的 Exception 回调约定](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugmanagedcallback2-exception-method) 规定未处理通知的 Frame 为 null；实现从异常线程取栈，双架构 Debug/Release 实测抛出位置与源码标记一致。

## 模块快照与符号状态（阶段1-9）

- Core 定义不可变 ModuleInfo 和 ModuleChangedEvent；模块 Load/Unload、符号状态变化与会话状态使用同一事件序列。快照包含加载实例 ID、路径、AppDomain、PDB 路径及 Loaded/Missing/Mismatch/ReadFailed 状态。
- 同一模块恢复或替换 PDB 保留实例 ID，真正卸载重载后产生新 ID。名称元数据、PDB 与断点缓存由 DebugModule 统一持有，卸载释放；事件快照不保留原生对象，退出后活动模块列表为空。
- `eng/verify-stage1-9.ps1 -Configuration Debug|Release` 使用真实 Windows PDB 验证缺失、不匹配、损坏、恢复及 AppDomain 重载，回归断点与线程栈。

## Engine/Host 进程协议（阶段1-10）

- 产品 Host 使用本机 Named Pipe + JSON-RPC v1，随机管道名、当前用户 ACL、预期 Engine PID 与本机校验；协议层为 netstandard2.0，Host 不依赖 Interop。帧格式、命令、事件及超时规则见 [engine-protocol.md](engine-protocol.md)。
- 管道 I/O、心跳与命令线程分离；调试操作只进入原有单一调度器。事件出站有界，帧不写入日志；Core 历史有界且事件序号保持单调。
- Host 崩溃、取消及正常关闭时，Engine 尝试在调度线程安全 Detach；真实双架构暂停附加目标均存活且可再次附加。强杀 Engine 本体会使 Desktop CLR 目标退出，不能宣称此路径能保活目标；Host 隔离错误并可继续工作。
- `eng/verify-stage1-10.ps1 -Configuration Debug|Release` 验证真实跨进程调试、双向崩溃、超时/取消、反复清理和句柄数量。

## CLI 持久会话入口（阶段1-11）

- CLI 参数解析和输出由 FxDbg.Cli 承担；独立命令通过本机管道访问 FxDbg.Host.PersistentSessionServer，后者调用同一 EngineProcessHost。CLI 不承载原生调试逻辑。
- 每个会话一个后台 Host，使用不继承句柄的 detached 进程启动，避免输出捕获等待后台进程退出。Detach 或 30 分钟空闲结束会话；请求中途断开执行既有取消/安全 Detach 流程。
- [CLI 文档](cli.md) 包含全套命令和 ID 使用规则。`eng/verify-stage1-11.ps1 -Configuration Debug|Release` 验证独立进程命令链与后台生命周期。

## ClrDebug 依赖结论（阶段0-2）

- NuGet 包固定为 `ClrDebug` **0.4.2**；包内仓库提交为 `9628778ff761b2e466ca3199392cbd3de6de5bc5`，本次验证下载的 nupkg SHA-256 为 `880276A4D34EAA32EF6FB3598B7464E16C133B1184E9963D59398607BB5EDFBA`。
- 包许可证为 **MIT**（Copyright © 2022 lordmilko），允许本项目引用与分发；项目不得复制或修改其自动生成包装代码。
- 0.4.2 同时提供 `net8.0` 与 `netstandard2.0` 资产，无传递依赖。本阶段使用 `net48` x86 Probe 加载 `netstandard2.0` 资产并调试 `net40` 目标。
- 维护状态：单维护者项目；0.4.2 于 2026-08-03 发布。固定版本并通过 `FxDbg.Interop` 薄适配层隔离，若上游失修则替换适配层实现。
- Framework CLR 必须显式按 `CLRMetaHost.GetRuntime("v4.0.30319") → CLRRuntimeInfo.GetInterface().CorDebug → Initialize()` 创建会话；`new CorDebug()` 会选择 Engine 当前运行时，不能作为“已绑定 FX4”的证据。

### 阶段0-2实测

- x86 Engine Probe 启动 `Fx40.Console.x86` 后收到 `CreateProcess` 回调，报告实际运行时 `v4.0.30319`。
- x86 Engine Probe 附加到已运行的 `Fx40.Console.x86` 后收到 `CreateProcess` 回调；Detach 后目标保持运行。
- 回调仅记录必要字段并将事件入队；`Continue`、Detach 与 `ICorDebug.Terminate` 在调用线程执行。

### 主要来源

- [ClrDebug 0.4.2 NuGet 元数据](https://www.nuget.org/packages/ClrDebug/0.4.2)
- [ClrDebug 上游仓库与 ICorDebug 示例](https://github.com/lordmilko/ClrDebug)
- [ICorDebug 接口（Microsoft Learn）](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface)
- [ICLRRuntimeInfo::GetInterface（Microsoft Learn）](https://learn.microsoft.com/dotnet/framework/unmanaged-api/hosting/iclrruntimeinfo-getinterface-method)

## 双架构与拒绝路径结论（阶段0-3）

| Engine Probe | 目标 | 启动 | 附加 | 目标结果 |
|---|---|---:|---:|---|
| x86 | FX 4.0 x86 | 通过 | 通过 | Detach 后存活 |
| x64 | FX 4.0 x64 | 通过 | 通过 | Detach 后存活 |

错误路径同时验证了 x86→x64 与 x64→x86 两个跨位数组合，并覆盖纯原生进程、CoreCLR 8 进程和 CLR v2.0.50727 进程。Probe 分别返回 `architecture_mismatch`、`not_managed_process`、`coreclr_not_supported`、`unsupported_clr_version`，且每个由验收脚本创建的目标在拒绝附加后仍保持运行。

阶段 0 Probe 使用 `IsWow64Process2` 和已加载 runtime 模块完成可解释的前置分类；这只是验证错误语义。产品化架构探测仍按既定方案在阶段1-2实现：启动模式读取 PE + CorFlags（含 AnyCPU 32BitPreferred），附加模式使用 `IsWow64Process2` 并回退 `IsWow64Process`。

## 回调线程模型结论（阶段0-4）

- 一次完整的 FX 4.0 x86 启动序列实测包含 `CreateProcess`、`CreateAppDomain`、`LoadAssembly`、`LoadModule`、`CreateThread` 五类核心回调。
- ClrDebug 将托管事件串行派发到同一个专用托管回调线程；Probe 的命令线程 ID 与该回调线程 ID 不同。
- 回调处理器只采集事件类型、Controller 和回调线程 ID 后写入无界内存队列；Continue 和日志文件 I/O 全部由命令线程执行。
- 普通回调与 `Continue(false)` 严格一一配对，`ExitProcess` 不调用 Continue；验收脚本同时核对事件数与 Continue 计数。
- `info` 只记录五类核心事件，`trace` 记录完整事件流，`off` 不创建日志文件。每条 JSONL 日志含 UTC 时间、会话 ID、进程 ID、回调类型、回调线程 ID 和命令线程 ID。
- Debug 与 Release 验收均要求单次回调入队耗时低于 50 ms；该阈值用于发现明显阻塞，不作为产品性能承诺。

## Windows PDB 读取结论（阶段0-5）

- 固定 `Microsoft.DiaSymReader` 2.2.11 与 `Microsoft.DiaSymReader.Native` 17.12.0-beta1.24603.5；前者提供托管接口，后者提供 Windows PDB 原生读取器。本项目将该 Native 版本作为 2023 年安全公告之后的固定基线，但不声称它是 Microsoft 官方确认的最低 CVE 修复版本。
- 只打开调用方明确指定的本地 PDB，不启用注册表 COM 回退或网络符号搜索。先从 PE CodeView 目录读取 GUID、stamp、age，再通过 `ISymUnmanagedReader5.MatchesModule` 验证 PDB 与模块身份。
- 源码行通过 document → method → sequence point 映射为 method token 与方法内 IL offset；隐藏 sequence point 被过滤，同一行的全部候选方法与 offset 都会保留，符号层不自行移动断点行。
- 在 x64 Probe 中读取真实 net40 Windows PDB（MSF 7.00），`FxDbg.Debuggees.ConsoleX86.Program.Main` 的已知输出语句映射为 method token `0x06000001`；当前 Release 样例位于第 41 行、IL offset 130，独立读取该方法 IL 确认 offset 130 是该语句的 `ldstr` 指令。验收脚本按语句内容动态定位行号，避免后续样例扩展造成脆弱测试。
- 对外明确区分 `pdb_missing`、`pdb_mismatch`、`read_failed`。PDB 已加载但指定行没有 sequence point 时返回 `loaded` 与空映射，不冒充读取失败。
- Windows PDB 始终按不可信输入处理：PE/PDB 单文件限制 512 MiB；在分配返回数组前先查询并限制文档、方法、sequence point 数量，分别最多 10 万、100 万、500 万项；托管枚举预算 30 秒。Native 调用仍无法在进程内可靠中断，产品化实现必须依靠 Engine 进程隔离及 Host 超时回收。
- reader 生命周期显式调用 `ISymUnmanagedDispose.Destroy`、释放 COM 引用并关闭文件流。阶段 0 的空 metadata provider 仅足够读取文档、方法和 sequence point；阶段 1 读取局部变量签名时必须换成真实 provider。

详细依据见 [Windows PDB / DiaSymReader 技术验证结论](research/windows-pdb-dia.md)，机器可读状态样例及人工 IL 复核见 [阶段 0-5 验证样例](validation/stage0-5-symbol-status-examples.md)。

## 源码断点与托管调用栈结论（阶段0-6）

- 断点以阶段 0-5 产出的 `mdMethodDef + IL offset` 为唯一绑定输入；目标模块尚未加载时保持 `pending`，模块加载后成功创建并激活 `ICorDebugFunctionBreakpoint` 才进入 `verified`，token 或 offset 无效则进入 `unresolved`。
- x86 Engine Probe 在真实 net40 目标的 `BreakpointTarget` 源码行成功停止。Debug 与 Release 都得到 14 层目标程序集托管栈，逐帧包含方法全名、模块、method token、IL offset、源码文件和行号。
- ICorDebug 原始栈与 Windows PDB 反向映射分层处理：Engine 读取 chain/frame/function/token/IP，Symbols Probe 选择不大于当前 IL IP 的最近非隐藏 sequence point。该近似规则会保留 ICorDebug 的 `MAPPING_EXACT` / `MAPPING_APPROXIMATE` 标记。
- 回调仍只入队；断点创建、栈遍历、符号读取和 Continue 都在命令线程执行。验收报告显示回调线程与命令线程不同，且除 `ExitProcess` 外每个回调恰好一次 Continue。
- 独立使用 Windows SDK x86 CDB + .NET Framework SOS `!clrstack`，对照确认 `BreakpointTarget → StackLevel12 → … → StackLevel01 → Main` 的方法序列与 ICorDebug 输出一致。
- `FxDbg.Breakpoint.Probe` 只是阶段 0 技术探针；其会话代码不会进入 CLI/MCP 产品路径。阶段 1 必须把同一机制收敛到 `FxDbg.Interop`、`FxDbg.Engine` 与 `FxDbg.Host` 的单一逻辑源。

复核记录见 [阶段 0-6 源码断点与调用栈验证](validation/stage0-6-breakpoint-stack.md)。

## 阶段 0 架构结论（阶段0-7）

- 技术验证确认既定 Host + x86/x64 双 Engine、同位数 ICorDebug、Windows PDB 与只读观察优先路线可行。
- 没有结论推翻需求 §16 的既定决策，因此本阶段没有新增反向 ADR。
- Probe 是阶段 0 验证资产，不是产品架构层；阶段 1 必须按计划目录把协议无关模型、ClrDebug 适配、符号、Engine 与 Host 分离。
- AnyCPU 32BitPreferred 启动路由、Named Pipe/JSON-RPC、变量读取、异常与崩溃清理仍是后续实现项，不得把阶段 0 的可行性结论误写成已经交付。

## 阶段 3 的增量架构（2026-09-06）

阶段1/2已经交付上述产品后端。阶段3保持既定决策：变量读取器提供有界按需分页，源码映射由会话固定配置并在模块断点/栈边界双向应用；AppDomain 用会话内单调递增标识区分同名、卸载和重建，域内变量读取器共享停止级10000引用预算。

新增可选 `FxDbg.DapHost`，仅通过 `DebugSessionService` 调用现有 Host/Engine。DAP 层负责 Content-Length stdio、初始化/配置状态、整数引用及编辑器事件，不接触 COM、ClrDebug 或 PDB 读取实现。VS Code/Cursor 扩展只启动 DAP 进程。共享Host保护只读观察取消：调用方立即返回，实际读取在原期限内收尾，原并发名额持有到完成；不会因编辑器取消过时栈刷新而关闭正在运行的会话。

Service/IIS 附加增强（3-4）按本轮用户决定跳过，不改变权限、安全范围或部署环境。没有推翻需求§16的决策。

## Service/IIS增量实现（2026-09-07）

用户随后授权实施3-4a～3-4f及本机测试环境，上节跳过结论仅属于2026-09-06。附加继续复用同一Host/Engine：Host先按目标实际位数路由，对必要的跨身份查询和CLR附加使用有界SeDebugPrivilege作用域并恢复原令牌状态；启动Engine时不继承临时权限调整。Engine保存原目标进程的同步句柄，清理不依赖重新打开可能复用的PID。

IIS业务模块通过实际加载路径定位Windows PDB；校验部署DLL与实际影子副本的身份后，再尝试对应符号文件。符号读取失败按现有调度轮询重试；动态模块、多域卸载和worker回收复用原断点、域身份及会话生命周期。旧worker退出后必须显式附加新PID，不自动转移调试会话。

Service/IIS测试资源由独立清单管理，记录身份、目录ACL、配置基线及进程实例；移除须确认SCM/WAS资源与记录进程均退出。调试协议仍只有stdio/Named Pipe，夹具HTTP仅绑定回环地址；未增加求值、状态修改、附加目标终止或远程调试能力，没有推翻需求§16决策。操作流程与验收依据见[Service/IIS指南](service-iis.md)和[本轮审核](validation/stage3-4-final-review.md)。

## ADR-004：阶段4受限表达式安全边界（2026-09-08，已接受）

### 门禁结论

用户于2026-09-08回复“批准，无现有管理员窗口”，批准本ADR。原条目同时要求目标业务状态不变、CLR函数求值以及超时/取消后可靠继续；对这一原样组合，安全评估不通过。下述调试器内解释方案的设计安全评估通过。4-1实现及真实双架构Debug/Release验收已完成，证据见docs/validation/stage4-1.md。

[Microsoft ICorDebugEval文档](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugeval-interface)说明求值需要Continue才能执行，等待EvalComplete期间可以出现调试事件和死锁。[Abort](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugeval-abort-method)在部分嵌套情况下会失败；[RudeAbort](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugeval2-rudeabort-method)不释放持有的锁，会留下不安全会话。因此不能把白名单、Abort或强杀Engine作为业务状态不变且必能继续的证明。

### 已接受决策

采用**调试器内解释求值**：Interop只读取停止态的基础值、字段、数组元素，共享Core解释器完成计算；由Engine单调度线程执行有界工作，Host统一编排，CLI/MCP/DAP仅适配。目标始终停止，不创建ICorDebugEval、不执行目标方法、不等待EvalComplete、不新增Continue。

这是对4-1“发起Eval并等待完成回调”和目标mscorlib方法调用的显式调整，已获用户确认并同步ROADMAP；不能把原要求当作已实现。原需求文档保持不变，MVP禁止函数求值与状态修改的决策继续有效。

### 允许与禁止清单

- 允许stopped状态下指定线程/帧中的参数、局部变量、this、已可读取的实例/静态字段；null、布尔、字符、字符串、数值字面量；数组/字符串索引；基础数值算术、比较、相等判断及短路布尔逻辑。多维数组按实际维度与下界校验。
- 数值采用文档化的C#基础提升子集，整数检查溢出，除零和类型不匹配明确报告；不声称支持完整C#。不得从截断的显示文本或丢失精度的数值还原参与计算的值。
- 按确切名称与参数类型匹配内建函数：Math.Abs/Min/Max基础数值重载、String.IsNullOrEmpty(string)、String.Equals(string,string)的ordinal语义、String.Concat(string,string)、Array.GetLength(array,dimension)。String/Array.Length为原生长度读取，不执行Getter。这些是解释器固有操作，不是目标mscorlib调用，不通过反射或目标类型名称决定可信度。
- 禁止所有目标方法/构造器/Getter/Setter/ToString、用户运算符与隐式转换、赋值/自增减、目标对象分配、反射、dynamic、委托、LINQ、循环、递归、脚本及文件/网络/进程访问。禁止Array.Sort/Clear/Copy等可写成员；未列入清单的语法或重载默认拒绝。
- 对象仅支持字段观察、null/引用身份比较及既有变量模型显示，不调用自定义格式化；未初始化静态字段返回不可获取，不执行类型初始化器。优化掉、不可获取与null分别处理。

### 资源、失败与只读保证

- 输入最多4096个UTF-16代码单元、1024个语法节点、32层语法/求值嵌套；最多10000解释步骤、1024次字段/元素读取；单个字符串最多32768、累积中间字符串最多65536个代码单元。分配前检查预算，元数据枚举沿用现有上限。
- 默认期限250ms，允许1～1000ms；排队时间计入期限。解析、树遍历、读取边界和内建操作检查期限/取消。语法错误、禁止操作、预算耗尽、类型/索引错误、上下文缺失、过期引用、值不可获取、超时和取消明确分类；错误响应与日志不泄漏值、表达式内容或内部堆栈。
- 输出沿用默认深度1/成员100/字符串256，上限8/1024/32768及分页、循环引用、停止代次失效语义。超出计算长度预算的字符串拒绝求值，不能把显示截断值用于计算。
- 保证范围是调试器不写目标内存、不执行目标代码、不恢复目标线程。原生COM读取无法在进程内强制安全中断；异常卡死沿用Host超时/隔离并明确报告会话不可用，不能保证此类基础设施故障后仍能继续，也不能把强杀Engine当作附加目标保活。外部进程改变共享内存不属于此保证。
- 可接受语法没有死循环；验收改为拒绝循环/任意调用，并使用长/深表达式、预算耗尽、已取消请求和可控慢读取验证超时/取消。真实正常CLR读取条件下必须证明错误后可继续、停止代次不变、Continue计数未增长。原生不可中断故障作为明确限制，不能冒充已解决。

### 后继任务与验收

- 4-2过滤按精确类型、命名空间边界前缀、真实基类链匹配派生类型，不使用求值；旧版first-chance=true且未提供规则保持原行为，显式空规则或关闭恢复仅未处理异常。匹配与内部继续放在调度线程。
- 4-3命中条件支持=N和>=N（N为正整数），逻辑断点实际命中计数，与本ADR表达式AND组合。false仅内部继续；表达式失败、非布尔或超时采用fail-stop，保留本次断点停止并附脱敏诊断，可修改/禁用条件后继续，避免静默忽略。
- 4-4维持“不实施”的决策关闭，不增加HTTP MCP或远程宿主。
- 每项依序实施，运行真实x86/x64 × Debug/Release和受影响CLI/MCP/DAP验收，快速审核只修阻塞项，通过后原地勾选并本地提交。最终按需求、质量、规范完整审核，分析覆盖率（变更生产行目标至少90%），执行包含真实Service/IIS、VS Code及阶段2/1/0的全回归。失败不勾选、不以跳过代替通过。
- 安全证据包括实例/静态字段/数组与副作用计数前后对比，禁止路径调用计数为零，超时/取消后继续和Continue计数。样例快照仅为证据之一，还须审核所有产品路径不存在目标调用/写入。

### 启动前确认

本ADR的执行方式及超时验收口径已获用户确认。首次UAC后的管理员预检证明IIS组件齐全，但后台进程因空列表判断错误退出；修正后用户明确允许在启动前补发一次UAC。2026-09-08 00:48Z管理员后台进程已恢复并确认就绪，随后开始目标。该进程仅执行本仓库验收及独立Service/IIS夹具清理；无人值守期间不再触发UAC、不自动启用缺失Windows功能或重启机器。本地逐项提交已获授权，不再逐次确认，不推送。
