# DAP 接入 oh-my-pi（omp）

[oh-my-pi](https://github.com/can1357/oh-my-pi)（omp，[omp.sh](https://omp.sh/)，npm 包 `@oh-my-pi/pi-coding-agent`）内置 `debug` 工具，可驱动任意 DAP 适配器（断点、单步、栈、变量、求值）。本文说明如何把 fxdbg 作为**自定义 DAP 适配器**接入 omp，并用 `debug` 工具调试本机 .NET Framework 4.x 进程。配置与用法已由真实 omp 会话实测（2026-09-09，见[实测记录](#5-实测记录)）。

- 本文面向 omp harness 接入；VS Code / Cursor 接入见 [dap.md](dap.md)。
- omp 侧字段与行为以官方 [debug 工具文档](https://github.com/can1357/oh-my-pi/blob/main/docs/tools/debug.md) 为准。

## 1. 适用范围与前置

- **适用**：本机（Windows）上 .NET Framework 4.x Desktop CLR 进程——Console / WinForms / WinExe / Windows 服务（SCM）/ 本机 IIS，x86/x64 自动路由（含 AnyCPU 32BitPreferred）。
- **不适用**：CoreCLR（.NET Core/5+）、远程调试、函数求值（fxdbg 仅只读解释）。
- **部署**：`C:\Soft\FxDbgAgent\` 完整目录——`fxdbg-dap.dll` + 相邻 `engines\`（x86/x64 Engine 与 ClrDebug/DIA 依赖）+ `engine-manifest.json`。来源与发布见 [release.md](release.md)；只复制单个 DLL/EXE 会启动失败。
- **PDB**：目标须为与 exe 相邻且身份匹配的 **Windows PDB**（Debug 构建、`DebugType=full`）；不要用 portable PDB 冒充，引擎只接受匹配的 Windows PDB。
- omp 版本：以下行为基于 `@oh-my-pi/pi-coding-agent` 的 `src/dap/session.ts` 与 `src/tools/debug.ts`（2026-09 实测版本）。

## 2. 适配器配置（`.omp/dap.json`）

在 omp 会话项目根创建 `.omp/dap.json`：

```json
{
  "adapters": {
    "fxdbg": {
      "command": "dotnet",
      "args": [
        "C:\\Soft\\FxDbgAgent\\fxdbg-dap.dll",
        "--engine-dir",
        "C:\\Soft\\FxDbgAgent\\engines"
      ],
      "languages": ["csharp"],
      "fileTypes": [".cs", ".exe"],
      "rootMarkers": ["*.sln", "*.csproj"],
      "connectMode": "stdio",
      "launchDefaults": { "request": "launch", "stopAtEntry": true },
      "attachDefaults": { "request": "attach" }
    }
  }
}
```

**关键点：`args` 必须带 `--engine-dir`，且只允许这一种参数**（实测修复点）：

- `fxdbg-dap` 的启动参数仅有 `--engine-dir <目录>`（恰好两个参数）；`--engine-dir` 指向部署目录的 `engines\`。
- 带**任何其他参数**（如 `--log-level` / `--value-logs`——那是 MCP 版参数）都会打印 `Usage: fxdbg-dap [--engine-dir DIRECTORY]` 后退出；表现为 `DAP connection closed` / 会话启动失败。
- 不带参数时默认从 DLL 相邻 `engines/` 加载，目录缺失或清单不完整报 `Incomplete Engine bundle. Run eng/publish-dap.ps1.`——显式 `--engine-dir` 最稳。

字段说明（omp 适配器字段）：

| 字段 | 说明 |
|---|---|
| `command` / `args` | 以 `dotnet` 启动 `fxdbg-dap.dll`；`--engine-dir` 必需 |
| `connectMode` | `"stdio"`（默认）：DAP 走标准输入输出 |
| `languages` / `fileTypes` / `rootMarkers` | 仅用于 omp 的自动选配与排序；显式 `adapter: "fxdbg"` 时不依赖 |
| `launchDefaults` | DAP launch 参数默认值；`stopAtEntry: true` 启动即停在入口 |
| `attachDefaults` | DAP attach 参数默认值；`request: "attach"`（PID 需调用时显式传 `pid`） |

配置文件也支持 `dap.json` / `.dap.json` / `dap.yaml` / `.dap.yml` 等变体、顶层直接映射适配器；`command` 也可直接指向 `fxdbg-dap.exe`（但 `dotnet <dll>` 与发布脚本一致）。

## 3. 配置查找规则（重要）

omp 按**会话 cwd 所在目录**下探查找适配器配置（依次：项目根 → `.omp/`、`.claude/`、`.codex/`、`.gemini/` → 用户级 `~/.omp/agent/` 等，低优先级到高优先级合并）：

- **调用 `debug` 工具时不要传 `cwd`**：默认会话 cwd=仓库根，能命中 `.omp/dap.json`；传子目录（如 `OutMes4\SFC`）会报 `No debugger adapter available. Installed adapters: none`（实测）。
- 目标程序自身的工作目录不受影响：.NET 从 exe 目录读取配置；omp 的 `cwd` 只影响适配器配置解析与相对路径解析。

## 4. 使用示例（omp `debug` 工具）

```text
1) launch          { "action":"launch", "adapter":"fxdbg",
                     "program":"C:\\Code\\ZTCBB\\2.soft\\ZTCBB\\OutMes4\\SFC\\SFCClient.exe" }
                   → Session 建立，Stop reason: entry
2) set_breakpoint  { "action":"set_breakpoint", "file":"<Program.cs 绝对路径>", "line":46,
                     "condition":"isLogoutPath == false" }
                   → 可能先返回 pending（模块未加载），继续后自动绑定为 verified
3) continue        { "action":"continue" }
                   → 命中：Stop reason: breakpoint, Frame: SFC.SFCClient.Program.Main
4) 观察            { "action":"threads" } / { "action":"stack_trace" }
                   { "action":"scopes", "frame_id":<n> }
                   { "action":"variables", "variable_ref":<n> }
                   { "action":"evaluate", "frame_id":<n>, "expression":"1+1" }
5) terminate       { "action":"terminate" }   → 结束会话
```

**附加（attach）**：fxdbg 的 attach 需要 `processId`，omp 调用时显式传 `pid`：

```text
{ "action":"attach", "adapter":"fxdbg", "pid":1234 }
```

断点同步约定：同一文件重新 `set_breakpoint` 会替换该文件的断点集；`pending` 不是失败，模块加载后自动重绑（引擎每 5 秒重试）并在响应中回 `verified`。继续/单步后旧帧与变量引用全部失效，需重新取栈。

## 5. 实测记录（本机 2026-09-09）

| 能力 | 结果 |
|---|---|
| 启动 + entry 停止 | ✅ |
| `threads` / `stack_trace` | ✅（入口处托管栈为空属正常） |
| 源断点 `pending` → `verified` | ✅ |
| 条件断点 `condition`（bool） | ✅ 真命中 / 假放行双向验证 |
| `scopes` + `variables`（局部变量、引用展开） | ✅（`isLogoutPath=false`、`fmlog=null` 等） |
| `evaluate` | ✅ 只读子集：字面量/算术/局部变量名（`1+1`→2、`isLogoutPath`→false）；属性链 `Environment.Version.Major` 报 `expression_name_not_found`（**非故障**，设计如此） |
| `hitCondition` | ⚠️ 引擎支持；**omp harness 丢字段**（见 §6.1） |

## 6. 已知问题与绕过

### 6.1 `hit_condition` 对源码断点被 omp 丢弃（harness 缺陷）

- **现象**：`debug` `set_breakpoint` 传 `hit_condition` 时首次经过即命中，过滤不生效。
- **根因**（已核对 omp 源码）：`packages/coding-agent/src/dap/session.ts` 的 `setBreakpoint(file, line, condition, …)` 构造 DAP `setBreakpoints` 请求时只写入 `{line, condition}`，未映射 `hitCondition`；`src/tools/debug.ts` 的 `set_breakpoint` 分支也未把 `hit_condition` 传给 session（仅 `set_instruction_breakpoint` / `set_data_breakpoint` 传）。类型 `DapSourceBreakpoint.hitCondition` 已定义，属接线遗漏。
- **绕过（引擎端已验证有效）**：用 `custom_request` 发原始 DAP 请求：

  ```json
  { "action":"custom_request", "command":"setBreakpoints",
    "arguments":{ "source":{"path":"C:\\…\\Program.cs","name":"Program.cs"},
                  "breakpoints":[{"line":46,"hitCondition":">=2"}] } }
  ```

  实测 `>=2` 首次经过不命中（hitCount=1<2），过滤生效；响应在 `details.customBody`。
- **限制**：`custom_request` 不更新 omp 的断点缓存。之后对**同一文件**再调 `set_breakpoint` / `remove_breakpoint`，omp 会基于自己的缓存重建整组断点并覆盖 adapter 侧，绕过断点丢失。建议该文件全程用 `custom_request` 管理，或不用 `hitCondition`、改用 `condition`（fxdbg 的 `hitCount` 计数仍生效，只是不参与过滤）。
- **建议**：反馈 omp 在 `src/dap/session.ts` 的 `setBreakpoint` / `src/tools/debug.ts` 的 `set_breakpoint` 分支补传 `hit_condition`；修复后 `set_breakpoint` 直接可用。

### 6.2 异常类型过滤

omp 没有内置的 exception-filter action；用 `custom_request` 发送 DAP `setExceptionBreakpoints`（fxdbg 声明 `supportsExceptionFilterOptions`）：

```json
{ "action":"custom_request", "command":"setExceptionBreakpoints",
  "arguments":{ "filters":[],
                "filterOptions":[{"filterId":"firstChance",
                                  "condition":"exact:MyApp.Error;namespace:MyApp.Errors;derived:MyApp.BaseError"}] } }
```

规则（`exact:` / `namespace:` / `derived:`，分号 OR，最多 64 条）与默认只停未处理异常等语义见 [dap.md 异常类型条件](dap.md#异常类型条件阶段4-2)。

## 7. 注意事项

- **只读约束**（安全设计）：禁止函数求值、Getter 调用、`ToString()`、反射、写值、set next statement、任意 COM 访问。`unavailable` / `null` / `optimizedAway` 是三种不同状态，勿合并处理。
- **条件断点语法**：`condition` 为受限 bool 表达式（字段/局部变量比较、算术），默认求值预算 250 ms（最大 1000 ms）；条件错误或超时→引擎**保守停止**，先查断点 `message` 中的 `conditionDiagnostic`，修正后重建断点。
- **evaluate**：只读子集（字面量/算术/局部变量名）；属性链、方法调用、Getter 一律 `expression_name_not_found`，属预期。建议显式传 `frame_id`；对象结果经 `variables` 分页读取，恢复运行后引用失效。
- **会话生命周期**：同一时刻仅一个根会话；完成必须 `terminate`（仅限本会话 launch 的目标）。**attach 的目标禁止 terminate**，只能 detach；不要杀进程树代替断开。目标被关闭/重启后旧会话不恢复，需重新 attach 并按 PID+创建时间核对（服务/IIS 场景，见 [service-iis.md](service-iis.md)）。

## 8. 常见故障对照表

| 现象 | 原因 | 处理 |
|---|---|---|
| `No debugger adapter available. Installed adapters: none` | 配置检索根不对：调用时传了子目录 `cwd`，或 `.omp/dap.json` 缺失 | 不传 `cwd`（回仓库根）；检查 §2 文件 |
| `DAP connection closed …` / 适配器 Usage 退出 | `--engine-dir` 缺失；或误传 `--log-level`（DAP 版不支持） | 按 §2 的 `args` 修正 |
| 断点 `pending` 不命中 | 模块/PDB 未加载；PDB 不匹配 | 继续运行让模块加载（自动重绑）；核对 PDB 相邻且匹配、Debug 构建 |
| `expression_name_not_found` | evaluate 触发了受限语法（属性链/函数） | 用 `scopes` / `variables` 读值，或改受限表达式 |
| `hit_condition` 首击即中 | harness 丢字段（§6.1） | 用 `custom_request` 原始 DAP |
| 附加提示 `architecture_mismatch` / `core_clr_not_supported` | x86/x64 路由错；CoreCLR 目标 | 核对目标位数；CoreCLR 不支持 |

## 9. 参考

- [dap.md](dap.md) — DAP 协议、边界与 VS Code / Cursor 接入
- [mcp-tools.md](mcp-tools.md) — MCP 工具契约（15 个 `debug_` 工具；`.omp/mcp.json` 接入为备选，本文主推 DAP）
- [service-iis.md](service-iis.md) — Windows Service / IIS 附加指南
- [release.md](release.md) — 发布 zip 与部署
- omp 侧：[debug 工具文档](https://github.com/can1357/oh-my-pi/blob/main/docs/tools/debug.md)、[项目主页](https://github.com/can1357/oh-my-pi)、[omp.sh](https://omp.sh/)
