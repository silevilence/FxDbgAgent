# 阶段 2-2c 超时、取消与资源边界

2026-09-05，Windows，Debug，真实 Desktop CLR x86/x64。

命令：`./eng/verify-stage2-2c.ps1 -Configuration Debug`。证据：`artifacts/stage2-validation/stage2-2c-Debug.log`。构建零警告/错误，70 项单元测试无失败/跳过；包含此前协议、四组合观察、双架构执行控制及各 100 次 continue/pause。最后缓存裁剪收尾另运行 Host 单元测试与双架构 execution/resources，记录在 `artifacts/stage2-validation/stage2-2c-final-quick.log`。

新增断言：

- 启动失败释放名额，两个会话配置下第三个立即 rate_limited、retryAfterMs=1000；单普通槽被等待占用时另一会话 status、本会话 pause 仍可完成。
- 150 ms 同步/异步期限均使目标实际暂停并记录 timedOut；返回异步 operationId 不丢期限；取消 status 不影响原操作。
- 显式 MCP notifications/cancelled 取消等待中的附加会话，终态 cancelled、目标仍存活、可重新附加，另一个会话仍 stopped；重复/未知取消不会结束另一会话。
- Host 受理前已取消的 launch 不创建 Engine；控制容量也有上限；租约重复 Dispose 不多释放。操作 128 条/10 分钟、会话 1024 条/10 分钟与新建前腾位通过可控时间测试。
- 无换行的超过 4 MiB 输入、重复 JSON 字段与非法启动限额明确拒绝；标准客户端仍能握手并调用 13 工具。大变量分别触发 Engine 帧限制和 MCP 双封装限制，缩页后原会话继续可读。

快速审核阻塞修复：取消清理期间的并发 status 不能抢先将操作标成 failed；执行请求取消在受理阶段也标记预期关闭；只读 status 用独立等待期限，不把本次查询取消传播成执行会话关闭；SDK 异步派发前后也有 64 条消息配额，避免仅限制通道却仍无限生成处理任务；终态产生时立即裁剪已完成操作，创建前为会话记录腾位。未保留诊断输出或原始帧日志。

测试发现 SDK 2.2.0 客户端本地取消令牌未可靠在传输上发出取消通知。验收改用 SDK 的 SendRequestAsync/SendMessageAsync 显式指定 ID 并发送真实 notifications/cancelled，同时检查服务端终态和目标存活。该记录不将“客户端停止等待”等同于“服务端收到取消”。SDK 源码的取消链可见 [固定版本 McpSessionHandler](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/src/ModelContextProtocol.Core/McpSessionHandler.cs)，第三方源码未修改。

Host EOF/崩溃及多会话整体收尾在 2-4 专项验证；Release、WinForms 完整矩阵及最终审核按阶段末任务执行。
