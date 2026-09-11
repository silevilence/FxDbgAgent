# IL 证明的属性代读

2026-09-11。本任务基线 `263ec32ba335d1f321ec2d129a654f7003784f48`，需求为 ROADMAP 第二项及 ADR-005。

## 实现与安全边界

精确字段优先，随后从 ExactType 沿基类解析零参数属性；虚覆盖使用运行时实现。Core 只识别精确字段/常量 IL，Interop 验证方法签名、已加载模块的真实方法头、RVA/代码地址/长度、字段 token 和槽身份，再复用原字段读取。getter、用户相等方法、类型初始化器都不由解释器执行，不新增 Continue 或目标写入。

实际 Desktop CLR 的普通 ICorDebugCode 不提供 ICorDebugILCode，不能用后者读取异常子句。因此依据 [ECMA-335 II.25.4](https://ecma-international.org/publications-and-standards/standards/ecma-335/) 只读检查 tiny/fat 方法头：tiny 无异常段，fat 必须无 MoreSects、无未知标志且代码长度/地址匹配。不读取磁盘替身，不接受动态/内存模块或无法核对的头。单方法仍 ≤16 字节；nop、分支、调用、异常/附加段、多字段及超长 getter 不会被“优化”成允许模式。

FieldDef 绑定模块和 token；MemberRef 对照声明类型（含同模块泛型实例的实际类型参数）、名称和签名，唯一匹配才读取。固定 ClrDebug 0.4.2 把 ResolveTypeRef 标记为不可用，跨模块 TypeRef 不能证明时保守拒绝；未绕过过时告警调用不可靠 vtable，也未增加或复制第三方 interop。

停止级缓存只保存元数据和证明，不保存字段值；普通停止、域/模块引用失效沿用原清理点，隐藏条件命中各用新上下文。根解析惰性缓存到单个表达式结束，保留短路不读取的语义；根元数据、字段/属性枚举和证明都计入新增 256 次探针预算。IL 累计 ≤256 字节，方法头读取计入原读取预算。失败不保留半成品目录或证明。

## 本任务快速审核

| 维度 | 检查结论 |
| --- | --- |
| 允许/拒绝边界 | List/Queue/Stack.Count、auto/表达式体字段属性、运行时虚覆盖、静态、常量、值类型、泛型字段；计算型 Dictionary.Count、计算覆盖、显式接口和索引器拒绝 |
| 字段身份 | 同名字段优先、FieldDef 模块+token、MemberRef 所有者和签名、错误泛型实例拒绝 |
| 执行纪律 | 属性路径无 Eval/目标调用/写入/Continue API；真实目标 getter/格式化/冷类型初始化副作用计数为 0，继续配对计数不变 |
| 预算 | 真实 260 字段类型在探针 257 执行前停止；预热仅类型目录后，平衡表达式实际读取 252 字节 IL，再读下一 getter 前拒绝；没有以 AST 深度限制冒充 IL 限制 |
| 缓存 | 真实第二次停止后读取目标自行修改的 24；停止上下文对象已替换，旧帧失效；重复根只扫描一次，短路未触发元数据探针 |
| 测试可靠性 | Release 保持优化；显式预热被测静态字段，冷类型作为独立拒绝场景；第二停止后实际实例检查保持接收者存活 |
| 文档 | 工具及技能副本字节同步，真实项目级 skills@1.5.23 安装校验通过 |

快速审核修复根元数据未计入新增预算的问题，并用惰性根描述复用避免重复遍历。未将完整独立审核或覆盖率测量提前到本任务。

## 验收记录

最终依次执行 `./eng/verify-stage4-1.ps1`、`./eng/verify-stage4-3.ps1`，默认 Debug/Release，共 28 个监督步骤全部通过；无跳过、超时或强制清理。原生 x86/x64、MCP、DAP、CLI 两配置通过，每配置普通单测 319/319。两份输入清单逐字节相同，运行前后输入核对通过。原生矩阵包含 17 个属性通过案例、9 个拒绝案例、真实预算边界及第二停止缓存验证；此前语法、字段/数组、取消/超时和条件断点全部回归。

仅本任务快速审核无遗留阻塞，可原地勾选并本地提交。整体独立双轴审核、全部前序真实环境回归及变更生产行 ≥90% 覆盖率仍待第四项。

证据：[求值结果](expression-extension-2-evidence/stage4-1-result.json)、[条件结果](expression-extension-2-evidence/stage4-3-result.json)、[输入哈希](expression-extension-2-evidence/stage4-1-inputs.sha256)、[技能安装](expression-extension-2-evidence/skill-install-result.json)。完整日志/进程/耗时记录仅存本机 `artifacts/expression-extension/task2-accepted/`，位置、长度及 SHA256 见 [归档清单](expression-extension-2-evidence/manifest.json)，未上传外部制品存储。
