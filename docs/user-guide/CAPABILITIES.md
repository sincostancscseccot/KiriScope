# 当前可用能力

本页是 KiriScope **当前实现**的能力总览，而不是将来的路线图。最终的一键式解包器体验见[产品契约](../product/ONE_CLICK_UNPACKER.md)；它尚未在当前预览版完整实现。本文适用于 Windows x64 的预览版；所有操作仅应针对你拥有或已获得授权的文件与进程。

CLI 默认输出 JSON。凡是创建文件、目录或报告的命令，均要求目标尚不存在，并且不得写入输入文件或游戏目录树；请把输出放到单独的工作目录。

GUI 可通过 `Build-GuiSingleFile.ps1` 打包为一个自包含 EXE。它不包含 CLI；为保留显式运行时采集能力，x86/x64 worker 会嵌入 EXE，并且只在用户授权采集后临时展开。详见[打包说明](../development/PACKAGING.md)。

## 先理解结果等级

| 结果等级 | 表示什么 | 不表示什么 |
|---|---|---|
| `ContainerIdentified` | 已识别并完成适用的容器或头部检查。 | 已解码像素、已成功解密或游戏一定可加载。 |
| `FormatValidated` | 已完成该格式当前实现范围内的完整结构验证。 | 已支持所有变体或与某部作品兼容。 |
| `ContentUsable` | 输出内容通过了对应格式验证。 | 原始归档一定被目标游戏接受。 |
| `HeuristicCandidate` | 静态分析或知识库给出的研究候选。 | 已证实算法、密钥或作品兼容性。 |

## 能力总览

| 目标 | 可用命令/入口 | 当前能够完成 | 重要边界 |
|---|---|---|---|
| 先检查文件 | GUI 的“XP3 归档与方案”标签，或 `probe`、`verify`、`xp3 list`、`xp3 profile`、`psb profile` | GUI 可只读发现 `.xp3`、读取索引、显示条目状态；CLI 可计算 SHA-256，并读取 XP3 索引或 PSB/PIMG 的直接根资源。 | 全程只读；识别格式不等于解码。 |
| 一键解包标准内容 | GUI 的“一键解包”标签，或 `unpack <游戏目录\|XP3\|游戏.zip> <新输出目录> --category <类别>` | 只读扫描游戏目录、独立 XP3 或完整游戏 ZIP，按“全部/图片/音频/脚本/其他”导出未标记加密的条目；多归档按来源保留目录结构。导出后会对受支持签名执行有上限的结构验证，并报告内容格式与路径类别是否一致。若内置受信任知识库给出唯一的已验证 SHA-256 命中，也会自动应用对应方案；ZIP 仅会按需暂存包内 XP3、EXE 或 DLL 用于指纹计算。 | ZIP 仅作为只读外层容器；限制条目数和解压体积，拒绝路径逃逸、重复路径、损坏或受保护的包。超出验证大小上限或缺少验证器的文件会明确标为未验证。弱匹配、未验证配置或多个并列命中绝不自动选择方案。 |
| 导出已验证资源 | GUI 的 XP3 导出，或 `xp3 extract`、`psb extract`、`psb extract-all` | 提取未标记加密的 XP3 条目，或导出 PSB/PIMG 的可验证直接根资源。GUI 只接受全新且位于归档目录外的输出目录。 | 拒绝覆盖和输入目录内输出；导出原始资源不代表图层已合成。 |
| 转换图像 | `convert bmp-to-png`、`convert tlg5-to-png`、`convert batch-to-png` | 解码并重新验证普通 24/32 位未压缩 BMP 与标准未加密 TLG5；批量转换保留相对路径。 | BMP 的 RLE、嵌入 JPEG/PNG 和旧式 DIB 变体不会被强行转换。 |
| 兼容性后备转换 | `convert tlg-to-png --freemote <EmtConvert.exe>` | 显式调用用户提供的 FreeMote 工具，将临时副本转换为 PNG 后再验证。 | 不捆绑、不自动查找外部工具；成功只证明导出的 PNG 有效。 |
| 已知方案过滤 | GUI 的“验证所选方案”，或 `xp3 extract --scheme`、`--xor-hex`、`filter score` | 对已明确指定的方案应用内容过滤器，并以格式验证给候选评分。GUI 必须在一个已标记加密条目上达到 `FormatValidated`，才允许该方案全量导出。 | 不按文件名自动猜测方案；具体游戏支持必须另有样本、哈希和证据。 |
| 新建研究归档 | `xp3 pack` | 从暂存目录生成全新的标准、未加密、未压缩 XP3。 | 不原位编辑已有归档；不保证目标游戏接受生成的归档。 |
| 比较松散文件 | `overlay plan` | 只读比较参考目录和覆盖目录，生成哈希化的覆盖计划。 | 不复制、部署或删除游戏文件；加载优先级仍须人工验证。 |
| 静态研究 | `analyze pe`、`analyze directory`、`analyze archive`、`report compare static` | 读取 PE、导入、字符串和插件目录，生成可复现归档与差异报告。 | 不加载或执行输入；启发式观察不会升格为兼容性事实。 |
| 汇总未知样本研究 | `research package <游戏目录> <新报告.json>` | 将 XP3 的哈希/索引摘要、去除原始字符串的目录静态分析、可选知识库扫描和既有运行时报告的哈希引用汇总为可复现 JSON。 | 仅接受游戏目录和全新、目录外报告路径；不嵌入游戏内容或原始二进制字符串，不执行输入，也不会启动或采集运行时。 |
| 可选 Ghidra 分析 | `analyze ghidra ... --headless <工具路径>` | 在显式指定的外部 Ghidra 路径下运行 headless 分析并归档过程信息。 | 工具缺失时只报告诊断；KiriScope 不捆绑 Ghidra。 |
| 受控运行时证据 | `analyze runtime inspect`、`snapshot`、`import-procmon`、`compare-procmon` | 读取已授权 PID 的进程/模块元数据，或离线导入用户导出的 ProcMon CSV。 | 采集默认关闭；不注入、不读进程内存、不暂停/结束目标，也不启动 ProcMon。 |
| 知识库与候选匹配 | `knowledge validate/list/match/scan/compare`，以及 `unpack ... --knowledge-root <目录>` | 验证版本化知识库，对二进制做指纹匹配与只读批量扫描；一键解包只会使用已验证、精确 SHA-256 且唯一命中的方案。 | 独立的 `knowledge` 研究命令仍只输出候选；参考知识库不含商业作品兼容项，弱匹配或歧义绝不自动应用。 |

## 按格式查看支持状态

| 格式 | 当前状态 | 可用操作 |
|---|---|---|
| XP3 | 标准签名、未压缩或 zlib 压缩索引、多段条目可读。 | 列表、摘要、对未标记加密条目提取、按明确方案过滤、创建新的标准归档。 |
| 加密 XP3 | 仅支持用户明确指定的 XOR 或方案 JSON。 | 候选评分与提取；缺少可验证方案时会跳过，而非伪称成功。 |
| PNG | 完整结构验证：签名、块、CRC、IHDR 与 IDAT 解压。 | `verify`，以及 BMP/TLG5/外部后备转换后的输出验证。 |
| BMP | 常见 24/32 位未压缩或位域 BMP 可完整验证和转换。 | `verify`、`convert bmp-to-png`、批量转换。 |
| TLG5 | 标准未加密 TLG5 可原生解码。 | 元数据验证与 `convert tlg5-to-png`。 |
| TLG6 | 容器与元数据可识别。 | `verify`；若需转换可显式使用 FreeMote 后备。原生像素解码尚未实现。 |
| PSB/PIMG | 明文 M2 头、范围、根键和直接资源可验证。 | `verify`、`psb profile`、`psb extract`、`psb extract-all`。不进行图层合成。 |
| JPEG、WAVE | 结构与关键元数据可验证。 | `verify`；JPEG 不进行 DCT 样本级解码，未知压缩音频仅识别容器。 |

## 不在当前范围内

- 自动解开所有 KiriKiri 游戏、自动推导密钥或按文件名认定作品兼容性。
- TLG6 原生像素解码、PIMG 图层合成、加密索引/加密数据段重打包和对现有 XP3 的原位修改。
- 自动部署覆盖文件、修改游戏目录、启动目标游戏，或以任何方式保证游戏会加载生成文件。
- 内存扫描、注入、Hook、暂停/结束进程、键盘记录、网络包捕获、ETW 内核会话或自动启动带驱动的调查工具。
- 未经用户显式选择的第三方插件或外部工具的自动加载。

## 从哪里开始

- 第一次使用：阅读[快速上手](GETTING_STARTED.md)。
- 需要完整命令形式：直接运行 `KiriScope.Cli.exe`（或源码构建的 CLI）而不带参数。
- 处理 PSB/PIMG：阅读[PSB/PIMG 结构验证与导出](../formats/PSB_PIMG.md)。
- 使用 CxEncryption 或方案 JSON：阅读[CxEncryption 方案与评分](../filters/CX_ENCRYPTION.md)。
- 进行静态或运行时研究：阅读[静态分析](../analysis/STATIC_ANALYSIS.md)和[运行时证据采集](../runtime/RUNTIME_EVIDENCE.md)。
- 管理可复现方案：阅读[知识库与兼容性矩阵](../knowledge/KNOWLEDGE_BASE.md)。
- 创建测试归档或准备松散覆盖验证：阅读[XP3 重打包](../formats/XP3_PACKING.md)和[松散文件覆盖计划](LOOSE_FILE_OVERLAY.md)。
