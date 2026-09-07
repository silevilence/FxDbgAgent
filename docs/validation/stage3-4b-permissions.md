# 阶段3-4b：跨身份附加权限

实现覆盖 Host 架构检查、Engine CLR 附加和安全分离。Host 使用 QUERY_LIMITED_INFORMATION，访问被拒绝时才在当前令牌的 SeDebugPrivilege 范围内重试；Engine 附加与所有清理入口共用可恢复范围。没有权限仍尝试普通同用户附加，无法访问跨身份目标时返回含 Host/Engine 环节及恢复步骤的 `access_denied`，不弹 UAC、不更改安全策略。

`WindowsDebugPrivilege` 只调整已有权限，检查 AdjustTokenPrivileges 的返回值及 ERROR_NOT_ALL_ASSIGNED，保留原启用状态。Host 的进程创建和临时权限调整共用同步门，避免并发创建的 Engine 继承临时权限；Engine 各自串行调度。CLR 持有的目标句柄复制为仅 SYNCHRONIZE 的存活句柄，分离时不重新打开 PID。

## 快速审核与实际发现

| 阻塞项 | 判断与修复 | 验证方式 |
| --- | --- | --- |
| 关闭权限后跨身份目标存活检查被拒绝 | 采纳：保存目标实例句柄，避免重新打开受保护的 PID | 真实 LocalService 附加后恢复权限再分离 |
| CLR Detach 返回 E_ACCESSDENIED，随后对象被 neuter | 采纳：显式分离、EOF 和异常清理均进入相同权限范围 | 最小复现先失败，修复后通过；完整矩阵继续覆盖 |
| Host 首次 ProcessManager 初始化持久启用权限 | 采纳：在可恢复范围内提前初始化，再允许创建 Engine | 真实令牌前后属性对照（关闭/已启用） |
| 测试把 Process.HasExited 当作无权限存活检查 | 采纳：使用服务心跳递增和 IIS HTTP 响应确认业务继续工作 | 每个真实跨身份案例均检查 PID 与业务结果 |
| 禁用管理员 SID 后受限测试进程在 DLL 初始化阶段退出 | 采纳：仅为一次性受限令牌设置当前用户可访问的默认 DACL，并使用独立隐藏桌面；不改系统对象权限 | 非管理员、SeDebugPrivilege 不存在的真实子进程完成四个拒绝案例及同用户附加 |

ProcessManager 的初始化副作用可在 [.NET 10 官方源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs) 及 [.NET Framework 官方参考源码](https://github.com/microsoft/referencesource/blob/main/System/services/monitoring/system/diagnosticts/ProcessManager.cs) 核查。权限调整规则以 [AdjustTokenPrivileges](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges) 为依据。

## 验证入口

管理员运行 `./eng/verify-stage3-4b.ps1`，默认 Debug/Release。实际 MCP stdio 客户端访问 LocalService/Session 0 服务与专属 IIS worker，覆盖双架构、权限关闭/已启用/受限令牌不存在、同用户调试、已有调试器、已退出目标与并发会话。令牌检查、目标业务检查和 JSON schema 检查使用真实进程；模拟仅用于权限 API 错误及恢复失败等难以稳定注入的边界。

机器证据：`artifacts/stage3-4-validation/permissions-result.json`、`permissions-admin-{配置}.json`、`permissions-restricted-{配置}.json`、各步骤日志及进程监督清单。专属环境 `Stage3-4-permissions` 在 finally 中清理，原默认环境保留。

2026-09-07 02:33:02Z 最终 Debug/Release 矩阵通过。两配置各 98 项单元测试、8 个管理员及 4 个受限令牌 Service/IIS 案例成功；同用户启动/附加、已退出目标、并发双架构服务附加与权限恢复通过。全部 12 个监督步骤退出码为 0，未超时、未强制清理自有进程，专属环境已移除。快速审核阻塞项全部修复，可勾选 3-4b；整体完成审核仍留到 3-4f。
