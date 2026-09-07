# 阶段3-4整体完成审核

审核日期：2026-09-07。固定实施基线：`cda481eea0d3ac1d4b27bab2ba858838a844c7ec`；首次独立审核目标：`5384613`。本报告覆盖3-4a～3-4f整体，逐项开发期间的快速审核不替代本报告。用户已授权修复阻塞项、复核及本地提交，不推送。

## 需求满足

| 任务 | 对照要求及证据 | 审核结果 |
| --- | --- | --- |
| 3-4a | [真实夹具](stage3-4a-fixtures.md)：net40双架构Service、完整IIS/WebForms、延迟模块及Windows PDB；SCM LocalService/Session 0；只读预检、专属资源、两轮部署清理和中断恢复 | 初审发现预检信息与清理完成判据两项P2，修复见下 |
| 3-4b | [权限矩阵](stage3-4b-permissions.md)：管理员和受限令牌、SeDebug启用/恢复、同用户及并发会话、错误语义、双架构Host/Engine路由 | 实现满足，等待最终整体回归 |
| 3-4c | [服务矩阵](stage3-4c-services.md)：源码/栈/变量、三种单步、拒绝terminate、EOF/Host异常退出、SCM重启和显式重新附加；业务心跳验证分离后进展 | 实现满足，等待最终整体回归 |
| 3-4d | [IIS符号矩阵](stage3-4d-iis-symbols.md)：准确worker选择、实际影子路径、业务和动态页面Windows PDB、缺失/失配/读取拒绝后恢复 | 实现满足，等待最终整体回归 |
| 3-4e | [IIS生命周期](stage3-4e-iis-lifecycle.md)：首次加载pending、多域身份/卸载重建、陈旧引用拒绝、内存模块诊断、重叠回收及显式新PID附加 | 实现满足，等待最终整体回归 |
| 3-4f | [总验收报告](stage3-4-service-iis.md)：一键专项/完整脚本、显式跳过只通过子集、MCP/CLI/DAP/真实VS Code、用户及随包文档同步、源码哈希与清理证据 | 实现已独立提交；最终回归、覆盖率和完成判定待录入 |

## 独立Standards与Spec审核

按code-review流程由独立Standards与Spec代理并行只读审核固定基线以来的变更，再由根代理逐项判断并修复。Standards初审无P0/P1/P2和硬性规范违反，发现1项P3；Spec初审发现2项P2。两位代理对修复和覆盖率采集入口再次只读复核，未发现新增阻塞；静态复核不替代真实管理员验收。

### 🟠 P2：Preflight缺少目录ACL、具体身份和计划配置变更

**完全采纳。** [环境脚本](../../eng/setup-stage3-4-environment.ps1)的Preflight现在报告部署目录或最近现存父目录的Owner/SDDL、调用者/LocalService/双应用池身份及SID、服务/池/站点/回环绑定和ACL授权计划。尚未创建或无权查询的项目明确标记unknown及原因，保持只读。非管理员预检已实际执行；管理员结构断言纳入[3-4a验收](../../eng/verify-stage3-4a.ps1)。

### 🟠 P2：清理只接受删除命令，未确认实际资源和进程退出

**完全采纳。** 清理通过句柄和创建时间固定自有SCM服务及WAS worker进程实例，停止/删除后有界等待这些实例退出，并确认服务、站点、应用池及worker记录均消失，之后才删除部署目录并记录removed。超时返回失败并保留资源清单；不强杀附加目标。3-4a新增持有ServiceHandle的真实延迟删除反例：首次清理必须失败且不得标记removed；释放句柄后重试必须成功。

真实反例首次暴露重试中的1072处理和测试句柄释放问题，已继续修复：1072仅视为pending，仍等待实际删除；测试显式Dispose取得的SafeServiceHandle；恢复清单保留已退出的原进程实例。独立Spec复核无新增阻塞。2026-09-07 06:21:34Z双配置专项通过：两次延迟删除均exit=1且符合预期，两次重试均exit=0，各记录4个实例全部退出；安装中断exit=197后恢复exit=0，IIS配置回到基线。原始结果为environment-result.json、environment-delayed-state-Debug/Release.json，以及environment-check目录内归档的state-fdff8436-7d95-45c5-9d19-fdc4d018341e.json和state-d276d5d7-8f78-44b3-b795-e8591f377cae.json。两项P2的专项修复验证已闭环，仍需最终整体回归。

### 🟡 P3：专项验收脚本重复Run-Check封装

**本轮不采纳重构。** b/c/d/e脚本存在少量重复进程执行封装，但均调用共享ValidationProcess监督器，未复制产品调试逻辑；当前无行为差异或阻塞。为避免仅为样式扩大本轮审核修复范围，保留现状，作为后续脚本维护项。此项不计作已修复或零发现。

## 代码质量与规范

| 不变量 | 检查依据 |
| --- | --- |
| Host不加载mscordbi/COM，实际目标位数路由 | 跨身份访问在共享Host/Engine层；ICorDebug仍仅由对应位数Engine持有 |
| 回调只入队、单调度线程、Continue配对 | 符号读拒绝重试复用现有调度轮询；真实单步、生命周期和前序回调回归 |
| 只读观察、无Getter/ToString/求值或状态修改 | 变量读取路径不扩展执行能力；MCP仍为13工具 |
| 附加目标不得terminate，异常退出尝试安全Detach | Service EOF/Host退出、拒绝终止、后续业务进展和重新附加的真实断言 |
| 令牌权限作用范围和恢复 | 共用SeDebug作用域、进程诊断初始化、并发序列化和权限属性前后对照 |
| 符号身份与有界查找 | Windows PDB匹配检查、真实shadow copy、自动重试和纯内存模块明确诊断 |
| 日志与通信边界 | stdout仅协议；日志不存目标变量值；测试HTTP仅回环，调试协议stdio/Named Pipe |
| 自有环境恢复 | 清单/目录标记/路径检查、SCM和WAS进程退出确认、配置基线、总期限和finally清理 |

## 测试与覆盖率

待最终运行后录入。使用VSTest内置Microsoft Code Coverage采集单元与现有真实权限/IIS矩阵，通过[可选采集入口](../../eng/collect-stage3-4-coverage.ps1)启动[测试harness](../../tests/FxDbg.ServiceIisCoverageTests/RealMatrixCoverage.cs)，复用原矩阵和发布产品；不另建调试逻辑。每次结果使用独立目录，并要求新Cobertura包含Host、x86/x64 Engine及Interop程序集，避免把harness自身执行误当产品覆盖率。

审核覆盖率范围为固定基线以来**新增/修改的生产C#可执行行**，合并各程序集、配置和真实矩阵的命中；同时单独报告所采集生产程序集的总体行覆盖率与未覆盖函数。不得把变更覆盖率称为全仓覆盖率。采集能力依据[Microsoft官方覆盖率配置](https://learn.microsoft.com/en-us/visualstudio/test/customizing-code-coverage-analysis?view=visualstudio)。

## 完成结论

暂不能标记父项与3-4f完成：待两项修复的真实管理员反例、最终Debug/Release专项及包含阶段2/1/0的完整回归、覆盖率核查和恢复证据齐全后更新。
