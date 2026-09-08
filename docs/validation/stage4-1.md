# 阶段4-1：受限只读解释求值与快速审核

日期：2026-09-08。需求基准为ROADMAP阶段4-1及用户已批准的ADR-004，变更起点为41c2274。阶段4-4保持既有不实施决策。

## 实现与边界

Core提供闭合语法、类型化标量、预算及解释器；Interop只读原生基础值、字段、字符串及真实维度/下界数组，结果通过既有VariableReader生成并共享当前停止的引用表。Engine单线程接收evaluate，队列期限取调用与计算期限较小值；没有ICorDebugEval、目标方法、Getter、写入或额外Continue。共享Host为观察取消保留实际调用期限和并发名额；CLI/MCP/DAP只做参数及结果转换。MCP新增debug_evaluate，原13工具兼容；DAP声明hover求值并支持watch/repl的同一受限子集。

该实现不支持任意C#、用户运算符/转换、decimal、指针、赋值、循环或反射。Math/String/Array仅为调试器内白名单固有操作。长字符串参与计算前拒绝超限，展示截断不参与计算；原生COM卡死的隔离和目标保活限制仍适用。契约见[工具文档](../mcp-tools.md)，安全依据见[ADR-004](../architecture.md#adr-004阶段4受限表达式安全边界2026-09-08已接受)。

## 专项验证

命令：`./eng/verify-stage4-1.ps1`（Debug和Release，不跳过）。本轮14个受进程超时监督的检查均exit=0，输入哈希前后相同。证据快照见[专项结果](stage4-1-evidence/stage4-1-result.json)及[输入哈希](stage4-1-evidence/stage4-1-inputs.sha256)。完整运行日志在artifacts/stage4-validation下。

| 范围 | Debug | Release | 断言 |
| --- | --- | --- | --- |
| 构建及MCP/DAP发布 | 通过 | 通过 | 零构建错误/警告，完整双架构Engine依赖 |
| 全部单元测试 | 187/187 | 187/187 | 含84项解释器数值、语法、短路、预算及超时/取消测试 |
| 原生x86/x64 | 均通过 | 均通过 | 每架构15种精确求值、原始值读取、字段、循环引用、多维与负下界数组、优化局部错误一致性 |
| MCP x86/x64 | 均通过 | 均通过 | 真实stdio/schema，字段、算术、对象分页、输入/求值错误和过期帧 |
| DAP及CLI x86/x64 | 均通过 | 均通过 | 真实协议/独立CLI进程、watch与对象分页、Getter拒绝、过期句柄 |

原生测试在求值前后检查同一CurrentStop及原ContinueStopCoordinator的StopCount/ContinueCount，证明求值无额外停止/继续。目标自身在恢复后检查实例字段、静态字段、数组内容、自引用及副作用计数，正常退出或写出ok证据。超时与取消分别检查错误码并继续调试；原生超时测试使用已过期预算，慢读取由可控测试上下文验证，不宣称注入真实不可中断COM故障。

## 快速审核（仅修阻塞项）

| 维度 | 结果 |
| --- | --- |
| 需求 | 已实现ADR明确批准的解释方案、资源边界及三入口，原CLR Eval要求已获批准替换 |
| 安全与执行纪律 | 仅Interop触碰CLR，所有公开读取检查stopped；回调与Continue路径没有新增求值调用；错误信息不回显表达式/值/堆栈 |
| 兼容性 | 原13工具保留，新增工具具备完整schema；字段、显示限制与引用失效同源 |
| 错误恢复 | 解析、禁止操作、类型、索引、算术、预算、不可获取/优化掉、超时/取消及过期帧分别验证 |
| 兼容性回归 | 最终Debug/Release阶段2/1/0完整通过；前一批次阶段3及真实Service/IIS、VS Code已通过，最终源码的整体回归仍按计划在全部任务后重跑 |
| 覆盖率 | 本次快速审核不运行完整覆盖率流程；本轮全部任务后的完整审核必须测量变更生产行覆盖率并报告缺口 |

已修复：Engine排队期限也必须受evaluationTimeoutMs约束，不能只在出队后检查；测试中DAP必须等待既有terminated事件并核对目标oracle；Release x64的shifted/oversized参数仅GC.KeepAlive时，既有变量读取同样返回CORDBG_E_READVIRTUAL_FAILURE，改为断点后参与真实状态校验以保持活跃，Release优化继续开启。另保留localNumber优化掉/不可获取与求值错误一致性的断言，没有通过关闭优化掩盖问题。临时诊断输出已移除。

🟠 快速审核追加期限阻塞已关闭：预算独立验证evaluationTimeoutMs后取与commandTimeoutMs较小值，拥有可释放的期限取消源，将Token传递到原生枚举及结果展开；归一化底层取消并保留用户取消/超时分类。新增较短外层期限、Token实际触发、慢读取及非法期限不能被clamp隐藏的测试，84项表达式测试通过。

🟠 前序回归暴露既有MCP初始化竞态：首次完整批次于01:55:32Z失败，阶段3各项及完整Service/IIS均通过，但阶段2生命周期中合法的立即tools/list偶发被拒绝。结果及失败日志保存在[first-regression](stage4-1-evidence/first-regression/)。100次原始连接及200次官方SDK连接未复现；设置DOTNET_PROCESSOR_COUNT=1并将通知/请求合并写入后100次中81次失败。与固定SDK源码的[消息分派逻辑](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/src/ModelContextProtocol.Core/McpSessionHandler.cs#L196)一致：进入过滤器前ForceYielding，顺序不再等同于接收顺序。修复由MCP Host在有界stdio接收边界记录逐条工具请求的初始化许可，过滤器只检查该快照；不修改SDK、协商或调试逻辑。新增20轮单逻辑处理器、合并写入、提前通知及前后请求顺序回归已通过，最终完整阶段2/1/0复验已通过。

两个阻塞修复后的14步专项矩阵再次通过，输入哈希前后一致，证据快照已更新；同一单逻辑处理器复现命令现为100/100通过。完整阶段2/1/0复验已通过，快速审核无遗留阻塞。最终覆盖率、完整独立审核与包含Service/IIS和VS Code的阶段4全回归在全部任务完成后对最终源码执行；首次批次阶段3的成功子结果不能冒充最终源码全量通过。

🟠 第二批次阶段2复验的Debug及MVP通过，Release另暴露测试信号发布竞态（已关闭）：ObservationSuite看到ready存在后，在目标File.WriteAllText尚未关闭写句柄时ReadAllText抛出共享冲突，证据见[ready-race](stage4-1-evidence/ready-race/)。仅调整测试等待器对Win32共享/锁冲突32/33在原期限内重试；其他I/O错误仍失败，完整secret与工作目录内容断言保留。增加持有独占写句柄、必须保持等待、释放后成功的确定性反例，无产品调试逻辑变化。

## 最终快速审核结论

可原地勾选4-1并本地提交。最后一轮`./eng/verify-stage2.ps1`在2026-09-08 02:35Z完成，Debug/Release全部通过，包含原13工具、新增初始化顺序回归、Console/WinForms、执行/资源/生命周期、MVP及阶段1/0；没有跳过，源码哈希前后一致。[最终回归证据](stage4-1-evidence/final-regression/stage2-result.json)与[源码哈希](stage4-1-evidence/final-regression/stage2-inputs.sha256)可复核。

[专项输入对照](stage4-1-evidence/final-regression/specialty-input-comparison.json)确认专项之后生产代码未再变动；唯一测试差异是ObservationSuite就绪文件修复，已被最终双配置完整回归覆盖。新旧失败批次均保留，不能将阶段3此前成功子结果称作最终源码的全量通过。按用户要求，本项只做快速审核；变更覆盖率、两轴独立完整审核及最终源码Service/IIS、VS Code和全前序回归将在4-2/4-3完成后进行。
日志快照仅移除PowerShell转录的行尾空格以通过仓库空白检查；原始日志仍保留在artifacts对应目录。
