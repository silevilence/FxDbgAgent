# FX 4.0 AI 调试器需求文档

## 1. 项目概述

### 1.1 项目名称

**FxDbg Agent——面向 AI Agent 的 .NET Framework 4.0 调试器**

### 1.2 项目目标

开发一套运行于 Windows 的托管代码调试器，使 Cursor、Claude Code、Copilot 等外部 AI Agent 能够调试 .NET Framework 4.0 应用，包括：

- 启动或附加目标进程
- 按源文件和行号设置断点
- 继续运行和单步执行
- 获取停止原因、调用栈、参数和局部变量
- 捕获托管异常
- 自动支持 32 位和 64 位目标进程

第一阶段重点是为 AI 提供稳定、结构化、低风险的调试能力，不追求完整复制 Visual Studio 调试体验。

## 2. 背景与问题

现有 AI 调试工具主要面向 CoreCLR 和现代 .NET。`.NET Framework 4.0` 使用 Desktop CLR，其调试实现、运行时发现机制和符号格式均与现代 .NET 存在差异：

- 运行时模块为 `clr.dll`，而非 `coreclr.dll`
- 调试接口由 Framework 自带的 `mscordbi.dll` 实现
- 通过 `ICLRMetaHost` 和 `ICorDebug` 建立调试会话
- 默认使用 Windows PDB，而非 Portable PDB
- 大量旧应用为 32 位进程
- ASP.NET 等应用可能包含多个 AppDomain

SharpDbg、netcoredbg、vsdbg 等现有工具不能直接满足 FX 4.0 的调试需求，因此需要开发专用后端。

## 3. 总体原则

1. 使用公开的 `ICorDebug` 接口，不依赖 Visual Studio 内部调试引擎。
2. 调试引擎使用 C# 开发，并引用 ClrDebug 作为 COM 接口包装层。
3. 不修改或复制 ClrDebug 的生成代码；将其作为第三方依赖使用。
4. 可参考 SharpDbg 的会话模型、变量树和 DAP 实现，但不直接依赖其 CoreCLR 调试后端。
5. Agent 默认通过 MCP stdio 接入。
6. 调试引擎按 x86、x64 分为两个独立进程，由统一入口自动选择。
7. 第一阶段以只读观察为主，暂不开放高风险函数求值与状态修改。

## 4. 目标用户与场景

### 4.1 目标用户

- 维护 .NET Framework 4.0 遗留系统的开发团队
- 使用外部 AI Agent 编写和修改代码的开发者
- 需要自动定位测试失败、运行时异常和业务逻辑错误的 Agent

### 4.2 首期支持场景

- Console 应用
- WinForms / WPF 应用
- Windows Service
- 普通可执行程序的启动调试和进程附加

### 4.3 后续支持场景

- ASP.NET / WebForms 的 `w3wp.exe` 附加
- 多 AppDomain 识别与筛选
- IIS 影子复制和动态模块加载
- CI 环境中的远程调试代理

## 5. 系统架构

```text
外部 AI Agent
      │
      │ MCP over stdio
      ▼
FxDbg.McpHost（统一入口、会话管理、位数判断）
      │
      │ Named Pipe / 本机 JSON-RPC
      ▼
┌──────────────────┬──────────────────┐
│ FxDbg.Engine.x86 │ FxDbg.Engine.x64 │
│ ICorDebug 32 位   │ ICorDebug 64 位   │
└─────────┬────────┴─────────┬────────┘
          │                  │
          ▼                  ▼
  FX 4.0 x86 进程     FX 4.0 x64 进程
```

### 5.1 组件职责

#### FxDbg.Core

与协议无关的领域模型和接口：

- 调试会话状态机
- 断点、线程、栈帧、变量和异常模型
- 引擎请求与事件定义
- 统一错误码

#### FxDbg.Engine

真正承载 `ICorDebug` 的同位数调试进程：

- 初始化 Desktop CLR 4.0 调试接口
- 启动或附加进程
- 处理 ICorDebug 异步回调
- 管理 AppDomain、Assembly、Module 和 Thread
- 加载 Windows PDB
- 绑定源码断点
- 提供单步、调用栈和变量读取

分别发布为：

- `FxDbg.Engine.x86.exe`
- `FxDbg.Engine.x64.exe`

#### FxDbg.McpHost

面向 Agent 的统一入口：

- MCP stdio 服务
- 启动参数校验
- 自动检测目标进程位数
- 拉起对应架构的 Engine
- 将 Engine 事件转换成结构化 MCP 结果
- 管理会话、超时、取消和资源清理

#### FxDbg.DapHost（可选）

复用同一调试引擎，实现 DAP，使开发者可从 VS Code 或 Cursor 的调试界面操作。

#### FxDbg.Cli（建议提供）

用于开发测试、人工排障和脚本集成，不承载独立调试逻辑。

## 6. 技术选型

| 项目 | 选型 |
|---|---|
| 开发语言 | C# |
| 调试接口 | ICorDebug / ICLRMetaHost |
| COM 包装 | ClrDebug |
| 目标平台 | Windows |
| 调试目标 | .NET Framework 4.0 / CLR v4.0.30319 |
| Agent 协议 | MCP over stdio |
| 内部进程通信 | Named Pipe，消息使用 JSON-RPC 或自定义 JSON 帧 |
| 符号读取 | Windows PDB：ISymUnmanagedReader / DIA / diasymreader |
| 可选 IDE 协议 | DAP over stdio |
| 引擎发布架构 | win-x86、win-x64 |

调试器宿主可使用受所选库支持的现代 .NET 版本，不要求自身运行于 .NET Framework 4.0。

## 7. 功能需求

### 7.1 会话管理

- 每个调试会话具有唯一 `sessionId`
- 支持创建、查询、结束会话
- 单个会话同时只控制一个目标进程
- 一个目标进程同时只允许一个 FxDbg 会话附加
- 会话状态至少包括：
  - `created`
  - `starting`
  - `running`
  - `stopped`
  - `detaching`
  - `terminated`
  - `failed`
- 所有状态变更均产生结构化事件

### 7.2 启动调试

输入：

- 可执行文件路径
- 命令行参数
- 工作目录
- 环境变量
- 架构：`auto | x86 | x64`
- 是否在入口处停止

要求：

- 使用 Desktop CLR 4.0 调试路径启动目标
- `auto` 模式根据 PE 和 CLR Header / CorFlags 选择架构
- 支持明确指定 x86 或 x64
- 返回实际架构、PID、CLR 版本和会话状态

### 7.3 附加进程

输入：

- PID
- 架构：`auto | x86 | x64`

要求：

- `auto` 模式通过 `IsWow64Process2`，必要时回退至 `IsWow64Process`
- 验证目标已加载或将加载 Desktop CLR
- 拒绝 CoreCLR、原生进程和不支持的 CLR 版本，并返回明确原因
- 附加失败不得影响目标进程继续运行

### 7.4 源码断点

- 通过 `源文件路径 + 行号` 设置断点
- 支持在模块尚未加载时创建待绑定断点
- 模块加载或 PDB 就绪后自动重新绑定
- 返回以下状态之一：
  - `pending`
  - `verified`
  - `moved`
  - `unresolved`
- 若指定行无可执行 IL，可移动到最近可执行行并告知 Agent
- 支持启用、禁用和删除断点
- 首期不要求条件断点、命中次数和日志断点

### 7.5 执行控制

支持：

- Continue
- Pause
- Step Into
- Step Over
- Step Out
- Detach
- Terminate（仅限由调试器启动的目标）

要求：

- 执行命令仅能在合法会话状态下调用
- 单步操作必须指定线程
- 返回命令受理结果；实际停止通过事件或等待接口返回
- 每个运行操作支持超时与取消

### 7.6 停止状态

每次停止返回：

- `sessionId`
- 停止原因：断点、单步、异常、用户暂停、入口、进程退出等
- PID、线程 ID、AppDomain
- 当前源文件、行号、模块和方法
- 简要调用栈
- 相关断点或异常信息

### 7.7 调用栈

- 枚举托管线程
- 获取指定线程的托管调用栈
- 栈帧包含：
  - 帧 ID
  - 方法全名
  - 模块和程序集
  - IL Offset
  - 源文件及行号（若可解析）
  - AppDomain
- 首期允许忽略纯原生帧和混合模式帧

### 7.8 参数与局部变量

- 获取指定栈帧的方法参数和局部变量
- 支持基础类型、字符串、数组、对象字段和静态字段
- 对对象采用按需展开，避免一次遍历完整对象图
- 每次展开设置最大深度、最大成员数和最大字符串长度
- 对循环引用返回引用标识，不递归展开
- 默认不调用属性 Getter、`ToString()` 或用户代码
- 明确区分无法获取、已优化掉和空值

### 7.9 异常处理

- 捕获未处理托管异常
- 可配置是否在 first-chance exception 停止，默认关闭
- 返回异常类型、消息、线程、抛出位置和调用栈
- 不调用目标对象的自定义格式化代码来生成异常内容

### 7.10 模块与符号

- 跟踪 Module Load / Unload 事件
- 识别 DLL、PDB 是否匹配
- 支持 Windows PDB
- 输出符号状态：已加载、不存在、不匹配、读取失败
- 支持源码路径映射，用于构建机路径与本地工作区路径不一致的情况

### 7.11 32/64 位自动选择

- MCP Host 不直接加载 `mscordbi.dll`
- ICorDebug 仅存在于 x86/x64 Engine 中
- 附加时按目标进程实际位数选择引擎
- 启动时按 PE 和 CorFlags 选择引擎
- 允许通过 `arch` 参数强制指定
- 如果实际位数与选择不一致，结束该引擎并返回可操作错误；不得静默跨位数附加

## 8. MCP 工具设计

首期建议提供以下工具：

| 工具 | 用途 |
|---|---|
| `debug_launch` | 启动并调试程序 |
| `debug_attach` | 附加已有进程 |
| `debug_set_breakpoint` | 设置源码断点 |
| `debug_remove_breakpoint` | 删除断点 |
| `debug_continue` | 继续运行并等待下一次停止 |
| `debug_step` | 执行 into / over / out |
| `debug_pause` | 暂停目标 |
| `debug_status` | 查询会话与当前停止位置 |
| `debug_threads` | 查询托管线程 |
| `debug_stack` | 查询调用栈 |
| `debug_variables` | 查询参数、局部变量或展开对象 |
| `debug_detach` | 安全分离调试器 |
| `debug_terminate` | 终止由调试器启动的目标 |

### 8.1 MCP 设计约束

- 采用 stdio 传输
- stdout 仅输出 MCP 协议内容，日志写入 stderr 或文件
- 所有会话内调用显式携带 `sessionId`
- 返回结构化 JSON，不要求 Agent 解析控制台文本
- `continue` 和 `step` 可支持等待至停止，也可返回操作 ID 后轮询
- 所有调用设置超时，防止 Agent 永久阻塞
- 首期不向 Agent 暴露原始 ICorDebug 对象或底层 COM 方法

## 9. CLI 设计

建议提供：

```text
fxdbg launch --exe app.exe --args "..." --arch auto
fxdbg attach --pid 1234 --arch auto
fxdbg break --session ID --file Form1.cs --line 120
fxdbg continue --session ID
fxdbg step --session ID --kind over --thread 3
fxdbg stack --session ID --thread 3
fxdbg variables --session ID --frame 5
fxdbg detach --session ID
```

CLI 与 MCP 必须复用相同 Host 和 Engine，不维护第二套调试逻辑。

## 10. 非功能需求

### 10.1 稳定性

- ICorDebug 回调不得直接执行耗时工作
- 所有调试命令进入单线程会话调度队列
- 严格处理 Continue 计数和停止/运行状态
- MCP Host 异常退出时，Engine 应尝试安全 Detach
- Engine 崩溃不得导致 Host 崩溃
- 附加模式默认不得终止目标进程

### 10.2 安全性

- 首期仅支持本机调试
- MCP 使用 stdio，不监听网络端口
- 默认禁止任意函数求值和目标状态修改
- 限制变量读取深度、数量和字符串长度
- 日志避免记录敏感变量值，可配置脱敏或完全禁用值日志

### 10.3 性能

- 停止后获取基础状态和前若干栈帧应在可接受时间内完成
- 大对象和数组采用分页读取
- 不主动遍历所有线程、帧和对象
- PDB 和元数据按模块缓存，并在模块卸载时清理

### 10.4 可观测性

- Host 和 Engine 分别记录日志
- 日志包含 sessionId、PID、线程、命令和状态变化
- 支持可配置日志级别
- 协议输出与诊断日志严格分离

## 11. 首期不做

- 原生/托管混合调试
- Edit and Continue
- Set Next Statement
- 修改变量值
- 任意表达式和函数求值
- 条件断点、数据断点和函数断点
- Dump 离线分析
- 多进程联调
- 跨机器 ICorDebug
- 远程 HTTP MCP
- Visual Studio 扩展
- 完整替代 Visual Studio 调试器

## 12. 分阶段实施

### 阶段 0：技术验证

完成以下验证后再进入产品开发：

1. 使用 ClrDebug 初始化 CLR `v4.0.30319` 的 ICorDebug。
2. 启动并附加简单的 FX 4.0 控制台程序。
3. 分别验证 x86→x86、x64→x64。
4. 收到 Process、AppDomain、Assembly、Module、Thread 等回调。
5. 读取 Windows PDB，并完成源码行到 IL Offset 的映射。
6. 命中源码断点并输出调用栈。

### 阶段 1：调试引擎 MVP

- launch / attach
- x86、x64 双引擎
- 自动架构选择
- Windows PDB
- 源码断点
- continue / step
- 调用栈
- 参数和基础局部变量
- 未处理异常
- CLI 验证入口

### 阶段 2：Agent 接入

- MCP stdio Host
- 结构化工具输出
- 会话、超时和取消管理
- Agent 使用说明 / SKILL
- 至少在一种外部 Agent 中完成端到端测试

### 阶段 3：可用性增强

- 对象和数组分页展开
- 源码路径映射
- 多 AppDomain
- Windows Service 支持增强
- ASP.NET / `w3wp` 附加
- 可选 DAP Host

### 阶段 4：高级能力

经过安全评估后考虑：

- 受限表达式求值
- first-chance exception 过滤
- 条件断点
- Streamable HTTP MCP
- CI 和远程宿主

## 13. 验收标准

MVP 满足以下条件即视为通过：

1. 在 64 位 Windows 上，通过同一 MCP 入口自动调试 x86 和 x64 的 FX 4.0 示例程序。
2. 能按源码文件和行号绑定 Windows PDB 断点，并正确报告是否绑定。
3. 命中断点后能返回停止原因、线程、源码位置和至少 10 层调用栈。
4. 能读取当前栈帧的方法参数、基础局部变量、字符串和一层对象字段。
5. 能完成 Continue、Step Into、Step Over、Step Out。
6. 能报告未处理托管异常的类型、消息、位置和调用栈。
7. MCP Host 或 Agent 断开时，能安全清理调试会话；附加目标不得被意外终止。
8. 不支持的 CoreCLR、位数不匹配、PDB 不匹配等情况均返回明确、可操作的错误。
9. 在 Console、WinForms 两类示例项目上完成端到端测试。

## 14. 主要风险

| 风险 | 影响 | 缓解措施 |
|---|---|---|
| Windows PDB 读取和匹配复杂 | 源码断点不可用 | 阶段 0 优先验证，不先开发 MCP |
| x86/x64 COM 位数限制 | 无法覆盖老旧应用 | 独立双引擎，由 Host 路由 |
| ICorDebug 异步状态机复杂 | 死锁、重复 Continue | 单线程命令调度，回调只入队 |
| Agent 频繁或错误调用 | 会话不稳定 | MCP 状态校验、超时、限流和 SKILL |
| 函数求值执行用户代码 | 卡死或改变业务状态 | MVP 禁止函数求值 |
| IIS 多 AppDomain 与影子复制 | 断点绑定失败 | 首期排除，后续专项支持 |
| ClrDebug API 或许可变化 | 维护成本 | 固定依赖版本并审查许可证 |

## 15. 建议项目结构

```text
FxDbg.sln
├─ src/
│  ├─ FxDbg.Core/
│  ├─ FxDbg.Interop/          # 对 ClrDebug 的薄适配层
│  ├─ FxDbg.Symbols.Windows/
│  ├─ FxDbg.Engine/
│  ├─ FxDbg.Engine.Protocol/
│  ├─ FxDbg.Host/
│  ├─ FxDbg.McpHost/
│  ├─ FxDbg.Cli/
│  └─ FxDbg.DapHost/          # 可选
├─ tests/
│  ├─ FxDbg.UnitTests/
│  ├─ FxDbg.IntegrationTests/
│  └─ Debuggees/
│     ├─ Fx40.Console.x86/
│     ├─ Fx40.Console.x64/
│     ├─ Fx40.WinForms.x86/
│     └─ Fx40.WinForms.x64/
└─ docs/
   ├─ architecture.md
   ├─ mcp-tools.md
   └─ agent-skill.md
```

## 16. 核心决策摘要

- **从头开发 FX 4.0 引擎，但使用 ClrDebug 包装 ICorDebug。**
- **SharpDbg 仅作为会话、DAP、变量模型的参考，不直接移植 CoreCLR 后端。**
- **采用一份 MCP Host 加 x86/x64 两份 Engine 的进程架构。**
- **默认自动判断架构，同时允许 `arch` 参数覆盖。**
- **MCP 首期采用 stdio；内部 Engine 通信采用 Named Pipe。**
- **先验证 Windows PDB 和断点，再开发 MCP 外壳。**
- **MVP 禁止函数求值和状态修改，以稳定性和可预测性优先。**
