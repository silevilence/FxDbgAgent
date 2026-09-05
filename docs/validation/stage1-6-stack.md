# 阶段 1-6：线程与调用栈验收及快速审核

日期：2026-09-05。范围：ROADMAP 阶段 1-6，依据需求 §7.6、§7.7、§10.3。

## 验证

- Debug/Release × x86/x64：读出 `FxDbg-main` 与 `FxDbg-worker`，线程 ID、AppDomain、停止状态齐全。
- 指定线程返回 `BreakpointTarget → StackLevel12 → … → StackLevel01 → Main` 共 14 个目标帧；逐帧检查方法、模块、程序集、源文件行号和 AppDomain。
- 四种组合均与同位数 CDB/SOS `!clrstack` 的全部 14 层目标方法顺序及数量一致，原始对照输出在 `artifacts/stage1-6/`。
- 栈分页、停止摘要前 5 帧、同一停止中的帧 ID 稳定性、继续后的帧 ID 失效，以及非法状态/线程/分页输入均有真实集成断言。
- Debug 39 项单元测试、双架构阶段 1-5 执行控制及阶段 1-4 断点回归通过。
- 复现：`eng/verify-stage1-6.ps1 -Configuration Debug` / `-Configuration Release`。

## 快速审核与必要修复

| 发现 | 决策 | 修复与验证 |
|---|---|---|
| 默认 CDB 符号搜索可导致无人值守对照长期等待 | 采纳 | 固定本地符号目录，捕获输出，30 秒上限及进程树清理；四组合对照通过 |
| 用活动线程的域标记所有帧会误报跨域帧 | 采纳 | 使用每帧方法模块所属 AppDomain |
| 退出或 Detach 后不应保留帧 COM 引用 | 采纳 | 生命周期清理同时释放帧缓存 |

结论：阶段 1-6 可原位勾选。变量读取继续在阶段 1-7 实现。
