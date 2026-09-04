# Windows PDB / DiaSymReader 技术验证结论

> 范围：ROADMAP 阶段 0-5；只讨论 Windows 上的经典 Windows PDB（MSF），不以 Portable PDB 作为验证证据。资料检索于 2026-09-04，来源均为 Microsoft/.NET 官方文档、官方源码或官方 NuGet 元数据。

## 结论

阶段 0-5 可采用以下固定组合：

```xml
<PackageReference Include="Microsoft.DiaSymReader" Version="2.2.11" />
<PackageReference Include="Microsoft.DiaSymReader.Native" Version="17.12.0-beta1.24603.5" />
```

`Microsoft.DiaSymReader` 只提供托管 COM 接口和辅助 API；Windows PDB 的实现来自 `Microsoft.DiaSymReader.Native`。官方仓库也明确把 Portable PDB 的实现列为另一个包，因此本验证不应引用 `Microsoft.DiaSymReader.PortablePdb`。[dotnet/symreader README](https://github.com/dotnet/symreader#readme)

托管包 `2.2.11` 是正式版，直接提供 `net10.0` 和 `netstandard2.0` 资产且无依赖；Native 包 `17.12.0-beta1.24603.5` 是预发行版，官方元数据列出 Windows x86、amd64、arm、arm64，且无框架资产和包依赖。阶段 0 探针固定 x64 时必须随输出携带 `Microsoft.DiaSymReader.Native.amd64.dll`；以后在 x86 Engine 内使用时必须携带 `Microsoft.DiaSymReader.Native.x86.dll`。[Microsoft.DiaSymReader 2.2.11](https://www.nuget.org/packages/Microsoft.DiaSymReader/2.2.11) [Microsoft.DiaSymReader.Native 17.12.0-beta1.24603.5](https://www.nuget.org/packages/Microsoft.DiaSymReader.Native/17.12.0-beta1.24603.5)

Native DLL 的架构跟随**读取 PDB 的进程架构**，不是目标程序集的 PE 架构。官方工厂按 `RuntimeInformation.ProcessArchitecture` 在 x86 进程 P/Invoke `Microsoft.DiaSymReader.Native.x86.dll`、在 x64 进程 P/Invoke `Microsoft.DiaSymReader.Native.amd64.dll`；工厂只支持相应进程架构。[SymUnmanagedFactory.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/SymUnmanagedFactory.cs)

## CVE-2023-36796 与版本边界

Microsoft 的公告确认：Windows 上 `Microsoft.DiaSymReader.Native.amd64.dll` 读取损坏 PDB 时可导致远程代码执行，而且 Microsoft 未给出缓解因素。公告明确列出的受影响/修复版本是 .NET Runtime Pack：6.0.0–6.0.21 修复于 6.0.22，7.0.0–7.0.10 修复于 7.0.11。[Microsoft Security Advisory](https://github.com/dotnet/runtime/issues/91948)

该公告**没有公布独立 `Microsoft.DiaSymReader.Native` NuGet 的受影响范围或“最低修复版”**。因此不能把 `16.11.29-beta1.23404.4`、`17.8.0-beta1.23475.2` 或任何其他独立包版本写成“Microsoft 官方确认的修复下限”。本项目选择 `17.12.0-beta1.24603.5`，是因为它由 Microsoft/RoslynTeam/dotnetframework 官方账户发布于 2025-01-15，明显晚于 2023-09-12 的安全修复，且覆盖所需的 x86/x64；这是一项项目固定版本决策，不是对官方 CVE 修复下限的转述。[Microsoft.DiaSymReader.Native 版本记录](https://www.nuget.org/packages/Microsoft.DiaSymReader.Native/17.12.0-beta1.24603.5) [Microsoft Security Advisory](https://github.com/dotnet/runtime/issues/91948)

Windows 自带的 .NET Framework `DiaSymReader.dll` 由 Windows/.NET Framework 累积更新修补；这不等价于把旧的独立 Native NuGet 包判定为安全。项目应使用随应用部署并固定的 Native 包，不应依赖机器上是否注册了某个 COM 版本。[.NET Framework September 2023 Security and Quality Rollup](https://devblogs.microsoft.com/dotnet/dotnet-framework-september-2023-security-and-quality-rollup-updates/) [SymUnmanagedReaderCreationOptions.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/SymUnmanagedReaderCreationOptions.cs)

## 打开 PE 与匹配的 Windows PDB

推荐路径是显式打开已确定的本地 PE/PDB，不使用 Binder 搜索符号服务器：

1. 用 `PEReader` 打开模块，确认 `HasMetadata`，调用 `ReadDebugDirectory()` 找到 `DebugDirectoryEntryType.CodeView`，再用 `ReadCodeViewDebugDirectoryData(entry)` 取 `Guid`、`Age` 和原始 PDB 路径；`entry.Stamp` 是匹配所需的时间戳。官方 API 在 CodeView 数据格式错误时会抛 `BadImageFormatException`，底层 I/O 错误会抛 `IOException`。[PEReader.ReadCodeViewDebugDirectoryData](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader.readcodeviewdebugdirectorydata?view=net-10.0)
2. 用只读 `FileStream` 打开明确的 PDB，调用 `SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(pdbStream, metadataProvider, SymUnmanagedReaderCreationOptions.Default)`。工厂会创建 Native reader，并把 PDB stream 与 metadata import 交给 `Initialize`；找不到/不能加载 Native 实现时抛 `DllNotFoundException`，接口版本不支持时抛 `NotSupportedException`。[SymUnmanagedReaderFactory.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/SymUnmanagedReaderFactory.cs)
3. 在读取任何符号结果前调用 `ISymUnmanagedReader4.MatchesModule(codeView.Guid, codeViewEntry.Stamp, codeView.Age, out matches)`，检查 HRESULT；只有 HRESULT 成功且 `matches == true` 才允许继续。该方法的官方接口注释明确说明它比较 PDB 中的 ID 与 PE/COFF Debug Directory 中的 ID。[ISymUnmanagedReader4.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/ISymUnmanagedReader4.cs)
4. 同一个 `PEReader` 调用 `GetMetadataReader()`，用于把 PDB 返回的 `mdMethodDef` token 解析为类型名和方法名。Microsoft 的示例也是 `PEReader` → `GetMetadataReader()` → `GetTypeDefinition()`/`GetString()` 的读取路径。[MetadataReader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadatareader?view=net-10.0)

最小调用骨架：

```csharp
using var peStream = File.OpenRead(modulePath);
using var pe = new PEReader(peStream);
var cvEntry = pe.ReadDebugDirectory()
    .Single(e => e.Type == DebugDirectoryEntryType.CodeView);
var cv = pe.ReadCodeViewDebugDirectoryData(cvEntry);

using var pdbStream = File.OpenRead(pdbPath);
var reader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(
    pdbStream,
    metadataProvider,
    SymUnmanagedReaderCreationOptions.Default);

Marshal.ThrowExceptionForHR(
    reader.MatchesModule(cv.Guid, cvEntry.Stamp, cv.Age, out bool matches));
if (!matches)
{
    // pdb_mismatch
}

MetadataReader metadata = pe.GetMetadataReader();
```

`SymUnmanagedReaderCreationOptions.Default` 不启用注册表 COM 回退，也不启用环境变量替代路径；阶段 0 应保留这个默认值，确保验证的确是项目固定的 Native 资产。[SymUnmanagedReaderCreationOptions.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/SymUnmanagedReaderCreationOptions.cs)

阶段 0 只读 documents/methods/sequence points 时，可采用返回 `false` 的最小 `ISymReaderMetadataProvider`；官方 Native reader 测试也用 dummy provider 打开 Windows PDB。阶段 1-7 开始读取局部变量和签名时，必须实现 `TryGetStandaloneSignature`，不能继续把空 provider 当作完整符号读取器。[SymUnmanagedFactoryNativeTests.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader.Native.Tests/SymUnmanagedFactoryNativeTests.cs) [ISymReaderMetadataProvider.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Metadata/ISymReaderMetadataProvider.cs)

## “源文件 + 行号”到方法和 IL Offset

推荐使用枚举而不是只调用 `GetMethodFromDocumentPosition`，因为同一源码位置可能包含多个方法（例如 lambda/局部函数）；官方 `ISymUnmanagedReader` 同时提供返回一个方法和返回多个方法的接口。[ISymUnmanagedReader](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedreader-interface)

具体算法：

1. `reader.GetDocuments()` 枚举全部文档；扩展方法内部使用两次调用获取实际长度。对每个文档调用 `document.GetName()`，在 Windows 上先规范化为绝对路径，再用 `OrdinalIgnoreCase` 与输入路径比较。官方接口定义 `GetDocuments` 返回 symbol store 的全部文档，`GetDocument` 也可按 URL 查找，但枚举更便于明确处理“无同名文档”。[ISymUnmanagedReader.GetDocuments](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedreader-getdocuments-method) [SymUnmanagedExtensions.Reader.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Extensions/SymUnmanagedExtensions.Reader.cs)
2. 对匹配文档调用 `reader.GetMethodsInDocument(document)`，然后对每个方法调用 `method.GetSequencePoints()`。官方接口定义 sequence point 同时给出方法内 CIL offset、document、start/end line/column；托管扩展将这些字段封装成 `SymUnmanagedSequencePoint`。[ISymUnmanagedMethod.GetSequencePoints](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedmethod-getsequencepoints-method) [SymUnmanagedExtensions.Method.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Extensions/SymUnmanagedExtensions.Method.cs)
3. 丢弃 `point.IsHidden`（其 start line 为 `0xFEEFEE`）；保留 `point.StartLine <= requestedLine && requestedLine <= point.EndLine` 的点。以 `(methodToken, offset)` 去重并按 token、offset 排序，所得 offset 是**方法起点相对的 IL offset**。[SymUnmanagedSequencePoint.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Extensions/SymUnmanagedSequencePoint.cs) [ISymUnmanagedMethod.GetSequencePoints](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedmethod-getsequencepoints-method)
4. `method.GetToken()` 取得 `mdMethodDef`。用 `MetadataTokens.EntityHandle(token)` 验证 handle kind 是 `MethodDefinition`，再依次调用 `metadata.GetMethodDefinition(handle)`、`methodDefinition.GetDeclaringType()`、`metadata.GetTypeDefinition(...)` 和 `metadata.GetString(...)` 拼出命名空间、类型、方法名；`GetDeclaringType` 的官方契约就是返回声明该方法的类型。[ISymUnmanagedMethod.GetToken](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedmethod-gettoken-method) [MethodDefinition.GetDeclaringType](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.methoddefinition.getdeclaringtype?view=net-10.0)

若产品后续需要基于“行内列位置”求精确 CIL 覆盖范围，可对每个候选方法调用 `GetRanges(document, line, column)`；它返回 `[start,end,start,end,...]` 的 CIL 区间对。阶段 0 的验收目标是行到可执行 sequence point，因此直接返回匹配点的 offset 集合更直观。[ISymUnmanagedMethod.GetRanges](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedmethod-getranges-method)

“PDB 已加载但该行没有映射”不是 PDB 读取失败，应返回 `loaded` 加空 `mappings`。断点层可在阶段 0-6/阶段 1-4 再决定将其标记为 `moved` 或 `unresolved`；符号层不应偷偷移动行号。

## 三类状态的互斥分类

| 对外状态 | 判定顺序 | 建议诊断字段 |
|---|---|---|
| `pdb_missing` | 在创建 PDB stream 前，预期 PDB 路径不存在。 | `assemblyFile`、`pdbFile`、`sourceFile`、`line`；不要抛异常代替状态。 |
| `pdb_mismatch` | PDB 文件存在、reader 成功初始化、`MatchesModule` HRESULT 成功，但 `matches == false`。 | PE CodeView `guid/stamp/age`；不要把它归入格式损坏。 |
| `read_failed` | 文件存在，但 PE CodeView 读取、PDB stream、Native reader 初始化、`MatchesModule` HRESULT 或 documents/methods/sequence-points 枚举失败。 | `errorType`、HRESULT（若有）、经过脱敏的消息；子原因至少区分 `pe_no_codeview`、`native_reader_unavailable`、`malformed_or_unsupported_pdb`、`io_error`。 |

`ReadCodeViewDebugDirectoryData` 明确会对坏格式抛 `BadImageFormatException`、对 I/O 抛 `IOException`；DiaSymReader 的托管工厂会对 native DLL 不可用抛 `DllNotFoundException`、不支持所需接口抛 `NotSupportedException`。这些异常都只能在 PDB 已存在之后归入 `read_failed`，不能据异常文本猜测成 `pdb_mismatch`。[PEReader.ReadCodeViewDebugDirectoryData](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader.readcodeviewdebugdirectorydata?view=net-10.0) [SymUnmanagedReaderFactory.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/SymUnmanagedReaderFactory.cs)

PE 没有 CodeView entry、存在多个含糊的 CodeView entry、或 PE 没有托管 metadata 时，也应是 `read_failed` 的明确子原因，而不是 `pdb_missing`。只有 `MatchesModule == false` 才是本验证定义下的 `pdb_mismatch`。[ISymUnmanagedReader4.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/ISymUnmanagedReader4.cs)

## 安全与资源约束

Microsoft 明确警告：打开来自不可信来源的 PDB 是安全风险；CVE 公告进一步确认损坏 PDB 曾可触发 RCE。因此即使固定了较新的 Native 版本，也必须继续把 PDB 视为不可信输入。[ISymUnmanagedBinder](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedbinder-interface) [Microsoft Security Advisory](https://github.com/dotnet/runtime/issues/91948)

- 不启用网络符号服务器或不受控 search path；阶段 0 只打开命令行明确给出的本地文件，并优先要求 PDB 与模块同目录。官方 Binder 文档正是对自动打开相关 PDB 的路径给出不可信输入警告。[ISymUnmanagedBinder](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/diagnostics/isymunmanagedbinder-interface)
- 对 PE/PDB 文件大小、读取总时长、document/method/sequence-point 数量设置上限；超限归入 `read_failed` 并给出专门原因。`PEReader`/`MetadataReader` 官方文档也警告这些 API 并非为不可信输入设计，畸形输入可能导致越界访问、崩溃或挂起。[System.Reflection.PortableExecutable](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable?view=net-10.0) [System.Reflection.Metadata](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata?view=net-10.0)
- 生命周期结束时释放 reader 的 COM 引用并关闭 PE/PDB stream；Native reader 还定义了 `ISymUnmanagedDispose.Destroy`，其拥有的内存只保证在 Destroy 前有效。[ISymUnmanagedDispose.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/ISymUnmanagedDispose.cs) [ISymUnmanagedReader4.cs](https://github.com/dotnet/symreader/blob/260aefcbf91e84ea4a4941336a0210656edd55d7/src/Microsoft.DiaSymReader/Reader/ISymUnmanagedReader4.cs)

## 阶段 0-5 验收建议

固定同一个已知源码行，报告应同时保留 method token、完整方法名、排序后的 IL offset 集合，并用反汇编或调试器人工核对。另生成三份机器可读样例：删除 PDB 得 `pdb_missing`；把另一轮构建的同名 PDB 替换进来得 `pdb_mismatch`；截断/破坏 PDB 得 `read_failed`。CVE 公告说明畸形 PDB 可能执行代码，因此损坏样例只应由本仓库内确定性脚本生成，使用固定包，且不得从外部下载随机恶意 PDB。[Microsoft Security Advisory](https://github.com/dotnet/runtime/issues/91948)
