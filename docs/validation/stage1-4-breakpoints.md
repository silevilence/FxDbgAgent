# 阶段 1-4：断点生命周期验收与快速审核

日期：2026-09-05。范围：ROADMAP 阶段 1-4，依据需求 §7.4 与 AGENTS 回调/单一逻辑源约束。

## 验证

- Debug、Release × x86、x64：真实 .NET Framework 模块延迟加载、PDB 到位后自动绑定、禁用时继续运行、启用后命中、AppDomain 卸载/重载状态事件、删除后不再绑定均通过。
- 6 项断点/符号测试覆盖 pending/verified/moved/unresolved、启用状态跨重绑定保持、多模块实例、异步绑定失败、真实 Windows PDB 映射。
- Debug 全部 37 项单元测试通过。
- `eng/verify-stage1-3.ps1 -Configuration Debug` 通过，覆盖产品双架构启动/附加、拒绝路径及 100 次 Continue 配对。
- 复现命令：`eng/verify-stage1-4.ps1 -Configuration Debug` 和 `-Configuration Release`。真实测试只使用产品 Core/Interop/Symbols，不引用阶段 0 Probe 后端。

## 快速审核与必要修复

| 发现 | 决策 | 修复与验证 |
|---|---|---|
| CLR 异步 BreakpointSetError 未改变已验证状态 | 采纳 | 转为 unresolved，停用失效绑定；增加状态回归测试 |
| Detach 前可能保留有效原生断点 | 采纳 | 在同步停止内先停用并清理绑定，再 Detach；既有启动/附加清理回归通过 |

结论：阶段 1-4 可原位勾选。完整执行控制、模块符号状态对外协议、Host/CLI 入口继续按后续任务实施。
