# 阶段4全回归：域卸载与安全分离诊断

2026-09-08，最终全回归重跑在 AppDomainSuite 的 x64 Release 分离处返回 operation_timed_out，既有四秒期限未放宽。该路径已在先前矩阵通过，但本次真实失败仍作为阻塞处理。

## 复现与根因

原 MCP appdomains 路径先连续 20 次通过；加入只记录阶段/HRESULT 的临时诊断后，第 9 次复现：最终绑定释放返回 CORDBG_E_PROCESS_TERMINATED，而目标尚未退出。进一步把真实、实际绑定模块的 UnloadModule 回调保留在托管队列中，然后调用正式 Detach，确定复现 NativeBinding.Dispose → BreakpointManager.Remove → SynchronizeAndDetach 的 CORDBG_E_CODE_NOT_AVAILABLE。

清理模式原先跳过所有模块/域回调处理，CLR 卸载后仍保留原生断点包装，最终对失效绑定调用 Activate(false)。这不是应忽略的通用 HRESULT；修复必须释放卸载元数据，并让 CLR 完成相关卸载工作。

初步只删除失效绑定后，真实压力又揭示 CORDBG_E_DETACH_FAILED_OUTSTANDING_BREAKPOINTS，以及成功返回 Detach 后目标的卸载/文件操作句柄异常。仅反复同步并不能证明安全分离。因此最终行为包含源断点所属域的退出及卸载后的短暂安静窗口，且继续验证目标后续业务与正常退出，不能只断言 Detach 返回成功。

## 最终改动

- 清理模式只继续处理 UnloadModule/ExitAppDomain，复用已有元数据释放路径；仍不处理加载、符号绑定或用户停止。
- 有原生源断点绑定的 FX 磁盘模块卸载时记录所属域，域退出后删除记录；整体清理也清空。没有为无绑定的纯内存/可回收模块强制等待域退出。
- 模块/域卸载后等待 100ms 安静窗口；这是本机复现验证后的项目同步策略，沿用既有初始化同步的时间尺度，不能视为 Microsoft 对任意 CLR 故障的保证。普通异常和线程回调不刷新此窗口。
- 如果等待 CLR 推进时仍持有自己的手动 Stop，先配对 Continue，成功后才清除标志；对明确的 outstanding-breakpoints 错误也仅释放本轮手动 Stop 再同步。其它错误仍按原有路径报告，四秒总期限不变。

回调队列须经 Continue 排空，才能确认已发生事件后的目标状态；官方说明见 [ICorDebugController.HasQueuedCallbacks](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugcontroller-hasqueuedcallbacks-method)。本次未增加目标代码执行、用户可见内部停止或任何无配对恢复。

## 独立审核与交错实验

两位独立代理在候选方案中发现手动 Stop 阻止卸载推进的边界；Spec 另指出不能让全部普通回调刷新安静窗口。两项均采纳。将真实回调延迟投递，并临时模拟 Stop 成功后的线程抢占，明确复现四秒超时；配对释放手动停止后相同实验通过。

延迟重投递实验依赖临时抢占屏障；去除屏障后可能人为把投递推到成功 Detach 之后，不适合作为正式协议行为测试。因此实验源文件仅作诊断归档，正式测试保留只读托管队列屏障，临时日志与生产抢占钩子均已删除。不存在通过跳过产品失败来接受候选方案的情况。

正式 [QueuedUnloadDetachTests](../../tests/FxDbg.IntegrationTests/QueuedUnloadDetachTests.cs) 只观察调试器自己的队列与原生包装的缓存身份，不执行目标反射或 COM 调试命令；所有 native 操作仍通过真实 FrameworkDebugSession。覆盖绑定模块卸载、域退出以及持续未匹配异常下的 Detach，检查目标存活、后续两个域完成和正常退出。已纳入默认原生生命周期矩阵及最终覆盖率 harness。

最终压力、专项、覆盖率与完整管理员回归结果及提交标识见 [阶段4最终报告](stage4-final-review.md)。原始诊断、失败候选与最终结果保存于 artifacts/stage4-detach-diagnosis，最终必要证据归档于 stage4-final-evidence/detach-recovery。
