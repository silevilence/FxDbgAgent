# 阶段3-3 多AppDomain验收

2026-09-06，前置提交`038e338`。每次域加载分配会话内唯一身份，名称/CLR数字编号仅用于展示与诊断；模块、线程、栈、变量、停止及生命周期事件携带域信息。断点按加载域限定，已卸载域的限定断点保持pending，不绑定同名新域。变量读取按域隔离，共享一次停止10000引用预算。

## 实测与快速审核

`eng/verify-stage3-feature.ps1 -Suites appdomains,source-mappings,paging` 默认Debug/Release：两配置各82项单元通过；双架构多域4组、源码映射8组、分页4组均通过。多域真实MCP用例创建两个SameName域，让未选中域先执行且不停止，随后选中域断点命中；验证按域线程、跨域栈及稳定帧ID、变量归属/误用、筛选status仍保留进程停止上下文、卸载移除旧身份、同名重建重新设置断点与旧引用拒绝。目标最终安全分离并自行退出。

另外双架构×Debug/Release运行`FxDbg.IntegrationTests.<arch>.exe <仓库> <配置>`，在真实三轮模块/域卸载重建样例中断言域退出对应已观测创建、各域生命周期ID不重复、模块事件携带域ID；4组全部通过。此验证亦纳入`eng/verify-stage3-3.ps1`。

快速审核与失败修复：

- CLR创建域回调返回Domain 2/Domain 3等临时名称，随后才有NameChange。接入名称更新事件，保留原ID并更新模块快照；真实同名域用例覆盖此时序。
- 退出线程的Thread.AppDomain已不可读取，导致回调处理失败与transport_disconnected。退出事件使用最后观测元数据，不再解引用退出线程，释放其跟踪记录；原复现见`domain-repro-state.log`，修复后完整流程通过。
- 各域变量读取器共用预算，防止把原10000引用上限乘以域数；单元覆盖跨域引用拒绝和总预算。
- status先缓存完整快照再生成筛选结果；筛选不污染后续会话查询。断点schema禁止在enabled更新时替换域。域卸载清理帧/引用与模块绑定，保持回调入队和Continue配对不变。
- 测试失败finally开放自身样例门闩，避免等待样例超时；不清理外部进程。

证据位于`artifacts/stage3-validation/appdomains-final.log`、`appdomains-mcp-*.log`、`domains-events-*.log`及`.processes`，单元日志`feature-unit-*.log`。监督均exit=0、timedOut=False、forcedOwnedCleanup为空；源码清单`stage3-3-inputs.sha256`。文档及随包参考同步，无剩余快速审核阻塞，可以勾选3-3。
