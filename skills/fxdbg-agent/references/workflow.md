# 使用 FxDbg 调试 .NET Framework

适用 Windows 本机 .NET Framework 4.x Desktop CLR。服务不调试 CoreCLR、不监听网络，也不执行目标 Getter、ToString 或任意函数求值。所有入口使用同一个 Host/Engine 后端。

## 构建和 MCP 配置

在 FxDbg 源码根目录运行 `./eng/publish-mcp.ps1 -Configuration Release`，需要 global.json 指定的 .NET SDK。完整发布目录是 `artifacts/mcp/Release`；部署时复制整个目录，包括 `engines/` 的双架构程序和依赖。目标可为已有可执行文件，PDB 必须与之匹配，源码路径使用实际本地路径。无需安装 IDE。

以下是通用 MCP 客户端配置，`C:/tools/FxDbg` 是示例部署目录，应替换为本机实际绝对路径，不绑定开发者目录：

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

客户端通过逐行 UTF-8 stdio 完成 initialize（2025-11-25）、notifications/initialized、tools/list、tools/call。stdout 只有协议。服务每 3 秒发标准 ping，客户端应立即回复同 ID 的 result:{}；超过 5 秒无应答会启动清理。官方 MCP 客户端自动处理 ping。

## 原13工具与受限表达式

阶段4-1新增debug_evaluate：在停止态先从debug_stack获取frameId，再调用`debug_evaluate({sessionId,frameId,expression:"number + 2"})`。字段与数组通过直接读取，String/Array/Math仅有契约列明的调试器内固有操作，不执行目标函数。对象结果referenceId可交给debug_variables分页；继续后所有旧引用失效。默认求值250ms、最大1000ms，显示上限与variables相同；禁止调用或预算错误不能用Getter、反射、赋值等替代，按错误码检查语法、状态或重新取帧。具体语法、数值子集、资源预算与原生不可中断故障限制见随包mcp-tools契约。

下表是 tools/call 的 `params`，表内 SESSION/BP/FRAME/REF/OP、线程和 PID 都是占位说明，实际必须使用上一步返回的数据；工具结果读取 structuredContent，并检查 ok/error.code。示例路径也须替换。先运行 tools/list 获得当前完整 schema。

| 工具 | params 示例 |
|---|---|
| debug_launch | `{"name":"debug_launch","arguments":{"exe":"C:/app/Sample.exe","args":["--mode","example value"],"cwd":"C:/app","env":{"EXAMPLE_MODE":"test"},"arch":"auto","stopAtEntry":true}}` |
| debug_attach | `{"name":"debug_attach","arguments":{"pid":1234,"arch":"auto"}}` |
| debug_set_breakpoint | `{"name":"debug_set_breakpoint","arguments":{"sessionId":"SESSION","file":"C:/app/Program.cs","line":42,"enabled":true}}` |
| debug_remove_breakpoint | `{"name":"debug_remove_breakpoint","arguments":{"sessionId":"SESSION","breakpointId":"BP"}}` |
| debug_continue | `{"name":"debug_continue","arguments":{"sessionId":"SESSION","waitForStop":true,"timeoutMs":10000}}` |
| debug_step | `{"name":"debug_step","arguments":{"sessionId":"SESSION","threadId":5678,"kind":"over","waitForStop":true}}` |
| debug_pause | `{"name":"debug_pause","arguments":{"sessionId":"SESSION"}}` |
| debug_status | `{"name":"debug_status","arguments":{"sessionId":"SESSION","operationId":"OP"}}` |
| debug_threads | `{"name":"debug_threads","arguments":{"sessionId":"SESSION"}}` |
| debug_stack | `{"name":"debug_stack","arguments":{"sessionId":"SESSION","threadId":5678,"start":0,"count":32}}` |
| debug_variables | `{"name":"debug_variables","arguments":{"sessionId":"SESSION","frameId":"FRAME","start":0,"count":100,"maxDepth":1,"maxStringLength":256}}` |
| debug_detach | `{"name":"debug_detach","arguments":{"sessionId":"SESSION"}}` |
| debug_terminate | `{"name":"debug_terminate","arguments":{"sessionId":"SESSION"}}` |

开始断点流程：launch(stopAtEntry=true) → set_breakpoint → continue 等待 → 从 result.stop 取得实际 threadId → stack 取得 frameId → variables。通过 `debug_threads` 核对需要观察的线程。继续或单步后之前的帧/引用失效，必须重新取栈；into/over/out 都需要实际 threadId。完成后 remove_breakpoint，按用户意图 detach 或合法 terminate。attach 是另一个创建会话的入口，不能对已调试 PID 重复附加。

已创建断点可用 `{"sessionId":"SESSION","breakpointId":"BP","enabled":false}` 更新，不能同时带 file/line。对象展开在 variables 原参数上增加 `referenceId:"REF"`；循环引用返回同一引用标识，不递归展开。运行时不应调用 threads/stack/variables。

异步流程将 continue/step 的 waitForStop 设为 false，保存 result.operationId，随后只调用 debug_status(sessionId, operationId)。state=running 时继续轮询，completed 时读取对应 stop，timedOut/cancelled/failed 时先恢复状态确认。旧操作结果不会被新停止覆盖。若想主动停止异步执行，调用 pause；它以 userPause 完成该操作。不要为了“等结果”再次 continue。

## 限额与恢复

- timeoutMs 默认 10000，1～240000；覆盖执行受理与等待，异步返回也不清除期限。超时通常返回真实暂停 StopInfo；取消执行请求会关闭会话并尝试安全 Detach。status 的超时/取消不改变运行操作期限。
- 每 Host 默认活动/启动中会话 8 个、普通在途调用 32 个，状态与清理另有 8 个槽。rate_limited 附 error.retryAfterMs=1000。启动参数可以降低上限，不能增大。单行请求和响应最多 4 MiB，变量超限后缩页重试。
- stack 默认 32 帧，最多 1024；variables 默认 100 成员/深度 1/字符串 256，最多 1024/8/32768。不要一次展开整个对象图。
- operation_not_found 表示未知、跨会话或已淘汰。每会话最多 128 个完成操作，保存 10 分钟；终态会话同样保留 10 分钟，总计最多 1024。重启后旧会话/操作不恢复。
- pending 断点需等待目标模块/PDB；moved 必须报告实际行；unresolved、symbols_missing/mismatch/read_failed 应检查模块与匹配的 Windows PDB。frame_not_found/value_unavailable 时重新获取当前停止下的栈与引用；null 与 optimizedAway 不应合并为“无值”。
- architecture_mismatch、core_clr_not_supported、already_debugged 需要修正目标或解除已有调试器。不要静默跨位数，不用 portable PDB 冒充 Windows PDB。

取消尚未完成请求需发 MCP `notifications/cancelled`，requestId 与原 JSON-RPC 请求 ID 相同；通知无响应。仅取消本地 await 并不证明通知已发出。取消和成功可能竞争，先查会话状态，不盲目重试产生副作用的创建或运行命令。

正常退出先关闭 MCP stdin，服务尝试将 launch/attach 目标安全分离。禁止用终止整个进程树实现“断开”；某些客户端有这种清理策略，必须由客户端所有者改为 EOF。Host 被强杀的附加目标验证仍可存活/重附加；Engine 本体被强杀时 Desktop CLR 目标可能一起退出，这是已实测限制。附加目标不能 terminate。

默认日志只含元数据，debug 级别也不写变量、参数/env、异常消息和 SDK 原始帧。可设 `--log-level off`，或 `--log-file <路径>` 写最多约 1 MiB 的本地文件；`--value-logs off` 永远禁止值日志。目标数据仍按用户明确请求进入工具结果。

## 安装技能与本项目验收

固定安装 CLI 为 `skills@1.5.23`。在要使用技能的项目目录执行：

```powershell
npx --yes skills@1.5.23 add <FxDbg源码绝对路径> --skill fxdbg-agent --agent codex --yes
```

采用项目级安装，不加 --global，不修改用户全局配置。安装方式依据 [skills 官方说明](https://github.com/vercel-labs/skills)。技能自带 references，安装后不依赖源码相对引用。可在没有源码的机器上使用已部署 MCP 服务；只有构建服务时才需要源代码仓库。

本项目按用户指定由独立 Codex 子代理验收，禁止使用 Claude Code。子代理先读取安装后的技能与引用，自主决定每次真实调用，保留身份、提示词、请求/响应与结论。透明 stdio 桥仅转发工具，不实现调试或预编排流程；固定脚本回归的通过结果单独记录，不能替代 Agent 自主验收。

## Windows Service 与 IIS

先核对SCM服务名/应用池及当前PID、创建时间；IIS先请求托管页面预热，再用appcmd list wp列出目标池全部候选。禁止按w3wp进程名任取目标，回收后不得复用历史PID。Host/Engine只按需启用已有SeDebugPrivilege并恢复；遇access_denied，用有权限的客户端重新启动Host后重新附加，工具不会自行弹UAC。native-only worker的not_managed_process需要预热并重新定位。

使用debug_attach保存sessionId和实际architecture，在可重复业务方法设置断点，再触发请求或等待服务周期。停止时获取实际threadId、frameId和变量；只读观察不执行Getter或业务函数。暂停影响整个进程及其所有请求。结束用debug_detach，附加目标禁止terminate；不要杀进程树代替分离。

保留IIS影子复制。status.modules的path/pdbPath/symbolStatus/diagnostic给出实际模块与相邻Windows PDB；仅该相邻路径是符号候选，匹配PE/PDB身份后才绑定，不扫描磁盘。missing/mismatch需正确部署匹配PDB；readFailed需修复读取权限或占用，权限恢复即使未改变文件元数据也会每五秒重试。pending会在模块/PDB就绪后自动重绑定。sourceMappings只转换源码路径；动态页面可使用部署根到本地根映射，业务DLL已有本地源码路径时使用精确module范围的同路径映射，避免被页面映射反向改写。

多域断点可限定appDomainId；域卸载后该ID及帧/引用失效，限定断点不跟随同名新域，未限定断点可在新模块重新绑定。纯内存/Reflection.Emit模块无可用PDB时明确报告符号不可用。服务重启/IIS回收使旧session结束；重新预热、列出候选、核对PID并显式attach，新会话不继承旧引用。正常EOF与Host退出会尝试安全detach；Engine硬崩溃的目标存活仍受CLR/操作系统约束。

阶段4-2新增debug_configure_exceptions：用sessionId和firstChance:true加rules数组限定异常类型（exact完整类型、namespace按点分隔的命名空间前缀、derived自身/基类匹配，多个规则OR）。运行中可动态更新；rules:[]或firstChance:false清空，true省略rules会停在所有first-chance。默认只停未处理异常，按需使用窄规则避免频繁停止；非法配置保留旧规则。不要用属性Getter、ToString或反射推断类型/消息。诊断提示无法过滤时会保守停止，可修正规则或关闭过滤再继续。

阶段4-3在debug_set_breakpoint新建时可带condition和hitCondition（=N或>=N，N正整数≤2147483647）。表达式必须为bool并遵守250ms只读解释预算，次数与表达式AND。debug_status的breakpoints可查hitCount和conditionDiagnostic；模块重载保留计数，删除重建重置。条件错误/超时会保守停止，先查诊断，禁用/移除或修正重建后再继续，不重复求值当前错误，不调用目标Getter/格式化。
