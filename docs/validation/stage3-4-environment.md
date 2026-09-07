# 阶段3-4本机环境准备记录

日期：2026-09-07。用户要求先安装本机测试环境。结论：**环境安装及健康检查通过，无需重启**。本记录不代表阶段3-4a全部交付，也不代表Service/IIS产品调试能力验收通过。

## 机器与部署

- 本机Windows Professional，25H2，build 26200，x64；注册表ProductName仍为`Windows 10 Pro`，以版本/build为准。
- 既有.NET Framework注册表版本`4.8.09221`、Release `533509`；真实样例CLR版本`4.0.30319.42000`。
- 既有SDK `10.0.301`用于构建，无需新装SDK或Framework。
- 本次启用IIS 10.0、ASP.NET 4.x及所需依赖；Windows报告不需要重启。
- 部署目录为 `C:\ProgramData\FxDbgAgent\Stage3-4`。两个服务均为手动启动、LocalService身份，当前已启动；两个站点和应用池以 `FxDbgStage34-x86` / `FxDbgStage34-x64` 命名。
- 站点仅绑定回环地址58340、58341端口；首次启用IIS生成的Default Web Site已停止并禁用自动启动。没有创建防火墙规则、远程管理端点或执行系统重启。

## 实际执行与结果

1. `dotnet build tests/Debuggees/<样例>/<样例>.csproj --configuration Debug`：Service.x86、Service.x64、Web三个项目均零警告、零错误。
2. 同三项目Release构建：均零警告、零错误。六份PDB均为`Microsoft C/C++ MSF 7.00`。
3. 管理员执行 `eng/setup-stage3-4-environment.ps1 -Action Install`：IIS组件安装成功，随后x64应用池名称刚创建时ACL身份解析失败。首次失败如实保留，未标记安装成功。
4. 增加有期限的应用池身份等待并使用SID授予目录权限；部署根目录收紧为管理员/SYSTEM可写、普通用户只读，服务仅对自身心跳目录具有修改权限。
5. 管理员执行 `-Action Remove`：首次部署的自有站点、应用池、已注册服务与部署目录成功清理，IIS组件保留；随后执行 `-Action Install -Configuration Debug` 成功。
6. 安装内置健康检查在北京时间09:21完成；另由普通权限会话再次请求两个HTTP端点并检查服务运行状态，均成功。

| 目标 | 本次检查PID（会变化） | 验证结果 |
| --- | --- | --- |
| x86服务 | 29720 | SCM Running、LocalService、Session 0、32位、心跳序号增长 |
| x64服务 | 19364 | SCM Running、LocalService、Session 0、64位、心跳序号增长 |
| x86 IIS | 29792 | worker属于指定池、HTTP成功、32位、CLR v4、业务结果85 |
| x64 IIS | 5192 | worker属于指定池、HTTP成功、64位、CLR v4、业务结果85 |

两站点均报告`shadowCopy=true`，预编译业务DLL实际加载于各自Framework/Framework64的`Temporary ASP.NET Files`目录；原始DLL来自专属站点的bin目录。健康页本身由ASP.NET动态编译。本轮仅安装Debug部署，Release只做构建及PDB格式验证，不冒充Release真实部署矩阵。

## 证据与后续

原始机器证据保存在 `artifacts/stage3-4-environment/`：

- `state.json`：最终部署ID `325efcda-05e7-403e-a63c-626d9012ab75`、`status=ready`、完整资源路径；`last-operation.json`记录Install成功。
- `health.json`：服务连续心跳和IIS响应、位数、域、影子复制路径。
- `Install-20260907-091720.log`：首次安装失败记录；`Remove-20260907-092105.log`及`Install-20260907-092106.log`：清理和重装记录。
- `90a9068d-7d75-43bb-a32f-ee5547a68bd2-features-before.json`与同前缀的`features-after.json`：**首次启用IIS前后的系统组件基线**；无前缀的features文件为第二次部署时的基线，此时IIS已启用。
- `source-inputs.sha256`：本次环境脚本及样例源码哈希。

使用、健康检查和卸载步骤见 [环境说明](../service-iis-environment.md)。卸载仅移除自有测试资源，保留Windows组件与验收证据。首次部分部署的卸载路径已真实验证；没有将完整环境反复安装或异常中断恢复记为已验收。

上述为初始环境准备时的历史状态，当时跨身份附加、产品断点/栈/变量、延迟模块/PDB重绑定、域重建/回收及完整回归尚未验收，3-4与3-4a尚未勾选。

## 2026-09-07完成后的环境刷新

3-4a～3-4f及完整Debug/Release回归已通过，见[总验收](stage3-4-service-iis.md)和[完成审核](stage3-4-final-review.md)。默认Stage3-4环境随后用本轮Debug夹具重新部署，ID为`6e4559a4-8253-47d2-bc5d-16b2076caf79`，08:29:21Z独立健康复查通过；双架构服务、IIS站点、CLR位数、持续心跳和影子复制均有效，含最新late.aspx及匹配部署DLL。

当前state/health见[最终环境快照](stage3-4-final-evidence/default-environment-state.json)及[健康记录](stage3-4-final-evidence/default-environment-health.json)。初始部署ID和日志仍为历史证据，运行时PID须重新查询。经用户授权的临时管理员工作进程于08:32:35Z停止，PID33084已退出；测试服务和站点保留供用户使用。
