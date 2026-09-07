# FxDbg Agent

Windows 上的 .NET Framework 4.x 本机只读调试扩展；VS Code / Cursor 共用 DAP over stdio 与 FxDbg Host/Engine。

先运行仓库 `eng/publish-dap.ps1 -Configuration Release`，将设置 `fxdbg.adapterPath` 指向发布目录中的 `fxdbg-dap.dll`，并保证 `fxdbg.dotnetPath` 指向具有 .NET 10 运行时的 dotnet。

启动配置使用 `type: "fxdbg"`、`request: "launch"`、`program` 的绝对路径；附加使用 `request: "attach"`、整数 `processId`。源码映射通过 `sourceMappings` 设置。仅支持 Windows PDB；不执行表达式、属性 Getter 或目标状态修改。默认断开会安全分离并让目标继续运行；附加目标不可被终止。

完整配置、发布及分页限制见仓库 `docs/dap.md`。

Service和完整IIS沿用processId附加，适配器继承编辑器令牌。先核对服务/应用池PID并预热ASP.NET请求；回收后显式附加新PID，不会自动转移旧会话。权限与影子符号流程见源码仓库的docs/service-iis.md。
