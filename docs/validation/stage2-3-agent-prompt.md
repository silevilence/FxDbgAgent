# 独立 Agent 调用验收提示词记录

子代理：`/root/mcp_agent_acceptance`。用户明确指定使用子代理，禁止 Claude Code。精确模型标识未由独立执行环境暴露，使用继承的主代理配置，不另选外部模型。

验收任务：先读取临时项目实际安装后的 `fxdbg-agent/SKILL.md` 及 references；凭文档自主通过真实 MCP 对仓库 .NET Framework 样例完成 stopAtEntry 启动、源码断点、继续并等待、真实线程/栈/帧与变量读取、单步、错误恢复和清理。允许按真实结果选择异步轮询或附加验证。只能启动/清理自己创建的测试目标，附加目标不能 terminate，禁止终止进程树。

执行入口：`artifacts/stage2-validation/agent-call-acceptance/ready.json` 标识实际桥/Host 进程。子代理逐次写 `NNN.request.json`，桥用官方 MCP 客户端将 tools/list 或 tools/call 转发至 stdio；结果写对应 response 文件，完整请求/结果记入 calls.jsonl。桥无调试流程、mock 或 CLI 后端，只自动完成协议握手和响应 ping。每个请求必须先写临时文件，再改名提交；子代理读取响应后才能自行决定下一步。允许单次任意请求辅助函数，禁止预编排整套验收脚本。

提供的环境信息：Windows 本地仓库 `C:/code/dotnet/FxDbgAgent`；技能首次实际安装在 `artifacts/stage2-validation/skill-project/.agents/skills/fxdbg-agent`；已有 Debug Console x86/x64 可执行文件和共享 `EndToEndScenarios.cs`，可自行读取样例选场景/源码行，并创建本验收的输出目录。没有提供预生成的 sessionId/threadId/frameId、调试调用结果或强制调用次序。

输出要求：持续写 decisions.md 记录每步真实观察与下一步理由；最终报告实际身份、可获取模型信息、安装路径、提示词摘要、调用列表和响应证据、通过项及阻塞。产品问题只报告给主代理，不自行修改实现。完成后通过 close 控制请求关闭桥和 Host。不得执行固定回归脚本替代自主验收，不得只审核已有测试报告。
