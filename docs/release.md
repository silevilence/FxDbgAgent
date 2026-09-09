# GitHub Actions 自动发布

`.github/workflows/release.yml` 将 MCP 与 DAP Host 发布到同一个 `fxdbg-<版本>.zip`，例如 `V0.1.0` 对应 `fxdbg-0.1.0.zip`。二者共用根目录相邻的 `engines/`，包内保留 `skills/fxdbg-agent/`、自有代码 PDB、引擎依赖清单及 `bundle-manifest.sha256`。DAP 入口为 `dotnet <解压目录>/fxdbg-dap.dll`，配置见 [dap.md](dap.md)；MCP 安装见 [skill-mcp-install.md](skill-mcp-install.md)。VSIX 仍通过 `eng/package-extension.ps1` 单独打包。

## 发布步骤

1. 在 `main` 完成版本变更与人工验收，将发布说明写入 `changelog.md` 的独立二级标题，例如 `## V0.1.0`。标题匹配忽略大小写，必须唯一且内容非空。正文截止到下一个二级标题，保留三级标题、列表和代码块。
2. 确认待发布提交已进入远端 `main` 历史，并包含工作流与发布脚本。发布前的真实环境、Service/IIS、DAP/VS Code、覆盖率及阶段验收由发布者完成。
3. 为该提交创建版本 tag 并推送，例如：

   ```powershell
   git tag V0.1.0 <已验收的提交>
   git push origin V0.1.0
   ```

4. 在 GitHub Actions 的 **Release** 工作流查看结果。成功后在 GitHub Releases 下载 zip。失败时修复原因，再按具体情况重跑失败作业或发布包含修复的新版本。

版本 tag 支持 `V` / `v` 前缀的 SemVer 2.0.0，包括 `v0.2.0-rc.1` 和构建元数据；预发布版本标记为 GitHub prerelease。不要为同一版本同时推送大小写两种 tag；它们是两个不同 Git tag。Release 关联实际 tag，标题采用 changelog 标题的原始大小写，正文取该版本条目。

普通分支 push 不启动工作流。tag glob 先筛选版本形状，再由只读 `eligibility` 作业严格检查 SemVer 与 `git merge-base --is-ancestor HEAD origin/main`。无效 SemVer、非 `main` 历史提交跳过 `release` 作业；删除 tag 不发布。由于 GitHub 的事件过滤器不能表达祖先关系，候选 tag 可能产生一次检查记录，但不会构建或创建 Release。完整历史来自 `checkout` 的 `fetch-depth: 0`，发布作业固定检出事件 SHA。

## 流水线范围与失败处理

- Windows Server 2022 runner，`setup-dotnet` 读取 `global.json`（10.0.301，同 feature band 补丁策略由该文件控制）。依赖动作固定 commit SHA。
- 先读取发布说明，然后复用 `publish-mcp.ps1`、`publish-dap.ps1` 的 Release 入口，依次发布到同一目录。两个入口内部均构建解决方案；不启用阶段验收复用上下文。
- 只执行 `tests/FxDbg.UnitTests` 的普通单元测试，不运行阶段验收、真实调试目标、环境安装或覆盖率采集。单元测试中已有 PDB 文件读取测试，不安装或启动 Service/IIS。
- 打包前检查两种 Host、双架构引擎、native 依赖、技能和 PDB 的存在，要求引擎目录内自有代码 PDB 为 Windows MSF 7.00 格式。发布目录清单与 zip 的每个文件均以 SHA256 比对；任一失败阻止后续 GitHub 操作。
- 仅发布作业授予 `contents: write`，其余权限关闭；只读检查作业使用 `contents: read`。使用内置 `GITHUB_TOKEN`，无需额外 PAT。
- 全部检查通过后才访问 Release 写接口。首次创建草稿、上传资产、最后公开；上传失败时草稿保留，重跑会继续更新。同名 Release 使用相同标题/正文并覆盖同名 zip；同一 tag 的运行串行化。已公开 Release 的资产覆盖遵循 GitHub CLI `--clobber` 行为，上传失败可重跑恢复。
- changelog 缺失、版本条目缺失/重复/为空时，在构建与写接口之前明确报错；应在 `main` 补齐对应版本说明后发布正确提交。

## 本地复现与校验

使用 PowerShell 7，在仓库根目录选择一个**全新的输出目录**：

```powershell
$output = "artifacts/release-local-$(Get-Date -Format yyyyMMddHHmmss)"
./eng/release-notes.ps1 -Tag V0.1.0 -OutputDirectory $output
./eng/publish-mcp.ps1 -Configuration Release -OutputDirectory "$output/bundle"
./eng/publish-dap.ps1 -Configuration Release -OutputDirectory "$output/bundle"
dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }
./eng/package-release.ps1 -Tag V0.1.0 -BundleDirectory "$output/bundle" -OutputDirectory $output
./eng/tests/release-tests.ps1 -BundleDirectory "$output/bundle"
```

本地步骤生成 zip 并测试发布脚本，不创建 GitHub Release。`release-tests.ps1` 使用隔离 Git 仓库与模拟 GitHub CLI，验证版本大小写、SemVer 边界、main 祖先关系、发布说明、更新重试、上传失败和包损坏。测试原始输出保留在被忽略的 `artifacts/`。

在解压目录核对所有文件（SHA256 清单不包含自身）：

```powershell
Get-Content ./bundle-manifest.sha256 | ForEach-Object {
    if ($_ -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid manifest line: $_" }
    $expected = $Matches[1]
    $file = $Matches[2]
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $expected) {
        throw "SHA256 mismatch: $file"
    }
}
```

协议发布布局的来源始终是既有 `publish-host.ps1`；打包脚本不筛掉文件、不重新生成二进制或 PDB，zip 根目录就是合并发布目录的完整内容。net48 与 netstandard2.0 项目固定 `DebugType=full`，使 Engine 及其自有共享依赖输出 Windows PDB。

工作流语义参考：[GitHub workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)、[setup-dotnet](https://github.com/actions/setup-dotnet)、[gh release create](https://cli.github.com/manual/gh_release_create)、[gh release upload](https://cli.github.com/manual/gh_release_upload)。
