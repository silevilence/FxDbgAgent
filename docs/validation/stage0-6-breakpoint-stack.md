# 阶段 0-6 源码断点与调用栈验证

> `eng/verify-stage0-6.ps1` 在 Debug、Release 下分别执行真实 ICorDebug 会话、Windows PDB 正反向映射和 WinDbg/SOS 对照。以下为 Release 代表结果，路径已改写为仓库相对路径。

## 断点绑定与命中

```json
{
  "sourceFile": "tests/Debuggees/Fx40.Console.x86/Program.cs",
  "line": 89,
  "methodToken": "0x0600000E",
  "ilOffset": 12,
  "states": ["pending", "verified"],
  "hit": true,
  "runtimeVersion": "v4.0.30319",
  "debuggerArchitecture": "x86",
  "callbackThreadId": 3,
  "commandThreadId": 1,
  "callbackCount": 12,
  "continueCount": 11
}
```

`ExitProcess` 不需要 Continue，故 12 个回调对应 11 次 Continue；其余回调逐个严格配对。

## 前 10 层已符号化托管栈

| # | 方法全名 | 模块 | IL Offset | 源文件:行 |
|---:|---|---|---:|---|
| 0 | `FxDbg.Debuggees.ConsoleX86.Program.BreakpointTarget` | `Fx40.Console.x86.exe` | 12 | `Program.cs:89` |
| 1 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel12` | `Fx40.Console.x86.exe` | 0 | `Program.cs:79` |
| 2 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel11` | `Fx40.Console.x86.exe` | 0 | `Program.cs:76` |
| 3 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel10` | `Fx40.Console.x86.exe` | 0 | `Program.cs:73` |
| 4 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel09` | `Fx40.Console.x86.exe` | 0 | `Program.cs:70` |
| 5 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel08` | `Fx40.Console.x86.exe` | 0 | `Program.cs:67` |
| 6 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel07` | `Fx40.Console.x86.exe` | 0 | `Program.cs:64` |
| 7 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel06` | `Fx40.Console.x86.exe` | 0 | `Program.cs:61` |
| 8 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel05` | `Fx40.Console.x86.exe` | 0 | `Program.cs:58` |
| 9 | `FxDbg.Debuggees.ConsoleX86.Program.StackLevel04` | `Fx40.Console.x86.exe` | 0 | `Program.cs:55` |

完整结果含 14 层，继续到 `StackLevel03`、`StackLevel02`、`StackLevel01`、`Main`。

## 绑定失败状态

指定尚未加载的 `NeverLoaded.Managed.dll` 时保持：

```json
{"bindingState":"pending","bindingStates":["pending"],"hit":false,"frames":[]}
```

目标模块已经加载但 method token `0x0600FFFF` 无效时转为：

```json
{"bindingState":"unresolved","bindingStates":["pending","unresolved"],"hit":false,"bindingError":"ClrDebug.DebugException: Error HRESULT E_INVALIDARG has been returned from a call to a COM component."}
```

## WinDbg/SOS 抽样对照

Windows SDK x86 CDB 在相同调用链的 `Debugger.Break` 处执行 .NET Framework SOS `!clrstack`，前 10 个项目方法依次为：

```text
BreakpointTarget
StackLevel12
StackLevel11
StackLevel10
StackLevel09
StackLevel08
StackLevel07
StackLevel06
StackLevel05
StackLevel04
```

顺序与 ICorDebug 报告完全一致；验收脚本分别提取两侧的前 10 个方法名并按序比较，而非只检查名称存在或保留人工截图。
