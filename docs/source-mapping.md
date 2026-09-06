# 源码路径映射

`debug_launch` / `debug_attach` 可选参数 `sourceMappings` 配置整个会话，最多128条：

```json
[
  { "buildRoot": "C:\\build\\src", "localRoot": "D:\\workspace\\src" },
  { "buildRoot": "C:\\build\\src", "localRoot": "D:\\workspace\\plugin", "module": "Plugin.dll" }
]
```

`buildRoot` 是Windows PDB记录的构建源码目录，`localRoot` 是本地源码目录；两者必须是绝对Windows路径，支持驱动器与UNC路径、正反斜杠和空格。路径按Windows大小写不敏感语义规范化。模块选择器为含扩展名的精确文件名或完整绝对模块路径，不支持通配符；省略时作用于所有模块。

断点接收本地路径，针对每个已加载模块反向映射到PDB路径；栈、停止/异常位置及实际绑定位置正向映射为本地路径，并在`originalFilePath`中保留PDB原始路径（无映射时为空）。同一方向中，先从匹配当前路径的规则中选择模块级规则，再选最长完整路径段前缀，最后应用一次替换，不链式重写。没有匹配规则时保留原路径；例如`C:\build`不会误匹配`C:\builder`。

同一模块作用域内相同构建根映射不同本地根，或相同本地根反向指向不同构建根，创建会话前返回`invalid_request`。完整模块路径与模块文件名规则在实际模块上同时命中且无法唯一选择时，断点明确报告`unresolved`及歧义原因，源码查询明确报错。映射只提供位置转换，不复制源码、不下载文件，不将存在源码视为PDB匹配证据。

CLI在launch/attach时使用`--source-maps <JSON文件路径>`读取相同数组。现有不带配置的调用继续使用原有PDB路径。配置随创建会话固定；需要另一组映射时安全分离并创建新会话。模块延迟加载、PDB就绪后的重绑定沿用同一映射。

`eng/verify-stage3-2.ps1`运行双架构Debug/Release单元及真实MCP专项：将源码复制到含空格的另一目录，从大小写变化的本地路径设置断点，验证普通/延迟模块命中、模块规则优先、栈行号和原始路径。前序无映射用例纳入最终全量回归。
