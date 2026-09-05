# 阶段 2 / MVP 验收

日期：2026-09-05。结论：**阶段 2 / MVP 验收通过**。最终一键入口 Debug/Release 全通过，源码哈希未变化，无遗留进程需要监督器强制清理。

## 入口与证据

```powershell
./eng/verify-stage2.ps1
# 定位单配置问题时：
./eng/verify-stage2.ps1 -Configuration Debug
./eng/verify-stage2.ps1 -Configuration Release
```

默认执行 Debug/Release 两份真实发布的 MCP stdio Host，标准客户端为官方 MCP SDK 2.2.0，协议 2025-11-25。MCP 验收不调用 CLI 代替工具，不直接调用共享 Host，不使用 mock Engine。单元竞态测试的内部接口仅用于控制线程交错。

入口先验证技能安装和已保存的真实独立 Agent 证据，再逐配置运行阶段 2-1、2-2a/b/c、2-4 与 MCP MVP 矩阵，最后调用 `eng/verify-stage1.ps1` 完整回归 CLI、共享后端及阶段 0 原生探针。单配置选项同样运行该配置的完整阶段 1 回归。

机器结果位于 `artifacts/stage2-validation/stage2-result.json`，构建/测试输入哈希位于 `stage2-inputs.sha256`；测试期间输入发生变化则失败。各子过程有总超时，记录日志与 `.processes` 身份/退出清单；内部用例还有取消期限和 finally 清理。任何遗留自有进程需要强制清理均判失败。监督器只处理本次创建、并经父子关系和创建时间核实的进程，持有句柄避免 PID 复用，不按进程名全局清理，不使用 KillTree。

## 需求 §13 对照

| 编号 | MCP 入口证据 | 命令及产物 |
|---|---|---|
| 1 双架构自动路由 | Console/WinForms × x86/x64 的 launch/attach 均从相同 MCP 入口按实际位数路由 | `verify-stage2.ps1`；`stage2-Debug.log`、`stage2-Release.log` 的 8 组 observation / 配置 |
| 2 Windows PDB / 断点 | 应用 MSF 7.00 Windows PDB，pending 自动变 verified，延迟模块加载后绑定，请求/绑定位置明确 | `stage2-mvp-{配置}.log`；`mvp-{配置}/.../calls.jsonl` |
| 3 停止与至少 10 层栈 | reason、实际线程、源码位置、断点 ID 匹配；矩阵断言至少 14 层，独立 Agent 实测 16 层 | MVP observe × 4 / 配置；[独立 Agent 报告](stage2-3-agent-acceptance.md) |
| 4 只读变量 | 参数 42、局部 84、字符串、一层 Node 字段、null/循环引用；userCodeCalls=0，正常结束 completed=ok | observation 和 MVP observe；分页/双层封装 4 MiB 拒绝及缩小恢复 |
| 5 Continue / 三种 Step | 同步/异步停止、over/into/out、自然退出、旧帧失效、操作结果不变，每架构 100 次 continue/pause | execution × 2 / 配置；MVP observe × 4 / 配置 |
| 6 未处理异常 | 安全存储字段给出 ScenarioException、e2e-unhandled-message、抛出位置和非空栈；默认不在 first-chance 停止 | MVP exception × 4 / 配置；debug 日志无异常内容 |
| 7 断开与生命周期 | 32 组 Console 双架构 launch/attach 故障矩阵；EOF/Host 强杀后 Engine 限期退出、目标存活并重附加；输出断开、8 会话、启动中断、句柄与脱敏 | `stage2-{配置}.log`；[生命周期报告](stage2-4-lifecycle.md) |
| 8 明确拒绝 | CoreCLR 返回 core_clr_not_supported；launch/attach 跨位数返回 architecture_mismatch；PDB 返回 symbolStatus=mismatch 与非空诊断，不接受错误符号绑定 | MVP rejections；`mvp-{配置}/pdb-mismatch-.../status.json` |
| 9 Console / WinForms | 两类目标均完成观察、延迟模块、异常三场景 × 双架构，且 observation 覆盖 launch/attach | 每配置 12 组 MVP + 8 组 observation，均为实际 .NET Framework 进程 |

## 工具、资源与独立 Agent

13 个工具均有实际成功路径；MVP 对每个工具发送不合法参数并断言 invalid_request，专项另覆盖无会话、无效状态、未知断点、失效帧/引用、错误位数、附加终止拒绝等业务反例。`SchemaAssertions` 将真实成功/失败结果对照 tools/list 中每个工具的完整输出 schema，包含可空源码字段、递归变量 children 和可选状态快照。原始 stdio 测试覆盖握手/协商、未知工具/方法、畸形与重复 JSON、4 MiB 无换行输入和 EOF。

资源专项覆盖 8/32/8 上限及可降低配置，饱和时控制路径、另一会话隔离、同步/异步期限实际暂停、真实 MCP 取消通知、受理失败释放资源、状态取消不中断运行、缓存期限/容量。状态查询取消后的槽保留到实际 RPC 完成，不能借取消绕过限额。

独立 Codex 子代理已经依据项目级安装的技能自主完成 17 次 debug_ 调用、10 种工具，解释 frame_not_found 并取新帧恢复；结果、决策和完整请求响应已提交到 [Agent 证据目录](stage2-3-agent.md)。没有使用 Claude Code，没有把固定脚本当作自主 Agent。安装固定 skills CLI 1.5.23，检查三个安装文件的哈希和引用一致性，不修改全局配置。缺少 Agent result.json 的负向检查确实失败；监督器对 30 秒睡眠进程施加 1 秒总期限并清理，证据为 `missing-agent-rejection.log` 与 `supervisor-timeout.log`。

## 审核与边界

[完整双轴审核](stage2-final-review.md) 的规范与需求发现已修复，并经两名审核代理只读复核，无剩余阻塞。三项确定性测试覆盖迟到 pause、异步接受前取消、超时与取消竞争；旧行为的前两项已实证失败，修复后通过。最终常规单元总数为 74 项。

本次发布只支持本机 Desktop CLR、只读观察，无求值、Getter、ToString、变量修改或网络监听。Host/Agent 断开尝试安全 Detach；**Engine 本体硬崩溃是独立限制**：实测 Desktop CLR 目标也退出，此时只验 Host 故障隔离和后续可用，不承诺目标存活。启动中断的 launch 用例观察到“目标尚未创建”，不扩大为所有启动时刻的目标存活保证。

最终双配置均通过：每配置 74 项单元测试、8 组 MCP observation、12 组 MVP、32 组生命周期矩阵及执行/资源专项；阶段 1 Debug/Release 完整回归同样通过。MCP 专项每配置观察到 663 个进程、MVP 每配置 72 个进程，均退出；清理最长 Debug 9458 ms、Release 9498 ms，均低于 15 秒。源码/构建/测试 201 个输入的哈希全程不变。六个受监督步骤全部 exit=0、无超时、无强制清理。持久化结果与日志哈希见 [机器结果](stage2-mvp-result.json)，输入清单见 [SHA256 清单](stage2-mvp-inputs.sha256)。ROADMAP 的阶段 2-5 已原地勾选，未移动条目。
