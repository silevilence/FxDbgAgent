# DAP 与 VS Code / Cursor

阶段3-5提供可选 `FxDbg.DapHost`，仅通过 stdio 使用 Content-Length 帧。每条连接拥有一个目标；入口只依赖 `FxDbg.Host`，继续复用同一双架构 Engine、断点、源码映射和变量读取逻辑。MCP 保持13个工具，两个协议不能同时附加同一个目标。

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

默认只停未处理异常，`setExceptionBreakpoints` 仅接受空过滤配置。不支持 evaluate、变量修改、条件/命中次数/日志断点、单线程运行、指令级单步、函数断点、restart、源文件传输或网络监听；对应能力不声明。标准错误响应携带稳定错误码；畸形帧/重复JSON字段关闭连接并清理会话。

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
