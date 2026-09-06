# 阶段3最终验收证据

对应实现提交 `b9d779ae97d06c56e91ab53aa1933b3a3c275897`，确认基线 `63ef4e73e7851f49cc95e710bee6892e9e816bd0`。范围为3-1、3-2、3-3、3-5，跳过3-4。结论与复现命令见[阶段3验收报告](../stage3-usability.md)，审核发现及修复见[最终双轴审核](../stage3-final-review.md)。

- `stage3-inputs.sha256`：验收开始时的源码、测试、发布、扩展与技能输入清单；验收结束逐项比较，变化即失败。
- `stage3-result.json`、`stage2-result.json`：最终完整Debug/Release结果，均为passed=true；阶段2入口包含阶段1及阶段0完整回归。
- `run-summary.json`：六个顶层及六个嵌套流程的日志/进程记录SHA256、监督退出状态与自有进程退出汇总；原始日志保留在本机artifacts中。
- `stage2-inputs.sha256`、`units-*.log`：嵌套回归输入清单及两种构建各85项单元通过记录。
- `paging-*.json`：Debug/Release双架构分页冷读取、30次采样及P95/最大值。
- `vscode-*.json`：真实VS Code扩展宿主测试记录，含版本、时间、四组启动/附加、请求/响应状态和事件；不含变量值。
- `dap-protocol-*.log`：两种构建的真实DAP协议、深栈和输出故障结果。
- `sdk-source.json`、`npm-source.json`、`windows-debugger-source.json`：本轮使用的官方工具来源、完整性及签名记录。

路径保留验收机绝对路径，便于匹配本地artifacts中的原始日志；不要求其它机器采用相同工作目录。历史失败尝试保留在本机 `artifacts/stage3-validation/first-full-attempt/`，不能替代最终通过结果。
