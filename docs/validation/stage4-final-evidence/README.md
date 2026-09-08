# 阶段4最终审核证据

固定基线 `41c2274c49b0e63fa7a70f96964a158e38439c4a`，最终被测实现 `2aa8a9d4067646fecf0707760980ca629312396a`，包含数值、随包文档同步与真实卸载期间分离的阻塞修复。结论与限制见 [最终报告](../stage4-final-review.md)。本目录保存最终实现的证据；逐项早期批次保存在各任务报告对应目录，不混入最终覆盖率。

| 文件/目录 | 内容 |
| --- | --- |
| [detach-recovery/summary.json](detach-recovery/summary.json) | 分离竞态的最终压力验证与候选失败诊断，详见 [修复报告](../stage4-detach-recovery.md) |
| [independent-review.md](independent-review.md) | 独立 Standards/Spec 初审及修复后复核 |
| [review-fixes.md](review-fixes.md) | 逐项采纳决定与验证 |
| [regression/manifest.json](regression/manifest.json) | 最终管理员双配置全回归，293份当前批次证据及输入/恢复核对 |
| [admin-closed.json](admin-closed.json)、[admin-exit-check.json](admin-exit-check.json) | 已授权管理员后台进程关闭及退出证据 |
| [stage4-matrix/summary.json](stage4-matrix/summary.json) | 3 个专项的双配置完整结果，日志及进程监督记录在同目录 |
| [coverage-inputs.json](coverage-inputs.json) | 最终三份 XML 的路径、长度、SHA256 和版本 |
| [coverage-xml.zip](coverage-xml.zip) | 未修改的原始 Cobertura XML，ZIP 内为文件 basename |
| [coverage-changes.json](coverage-changes.json) | 文件/行/函数覆盖汇总及未覆盖变更行；实际程序集记录在原始 XML |
| [coverage-files.csv](coverage-files.csv) | 文件级变更覆盖率 |
| [uncovered-methods.csv](uncovered-methods.csv) | 所有未完全覆盖的已测量生产函数及覆盖率 |
| [summarize-coverage.ps1](summarize-coverage.ps1) | 校验 XML 哈希后合并行命中，并与固定 Git diff 求交集 |
| [coverage-harness/RealStage4Coverage.cs](coverage-harness/RealStage4Coverage.cs) | 可选真实调用覆盖率入口，复用已有套件及进程监督器 |
| coverage-logs/ | 单元与真实调用采集日志、子进程监督记录、恢复/构建日志 |
| [documentation-only-fix.json](documentation-only-fix.json) | 历史 `106b747` → `6f497bf` 文档修复的单文件差异证明；不代表最终实现未修改生产代码 |
| failed-regression/ | 文档同步及卸载分离修复前的失败批次，不作为整体通过依据 |

## 覆盖率重算

将 ZIP 三个文件分别恢复到 coverage-inputs.json 的对应路径后，在仓库根目录运行：

```powershell
./docs/validation/stage4-final-evidence/summarize-coverage.ps1 -RepoRoot $PWD.Path
```

脚本读取固定基线与被测提交，不使用当前 HEAD 代替原始范围；原始 XML 的源码根路径来自清单。输出为 artifacts/stage4-coverage/coverage-changes.json。不同机器先恢复相同 Git 提交及原始证据再重算，不能用新采集文件冒充本批次哈希。

## 新采集的复现步骤

在被测提交、Windows 及项目固定 SDK 上，先运行三个 stage4 验收脚本，生成双配置构建和发布目录。随后采集单元：

```powershell
foreach ($configuration in @('Debug','Release')) {
    dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj -c $configuration --no-build --no-restore --settings eng/coverage.runsettings --collect 'Code Coverage' --results-directory "artifacts/stage4-coverage/reproduce-unit-$configuration"
}
```

真实调用使用单独 harness，避免向正式解决方案添加运行环境门槛：

```powershell
$harness = 'docs/validation/stage4-final-evidence/coverage-harness/Stage4Coverage.csproj'
dotnet restore $harness
dotnet build $harness -c Debug --no-restore
$env:FXDBG_COVERAGE_ROOT = $PWD.Path
$env:FXDBG_COVERAGE_CONFIGURATION = 'Debug'
$env:FXDBG_COVERAGE_NODE = 'C:\Program Files\nodejs\node.exe'
dotnet test $harness -c Debug --no-build --no-restore --settings eng/coverage.runsettings --collect 'Code Coverage' --results-directory artifacts/stage4-coverage/reproduce-real-Debug
```

本批次双配置单元各 233 项、真实调用 11 项均通过；变更生产行697/738（94.44%），全部已测生产行4991/6099（81.83%）。coverage-logs的final-*文件名保留稳定名称，内容来自coverage-inputs清单的accepted-*最终采集；harness-build也已替换为本次构建。首次受限环境的依赖漏洞索引访问失败后，在普通宿主用户上下文恢复成功；没有关闭 NuGet 审计或更换固定依赖，不需要新的 UAC。新的结果目录/时间/哈希自然不同，需另建输入清单并重新汇总，不覆盖历史归档。

归档文本日志只去除尾随空白并统一末尾换行，测试内容未改写；原始 XML 保持字节不变并单独记录哈希。测试日志含受控夹具值及本机路径，未包含外部业务变量或凭据。
