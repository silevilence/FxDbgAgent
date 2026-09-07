# 阶段3-4本机测试环境

此环境用于后续Service/IIS附加开发。安装成功仅表示真实SCM服务、完整IIS、CLR v4、双架构与影子复制可运行，不表示阶段3-4调试能力已经实现或验收。

## 环境与资源

安装入口为 `eng/setup-stage3-4-environment.ps1`，使用64位管理员PowerShell；脚本本身不会弹UAC或自动重启。Windows需支持完整IIS及ASP.NET 4.x，样例以net40构建，在本机安装的4.x CLR上执行。

| 架构 | 服务 / 站点 / 应用池名称 | 健康检查 |
| --- | --- | --- |
| x86 | `FxDbgStage34-x86` | [32位站点](http://127.0.0.1:58340/health.aspx) |
| x64 | `FxDbgStage34-x64` | [64位站点](http://127.0.0.1:58341/health.aspx) |

- 专属部署目录：`%ProgramData%\FxDbgAgent\Stage3-4`。目录有部署身份标记，重名资源或端口占用时拒绝覆盖。
- 服务由SCM启动，以`LocalService`身份运行于Session 0，启动方式为手动；每秒执行一次可设置断点的业务方法，并更新自己的`service-x86/data/heartbeat.json`或`service-x64/data/heartbeat.json`。
- IIS应用池使用CLR v4、Integrated模式和各自的ApplicationPoolIdentity；32位池开启32位worker。匿名请求使用应用池身份，只有其专属站点目录获得读取权限。
- 站点仅绑定`127.0.0.1`，不创建防火墙规则。新安装IIS生成的Default Web Site会停止并关闭自动启动；原本已启用的IIS默认站点不作此改动。
- `health.aspx`为ASP.NET动态编译页面，调用带Windows PDB的预编译业务DLL，返回PID、实际位数、CLR版本、AppDomain、影子复制标记与实际程序集位置；这两个健康端点只提供固定测试信息。
- 保留IIS默认ping、空闲超时和回收策略；长时间断点暂停前，需按后续3-4任务配置专属测试池。当前环境健康检查没有承诺长暂停不被IIS回收。

## 构建和安装

在仓库根目录用SDK 10.0.301构建；Debug或Release均可，默认安装Debug。项目继承仓库固定的net40引用程序集与Windows PDB配置。

```powershell
foreach ($sample in @('Fx40.Environment.Service.x86', 'Fx40.Environment.Service.x64', 'Fx40.Environment.Web', 'Fx40.Environment.Late')) {
    dotnet build "tests/Debuggees/$sample/$sample.csproj" -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $sample" }
}
# 以下在64位管理员PowerShell中运行：
./eng/setup-stage3-4-environment.ps1 -Action Install -Configuration Debug
```

`-Action Preflight`可在普通权限下只读运行，不创建目录或改变配置；组件详情需要管理员令牌，权限不足会明确返回unknown。默认环境沿用上表名称，也支持`-EnvironmentName Stage3-4-<小写字母数字后缀> -PortBase <端口>`建立独立测试环境；清单及部署目录按后缀隔离，Verify/Remove须使用同一EnvironmentName。

服务写入`data/late.request`后按需加载独立`late/Fx40.Environment.Late.dll`，后续心跳的`lateResult=85`；IIS访问`health.aspx?action=late`触发同一库，默认健康请求不加载该库。样例业务入口和嵌套方法有稳定的源码标记，供后续产品断点与单步测试使用。

管理员执行`eng/verify-stage3-4a.ps1`可进行Debug/Release环境验收：使用独立`Stage3-4-check`资源及58342/58343端口，验证真实部署、延迟加载、DLL/PDB身份和源码行、清理、安装进程突然退出后按清单恢复，以及IIS配置完全回到基线。每个子进程均有限期；不清理默认环境。

安装先记录Windows组件基线，再仅启用所需IIS/ASP.NET组件及其依赖，不安装.NET 3.5、FTP或远程管理服务。若Windows要求重启，记录`restartRequired`并失败退出，不自动重启或宣称安装完成。失败时保留安装日志、状态与已创建资源清单供诊断，不执行全局IIS配置回滚或终止无关进程。

## 检查与使用

```powershell
# 管理员：检查服务身份、SCM PID、Session 0及持续心跳，
# 请求真实IIS页面，核对worker所属应用池、位数、CLR v4与影子复制。
./eng/setup-stage3-4-environment.ps1 -Action Verify

# 普通用户：触发可设置断点的页面业务方法。
Invoke-RestMethod http://127.0.0.1:58340/health.aspx
Invoke-RestMethod http://127.0.0.1:58341/health.aspx

# 管理员：获取当前服务/worker PID，不复用报告中的历史PID。
Get-CimInstance Win32_Service -Filter "Name='FxDbgStage34-x86' OR Name='FxDbgStage34-x64'" |
    Select-Object Name, State, ProcessId, StartName
& "$env:windir/System32/inetsrv/appcmd.exe" list wp
```

`Verify`不附加调试器、不回收应用池，也不终止服务。机器结果位于 `artifacts/stage3-4-environment/`：`state.json`记录资源与安装状态，`health.json`记录最近一次成功检查，`last-operation.json`记录最近一次操作结果；同时保留安装/检查/卸载日志与`features-before.json`、`features-after.json`。以当前检查结果判断可用性，不能把历史健康记录当作当前成功。

## 移除测试资源

先关闭附加这些目标的调试会话，然后在管理员PowerShell执行：

```powershell
./eng/setup-stage3-4-environment.ps1 -Action Remove
```

仅移除清单中的两个测试站点、应用池、服务及带匹配身份标记的部署目录；文件删除前校验绝对路径并拒绝reparse point。删除前核对服务可执行路径、站点目录和应用池是否被其他站点使用，不调用iisreset或进程树终止。保留仓库内源码、构建产物及证据。

卸载测试资源不会禁用Windows组件；组件基线及本次新增组件列表保留供以后评估系统恢复，避免误移除后续被其他应用使用的IIS依赖。卸载完成后可再次执行Install。

配置依据：[Microsoft AppCmd](https://learn.microsoft.com/en-us/iis/manage/provisioning-and-managing-iis/appcmdexe)、[IIS站点绑定](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/sites/site/bindings/)、[Windows Service调试](https://learn.microsoft.com/en-us/dotnet/framework/windows-services/how-to-debug-windows-service-applications)。
