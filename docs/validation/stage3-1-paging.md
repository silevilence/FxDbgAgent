# 阶段3-1 分页验收

2026-09-06。准备基线 `63ef4e7`。共享变量读取器将取消传入成员计数、元数据枚举、数组/对象页及递归展开；不在跨请求保存的引用上保留请求取消令牌。沿用直接数组定位、有界字段元数据及10000引用容量，未增加MCP工具或破坏结果结构。

## 验证

使用仓库固定SDK 10.0.301（微软官方zip及SHA-512校验，环境文件位于artifacts）。`eng/verify-stage3-1.ps1` 默认Debug/Release，调用完整构建发布、单元及真实官方MCP客户端。两配置各76项单元通过，包括取消中断、只读取请求页、引用容量达到上限后既有引用可用。真实样例为Windows PDB、CLR Framework双架构，数组100001元素、对象图101101叶成员；覆盖首/中/尾页、溢出安全、循环、字符串、非法预算、单步后引用失效与安全分离。目标自行检查Getter/ToString未被执行。

| 配置 | 架构 | 首次变量读取ms | 30页P95 ms | 单页最大ms |
|---|---|---:|---:|---:|
| Debug | x86 | 24.5 | 17.7 | 29.4 |
| Debug | x64 | 29.2 | 16.0 | 45.0 |
| Release | x86 | 26.4 | 18.0 | 29.9 |
| Release | x64 | 34.7 | 15.6 | 41.7 |

机器证据：`artifacts/stage3-validation/paging-{Debug,Release}.json`、`paging-unit-*.log`、`paging-mcp-*.log`及`.processes`。监督结果均exit=0、timedOut=False、forcedOwnedCleanup为空。源码清单为`stage3-1-inputs.sha256`。

## 快速审核

核查取消沿Engine调度令牌到COM页循环，未改回调/Continue纪律；引用在恢复运行后清空，取消不转换为普通不可获取值。发现测试页选择表达式需显式括号，已修复后重建MCP测试项目、重跑Debug/Release双架构专项，表中为最终结果。修复技能随包工具参考与docs逐字同步，`git diff --check`通过。无剩余阻塞，可勾选3-1。字段元数据及引用数量的明确上限详见`docs/variable-paging.md`；不能将大成员图支持解释为无限对象句柄保留。
