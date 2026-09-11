# 独立双轴审核与修复复核

基线 f831779744ea743ebd35620652b4b244c4efb1e7。首轮审核覆盖 263ec32、0cd7011、d09d7ff 及第四项工作树验收入口；两名独立只读子代理按 code-review 技能并行审查，未运行构建/测试。主线程负责实测与证据。修复复核覆盖下述最终工作树，最终生产源码以同目录 coverage-inputs.json 和输入清单固定。

## Standards

原发现1项P2：String.Trim在累计字符串预算检查前分配结果，违反ADR-004的“分配前检查预算”。完全采纳；先扫描裁剪边界，检查预算，再Substring。原审核者复核确认空串、全空白、无需裁剪边界和既有预算拒绝测试齐全，问题关闭。

其他规范检查未发现COM越层、目标方法调用/Eval/写入/Continue、停止缓存寿命、输入冻结、历史证据冒充新验收等阻塞。隐藏条件命中使用独立上下文，缓存不保存运行值。扩展入口继承或创建单轮上下文，拥有上下文的入口最终核验产物哈希。原始证据仅本机的限制明确记录。没有另报判断性代码异味。

Standards：原发现1项，已修复1项，遗留阻塞0项。结论为静态复核通过。

## Spec

原发现2项P2：

1. 只按Property表选择getter，不能识别无Property行的MethodImpl或隐式虚覆盖，可能代读基类字段而实际分派执行其他实现。完全采纳；检查相关层级MethodImpl与更派生类同名方法，不能证明槽映射时保守拒绝。真实Framework目标通过Reflection.Emit分别创建显式和隐式隐藏覆盖，且普通继承属性作为允许对照。固定ClrDebug的GetVirtualMethod及GetVirtualMethodAndType注明未实现，未调用不可用API或复制第三方interop。
2. 字段getter丢失声明返回类型，例如byte字段的int属性会显示byte，并错误允许与ulong混合的位运算。完全采纳；字段证明保留返回签名，按IL栈与声明返回类型解释读取值。真实用例覆盖byte→int、byte、uint、ulong，以及返回int后与ulong位运算拒绝。float→double的C#getter实际包含conv.r8，仍按精确IL白名单拒绝。

原审核者复核确认两项已关闭，元数据枚举计入预算，普通继承getter保留，修复没有引入目标执行、Continue或字段值缓存。其余语法、固有函数、字段token身份、缓存及诊断符合ADR-005。

Spec：原发现2项，已修复2项，遗留阻塞0项。结论为静态复核通过。完整Debug/Release验收和覆盖率由最终报告另行证明。
