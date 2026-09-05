# 阶段 1-8 异常捕获验收

日期：2026-09-05。依据 ROADMAP 阶段 1-8、需求异常观察约束。

ICorDebug Exception2 回调仍只入队，命令线程按策略处理。默认仅 DEBUG_EXCEPTION_UNHANDLED 停止；显式配置可开启 DEBUG_EXCEPTION_FIRST_CHANCE，USER_FIRST_CHANCE 和 CATCH_HANDLER_FOUND 不重复停止。每次停止与 Continue 配对，策略可在同一会话调整。

异常报告包含真实类型、基类存储消息、线程、抛出源位置及最多 32 个托管帧，并区分是否未处理。仅读取 mscorlib 的 System.Exception._message，忽略子类同名字段、Message Getter 和 ToString。消息有界，读取失败返回 null 与诊断，不生成目标自定义文本。

验证：`eng/verify-stage1-8.ps1 -Configuration Debug`、`Release` 均通过。51 项单元测试通过；x86/x64 对先捕获后未处理的自定义异常验证默认策略、显式 first-chance、运行时关闭策略、准确抛出行和栈，以及自定义 Message/ToString 调用计数为零。同步变量回归通过，测试结束终止自身启动样例，无异常弹窗残留。构建零警告、零错误。

快速审核：回调线程未新增原生读取或 I/O；未处理回调的 Frame 为 null，因此从当前异常线程获取托管栈，不解引用该空帧。无未修复必要阻塞项。

结论：阶段 1-8 可原位勾选。
