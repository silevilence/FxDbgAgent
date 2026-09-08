# 调试 Windows Service 与完整 IIS

FxDbg 按 PID 附加本机已运行、已加载 CLR v4 的 .NET Framework 4.x 进程。SCM 服务和完整 IIS 的 ASP.NET/WebForms worker 均使用现有 Host/Engine；支持真实 x86/x64 路由、源码断点、栈与只读变量、单步、继续及安全分离。MCP使用stdio调试工具，完整工具契约见[mcp-tools.md](mcp-tools.md)；不提供服务管理、远程调试或自动跨PID重附加。

## 身份与 PID

跨身份的 LocalService、Session 0 服务及 ApplicationPoolIdentity worker 通常需要具备调试权限的 Host。Host 和 Engine 只按需启用当前令牌已有的 SeDebugPrivilege 并恢复原状态；不会自行弹 UAC、索取凭据或修改策略。`access_denied` 会说明架构探测、运行时检查或附加失败的环节。用有权限的客户端重新启动 Host，再核对 PID 附加；修改目标权限或强杀目标不是恢复步骤。

调试器不会因附加而再次执行 OnStart。选择持续业务方法或可重复触发的请求设置断点。以下名称来自[独立测试环境](service-iis-environment.md)，用于实际业务时替换为已授权的精确名称：

```powershell
Get-CimInstance Win32_Service -Filter "Name='FxDbgStage34-x86'" |
    Select-Object Name, State, ProcessId, StartName
# 先发请求，使目标应用加载 CLR，然后列出该池全部 worker。
Invoke-RestMethod http://127.0.0.1:58340/health.aspx
& "$env:windir/System32/inetsrv/appcmd.exe" list wp /apppool.name:FxDbgStage34-x86
Get-Process -Id <核对后的PID> | Select-Object Id, StartTime
```

无 worker 时先启动站点/应用池并请求预热；native-only worker 返回 `not_managed_process` 时，预热托管页面后重新定位。多 worker 或回收重叠时列明所有候选，结合站点响应 PID、应用池和创建时间选择；不要按 w3wp 进程名任取一个，也不要复用历史报告中的 PID。附加响应的 architecture 为实际位数，显式指定错误位数会失败。

## 产品入口

MCP：`debug_attach({pid})` 保存返回的 sessionId；用 `debug_set_breakpoint` 设置业务源码行。必要时 `debug_pause` 后查看线程、栈及变量；继续用 `waitForStop:false` 时轮询 `debug_status`，不要重复 continue。由业务请求或服务工作周期触发断点，停止后从实际 threadId/栈获取新 frameId。附加状态禁止 `debug_terminate`，结束用 `debug_detach`。

CLI 使用同一后端，[命令与字段](cli.md)如下；SESSION、PID、线程号和路径须替换为实际结果：

```powershell
dotnet <发布的fxdbg.dll> attach --pid <PID>
dotnet <发布的fxdbg.dll> pause --session <SESSION>
dotnet <发布的fxdbg.dll> break --session <SESSION> --file C:/src/App/Work.cs --line 42
dotnet <发布的fxdbg.dll> continue --session <SESSION>
# 从另一个终端触发 HTTP 请求，或等服务下一次业务周期。
dotnet <发布的fxdbg.dll> wait --session <SESSION>
dotnet <发布的fxdbg.dll> stack --session <SESSION> --thread <实际线程号>
dotnet <发布的fxdbg.dll> detach --session <SESSION>
```

VS Code/Cursor 使用[已发布的 DAP 和扩展](dap.md)，配置 `type: "fxdbg"`、`request: "attach"`、`processId: <PID>`；适配器继承启动编辑器的令牌。先设置源码断点，启动附加，再触发请求。断开调试会话默认安全分离；禁止把重启按钮当成 IIS 回收后的重附加。真实客户端验收使用 VS Code，Cursor 通过同一扩展接口使用，不将前者冒充后者的实测。

## 影子复制与符号

保持 ASP.NET 影子复制开启。`debug_status.modules` 返回实际 path、pdbPath、symbolStatus、diagnostic 和 appDomainId；业务 DLL 与动态 App_Web 模块可能位于 Temporary ASP.NET Files。符号候选仅限实际加载模块旁的同名 `.pdb`，不扫描磁盘；用 PE CodeView/PDB 身份匹配验证，部署目录的同名 PDB 不会被强行拿来绑定另一个版本。

| 状态 | 处理 |
| --- | --- |
| missing | 将匹配 Windows PDB 随实际部署产物发布，检查影子副本是否带上 PDB |
| mismatch | 重新部署与 DLL 同一次构建的 PDB，不能仅改名替换 |
| readFailed | 根据 diagnostic 修复文件读取权限或占用；元数据不变时也会每五秒重试 |
| loaded | 再核对源码文档与行号、模块及域；源码映射不负责寻找 PDB |

PDB 到位后自动重新加载并重绑定 pending 断点。每个模块/域有独立生命周期身份，不复用卸载模块的符号对象。ASP.NET 动态页面须实际生成可用 Windows PDB；纯内存/Reflection.Emit 模块仍可枚举，但明确报告没有可用磁盘符号，不承诺任意动态 IL 源码调试。

页面 PDB 常记录部署路径，业务 DLL PDB 可能已记录本地仓库路径。可在 attach 的 sourceMappings 配置页面部署根→本地根，并为业务 DLL 增加精确模块范围的同路径映射，防止页面通用映射反向改写业务文档。例如：

```json
[
  { "buildRoot": "C:/deploy/web", "localRoot": "C:/src/Web" },
  { "buildRoot": "C:/src/Web", "localRoot": "C:/src/Web", "module": "Web.dll" }
]
```

模块匹配为精确文件名或路径，不使用通配符。CLI 通过 `--source-maps <JSON文件>` 传入，MCP/DAP 使用 sourceMappings。具体规则见[源码映射](source-mapping.md)。同一 ASPX 行可含多个序列点；验证某一次命中后要让整次请求完成，可移除或禁用该断点再继续。

## 暂停、域卸载与回收

停止调试会暂停整个目标进程，服务心跳和该 worker 请求会等待；不会只暂停当前 AppDomain。不要以函数求值或 Getter 读取来触发业务。长暂停可能触发外部服务监控、IIS ping 或回收策略；调试器不修改这些业务设置。测试脚本只临时调整自己创建的池，并保存、恢复、核对原设置。

限定 appDomainId 的断点不会跟随同名新域；域重建后重新查询 ID。未限定域的断点可在新模块就绪后自动绑定。继续、单步、卸载和退出均使旧帧/变量引用失效，需重新获取。旧帧返回 frame_not_found，失效变量引用返回 value_unavailable，终态会话命令返回 invalid_session_state。

服务重启或 IIS 正常回收后，旧 session 结束，不会转移到新 PID。等旧请求恢复或退出后重新列出候选、预热、核对创建时间并显式 attach。显式 detach、正常 EOF 和 Host 异常退出都尝试安全分离；不要用杀进程树代替分离。Engine 硬崩溃仍受 CLR/操作系统调试器退出语义约束，不能保证目标存活。

## 验收

管理员 64 位 PowerShell 运行 `./eng/verify-stage3-4.ps1`，默认 Debug/Release，也支持 `-Configuration`、`-NodePath` 和 `-CodePath`。覆盖真实服务与完整 IIS、权限、影子 PDB、动态模块、多域、回收、CLI/DAP/真实 VS Code及自有资源恢复。缺权限、缺组件、用例失败或清理失败均非零退出。

完整阶段 3 运行 `./eng/verify-stage3.ps1`，还包含 3-1/2/3/5、阶段 2/1/0及双架构 CDB/SOS 对照。仅需既有子集时显式使用 `-SkipServiceIis`；结果列出 skipped，`subsetPassed` 与完整验收的 `passed` 分开，不能据此勾选阶段 3 全部完成。原始证据位于 `artifacts/stage3-4-validation/`，总报告见[Service/IIS 验收](validation/stage3-4-service-iis.md)。
