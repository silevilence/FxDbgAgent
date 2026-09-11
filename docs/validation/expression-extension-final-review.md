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

## 2026-09-11 增量审核修复（基线 a7914a9）

本节对应用户后续提供的7项非阻塞意见；上述全量验收及569/598覆盖率仍仅证明原被测子树，不能作为本次修改的实测证据。本次逐条核实结果为：完全采纳4项、部分采纳2项、不采纳1项。没有需要用户另行决策的事项。

| 编号 | 意见 | 决策 | 处理与依据 |
| --- | --- | --- | --- |
| Standards-1 | 字段枚举重复及不同上限错误码 | 部分采纳 | 在[NativeVariableValue.cs](../../src/FxDbg.Interop/NativeVariableValue.cs)提取AddFields，共用EnumFields/GetFieldProps/CloseEnum、64项缓冲和10000字段上限；变量树传取消检查，表达式传MetadataProbe。不统一两者错误码：ADR-005要求表达式256探针先行限流，不能为追求同码绕过预算。真实Huge用例验证表达式在第257次探针执行前拒绝，而变量树仍能分页读取第256～260个字段。 |
| Standards-2 | 固有函数元数三处声明 | 完全采纳 | [ExpressionIntrinsics.Arity](../../src/FxDbg.Core/Evaluation/ExpressionIntrinsics.cs)成为本次扩展16个固有函数的唯一支持名单与元数来源，解析器和求值器共同使用。增加16组未执行分支的过量参数测试；既有合法元数/结果测试保留，原7个固有函数契约不变。 |
| Standards-3 | MemberCatalog与EvaluationBudget参数簇、元数据重取 | 不采纳 | MemberCatalog按停止代次缓存，EvaluationBudget按单次求值持有取消和截止时间，两者生命周期不同；现有显式参数避免把过期预算放入缓存。实例和MemberCatalog均未持久持有MetaDataImport，枚举方法中的metadata只是局部变量。属性可能在祖类或其他模块声明，ProveProperty必须从property.Type取得正确模块元数据，不能用接收者模块替代。没有重复查询导致预算/耗时失败的证据，新增上下文/元数据缓存超出本次最小修复需要。 |
| Standards-4 | 16字节IL上限散布 | 完全采纳 | [TrivialGetterProof.MaximumIlBytes](../../src/FxDbg.Core/Evaluation/TrivialGetterProof.cs)统一预算、解码器、方法头检查和Interop拒绝文案；数值仍为16。补充16/17字节头边界，既有单方法及累计预算测试保留。 |
| Spec-1 | Dictionary.Count与ROADMAP表述矛盾 | 完全采纳 | 按用户要求原地修正[ROADMAP](../../ROADMAP.md)已勾选行：List/Queue/Stack的纯字段Count通过，Dictionary.Count属于计算型拒绝矩阵；不放宽IL白名单，不改写历史验收结果。 |
| Spec-2 | 无关MethodImpl误拒继承getter | 完全采纳 | 先加入UnrelatedImplementationModel，经中间祖类继承VirtualProofBase.Value，并显式实现无关接口Run；旧实现真实Debug x64复现expression_name_not_found。删除任意MethodImpl直接拒绝，保留逐声明名称检查及隐式覆盖扫描。修复后该属性返回13，OddVirtual/ImplicitVirtual仍拒绝，副作用计数和Continue计数均不变。 |
| Spec-3 | 十六进制L溢出行为 | 部分采纳 | 采纳十进制/十六进制L行为不一致的事实，不采纳将合法十六进制L改为语法错误的建议。修正十进制路径允许long/ulong，新增两种进制的long上界、上界+1、ulong上界及越界测试，同步工具文档和技能副本。 |

Spec-3的关键依据：[微软整数类型文档](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types#integer-literals)明确L后缀按long、ulong依次选型，超出ulong才产生CS1021。本机.NET SDK 10.0.301编译器对`(0xffffffffffffffffL).GetType()`与`(18446744073709551615L).GetType()`均输出System.UInt64，对`(9223372036854775807L).GetType()`输出System.Int64。修复前新增十进制边界测试2项失败，均在原Parser.Next抛ExpressionSyntaxError，证明应修正十进制路径。未改变已明确文档化的无后缀十进制int/long/ulong子集规则。

### 增量修复验证

被测生产子树固定为`6055ed4d8e838532c82a598aca7a4cbd1da2e0cc`。`eng/verify-expression-extension.ps1`双配置默认入口通过，本轮上下文为`2e38d3ed7cdd4aebb902296bcff3a72e`；阶段4-1/4-3共28项监督步骤（含准备复用）均退出0，覆盖Debug/Release、x86/x64、原生/MCP/DAP/CLI的求值和条件断点。每配置完整普通单测348项通过、0失败/跳过；最终884项准备产物SHA256核验通过，输入未变。

随后单独采集Debug/Release普通单测（各348项）及Debug真实四入口求值/条件断点harness（6组，内部包含双架构），全部通过、0跳过。仅合并本轮3个主XML附件，未重复计入TRX附件副本。相对a7914a9，本次6个生产文件的变更可执行行**39/39（100%）**，满足≥90%门槛；这是行覆盖率，不是分支覆盖率。309项专项输入在采集前后SHA256均一致；Fx40目标继续排除在插桩之外。

证据：[精简结果及原件哈希](expression-extension-final-evidence/followup/followup-result.json)、[专项输入](expression-extension-final-evidence/followup/inputs.sha256)、[覆盖率输入与源码子树](expression-extension-final-evidence/followup/coverage-inputs.json)、[变更行覆盖率](expression-extension-final-evidence/followup/coverage-changes.json)、[汇总脚本](expression-extension-final-evidence/followup/summarize-coverage.ps1)。新增机器摘要最大约14KiB；XML、TRX、日志、负向复现及编译器对照源码仅保存在本机`artifacts/expression-review-followup/`，不作为已上传制品。原始真实场景日志已复制到该目录的`real-logs/`，避免后续harness覆盖其原路径。

复现时先运行`./eng/verify-expression-extension.ps1`，再对普通单测项目的Debug/Release和现有`expression-extension-final-evidence/coverage-harness/ExpressionCoverage.csproj`的Debug执行`dotnet test --collect "Code Coverage" --settings eng/coverage.runsettings`。真实harness需设置`FXDBG_COVERAGE_ROOT`为仓库根、`FXDBG_COVERAGE_CONFIGURATION=Debug`及`FXDBG_COVERAGE_NODE`。新一轮须为独立结果目录重新记录3个主XML路径/SHA256、输入哈希及被测子树，再运行汇总脚本；不能将新结果套用本轮清单。使用本机保留原件复核本轮覆盖率可执行：

```powershell
./docs/validation/expression-extension-final-evidence/followup/summarize-coverage.ps1 -RepoRoot (Get-Location).Path
```

本次范围内静态复核无遗留阻塞：没有新增目标调用、写入、ICorDebugEval或Continue，原错误码保持不变，两组工具文档与技能副本逐字节一致。本轮未重新启动独立审核代理，也未重跑Service/IIS、真实VS Code或阶段0～4整套验收；它们的上次完整通过记录仍仅对应本报告前述历史源码子树。
