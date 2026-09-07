# 阶段3可用性增强验收

> 本文主体保留2026-09-06的历史范围。2026-09-07已完成3-4a～3-4f及阶段3全部Debug/Release回归，最新结论见文末补充。

范围为用户确认的阶段3-1 → 3-2 → 3-3 → 3-5，阶段3-4明确跳过且保持原地未勾选。本轮全部变更仅本地提交，未推送。准备确认独立提交为 `63ef4e7`；最终实现及验收脚本版本为 `b9d779ae97d06c56e91ab53aa1933b3a3c275897`。

## 交付

| 任务 | 独立提交 | 交付与专项记录 |
| --- | --- | --- |
| 3-1 对象/数组分页 | bd6592e | 100001元素数组和101101叶成员图按需读取、循环和边界/失效引用；[记录](stage3-1-paging.md) |
| 3-2 源码路径映射 | 038e338 | 会话全局/模块级双向映射、原PDB路径、延迟模块重绑定；[记录](stage3-2-source-mapping.md) |
| 3-3 多AppDomain | 8848424 | 稳定域身份、事件/线程/栈/变量归属和筛选、卸载重建与引用清理；[记录](stage3-3-appdomains.md) |
| 3-5 DAP | bc0cbdd | 共用Host/Engine、stdio适配、VSIX及真实VS Code矩阵；[记录](stage3-5-dap.md) |

完整审核修复提交 `5d992b3`：输出背压/关闭退出、深栈分页与totalFrames、共用发布步骤。验收环境修复 `b9d779a`：允许显式工作区CDB目录并提前检查，原独立CDB/SOS对照完全保留。双轴初审与独立复核见[最终审核](stage3-final-review.md)。

## 复现环境

Windows本机，.NET SDK10.0.301，.NET10及.NET8.0.30运行时，Node24.19.0/npm10.9.2，VS Code1.136.1。CDB为微软签名10.0.26100.9169，x86/x64各一套。工具源码/签名/哈希记录在 `artifacts/stage3-validation/sdk-source.json`、`npm-source.json`、`windows-debugger-source.json`。

本机缺少global.json指定SDK及默认CDB安装，使用官方SDK ZIP和官方调试工具离线包，均保留在artifacts并验证完整性/签名；未安装IIS、服务或系统调试工具。npm仅用于固定技能安装与本地VSIX打包，不发布扩展市场。

本机最终命令：

```powershell
$env:PATH = (Join-Path $PWD 'artifacts/dotnet-sdk') + ';' + (Join-Path $PWD 'artifacts/npm-runtime') + ';' + $env:PATH
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet-sdk'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'artifacts/nuget-packages'
$env:FXDBG_DEBUGGERS_DIRECTORY = Join-Path $PWD 'artifacts/windows-debuggers'
./eng/verify-stage3.ps1 -NodePath (Join-Path $PWD 'artifacts/npm-runtime/node.exe') -CodePath 'D:/Program Files/Microsoft VS Code/Code.exe'
```

## 最终回归

最终修复版本 `b9d779a` 的全量Debug/Release验收于2026-09-06 05:23:48 UTC（北京时间13:23:48）通过，主进程退出码0。六个顶层流程及嵌套阶段2/1/0全部成功；监督记录均exit=0、timedOut=false、forcedOwnedCleanup为空，记录的自有进程全部退出。早先因CDB缺失而失败的尝试保留为失败，不作为最终通过依据。

运行前后235项输入的SHA256逐项一致；输入清单自身SHA256为 `0D33C03321BAAF4B0D2C20C5AC87BE1A0DE01D56A7191543E3449649FDAD2562`。最终机器结果见 [stage3-result.json](stage3-final-evidence/stage3-result.json)，嵌套完整回归见 [stage2-result.json](stage3-final-evidence/stage2-result.json)，原始日志与进程记录的哈希及退出汇总见 [run-summary.json](stage3-final-evidence/run-summary.json)。最终提交仅归档文档与证据，不改变已验收实现输入。

验收入口覆盖3-1/2/3/5、全部阶段2 MCP回归、技能安装和既有独立Agent证据、阶段1产品矩阵与阶段0真实探针/CDB对照。每个子流程有总期限、日志和自有进程清理监督；源码/测试/发布/技能输入在开始和结束逐项SHA256比较，有变化或任一步失败则整个结果失败。

阶段3专项覆盖：Debug/Release各85项单元测试；8组源码映射场景；双架构多域与4组原生生命周期事件；8组真实DAP启动/附加、8组真实VS Code会话。两种构建都通过184帧栈分页、100001元素数组、失效句柄、取消/EOF、输出关闭与背压、安全分离场景。输出故障时要求适配器有界退出，附加目标保持存活并恢复运行。

前序完整回归覆盖全部13个MCP工具、Console/WinForms真实场景、技能安装与原有独立Agent的17次调用/10工具及错误恢复证据；产品层断点竞态、变量、异常、模块、100次Continue配对、生命周期和CLI；阶段0真实双架构启动/附加、回调纪律、Windows PDB、源码断点与14帧WinDbg/SOS对照。独立Agent检查使用阶段2已提交证据，不宣称本轮重新执行了外部Agent任务。

分页实测如下，每组30次预热后页读取；冷读取单独列出，单位均为毫秒。对象图含101101个叶成员，数组含100001元素。

| 构建 | 架构 | 冷读取 | P95 | 最大值 |
| --- | --- | ---: | ---: | ---: |
| Debug | x86 | 26.85 | 21.90 | 43.97 |
| Debug | x64 | 40.94 | 24.56 | 39.16 |
| Release | x86 | 30.11 | 21.05 | 37.65 |
| Release | x64 | 41.37 | 21.96 | 31.75 |

本机四组均满足P95≤1000毫秒、单次≤5000毫秒；这些数值是本机实测，不代表其它硬件的承诺。完整采样与真实编辑器协议记录保存在 [stage3-final-evidence/](stage3-final-evidence/)。记录只包含请求名称、结果状态及事件名称，不保存目标变量值。

## 使用与边界

DAP成品为 `artifacts/dap/Release/` 完整目录和 `artifacts/dap/fxdbg-0.1.0.vsix`，使用说明见[配置文档](../dap.md)。MCP入口与13个工具保持兼容，新增可选参数与归属字段。真实编辑器验收为VS Code；Cursor交付共用扩展，未冒充Cursor实测。

不提供求值、变量修改、条件断点、HTTP/远程调试；附加目标不能终止。3-4的Service/IIS、权限与影子复制仍未实施或验收。本轮通过只覆盖明确确认的四项任务。

## 2026-09-07完整阶段3补充验收

实现`a0b4a2c6851b25414d247da75bc0bff755eccea5`的完整Debug/Release回归于08:23:55Z通过，新增真实Service/IIS的权限、符号、动态模块/多域/回收矩阵，并重新执行3-1/2/3/5、真实VS Code及全部阶段2/1/0。269项输入前后一致，监督记录无超时或强制清理，自有验收资源已恢复/移除。单元测试每配置103项；Service/IIS入口每配置16项MCP/CLI、4项DAP及4项真实VS Code。

当前阶段3全部完成；上文跳过结论不再代表现状。完整命令、环境版本、独立审核、覆盖率和机器证据见[Service/IIS总验收](stage3-4-service-iis.md)、[完成审核](stage3-4-final-review.md)及[本轮证据](stage3-4-final-evidence/README.md)。旧分页性能和外部Agent记录仍标识其原批次，不冒充本轮重新测量。
