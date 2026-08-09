# KiriScope

当前状态：阶段 7 已完成首个安全实用扩展集。除阶段 6 的知识库能力外，现可新建标准 XP3 归档，并可只读生成松散文件覆盖计划；任何具体作品方案仍须以合法样本、哈希和完整格式验证证据单独登记。详见 [知识库文档](docs/knowledge/KNOWLEDGE_BASE.md)。

KiriScope 是一个面向 Windows 的 KiriKiri 引擎资源分析、解密、提取与验证工作台。

项目优先处理图像等艺术资源，并同时提供 GUI 与命令行界面。其目标不是承诺用固定算法解开所有作品，而是把已知方案、未知方案研究、运行时观察、格式验证和可复现报告组织成一套可持续扩展的工具链。

> 当前状态：阶段 7 已完成首个安全实用扩展集。除安全的 XP3 读取/提取、资源验证/转换、可配置 CxEncryption、可复现静态分析、默认关闭的运行时证据采集和知识库能力外，现已具备无覆盖的标准 XP3 重打包与只读松散文件覆盖计划；它们不修改游戏目录，也不宣称目标构建一定接受重打包或覆盖文件。

## 项目原则

- 先证明容器、索引和内容状态，再宣称解密成功。
- 原始提取、解密结果、格式转换结果和验证报告相互分离。
- XP3 结构解析与作品专用内容过滤算法相互独立。
- GUI 与 CLI 共享同一核心能力，GUI 操作应能映射为可复现命令。
- 离线分析优先；静态和运行时逆向能力作为逐级增强的研究路径。
- 仅用于自有或获得授权的游戏副本。

## 快速上手

新用户可从[快速上手教程](docs/user-guide/GETTING_STARTED.md)开始：安装或从源码构建、只读分析 XP3/PSB/PIMG、受控导出、静态分析，以及显式授权的运行时证据采集均有可复制命令。教程明确区分“结构已识别”和“格式已验证”，并说明所有输出必须位于输入目录之外。

## 许可证

KiriScope 以 [MIT License](LICENSE) 发布。第三方来源代码、工具与样本的单独归属和通知保留在 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)；游戏文件、私有样本和解密结果不属于本仓库的授权范围。

## 文档入口

- [项目计划书](docs/PROJECT_PLAN.md)
- [文档目录](docs/README.md)
- [目标架构](docs/architecture/README.md)
- [研究记录规则](docs/research/README.md)
- [静态分析说明](docs/analysis/STATIC_ANALYSIS.md)
- [Ghidra headless 适配](docs/integrations/GHIDRA_HEADLESS.md)
- [运行时证据采集](docs/runtime/RUNTIME_EVIDENCE.md)
- [快速上手教程](docs/user-guide/GETTING_STARTED.md)
- [源码目录规划](src/README.md)
- [测试策略](tests/README.md)
- [发布说明](docs/RELEASE_NOTES.md)
- [打包与签名](docs/development/PACKAGING.md)
- [架构决策记录：.NET 10](docs/decisions/ADR-0001-dotnet-10.md)

## 当前可用能力

\`\`\`powershell
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- version
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- probe <文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- verify <资源文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 list <XP3文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 pack <暂存目录> <新建XP3文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 extract <XP3文件路径> <输出目录>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 extract <XP3文件路径> <输出目录> --xor-hex <十六进制密钥>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 extract <XP3文件路径> <输出目录> --scheme <方案JSON>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- filter score <密文文件> <方案JSON> [更多方案JSON] --entry <XP3条目名> --adler32 <十六进制或十进制值>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- psb profile <PSB或PIMG文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- psb extract <PSB或PIMG文件路径> <内嵌资源名> <输出文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- psb extract-all <PSB或PIMG文件路径> <新输出目录>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze pe <二进制文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze directory <游戏目录路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze archive <二进制文件路径> <新报告JSON路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze ghidra <二进制文件路径> <新项目目录> <项目名> --headless <analyzeHeadless.bat或可执行文件>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze runtime snapshot <PID> <新报告JSON路径> --enable-runtime-capture
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze runtime inspect <PID>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze runtime import-procmon <PID> <ProcMon导出的CSV> <新报告JSON路径> --enable-runtime-capture
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- analyze runtime compare-procmon <PID> <前次CSV> <后次CSV> <新报告JSON路径> --enable-runtime-capture
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- knowledge validate <知识库根目录>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- knowledge match <知识库根目录> <二进制文件>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- knowledge scan <知识库根目录> <输入目录> <新报告JSON路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- overlay plan <参考目录> <覆盖目录> <新报告JSON路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- report compare static <左静态归档JSON> <右静态归档JSON> <新报告JSON路径>
\`\`\`

\`probe\` 以只读方式读取文件，计算 SHA-256，并检查标准 XP3 签名和第一个索引偏移。\`xp3 list\` 可读取标准或zlib压缩的 XP3 索引，列出已验证的条目元数据。\`xp3 pack\` 从暂存目录创建新的标准、未加密且未压缩 XP3；它拒绝写入暂存目录内或覆盖现有文件，且会记录来源/归档哈希。\`xp3 extract\` 可安全提取未标记加密的条目（含多段与zlib解压）；加密条目会被跳过并在JSON报告中标出，不会被误报为已解密。

\`verify\` 会先识别真实资源格式。当前对 PNG 完成签名、块边界、CRC、IHDR 和完整 IDAT 解压验证，并仅在这些检查成功后标记为 \`FormatValidated\`；对未变种 TLG5/TLG6（含 SDS 包装）验证头部与图像元数据，并只标记为 \`ContainerIdentified\`，因为尚未解码像素；对明文 M2 PSB 验证头、偏移范围和适用的 Adler-32 头校验，并安全读取根键、直接资源范围及直接无符号整数。根键包含 \`layers\`、\`width\`、\`height\` 时会标记为 PIMG 候选，但不会宣称已完成图层组合或像素解码。

加密过滤器通过稳定接口加载。CLI 的 `--xor-hex` 是分段、偏移和报告链路的参考实现；方案 JSON 可配置 `builtin.repeating-xor` 或 `builtin.cx-encryption`，并必须记录参数来源。具体作品的兼容性声明仍须以相应版本的合法样本、参数推导记录和完整格式验证为依据。

阶段 3 已加入 `builtin.cx-encryption` 的 `standard` 与 `nana` 变体、JSON 方案文件、参数来源记录以及候选评分。`filter score` 会输出方案、算法版本、来源、密文/明文差异范围和格式验证结果；仅达到完整结构验证的候选才会被接受。方案格式见 `docs/filters/CX_ENCRYPTION.md`。

阶段 4 已加入不执行输入文件的 PE/插件静态分析。`analyze pe` 将直接观察与启发式候选明确分层；`analyze directory` 报告目录中的二进制清单和可解析的导入关系；`analyze archive` 以无覆盖方式保留输入哈希和复现命令。Ghidra 仅通过显式 `analyze ghidra` 调用，完整记录工具版本、命令、进程输出、输入与项目哈希。详见 `docs/analysis/STATIC_ANALYSIS.md` 和 `docs/integrations/GHIDRA_HEADLESS.md`。

阶段 5 已加入默认关闭的运行时研究链路。`analyze runtime snapshot` 必须带 `--enable-runtime-capture`，并只通过架构匹配的 x86/x64 隔离 worker 读取目标 PID 的进程和模块元数据；输出用新建 JSON 归档记录目标/worker 哈希、时点和诊断。用户手动导出的 ProcMon CSV 可离线导入或比较，但 KiriScope 不会启动 ProcMon、加载驱动或自动观察进程。详见 `docs/runtime/RUNTIME_EVIDENCE.md`。

对于已验证为明文结构的 PSB/PIMG，\`psb profile\` 会只读其直接根资源的短头，报告检测到的格式及可验证的 TLG 元数据；\`psb extract\` 可将指定的根资源（例如 \`10.tlg\`）复制到新建的输出文件；\`psb extract-all\` 可批量复制所有具有已验证范围的直接根资源，并为每份输出记录 SHA-256。两种提取都拒绝覆盖，也拒绝在输入目录树内写入；批量导出使用新目录和临时目录，失败时不会提升不完整输出。它们仅提取已验证的原始资源，尚不代表图层合成或图像解码成功。详见 [PSB/PIMG](docs/formats/PSB_PIMG.md)。

当前也支持常见未压缩或位域 BMP 的文件/DIB 头、调色板和像素数据范围验证，验证通过时标记为 `FormatValidated`。RLE、嵌入 JPEG/PNG 和旧式 DIB 变体则只会保守地标记为已识别，直到专用解码器可用。

对于 24 位或 32 位、未压缩的 BMP，`kiriscope convert bmp-to-png <input-bmp> <output-png>` 会解码为 RGBA，再写出并重新验证一个不覆盖原文件的 PNG。其他 BMP 变体会被明确拒绝，而不会产生可能错误的转换结果。

标准未加密 TLG5 也可通过 `kiriscope convert tlg5-to-png <input-tlg> <output-png>` 解码、转换并重新验证。`kiriscope convert batch-to-png <input-directory> <output-directory>` 会批量处理 BMP 与 TLG5，保留相对路径、拒绝输出冲突及输入目录内的输出位置。TLG6 和非标准变体仍只报告已识别的容器状态；需要兼容性后备时，可显式指定 FreeMote 工具路径：`kiriscope convert tlg-to-png <input-tlg> <output-png> --freemote <EmtConvert.exe>`。该命令只把临时副本交给外部工具，并由 KiriScope 验证输出 PNG。

对 RIFF/WAVE，项目会验证分块边界以及 PCM/IEEE 浮点音频的采样元数据和帧对齐；未知压缩编解码只会标记为容器已识别。

JPEG 已验证标记序列、帧尺寸、扫描段与结束标记，并可在 GUI 中预览通过验证的文件；当前验证器不宣称已完成 DCT 样本级解码。

## 顶层目录

| 目录 | 职责 |
|---|---|
| `docs` | 产品边界、架构、路线图、决策与研究文档 |
| `src` | 后续的 .NET 核心库、CLI、GUI、运行时辅助进程和外部工具适配器 |
| `plugins` | 内置之外的内容过滤器、作品方案和格式扩展 |
| `tests` | 单元、集成、回归、损坏输入和端到端测试 |
| `samples` | 可公开或自行构造的最小测试样本及其元数据，不存放完整游戏资源 |
| `tools` | 开发期脚本和第三方工具适配说明，不复制第三方大型程序 |
