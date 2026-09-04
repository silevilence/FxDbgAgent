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
