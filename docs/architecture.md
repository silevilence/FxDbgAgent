# FxDbg Agent 架构与技术验证记录

> 状态：阶段 0 技术验证进行中。阶段0-7将汇总本文件并固化最终结论。

## 已定架构约束

- `FxDbg.McpHost` 负责统一入口、会话管理与目标架构路由，不加载 `mscordbi.dll`。
- `FxDbg.Engine.x86.exe` 与 `FxDbg.Engine.x64.exe` 分进程承载 ICorDebug，Engine 与目标进程位数必须一致。
- Engine 与 Host 使用 Named Pipe + JSON-RPC；CLI、MCP 与未来 DAP 共享同一 Host/Engine 逻辑。
- MVP 只观察目标状态，不执行函数求值、属性 Getter、`ToString()` 或变量修改。

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
