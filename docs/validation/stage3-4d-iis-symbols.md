# 阶段3-4d：完整 IIS 与影子复制符号

实际 IIS 应用池使用 CLR v4、Integrated、ApplicationPoolIdentity，分别启用和禁用 32 位 worker。测试先请求预热，再用 `appcmd list wp /apppool.name:<名称> /xml` 列出所有候选，核对响应 PID 和实际架构；保留影子复制开启。未预热、没有 CLR 的进程继续返回既有 `not_managed_process`，调用方预热后重新核对 PID。

## 实现与快速审核

- 业务 DLL 从 Temporary ASP.NET Files 的实际影子路径加载；动态 `health.aspx` 的 App_Web 模块也读取相邻 Windows PDB。Engine 每个模块的候选仅为实际加载路径旁的 `.pdb`，不扫描磁盘，不以源码映射代替符号搜索。WindowsModuleSymbols 校验 PE CodeView 与 PDB 身份，失配不绑定。
- 页面 PDB 使用部署路径，配置部署根到仓库根的映射；业务 DLL 已有仓库源码路径，使用精确模块范围的同路径映射，避免通用映射反向改写业务源码。验证源码绝对路径及精确行号、参数 42 和局部变量 84。
- 实际影子 PDB 经缺失→失配→恢复及拒绝读取→权限恢复两条路径，pending 自动重绑定后必须再次命中 HTTP 请求。只操作与专属部署 DLL 哈希一致的实际影子副本旁的那个 PDB；finally 恢复字节、时间和原 ACL，失败时也恢复缺失文件。
- 真实反例证明权限恢复不会改变文件时间/长度，原 Engine 一直保持 readFailed。修复为在现有单调度线程的符号轮询中，每五秒重试一次 readFailed；既有元数据变化仍立即触发重新加载。没有新增 IIS 调试后端或并行 COM 调用。
- 业务路径验证 continue 后请求完成再 detach；动态页面及恢复路径验证停止时 detach 后请求恢复。测试与进程监督都有期限，未命中而请求提前完成会明确失败。

本项仅快速审核；整体完成审核和覆盖率留到 3-4f。读取权限恢复的单元测试直接验证 ReadFailed→Loaded 且 mtime 不变，真实 IIS 用例覆盖 Engine 自动重试及断点重绑定。前后两层证据不互相替代。

## 证据

管理员运行 `./eng/verify-stage3-4d.ps1`，默认 Debug/Release，亦支持 `-Configuration`。结果在 `artifacts/stage3-4-validation/iis-result.json`、`iis-{配置}.json`；每配置记录两种架构的候选 PID、实际影子路径、业务/动态页面源码和模块快照、符号状态变化。`iis-acl-retry-red.log` 保留修复前真实 IIS 失败反例。监督日志及独立环境清单记录期限、退出和自有资源移除。


2026-09-07 03:18:35Z 最终双配置通过：每配置 102 项单元测试、两种架构各四条实际 IIS 调试路径；12 个监督步骤均退出 0，无超时或强制清理，独立资源已移除。快速审核无遗留阻塞，可勾选 3-4d。
