# 阶段3-4：Service/IIS 总验收

本轮按用户2026-09-07授权，先提交文档，再顺序开发3-4a～3-4f；每项只做快速审核、修复阻塞项并独立本地提交。整体完成审核使用实施前固定基线 `cda481eea0d3ac1d4b27bab2ba858838a844c7ec`，不推送。先前3-4跳过记录只属于2026-09-06。

## 任务证据

| 任务 | 实现及专项证据 |
| --- | --- |
| 3-4a | [可恢复环境](stage3-4a-fixtures.md)：SCM LocalService/Session 0、完整IIS、双架构、Windows PDB、部署中断恢复和配置基线 |
| 3-4b | [权限与安全分离](stage3-4b-permissions.md)：管理员/受限令牌、启用及恢复SeDebug、同用户/跨身份/并发附加和退出 |
| 3-4c | [真实服务](stage3-4c-services.md)：源码、变量、三种单步、SCM停止/重启、EOF/Host强杀后存活与重新附加 |
| 3-4d | [真实IIS符号](stage3-4d-iis-symbols.md)：影子复制、业务/动态页面、缺失/失配/读拒绝/恢复及自动重绑定 |
| 3-4e | [IIS生命周期](stage3-4e-iis-lifecycle.md)：pending动态加载、多域卸载重建、内存模块和真实重叠回收 |
| 3-4f | 一键脚本、MCP/CLI/DAP/真实VS Code、随包技能和使用指南；整体审核及完整回归的最终结果在下方记录 |

## 环境和命令

本机Windows 11 Professional 25H2（build 26200）、完整IIS 10、CLR v4.0.30319（4.8.9325，NET481REL1LAST_25H2_C）。目标为net40生成物及匹配Windows PDB；服务为LocalService，worker为ApplicationPoolIdentity，各有x86/x64进程。MCP只用stdio，Engine通信为Named Pipe；测试HTTP仅绑定127.0.0.1。

专项：管理员64位PowerShell运行 `./eng/verify-stage3-4.ps1`。完整：`./eng/verify-stage3.ps1 -NodePath <node.exe> -CodePath <Code.exe>`。均默认Debug/Release，支持单配置；完整验收包含3-1/2/3/5、VSIX、真实VS Code、阶段2/1/0及CDB/SOS对照。`-SkipServiceIis`明确是子集，不产生完整passed=true。

原始结果在 `artifacts/stage3-4-validation/stage3-4-result.json` 与 `artifacts/stage3-validation/stage3-result.json`。两份inputs.sha256记录源码/测试/脚本/技能/ASPX/config等输入，运行前后必须一致；步骤日志附进程监督清单，超时、非零退出或需要强制清理均失败。配置/资源清单及CLR实际值另记录在各独立environment目录；全局Windows组件保留，专属验收资源按清单删除，用户默认环境保留。

入口矩阵扩展3-4c：每配置16项MCP/CLI（原14项服务案例加2项IIS CLI）、4项DAP、4项真实VS Code，均验证两类目标的双架构附加、精确源码断点、分离后的实际业务进展/响应。证据文件为services-{配置}.json、dap-service-iis-{配置}.json、vscode-service-iis-{配置}.json；VS Code记录版本、原生API和协议事件，不记录变量值。

## 整体审核与结论

**已完成。** 固定基线以来的独立Standards/Spec审核、修复复核、覆盖率分析及最终Debug/Release完整回归通过。冻结实现为`a0b4a2c6851b25414d247da75bc0bff755eccea5`，完整批次`8183476133644ecd9d46e7b5114573bc`于2026-09-07 08:23:55Z结束；3-4专项于08:02:09Z通过，未跳过任何任务。269项输入前后一致，88份监督日志与进程记录核对通过，正常步骤无超时或强制清理，专属验收环境均已恢复/移除。

整体初审的预检/清理两项P2及全回归暴露的入口分离问题均修复并独立复核；遗留阻塞0，保留1项非阻塞脚本维护建议。变更生产可执行行140/155（90.32%），测量到的生产代码总体3635/4990（72.85%），不混称全仓覆盖率。详见[完成审核](stage3-4-final-review.md)与[机器证据](stage3-4-final-evidence/README.md)。阶段2独立Agent部分核验已提交的真实调用证据，不宣称本轮重跑外部Agent。

3-4f实现快速审核：2026-09-07 03:54:06Z（Debug）和03:58:20Z（Release）的入口回归通过；各102项单元、16项MCP/CLI、4项DAP及VS Code 1.136.1的4项Service/IIS会话。每轮9个监督步骤全为0，无超时或强制清理，专属资源已移除。新PowerShell脚本解析检查、Node语法检查及随包引用哈希核对通过。当时整体验收尚待完成，本条只保留快速审核的历史结果。

上述快速审核是开发时记录；最终完整回归各配置为103项单元，Service/IIS入口仍各16项MCP/CLI、4项DAP与4项真实VS Code，另包括全部权限、符号和生命周期矩阵。先前因输入变化及入口停止失败的完整尝试均保留为失败，未替代本次通过结果。

## 默认本机环境

用户保留的`Stage3-4`环境已刷新为本轮Debug夹具，部署ID为`6e4559a4-8253-47d2-bc5d-16b2076caf79`，2026-09-07 08:29:21Z独立健康复查通过。两个LocalService服务及x86/x64 IIS站点可用，地址仍为`http://127.0.0.1:58340/health.aspx`和`http://127.0.0.1:58341/health.aspx`；包含最新late.aspx与业务DLL，部署DLL哈希与本轮构建一致。当前PID应重新查询，不使用报告中的历史PID。使用与移除见[环境指南](../service-iis-environment.md)。
