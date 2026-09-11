# 受限表达式语法与固有函数增强

2026-09-11。需求为 ROADMAP「开发中」第一项及已接受 ADR-005；本组开发基线为 `f831779744ea743ebd35620652b4b244c4efb1e7`。

## 实现范围

Core 的闭合解析器增加三元、位运算、十六进制及基础数值显式转换；三元只读取选中分支，位运算采用 C# 优先级及数值提升，转换采用 unchecked，原整数算术仍检查溢出。新增固有函数只操作捕获的标量及经预算读取的数组元素。Array.IndexOf 使用精确标量类型/值或对象引用身份，拒绝多维数组，不调用目标 Equals。

Interop 根变量解析先匹配既有根，再回退 this 字段；一维搜索读取真实下界。所有能力经既有 Host/Engine 自动用于 CLI、MCP、DAP 和条件断点。七个表达式错误码、超时/取消分类和原预算不变。具体参数、null/NaN、三元分支运行时类型与浮点越界转换的限制见 [工具契约](../mcp-tools.md)。

## 本任务快速审核

| 维度 | 检查 |
| --- | --- |
| 需求 | 新语法、全部固有函数、裸名回退及断点条件继承均有实现和正反例 |
| 安全 | 产品变更没有 Eval、目标方法、目标写入或 Continue；数组搜索不分派目标相等方法 |
| 兼容 | 旧固有函数保留原解析参数上限；新三参数函数独立扩展；旧表达式和错误分类回归 |
| 边界 | 整数提升/窄化、浮点、布尔非短路、三元短路、空值、负下界、查找预算和字符串预算 |
| 文档 | mcp-tools 与技能 mcp-tools、agent-skill 与技能 workflow 按字节同步，并执行真实 skills@1.5.23 项目安装 |
| 覆盖率 | 按用户要求，快速审核不运行全套覆盖率及独立双轴审核；在第四项统一采集和验收 |

已关闭的阻塞：

- 重复发布 MCP 时 `Copy-Item -Recurse` 因已存在技能目录而失败。独立临时目录两次复制可稳定复现；添加 `-Force` 后复制成功，SKILL.md、workflow.md、mcp-tools.md 的源/目标 SHA256 完全一致。真实重复发布仍由专项矩阵复验。
- 新实例夹具最初改变了静态计数器作用域，改为原 Program 类型的实例方法，保留真实原静态副作用计数器。
- Release 优化使新增 this 与原 matrix 参数不可读取。断点后增加实际实例/参数状态核对，保留 Release 优化及原局部变量不可用/已优化错误一致性测试。针对性 x86/x64 Release 复验通过，未修改产品读取失败语义；临时诊断已移除。
- 技能工作流副本使用契约规定的 references/workflow.md，删除本轮误建的副本；真实技能安装和哈希校验通过。

## 验收记录

最终执行 `./eng/verify-stage4-1.ps1` 及 `./eng/verify-stage4-3.ps1`，默认 Debug/Release。两组各 14 个监督步骤均通过，共 28 步，无跳过、超时或强制进程清理。两份输入 SHA256 清单逐字节相同，各脚本也核对运行前后输入不变。每配置普通单元测试 315/315。

| 专项 | x86 Debug/Release | x64 Debug/Release |
| --- | --- | --- |
| 原生表达式：既有 15 项及新增 26 项精确结果、错误/预算/取消/期限、状态及 Continue 计数 | 通过 | 通过 |
| 真实 MCP、DAP、CLI：新语法、裸名回退、字符串、数组、原接口回归及目标状态 oracle | 通过 | 通过 |
| 条件断点：新增三元/位运算/Clamp 场景，旧条件、次数、错误策略、模块重载及四入口回归 | 通过 | 通过 |

原生表达式测试同时核对未新增 Stop/Continue，实例、静态、数组与副作用计数保持不变；目标恢复后状态 oracle 通过。仅对本任务的快速审核无遗留阻塞，可原地勾选。完整全仓回归、独立双轴审核和变更生产行 ≥90% 覆盖率仍留待第四项，不将本次专项称为全量验收。

精简结果与复现输入见 [求值矩阵](expression-extension-1-evidence/stage4-1-result.json)、[条件矩阵](expression-extension-1-evidence/stage4-3-result.json)、[输入哈希](expression-extension-1-evidence/stage4-1-inputs.sha256)、[技能安装](expression-extension-1-evidence/skill-install-result.json)。原始日志/进程/耗时记录保存在本机 `artifacts/expression-extension/task1-accepted/`，逐文件位置/长度/SHA256 见 [归档清单](expression-extension-1-evidence/manifest.json)；未上传外部制品存储。

失败批次仅保留在本机忽略目录 `artifacts/expression-extension/task1-failed-initial/`、`task1-failed-publish/`、`task1-failed-receiver/`。其中首轮复制目录可能包含前次历史文件，仅本报告明确列出的失败步骤属于本轮失败证据；不能将目录中历史通过日志计为本轮通过。
