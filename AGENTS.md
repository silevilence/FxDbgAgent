# AGENTS.md

本文件为在本仓库工作的 AI 编码助手与人类开发者提供事实与约束。仓库已完成阶段 0 技术验证和阶段 1 产品内核；阶段 2 MCP 接入及 MVP 已完成最终 Debug/Release 验收。技术探针只作为可行性证据，不得直接演变成第二套产品调试逻辑。

## 项目状态（2026-09）

- 阶段 0-1～0-6 已完成真实 Windows 验证；阶段 0-7 报告见 `docs/stage0-validation-report.md`，并已于 2026-09-04 获用户确认。
- 可行性结论：**技术上可行**。ICorDebug 双架构启动/附加、回调线程纪律、Windows PDB、源码断点和 14 层托管栈均已实测。
- 阶段 1 已完成，依据为 `docs/validation/stage1-final-review.md`。阶段 2 的实现、独立 Agent 调用证据与完整回归见 `docs/validation/stage2-mvp.md`；按 `ROADMAP.md` 原地勾选保留任务位置。
- 阶段3-1/3-2/3-3/3-5已完成；阶段3-5提供可选DAP入口与VS Code/Cursor扩展。完整Debug/Release回归、真实VS Code证据和独立双轴审核见 `docs/validation/stage3-usability.md`、`docs/validation/stage3-final-review.md`。阶段3-4在该轮按用户决定跳过；2026-09-07按用户要求完成本机Service/IIS测试环境准备，见 `docs/service-iis-environment.md`、`docs/validation/stage3-4-environment.md`，附加增强与阶段3-4整体验收仍未完成。

## 项目是什么

FxDbg Agent：运行于 Windows 的托管代码调试器，使外部 AI Agent（Cursor、Claude Code、Copilot）通过 **MCP over stdio** 调试 .NET Framework 4.x（CLR v4.0.30319）应用。定位为只读观察优先：**MVP 禁止函数求值与状态修改**，不追求复制 Visual Studio 完整体验。

## 架构与已定决策（需求 §5、§16，未经评审不得推翻）

- 进程模型：`FxDbg.McpHost`（统一入口、会话管理、位数判断）+ `FxDbg.Engine.x86.exe` / `FxDbg.Engine.x64.exe` 两个独立 Engine 进程；内部经 **Named Pipe + JSON-RPC** 通信。
- **Host 绝不加载 `mscordbi.dll`**；ICorDebug 只存在于 Engine 内，且 Engine 位数必须与目标进程完全一致。位数不符 → 报错退出，禁止任何「静默跨位数」。
- 架构探测：启动模式按 PE + CorFlags（必须正确处理 AnyCPU 32BitPreferred）；附加模式用 `IsWow64Process2`，回退 `IsWow64Process`。
- 技术栈：C#；ICorDebug 经 **ClrDebug**（lordmilko，MIT，NuGet 0.4.2）包装，作为固定版本第三方依赖——**不修改、不复制其生成代码**；Windows PDB 基于 DIA（Microsoft.DiaSymReader 系包）；SharpDbg 仅作会话模型 / 变量树 / DAP 参考，不依赖其调试后端（已验证：SharpDbg 自身为 CoreCLR-only——基于 ICorDebugSharp + DbgShim 的 RegisterForRuntimeStartup，仅读 portable PDB，net10.0 AnyCPU 无 x86 构建，无任何 ICLRMetaHost/FX 运行时发现路径，不能调试 FX 目标）。
- 首期不做：函数求值、变量修改、Edit and Continue、Set Next Statement、条件/数据断点、混合模式调试、Dump 分析、多进程联调、远程调试、HTTP MCP、VS 扩展。
- 阶段 0 定案：固定 `ClrDebug` 0.4.2；Windows PDB 固定 `Microsoft.DiaSymReader` 2.2.11 与 `Microsoft.DiaSymReader.Native` 17.12.0-beta1.24603.5。独立 Native 包没有 Microsoft 公布的 CVE 最低修复版，所选版本是公告后的项目安全基线，并非官方最低修复下限。
- MCP 固定官方 `ModelContextProtocol` / `ModelContextProtocol.Core` 2.2.0，协议 2025-11-25，仅 stdio 和 13 个 `debug_` 工具；字段、默认上限和错误恢复见 `docs/mcp-tools.md`，安装与调用流程见 `docs/agent-skill.md`。

## 计划目录结构（需求 §15）

```text
FxDbg.sln
├─ src/
│  ├─ FxDbg.Core/            # 协议无关领域模型：会话状态机、断点/线程/帧/变量/异常模型、事件、错误码
│  ├─ FxDbg.Interop/         # 对 ClrDebug 的薄适配层（仅此层可触碰 COM 包装）
│  ├─ FxDbg.Symbols.Windows/ # Windows PDB 读取与源码行→IL Offset 映射
│  ├─ FxDbg.Engine/          # ICorDebug 承载进程（发布为 x86/x64 两个 exe）
│  ├─ FxDbg.Engine.Protocol/ # Engine↔Host 协议与帧格式
│  ├─ FxDbg.Host/            # 会话编排、位数路由、CLI 与 MCP 共享层
│  ├─ FxDbg.McpHost/         # MCP stdio 服务
│  ├─ FxDbg.Cli/             # 开发测试入口（不得承载独立调试逻辑）
│  └─ FxDbg.DapHost/         # 可选，阶段 3
├─ tests/
│  ├─ FxDbg.UnitTests/
│  ├─ FxDbg.IntegrationTests/
│  └─ Debuggees/             # Fx40.Console/WinForms × x86/x64 样例
└─ docs/
   ├─ architecture.md
   ├─ mcp-tools.md
   └─ agent-skill.md
```

## 开发约束（行为不变量，实现时必须遵守）

1. **回调纪律**：ICorDebug 回调只入队、不执行耗时工作；所有调试命令经单一调度线程串行执行；每次停止状态必须恰好一次 Continue（严格计数配对）。这是最容易产生死锁与「重复 Continue」的地方，改动执行控制代码前先复习该不变量。
2. **变量读取**：默认不调用属性 Getter、`ToString()` 或任何用户代码；区分「无法获取 / 已优化掉 / 空值」；循环引用返回引用标识，禁止递归展开；遵守深度、成员数、字符串长度上限。
3. **断点**：状态机仅 `pending / verified / moved / unresolved`；模块加载或 PDB 就绪后自动重绑定；行偏移必须明确告知调用方。
4. **异常**：默认只报告未处理异常；first-chance 停止默认关闭；异常内容不得来自目标自定义格式化代码。
5. **MCP**：stdout 仅协议内容，日志一律 stderr 或文件；会话内调用显式携带 `sessionId`；所有调用有超时与取消；首期不向 Agent 暴露原始 ICorDebug 对象或 COM 方法。
6. **生命周期**：附加模式不得终止目标进程；Host 异常退出时 Engine 须尝试安全 Detach；Engine 崩溃不得导致 Host 崩溃。
7. **单一逻辑源**：CLI、MCP（及未来 DAP）复用同一 FxDbg.Host 与 Engine，禁止第二套调试逻辑。
8. **安全**：首期仅本机调试，不监听网络端口；日志默认脱敏（敏感变量值不入日志，可配置禁用值日志）。

## 构建与测试

- MVP 一键验收：`./eng/verify-stage2.ps1`，默认 Debug/Release，支持 `-Configuration Debug|Release`。包括真实 MCP、技能安装/独立 Agent 证据检查和 `eng/verify-stage1.ps1`；记录源码哈希、超时、日志及自有进程退出，任何失败不得标记通过。
- 阶段3本轮一键验收：`./eng/verify-stage3.ps1 -NodePath <node.exe> -CodePath <Code.exe>`，默认 Debug/Release，包含3-1/2/3/5、真实VS Code、VSIX打包及阶段2/1全回归；需要Node/npm、VS Code和.NET 8/10运行时。3-4保持跳过。DAP发布：`./eng/publish-dap.ps1`，扩展打包：`./eng/package-extension.ps1`，配置见 `docs/dap.md`。
- 发布 MCP：`./eng/publish-mcp.ps1 -Configuration Release`，入口为 `dotnet <发布目录>/fxdbg-mcp.dll`；必须携带相邻 `engines/` 的完整双架构依赖，不依赖当前目录。
- SDK 由 `global.json` 固定为 .NET SDK 10.0.301（允许同一 feature band 的最新补丁）。
- `net40` 使用 SDK 风格项目；`Directory.Build.targets` 固定引用 `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3，因此构建机无需预装 .NET Framework 4.0 targeting pack。
- 构建全部阶段 0 样例：`dotnet build FxDbg.sln --configuration Debug`。
- 阶段 0 的验收脚本按依赖递进：`./eng/verify-stage0-1.ps1`、`verify-stage0-2.ps1`、`verify-stage0-3.ps1`、`verify-stage0-4.ps1`、`verify-stage0-5.ps1`、`verify-stage0-6.ps1`。每个脚本接受 `-Configuration Debug|Release`；0-6 会覆盖此前构建/PDB 验证并额外执行真实断点、14 层栈、pending/unresolved 和 x86 CDB/SOS 对照。
- 完整阶段 0 回归至少运行：`./eng/verify-stage0-3.ps1 -Configuration Debug`、`./eng/verify-stage0-3.ps1 -Configuration Release`、`./eng/verify-stage0-4.ps1 -Configuration Debug`、`./eng/verify-stage0-4.ps1 -Configuration Release`、`./eng/verify-stage0-6.ps1 -Configuration Debug`、`./eng/verify-stage0-6.ps1 -Configuration Release`。0-3 覆盖双架构启动/附加和拒绝路径；0-4 覆盖回调线程纪律与日志级别；0-6 覆盖构建、PDB、断点及栈。
- 注意：net40 默认产出 **Windows PDB**（MSF 7.00 文件头）。验证符号路径必须使用 Windows PDB，禁止拿 portable PDB 当证据。
- 本项目 Windows-only：ICorDebug、Named Pipe、DIA 均需在 Windows 上验证；调试样例须为真实 .NET Framework 4.x 进程。
- 测试要求：会话状态机、断点状态迁移、变量读取边界、异常报告、生命周期清理均有自动化覆盖；集成测试基于 `tests/Debuggees/` 样例。

## 文档纪律

- 推翻需求 §16 决策之前先写 `docs/architecture.md` 的 ADR 记录，并与用户确认；不要「顺手修正」需求文档本身。
- ROADMAP.md 的任务条目按 roadmap 流程维护；不得跳过「阶段 0 技术验证」直接开发 MCP。
- 本文件是权威事实来源的衍生物：与需求文档冲突时先确认，再修改本文件。

## 已知坑

- **CVE-2023-36796**：处理畸形 Windows PDB 存在 RCE 风险。必须固定已修复版本的 Microsoft.DiaSymReader 系包，并假定 PDB 为不可信输入。
- **AnyCPU 32BitPreferred**：在 64 位系统上优先以 x86 运行；普通 AnyCPU 则以 x64 运行。不能只检查 32BITREQUIRED，`auto` 探测必须同时覆盖 32BITPREFERRED。
- **就地升级**：v4.0.30319 实为已安装的最新的 4.x CLR（通常 4.8）；Engine 必须按目标进程实际运行时版本加载匹配的 mscordbi，不能假定 4.0。
- **单一调试器**：操作系统层面一个进程只允许一个调试器附加，与「一个目标进程一个 FxDbg 会话」的需求一致——附加冲突必须返回明确错误。
- **ClrDebug 是单维护者项目**：固定版本并评审许可证；如上游失修，`FxDbg.Interop` 薄适配层是替换为自研 COM interop 的隔离点（这正是该层存在的意义）。
