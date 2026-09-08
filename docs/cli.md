# CLI 使用

阶段4-1增加`fxdbg evaluate --session ID --frame FRAME --expression "number + matrix[1,1]"`。可选`--evaluation-timeout-ms 1..1000`（默认250）、`--max-depth`、`--count`、`--max-string-length`及`--app-domain`，显示上限同variables。结果为单个VariableInfo；对象引用用variables继续分页，恢复后重新取帧。仅支持[MCP契约](mcp-tools.md#受限表达式阶段4-1)定义的只读解释子集，目标不执行函数，不调用Getter或ToString。计算超时/取消不增加Continue；原生读取卡死的隔离边界见ADR-004。

开发构建后可使用 `src/FxDbg.Cli/bin/Debug/net10.0-windows/fxdbg.exe`，或 `dotnet .../fxdbg.dll`。发布布局在 CLI 旁放置 `engines/`，也可用 `--engine-dir` 指定含双架构 Engine 的目录。

```powershell
$cli = './src/FxDbg.Cli/bin/Debug/net10.0-windows/fxdbg.exe'
$launch = & $cli launch --exe 'C:/samples/app.exe' --arg 'argument with spaces' --arch auto --stop-at-entry | ConvertFrom-Json
$session = $launch.sessionId
& $cli break --session $session --file 'C:/samples/Program.cs' --line 20
& $cli continue --session $session
$stop = & $cli wait --session $session --timeout-ms 15000 | ConvertFrom-Json
$thread = [string]$stop.result.threadId
$stack = & $cli stack --session $session --thread $thread | ConvertFrom-Json
& $cli variables --session $session --frame $stack.result[0].frameId --max-depth 1
& $cli step --session $session --kind over --thread $thread
& $cli wait --session $session
& $cli detach --session $session
```

附加：`fxdbg attach --pid 1234 --arch auto`，然后使用返回的 sessionId。完整命令见 `fxdbg --help`，另有 pause、threads、modules、events、state、断点管理、异常策略和 terminate（仅允许由调试器启动的目标）。

每条 CLI 命令返回一个 JSON 对象，成功为 ok/sessionId/result，错误写 stderr 并以非零退出。所有帧 ID 和变量引用必须来自当前停止的返回结果，不能猜测为数字；Continue 或 Step 后旧引用失效。`--arg` 可重复并原样保留字符串；`--args` 按 Windows 引号规则拆分。启动支持 `--cwd` 和重复 `--env NAME=VALUE`。

每个会话持有一个后台 Host 和匹配架构的 Engine，CLI 命令之间不重复附加。Host 由显式不继承句柄的 detached 进程启动，避免阻塞 PowerShell 的输出捕获。控制管道限同一用户、本机访问；30 分钟没有 CLI 连接则自动清理。Detach 结束会话并让后台进程退出；CLI 在请求期间被取消或异常断开时，尝试安全分离。

后台服务位于 FxDbg.Host，CLI 仅解析参数和传递消息；所有执行控制、断点、栈与变量逻辑都由同一 Host/Engine 提供。MCP 外壳按阶段 2 实施，本阶段没有第二套调试后端。生命周期的实测边界见 [Engine 协议](engine-protocol.md)。

对象与数组分页沿用共享读取器，边界和取消语义见 [对象与数组分页](variable-paging.md)。

launch/attach 支持 --source-maps <JSON文件>，数组字段与MCP的sourceMappings一致，详见 [源码映射](source-mapping.md)。

threads、stack、variables、break支持 --app-domain <status中的appDomainId>；详细语义见 [AppDomain识别](appdomains.md)。CLI通过state的域列表及events查看生命周期。

Service/IIS使用同一个attach入口；先核对服务/应用池的当前PID并为IIS请求预热，权限、影子符号、回收及安全分离见[Service/IIS指南](service-iis.md)。CLI已纳入两类目标的双架构真实断点/分离验收。
