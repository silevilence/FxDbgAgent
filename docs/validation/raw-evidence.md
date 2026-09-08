# 原始验收证据的归档与恢复

2026-09-08将原始日志、进程记录、逐调用流水、原始覆盖率压缩包和超过64 KiB的会话快照移出文档目录。报告、精简结果、输入哈希、覆盖率汇总和验证源码仍保留在Git中。旧报告中描述的日志目录是历史采集位置；当前工作树中的原始附件应按本页恢复，不能把文件缺席解释为该历史批次没有执行。

本次归档768份文件，其中644份原先受Git跟踪；全部文件在删除前均验证了ZIP解压内容的长度及SHA256。逐文件原路径、长度、SHA256及是否受Git跟踪见 [归档清单](raw-evidence-archive.json)。独立Agent验收读取的五份冻结夹具未迁移。

## 本机归档

- 文件：`artifacts/evidence-archives/validation-raw-20260908.zip`，约1.35 MiB。
- ZIP SHA256：`462F15FD3BBF2B42B8E4FF0990E36084FE8E6843974E64669686866D331134F5`。
- ZIP条目保留仓库相对路径，包括原始 `coverage-xml.zip`；未修改附件内容。
- 归档被Git忽略，当前只存本机，没有上传外部制品存储。其他机器不能仅凭这个本机路径取得未跟踪附件。

在仓库根目录校验并解压到独立的本地目录，不覆盖工作树：

```powershell
$archive = 'artifacts/evidence-archives/validation-raw-20260908.zip'
$expected = (Get-Content docs/validation/raw-evidence-archive.json -Raw | ConvertFrom-Json).archiveSha256
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Archive hash mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath artifacts/restored-validation
```

解压后例如 `artifacts/restored-validation/docs/validation/stage4-final-evidence/coverage-xml.zip` 就是原覆盖率ZIP。按原 `coverage-inputs.json` 恢复其中三个XML到对应采集路径，再使用保留的汇总脚本重算。其他历史报告中的日志链接统一指向本页，原始附件的精确路径以归档清单及原结果JSON为准；JSON中的原路径和原始哈希没有改写。

## 从Git历史恢复已提交附件

归档时的原提交为 `2f45a9de3114bbd2531c5c74bab9255359d39553`。未重写Git历史，清单中 `tracked=true` 的644份附件也能从该提交取回，例如：

```powershell
git archive --format=zip --output=artifacts/validation-from-git.zip 2f45a9de3114bbd2531c5c74bab9255359d39553 docs/validation
Expand-Archive -LiteralPath artifacts/validation-from-git.zip -DestinationPath artifacts/restored-git-validation
```

Git历史只包含当时已提交的附件，不能恢复清单中的124份未跟踪本机文件。需要长期共享这些附件时，应保存本机ZIP到外部制品存储，并补充位置和SHA256。

## 后续保留方式

原始输出继续保存在 `artifacts/`；Git只接收报告和精简的结果/证据清单。新增 `*-evidence` 文件按 `.gitignore` 白名单处理，单个机器摘要原则上不超过64 KiB，逐文件哈希清单除外。具体规则见根目录 [AGENTS.md](../../AGENTS.md)。
