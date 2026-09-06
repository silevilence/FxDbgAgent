# 🗺️ Product Roadmap

> 项目：FxDbg Agent —— 面向 AI Agent 的 .NET Framework 4.0 调试器
> 依据：《FX 4.0 AI 调试器需求文档》§12（分阶段实施）与 §13（验收标准）
> 里程碑：**MVP = 阶段 0 + 阶段 1 + 阶段 2**，对应需求验收标准 1~9；阶段 3/4 在 MVP 验收后启动

## 📝 计划中 (Planned)

### 阶段 4：高级能力（需先完成安全评估）

- [ ] **阶段4-1 受限表达式求值**——安全评估通过后设计受限求值
    - 验收：求值能力不改变业务状态，超时与取消可靠。

- [ ] **阶段4-2 first-chance exception 过滤**——按类型配置停止条件
    - 验收：可只对指定异常类型在 first-chance 停止。

- [ ] **阶段4-3 条件断点**——基于简单表达式或命中次数的条件
    - 验收：不命中条件的断点不触发停止。

- [ ] **阶段4-4 Streamable HTTP MCP 与远程宿主**——评估安全边界后实施
    - 验收：远程会话可用且经过认证与权限控制。

## 🚧 开发中 (In Progress)

### 阶段 3：可用性增强（MVP 验收后启动）

> 无人值守实施约定（2026-09-06，用户已确认）：本轮顺序为阶段3-1 → 阶段3-2 → 阶段3-3 → 阶段3-5；阶段3-4暂时跳过，保持未勾选，不安装 IIS 或创建系统服务。本轮完成不代表阶段3-4完成。
> 开始前将本节确认内容与既有分区调整单独本地提交，然后启动目标；每项测试与快速审核通过、阻塞修复后才原地勾选并独立本地提交。当前 main 分支执行，不推送。最终以准备提交为固定基线，两个独立子代理分别审核规范与需求，修复并复核；运行 Debug/Release 全量回归（含阶段2及共享后端），将命令、结果、源码哈希与清理证据记录到 docs/validation/ 和 artifacts/stage3-validation/。
> 统一约束：保持现有13个MCP工具兼容，以可选参数和附加结果字段扩展；CLI、MCP、DAP复用 FxDbg.Host/Engine；保持双架构、回调仅入队、单调度线程、Continue配对、只读观察与安全分离纪律。未通过或缺失真实验收不得记为通过；测试进程有总期限且只清理自身创建的资源。

- [x] **阶段3-1 对象与数组分页展开**——大对象/数组分页读取，深度、数量、字符串长度限制调优
    - [x] 超过10万元素数组及对象图按需读取，覆盖首/中/尾页、循环引用、运行后引用失效与有界资源；不得先遍历整个对象图再分页
    - [x] 保留深度、数量、字符串长度边界，验证越界页及取消；同步MCP契约、CLI与技能文档
    - 验收：真实Windows x86/x64样例与MCP链路通过；本机预热后默认页读取P95≤1秒、单次≤5秒，另记录冷启动耗时（本项目量化标准，需求§10.3未指定数值）。
    - 验证记录：Debug/Release共76项单元及真实MCP双架构分页通过；快速审核已修复测试页位置表达式和随包文档同步，见 docs/validation/stage3-1-paging.md。

- [ ] **阶段3-2 源码路径映射**——构建机路径与本地工作区路径不一致时的映射（全局 + 模块级）
    - [ ] 支持会话内全局与模块级映射；模块级优先，同级最长路径前缀匹配，按路径段边界匹配，歧义明确报错
    - [ ] 同时映射断点输入与栈帧源码输出，保留原始符号路径以便诊断；覆盖含空格、大小写与未映射路径
    - 验收：真实双架构样例的构建路径与本地路径不同，映射后断点绑定、命中与栈帧源码行正确；模块延迟加载后仍可绑定，既有无映射调用兼容。

- [ ] **阶段3-3 多 AppDomain 识别与筛选**——事件、栈、变量携带 AppDomain 信息，支持按需过滤
    - [ ] 提供稳定域标识与名称，覆盖事件、线程、栈、变量及断点；默认全部域，可按域筛选
    - [ ] 验证同一程序集多域加载、域卸载与重建后的断点绑定及引用清理，不用域名称作为唯一身份
    - 验收：真实双架构多域样例可定位指定域线程与断点，跨域栈与变量归属正确，卸载后不返回陈旧引用；未指定筛选时保持原行为。

- [ ] **阶段3-4 Windows Service 与 w3wp 附加增强**——SeDebugPrivilege 权限路径、IIS 影子复制与动态模块加载
    - 本轮状态：用户于2026-09-06明确允许先跳过；不实施、不验收、不勾选，留待具备管理员与真实Service/IIS环境后完成。
    - 验收：可在管理员权限下附加运行中的 Service 与 w3wp 并正常断点。

- [ ] **阶段3-5 可选 DAP Host**——复用同一 Engine，支持 VS Code / Cursor 调试界面
    - [ ] 本轮作为必做项，交付DAP over stdio Host、最小VS Code/Cursor扩展及配置/发布文档，复用共享Host/Engine
    - [ ] 支持启动、附加、源码断点、继续/暂停、三种单步、线程/栈/作用域/变量分页与安全分离；仅声明实际支持能力，不支持求值或修改状态
    - [ ] 覆盖协议初始化、配置完成、停止/退出事件、取消与断开清理，恢复运行后使帧和变量引用失效
    - 验收：真实DAP协议回归与真实VS Code调试会话完成启动、附加、断点、三种单步、栈、变量分页及分离，记录可复核客户端证据；双架构Debug/Release通过。

## ✅ 已完成 (Completed)

### 阶段 0：技术验证（先于一切产品开发，需求 §12）

- [x] **阶段0-1 搭建解决方案骨架与 FX 4.0 调试样例工程**
    - [x] 按需求 §15 建立 `src/`、`tests/`、`docs/` 目录与解决方案
    - [x] 4 个调试样例（Fx40.Console.x86 / x64、Fx40.WinForms.x86 / x64）可编译
    - [x] 明确 SDK 版本、net40 目标构建方式（Microsoft.NETFramework.ReferenceAssemblies）并写入 AGENTS.md
    - 任务描述：net40 默认产出 **Windows PDB**（文件头 MSF 7.00），验收时须确认；禁止用 portable PDB 验证符号路径。
    - 验收：一条命令构建全部样例；每个 `.pdb` 文件头为 MSF 7.00；WinForms 样例可正常启动显示窗口。

- [x] **阶段0-2 用 ClrDebug 初始化 CLR v4.0.30319 的 ICorDebug 会话**
    - [x] 通过 ClrDebug 创建 ICorDebug 对象并初始化
    - [x] 启动简单 FX 4.0 控制台程序（x86）并进入调试会话
    - [x] 附加已有 FX 4.0 进程（x86）并进入调试会话
    - 任务描述：ClrDebug（MIT，NuGet 0.4.2）为固定版本第三方依赖；许可证与维护状态记录进 `docs/architecture.md`。
    - 验收：启动与附加均产生正常 Process 回调；全程无托管异常；记录 ClrDebug 版本与许可证结论。

- [x] **阶段0-3 双架构矩阵验证（x86→x86、x64→x64）**
    - [x] 验证启动/附加 × x86/x64 四种组合全部成功
    - [x] 验证错误路径：跨位数附加、原生进程、CoreCLR、不支持的 CLR 版本
    - [x] 附加失败时目标进程继续运行
    - 验收：四组合成功；每种错误路径返回明确可读原因，且目标进程存活。

- [x] **阶段0-4 收集核心回调（Process / AppDomain / Assembly / Module / Thread）**
    - [x] 确认五类回调均能收到
    - [x] 验证回调在专用托管回调线程上触发，回调内不做耗时工作（仅入队）
    - [x] 回调打点日志可按级别配置
    - 验收：一次完整启动序列可观测全部五类回调；日志含会话、进程、线程信息；回调线程无阻塞迹象。

- [x] **阶段0-5 Windows PDB 符号读取与源码行→IL Offset 映射**
    - [x] 读取 Windows PDB（基于 DIA 的 Microsoft.DiaSymReader 系包）
    - [x] 给定「源文件 + 行号」得到方法名与 IL Offset 集合
    - [x] 区分并报告 PDB 缺失 / 版本不匹配 / 读取失败三种状态
    - 任务描述：固定已修复版本（注意 CVE-2023-36796，畸形 PDB 存在 RCE 风险），将 PDB 视为不可信输入。
    - 验收：对样例已知源码行，映射结果与人工核对一致；三种异常状态各出一份报告样例。

- [x] **阶段0-6 源码断点命中与调用栈验证**
    - [x] 按源码行设置断点并命中
    - [x] 输出至少 10 层托管调用栈（方法全名、模块、IL Offset、源文件行号）
    - 任务描述：断点绑定若失败，须能区分 pending / unresolved。
    - 验收：断点命中且停止位置正确；10 层栈字段齐全，抽样与 WinDbg 对照一致。

- [x] **阶段0-7 输出技术验证报告与关键决策**
    - [x] 汇总可行/不可行项、ClrDebug 依赖结论、PDB 读取方案定案、风险清单更新
    - [x] 生成 `docs/architecture.md` 初稿
    - 验收：报告经用户确认后才开启阶段 1；任何推翻需求 §16 决策的结论须显式列出。

### 阶段 1：调试引擎 MVP

- [x] **阶段1-1 FxDbg.Core：会话状态机与领域模型**
    - [x] 状态集合：created / starting / running / stopped / detaching / terminated / failed
    - [x] 断点、线程、栈帧、变量、异常的统一模型
    - [x] 引擎请求与事件定义、统一错误码
    - [x] 所有状态变更产生结构化事件
    - 验收：单元测试覆盖非法迁移（未停止时继续、重复继续等）；事件序列完整可枚举。

- [x] **阶段1-2 Engine 进程宿主：启动 / 附加与架构探测**
    - [x] 启动入口：exe 路径、参数、工作目录、环境变量、`arch`（auto | x86 | x64）
    - [x] `auto` 模式按 PE + CorFlags 探测（含 AnyCPU 32BitPreferred 判定）
    - [x] 附加入口：PID + `IsWow64Process2`（回退 `IsWow64Process`）
    - [x] 位数不符时干净退出并返回可操作错误，绝不静默跨位数附加
    - [x] 启动模式返回实际架构、PID、CLR 版本
    - 验收：脚本矩阵跑通全部组合；错误场景错误信息可操作；退出码与错误码映射文档化。

- [x] **阶段1-3 单线程命令调度队列与回调桥**
    - [x] ICorDebug 回调仅入队，绝不直接执行命令
    - [x] 所有调试命令在单线程调度队列串行执行
    - [x] 严格 Continue 计数与停止/运行状态配对
    - [x] 每个运行操作支持超时与取消
    - 验收：100 次连续继续/暂停交替无重复 Continue、无死锁；取消在超时内生效并产生一致状态。

- [x] **阶段1-4 断点管理（完整绑定生命周期）**
    - [x] 源码断点设置 / 删除 / 启用 / 禁用
    - [x] 模块未加载时的 pending 断点，模块加载或 PDB 就绪后自动重绑定
    - [x] 状态：pending / verified / moved / unresolved，移动到最近可执行行时明确告知
    - 验收：对延迟加载程序集与模块卸载重载场景，状态迁移正确且事件完整。

- [x] **阶段1-5 执行控制：Continue / Pause / Step / Detach / Terminate**
    - [x] Continue、Pause、Step Into / Over / Out（单步必须指定线程）
    - [x] Detach（启动与附加两种模式均可）、Terminate（仅限由调试器启动的目标）
    - [x] 非法状态下调用返回明确错误
    - 验收：step 后停止位置与源码行吻合；detach 后目标继续运行；terminate 后目标退出且会话终止。

- [x] **阶段1-6 托管线程枚举与调用栈**
    - [x] 枚举托管线程（线程 ID、名称、AppDomain、当前停止状态）
    - [x] 指定线程的托管调用栈：帧 ID、方法全名、模块/程序集、IL Offset、源文件行号、AppDomain
    - [x] 首期忽略纯原生帧与混合模式帧
    - 验收：对递归 + 多线程样例，10 层以上栈逐帧字段齐全；帧计数与 WinDbg 对照一致。

- [x] **阶段1-7 参数与局部变量读取**
    - [x] 方法参数与局部变量：基础类型、字符串、数组、对象字段、静态字段
    - [x] 按需展开：最大深度、最大成员数、最大字符串长度，对象分页读取
    - [x] 循环引用返回引用标识，不递归展开
    - [x] 默认不调用属性 Getter、`ToString()` 或任何用户代码
    - [x] 明确区分「无法获取 / 已优化掉 / 空值」
    - 验收：循环引用、大数组、优化编译组合场景输出正确；读取时间满足需求 §10.3 性能要求。

- [x] **阶段1-8 未处理托管异常捕获**
    - [x] 捕获未处理异常：类型、消息、线程、抛出位置、调用栈
    - [x] 不调用目标对象自定义格式化代码生成异常内容
    - [x] first-chance 停止默认关闭、可配置
    - 验收：主动抛异常的样例程序被正确报告，且内容不来自目标自定义代码。

- [x] **阶段1-9 模块 / PDB 跟踪与符号状态**
    - [x] Module Load / Unload 事件跟踪
    - [x] DLL 与 PDB 匹配识别
    - [x] 符号状态：已加载 / 不存在 / 不匹配 / 读取失败
    - 验收：对 PDB 缺失与版本不匹配样例输出正确符号状态；模块卸载时清理对应缓存。

- [x] **阶段1-10 Engine↔Host 协议定形与双进程健壮性**
    - [x] Named Pipe + JSON-RPC 帧格式、请求/响应/事件三通道
    - [x] 心跳、超时、双向断开检测
    - [x] Engine 崩溃不拖垮 Host，Host 崩溃时 Engine 尝试安全 Detach
    - 验收：模拟双方崩溃，另一方均能检测并清理，无僵尸进程、无半开会话、无泄漏句柄。

- [x] **阶段1-11 CLI 验证入口（复用同一 Host/Engine）**
    - [x] `fxdbg launch/attach/break/continue/step/stack/variables/detach` 全套命令
    - [x] CLI 与 MCP 共享 FxDbg.Host，不维护第二套调试逻辑
    - 验收：需求 §9 全部命令可用，输出语义与 MCP 结果同源一致。

- [x] **阶段1-12 端到端集成测试（Console + WinForms，双架构）**
    - [x] 示例场景：Hello 断点、递归 + 变量、未处理异常、延迟加载模块、循环引用对象
    - [x] 覆盖需求验收标准 1~6、8~9（调试能力由共同 Host/Engine 验收；实际 MCP 入口按确认范围留到阶段 2）
    - 验收：自动化脚本全量通过；后续回归一键执行。

### 阶段 2：Agent 接入（MCP）

> 无人值守实施补充（2026-09-05）：原四项描述不足以独立实施，以下补齐工具契约、执行边界、依赖与验收。阶段 1 已完成的依据为 `docs/validation/stage1-final-review.md`；本节所有未勾选项仍是待开发任务。
> 执行顺序：阶段2-1 → 阶段2-2a → 阶段2-2b → 阶段2-2c → 阶段2-4 → 阶段2-3 → 阶段2-5；保留原任务编号，阶段2-2拆为三个约 1～3 天的交付单元。前置任务验收通过后才启动后继任务。
> 统一约束：复用 `FxDbg.Host` / Engine；共享的操作跟踪、停止等待和会话清理放在 Host，MCP 只做协议适配，不调用 CLI 子进程实现工具、不加载 Interop / ClrDebug / mscordbi。保留阶段 1 的只读观察、双架构路由、单调度线程和 Continue 配对规则，不提前实现阶段 3/4。
> 完成纪律：每项记录验收命令、配置、结果和证据路径后才勾选；失败先修复再继续。缺少真实外部 Agent、登录凭据或运行权限时记录阻塞及所需条件，继续不依赖它的本地验证，不以跳过代替通过，也不自动改写需求 §16 的决策。
> Agent 验收口径更新（2026-09-05，用户明确指定）：本轮由独立子代理读取文档与安装后的技能，自主调用真实 MCP stdio 服务并留存请求/结果，作为下文 Agent 验收证据；禁止使用 Claude Code，不要求外部账号登录。固定脚本回归仍不可冒充子代理自主验收。此指定覆盖本节原有外部客户端选择条件。

- [x] **阶段2-1 MCP stdio Host 骨架**
    - [x] 新建 `src/FxDbg.McpHost` 并加入解决方案；使用官方 C# `ModelContextProtocol` SDK 的 stdio 宿主，实施时选择支持下述协议且兼容当前目标框架的最新稳定版，固定精确包版本并记录许可证与支持的协议版本，后续任务不浮动升级
    - [x] 以 MCP `2025-11-25` 为验收基线，完成 initialize 版本协商、`notifications/initialized`、tools/list、tools/call、ping、`notifications/cancelled`；不支持的版本、未初始化调用和非法请求按协议处理；仅声明实际支持的 tools 能力
    - [x] MCP 外部 stdio 使用逐行 UTF-8 JSON-RPC；内部 Named Pipe 继续使用既有长度前缀帧，两种帧格式不得混用。stdout 仅协议内容，SDK/Host/Engine 日志及目标程序输出均不得混入
    - [x] 定义启动与发布布局：MCP 入口旁 `engines/` 包含完整双架构 Engine 及其托管/本机依赖；支持显式 `--engine-dir`，路径含空格、任意工作目录均可启动；缺少依赖时 stderr 给出可操作错误并非零退出
    - [x] 建立 `docs/mcp-tools.md`：列明 13 个工具名称、输入/输出 schema、字段类型、必填项、默认值、范围、合法状态及错误示例；launch/attach 由 Host 生成 sessionId，其余工具必须显式携带 sessionId；未知字段或错误类型在进入 Engine 前拒绝
    - 验收：自动化标准 MCP 客户端完成握手、列出 13 个完整 schema、调用不存在会话的 debug_status 并得到结构化错误；覆盖未初始化、版本协商、畸形 JSON、未知方法/工具和 EOF。捕获 stdout 逐行解析，证明启动信息、异常日志和目标输出均未污染协议；不得将占位成功响应作为工具完成证据。
    - 验证记录：Debug 验收通过，快速审核阻塞项已修复；见 docs/validation/stage2-1-stdio.md。

- [x] **阶段2-2 13 个 MCP 工具实现（需求 §8，下列三个子任务全部通过后勾选）**
    - 任务描述：需求 §8 实际列出 13 个工具，原“14 个”为计数错误。工具使用完整 `debug_` 前缀；操作轮询通过 debug_status 完成，不为凑数量增加工具。

- [x] **阶段2-2a 会话、断点与观察工具**
    - [x] 实现 `debug_launch`、`debug_attach`、`debug_set_breakpoint`、`debug_remove_breakpoint`、`debug_status`、`debug_threads`、`debug_stack`、`debug_variables`，参数映射既有 Core 请求与 Host/Engine 命令
    - [x] launch 支持 exe、字符串数组 args、cwd、env、arch、stopAtEntry；attach 支持 pid、arch；arch 默认 auto，stopAtEntry 默认 false，文档断点流程显式使用 true。返回 sessionId、PID、实际架构、CLR 承载标识/文件版本与会话状态；同一 MCP 连接支持相互隔离的会话，一个 PID 仍只允许一个调试会话
    - [x] set_breakpoint 支持 file、line、enabled（默认 true），可携带已有 breakpointId 仅更新 enabled；新增、更新与删除均复用现有断点管理。返回 pending / verified / moved / unresolved、请求行及实际绑定位置；模块/PDB 后续变化可由 status 查询，不以首次 pending 作为最终失败
    - [x] status 返回当前状态、最近停止/退出观察、断点与模块符号状态，以及可选 operationId 对应的操作结果；通过 Host 持续消费既有事件维护观察，标明事件序号与快照时间，不将历史停止位置标成当前运行位置。MVP 无需客户端订阅自定义通知即可完成流程
    - [x] stack 必须指定 threadId，默认 start=0、count=32；variables 必须指定 frameId，展开时再带 referenceId，默认 start=0、count=100、maxDepth=1、maxStringLength=256；沿用 Core 上限（深度 8、成员 1024、字符串 32768）。帧及引用均是不透明字符串，恢复运行后失效，禁止跨会话复用
    - [x] 结果在 MCP structuredContent 中提供统一对象：成功含 ok=true、sessionId、result，失败含 ok=false、sessionId（尚未创建时可空）、error.code/message；同时返回序列化 JSON 的 text content。枚举与 ID 沿用 Core 线格式，禁止输出 COM 对象或要求 Agent 解析日志
    - [x] 非法会话状态、目标类型、位数、符号与失效引用等业务错误使用 MCP 工具结果 isError=true，沿用 Core 稳定错误码；JSON-RPC 封装错误/未知工具按 MCP 协议错误处理。错误说明可操作且不泄漏原始异常堆栈或敏感请求内容
    - 验收：八个工具的文档示例经真实 stdio 调用并验证 schema；覆盖 x86/x64 自动路由、含空格路径与参数、环境变量、断点移动与重绑定、符号异常、优化掉/不可获取/null、对象分页与循环引用、失效及跨会话 ID。超大变量结果可缩页重试且原会话仍有效。
    - 验证记录：Debug 四组合及 64 项单元测试通过，快速审核阻塞项已修复；见 docs/validation/stage2-2a-observations.md。

- [x] **阶段2-2b 执行控制与操作轮询**
    - [x] 实现 `debug_continue`、`debug_step`、`debug_pause`、`debug_detach`、`debug_terminate`；step 必须携带 threadId 与 kind=into/over/out，terminate 仅限 launch 创建的目标；状态判断以共享 Host/Engine 为准
    - [x] continue/step 使用 waitForStop（默认 true）：true 等待一次新的停止或进程退出；false 在命令受理后返回 operationId，并由 debug_status(sessionId, operationId) 轮询。两种模式共用一次执行操作，不能在轮询或重试时再次 Continue/Step；不得把 MCP request id 或 MCP Tasks 扩展当作此 operationId
    - [x] 明确定义操作状态 running / completed / timedOut / cancelled / failed，completed 携带对应 StopInfo 或 processExit；一次运行操作最多产生一个终态，捕获命令返回前已发生的停止，重复轮询结果一致，旧停止事件不能完成新操作
    - [x] 每会话最多一个运行操作；运行期间 status、pause、detach 及合法 terminate 仍可响应。pause 使运行操作以 userPause 停止完成；detach/terminate、超时、取消及 Engine 故障均结束关联等待，不占住 Engine 调度线程等待客户端轮询
    - [x] 已完成操作仅在本 MCP 进程保留，默认最多每会话 128 条且终态保留 10 分钟；已结束会话保留终态查询同样 10 分钟，Host 总计最多保留 1024 条会话记录，超限淘汰最早终态。未知、跨会话或已淘汰 operationId 返回 operation_not_found；Host 重启不恢复旧会话或操作
    - 验收：五个工具及两种等待模式覆盖断点、三种单步、暂停、自然退出、未处理异常；另测立即停止竞态、重复 continue、并发操作、旧操作轮询、操作过期、跨会话误用。连续 100 次 continue/pause 无重复 Continue 或死锁，等待中其他会话可用。
    - 验证记录：Debug 双架构执行矩阵、66 项单元测试及原生执行回归通过；见 docs/validation/stage2-2b-execution.md。

- [x] **阶段2-2c 超时、取消、限流与资源边界**
    - [x] 每个工具有 timeoutMs，默认 10000、允许 1～240000 毫秒；continue/step 的期限覆盖命令受理和等待，异步返回不取消后台操作的期限。status 的超时只影响本次查询，不延长或取消正在执行的操作
    - [x] 区分协作超时与传输故障：协作超时尝试暂停并返回 operation_timed_out 与实际会话状态；暂停/传输失败则按现有 Host 规则安全清理并报告失败，不能声称目标仍可继续。取消通过 MCP notifications/cancelled 关联尚未完成的请求，传递到共享 Host；已返回的异步运行通过 pause 停止、或 detach 结束会话
    - [x] 取消通知本身不产生响应；取消与成功竞争时至多形成一次终态，不误取消其他请求。正在执行的取消沿用 `docs/engine-protocol.md` 的安全分离语义；尚未进入 Engine 的取消无目标副作用，文档说明操作可能已执行，禁止盲目重试 launch/attach/continue/step
    - [x] 默认每 Host 最多 8 个活动/启动中会话、32 个在途普通工具调用；超限立即返回 rate_limited 并附 retryAfterMs=1000，不无界排队。状态查询与清理控制单独保留有界容量，确保普通调用饱和时仍能取消/暂停/分离；上限可由启动配置降低，非法配置拒绝启动
    - [x] 为请求体、结果及事件/操作缓存设界限：MCP 单条输入最多 4 MiB，结果封装前验证序列化体积，超限返回 invalid_request 并提示缩页，不截断 JSON；既有 Engine/Host 事件队列及时消费，溢出必须显式失败并清理，禁止无限积压或静默漏失停止事件
    - 验收：用短期限覆盖受理前取消、等待中取消/超时、成功竞争、未知/重复取消和启动失败；验证每次最终状态与目标实际状态一致。饱和调用下 status/pause/detach 可用、另一会话不被误取消；缓存过期和容量上限可自动断言。
    - 验证记录：Debug 全前序回归、70 项单元测试及双架构资源专项通过；见 docs/validation/stage2-2c-resources.md。

- [x] **阶段2-3 Agent 使用文档与 SKILL**
    - [x] `docs/agent-skill.md` 与 `docs/mcp-tools.md` 提供构建/发布、MCP command/args/env 配置、13 个工具示例、默认限额、错误恢复、异步轮询及断开清理说明；运行路径不能绑定开发者个人目录
    - [x] 提供 `skills/fxdbg-agent/SKILL.md`，含 name=fxdbg-agent 与 description 的 YAML frontmatter；技能自带必要流程及随包 references，不依赖安装后不可达的仓库相对路径，文档与技能示例保持一致
    - [x] 固定验证用 skills CLI 精确版本，在临时项目目录运行 `npx --yes skills@<固定版本> add <仓库绝对路径> --skill fxdbg-agent --agent <验证客户端> --yes`，再检查实际安装文件与引用；记录展开后的命令，不修改用户全局 Agent 配置。安装方式依据 [skills 官方说明](https://github.com/vercel-labs/skills)
    - [x] 文档流程明确：stopAtEntry=true 启动 → 设置断点 → 继续并等待 → 取实际 threadId/frameId → 查看变量 → 单步/继续 → detach 或允许的 terminate；说明运行后引用失效、first-chance 默认关闭，以及禁用求值、Getter 和 ToString
    - [x] 由独立子代理完成同一流程，记录代理身份、可获取的模型信息、隔离配置、提示词、实际 MCP 工具调用和结果；子代理根据每次真实响应自主决定下一步，可使用透明 stdio 客户端桥接，不能以 Inspector 或固定脚本执行记录冒充自主验收。禁止使用 Claude Code
    - 验收：技能通过 npx skills 非交互安装且安装后引用完整；真实 Agent 仅凭文档和技能完成工具链并正确解释一次结构化错误，输出可复核证据到 `docs/validation/stage2-3-agent.md`，敏感内容脱敏。

- [x] **阶段2-4 生命周期与安全清理**
    - [x] stdio EOF、输出管道断开、Agent 退出、Host 正常退出/被终止均触发所属会话清理；覆盖 starting/running/stopped/等待中状态，清理可重入且有期限，多会话清理不串行累加为无限等待
    - [x] 非显式 terminate 的退出默认对 launch/attach 目标均尝试安全 Detach，目标恢复运行；附加目标禁止调用 terminate 或进程树终止。自然退出与显式 detach/terminate 后及时释放 Engine、管道、事件消费者及操作资源
    - [x] 正常清理及 Host 被终止测试要求 15 秒内 Engine 全部退出，附加目标存活且可再次附加；清理失败记录实际状态。Engine 本体硬崩溃按阶段 1 已实测限制单独验证：Host 存活并报告错误，不承诺此场景目标存活，不将强杀 Engine 用作“安全分离成功”的证据
    - [x] 日志默认仅含 sessionId、PID、线程、命令、状态与稳定错误码；提供日志级别和完全禁用值日志配置。变量值、启动参数/env 内容、异常消息及 SDK 原始工具报文不进入日志，配置为调试级别也不能绕过脱敏；业务工具结果仍按契约返回所请求的数据
    - 验收：真实 x86/x64 launch/attach 故障矩阵覆盖 EOF、Host 强杀、Engine 崩溃、清理重复、启动中断和停止中断；用带唯一敏感标记的变量/env/异常样例扫描 stdout 协议外输出、stderr 与文件日志。记录目标存活、可重新附加、Engine 退出与句柄基线，覆盖需求验收标准 7。
    - 验证记录：Debug 32 组合故障矩阵、多会话/启动中断/句柄与日志专项通过；见 docs/validation/stage2-4-lifecycle.md。

- [x] **阶段2-5 MCP 端到端回归与 MVP 验收**
    - [x] 新增 `eng/verify-stage2.ps1` 作为一键无人值守入口，默认跑 Debug/Release，支持单配置；驱动真实发布的 MCP stdio 进程与标准客户端，不以直接调用 Host 或 mock Engine 代替产品链路
    - [x] Console/WinForms × x86/x64 验证需求 §13 的 1～9：架构自动选择、Windows PDB、断点与停止信息、至少 10 层栈、变量、Continue/三种 Step、未处理异常、清理及明确错误；复用 `tests/Debuggees/` 并覆盖 CoreCLR/跨位数/PDB 不匹配拒绝路径
    - [x] 汇总阶段2-1～2-4的协议、schema、取消/限流、生命周期、技能安装与真实 Agent 证据；全量回归包括 `eng/verify-stage1.ps1`，不因 MCP 适配改变既有 CLI 行为
    - [x] 测试脚本各子进程有总超时与 finally 清理，只清理自己创建的测试进程；失败或缺少外部 Agent 验收证据时非零退出。机器结果写入 `artifacts/stage2-validation/`，结论写入 `docs/validation/stage2-mvp.md`，逐项关联需求验收编号、命令及证据
    - 验收：一键入口 Debug/Release 全通过，13 个工具均有正反例，真实外部 Agent 验收证据齐全，所有进程清理可核查；只有上述条件满足才标记阶段 2 / MVP 完成，不能把阶段 1 的共享后端验收直接算作 MCP 入口验收。
    - 验证记录：最终一键 Debug/Release、74 项单元、真实 MCP/CLI/原生回归及双轴审核通过，源码哈希稳定且监督清理零强制终止；见 docs/validation/stage2-mvp.md。阶段 2 / MVP 完成，条目依要求保留原位置。

本阶段协议行为以 [MCP 生命周期](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)、[stdio 传输](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)、[工具结果](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)、[取消](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/cancellation) 与 [官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 为依据；轮询、限额和保留时间为本项目的实施默认约定，不宣称为 MCP 规范要求。
