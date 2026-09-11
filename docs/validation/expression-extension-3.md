# 成员诊断自愈

2026-09-11。本任务基线 `0cd7011`，范围为 ROADMAP 第三项、已批准 ADR-005 的元数据名称脱敏例外。

## 实现与快速审核

Core 对已有字段/属性元数据目录计算建议，不读取字段值，不解析其他类型，也不执行 getter。只输出最多8个不同名称，每名最多64字符；允许字母、数字和下划线且首字符非数字，排除控制字符、显式接口限定和编译器生成名。先按精确大小写前缀，再按忽略大小写前缀，最后按忽略大小写编辑距离≤2；同级按距离及 Ordinal 名称排序。编辑距离采用宽度5的带状计算，目录遍历、名称验证及距离运算计入既有10000步骤预算，超限仍为 expression_limit_exceeded。

成员缺失和属性拒绝保持 expression_name_not_found；拒绝原因明确 getter 无法证明只读、不会调用。候选不表示属性必然可读。没有新增错误码、目标读取/调用、Continue 或缓存。工具与技能文档同步解释元数据名称例外，仍不允许表达式文本、值和内部堆栈进入错误消息。

本任务快速审核检查了候选来源、去重/排序/上限、恶意元数据字符、计算预算、错误分类和四入口消息传播。未发现遗留阻塞；完整独立双轴审核留待第四项。

## 验收

`./eng/verify-stage4-1.ps1` 默认 Debug/Release 共14个监督步骤全部通过，无跳过、超时或强制清理，输入前后相同。每配置普通单测322/322；新增3项候选测试覆盖排序、最多8个、无关/超长/非法名字、Unicode、重复输入计费及取消。

真实 x86/x64 原生、MCP、DAP、CLI 覆盖 Properties.Puer、Properties.pure、node.Labl 和计算属性拒绝，确认消息只有元数据候选及稳定原因，错误码保持不变；此前语法、属性、预算、副作用计数和 Continue 配对回归通过。MCP 开启 debug 审计且值日志关闭，完成后扫描审计文件和 stderr，确认无秘密表达式字面量、目标字符串值、完整成员表达式和 Interop 栈；DAP stderr 同样扫描。原生和协议断言另查错误响应，避免只验证空日志。

项目级 skills@1.5.23 安装及两份文档逐字节校验通过。本任务可原地勾选并本地提交。

证据：[求值结果](expression-extension-3-evidence/stage4-1-result.json)、[输入哈希](expression-extension-3-evidence/stage4-1-inputs.sha256)、[技能安装](expression-extension-3-evidence/skill-install-result.json)、[原始文件清单](expression-extension-3-evidence/manifest.json)。46个日志/进程/耗时/审计原件只在本机 `artifacts/expression-extension/task3-accepted/`，逐文件SHA256已记录，未上传外部存储。
