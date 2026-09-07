# 阶段3-4整体完成审核

审核日期：2026-09-07。固定实施基线：`cda481eea0d3ac1d4b27bab2ba858838a844c7ec`；首次独立审核目标：`5384613`。本报告覆盖3-4a～3-4f整体，逐项开发期间的快速审核不替代本报告。用户已授权修复阻塞项、复核及本地提交，不推送。

**最终结论：可标记3-4父项及3-4f完成。** 冻结实现`a0b4a2c6851b25414d247da75bc0bff755eccea5`的完整Debug/Release验收于2026-09-07 08:23:55Z通过，包含Service/IIS、3-1/2/3/5、真实VS Code和阶段2/1/0。独立审核无遗留阻塞，保留1项非阻塞P3；变更生产可执行行覆盖率140/155（90.32%）。

## 需求满足

| 任务 | 对照要求及证据 | 审核结果 |
| --- | --- | --- |
| 3-4a | [真实夹具](stage3-4a-fixtures.md)：net40双架构Service、完整IIS/WebForms、延迟模块及Windows PDB；SCM LocalService/Session 0；只读预检、专属资源、两轮部署清理和中断恢复 | 两项P2修复后，真实反例及最终双配置回归通过 |
| 3-4b | [权限矩阵](stage3-4b-permissions.md)：管理员和受限令牌、SeDebug启用/恢复、同用户及并发会话、错误语义、双架构Host/Engine路由 | 满足，最终双配置14个监督步骤通过 |
| 3-4c | [服务矩阵](stage3-4c-services.md)：源码/栈/变量、三种单步、拒绝terminate、EOF/Host异常退出、SCM重启和显式重新附加；业务心跳验证分离后进展 | 满足，最终双配置18个监督步骤通过 |
| 3-4d | [IIS符号矩阵](stage3-4d-iis-symbols.md)：准确worker选择、实际影子路径、业务和动态页面Windows PDB、缺失/失配/读取拒绝后恢复 | 满足，最终双配置14个监督步骤通过 |
| 3-4e | [IIS生命周期](stage3-4e-iis-lifecycle.md)：首次加载pending、多域身份/卸载重建、陈旧引用拒绝、内存模块诊断、重叠回收及显式新PID附加 | 满足，最终双配置12个监督步骤通过 |
| 3-4f | [总验收报告](stage3-4-service-iis.md)：一键专项/完整脚本、显式跳过只通过子集、MCP/CLI/DAP/真实VS Code、用户及随包文档同步、源码哈希与清理证据 | 整体审核、覆盖率、专项及完整回归通过 |

## 独立Standards与Spec审核

按code-review流程由独立Standards与Spec代理并行只读审核固定基线以来的变更，再由根代理逐项判断并修复。Standards初审无P0/P1/P2和硬性规范违反，发现1项P3；Spec初审发现2项P2。两位代理对修复和覆盖率采集入口再次只读复核，未发现新增阻塞；静态复核不替代真实管理员验收。

| 审核轴 | 初审 | 修复及实证复核后 |
| --- | --- | --- |
| Standards | P0/P1/P2：0；P3：1 | 阻塞0；保留1项非阻塞维护建议 |
| Spec | P2：2 | 两项均经独立复核及真实双配置反例关闭，遗留阻塞0 |

### 🟠 P2：Preflight缺少目录ACL、具体身份和计划配置变更

**完全采纳。** [环境脚本](../../eng/setup-stage3-4-environment.ps1)的Preflight现在报告部署目录或最近现存父目录的Owner/SDDL、调用者/LocalService/双应用池身份及SID、服务/池/站点/回环绑定和ACL授权计划。尚未创建或无权查询的项目明确标记unknown及原因，保持只读。非管理员预检已实际执行；管理员结构断言纳入[3-4a验收](../../eng/verify-stage3-4a.ps1)。

### 🟠 P2：清理只接受删除命令，未确认实际资源和进程退出

**完全采纳。** 清理通过句柄和创建时间固定自有SCM服务及WAS worker进程实例，停止/删除后有界等待这些实例退出，并确认服务、站点、应用池及worker记录均消失，之后才删除部署目录并记录removed。超时返回失败并保留资源清单；不强杀附加目标。3-4a新增持有ServiceHandle的真实延迟删除反例：首次清理必须失败且不得标记removed；释放句柄后重试必须成功。

真实反例首次暴露重试中的1072处理和测试句柄释放问题，已继续修复：1072仅视为pending，仍等待实际删除；测试显式Dispose取得的SafeServiceHandle；恢复清单保留已退出的原进程实例。独立Spec复核无新增阻塞。06:21:34Z专项及本轮07:41:36Z环境回归均通过：两次延迟删除exit=1符合预期，重试exit=0，正常清理各记录4个实例全部退出；安装中断exit=197后恢复exit=0，IIS配置回到基线。最新反例、正常清理和最终中断恢复清单均保存在[证据目录](stage3-4-final-evidence/README.md)。

### 🟠 P2：前序回归暴露启动入口分离后目标无业务进展

**完全采纳并修复。** 首次完整回归在阶段1入口测试失败，独立压力两次复现Engine退出0而目标仍挂起。`a0b4a2c`加入受限于初始launch入口的主模块加载就绪条件，维持原4秒清理期限及Continue配对。候选复核中两项失败重试/ExitProcess状态遗漏也已修复并独立复核；双架构各200次压力和增强入口测试通过。覆盖采集另发现目标FileLoadException，改为只对调试器采集、排除Fx40夹具探针，保留全部生产程序集。三轮103项单元、最终重新采集和完整双配置回归通过；没有延长业务断言或跳过失败用例。详见[诊断及修复记录](stage3-4-entry-cleanup.md)。

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

最终完整批次为`8183476133644ecd9d46e7b5114573bc`，07:31:35Z开始、08:23:55Z结束，全部通过。最终归档核对88份监督日志及对应进程记录的批次时间与哈希；正常步骤退出0，延迟删除与中断安装反例分别按预期退出1/197，无超时或强制清理，记录的进程均已退出。269项源码/测试/脚本/技能等输入前后一致，清单SHA256为`B0DA607C16DFA2B142AAA7BECFE0B738ACAA849A706AFECA7387DFCAC4C53D39`。完整机器结果、专项、前序回归及日志汇总见[最终证据](stage3-4-final-evidence/README.md)。

使用VSTest内置Microsoft Code Coverage采集单元与现有真实权限/IIS矩阵，通过[可选采集入口](../../eng/collect-stage3-4-coverage.ps1)启动[测试harness](../../tests/FxDbg.ServiceIisCoverageTests/RealMatrixCoverage.cs)，复用原矩阵和发布产品；不另建调试逻辑。每次结果使用独立目录，并要求新Cobertura包含Host、x86/x64 Engine及Interop程序集，避免把harness自身执行误当产品覆盖率。

审核覆盖率范围为固定基线以来**新增/修改的生产C#可执行行**，合并各程序集、配置和真实矩阵的命中；同时单独报告所采集生产程序集的总体行覆盖率与未覆盖函数。不得把变更覆盖率称为全仓覆盖率。采集能力依据[Microsoft官方覆盖率配置](https://learn.microsoft.com/en-us/visualstudio/test/customizing-code-coverage-analysis?view=visualstudio)。

最终覆盖率汇总锁定[六份成功采集的路径及SHA256](stage3-4-final-evidence/coverage-inputs.json)：最终103项单元测试、权限/IIS各Debug/Release真实矩阵，以及1项[自有进程ACL审核实验](stage3-4-final-evidence/README.md)。两项补充采集在完整回归后重新执行，ACL实验直接引用本轮发布Host并核对DLL哈希，验证早期架构查询拒绝及ACL恢复；不混入旧提交或失败采集结果。

| 范围 | 已覆盖 / 可执行行 | 行覆盖率 | 结论 |
| --- | ---: | ---: | --- |
| 12个生产变更文件的新增/修改行 | 140 / 155 | **90.32%** | 达到本次变更审核默认90%阈值；没有未测量的变更C#文件 |
| 六份采集中生产源码的全部测量行 | 3635 / 4990 | 72.85% | 包含未修改的既有代码，仅报告事实，不宣称全仓90% |

以下是仍含未覆盖变更行的函数；函数覆盖率按该函数所有测量源码行计算，与上表变更行分母不同。未覆盖部分主要是原生API错误与分层防御性权限转换；不能由上层已返回access_denied推断每层异常转换都执行过。

| 函数 | 函数覆盖行 / 测量行 | 覆盖率 | 未覆盖的变更路径 |
| --- | ---: | ---: | --- |
| EngineTargetValidator.ValidateAttachRuntime | 22 / 42 | 52.38% | 第81行Win32权限异常转换 |
| Engine Program.Main的非server清理委托 | 0 / 4 | 0% | 第78行；产品serve会话的清理路径已实测，不能替代这个入口 |
| Engine Program.AttachDenied | 0 / 2 | 0% | 第129～130行额外COM/Win32权限转换 |
| FrameworkDebuggerBootstrap.AttachProcess | 8 / 11 | 72.73% | 第56～58行ICorDebug E_ACCESSDENIED转换 |
| FrameworkDebugSession.SynchronizeAndDetach | 52 / 110 | 47.27% | 第212、234行两种失败后的Continue重试；各生成委托0/1，未与外层重复计行 |
| TargetProcessLifetime构造器 | 5 / 6 | 83.33% | 第18行DuplicateHandle原生失败 |
| TargetProcessLifetime.HasExited | 5 / 6 | 83.33% | 第27行原生等待失败 |
| TargetProcessLifetime.ReadImagePath | 6 / 7 | 85.71% | 第36行主映像路径查询原生失败 |
| WindowsDebugPrivilege.NativeAdjustment.Acquire | 13 / 16 | 81.25% | 第68、73、83行令牌打开/权限查询失败及异常清理 |

完整文件级数据、函数明细和可重复汇总脚本见[证据快照](stage3-4-final-evidence/README.md)。最终Service/IIS专项于08:02:09Z通过，a～e全为exit=0、skipped为空；真实权限、服务、影子复制、动态模块/多域/回收、CLI/DAP/VS Code及恢复均通过。随后包含阶段2/1/0的完整回归同样通过。历史的输入变动失败与入口停止失败批次保留在artifacts，均未作为本轮通过依据。

## 完成结论

可标记3-4父项及3-4f完成。需求满足、代码质量、规范、覆盖率和真实Windows双配置完整回归均有证据，阻塞项已修复并独立复核。保留1项非阻塞P3脚本维护建议，不宣称零审核发现；不将本机性能或VS Code证据推广为其他硬件或Cursor实测。
