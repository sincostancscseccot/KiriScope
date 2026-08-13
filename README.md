# KiriScope

KiriScope 是面向 Windows 的 KiriKiri 资源解包器，提供 GUI 与 CLI。最终目标是让用户只需选择游戏目录、XP3 或完整游戏压缩包，导出资源类型和新的导出位置，即可完成自动发现、匹配、提取与汇总；分析、验证和方案库是支撑这一体验的内部能力。

## 最新预发布版：0.1.0-preview.24

`preview.24` 可从 [GitHub Releases](https://github.com/sincostancscseccot/KiriScope/releases/tag/v0.1.0-preview.24) 下载单文件 Windows x64 GUI。它面向普通用户提供“游戏目录 / XP3 / 完整游戏 ZIP → 资源类型 → 新导出目录 → 开始解包”的流程，不会修改输入游戏或压缩包。

- 对标准 XP3 可直接提取；对已内置并经当前输入验证的 Cx 配置，会自动尝试解码，不要求选择 scheme JSON。
- XP3 v3 的 `hnfn` 原始路径表会优先用于恢复真实目录和文件名；同名条目使用稳定的 `__duplicate-NNN` 后缀，绝不静默覆盖。
- 运行时回退保留可启动启动器的优先级；`VERSION.dll` 导入与保护段仅作诊断，不会覆盖实际启动兼容性。`preview.24` 仅从引擎线程的首次真实资源打开启动捕获，不会从后台线程重入 KiriKiri，也不会对已解码流重复应用解密过滤器；对于索引校验非标准的作品，已写入流会交给内容识别和结构验证决定是否导出。
- 已在获授权的《9-nine-天色天歌天籁音》完整样本上验证：写入 9,982 个唯一资源路径，未产生 32 位哈希式乱码导出名。

这不是“所有 KiriKiri 游戏均可自动解包”的承诺。不同作品可拥有自定义索引、过滤器、文件名表和密钥；未知或未验证的变体会报告并跳过，而不会把密文伪装成成功的资源。完整边界与后续工作见[当前可用能力](docs/user-guide/CAPABILITIES.md)和[产品契约](docs/product/ONE_CLICK_UNPACKER.md)。

当前工作树已具备 XP3 读取/提取、资源验证、可配置内容过滤、静态分析和知识库等基础，并实现了标准内容的一键解包：可输入游戏目录、独立 XP3 或完整游戏 ZIP，并按资源类别导出。导出后会对可识别内容执行有上限的结构验证，并显式报告路径类别与内容签名不一致的条目。对于已纳入受信任知识库的方案，只有“已验证、精确 SHA-256、唯一命中”时才会自动应用；参考知识库不包含任何商业作品兼容项。第三阶段现已提供只读 `research package`，用于将授权游戏目录的 XP3 摘要、脱敏静态分析、知识扫描和用户明确提供的既有运行时报告引用归档为一个全新的 JSON。当前可用功能与限制请以能力矩阵为准。

## 最终产品目标（开发中）

普通模式的流程固定为“**输入游戏目录、XP3 或完整游戏压缩包 → 选择资源类型 → 选择新导出目录 → 开始解包**”。已知兼容配置会由程序按内容指纹自动匹配，普通用户无需手动选择方案 JSON；未知保护会被明确报告而不会伪装成解密成功。完整游戏压缩包会作为只读外层容器处理；仅接受已实现且可安全枚举的格式。

完整的用户体验、边界和可验收场景见[一键式 KiriKiri 解包器：产品契约](docs/product/ONE_CLICK_UNPACKER.md)，下一轮工作见[开工清单](docs/development/RESTART_PLAN.md)。

## 从这里开始

- 想安装并完成第一次分析：[快速上手教程](docs/user-guide/GETTING_STARTED.md)。
- 想确认当前版本已经实现的功能及其边界：[当前可用能力](docs/user-guide/CAPABILITIES.md)。
- 想直接下载：前往 [0.1.0-preview.24 预发布](https://github.com/sincostancscseccot/KiriScope/releases/tag/v0.1.0-preview.24)。

## 当前能力，一页看懂

| 你想做什么 | 当前入口 | 结果边界 |
|---|---|---|
| 识别 XP3、资源或二进制 | GUI 的“资源验证”与“XP3 归档与方案”标签，或 `probe`、`verify`、`xp3 list`、`analyze pe` | 只读；容器识别不等于已解码或已解密。 |
| 提取、转换或打包 | GUI 的 XP3 导出，或 `xp3 extract`、`psb extract-all`、`convert`、`xp3 pack` | 只写入全新输出；不修改游戏目录，也不保证游戏加载结果。 |
| 使用已有过滤方案 | GUI 的单条格式验证，或 `xp3 extract --scheme`、`filter score` | 必须显式指定方案；候选要经过完整格式验证。 |
| 汇总未知样本研究 | `research package` | 只读汇总元数据、哈希和报告引用；不含游戏内容或原始二进制字符串，也不会触发运行时采集。 |
| 研究未知样本 | `analyze`、`knowledge`、显式启用的 `analyze runtime` | 观察与启发式候选不会自动成为算法、密钥或兼容性结论。 |

完整矩阵包含格式支持状态、所有命令分组、外部工具边界和未支持事项，见[当前可用能力](docs/user-guide/CAPABILITIES.md)。

## 项目原则

- 先证明容器、索引和内容状态，再宣称解密成功。
- 原始提取、解密结果、格式转换结果和验证报告相互分离。
- XP3 结构解析与作品专用内容过滤算法相互独立。
- GUI 与 CLI 共享同一核心能力，GUI 操作可映射为可复现命令。
- 运行时研究默认关闭，仅限已授权目标；不注入、不读内存、不自动启动驱动工具。
- 仅用于自有或获得授权的游戏副本。

## 文档导航

| 主题 | 文档 |
|---|---|
| 用户使用 | [快速上手](docs/user-guide/GETTING_STARTED.md)、[当前可用能力](docs/user-guide/CAPABILITIES.md)、[松散文件覆盖计划](docs/user-guide/LOOSE_FILE_OVERLAY.md) |
| 格式与过滤器 | [PSB/PIMG](docs/formats/PSB_PIMG.md)、[XP3 重打包](docs/formats/XP3_PACKING.md)、[CxEncryption](docs/filters/CX_ENCRYPTION.md) |
| 研究与证据 | [静态分析](docs/analysis/STATIC_ANALYSIS.md)、[运行时证据](docs/runtime/RUNTIME_EVIDENCE.md)、[知识库](docs/knowledge/KNOWLEDGE_BASE.md) |
| 外部工具 | [Ghidra headless](docs/integrations/GHIDRA_HEADLESS.md)、[FreeMote 适配](tools/FREEMOTE_ADAPTER.md) |
| 开发与发布 | [文档目录](docs/README.md)、[打包与签名](docs/development/PACKAGING.md)、[发布说明](docs/RELEASE_NOTES.md) |

## 许可证

KiriScope 以 [MIT License](LICENSE) 发布。第三方来源代码、工具与样本的单独归属和通知保留在 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)；游戏文件、私有样本和解密结果不属于本仓库的授权范围。

## 目录

| 目录 | 职责 |
|---|---|
| `docs` | 面向用户的手册、格式边界、研究规则、构建与发布文档 |
| `src` | .NET 核心库、CLI、GUI、运行时辅助进程和外部工具适配器 |
| `plugins` | 内容过滤器、作品方案和插件模板 |
| `tests` | 单元、集成、回归、损坏输入和端到端测试 |
| `samples` | 可公开或自行构造的最小测试样本及其元数据；不存放完整游戏资源 |
| `tools` | 第三方工具的可选适配说明；不复制大型第三方程序 |
