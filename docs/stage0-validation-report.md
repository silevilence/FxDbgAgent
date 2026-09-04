# FxDbg Agent 阶段 0 技术验证报告

> 日期：2026-09-04
> 范围：ROADMAP 阶段 0-1～0-7
> 结论：技术上可行；无需求 §16 决策需要推翻；用户确认本报告前不进入阶段 1。

## 1. 执行摘要

阶段 0 已打通 FxDbg Agent 最关键且风险最高的纵向链路：在独立 x86/x64 进程中通过 ClrDebug 初始化 CLR v4 ICorDebug，启动或附加真实 .NET Framework 4.0 目标，安全地收集回调；读取真实 Windows PDB，把源码行映射到方法 token 与 IL offset；在目标模块加载后绑定源码断点，命中并输出 14 层已符号化托管调用栈。

验证没有发现架构不可行项。发现的限制均可在既定架构内处理，不要求修改需求 §16 的 Host/双 Engine、Named Pipe + JSON-RPC、只读 MVP 或单一逻辑源等决策。

本结论只覆盖调试核心技术风险，不代表阶段 1～4 已实现。位数路由产品化、会话状态机、变量读取、异常策略、Named Pipe、CLI/MCP 和生命周期容错仍按 ROADMAP 实施。

## 2. 验证环境

| 项目 | 实测值 |
|---|---|
| 操作系统 | Windows NT 10.0.26200.0，x64 |
| .NET SDK | 10.0.301（由 `global.json` 固定 feature band） |
| .NET Framework 4.x Release | 533509（目标进程报告 CLR `v4.0.30319`） |
| Windows Debugger | x86 CDB 10.0.26100.7705 |
| 样例 | net40 Console/WinForms × x86/x64；另有 CLR 2、CoreCLR 和原生拒绝路径样例 |
| 符号格式 | Windows PDB，MSF 7.00 |

`net40` 使用 SDK 风格工程及 `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3，因此构建机无需安装 .NET Framework 4.0 targeting pack。

## 3. 逐项结果

| 任务 | 结果 | 主要证据 |
|---|---|---|
| 0-1 解决方案与样例 | 通过 | 一条命令构建四个 FX4 样例；四份 PDB 均为 MSF 7.00；x86/x64 WinForms 都显示主窗口。 |
| 0-2 ClrDebug 初始化 | 通过 | x86 启动与附加均收到 `CreateProcess`；显式使用 `CLRMetaHost.GetRuntime("v4.0.30319")`；Detach 后目标存活。 |
| 0-3 双架构矩阵 | 通过 | x86/x64 各自启动与附加成功；跨位数、原生、CoreCLR、CLR v2 均返回稳定错误码且失败目标存活。 |
| 0-4 回调线程 | 通过 | Process/AppDomain/Assembly/Module/Thread 五类回调齐全；回调线程只入队，命令线程执行 Continue；计数严格配对。 |
| 0-5 Windows PDB | 通过 | 已知输出语句映射为 `Program.Main`、token `0x06000001` 和 IL offset；缺失/不匹配/读取失败三态有独立报告。 |
| 0-6 断点与栈 | 通过 | Debug/Release 源码断点均命中；14 层目标托管栈字段完整；pending/unresolved 可区分；WinDbg/SOS 前 10 层顺序一致。 |

详细的符号状态样例见 [stage0-5-symbol-status-examples.md](validation/stage0-5-symbol-status-examples.md)，断点、调用栈和 WinDbg 对照见 [stage0-6-breakpoint-stack.md](validation/stage0-6-breakpoint-stack.md)。

## 4. 依赖与方案定案

### ClrDebug

- 固定 NuGet `ClrDebug` 0.4.2，MIT；本次 nupkg SHA-256 为 `880276A4D34EAA32EF6FB3598B7464E16C133B1184E9963D59398607BB5EDFBA`。
- x86/x64 Probe 均能调试相同位数的 CLR v4 目标。Framework CLR 必须经 `CLRMetaHost.GetRuntime("v4.0.30319")` 显式获取；不能用随 Probe 自身运行时选择的便捷构造作为证据。
- ClrDebug 是单维护者项目。产品实现只允许 `FxDbg.Interop` 触碰其 COM 包装，以保留未来替换能力；不复制、不修改其生成代码。

### Windows PDB

- 固定 `Microsoft.DiaSymReader` 2.2.11 与 `Microsoft.DiaSymReader.Native` 17.12.0-beta1.24603.5，使用 Native reader 读取经典 Windows PDB，不引入 PortablePdb reader 冒充验证。
- 打开明确的本地 PE/PDB；从 CodeView 读取 GUID/stamp/age，并在读取符号前调用 `MatchesModule`。不启用网络符号搜索或注册表 COM 回退。
- 通过 document → method → sequence point 完成源码行到 IL offset；通过 method token 与“不大于当前 IP 的最近非隐藏 sequence point”完成栈帧反向符号化。
- 阶段 0 的空 metadata provider 只适用于 document/method/sequence point。阶段 1-7 读取局部变量签名时必须提供真实 metadata provider。
- CVE-2023-36796 证明畸形 Windows PDB 是 RCE 面。独立 Native NuGet 没有 Microsoft 公布的最低修复版本范围；17.12.0-beta1.24603.5 是本项目选择的公告后固定基线，不得写成官方最低修复版。

## 5. 可行、不可行与未验证边界

### 已确认可行

- 由同位数独立进程承载 ICorDebug 的技术路径可行，支持后续保持 Host 不加载 `mscordbi.dll` 的架构约束。
- FX4 x86/x64 的启动、附加、回调、Continue、Detach。
- Windows PDB 身份验证、源码正向映射与栈帧反向符号化。
- 模块加载后创建 IL breakpoint、命中和读取 10 层以上托管栈。
- 对跨位数、非托管、CoreCLR 和旧 CLR 目标做前置拒绝且不伤害目标进程。

### 明确不采用或不支持

- `new CorDebug()` 一类按当前进程运行时选择的隐式初始化，不用于 Framework CLR 产品路径。
- SharpDbg 的 CoreCLR 调试后端、Portable PDB-only 路径不用于 FX4 后端。
- CoreCLR、CLR v2、跨位数静默附加均在本项目范围外，必须显式拒绝。
- 阶段 0 没有发现“原定 MVP 能力技术上不可实现”的项目。

### 尚未由阶段 0 证明

- 启动模式 PE + CorFlags 路由，尤其 AnyCPU 32BitPreferred；阶段 0 只验证了固定平台样例与附加前置分类。
- Named Pipe/JSON-RPC、Engine 崩溃隔离、Host 异常退出后的自动 Detach。
- 变量树、泛型/数组/闭包、优化变量边界、异常策略和多会话隔离。
- MCP stdio 协议及外部 Agent 端到端调用。这些工作必须等报告确认后按阶段 1 顺序开展。

## 6. 风险清单更新

| 风险 | 阶段 0 观察 | 后续强制措施 | 剩余等级 |
|---|---|---|---|
| 回调死锁/重复 Continue | 专用回调线程可稳定复现；入队模型与计数配对通过 | 单一调度线程；回调只采集；停止事件所有路径恰好一次 Continue | 高 |
| Windows PDB RCE/挂起 | 损坏 PDB 由 Native reader 返回 COM 错误；Native 调用不可在进程内可靠中断 | 固定版本、本地显式路径、大小/数量限制、Engine 进程隔离、Host 超时回收 | 高 |
| ClrDebug 上游维护 | 0.4.2 功能足够但为单维护者 | 固定版本/哈希/许可证；薄 `FxDbg.Interop`；预留自研 COM interop 替换 | 中高 |
| ICorDebug 对象失效 | ExitProcess 后访问对象实测触发 `CORDBG_E_OBJECT_NEUTERED` | Continue/退出前提取标量；不得跨恢复边界缓存可失效包装对象 | 高 |
| Release 优化影响断点 | 可被优化掉的局部赋值不会稳定命中；有真实副作用的 sequence point 稳定 | 绑定结果支持 `moved/unresolved`；用实际 sequence point；报告最终行偏移 | 中高 |
| 位数/运行时路由错误 | 同位数成功，跨位数和非 FX4 路径可解释拒绝 | 启动读取 PE+CorFlags；附加 IsWow64Process2 回退；按目标实际 runtime 获取 mscordbi | 高 |
| 附加生命周期伤害目标 | 正常 Detach 与所有前置拒绝路径目标均存活 | 附加模式禁止 Terminate；Host/Engine 异常路径集成测试 | 高 |
| 技术探针演变为第二套逻辑 | 阶段 0 为快速验证存在独立 Probe | 阶段 1 重新收敛到 Core/Interop/Engine/Host；CLI/MCP 只调用共享 Host | 中 |

## 7. 架构决策结论

阶段 0 **没有推翻需求 §16 的任何决策**，因此没有新增“反向 ADR”。[architecture.md](architecture.md) 已形成初稿并逐项固化 0-2～0-6 结论。以下原决策继续有效：

1. Host + x86/x64 双 Engine 进程，Host 不加载 `mscordbi.dll`。
2. Engine 与目标严格同位数，内部 Named Pipe + JSON-RPC。
3. CLI、MCP、未来 DAP 共用 Host/Engine 单一逻辑源。
4. MVP 观察优先，不做函数求值、状态修改及其他明确排除项。
5. 阶段 1 开始前仍需用户确认本报告。

## 8. 复现命令

```powershell
dotnet build FxDbg.sln --configuration Debug
./eng/verify-stage0-3.ps1 -Configuration Debug
./eng/verify-stage0-3.ps1 -Configuration Release
./eng/verify-stage0-4.ps1 -Configuration Debug
./eng/verify-stage0-4.ps1 -Configuration Release
./eng/verify-stage0-6.ps1 -Configuration Debug
./eng/verify-stage0-6.ps1 -Configuration Release
```

0-3 脚本覆盖 ClrDebug 双架构启动/附加矩阵和拒绝路径；0-4 脚本覆盖回调线程纪律与日志级别；0-6 脚本递进覆盖构建、Windows PDB、源码断点、14 层栈、绑定失败状态及 WinDbg/SOS 对照。
