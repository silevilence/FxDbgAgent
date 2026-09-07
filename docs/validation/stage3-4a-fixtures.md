# 阶段3-4a：环境与验收夹具

2026-09-07完成。`eng/verify-stage3-4a.ps1`最终版本于01:40:24Z通过Debug/Release两轮真实部署、延迟加载、清理和安装进程中断恢复；原有默认测试环境未被停止或删除。

新增四个net40 SDK样例并加入解决方案：x86/x64 SCM服务、ASP.NET WebForms页面与预编译业务库、独立延迟加载库。服务运行于LocalService/Session 0，按文件请求加载独立库；IIS由HTTP请求预热与触发延迟加载，应用池独立且实际32/64位可核查。记录PID、创建时间、CLR、域、业务结果及影子路径。

环境脚本提供只读Preflight、Install、Verify、Remove，按EnvironmentName隔离资源和证据。清单原子写入，在外部资源创建前记录拥有关系；清理校验专属路径、身份标记、服务路径、IIS归属及reparse point，并移除自有IIS location配置。正常卸载保留Windows组件基线，不自动重启或全局重置IIS。

## 验证

- 管理员命令：`./eng/verify-stage3-4a.ps1`，默认Debug/Release；子进程由现有ValidationProcess监督，单个安装/检查/卸载240秒期限。
- 两配置各四个样例构建通过；各4项WindowsModuleSymbols自动测试验证DLL/PDB匹配、精确可执行源码行，以及错误PDB被识别为Mismatch。
- `Stage3-4-check`两次安装→服务心跳/IIS健康→延迟加载结果85→检查→清理通过；服务身份、SCM PID、Session 0、进程创建时间、实际位数与影子复制均来自真实目标。
- 首个IIS站点配置提交后安装进程直接退出197；独立恢复进程依据清单移除部分资源，目录、服务、站点和应用池无遗留；全程监督记录无强制清理。
- 最终IIS applicationHost.config全文与验收前基线相同，未将自有资源删除等同于无关配置未变。
- 证据：`artifacts/stage3-4-validation/environment-result.json`、`environment-*.log`及`.processes`；分配置符号测试日志`environment-symbols-{配置}.log`；环境部署清单和健康数据在`artifacts/stage3-4-environment-check/`及其归档清单。

## 快速审核

本项按用户要求仅快速审核，整体规范/需求/质量及覆盖率审核留到3-4全部开发之后。

| 项 | 判断与修复 | 验证 |
| --- | --- | --- |
| 心跳读取失败时可能复用上一目标结果 | 采纳：每个服务开始轮询前清空结果，避免错误通过 | 修复版本双配置重跑通过 |
| 中断安装和IIS location残留 | 采纳：原子清单、创建前拥有关系记录、清理自有location及配置全文比对 | 真实退出197后恢复通过 |
| DLL/PDB仅检查文件头不足以证明匹配 | 采纳：增加真实DIA匹配/错配及源码行测试 | Debug/Release各4项通过 |

未发现其余阻塞项，可勾选阶段3-4a。此结论不覆盖产品附加权限或Service/IIS断点功能。
