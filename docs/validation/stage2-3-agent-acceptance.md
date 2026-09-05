# 阶段 2-3：独立 Codex Agent 真实 MCP 调用验收

日期：2026-09-05。结论：**本次自主验收通过**。安装后的技能与引用足以指导独立 Agent 完成真实 .NET Framework x86 目标的启动、源码断点、继续等待、线程/栈/变量读取、单步、错误恢复及清理。本报告不是固定回归脚本的结果。

## 身份与输入

执行者：独立 Codex 子代理 `/root/mcp_agent_acceptance`。精确底层模型未向该子代理运行环境暴露；继承父任务模型，具体版本未知，不推测型号。未使用 Claude Code 或其他外部账号。

任务提示词摘要：读取项目级已安装 fxdbg-agent 技能及引用，自主决定每一次真实 MCP 调用；仅经官方 MCP 客户端透明桥访问独立 stdio Host；不得直接调用 Host/Engine/CLI，不得修改产品代码，不得用固定回归脚本代替自主操作；依据真实返回标识完成调试并写入证据，最后关闭 stdin。

实际读取的安装文件：

- `C:/code/dotnet/FxDbgAgent/artifacts/stage2-validation/skill-project/.agents/skills/fxdbg-agent/SKILL.md`
- 同目录 `references/workflow.md`
- 同目录 `references/mcp-tools.md`

另读取目标源码 `tests/Debuggees/Shared/EndToEndScenarios.cs`，搜索 Console 程序的 `--scenario` 参数入口与实际源码行。未读取产品实现来决定调用，也未构建或修改产品代码。

## 实际环境与证据

透明桥 ready.json 报告官方 MCP SDK 2.2.0、协议 2025-11-25、独立 MCP Host PID 28668、桥 PID 29504。桥只转发单次 model-chosen 请求；辅助脚本 `invoke-one.ps1` 只分配递增编号、原子提交一个请求和等待对应响应，无调试流程或条件分支。

目标：`C:/code/dotnet/FxDbgAgent/tests/Debuggees/Fx40.Console.x86/bin/Debug/net40/Fx40.Console.x86.exe`，参数 `--scenario values C:/code/dotnet/FxDbgAgent/artifacts/stage2-validation/agent-call-acceptance/target-output`。

会话 `19a7c5ef-5403-4588-80c7-08f9be89b00f`；实际 PID 19676；自动路由 x86；CLR `v4.0.30319`，文件版本 `4.8.9325.0 built by: NET481REL1LAST_25H2_C`。

完整请求与响应位于 `artifacts/stage2-validation/agent-call-acceptance/NNN.request.json`、`NNN.response.json`，另有原始 `calls.jsonl`。逐步观察和下一步理由在 `decisions.md`。以下编号直接对应原始证据文件。

| 编号 | 实际调用 | 关键响应与决策 |
|---|---|---|
| 001 | tools/list | 发现 13 个 debug_ 工具及严格输入 schema |
| 002 | debug_launch，auto，stopAtEntry=true | ok=true，实际 x86/PID/CLR 返回，状态 stopped |
| 003 | debug_set_breakpoint，EndToEndScenarios.cs:67 | 返回实际断点 ef1c280b-09a8-4e99-8088-34d7f0d092e1；pending，等待模块符号 |
| 004 | debug_continue，waitForStop=true | completed，reason=breakpoint，精确命中请求的第 67 行；线程 40844，Observe 方法 |
| 005 | debug_threads | 停止线程 21860、40844，确认停止线程真实存在 |
| 006 | debug_stack，线程 40844，count=32 | 16 帧：Observe、13 个 Recurse、Run、Main；首帧 1:40844:0 |
| 007 | debug_variables，首帧，100/1/256 限额 | number=42、message=hello-framework、localNumber=84、localText=local-value；Nothing 为 null；Self 复用父引用；userCodeCalls=0 |
| 008 | debug_variables，实际 LargeTexts 引用，count=2、maxStringLength=32 | 仅 [0]、[1] 两元素，长字符串各截断到 32 字符（含省略号） |
| 009 | debug_step，over，waitForStop=false | running，操作 ea6cf53a-2f19-458e-800f-fc4d2a6632cd |
| 010 | debug_status，指定上述 operationId | completed，step 停于第 68 行；断点 verified；目标 Windows PDB loaded；未再发 continue 等待 |
| 011 | debug_variables，故意使用之前有效但已过期的帧 | ok=false，frame_not_found，消息明确属于 earlier stop；预期负向测试 |
| 012 | debug_stack，当前线程，count=1 | 获得新帧 2:40844:0，第 68 行 |
| 013 | debug_variables，使用新帧 | 恢复成功；sink 从 0 变为 42；userCodeCalls 仍为 0；获得停止 2 的新对象引用 |
| 014 | debug_step，into | completed，进入 AddOne 第 79 行（方法开括号的实际序列点） |
| 015 | debug_step，out | completed，返回 Observe 第 68 行的调用者序列点 |
| 016 | debug_remove_breakpoint，实际断点 ID | removed=true |
| 017 | debug_detach | ok=true，调试会话终态；随后目标输出 completed=ok 并自然退出 |
| 018 | debug_status | 终态可查询，stop=null、closing=false、activeOperationId=null |
| 019 | 桥 close（不是 debug_ 工具） | closed=true，正常关闭 MCP stdin |

## 验收判断

启动 stopAtEntry、pending 自动重绑定、源码精确命中、继续等待、真实线程与深栈、局部及参数、对象字段与循环引用、分页/字符串截断、over/into/out、异步 status 查询和旧帧错误恢复均由实际响应确认。

没有调用 Getter、ToString 或求值工具。Node.Dangerous 属性未出现在原始字段展开中；两次读取 userCodeCalls=0，且分离后目标的防求值检查通过并写入 `target-output/completed` 内容 `ok`，为本次路径提供行为证据。null 被明确表示，本次 Debug 样例未出现 unavailable 或 optimizedAway，因此不声称覆盖两者。

清理：删除断点后 detach，未调用 terminate，未杀目标或进程树。close 后独立只读进程查询确认目标 PID 19676、MCP PID 28668、桥 PID 29504 均不存在。detach 返回 sessionState=terminated 表示调试会话终态；目标正常完成由 completed=ok 单独证实。

未发现阻塞本次目标的产品或文档问题。框架 mscorlib/System 的本地 PDB 缺失有明确诊断，不阻塞本次目标源码观察；应用自身 PDB 为 loaded。工具契约仍有“阶段 2-3 将补充完整示例”的进度措辞，工作流已提供可用示例，这只是非阻塞文案滞后。

范围边界：本次独立 Agent 执行 17 次 debug_ 调用、覆盖 10 种 debug_ 工具。没有自主执行 attach、pause、terminate、取消/超时、异常、x64、WinForms 或 Engine 崩溃专项；这些不能由本报告推断通过，也不以其他脚本结果补充冒充本次 Agent 证据。
