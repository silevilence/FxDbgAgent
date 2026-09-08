# SKILL 与 MCP 安装指南（使用者）

FxDbg 的 AI Agent 接入由两部分组成：**MCP 服务**（发布包内的 `fxdbg-mcp.dll` 与相邻 `engines/` 双架构依赖，运行在 Windows 本机）与 **fxdbg-agent 技能**（发布包内的 `skills/fxdbg-agent/`，用 `skills` CLI 安装到你的 AI 客户端项目）。按本指南四步完成：**下载检查 → 配置 MCP → 安装技能 → 验证**。工作流与工具调用见 [agent-skill.md](agent-skill.md)，工具契约与限额见 [mcp-tools.md](mcp-tools.md)。

## 1. 下载并检查发布包

**下载**：GitHub Releases——<https://github.com/silevilence/FxDbgAgent/releases>，下载 `fxdbg-<版本>.zip`（版本名以实际发布为准；发布尚未上线时，以发布者提供的包为准）。

**解压**：解压到任意目录，例如 `C:/tools/FxDbg/`（路径含空格、任意工作目录均可运行）。

**检查完整性**——以下文件必须都在（有缺失请重新下载完整压缩包；压缩包损坏时解压会报错）：

```text
C:/tools/FxDbg/
├─ fxdbg-mcp.dll            MCP 服务入口
├─ fxdbg-mcp.pdb
├─ fxdbg-mcp.runtimeconfig.json、fxdbg-mcp.deps.json
├─ ModelContextProtocol.dll、ModelContextProtocol.Core.dll 及依赖 ·dll
├─ engines/
│  ├─ engine-manifest.json  引擎清单（数量与文件列表）
│  ├─ FxDbg.Engine.x86.exe、FxDbg.Engine.x64.exe（+ 对应 PDB）
│  ├─ FxDbg.Interop.dll、FxDbg.Symbols.Windows.dll、FxDbg.Engine.Runtime.dll 等
│  └─ ClrDebug、DiaSymReader 系本机依赖（.dll/.pdb）
└─ skills/fxdbg-agent/
   ├─ SKILL.md
   └─ references/workflow.md、references/mcp-tools.md
```

**机器要求**：Windows（本机，x64），目标应用为 .NET Framework 4.x Desktop CLR 进程并带匹配的 Windows PDB；运行服务需要 **.NET 10 运行时**（`dotnet` 命令可用）；安装技能需要 **Node.js/npm**；无需 IDE 与源码。

**注意**：只复制单个 DLL/EXE 无法启动；`engines/` 与 `skills/` 必须随包保留。

## 2. 配置 MCP 客户端

把下面配置加进你的 AI 客户端 MCP 配置（`C:/tools/FxDbg` 已在此示例中，请与你的实际解压路径一致）：

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

- 协议固定 `2025-11-25`：客户端按 stdio 逐行 JSON 完成 initialize → notifications/initialized → tools/list / tools/call。
- stdout 只有协议；启动错误写 stderr 并非零退出。服务每 3 秒发 MCP ping，客户端应立即以同 ID 的 `result:{}` 回复（官方客户端自动处理），超过 5 秒无响应会开始清理。
- 常用启动参数（其余见 [mcp-tools.md](mcp-tools.md)）：`--log-level off|error|info|debug`（默认 info）、`--log-file <路径>`、`--value-logs off`（永远禁止值日志）。
- 配置文件的存放位置因客户端而异（VS Code 项目级为 `.vscode/mcp.json`），以所用客户端文档为准。

## 3. 安装 SKILL

在你的 AI 客户端项目目录（使用技能的目录）执行：

```powershell
npx --yes skills@1.5.23 add C:/tools/FxDbg --skill fxdbg-agent --agent codex --yes
```

- `C:/tools/FxDbg` 指向**解压后的发布包目录**（其下含 `skills/fxdbg-agent/`），不需要源码仓库。
- 固定 CLI 版本 `skills@1.5.23` —— 安装方式依据 [skills 官方说明](https://github.com/vercel-labs/skills)。
- **项目级安装**（不加 `--global`），不改你的全局配置；装到项目 `.agents/skills/fxdbg-agent/`。
- 技能自带 `SKILL.md` 与 `references/`，安装后不依赖发布包即可使用。
- `--agent codex` 为本项目验收所用客户端；其他客户端按 skills 官方文档选对应参数。

## 4. 验证

1. **技能**：确认项目 `.agents/skills/fxdbg-agent/` 下 `SKILL.md`、`references/workflow.md`、`references/mcp-tools.md` 三个文件存在。
2. **服务**：重启客户端连接后运行 `tools/list`，应列出 15 个工具（13 个原工具 + `debug_evaluate` + `debug_configure_exceptions`）。
3. **错误路径**：用不存在的 sessionId 调 `debug_status`，应返回 `ok=false`、`error.code=session_not_found` 的结构化错误（不是连接失败）。
4. **真实冒烟**：`debug_launch(key, stopAtEntry=true)` → 设置断点 → `debug_continue` → `debug_stack`/`debug_variables` → `debug_detach`，完整流程见 [agent-skill.md](agent-skill.md)。

## 5. 更新

- 下载新版本 `fxdbg-<版本>.zip`，**整体替换**部署目录（先关闭使用该服务的客户端连接；不要只覆盖单个文件），客户端重新连接。
- 技能是拷贝不是链接：替换后重复第 3 步命令刷新 `.agents/skills/fxdbg-agent/`。
- 已用 `sourceMappings` 等会话配置不会保留到新版本；调试目标是继续调试还是分离以实际会话状态为准。

## 6. 常见问题

| 现象 | 处理 |
|---|---|
| 启动即失败，stderr 指出 engines 缺失/清单不符 | 用完整解压目录替换；核对 `engine-manifest.json` 与相邻依赖 |
| `access_denied`（Service/IIS 跨身份附加） | 用管理员令牌重启客户端与该 MCP 服务后重新附加；服务只按需启用已有 SeDebugPrivilege 并恢复，不自动弹 UAC |
| `not_managed_process` | 目标尚未加载 CLR：先请求页面/服务预热，重新定位 PID 后附加 |
| 服务重启/IIS 回收后旧 session 失败 | 旧 session 与引用已失效；重新核对 PID 并显式 attach |
| 客户端不响应 ping | 服务约 5 秒后开始清理；确认客户端实现官方 ping 处理 |
| 日志脱敏 | 默认仅元数据；完全静默用 `--log-level off`，永远禁止值日志用 `--value-logs off` |

Service/IIS 的完整流程（SCM 服务名/应用池核对、请求预热、影子复制符号定位、回收后重附加）见 [service-iis.md](service-iis.md)。

## 附：源码构建与发布（维护者）

从源码构建/发布（需要 .NET SDK 10.0.301，见 `global.json`）与技能安装验证：

```powershell
./eng/publish-mcp.ps1 -Configuration Release   # 产物 artifacts/mcp/Release/（含 engines/ 与 skills/）
./eng/verify-skill-install.ps1                 # 校验技能安装与文档同步（SHA256）
```

`docs/mcp-tools.md`、`docs/agent-skill.md` 与 `skills/fxdbg-agent/references/` 对应文件必须逐字节一致（`verify-skill-install.ps1` 强制），修改文档后须同步；技能安装固定 `skills@1.5.23`，项目级安装不加 `--global`。
