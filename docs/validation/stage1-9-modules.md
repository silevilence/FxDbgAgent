# 阶段 1-9 模块与符号验收

日期：2026-09-05。依据 ROADMAP 阶段 1-9、需求 §10.3。

- 每个模块加载实例有独立 ID；快照包含路径、名称、AppDomain、本地 PDB 路径、符号状态及诊断。Module Load、Unload 和符号状态变化复用 Core 会话事件序列。
- 符号状态为 Loaded、Missing、Mismatch、ReadFailed。匹配使用 DLL CodeView 与 Windows PDB 身份，损坏文件和本地文件访问错误均返回状态，不中断整个会话。
- 方法名称元数据按模块缓存，卸载时清除名称、PDB 和原生断点缓存；退出后活动模块列表为空。事件仅保留不可变模型快照，不持有 COM 对象。

验证：`eng/verify-stage1-9.ps1 -Configuration Debug` 和 `Release` 均通过；51 项单元测试通过。真实 x86/x64 覆盖缺失→有效但不匹配→损坏→恢复匹配 PDB，模块跨 AppDomain 卸载重载的新实例身份、卸载事件、退出清理和统一事件顺序；同时回归阶段 1-4 断点生命周期和阶段 1-6 线程/14 层栈。构建零警告、零错误。

快速审核修复必要阻塞：InvalidDataException 原先未被符号读取错误过滤器捕获；现映射为 ReadFailed。文件属性访问失败同样降为符号诊断。恢复 PDB 保留模块身份，实际卸载再加载才换 ID。

结论：阶段 1-9 可原位勾选。
