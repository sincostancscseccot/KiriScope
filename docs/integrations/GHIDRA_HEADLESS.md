# Ghidra Headless 适配

Ghidra 是阶段 4 的可选外部适配器，不是 KiriScope 的基础 PE 解析依赖。只有用户显式执行以下命令时才会启动 Ghidra：

```powershell
kiriscope analyze ghidra <binary> <new-project-directory> <project-name> --headless <analyzeHeadless.bat-or-executable>
```

在 Windows 上，`--headless` 可以指向 Ghidra 发行版中的 `support\\analyzeHeadless.bat`。适配器会从同一发行版的 `Ghidra/application.properties` 读取版本、发行名称和修订号；这一步不执行目标文件。

## 输入、输出和可追溯性

调用前，适配器会验证输入和工具路径、计算输入 SHA-256，并拒绝项目名中的路径分隔符。它以新建方式创建 `<project-name>.gpr`、`.rep` 目录和 `<project-name>.kiriscope-analysis.json`；任一同名产物已存在即拒绝执行，避免覆盖先前研究。

归档 JSON 包含：

- 输入绝对路径、SHA-256 和长度；
- Ghidra 工具路径、版本、发行名称、修订号和属性文件位置；
- 完整参数化命令、退出码、超时状态，以及受长度限制的标准输出/错误；
- 已生成 `.gpr` 项目的路径、SHA-256 和长度；
- KiriScope 诊断。

默认外层超时为 20 分钟；Ghidra 的每文件分析超时会使用相同上限。外层超时或非零退出码都记录为失败，不会把部分项目误报为成功。工具缺失时只返回 `GHIDRA_TOOL_NOT_FOUND` 警告，不创建项目目录，也不影响内置静态分析。

## 解释规则

Ghidra 的字符串、反编译结果或自动分析警告是外部工具证据，而非方案结论。将它们关联到过滤器候选时，必须在研究记录中保留输入哈希、Ghidra 版本、命令、相关偏移/函数和独立格式验证结果；不得从项目文件本身推导密钥或宣称作品兼容性。
