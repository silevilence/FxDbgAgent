# 阶段3最终双轴审核

> 本文主体为2026-09-06历史审核。2026-09-07的完整阶段3结论已包含Service/IIS，见文末补充与[3-4完成审核](stage3-4-final-review.md)。

范围：用户2026-09-06确认的3-1、3-2、3-3、3-5；3-4明确跳过。固定准备基线 `63ef4e73e7851f49cc95e710bee6892e9e816bd0`。初审比较 `git diff 63ef4e7...bc0cbdd`，涵盖 `bd6592e`、`038e338`、`8848424`、`bc0cbdd` 四项独立任务提交。

按 code-review 流程，两位独立代理分别读取完整差异、AGENTS.md规范和ROADMAP/需求规格；代理只读审核，不与主代理并行构建。以下保留两个轴，不合并或重排其发现。

## Standards

初审：1项P1阻塞、1项P3维护性建议。

- P1：DAP输出写锁/写入只有生命周期令牌；保持stdin开启且不消费stdout时，写满管道会阻塞清理，绕过30秒请求期限。独立无目标复现35秒后仍存活、输出缓冲65822字节。
- P3：MCP/DAP发布脚本重复，未来Engine依赖清单容易不同步。

修复：输出全过程独立3秒期限并通知断开；读取等待可取消，不受Windows阻塞ReadFile影响；标准输出FileStream报告Broken Pipe，不吞错误。真实附加测试覆盖输出关闭和背压，要求Host有界退出、目标继续。两个发布入口复用 `eng/publish-host.ps1`。

独立复核：两项均修复，未发现新阻塞。另检查管道FileStream的Dispose路径，未发现重新同步冲刷正文导致退出阻塞。未发现新增回调线程违规、重复Continue、Getter/ToString执行、Host加载COM或终止附加目标。

## Spec

初审：1项P2。

- DAP把 `levels=0` 和大于128的请求静默裁剪为128帧，客户端可能误认为栈结束；声明延迟栈加载却未提供 `totalFrames`。

修复：超预算和未分页深栈请求明确报错；合法分页有界多读一帧，返回请求数量和单调增长的totalFrames提示；每次停止/恢复清空提示。真实184帧场景验证连续分页、末页总数及拒绝路径。

独立复核：P2已修复，未发现新增需求缺陷；3-4跳过符合用户决定。协议依据为[官方DAP schema](https://raw.githubusercontent.com/microsoft/debug-adapter-protocol/main/debugAdapterProtocol.json)的stackTrace及延迟加载定义。

## 验证与最终状态

修复后Release真实DAP全套协议及新增184帧/输出故障回归通过，记录 `artifacts/stage3-validation/review-dap-protocol-Release.log`，监督器exit=0、timedOut=false、forcedOwnedCleanup为空。静态审核不替代运行时验收。

修复后实现 `b9d779a` 的完整Debug/Release回归于2026-09-06 05:23:48 UTC通过：3-1/2/3/5、真实VS Code以及全部阶段2/1/0回归成功，235项源码/测试/发布/技能输入前后一致。监督记录均exit=0、无超时、无强制清理残留；记录的自有进程全部退出。结论与固定机器证据见[阶段3验收报告](stage3-usability.md)。运行期间源码有变化的先前尝试不作为最终通过依据。

首次全量尝试在既有阶段1 CDB/SOS对照处失败：本机没有默认WindowsKits调试工具。已从微软官方SDK10.0.26100.9169以layout下载并通过MSI /a仅展开到artifacts，核验双架构CDB的Microsoft有效签名。新增显式绝对工具目录配置和提前预检，原对照命令/断言不变；两位代理只读复核确认无验收弱化。独立Debug/Release×x86/x64的14帧CDB/SOS对照已全部通过（`cdb-preflight-*.log`）。首轮失败结果保留在 `artifacts/stage3-validation/first-full-attempt/`。

审核计数：Standards初始2项、复核遗留0项；Spec初始1项、复核遗留0项。

## 2026-09-07完整阶段3审核补充

3-4a～3-4f以`cda481e`为固定基线独立审核。预检/清理P2及全回归暴露的入口分离问题均修复并复核，冻结实现`a0b4a2c`于08:23:55Z通过完整Debug/Release回归，包含全部阶段3与阶段2/1/0。最终遗留阻塞0，保留1项非阻塞脚本维护建议；变更生产行140/155（90.32%），测量到的生产代码总体72.85%。本轮[审核报告](stage3-4-final-review.md)和[证据快照](stage3-4-final-evidence/README.md)取代上文3-4跳过状态，保留历史发现和结果原貌。
