# 阶段3-2 源码映射验收

2026-09-06，前置提交`bd6592e`。SourcePathMapper位于Core；launch/attach通过共享Host及Engine启动帧传递不可变配置。模块直接复用此配置解析本地断点输入和源码输出；CLI提供JSON文件入口，MCP仍13个工具。原始PDB路径保存在originalFilePath。

最终运行`eng/verify-stage3-feature.ps1 -Suites source-mappings,paging`（默认Debug/Release）：两配置各80项单元通过，源码映射双架构×普通/延迟模块共8组通过，前序分页四组通过。映射用例在另一真实本地目录复制源码，以含空格和不同大小写的路径设置断点；验证module优先、pending自动绑定、命中行、栈路径和原始路径，创建前拒绝冲突配置。单元另覆盖最长前缀、段边界、UNC/驱动器根和反向歧义。无映射兼容通过分页实际链路验证，完整前序回归留在本轮最终验收。

快速审核修复：

- 将测试预期路径统一为Windows完整路径；原失败实际返回正确路径和行号，测试混用斜杠造成误判。保留具体位置失败断言，证据`mapping-repro.log`。
- 断点重绑定原先仅比较行号，路径/列/原始路径改变时可能保留旧快照。增加完整位置比较及同一行路径变化的回归测试。
- 抽出统一的有期限构建/单元/MCP监督入口，配置省略时用参数转发保持Debug/Release默认行为。各监督结果exit=0、timedOut=False、forcedOwnedCleanup为空。

证据：`artifacts/stage3-validation/source-mappings-final.log`、`source-mappings-mcp-{Debug,Release}.log`及`.processes`、`feature-unit-*.log`、`stage3-2-inputs.sha256`。CLI、工具契约与技能参考已同步。快速审核无剩余阻塞，可勾选3-2。
