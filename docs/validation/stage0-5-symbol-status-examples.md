# 阶段 0-5 Windows PDB 验证样例

> 由 `eng/verify-stage0-5.ps1` 在 Debug 与 Release 配置下生成并校验。以下路径改写为仓库相对路径，避免报告绑定到开发机。

## 已知源码行映射

```json
{
  "status": "loaded",
  "pdbFormat": "windows",
  "assemblyFile": "tests/Debuggees/Fx40.Console.x86/bin/Release/net40/Fx40.Console.x86.exe",
  "pdbFile": "tests/Debuggees/Fx40.Console.x86/bin/Release/net40/Fx40.Console.x86.pdb",
  "sourceFile": "tests/Debuggees/Fx40.Console.x86/Program.cs",
  "line": 41,
  "mappings": [
    {
      "methodToken": "0x06000001",
      "methodName": "FxDbg.Debuggees.ConsoleX86.Program.Main",
      "ilOffsets": [130],
      "sourceFile": "tests/Debuggees/Fx40.Console.x86/Program.cs",
      "startLine": 41,
      "endLine": 41
    }
  ],
  "errorType": null,
  "errorCode": null,
  "hResult": null,
  "error": null
}
```

独立使用 x86 反射读取程序集 metadata 与 `0x06000001` 的方法体，确认 IL offset 130 的操作码为 `0x72`（`ldstr`），即该 `Console.WriteLine` 语句的首条 IL 指令。验收脚本通过语句内容动态定位行号；本报告记录当前 Release 样例的具体值。

## PDB 缺失

```json
{
  "status": "pdb_missing",
  "pdbFormat": "unknown",
  "mappings": [],
  "errorType": null,
  "errorCode": null,
  "hResult": null,
  "error": "The requested Windows PDB file does not exist."
}
```

## PDB 与程序集不匹配

```json
{
  "status": "pdb_mismatch",
  "pdbFormat": "windows",
  "mappings": [],
  "errorType": null,
  "errorCode": null,
  "hResult": null,
  "error": "The Windows PDB signature does not match the assembly CodeView entry."
}
```

## PDB 读取失败

损坏样例由验收脚本在本地确定性生成，不从外部获取未知 PDB。

```json
{
  "status": "read_failed",
  "pdbFormat": "windows",
  "mappings": [],
  "errorType": "System.Runtime.InteropServices.COMException",
  "errorCode": "malformed_or_unsupported_pdb",
  "hResult": "0x806D000C",
  "error": "Windows PDB reading failed: 0x806D000C"
}
```
