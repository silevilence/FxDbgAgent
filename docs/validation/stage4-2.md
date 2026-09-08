# 阶段4-2：异常类型过滤与快速审核

日期：2026-09-08。基准为ROADMAP阶段4-2、ADR-004及前一任务提交5ed00cf。阶段4-4继续保持不实施决策。

## 实现与边界

Core的ExceptionStopConfiguration提供精确类型、按点边界命名空间前缀、真实基类链匹配及统一解析；配置不可变且先验证后替换。省略rules且true保持原全first-chance，[]或false恢复仅未处理异常。原生回调仍只入队；Interop在原调度线程读取异常类型，未匹配时沿原Continue配对路径继续，不发布停止、不改变用户停止代次。未处理异常始终停止，消息只读System.Exception._message。类型读取失败保守停止并给脱敏诊断。

CLI、Engine、MCP、DAP共享过滤模型；MCP新增debug_configure_exceptions，现15工具，原13工具保持兼容。DAP声明支持firstChance条件选项，并遵守filters与filterOptions相加的OR语义。规则最多64条、名称1024字符、基类链128层，未引入目标求值、反射或自定义格式化。

## 快速审核

| 方面 | 检查与结论 |
| --- | --- |
| 需求满足 | 三种类型关系、动态更新、清空/关闭、旧开关语义、运行操作连续性及四入口一致性均有真实样例 |
| 代码质量 | 规则原子替换；未匹配无停止事件；类型不可读取时保守停止；不增加目标状态写入 |
| 代码规范 | Core定义规则、Interop封装COM、单调度线程执行，协议入口仅转换；stdout仅协议 |

🔴 已修阻塞：真实MCP高频抛出测试暴露持续命令可饿死回调泵。确定性单测在修改前失败（8个排队命令均未让回调执行），修改后通过；调度器每个命令后让回调泵执行一次，保持原线程及Continue逻辑。原生类型为空的异常读取边界同样改为保守停止，避免误忽略。

🟡 无额外风格重构。首次测试驱动中CLI测试参数拼写/必需session遗漏已更正，不作为产品问题。完整覆盖率及独立双轴审核在阶段4全部任务完成后执行，本快速审核不宣称已达到最终覆盖率阈值。

## 验证与证据

专项命令：`./eng/verify-stage4-2.ps1`，默认Debug/Release、x86/x64、原生/MCP/DAP/CLI。每个外部进程有超时与退出清理监督，执行前后比较输入哈希。14项检查全部通过，Debug/Release各206项单元测试，输入哈希前后相同。见[结果](stage4-2-evidence/stage4-2-result.json)、[哈希](stage4-2-evidence/stage4-2-inputs.sha256)及同目录原生/MCP/DAP/CLI日志。原生日志记录500次未匹配异常608～1719ms、队列采样峰值0～1；MCP含状态轮询耗时12398～13603ms，均在20秒测试界限内。

真实样例包含继承、同前缀不同命名空间、被捕获异常、500次高频未匹配异常、运行中规则替换、清空、全first-chance兼容、未处理停止及副作用计数。原生逐次断言Continue配对并采样队列峰值；MCP断言运行中更新与未匹配异常不会完成operation或产生stop；DAP断言条件与无条件OR行为及空配置；CLI验证相同规则语法和行为。

补充回归：双架构Debug/Release的MCP evaluation、execution（含100次continue/pause、并发会话、单步和异常），以及Debug双架构完整DAP protocol矩阵均通过，日志见同一证据目录。最终Service/IIS、真实VS Code及全部阶段2/1/0在阶段4-3后统一复验。`git diff --check`通过。

可否标记完成：可以。快速审核无遗留阻塞；已修调度公平性问题有修改前失败和修改后通过的证据。覆盖率与独立双轴审核待最终执行。

提交前仅去除了docs/dap.md、CliCommandTests.cs、verify-stage4-2.ps1及exception-filters.js的多余文件尾空行，正文逐字相同；原验收哈希保留，前后哈希和比较证明见[格式记录](stage4-2-evidence/final-formatting.json)。
