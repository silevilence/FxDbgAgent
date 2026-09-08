# FxDbg Agent

面向 AI Agent 的 Windows .NET Framework 4.x 本机调试器：外部 AI Agent 通过 **MCP over stdio** 使用 15 个 `debug_` 工具，对本地 .NET Framework 4.x（CLR v4.0.30319）进程进行**只读优先**的源代码级调试；可选 DAP 接入 VS Code，CLI 供开发验证。只在本机工作，不监听网络端口。

## 它能做什么

- **启动与附加**：任意 Desktop CLR 进程（Console、WinForms、Windows Service、完整 IIS `w3wp`），x86/x64 自动路由（含 AnyCPU 32BitPreferred 判定），跨位数附加报错拒绝。
- **源码断点**：基于 Windows PDB（DIA），状态机 `pending / verified / moved / unresolved`，模块或 PDB 就绪后自动重绑定；支持命中次数与受限表达式条件（阶段 4-3）。
- **观察**：托管线程枚举、14+ 层调用栈、参数/局部/字段变量按需分页（深度/成员数/字符串长度上限）、循环引用标识，区分「空值 / 不可获取 / 已优化」。
- **受限只读求值**：`debug_evaluate` 仅解释 ADR-004 白名单内的 C# 子集，默认 250 ms 预算，不执行目标函数、Getter、ToString 或任何修改。
- **异常**：未处理异常报告（内容只读原始 `_message` 字段）；first-chance 类型过滤（精确/命名空间/派生，阶段 4-2，默认关闭）。
- **生产环境**：源码路径映射（构建机↔本地）、多 AppDomain 识别与筛选、Windows Service/IIS 影子复制符号定位与回收后重附加。
- **安全分离**：detach 后目标继续运行；附加目标禁止 terminate；Engine 崩溃不拖垮 Host；日志默认脱敏（敏感值不入日志）。

## 环境要求

- Windows（x64），调试器与目标同机；目标为 .NET Framework 4.x Desktop CLR 进程，PDB 与可执行文件匹配。
- 构建/发布：.NET SDK 10.0.301（`global.json` 固定，同 feature band 最新补丁）。
- 运行已发布的 MCP 服务：.NET 10 运行时；无需 IDE。
- 安装技能：Node.js/npm（`npx`）。
- Service/IIS 跨身份附加：管理员令牌（`SeDebugPrivilege` 由 Host 按需启用并恢复，不自动弹 UAC）。
- 可选 VS Code 扩展：Node/npm + 固定 `@vscode/vsce` 3.6.2 打包。

## 快速开始

**直接使用（推荐）**：从 [GitHub Releases](https://github.com/silevilence/FxDbgAgent/releases) 下载 `fxdbg-<版本>.zip`，解压后按 [docs/skill-mcp-install.md](docs/skill-mcp-install.md) 依次完成——检查包内文件 → 配置 MCP 客户端 → 安装技能 → 验证（`tools/list` 应列出 15 个工具）。无需 IDE 与源码。

**从源码构建（开发者）**：

1. **发布 MCP 服务**（源码根目录）：

   ```powershell
   ./eng/publish-mcp.ps1 -Configuration Release
   ```

   产物为 `artifacts/mcp/Release/`：`fxdbg-mcp.dll`、`engines/`（x86/x64 Engine 与 ClrDebug/DIA 依赖）、`engine-manifest.json`、`skills/fxdbg-agent/`。**部署时复制整个目录**。

2. **配置 MCP 客户端**（路径替换为本机部署位置）：

   ```json
   {
     "mcpServers": {
       "fxdbg": {
         "command": "dotnet",
         "args": ["C:/tools/FxDbg/fxdbg-mcp.dll", "--log-level", "info", "--value-logs", "off"],
         "env": {"DOTNET_NOLOGO": "1"}
       }
     }
   }
   ```

3. **安装技能**（在使用技能的 AI 客户端项目目录）：

   ```powershell
   npx --yes skills@1.5.23 add <发布包或源码目录> --skill fxdbg-agent --agent codex --yes
   ```

   项目级安装到 `.agents/skills/fxdbg-agent/`，技能自带工作流与契约引用，安装后不依赖该目录。

4. **开始调试**：客户端 `initialize` 后 `tools/list` 应列出 15 个工具；随后 `debug_launch(stopAtEntry=true)` → `debug_set_breakpoint` → `debug_continue` → `debug_stack`/`debug_variables` → `debug_detach`。完整调用流程见 [docs/agent-skill.md](docs/agent-skill.md)，自动安装全流程见 [docs/skill-mcp-install.md](docs/skill-mcp-install.md)。

## CLI 快速验证（开发）

```powershell
dotnet build FxDbg.sln --configuration Debug
$cli = './src/FxDbg.Cli/bin/Debug/net10.0-windows/fxdbg.exe'
& $cli launch --exe 'C:/samples/app.exe' --arch auto --stop-at-entry
```

CLI 与 MCP 共享同一 Host/Engine；完整命令与 JSON 输出见 [docs/cli.md](docs/cli.md)。

## 构建与测试

- 构建全部：`dotnet build FxDbg.sln --configuration Debug`（Windows；`net40` 目标无需预装 targeting pack）。
- 单元测试：`dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj -c Debug --no-build --no-restore`（构建后执行）。
- 一键验收（需要真实 Windows 环境，缺失或未运行不构成通过）：
  - MVP：`./eng/verify-stage2.ps1`（默认 Debug/Release，真实 MCP、技能安装与独立 Agent 证据）。
  - 阶段 3/4 全量：管理员 64 位 PowerShell `./eng/verify-all.ps1 -NodePath <node.exe> -CodePath <Code.exe>`，覆盖阶段 4-1/2/3、3-1/2/3/4/5、真实 VS Code 及阶段 2/1/0 回归，含 IIS/SCM/CDB 环境要求。
  - 专项：`./eng/verify-stage3-4.ps1`（Service/IIS）、`./eng/verify-stage4-1.ps1`、`verify-stage4-2.ps1`、`verify-stage4-3.ps1`。
- 项目为 Windows-only：ICorDebug、Named Pipe、DIA 均需真实 Windows 验证；符号验证必须使用 Windows PDB（MSF 7.00），禁止用 portable PDB 冒充。

## 项目结构

```text
FxDbg.sln
├─ src/
│  ├─ FxDbg.Core/            # 协议无关领域模型：会话状态机、断点/线程/帧/变量/异常、错误码
│  ├─ FxDbg.Interop/         # ClrDebug 薄适配层（唯一触碰 COM 包装的层）
│  ├─ FxDbg.Symbols.Windows/ # Windows PDB 读取与源码行→IL Offset 映射
│  ├─ FxDbg.Engine/          # ICorDebug 承载进程（x86/x64 两个 exe）
│  ├─ FxDbg.Engine.Protocol/ # Engine↔Host Named Pipe JSON-RPC 协议
│  ├─ FxDbg.Host/            # 会话编排、位数路由（CLI/MCP/DAP 共享）
│  ├─ FxDbg.McpHost/         # MCP stdio 服务
│  ├─ FxDbg.DapHost/         # 可选 DAP over stdio（阶段 3）
│  ├─ FxDbg.Cli/             # 开发测试入口（不承载独立调试逻辑）
│  └─ Shared/                # 架构探测、SeDebugPrivilege 等宿主辅助
├─ tests/                    # 单元/主机/MCP/DAP 集成测试与 Debuggees 样例
├─ skills/fxdbg-agent/       # 随包技能：SKILL.md + references/
├─ extensions/fxdbg/         # VS Code / Cursor 共用调试扩展（真实客户端验收为 VS Code）
├─ eng/                      # 构建、发布与一键验收脚本
└─ docs/                     # 契约、指南、ADR 与验证报告
```

## 技术栈

C# / .NET 10（构建与运行宿主）+ `net40` 目标；ICorDebug 经 **ClrDebug 0.4.2**（MIT，固定版本）包装；Windows PDB 基于 **Microsoft.DiaSymReader 2.2.11** 与 **Microsoft.DiaSymReader.Native 17.12.0-beta1.24603.5**（CVE-2023-36796 修复基线，PDB 视为不可信输入）；MCP 用官方 **ModelContextProtocol / Core 2.2.0**（Apache-2.0，协议 2025-11-25）；Engine↔Host 即 Named Pipe + JSON-RPC。项目仅在 Windows 上运行与验证。

## 文档

- [docs/skill-mcp-install.md](docs/skill-mcp-install.md) — SKILL 与 MCP 安装指南（下载检查、客户端配置、技能安装、验证、升级、排障）
- [docs/agent-skill.md](docs/agent-skill.md) — Agent 使用工作流（工具调用、异步轮询、限额与错误恢复、Service/IIS 流程）
- [docs/mcp-tools.md](docs/mcp-tools.md) — 15 个工具契约：schema、默认值、错误码、资源边界
- [docs/cli.md](docs/cli.md) / [docs/dap.md](docs/dap.md) — CLI 与 DAP/VS Code 用法
- [docs/service-iis.md](docs/service-iis.md) — Windows Service 与 IIS 附加指南
- [docs/architecture.md](docs/architecture.md) — 架构、决策与 ADR（含求值 ADR-004）
- [ROADMAP.md](ROADMAP.md) — 各阶段实现与验收状态（阶段 0–4 已完成）

状态与验收记录：MVP（阶段 0/1/2）与阶段 3、阶段 4 均已完成并原地勾选，详见 `ROADMAP.md` 与 `docs/validation/`。
