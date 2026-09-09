# 🗺️ Product Roadmap

> 项目：FxDbg Agent —— 面向 AI Agent 的 .NET Framework 4.0 调试器
> 依据：《FX 4.0 AI 调试器需求文档》§12（分阶段实施）与 §13（验收标准）
> 里程碑：**MVP = 阶段 0 + 阶段 1 + 阶段 2**，对应需求验收标准 1~9；阶段 3/4 在 MVP 验收后启动

## 📝 计划中 (Planned)

## 🚧 开发中 (In Progress)

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

### 阶段 3：可用性增强（已验收，按约定保留原位置）

> 无人值守实施约定（2026-09-06，用户已确认）：本轮顺序为阶段3-1 → 阶段3-2 → 阶段3-3 → 阶段3-5；阶段3-4暂时跳过，保持未勾选，不安装 IIS 或创建系统服务。本轮完成不代表阶段3-4完成。
> 开始前将本节确认内容与既有分区调整单独本地提交，然后启动目标；每项测试与快速审核通过、阻塞修复后才原地勾选并独立本地提交。当前 main 分支执行，不推送。最终以准备提交为固定基线，两个独立子代理分别审核规范与需求，修复并复核；运行 Debug/Release 全量回归（含阶段2及共享后端），将命令、结果、源码哈希与清理证据记录到 docs/validation/ 和 artifacts/stage3-validation/。
> 统一约束：保持现有13个MCP工具兼容，以可选参数和附加结果字段扩展；CLI、MCP、DAP复用 FxDbg.Host/Engine；保持双架构、回调仅入队、单调度线程、Continue配对、只读观察与安全分离纪律。未通过或缺失真实验收不得记为通过；测试进程有总期限且只清理自身创建的资源。
> 本轮最终验收（2026-09-06）：四项已按顺序完成并独立提交；规范审核2项、需求审核1项均已修复并独立复核，遗留0项。修复版本b9d779a的Debug/Release全量回归及235项输入一致性检查通过，覆盖阶段3专项、真实VS Code和全部阶段2/1/0；报告与证据见 docs/validation/stage3-usability.md、docs/validation/stage3-final-review.md。阶段3-4仍未完成，阶段3保留在开发中。

> 当前最终验收（2026-09-07）：3-4a～3-4f已按顺序开发、快速审核并本地提交，整体双轴审核与阻塞修复复核完成。a0b4a2c的Debug/Release完整回归于08:23:55Z通过，包含真实Service/IIS、3-1/2/3/5、真实VS Code及全部阶段2/1/0；269项输入一致、变更生产行覆盖率140/155（90.32%）。遗留阻塞0，保留1项非阻塞脚本维护建议。阶段3现已全部完成，按本轮约定原地保留条目；报告见 `docs/validation/stage3-4-final-review.md`、`docs/validation/stage3-4-service-iis.md`。

- [x] **阶段3-1 对象与数组分页展开**——大对象/数组分页读取，深度、数量、字符串长度限制调优
    - [x] 超过10万元素数组及对象图按需读取，覆盖首/中/尾页、循环引用、运行后引用失效与有界资源；不得先遍历整个对象图再分页
    - [x] 保留深度、数量、字符串长度边界，验证越界页及取消；同步MCP契约、CLI与技能文档
    - 验收：真实Windows x86/x64样例与MCP链路通过；本机预热后默认页读取P95≤1秒、单次≤5秒，另记录冷启动耗时（本项目量化标准，需求§10.3未指定数值）。
    - 验证记录：Debug/Release共76项单元及真实MCP双架构分页通过；快速审核已修复测试页位置表达式和随包文档同步，见 docs/validation/stage3-1-paging.md。

- [x] **阶段3-2 源码路径映射**——构建机路径与本地工作区路径不一致时的映射（全局 + 模块级）
    - [x] 支持会话内全局与模块级映射；模块级优先，同级最长路径前缀匹配，按路径段边界匹配，歧义明确报错
    - [x] 同时映射断点输入与栈帧源码输出，保留原始符号路径以便诊断；覆盖含空格、大小写与未映射路径
    - 验收：真实双架构样例的构建路径与本地路径不同，映射后断点绑定、命中与栈帧源码行正确；模块延迟加载后仍可绑定，既有无映射调用兼容。
    - 验证记录：Debug/Release各80项单元、8组真实MCP映射及前序分页回归通过；快速审核阻塞已修复，见 docs/validation/stage3-2-source-mapping.md。

- [x] **阶段3-3 多 AppDomain 识别与筛选**——事件、栈、变量携带 AppDomain 信息，支持按需过滤
    - [x] 提供稳定域标识与名称，覆盖事件、线程、栈、变量及断点；默认全部域，可按域筛选
    - [x] 验证同一程序集多域加载、域卸载与重建后的断点绑定及引用清理，不用域名称作为唯一身份
    - 验收：真实双架构多域样例可定位指定域线程与断点，跨域栈与变量归属正确，卸载后不返回陈旧引用；未指定筛选时保持原行为。
    - 验证记录：Debug/Release各82项单元、多域及前序MCP回归、4组真实生命周期事件通过；快速审核已修复名称更新和退出线程生命周期，见 docs/validation/stage3-3-appdomains.md。

- [x] **阶段3-4 Windows Service 与 w3wp 附加增强**——SeDebugPrivilege 权限路径、IIS 影子复制与动态模块加载
    - 规划补充（2026-09-07）：本次细化待实施任务，保留阶段3-4编号与原位置；上方2026-09-06的跳过及回归结论为历史记录，不覆盖本项。以下3-4a～3-4f全部验收通过后才勾选父项。
    - 本轮实施约定（2026-09-07，用户授权）：先单独本地提交文档，再顺序完成3-4a～3-4f；每项完成后只做快速审核，修复阻塞项、记录证据后原地勾选并独立本地提交。全部完成后对3-4整体执行完成审核、修复及复核，运行Debug/Release完整回归；本次目标期间允许本地提交，继续使用main，不推送。
    - 执行顺序与工作量：3-4a（1～2天）→ 3-4b（2～3天）→ 3-4c（1～2天）→ 3-4d（2～3天）→ 3-4e（2～3天）→ 3-4f（2～3天），合计约10～16个工作日，不含测试环境安装与等待；复用已完成的3-2源码映射、3-3多域及3-5 DAP能力。
    - 范围：本机、已运行且已加载CLR v4的独立Service进程与完整IIS的ASP.NET/WebForms `w3wp.exe`，沿用按PID附加和13个MCP工具；按服务名/应用池查找PID作为测试工具及文档流程，不增加服务管理工具。进程重启或回收后由调用方显式创建新会话；服务启动前调试、自动跨PID重附加、多进程联调与远程调试不在本项范围。
    - 环境安排（2026-09-07，用户指定）：优先尝试在本机部署测试环境，FxDbg与目标同机运行；先只读预检Windows版本/组件、管理员及受限令牌、完整IIS/ASP.NET 4.x和双架构条件，再部署专属测试资源。本次只完善规划；此前“不安装IIS或创建系统服务”属于2026-09-06已结束轮次，后续本机部署按本条执行。若本机条件不足，记录具体阻塞与所需变更，不自动改为VM或将缺失项算作通过。
    - 统一约束：CLI/MCP/DAP复用共享Host/Engine，权限与符号能力不放入协议适配层；保持Host不加载COM、实际进程位数路由、回调仅入队、单调度线程、Continue配对、只读观察及附加目标禁止terminate。IIS测试站点仅绑定回环地址，调试协议仍只用stdio/Named Pipe。
    - 总验收：真实Service与完整IIS × x86/x64 × Debug/Release均完成附加、源码断点、栈/变量、继续与安全分离，权限失败、影子复制、动态加载及回收场景证据齐全；3-4专项和包含3-1/2/3/5及阶段2/1/0的完整回归通过，缺环境或跳过不得记为阶段3全部通过。

- [x] **阶段3-4a 真实Service/IIS样例与可恢复测试环境**——交付可重复部署、触发和清理的验收夹具（1～2天）
    - 环境准备记录（2026-09-07）：本机IIS 10/ASP.NET 4.x、LocalService身份的x86/x64服务及两个回环站点已安装并通过心跳/HTTP/CLR位数/影子复制检查；提供安装、检查和自有资源卸载脚本，首次部分部署清理后重装成功。此为环境准备，不覆盖延迟加载、故障恢复及产品附加验收，当时3-4a尚未勾选；见 `docs/validation/stage3-4-environment.md`、`docs/service-iis-environment.md`。
    - [x] 在 `tests/Debuggees/` 提供net40双架构Windows Service样例与ASP.NET/WebForms样例；均有可重复触发的业务方法、已知局部变量和延迟加载程序集，构建产物包含匹配的Windows PDB
    - [x] Service由SCM启动，覆盖Session 0及不同于调试器用户的服务身份；IIS使用专属站点和应用池，分别覆盖32位/64位worker、CLR v4、影子复制和请求预热。记录服务名/应用池、PID、创建时间、实际架构与运行时，确保测试选中自己的目标
    - [x] 提供只读环境预检及独立的部署/清理入口；优先在本机尝试部署，预检列出缺失Windows组件、目录ACL、测试身份、端口占用和配置变更。启用所需IIS/ASP.NET组件前记录原状态，已有组件保留；遇到系统重启要求报告待重启状态，不自动重启机器，不覆盖同名已有资源
    - [x] 保存自有资源清单与配置基线；测试有总期限及finally清理，支持中断后按清单恢复。停止/删除仅限测试创建的服务、站点、应用池及目录，不执行全局iisreset或批量终止w3wp；只在分离/存活证据采集后由测试夹具清理目标
    - 验收：环境预检能明确报告缺失条件；两次部署→触发→清理均成功，Service经SCM确认运行、IIS请求有预期响应，DLL/PDB匹配及MSF 7.00文件头可核查；异常中断后可恢复，已有服务、站点和应用池配置保持基线。

    - 完成记录：Debug/Release两轮真实部署/清理、延迟加载、安装中断恢复、IIS配置基线及各4项DLL/PDB身份测试通过；快速审核阻塞项已修复并复跑，见 `docs/validation/stage3-4a-fixtures.md`。

- [x] **阶段3-4b 跨身份附加的权限路径与错误诊断**——管理员可用、受限调用可解释失败（2～3天，依赖3-4a）
    - [x] 在Host架构探测和Engine实际附加两处覆盖所需进程访问权限；普通同用户附加继续可用。需要时只启用当前令牌已持有的SeDebugPrivilege，验证启用结果及权限作用范围/恢复，不把管理员组成员身份或API返回成功直接当作权限已获得
    - [x] 不自动弹UAC、索取凭据、修改安全策略或创建提权服务；权限不足时沿用 `access_denied`，说明失败环节和以具备权限的Host重新附加等恢复步骤。保留目标不存在、运行时不支持、位数不符及已有调试器的既有错误语义
    - [x] 覆盖权限不存在/未启用/已启用、受限令牌访问跨身份目标、目标检查后退出与并发会话；按需启用/恢复不能互相干扰，不能因附加失败遗留Engine、句柄、半开会话或改变目标状态
    - 验收：管理员上下文通过真实跨身份Service和w3wp访问/附加，受限上下文按预期返回结构化错误且目标存活；`ERROR_NOT_ALL_ASSIGNED`等边界有自动化覆盖，同用户附加及双架构路由回归通过，MCP stdout无权限提示或日志污染。
    - 完成记录：Debug/Release各98项单元测试、各8项管理员及4项受限令牌真实Service/IIS案例、同用户/并发附加和自有资源清理通过；快速审核阻塞项已修复，见 `docs/validation/stage3-4b-permissions.md`。

- [x] **阶段3-4c Windows Service附加与安全分离**——通过现有产品入口调试运行中的服务（1～2天，依赖3-4b）
    - [x] 按已核实的Service PID附加，设置业务方法断点后由测试夹具触发工作；返回正确源码行、线程、托管栈、参数/局部变量，支持暂停、继续与三种单步，不要求已执行完的OnStart再次命中
    - [x] 显式detach、MCP EOF与Host异常退出后，服务继续处理下一次工作，Engine按既有15秒清理期限退出且可重新附加；附加目标terminate被拒绝，SCM状态和业务响应均需检查，不能只以PID存在判定服务正常
    - [x] 覆盖测试服务被SCM正常停止/重启；旧会话正确结束、旧帧/变量引用失效，新PID必须显式附加。服务恢复策略和故障注入仅作用于专属测试服务，Engine硬崩溃继续沿用既有“不承诺目标存活”的限制
    - 验收：真实MCP完成Service × x86/x64 × Debug/Release完整流程及生命周期反例，另以CLI和DAP完成双架构附加/断点/分离冒烟；退出与重附加证据能关联到同一测试服务的实际进程实例。
    - 完成记录：Debug/Release各101项单元、14个MCP/CLI服务场景与2个DAP场景通过；EOF/Host退出、SCM重启、终态竞态修复及自有资源清理已验证，见 `docs/validation/stage3-4c-services.md`。

- [x] **阶段3-4d IIS附加与影子复制符号定位**——在实际worker中命中ASP.NET源码（2～3天，依赖3-4c）
    - [x] 文档与夹具支持应用池→worker PID核对、请求预热和实际架构确认；多worker或回收重叠时列明候选，不按进程名任取一个。未加载托管运行时按既有错误语义报告并提示预热后重新选择PID
    - [x] 保持测试应用影子复制开启，观察实际加载路径与部署路径差异；用匹配Windows PDB绑定预编译业务代码及带可用Windows PDB的ASP.NET动态编译页面，结合3-2映射返回正确本地源码行
    - [x] 符号查找有明确、可诊断且有界的候选范围；如需部署目录/PDB搜索配置，沿用可选参数兼容扩展，不把源码路径映射当作PDB搜索配置。验证模块与PDB身份，不按同名DLL/PDB强行绑定，不无界扫描磁盘
    - [x] 覆盖PDB缺失、不匹配、无读取权限及稍后就绪；报告真实符号状态，PDB就绪后自动重绑定。临时ASP.NET目录变化或同名不同版本模块不得复用错误符号缓存
    - 验收：完整IIS × x86/x64 × Debug/Release通过真实MCP附加→HTTP触发→断点→栈/变量→继续→detach，分离后请求恢复；记录实际影子路径、PDB匹配与断点位置。IIS Express、普通Console影子复制样例或关闭影子复制均不能替代本项证据。
    - 完成记录：Debug/Release各102项单元测试、完整IIS双架构业务/动态页面断点及PDB状态恢复通过；修复ACL恢复无元数据变化时不重试的阻塞项，见 `docs/validation/stage3-4d-iis-symbols.md`。

- [x] **阶段3-4e 动态模块、多域及IIS回收生命周期**——部署变化后仍能正确绑定或明确结束会话（2～3天，依赖3-4d）
    - [x] 对尚未加载的磁盘程序集和ASP.NET按需编译代码预设断点，验证pending→verified/moved及实际命中；模块加载、符号更新和卸载继续走现有共享后端，不另建IIS调试路径
    - [x] 覆盖同一worker内同一程序集多AppDomain加载、指定域断点与域卸载/重建；模块/域身份、栈/变量归属正确，旧域及模块的绑定、帧/引用和符号缓存及时清理，未限定域的断点可在新域重绑定
    - [x] 区分“有磁盘产物及Windows PDB的动态编译/延迟加载”和纯内存、Reflection.Emit无可用符号的模块；后者能枚举并明确报告符号/源码不可用，不导致会话崩溃，也不承诺本阶段支持任意动态IL源码调试
    - [x] 覆盖专属应用池正常回收、旧/新worker短暂并存与显式重新附加；旧session不得转移到新PID或误命中其他worker。断点暂停涉及的ping、空闲超时和回收设置仅在测试池内调整并记录恢复，不由调试器改动业务IIS配置
    - 验收：真实IIS双架构、双配置完成延迟加载、页面首次编译、域重建、应用池回收和新PID重新附加；事件/断点状态顺序、旧引用拒绝及请求恢复均可自动断言，无重复Continue、死锁或陈旧模块；仅模拟回调或Console多域回归不足以标记通过。
    - 完成记录：Debug/Release各102项单元及真实IIS双架构延迟加载、首次页面编译、多域重建、内存模块和重叠回收通过；旧引用拒绝、新PID显式附加及池设置恢复均已断言，见 `docs/validation/stage3-4e-iis-lifecycle.md`。

- [x] **阶段3-4f 产品入口回归、文档与阶段3最终验收**——形成可复核的一键证据（2～3天，依赖3-4a～3-4e）
    - [x] 新增 `eng/verify-stage3-4.ps1`，默认Debug/Release并支持单配置，驱动真实发布MCP、SCM服务和完整IIS；环境缺失、权限不足、用例失败或清理不完整均非零退出，输出明确原因，不以跳过返回通过
    - [x] 扩展 `eng/verify-stage3.ps1` 纳入3-4；保留显式跳过选项供无IIS环境运行既有子集，结果必须列出skipped且不得宣称阶段3全验收通过。全验收覆盖3-1/2/3/5、真实VS Code及全部阶段2/1/0回归
    - [x] MCP完成全部Service/IIS行为矩阵；CLI和DAP各覆盖两类目标的双架构附加/断点/分离，真实VS Code补充Service与IIS附加会话。保持13工具契约及旧调用兼容，必要的新参数/schema/发布内容同步测试
    - [x] 编写 `docs/service-iis.md`，同步CLI/MCP/DAP、Agent技能及随包引用，说明管理员启动、PID定位、预热、符号诊断、回收后重附加、暂停对服务/请求的影响与安全分离；按实现事实更新AGENTS.md，保留先前跳过的历史验收记录
    - [x] 原始证据写入 `artifacts/stage3-4-validation/`，报告写入 `docs/validation/stage3-4-service-iis.md`，补充阶段3最终报告；记录命令、OS/IIS/CLR版本、身份/权限、配置、源码哈希、结果和自有资源恢复证据，日志保持脱敏
    - 验收：专项与完整回归Debug/Release全部通过；以实施前固定基线完成规范/需求双轴独立审核并修复、复核阻塞项；报告逐条关联3-4a～3-4f。所有真实环境证据与清理证据齐全后才原地勾选，历史3-1/2/3/5通过记录不得替代3-4证据。
    - 实现记录：一键脚本、显式子集标识、CLI/DAP/真实VS Code的双配置Service/IIS入口矩阵及随包文档已完成快速审核并本地提交；完整双配置回归及整体审核已通过，见 `docs/validation/stage3-4-service-iis.md`。

阶段3-4的技术边界参考Microsoft官方文档：[调试Windows Service](https://learn.microsoft.com/en-us/dotnet/framework/windows-services/how-to-debug-windows-service-applications)、[令牌权限调整及ERROR_NOT_ALL_ASSIGNED](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges)、[程序集影子复制](https://learn.microsoft.com/en-us/dotnet/framework/app-domains/shadow-copy-assemblies)、[IIS应用池进程模型与ping设置](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/processmodel)。上述范围、工期和验收矩阵为本项目规划约定。

- [x] **阶段3-5 可选 DAP Host**——复用同一 Engine，支持 VS Code / Cursor 调试界面
    - [x] 本轮作为必做项，交付DAP over stdio Host、最小VS Code/Cursor扩展及配置/发布文档，复用共享Host/Engine
    - [x] 支持启动、附加、源码断点、继续/暂停、三种单步、线程/栈/作用域/变量分页与安全分离；仅声明实际支持能力，不支持求值或修改状态
    - [x] 覆盖协议初始化、配置完成、停止/退出事件、取消与断开清理，恢复运行后使帧和变量引用失效
    - 验收：真实DAP协议回归与真实VS Code调试会话完成启动、附加、断点、三种单步、栈、变量分页及分离，记录可复核客户端证据；双架构Debug/Release通过。
    - 验证记录：Debug/Release各85项单元、8组真实DAP及8组真实VS Code会话通过；快速审核修复激活、会话结束及只读取消竞态，见 docs/validation/stage3-5-dap.md。

### 阶段 4：高级能力（已完成，按要求原位保留）

> 规划补充（2026-09-08，用户指定）：本阶段条目细化，保留原编号与原位置。4-1 以安全评估为前置门禁——评估未通过并记录 ADR 前不实现；4-2 只做观察过滤、不执行用户代码；4-3 命中次数条件不依赖求值，表达式条件依赖 4-1；4-4 已决定不实施（见条目内决策记录）。统一约束：CLI/MCP/DAP 复用共享 Host/Engine，求值/过滤/条件逻辑不放入协议适配层；保持回调仅入队、单调度线程、Continue 配对、只读观察（禁止变量修改、Set Next Statement、Edit and Continue）与安全分离纪律；MCP 以可选参数或新增工具兼容扩展并同步 `docs/mcp-tools.md`、CLI、DAP 与 Agent 技能文档。完成纪律：每项记录验收命令、配置、结果与证据路径后才勾选，失败先修复再继续。数据断点、函数断点、日志断点、变量修改不在本阶段范围（需求 §11）。

- [x] **阶段4-1 受限表达式求值**——安全评估通过后实现只读、限时、可取消的表达式求值
    - [x] 前置门禁：安全评估及允许/禁止清单、只读保证、资源上限、失败与超时边界已记录于 `docs/architecture.md` 的 ADR-004，并于2026-09-08获用户批准。采用调试器内只读解释求值，显式取代目标CLR Eval方案；实现与真实验收见docs/validation/stage4-1.md
    - [x] 求值范围：仅限 stopped 状态下基于指定线程/帧上下文求值；支持字面量、字段/数组/索引访问、算术/比较/逻辑运算与受控字符串操作；String/Array/Math仅支持ADR-004白名单内解释器固有操作，不调用目标mscorlib或任意用户方法，禁止属性Getter、Setter/变量修改、反射与动态调用
    - [x] 只读保证：求值不得改变业务状态——求值前后目标字段/静态/对象状态一致，不调用任何可写路径；结果显示沿用既有深度、成员数、字符串长度与分页上限，不调用目标自定义格式化代码
    - [x] 执行纪律：通过既有单调度线程完成有界只读解释，不创建ICorDebugEval、不等待EvalComplete、不新增Continue，回调仍只入队；解析、计算与读取边界检查期限/取消，正常CLR读取条件下超时/取消后会话状态一致且可继续；禁止在回调线程求值或无界阻塞调度线程；原生COM不可中断故障按ADR-004明确报告，不承诺故障后仍可继续；恢复运行后旧帧/引用失效语义不变
    - [x] 入口复用：CLI、MCP、DAP 复用同一 Host/Engine 求值路径；MCP 以新增工具或可选参数扩展并保持既有 13 工具调用兼容，同步工具契约、CLI 命令、DAP evaluate 能力声明与 Agent 技能文档
    - 验收：真实 x86/x64 × Debug/Release 样例在停止态完成受限表达式求值，结果与变量模型一致；用带副作用计数与状态字段的样例及产品路径审核证明不执行目标代码、不修改业务状态、禁止路径调用计数为零；拒绝循环和任意调用语法，使用长/深表达式、预算耗尽、已取消请求及可控慢读取验证期限/取消，真实正常CLR读取条件下会话可继续、停止代次不变、无新增或重复Continue、无死锁；原生不可中断故障单列限制；语法错误、上下文不可用、引用失效、超时、取消错误分类明确且不泄漏堆栈；既有13工具与CLI/DAP全量回归通过。
    - 验证记录：双架构Debug/Release原生、MCP、CLI、DAP专项通过，各187项单元测试；快速审核修复期限传递及前序回归暴露的初始化/测试发布竞态，阶段2/1/0完整复验通过。证据与批次差异见docs/validation/stage4-1.md；最终覆盖率与全量整体审核在全部任务后执行。

- [x] **阶段4-2 first-chance exception 过滤**——按异常类型配置 first-chance 停止条件
    - [x] 在既有会话级 first-chance 开关（CLI `exceptions --first-chance`、Engine `exceptions.configure`，默认关闭）之上增加按类型过滤：配置一个或多个匹配规则（精确类型名、命名空间前缀、基类/派生匹配，语义文档化），仅匹配类型在 first-chance 停止，未匹配类型不停止
    - [x] 过滤规则可动态更新（停止态或按既有运行中安全规则），清空/关闭后恢复默认仅未处理异常；异常报告字段（类型、消息、线程、抛出位置、调用栈）沿用阶段 1-8，消息仍只读 `_message` 字段，不调用自定义格式化代码
    - [x] 高频约束：first-chance 回调只入队，类型匹配判断不得在回调线程执行耗时工作或触发求值；未匹配异常不产生停止、不完成运行操作、不污染状态观察；过滤开启不得引入无界队列积压或拖慢停止响应
    - [x] 入口复用：CLI 扩展 `exceptions` 命令、Engine 协议扩展；MCP 以可选参数或新工具暴露会话级过滤配置（保持既有调用兼容），DAP 按声明能力扩展 `setExceptionBreakpoints`；同步文档与 Agent 技能
    - 验收：真实 x86/x64 × Debug/Release 样例抛出多种异常（含继承、命名空间分组、被捕获异常），仅配置类型在 first-chance 停止且停止信息正确；未匹配类型不停止、目标继续运行；动态开关/清空后恢复默认行为；高频抛出场景停止响应有界、无积压；关闭状态下与既有回归完全一致；CLI/MCP/DAP 入口结果一致。
    - 验证记录：2026-09-08双架构Debug/Release四入口专项及206项单测均通过；快速审核修复命令饥饿回调泵阻塞项，相关执行/求值/DAP兼容回归通过。证据见docs/validation/stage4-2.md；完整审核在全部任务完成后执行。

- [x] **阶段4-3 条件断点**——命中次数条件独立实现，表达式条件依赖 4-1
    - [x] 断点增加可选条件：命中次数（阈值语义文档化）与/或受限表达式（AND 组合）；无条件的既有断点行为完全不变，断点状态机仍仅 pending/verified/moved/unresolved，沿用模块加载或 PDB 就绪后自动重绑定
    - [x] 命中次数：仅在实际命中时计数，达到阈值才作为用户停止返回；断点删除后计数重置，模块重载/重绑定保持同一断点计数；计数与条件状态在断点查询中可见
    - [x] 表达式条件：仅允许 4-1 定义的受限表达式（不执行用户代码、不改状态、限时），结果必须为布尔可判定；条件不满足时内部自动继续——不得向用户暴露该次内部停止、不得完成 waitForStop 运行操作、不得产生停止事件；条件求值失败/超时按明确策略处理并留诊断，不得静默或造成命中-求值-再命中死循环
    - [x] 入口复用：`debug_set_breakpoint` 以可选参数扩展条件字段，CLI `break` 与 DAP conditionalBreakpoints 能力同步扩展并声明；同步 `docs/mcp-tools.md`、`docs/cli.md`、`docs/dap.md` 与 Agent 技能
    - 验收：真实 x86/x64 × Debug/Release 样例：命中次数阈值前后停止行为正确（=N/≥N）；表达式条件命中/不命中符合预期，不命中时无停止泄漏、waitForStop 继续等待真实停止；条件与命中次数组合正确；条件错误/超时策略明确且会话可继续；无条件断点行为与前序回归完全一致；CLI/MCP/DAP 三入口结果一致。
    - 验证记录：2026-09-08双架构Debug/Release四入口条件矩阵、真实模块重载及各231项单测通过；快速审核无遗留阻塞，求值和DAP兼容回归通过。证据见docs/validation/stage4-3.md；随后进行阶段4完整审核与全回归。

- [x] **阶段4-4 Streamable HTTP MCP 与远程宿主**——已决定不实施（2026-09-08）
    - 决策记录：不实施远程调试与网络监听。依据需求 §10.2（首期仅本机调试、MCP 使用 stdio、不监听网络端口）与 §11（首期不做远程 HTTP MCP、跨机器 ICorDebug）；远程会话的认证与权限控制需要独立安全边界与长期维护成本，超出本项目当前定位。本勾选仅表示决策关闭，不代表功能已完成；如需重新评估，先写 ADR 并与用户确认。

> 阶段4整体完成记录（2026-09-08）：4-1/4-2/4-3按顺序实施、快速审核仅修阻塞、原地勾选并分别本地提交；4-4按批准决策不实施。最终实现2aa8a9d通过双配置双架构四入口专项（42个监督步骤、每配置233项单元）、独立Standards/Spec完整审核及阻塞修复复核。变更生产行覆盖率697/738（94.44%）；真实Service/IIS、VS Code及阶段2/1/0的Debug/Release完整回归通过，无跳过，298项输入一致，资源恢复与管理员后台进程退出已核对。遗留阻塞0，保留2项非阻塞维护建议。证据见docs/validation/stage4-final-review.md；全部操作仅本地提交，不推送。

### 其它

- [x] **GitHub Actions 自动发布**——主分支推送版本 tag（如 `V0.1.0`，忽略大小写）时自动触发，仅运行必要单元测试，打包发布产物 zip 并发布 GitHub Release
    - [x] 触发：推送形如 `V0.1.0` / `v0.1.0`（忽略大小写，`V`/`v` 前缀 + 语义化版本号）的版本 tag 且该 tag 指向的提交属于 `main` 分支历史时运行发布流水线；非版本 tag、普通分支/主分支推送、tag 指向非 main 提交均不触发发布
    - [x] 构建与测试：在 Windows runner（项目 Windows-only）上按 `global.json` 固定 SDK（10.0.301）构建解决方案；仅运行 `tests/FxDbg.UnitTests` 的必要单元测试，任一失败即终止且不发布；不运行任何真实环境、Service/IIS、DAP/VS Code、覆盖率采集及阶段验收（`verify-*`、`setup-stage3-4-*`）脚本，这些由发布者发布前人工把控
    - [x] 打包：复用现有发布入口（`eng/publish-mcp.ps1`、`eng/publish-dap.ps1` / `publish-host.ps1`，Release 配置）的产物布局，将二者合并发布目录内容整体打包为单个 `fxdbg-<版本>.zip`：`fxdbg-mcp`、`fxdbg-dap` 主机可执行文件与依赖、共用 `engines/` 下双架构（x86/x64）`FxDbg.Engine.*.exe` 及 native 依赖（ClrDebug、DiaSymReader 系等）、`engine-manifest.json`、`skills/fxdbg-agent/`（SKILL.md 与 references，供终端用户安装技能）与匹配的 PDB（自有代码 PDB 均包含，engines 为 Windows PDB），附全包文件 SHA256 清单
    - [x] 发布：以 tag 名创建（或更新，幂等重试）GitHub Release 并上传 zip 资产，workflow 权限最小化（仅 `contents: write`）；Release Notes 取 `changelog.md` 中对应版本条目（`## V0.1.0` 标题块，忽略大小写）；`changelog.md` 没有对应条目时以明确可操作错误终止，不发布
    - 验收：推送 `V0.1.0` 与 `v0.1.0` 均触发并成功发布，Release 名称/说明与 changelog 对应条目一致，zip 可下载且内部清单 SHA256 可核对；非版本 tag、指向非 main 提交的 tag 及普通 push 不触发发布；单元测试失败时无 Release/资产产生；流水线日志中不存在任何 `verify-*`/`setup-stage3-4-*`/IIS/真实环境调用；zip 内容与 MCP/DAP 两个入口合并到同一目录的本地发布结果一致（含双架构 engines 与匹配 PDB）；`changelog.md` 缺失对应版本条目时 workflow 非零退出并给出可操作原因。
    - 实现与本地验证（2026-09-09）：Release 构建、MCP/DAP 发布、233 项普通单元测试、52 项发布脚本回归断言、actionlint 1.7.12 及 zip 61 文件逐项 SHA256 比对通过。GitHub 事件过滤无法直接判断祖先关系，候选 tag 经只读检查后决定是否跳过整个发布作业。流程见 `docs/release.md`。
    - 待线上验收：尚未推送版本 tag、运行真实 GitHub Actions 或创建/下载 Release；本地模拟不作为线上验收通过证据，父任务保留未勾选。
