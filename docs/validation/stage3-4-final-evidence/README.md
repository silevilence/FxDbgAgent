# 阶段3-4最终证据快照

固定实施基线为`cda481eea0d3ac1d4b27bab2ba858838a844c7ec`，冻结实现为`a0b4a2c6851b25414d247da75bc0bff755eccea5`。本目录保存机器结果、输入哈希及补充审核实验；完整原始日志和Cobertura仍在本机`artifacts/stage3-4-validation/`等验收目录。旧成功记录不替代本批次结果。

`stage3-4-result.json`记录2026-09-07 08:02:09Z的Debug/Release专项通过，a～e全部exit=0、skipped为空。`stage3-result.json`与`stage2-result.json`记录随后全部阶段3及阶段2/1/0通过。完整批次`8183476133644ecd9d46e7b5114573bc`于07:31:35Z开始、08:23:55Z结束；`run-summary.json`核对88份监督日志及进程记录的时间窗、哈希与退出状态。正常步骤退出0，延迟删除/中断安装反例按预期退出1/197；无超时或强制清理。269项输入前后一致。

服务、权限、IIS、DAP及VS Code JSON记录本次真实目标或客户端结果；`iis-lifecycle-*.json`包括多域、动态模块和回收快照。`environment-delayed-state-*.json`有意保留延迟删除反例的未完成状态，恢复成功另看removed清单与environment-result.json。`environment-check-final-removed.json`记录最终中断恢复清理，不能把反例状态当作遗留失败或成功清理。

## 覆盖率复核

`coverage-inputs.json`固定六份成功采集的路径、长度及SHA256：最终103项单元、完整批次内真实权限Debug/Release、真实IIS Debug/Release，以及重新执行的ACL审核实验。全部对应本轮实现，未沿用85bec38汇总。汇总脚本逐一核对哈希，不扫描其他批次；再按固定Git基线与审核提交定位生产新增/修改行。共享源码和双架构/配置重复测量按源文件及行号取命中并集。采集使用`eng/coverage.runsettings`，只排除Fx40夹具探针，保留生产程序集及Engine子进程。

保有清单中的原始附件时，在仓库根目录运行：

```powershell
./docs/validation/stage3-4-final-evidence/summarize-coverage.ps1 -RepoRoot (Get-Location).Path
```

输出为`artifacts/stage3-4-validation/coverage-changes.json`。12个生产变更C#文件均有测量：变更可执行行140/155（90.32%）；所采集生产代码总体3635/4990（72.85%）。前者达到本次变更审核90%阈值，不表示全仓或每个程序集各自达到90%。未覆盖函数见[整体审核](../stage3-4-final-review.md)。`unit-final.log`记录最后一次103/103成功采集。

## 补充ACL审核实验

`acl-audit/`直接引用冻结发布Host，没有复制产品逻辑或改变一键验收输入。实验启动自己的30秒Console样例，取得句柄/创建时间，以受限线程令牌查询架构；只对该子进程加入查询拒绝ACE，断言Host返回actionable access_denied；恢复原DACL后断言查询成功并比对原SDDL。finally恢复ACL、清理自建进程并记录退出。没有附加调试器或修改业务进程。

2026-09-07 08:26:16Z重新实测1/1通过，进程63172已退出。实验复制Host DLL与本轮发布物SHA256均为`B7D48CB2325F39E93DC2A222FAEA0B94631EE2E017ABC48A312C0A67ABF9FC87`。独立Standards代理已复核实验源码及归并方式；本目录保存本轮成功日志、恢复与退出结果及XML哈希。

复现需保有本轮Debug发布物与Console样例；以普通用户在仓库根目录运行，无需UAC：

```powershell
$env:FXDBG_COVERAGE_ROOT = (Get-Location).Path
$env:DOTNET_CLI_HOME = Join-Path $env:FXDBG_COVERAGE_ROOT 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $env:FXDBG_COVERAGE_ROOT 'artifacts/nuget-packages'
dotnet test docs/validation/stage3-4-final-evidence/acl-audit/AclAudit.csproj `
    "-p:FxDbgRoot=$env:FXDBG_COVERAGE_ROOT" -c Debug `
    --settings eng/coverage.runsettings --collect 'Code Coverage;Format=cobertura' `
    --results-directory artifacts/stage3-4-validation/acl-audit-reproduction
if ($LASTEXITCODE -ne 0) { throw 'Supplementary ACL audit failed.' }
```

复现属于新批次，不自动混入冻结清单。API依据为Microsoft的[内核对象安全描述符](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-setkernelobjectsecurity)与[受限令牌](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken)文档。

## 启动入口修复证据

`entry-cleanup/`保留先前失败、隔离旧Engine对照、修复后双架构压力和三轮单元采集日志。`cycles-x86-then-x64.jsonl`前200条为x86、后200条为x64，各cycle=0～199；执行命令、范围和结论限制见[入口清理记录](../stage3-4-entry-cleanup.md)。这些实验用于定位修复，最终完整通过以本目录的新批次结果为准。

## 保留给用户的环境与管理员工作进程

`default-environment-state.json`与`default-environment-health.json`属于完整验收之后的默认环境刷新，非验收专用环境的清理记录。默认Stage3-4在08:29:21Z复查通过，保留两个LocalService服务和回环IIS站点供用户使用。两个站点均含最新late.aspx；default-environment-files.json记录14项页面、Service/Web/Late DLL与Windows PDB和本轮Debug构建的哈希一致性。

`admin-worker-stopped.json`与`admin-worker-stop-result.json`记录用户授权的临时管理员工作进程于08:32:35Z停止；PID33084已核对退出。未保留计划任务、开机启动项或通用提权服务。
