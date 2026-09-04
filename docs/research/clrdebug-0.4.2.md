# ClrDebug 0.4.2：CLR v4.0.30319 启动/附加验证笔记

> 调研日期：2026-09-04
> 范围：ROADMAP 阶段0-2；只采用 NuGet 包元数据、`lordmilko/ClrDebug` 上游源码/许可证和 Microsoft 官方文档/IDL。

## 结论

ClrDebug 0.4.2 能覆盖阶段0-2 所需的全部薄包装：显式取得 CLR `v4.0.30319` 的 `ICorDebug`、初始化、注册托管回调、启动进程、附加进程、`Continue`、`Stop`、`Detach` 与关闭调试服务。建议阶段0探针显式使用 `CLRMetaHost.GetRuntime("v4.0.30319")`，不要依赖 `new CorDebug()` 的隐式 runtime 选择；后者在 0.4.2 中使用当前 Engine 进程的 `RuntimeEnvironment.GetSystemVersion()`。[上游实现](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CorDebug.cs#L19-L40)

阶段0的 x86 探针应自身以 x86 运行，并只启动/附加 x86 .NET Framework 目标。ClrDebug 包不携带 `mscoree.dll`、`mscordbi.dll` 或其他 RID 原生资产；它从 Windows 的 `mscoree.dll` 调用 `CLRCreateInstance`，再让选定的已安装 CLR 提供调试接口。[ClrDebug 激活代码](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CLRCreateInstance.cs#L12-L55) [Microsoft `ICLRRuntimeInfo::GetInterface`](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/hosting/iclrruntimeinfo-getinterface-method)

## 包、许可证与维护证据

| 项目 | 已核验事实 | 判断 |
|---|---|---|
| 固定版本 | NuGet `ClrDebug` 0.4.2，2026-08-03 发布；包元数据的 repository commit 是 `9628778ff761b2e466ca3199392cbd3de6de5bc5`，该提交标题即 `ClrDebug 0.4.2`。[NuGet](https://www.nuget.org/packages/ClrDebug/0.4.2) [提交](https://github.com/lordmilko/ClrDebug/commit/9628778ff761b2e466ca3199392cbd3de6de5bc5) | 必须固定 `0.4.2`，不能用浮动版本。 |
| 目标框架 | 包内实际程序集为 `netstandard2.0` 与 `net8.0`，均无 NuGet 依赖。[NuGet frameworks](https://www.nuget.org/packages/ClrDebug/0.4.2#supportedframeworks-body-tab) [项目文件](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/ClrDebug.csproj#L6-L18) | `net8.0-windows` Engine 会采用 `net8.0` 资产；包不能直接供 `net40` 项目引用，但 Engine 本就不应运行在目标的 net40 内。 |
| 许可证 | 包内 `LICENSE` 是 MIT License，Copyright © 2022 lordmilko。[许可证](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/LICENSE) | 允许使用、复制、修改和分发，但分发时须保留版权及许可声明；项目继续记录第三方告知即可。 |
| 原生内容 | 0.4.2 nupkg 只有两个 TFM 的 `ClrDebug.dll`/XML、LICENSE 与元数据，无 native/RID 文件；`CorDebug` 的 Windows 快捷激活显式从 `mscoree.dll` 取入口。[激活实现](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CLRCreateInstance.cs#L17-L55) | 运行机必须安装相应 .NET Framework 4.x runtime；调试组件不是由该包私带。 |
| 维护状态 | 0.4.0、0.4.1、0.4.2 分别于 2026-05-23、06-23、08-03 发布；0.4.2 源码提交后，上游 master 在 2026-08-08 仍有提交。[NuGet versions](https://www.nuget.org/packages/ClrDebug/0.4.2#versions-body-tab) [上游提交历史](https://github.com/lordmilko/ClrDebug/commits/master/) | 调研时仍活跃，但 NuGet 仅列 `lordmilko` 一个 owner，继续按“单维护者依赖”管理。 |
| 可追溯性缺口 | 0.4.2 包的 release-notes URL 指向 `releases/tag/v0.4.2`，但调研时上游远端标签只到 `v0.4.0`；0.4.2 可由包内 commit 精确追溯。[NuGet release notes](https://www.nuget.org/packages/ClrDebug/0.4.2#releasenotes-body-tab) [上游 tags](https://github.com/lordmilko/ClrDebug/tags) | 不阻断验证，但应同时保存包锁定信息/哈希与 repository commit，避免只依赖不存在的 tag。 |

ClrDebug 明示其目标是准确映射原生 API，并警告每个版本都可能有破坏性变更，因此固定版本是正确选择。[上游 README](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/README.md#methods--properties)

## 精确 API 映射

| 目的 | ClrDebug 0.4.2 API | 对应 Microsoft API / 关键语义 |
|---|---|---|
| 取得 MetaHost | `new CLRMetaHost()` | `CLRCreateInstance(CLSID_CLRMetaHost, IID_ICLRMetaHost, ...)`。[Microsoft](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/hosting/clrcreateinstance-function) |
| 选择 FX4 runtime | `metaHost.GetRuntime("v4.0.30319")` | `ICLRMetaHost::GetRuntime`；版本格式为 `vA.B[.X]`。[Microsoft](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/hosting/iclrmetahost-getruntime-method) [ClrDebug](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CLRMetaHost.cs#L27-L35) |
| 取得 ICorDebug | `runtime.GetInterface().CorDebug` | `ICLRRuntimeInfo::GetInterface(CLSID_CLRDebuggingLegacy, IID_ICorDebug)`；该调用加载但不初始化 CLR。[Microsoft](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/hosting/iclrruntimeinfo-getinterface-method) [ClrDebug](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CLRRuntimeInfo.cs#L35-L40) |
| 初始化 | `corDebug.Initialize()` | 必须在其他 `ICorDebug` 方法前调用。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-initialize-method) [ClrDebug](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Managed/Cordb/CorDebug.cs#L34-L54) |
| 注册回调 | `corDebug.SetManagedHandler(callback)` | 创建期注册 `ICorDebugManagedCallback`；ClrDebug 的 `CorDebugManagedCallback` 同时实现 callback 1–4。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-setmanagedhandler-method) [ClrDebug](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Managed/Cordb/Callbacks/CorDebugManagedCallback.cs#L9-L26) |
| 启动 | `corDebug.CreateProcess(commandLine, ...)` | `ICorDebug::CreateProcess`；纯托管调试不要设置 Win32 `DEBUG_PROCESS` / `DEBUG_ONLY_THIS_PROCESS`。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-createprocess-method) [ClrDebug 便捷重载](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CorDebug.cs#L44-L130) |
| 附加 | `corDebug.DebugActiveProcess(pid, false)` | `false` 表示不充当 Win32 debugger，只要托管回调。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-debugactiveprocess-method) [ClrDebug](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Managed/Cordb/CorDebug.cs#L247-L290) |
| Process 回调 | `callback.OnCreateProcess` | 包装 `ICorDebugManagedCallback::CreateProcess(ICorDebugProcess*)`，启动或首次附加时触发。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugmanagedcallback-createprocess-method) [ClrDebug](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Managed/Cordb/Callbacks/CorDebugManagedCallback.cs#L58-L68) |
| 恢复 | `eventArgs.Controller.Continue(false)` | 普通托管事件必须用 `false`；`true` 只用于 out-of-band 非托管事件。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-continue-method) |
| 停止/分离 | `process.Stop(0)`、`process.Detach()` | `Stop` 参数目前忽略且同步等待；`Detach` 后目标继续运行、对象失效且不再回调。[Stop](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-stop-method) [Detach](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-detach-method) |
| 关闭服务 | `corDebug.Terminate()` | 关闭 debugger service，不是杀目标；全部被调进程退出/分离后调用。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-terminate-method) |

不要把 `ICorDebugController::Terminate(exitCode)` 与 `ICorDebug::Terminate()` 混淆：前者终止 debuggee，后者关闭调试服务。[Controller Terminate](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-terminate-method) [ICorDebug Terminate](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-terminate-method)

## 阶段0最小实现形状

### 1. 显式初始化 CLR v4.0.30319

```csharp
using ClrDebug;

const string Fx4Runtime = "v4.0.30319";

// 入口线程必须为 MTA，例如 Main 上标 [MTAThread]。
var metaHost = new CLRMetaHost();
var runtime = metaHost.GetRuntime(Fx4Runtime);
var corDebug = runtime.GetInterface().CorDebug;
corDebug.Initialize();
```

ClrDebug 0.4.2 在 Windows 快捷构造器中主动拒绝 STA，因为托管回调会从其他线程进入，而经典 COM RCW 的 apartment marshalling 会破坏这一使用方式。[上游 MTA 检查及解释](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CorDebug.cs#L9-L39)

`new CorDebug()` 虽然会自动完成激活与 `Initialize()`，但它通过 `RuntimeEnvironment.GetSystemVersion()` 选取“当前 Engine runtime”；阶段0要求显式证明 `v4.0.30319`，故不应使用这个隐式构造器。[默认 runtime 选择](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CLRMetaHost.cs#L18-L35)

### 2. 最小 Process 回调桥

```csharp
var processSeen = new ManualResetEventSlim(false);
var exitSeen = new ManualResetEventSlim(false);
CorDebugProcess? callbackProcess = null;

// callback 必须作为字段或局部强引用存活到会话结束。
var callback = new CorDebugManagedCallback();
callback.OnCreateProcess += (_, e) =>
{
    callbackProcess = e.Process;
};
callback.OnExitProcess += (_, _) => exitSeen.Set();
callback.OnAnyEvent += (_, e) =>
{
    // 阶段0 smoke probe 可直接续行；正式 Engine 必须只入队，交给单一调度线程配对 Continue。
    if (e.Kind != CorDebugManagedCallbackKind.ExitProcess)
        e.Controller.Continue(false);

    // 在 Process callback 的 Continue 已发出后再通知等待方。
    if (e.Kind == CorDebugManagedCallbackKind.CreateProcess)
        processSeen.Set();
};

corDebug.SetManagedHandler(callback);
```

ClrDebug 会先触发专用事件，再触发 `OnAnyEvent`；它不会依据 `CorDebugManagedCallbackEventArgs.Continue` 自动调用 `Continue`，该属性只是调用方的便利标志。[事件派发顺序](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Managed/Cordb/Callbacks/CorDebugManagedCallback.cs#L307-L326) [Continue 属性](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Managed/EventArgs/CorDebugManagedCallback/CorDebugManagedCallbackEventArgs.cs#L35-L43)

Microsoft 的 callback 合同是：所有托管 callback 串行、在同一 callback 线程、进程处于 synchronized 状态；除 `ExitProcess` 外，每个已分派 callback 必须恰好配对一次 `Continue(false)`，否则目标保持停止且后续 callback 不再分派。[callback 接口](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugmanagedcallback-interface) [ExitProcess](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugmanagedcallback-exitprocess-method)

### 3. 启动 x86 FX4 目标

```csharp
CorDebugProcess launched = corDebug.CreateProcess(
    $"\"{targetExe}\" --probe",
    bInheritHandles: false,
    lpCurrentDirectory: Path.GetDirectoryName(targetExe));

if (!processSeen.Wait(TimeSpan.FromSeconds(15)))
    throw new TimeoutException("No CreateProcess callback was received.");

if (callbackProcess is null || callbackProcess.Id != launched.Id)
    throw new InvalidOperationException("CreateProcess callback did not match the launched process.");
```

`CreateProcess` 的 ClrDebug 便捷重载会初始化 `STARTUPINFOW.cb`，返回 `CorDebugProcess`，并关闭 Win32 `PROCESS_INFORMATION` 中的 process/thread handle；调用方不需要重复关闭这些两个 handle。[便捷重载实现](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/Extensions.CorDebug.cs#L66-L130)

若 smoke debuggee 自然退出，应等待 `OnExitProcess`，然后调用 `corDebug.Terminate()`。Microsoft 要求在释放/关闭 `ICorDebug` 前等待所有被调进程的 `ExitProcess`。[ICorDebug 接口](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface)

### 4. 附加已有 x86 FX4 目标

```csharp
CorDebugProcess attached = corDebug.DebugActiveProcess(pid, win32Attach: false);

if (!processSeen.Wait(TimeSpan.FromSeconds(15)))
    throw new TimeoutException("No CreateProcess callback was received.");

if (callbackProcess is null || callbackProcess.Id != attached.Id)
    throw new InvalidOperationException("CreateProcess callback did not match the attached process.");

// smoke 验证完毕：同步停住，再分离；Detach 会让目标继续运行。
attached.Stop(0);
attached.Detach();
corDebug.Terminate();
```

附加到任意 PID 前，产品实现还应先通过目标进程实际已加载的 runtime/架构作选择；Microsoft 提供 `ICLRMetaHost::EnumerateLoadedRuntimes(processHandle)` 枚举目标中的 CLR。[Microsoft](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/hosting/iclrmetahost-enumerateloadedruntimes-method)

## 安全清理次序

### 目标自然退出

1. 普通 callback 均调用一次 `Continue(false)`。
2. 收到 `OnExitProcess`；该事件不可再 `Continue`。
3. 所有被调进程均退出后调用 `corDebug.Terminate()`。
4. 释放强引用，让 ClrDebug 包装对象及底层 COM 对象由运行时回收。ClrDebug 的通用 `ComObject<T>` 不实现 `IDisposable`。[上游类型](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/ClrDebug/Extensions/ComObject.cs)

### 附加会话结束但目标必须存活

1. 确保当前没有未完成的 callback 命令；必要时 `process.Stop(0)` 同步停住目标。
2. `process.Detach()`；目标恢复运行，既有 process/app-domain 调试接口失效且不再产生 callback。
3. `corDebug.Terminate()`。
4. **不得**调用 `process.Terminate(...)`。

### 仅对由调试器启动的目标执行强制终止

1. `process.Stop(0)`。
2. `process.Terminate(exitCode)`。
3. 如果终止时目标处于停止状态，调用一次 `process.Continue(false)`，否则收不到退出确认。
4. 等待 `OnExitProcess`，然后 `corDebug.Terminate()`。[Microsoft Controller Terminate](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-terminate-method)

`Stop`/callback/`Continue` 共用内部停止计数：每次 `Stop` 或分派一个 callback 加一，每次 `Continue` 减一，计数归零才恢复运行。因此“每次停止恰好一次 Continue”不是风格要求，而是 API 正确性条件。[Microsoft Stop](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-stop-method) [Microsoft Continue](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-continue-method)

## 阶段0-2验收建议

- probe 进程固定 `win-x86`/`PlatformTarget=x86`，入口标记 `[MTAThread]`。
- 日志/JSON 同时记录：操作模式、返回的 PID、`OnCreateProcess` PID、callback 托管线程 ID、请求 runtime `v4.0.30319`、probe 架构、异常（应为 null）。
- launch 与 attach 各设独立超时；超时后仍执行 Detach/Terminate 清理。
- attach 验证结束后检查目标进程仍存活。
- 不把 `CreateProcess`/`DebugActiveProcess` 同步返回视为 callback 验收；必须实际观察 `OnCreateProcess`。
- 将 callback 对象保持强引用到 Detach/ExitProcess 与 `corDebug.Terminate()` 完成之后。

## 主要一手资料

- [ClrDebug 0.4.2 NuGet 页面](https://www.nuget.org/packages/ClrDebug/0.4.2)
- [ClrDebug 0.4.2 对应源码提交](https://github.com/lordmilko/ClrDebug/tree/9628778ff761b2e466ca3199392cbd3de6de5bc5)
- [ClrDebug README：ICorDebug 入门及包装约定](https://github.com/lordmilko/ClrDebug/blob/9628778ff761b2e466ca3199392cbd3de6de5bc5/README.md#icordebug)
- [Microsoft `metahost.idl`](https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/metahost.idl)
- [Microsoft `cordebug.idl`](https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/cordebug.idl)
- [Microsoft ICorDebug 文档索引](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface)
