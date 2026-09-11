# 受限表达式扩展最终验收与独立审核

2026-09-11：全量管理员验收与独立审核均通过，本组四项可标记完成。基线 `f831779744ea743ebd35620652b4b244c4efb1e7`，需求为 ROADMAP 本组四项及已批准 ADR-005。前三项已按开发、任务内快速审核、修复、原地勾选、本地提交完成（263ec32、0cd7011、d09d7ff）。

## 范围与证据

| 任务 | 实现及任务内验收 |
| --- | --- |
| 语法与固有函数 | [第一项报告](expression-extension-1.md)：三元、位运算、十六进制、unchecked转换、裸名回退及String/Math/Array固有函数，条件断点继承 |
| IL证明属性代读 | [第二项报告](expression-extension-2.md)：精确IL白名单、真实方法头、token/槽匹配、预算及停止缓存；最终补充虚分派与返回类型修复 |
| 诊断自愈 | [第三项报告](expression-extension-3.md)：最多8个元数据名称建议、拒绝原因、七类错误码不变及真实日志脱敏 |
| 全量验证与独立审核 | 新`eng/verify-expression-extension.ps1`串行执行4-1/4-3，已接入`eng/verify-all.ps1`，与4-2及完整阶段3/2/1/0共享单轮准备 |

独立扩展入口双配置通过，产物884项SHA256核验完成、输入未变化，见[入口结果](expression-extension-final-evidence/standalone-result.json)、[核验摘要](expression-extension-final-evidence/standalone-verification-result.json)。重复准备明确标记REUSED；真实原生、MCP、DAP、CLI场景仍全部执行。

## 独立审核与修复

按code-review技能使用两个独立只读代理并行审核Standards和Spec。主线程分别核实后完全采纳三项发现：Trim分配前预算；无Property行的显式/隐式虚覆盖；字段getter声明返回类型。分别增加边界或真实Framework用例，原审核者复核关闭，最后又补审组合后的Interop改动。

Standards原1项、已修复1项、遗留0项；Spec原2项、已修复2项、遗留0项。没有以单轴通过代替另一轴，完整结论见[独立审核记录](expression-extension-final-evidence/independent-review.md)。无新增目标函数、Getter、Eval、写入或Continue；虚槽不能证明时保守拒绝，getter始终不执行。

## 代码规范核对

| 规范 | 结论 | 依据 |
| --- | --- | --- |
| COM隔离与单一调试逻辑 | ✅ | 新元数据/IL读取仅在Interop；CLI/MCP/DAP调用共享Engine |
| 只读与回调纪律 | ✅ | 无目标Eval/Getter/写入/新增Continue；真实副作用及停止配对断言 |
| 预算与缓存 | ✅ | 原预算保留，新增元数据/IL预算与诊断步骤计费，停止缓存失效 |
| 错误与日志 | ✅ | 七个分类不变，仅元数据名称例外；原生/协议消息及真实审计扫描 |
| 证据与文档 | ✅ | 工具与技能副本同步，原始产物留artifacts，最终生产子树和测试输入固定 |

代码质量原有3项🟠 Important问题均已修复并独立复核；未遗留🔴 Critical或其他阻塞。覆盖率未命中函数与行号完整保留在机器汇总，未将它们删除出分母。

## 覆盖率

最终生产源码由不可变Git子树 `d46dfc7e877d9a45867f6432d3b68a5c37d08c6c` 固定；与基线的src子树比较，包含所有工作树审核修复，最终任务提交引用同一src子树。测试/脚本输入及测量harness另以SHA256固定，避免在任务验收前提前提交。

使用Microsoft Code Coverage，排除Fx40目标注入、跟踪实际产品子进程；Debug/Release普通单测各325/325，Debug真实矩阵harness 6/6，无跳过。真实矩阵包括原生双架构、MCP、DAP/CLI的求值与条件断点。三份最终XML按源码行合并，未混入两次更早的探索性采集。

变更生产可执行行 **569/598（95.15%）**，达到≥90%门槛。所有12个变更生产文件均已插桩；其中2个只改接口/可见性，没有新增可执行行。此专项覆盖范围内整体生产行为5396/6678（80.80%），该数值不替代本任务的变更行门槛。逐文件、未覆盖函数及行号见[覆盖率汇总](expression-extension-final-evidence/coverage-changes.json)；[输入清单](expression-extension-final-evidence/coverage-inputs.json)固定XML路径、长度、哈希及源子树，[汇总脚本](expression-extension-final-evidence/summarize-coverage.ps1)可复算并拒绝缺失插桩或门槛不足。

## 全量验收

管理员64位PowerShell执行：

```powershell
./eng/verify-all.ps1 -NodePath 'C:\Program Files\nodejs\node.exe' -CodePath 'C:\Program Files\Microsoft VS Code\Code.exe'
```

最终本轮 `a4e7cdc4fd774cb481fa3203cc0ffea2` 在2026-09-11 10:26:26～11:10:07（UTC+8）完成，墙钟2620.929秒（43分41秒）；父子监督步骤有包含关系，此处只使用最外层墙钟，没有叠加嵌套耗时。

| 验收范围 | 结果 |
| --- | --- |
| 本组扩展、阶段4-1/4-2/4-3 | Debug/Release、x86/x64、原生/MCP/DAP/CLI全部通过 |
| 阶段3多域、分页、源码映射、DAP | 双配置全部通过，VSIX打包及真实VS Code通过 |
| 真实Service/IIS | 环境、权限、服务、IIS、生命周期五组双配置通过；skipped为空 |
| 阶段2 | MCP完整与MVP双配置、技能安装及冻结独立Agent证据校验通过 |
| 阶段1/0 | Debug/Release完整回归；Windows PDB、真实启动/附加、14层栈及WinDbg/SOS对照通过 |
| 输入与准备 | 332个输入文件未变化，最终1052个准备产物SHA256核验通过 |

归档中本轮175个监督步骤均符合预期：没有非预期失败、监督超时或强制清理。3个负向步骤分别预期延迟SCM删除退出1（两个配置）及安装中断退出197，实际匹配，随后清理/恢复通过。[运行摘要](expression-extension-final-evidence/run-summary.json)明确列出它们；[全量结果](expression-extension-final-evidence/full-result.json)、[最终哈希核验](expression-extension-final-evidence/full-verification-result.json)、[完整输入清单](expression-extension-final-evidence/full-inputs.sha256)可复核。

单轮复用实际验证通过：每配置1次强制解决方案构建、1次完整普通单测、MCP/DAP各1次发布；共12项准备记录、132次显式REUSED记录。Service/IIS插桩准备与普通准备仍分别执行；所有真实场景、取消/超时和故障恢复仍运行。本轮耗时不用于宣称相对历史环境的加速比例。

五组独立环境均标记removed并验证自有进程退出，详见[恢复摘要](expression-extension-final-evidence/environment-result.json)。IIS配置恢复到基线，既有默认演示服务仍运行；本次临时管理员执行器已主动关闭。

## 归档与限制

原始日志、进程/耗时记录、审计、XML、TRX与完整场景结果仅在被忽略的artifacts下。Git仅提交报告、精简摘要、哈希/归档清单和可复现harness源码。原件仅存本机，未上传外部制品存储，不能宣称其他机器已可下载。


## 结论

本组四项全部完成，可按原位置勾选并本地提交；Standards和Spec遗留阻塞均为0。最后验收结束后仅补充完成状态与证据文档，生产源码保持上述被测子树。Dictionary.Count等计算型getter、跨模块TypeRef等不能证明的字段引用继续明确拒绝；没有扩大目标代码执行权限。

929个可保留原始证据文件已复制到本机`artifacts/expression-extension/full-a4e7cdc4fd774cb481fa3203cc0ffea2/`并逐文件核验SHA256，原路径、归档路径、长度和哈希见[归档清单](expression-extension-final-evidence/manifest.json)。其中包括完整全量日志、阶段0对照、环境状态、单独扩展入口及最终覆盖率原件；Git中的机器摘要均≤64KiB，输入/归档逐文件清单按仓库规则保留。
