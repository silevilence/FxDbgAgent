# DAP 与 VS Code / Cursor

阶段3-5提供可选FxDbg.DapHost，仅通过stdio使用Content-Length帧。每条连接拥有一个目标，入口只依赖FxDbg.Host，复用双架构Engine；阶段4-1的evaluate也使用同一共享后端。MCP原13工具保持兼容，新增debug_evaluate；两个协议不能同时附加同一个目标。

## 发布与配置

Windows 上安装与 `global.json` 匹配的 .NET SDK，然后执行：

```powershell
./eng/publish-dap.ps1 -Configuration Release
./eng/package-extension.ps1
code --install-extension ./artifacts/dap/fxdbg-0.1.0.vsix
# Cursor 使用 cursor --install-extension 同一 VSIX
```

打包扩展使用固定 `@vscode/vsce` 3.6.2，需要 Node/npm；无需扩展运行时 npm 依赖。VSIX 与 `artifacts/dap/Release/` 完整目录配套交付。入口为 `dotnet <目录>/fxdbg-dap.dll`，默认从相邻 `engines/` 加载完整双架构依赖并检查清单；与当前工作目录无关。不要只复制单个 DLL/EXE。

在编辑器设置中指定绝对路径：

```json
{
  "fxdbg.adapterPath": "E:\\tools\\fxdbg\\fxdbg-dap.dll",
  "fxdbg.dotnetPath": "C:\\Program Files\\dotnet\\dotnet.exe"
}
```

`dotnet` 必须具有 .NET 10 运行时。也可在调试配置内指定 `adapterPath`、`dotnetPath`。`.vscode/launch.json` 示例：

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "FxDbg launch",
      "type": "fxdbg",
      "request": "launch",
      "program": "${workspaceFolder}\\bin\\Debug\\MyApp.exe",
      "args": [],
      "cwd": "${workspaceFolder}",
      "arch": "auto",
      "sourceMappings": [
        { "buildRoot": "C:\\build\\MyApp", "localRoot": "${workspaceFolder}" }
      ]
    },
    {
      "name": "FxDbg attach",
      "type": "fxdbg",
      "request": "attach",
      "processId": 12345
    }
  ]
}
```

目标须为真实 .NET Framework 4.x，源码行依赖 Windows PDB。`arch` 为 `auto|x86|x64`；启动支持 `env` 字符串字典。启动、附加均支持[会话源码映射](source-mapping.md)。默认 `stopAtEntry=false`：配置断点后运行；`true` 停在引擎启动入口，此时 CLR 可能尚未创建可枚举托管线程，继续运行到源码断点后观察。

## 直接启动 DAP 连接（不依赖 VS Code）

DAP 入口不依赖编辑器：VS Code/Cursor 扩展只负责启动 DAP 进程并转发 stdio。任何 DAP 客户端都可以直接连接。

1. **发布**（一次）：`./eng/publish-dap.ps1 -Configuration Release`，使用完整 `artifacts/dap/Release/`（含 `engines/` 与 `engine-manifest.json`；不要只复制单个 DLL/EXE）。
2. **启动进程**：

```powershell
dotnet C:/tools/fxdbg/fxdbg-dap.dll
# 可选（唯一启动参数）：--engine-dir C:/tools/fxdbg/engines
```

默认从程序所在目录相邻 `engines/` 加载完整双架构依赖并校验清单；清单不完整时写 stderr 并非零退出。stdout 只输出 DAP 帧，不得混入日志或启动信息。

3. **会话序列**（stdio 逐帧 `Content-Length: <N>\r\n\r\n` + JSON 体，帧限制见「协议与边界」）：

| 顺序 | 请求 | 说明 |
|---|---|---|
| 1 | `initialize` | `{"adapterID":"fxdbg","pathFormat":"path","supportsVariablePaging":true}`；响应先于 `initialized` 事件，并声明 `supportsConfigurationDoneRequest`、`supportsEvaluateForHovers` 等 |
| 2 | `launch` / `attach` | 可先于 `initialized` 发送；`launch` 参数为 `program`、`args`、`cwd`、`env`、`arch`、`stopAtEntry`、`sourceMappings`；`attach` 参数为 `processId`、`arch`、`sourceMappings`；其成功响应在 `configurationDone` 之后返回 |
| 3 | `initialized` 事件后 | `setBreakpoints`（每项可带 `condition`/`hitCondition`，见[条件断点](#条件断点阶段4-3)）→ `configurationDone` → 目标按 `stopAtEntry` 运行 |
| 4 | 控制/观察 | `pause`/`continue`/`next`/`stepIn`/`stepOut`、`threads`/`stackTrace`/`scopes`/`variables`/`evaluate`、`setExceptionBreakpoints`（见[异常类型条件](#异常类型条件阶段4-2)）；恢复运行后旧帧与变量句柄失效 |
| 5 | 结束 | `disconnect`（默认安全分离、目标继续运行；`terminateDebuggee:true` 仅限本入口启动的目标）或直接关闭 stdin（EOF 触发等效安全分离） |

裸请求示例（`launch`）：

```json
{"seq":1,"type":"request","command":"launch","arguments":{"program":"C:/app/MyApp.exe","args":["--mode","x"],"cwd":"C:/app","arch":"auto","stopAtEntry":false}}
```

4. **参考实现**：`tests/FxDbg.DapTests/protocol.js` 是直接连接（不经编辑器）的完整参考——用 Node `spawn('dotnet', [<发布目录>/fxdbg-dap.dll])` 并按上述序列完成启动/附加、断点、暂停/继续、三种单步、栈/变量分页、取消与 EOF 矩阵；`eng/verify-stage3-5.ps1` 运行同一矩阵。
5. **注意**：一条连接对应一个目标（1:1）；MCP 与 DAP 两个协议不能同时附加同一目标；请求（含配置等待）30 秒截止、普通待处理请求最多 32 个、控制请求另有 4 个名额，见「协议与边界」。

## 协议与边界

| DAP 请求 | 行为 |
| --- | --- |
| initialize / launch / attach / configurationDone | 初始化响应先于 initialized；目标先暂停等待断点配置，配置完成后回复启动/附加并按 stopAtEntry 运行 |
| setBreakpoints | 按源码文件替换断点；pending 后绑定发送 breakpoint changed；moved 返回实际行号及说明 |
| continue / pause / next / stepIn / stepOut | 共享执行控制，步进指定线程、全线程停止/恢复；仅源码级单步 |
| threads / stackTrace / scopes / variables | 托管线程、栈、参数/局部/静态字段及按需分页；从不执行 Getter、ToString 或表达式 |
| cancel | 取消指定 requestId；配置阶段取消会安全分离并回复启动失败；无匹配请求时幂等成功 |
| disconnect | 默认安全分离、目标继续运行；terminateDebuggee=true 仅允许终止本入口启动的目标 |
| terminate | 仅允许终止本入口启动的目标 |

默认只停未处理异常，阶段4-2的setExceptionBreakpoints支持下文的firstChance类型过滤。阶段4-1声明supportsEvaluateForHovers，evaluate必须携带当前stackTrace返回的frameId和expression，watch/hover/repl都使用同一只读子集，不执行任意C#或目标函数。对象结果variablesReference可通过variables分页，恢复后失效；默认计算期限250ms。具体语法、白名单、预算及错误见[MCP求值契约](mcp-tools.md#受限表达式阶段4-1)。不支持变量修改、日志断点、单线程运行、指令级单步、函数断点、restart、源文件传输或网络监听；对应能力不声明。标准错误响应携带稳定错误码；畸形帧/重复JSON字段关闭连接并清理会话。

输入头最大8 KiB，消息最大4 MiB，普通待处理请求最多32个，另为控制请求保留4个名额；请求（含配置等待）30秒截止。写锁和输出有独立3秒期限，客户端保持输入开启但不消费输出时会断开并清理会话；Windows上不可取消的输入读取不阻止Host收尾。底层继续/步进保留最长240秒运行期限，达到期限后共享后端尝试暂停，可再继续。EOF、客户端崩溃或输入错误均释放 Host/Engine 并尝试安全 Detach；不会终止附加目标。

编辑器取消过时的线程/栈/变量刷新时，共享Host立即结束客户端等待，后台读取保留原期限和并发名额直到结束，不关闭Engine连接。新停止出现后必须重新获取引用。运行时线程列表使用最近一次停止的快照；没有快照时返回空列表，下一次停止会刷新。

栈每页最多128帧；变量单次最多1024项、字符串最多256字符，不预先遍历全图。数组通过 indexedVariables，字段通过 namedVariables 告知成员数，使用 `start`/`count` 请求页；缺省或 `count=0` 读取剩余全部成员，但超过单次预算会明确报错并要求分页，不静默截断为成功。根作用域尚无总数元数据，未分页读取达到1024项时同样要求分页。帧与变量整数句柄在恢复运行、下一次停止或结束后失效；旧句柄报错并要求重新获取，活动句柄合计最多10000个。无符号、空值、不可用与已优化值保留明确显示状态。

栈的 `levels=0` 或缺省请求全部剩余帧，超过128帧时明确要求用 `startFrame/levels` 分页；显式大于128同样报错。分页最多额外读取1帧，并返回单调增长的 `totalFrames` 提示，最后短页表示栈结束，不将资源截断伪装为栈末尾。

## 验证

```powershell
./eng/verify-stage3-5.ps1 -NodePath 'C:\\tools\\node.exe' -CodePath 'C:\\Program Files\\Microsoft VS Code\\Code.exe'
```

默认 Debug/Release，协议测试覆盖双架构启动/附加、断点、暂停/继续、三种单步、14层栈、变量首尾页和过期引用、取消与EOF。真实 VS Code 通过独立 user-data/extensions 目录、扩展测试入口和原生调试API执行同一矩阵，记录版本、请求/响应/事件摘要；不保存变量值。Cursor 使用同一扩展接口，当前真实客户端验收记录为 VS Code，不将其冒充 Cursor 实测。

完整 `eng/verify-stage3.ps1` 还运行阶段2/1/0回归，需要 .NET 8/10运行时和双架构 Windows Debugging Tools 做独立CDB/SOS对照。默认查找Windows Kits安装位置，也可把 `FXDBG_DEBUGGERS_DIRECTORY` 设为含 `x86/cdb.exe`、`x64/cdb.exe` 的官方工具展开目录绝对路径；缺失时在全量测试开始即报错，不跳过对照。

协议依据：[DAP 概述](https://github.com/microsoft/debug-adapter-protocol/blob/main/overview.md)、[VS Code 调试扩展接口](https://code.visualstudio.com/api/extension-guides/debugger-extension)、[激活事件](https://code.visualstudio.com/api/references/activation-events)。

Service与完整IIS使用现有processId附加配置；适配器继承编辑器令牌，权限、PID核对、影子PDB和回收后重新附加见[Service/IIS指南](service-iis.md)。完整阶段3脚本默认包含3-4并需管理员；显式-SkipServiceIis只运行子集，结果passed=false、subsetPassed单独报告且列出skipped。真实VS Code另覆盖Service/IIS各双架构附加、源码断点和安全分离。

## 异常类型条件（阶段4-2）

initialize 声明 supportsExceptionFilterOptions，firstChance 过滤器支持 condition。`setExceptionBreakpoints({filters:[],filterOptions:[{filterId:"firstChance",condition:"exact:MyApp.Error;namespace:MyApp.Errors;derived:MyApp.BaseError"}]})` 使用共享类型规则，分号之间为 OR；类型名称、继承及上限见 [MCP契约](mcp-tools.md#会话异常类型过滤阶段4-2)。filters 必须为数组；exceptionOptions 仅接受空数组，未知过滤器和非法规则拒绝。

DAP filters 与 filterOptions 是相加的 OR：filters:["firstChance"] 表示无条件全 first-chance，即使同时提供条件选项也保持全匹配。空/省略 condition 的 firstChance 选项同样全匹配。`filters:[]` 且无 filterOptions 清空，仅未处理异常停止。响应 breakpoints 按 filters 后 filterOptions 顺序返回 verified:true。可在运行中更新，不伪造 stopped 事件。实现不执行目标格式化或求值。

## 条件断点（阶段4-3）

initialize声明supportsConditionalBreakpoints及supportsHitConditionalBreakpoints。setBreakpoints的每项接受condition、hitCondition，语义与[MCP契约](mcp-tools.md#条件断点阶段4-3)相同；空字符串按未设置处理。表达式必须为bool、250ms期限，错误保守停止并在Breakpoint.message显示脱敏诊断。日志断点仍拒绝。

同一文件的请求列表按位置和两个条件匹配既有断点，未变条目保留ID与实际命中计数；增加/删除其他条目不重置它，修改条件或删除重建则从0开始。全部条目先校验再更新；不因隐藏命中次数变化发送breakpoint changed事件。失败后可移除断点并继续。共享CLI/MCP查询可查看完整命中计数；DAP使用标准Breakpoint响应和stopped事件，不添加私有计数字段。
