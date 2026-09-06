# AppDomain 识别与筛选

`debug_status` 的 `appDomains` 列出已观测且仍存在的域：`appDomainId` 为本次会话中每次加载的唯一字符串，`name` 为友好名称，`runtimeId` 为CLR提供的诊断编号。以`appDomainId`选域，不以名称或runtimeId作为长期身份；同名域可以同时存在，卸载后同名重建会获得新标识。最多跟踪1024个活动域，超过时明确报错。

`debug_threads`、`debug_stack`、`debug_variables`、`debug_status`与创建`debug_set_breakpoint`可带可选`appDomainId`，省略则沿用观察全部域的行为。未知、其他会话或已卸载域返回`invalid_request`，应重新查询status。

- 线程按当前执行域筛选。栈按各托管帧所属模块的域筛选，在筛选后的帧序列分页；frameId仍标识原物理帧，不能把筛选后的序号拼作frameId。
- 变量的`appDomainId`/`appDomain`表示读取帧所在的域上下文，不承诺推断跨域代理对象的实际堆归属。指定的域必须与帧一致。引用按域隔离；整个停止状态共用10000个对象引用上限，恢复运行或域卸载清理旧引用。
- 断点的`appDomainId`表示可选的限定域，`boundAppDomainIds`列出现有绑定域。限定域卸载后断点回到pending，不跟随同名新域。为新域创建新断点；已有断点只允许切换enabled，不能悄悄更换其域范围。
- status筛选域列表、模块及适用于该域的断点；会话状态、停止原因和执行操作仍属整个进程，不因筛选隐藏其他域导致的停止。

Engine事件新增`appDomainChanged`（created/updated/exited，CLR初始临时名称通过NameChange更新）和`threadChanged`，事件内携带域信息；模块和停止事件亦携带域标识。回调线程仍只入队，域跟踪、符号处理与事件生成在Engine命令线程执行。MCP继续通过status快照观察，CLI可读取既有events通道。

CLI的threads、stack、variables、break命令提供`--app-domain <标识>`。暂停/继续仍作用于一个目标进程，并非只暂停或恢复某个AppDomain。first-chance过滤、条件断点与用户代码求值不在此能力范围内。

`eng/verify-stage3-3.ps1`执行真实双架构同名域样例，验证指定域线程/断点、跨域托管栈、变量域隔离，以及卸载后重建的重新绑定。最终全量回归同时覆盖前序无筛选行为。
