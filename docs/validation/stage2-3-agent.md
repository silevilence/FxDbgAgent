# 阶段 2-3：技能安装与独立 Agent 验收

2026-09-05，结论：通过。快速审核未发现阻塞项；独立代理指出的过时进度措辞已改正，产品行为未改变。

## 安装与可移植性

技能为 `skills/fxdbg-agent/SKILL.md`，包含 YAML name/description 和两个随包引用。安装后的引用不依赖仓库路径。构建及 MCP 配置示例使用可替换的发布目录；13 个工具示例已解析并与真实 tools/list 的名称、字段及必填项核对。

实际项目级安装命令（固定 CLI 1.5.23，未修改全局配置）：

```powershell
npx --yes skills@1.5.23 add C:/code/dotnet/FxDbgAgent --skill fxdbg-agent --agent codex --yes
./eng/verify-stage2-3.ps1
```

首次执行目录为 `artifacts/stage2-validation/skill-project`，独立代理读取该目录下 `.agents/skills/fxdbg-agent/` 的实际安装文件。复验使用 artifacts 下新的临时项目。验证脚本检查三个文件的 SHA256、两份引用与 docs 的一致性及 frontmatter；展开后的命令、目录、哈希写入 `artifacts/stage2-validation/skill-install.json`。npm 缓存限定 artifacts，关闭 skills 遥测。最后的文案修正也重新经过安装验证。

## 自主调用证据

执行者为独立 Codex 子代理 `/root/mcp_agent_acceptance`；精确模型不可获取，继承父任务配置。未使用 Claude Code。透明桥仅将子代理选择的单次请求交给官方 MCP SDK 2.2.0 客户端，连接独立发布的 stdio Host；桥不包含调试流程，不直接调用 Host/Engine。

子代理依据每次真实响应自主完成 17 次 debug_ 调用、10 种工具：x86 自动启动、pending 断点重绑定、第 67 行停止、16 帧栈、变量和循环引用、分页截断、异步 step/status、旧帧 frame_not_found 及重新取帧恢复、三种单步、删除断点和 detach。目标随后写入 completed=ok 并自然退出；关闭桥后目标、Host 和桥 PID 均不存在。没有冒称 x64、WinForms、取消和故障矩阵为此次自主验收范围。

- [独立代理报告](stage2-3-agent-acceptance.md)
- [派发提示词](stage2-3-agent-prompt.md)
- [原始调用记录](stage2-3-agent-evidence/calls.jsonl)、[逐步决策](stage2-3-agent-evidence/decisions.md)
- [代理自身结论](stage2-3-agent-evidence/result.json)、[代理元数据](stage2-3-agent-evidence/agent-run.json)、[桥连接元数据](stage2-3-agent-evidence/ready.json)

上述文件从实际执行产物原样保存，包含受控测试样例数据，无用户敏感变量。独立代理产物不足或结论不通过时，`eng/verify-agent-evidence.ps1` 非零失败；此脚本只校验证据，不冒充重新运行自主 Agent。
