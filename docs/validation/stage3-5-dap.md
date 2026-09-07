# 阶段3-5 DAP 快速审核与验收

2026-09-06：交付可选 DAP stdio Host、最小 VS Code/Cursor 扩展、发布/VSIX打包和配置文档。入口只引用共享Host；没有第二套调试逻辑、COM依赖或求值路径。

执行命令（本机使用 artifacts 内经官方SHA512核验的SDK10.0.301）：

```powershell
./eng/verify-stage3-5.ps1 -NodePath 'C:/Users/silev/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' -CodePath 'D:/Program Files/Microsoft VS Code/Code.exe'
./eng/package-extension.ps1
dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj -c Debug --no-build --no-restore
dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj -c Release --no-build --no-restore
```

结果：Debug/Release均通过真实协议与真实VS Code四组启动/附加×x86/x64会话。VS Code版本1.136.1；客户端分别于04:35:13Z和04:35:58Z完成。实际使用标准初始化/断点配置流程、线程和14层栈、根作用域和数组尾页、next/stepIn/stepOut、旧帧/引用拒绝、附加目标不可终止、分离后目标正常退出。协议专项另覆盖100001元素首/中/尾分页、未分页及非法页拒绝、自然退出、配置取消、附加配置EOF以及畸形帧/超大消息拒绝。Cursor交付兼容扩展，未声明本机做过Cursor客户端实测。

全部单元测试Debug/Release各85项通过，其中三项新增Host竞态测试验证过时线程/栈/变量刷新取消不会取消共享Engine或正在运行的操作。监督器记录均exit=0、timedOut=false、forcedOwnedCleanup为空。

快速审核修复：

- 使用正确的VS Code激活事件，并等待编辑器实际完成会话清理；重复disconnect不重复发terminated，结束后的terminate幂等。
- 真实客户端在继续后取消自动栈刷新，曾触发共享Host关闭Engine，造成偶发断点等待超时。只读观察改为立即取消调用方等待，实际查询按原期限收尾且保留并发名额；针对三个只读命令加入受控竞态回归。
- 状态同步后再提供观察引用，避免轮询事件清空刚返回的帧；运行态返回最近线程快照。
- count=0遵循请求全部成员语义，超预算明确要求分页；不静默缩短成功结果。条件断点、求值、单线程执行、指令单步和异常过滤均明确拒绝。

机器证据位于 `artifacts/stage3-validation/`：`dap-protocol-{Debug,Release}.log`、`dap-vscode-{Debug,Release}.log`、`vscode-{Debug,Release}.json`、`dap-units-{Debug,Release}.log`、`dap-package.log`。JSON只记录客户端版本和请求/响应/事件摘要，不存变量值。独立VS Code的mutex/已有安装修改提示来自本机编辑器；验收以扩展测试、四组断言和进程退出证据为准。

完整阶段审核及最终源码哈希另由 `eng/verify-stage3.ps1` 和阶段3最终报告记录。本报告原批次跳过3-4；2026-09-07已补齐Service/IIS及包含DAP/真实VS Code的完整回归，见[3-4总验收](stage3-4-service-iis.md)。
