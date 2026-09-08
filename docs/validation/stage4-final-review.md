# 阶段4整体完成审核

审核日期：2026-09-08。固定实施基线为 `41c2274c49b0e63fa7a70f96964a158e38439c4a`，最终被测实现为 `2aa8a9d4067646fecf0707760980ca629312396a`。按照用户批准的 [ADR-004](../architecture.md) 顺序实施，逐项快速审核、仅修复阻塞项、原地勾选并本地提交；不推送。

最终结论：阶段4全部任务可标记完成（4-4为不实施决策闭环）。逐项快速审核、独立双轴完整审核、阻塞修复复核、双配置专项及管理员完整回归全部完成，遗留阻塞为0。

## 需求满足

| 任务 | 实现与实证 | 结果 |
| --- | --- | --- |
| 4-1 受限表达式求值 | [任务报告](stage4-1.md)：共享 Core/Engine 解释器、原始字段/数组读取、有界资源、包含排队时间的取消/期限；CLI、MCP、DAP 共用后端 | Debug/Release、真实 x86/x64 四入口专项通过 |
| 4-2 first-chance 类型过滤 | [任务报告](stage4-2.md)：精确类型、命名空间段边界、真实继承链，运行中更新/清除；无法识别时保守停止；500 次噪声压力及 Continue 配对 | Debug/Release、真实 x86/x64 四入口专项通过 |
| 4-3 条件断点 | [任务报告](stage4-3.md)：布尔表达式与命中次数 AND、真实命中计数、未达阈值不求值、false 隐藏停止、错误保守停止；模块重载与 DAP 列表保留 | 最终 10 个条件场景、Debug/Release、真实 x86/x64 四入口专项通过 |
| 4-4 HTTP/远程宿主 | [ROADMAP 决策](../../ROADMAP.md)：已决定不实施，本阶段只在本机运行，仍为 stdio/Named Pipe | 决策闭环；没有增加 HTTP 调试或远程宿主 |

三个实现任务分别提交为 `5ed00cf`、`fbbec36`、`1c73d11`，逐项报告保留当时测试数量与快速审核记录。最终数值修复提交为 `106b747`。最终专项是在全部阻塞修复后重新执行，旧任务报告中的 187/206/231 项测试数不替代本报告的最终 233 项。

## 独立 Standards 与 Spec 审核

按照 code-review 流程，由两个独立只读代理分别审核规范与需求；根代理独立判断、修复及执行测试，随后两轴复核。完整记录见 [独立审核](stage4-final-evidence/independent-review.md) 与 [逐条修复决定](stage4-final-evidence/review-fixes.md)。

| 审核轴 | 初审结果 | 复核结果 |
| --- | --- | --- |
| Standards | 硬违反/阻塞 0，非阻塞维护建议 2 | 数值修复无新增阻塞，维护建议保留 |
| Spec | 🟠 P2 阻塞 1 | 已修复并独立复核关闭，遗留阻塞 0 |

🟠 P2 完全采纳：小整数 Math 重载原先错误地统一提升为 int，使 `Math.Abs(short.MinValue)` 返回 32768，条件断点可能静默跳过。现在精确匹配 sbyte/short 的 Abs 及同类型小整数 Min/Max，保留返回类型与溢出；其他已声明的基础提升边界同步文档和随包副本，不承诺完整 C# 重载解析。新增两项单元反例先失败再通过，相关 109 项通过；真实 `small-overflow` 条件验证算术错误保守停止及随后恢复。证据为 [红灯日志](stage4-final-evidence/math-red.log)、[绿灯日志](stage4-final-evidence/math-green.log) 和最终条件矩阵。

🟡 两项维护建议本轮不采纳重构：三个验收脚本的输入/监督封装重复，以及 BreakpointManager 多处复制断点字段。当前没有行为差异或遗漏，后续可提取公共操作；本轮不扩大阻塞修复范围。不将这两项写成已修复或“零发现”。

🟠 全回归另发现随包文档同步阻塞：主文档与 workflow 副本的 4-2/4-3 段落分别写了不同摘要，技能安装的逐字哈希检查失败。完全采纳修复，`6f497bf` 将主文档原样同步至副本；技能安装及独立 Agent 证据检查复验通过，两位审核代理补审均无阻塞，见 [复验日志](stage4-final-evidence/skill-resync.log)。此提交仅改一个 Markdown 副本，其相对于数值修复提交的历史差异见 [差异证明](stage4-final-evidence/documentation-only-fix.json)。

🟠 完整回归第二次发现卸载期间分离阻塞：排队的 UnloadModule/ExitAppDomain 在清理路径未更新模块与断点状态，导致失效原生绑定、分离超时或目标后续 AppDomain 卸载失败。完全采纳修复，提交 `2aa8a9d` 复用既有卸载处理、等待相关域完成卸载及短暂卸载静默期，并在等待 CLR 进展前成对释放自身 Stop；连续异常不会延长该期限。独立复核发现的候选修复边界均已关闭。最终生产实现通过 100 次原生双配置双架构压力、40 次 MCP 双架构多域压力，以及四种配置/架构的持续异常检查；详见 [分离修复报告](stage4-detach-recovery.md)。

## 代码质量与规范

| 约束 | 审核与测试依据 |
| --- | --- |
| 单一调试逻辑源、Host 不加载 COM | 解释与条件策略位于 Core，原始读取位于 Interop，四入口通过共享 Host/Engine；实际目标位数路由保持原有规则 |
| 回调仅入队、单调度线程、Continue 配对 | 条件/异常筛选在调度线程消费回调时执行；false 复用既有 Continue 路径；真实隐藏停止、队列峰值及生命周期断言 |
| 不执行目标代码、不修改状态 | 无 ICorDebugEval、目标 Getter/ToString、构造函数、用户运算符或转换；真实格式化计数保持 0 |
| 有界执行与明确错误 | 输入/AST/深度/步骤/读取/字符串限制，默认 250ms，取消含排队时间；非布尔、未知类型和算术错误保守停止并提供脱敏诊断 |
| 停止代次及计数 | 隐藏命中不发布停止事件或分配公共帧/变量引用；真实恢复后引用失效；重绑定保留计数，删除后重新创建重置 |
| 协议与兼容性 | 原 13 个 MCP 工具兼容，新增 evaluate/configure_exceptions 后共 15 个；stdout 只传协议，CLI/DAP/技能契约同步 |
| 生命周期 | 附加不得 terminate；超时/取消不引入额外 Continue；COM 调用不可中断与硬崩溃不保证目标存活的既有边界明确保留 |

快速审核还修复了 MCP 初始化通知竞态、测试就绪文件共享冲突及调度工作队列饥饿等实际阻塞，分别在任务报告记录；最终回归包含对应反例，不以重试掩盖失败。

## 最终专项与覆盖率

冻结最终实现后依次运行 `eng/verify-stage4-1.ps1`、`eng/verify-stage4-2.ps1`、`eng/verify-stage4-3.ps1`（默认双配置）。三个脚本各 14 个监督步骤通过，共 42 步；每个配置单元测试 233/233，真实 x86/x64 原生、MCP、DAP、CLI 全部通过。三个源码/测试/脚本/技能输入清单相同，见 [专项汇总](stage4-final-evidence/stage4-matrix/summary.json)。异常压力仍在 20 秒上限内；本机数值不推广为其他硬件的性能保证。

覆盖率使用现有 Microsoft Code Coverage 与 [配置](../../eng/coverage.runsettings)，采集双配置单元测试及 Debug 真实矩阵（11/11 理论子项通过，无跳过；包含真实卸载队列分离与持续异常噪声）。[采集 harness](stage4-final-evidence/coverage-harness/RealStage4Coverage.cs) 只调用已有验收套件，经共享 ValidationProcess 监督并限时；没有第二套调试逻辑。排除 Fx40 目标的探针注入，收集实际子进程中的 CLI、MCP、DAP、Host、双架构 Engine 及 Interop。

| 测量范围 | 覆盖行 / 可执行行 | 行覆盖率 | 判定 |
| --- | ---: | ---: | --- |
| 基线以来新增/修改的生产可执行行 | 697 / 738 | **94.44%** | 达到 ADR-004 约定的 90% 变更行门槛 |
| 三份采集中生产代码的全部测量行 | 4991 / 6099 | 81.83% | 单独报告事实，不宣称全仓 90% |

唯一没有插桩条目的变更生产文件为枚举 `FxDbgErrorCode.cs`，其变更可执行行数为 0；没有借此排除未测的可执行改动。详细分母、未覆盖变更行及函数见 [汇总 JSON](stage4-final-evidence/coverage-changes.json)、[文件 CSV](stage4-final-evidence/coverage-files.csv) 和 [全部未完全覆盖生产函数 CSV](stage4-final-evidence/uncovered-methods.csv)。后者按函数全部测量行计算，不能与变更行分母混用；包括原生元数据失败转换、取消防御路径及 CLI 帮助分支等未覆盖路径。

三份最终原始 XML 的路径、长度、SHA256 与提交固定在 [输入清单](stage4-final-evidence/coverage-inputs.json)，压缩原件见 [XML 归档](stage4-final-evidence/coverage-xml.zip)。汇总按文件/源码行合并命中，不将不同程序集或配置重复计行；全部重新采集于最终分离修复提交，没有混入更早版本的采集。可使用 [汇总脚本](stage4-final-evidence/summarize-coverage.ps1) 重算。完整采集日志见 `stage4-final-evidence/coverage-logs/`。

## 管理员全回归与恢复

首轮 `stage4-final-regression` 于 2026-09-08 04:07:26Z 启动，04:35:27Z 因技能副本文档差异失败；此前 Service/IIS 双配置已通过，保留 [失败批次证据](stage4-final-evidence/failed-regression/stage3-result.json)，不将本轮计作全量通过。第二次 `stage4-final-rerun` 在多域分离遇到真实竞态后失败，证据与修复见上文。全部阻塞修复并重新完成专项和覆盖率后，2026-09-08 05:43:19Z 从头启动 `stage4-final-accepted`，使用目标开始前获授权的同一管理员后台进程；执行期间不请求新的 UAC。命令如下，未使用 SkipServiceIis：

```powershell
./eng/verify-stage3.ps1 -NodePath 'C:\Program Files\nodejs\node.exe' -CodePath 'C:\Program Files\Microsoft VS Code\Code.exe'
```

最终管理员批次于 2026-09-08 06:28:58Z（本地 14:28:58）完成，退出码 0，持续约 45 分 38 秒。阶段3汇总 `passed=true`、`subsetPassed=true`、`skipped=[]`；完整 Service/IIS 汇总及环境、权限、Service、IIS符号、生命周期专项均通过；阶段2及其阶段1/0 Debug/Release全回归通过。真实 VS Code 双配置及 Service/IIS 会话通过；不宣称 Cursor 实测。

本批次归档 293 份日志、结果和进程监督记录，见 [回归清单](stage4-final-evidence/regression/manifest.json)。清单验证原始文件时间均晚于本次开始时间，记录原始/归档 SHA256；298 项输入在完整回归内保持一致，其中与阶段4专项共享的292项逐项相同，额外6项为两个IIS页面及四个根构建输入。不会把不同输入范围的清单总哈希当作同一个哈希。

五组专属环境（check/permissions/services/iis/lifecycle）状态均为 `removed`，测试验证服务、站点及应用池清理和设置恢复。监督日志无超时、无强制清理残留，所有已记录自有进程均已退出。管理员后台进程完成固定动作后已关闭，PID 15332 不再存在，见 [关闭记录](stage4-final-evidence/admin-closed.json) 与 [退出核对](stage4-final-evidence/admin-exit-check.json)。本次执行没有再次请求 UAC。

## 完成判定

独立审核与实际回归发现的3类阻塞全部修复并复核，遗留阻塞为0；两项非阻塞维护建议保留。阶段4专项、94.44%变更生产行覆盖率、完整管理员Debug/Release回归及资源恢复全部通过。ROADMAP各项原地勾选，逐任务本地提交已完成；最终报告与证据另作本地提交，不推送。
