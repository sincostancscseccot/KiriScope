# KiriScope

KiriScope 是面向 Windows 的 KiriKiri 引擎资源分析、提取、验证与研究工作台，提供 GUI 与 CLI。它优先处理图像等艺术资源，并将格式证据、作品方案、研究观察和可复现报告分开保存。

当前发布为 `0.1.0-preview.6`：阶段 7 的安全实用功能已经实现，包括 XP3 读取/提取与标准重打包、资源验证和受控转换、可配置内容过滤、静态分析、默认关闭的运行时证据、版本化知识库，以及只读松散文件覆盖计划。

## 从这里开始

- 想安装并完成第一次分析：[快速上手教程](docs/user-guide/GETTING_STARTED.md)。
- 想确认某项功能是否已经实现及其边界：[当前可用能力](docs/user-guide/CAPABILITIES.md)。
- 想直接下载：前往 [0.1.0-preview.6 预发布](https://github.com/sincostancscseccot/KiriScope/releases/tag/v0.1.0-preview.6)。

## 当前能力，一页看懂

| 你想做什么 | 当前入口 | 结果边界 |
|---|---|---|
| 识别 XP3、资源或二进制 | GUI 的资源检查，或 `probe`、`verify`、`xp3 list`、`analyze pe` | 只读；容器识别不等于已解码或已解密。 |
| 提取、转换或打包 | `xp3 extract`、`psb extract-all`、`convert`、`xp3 pack` | 只写入全新输出；不修改游戏目录，也不保证游戏加载结果。 |
| 使用已有过滤方案 | `xp3 extract --scheme`、`filter score` | 必须显式指定方案；候选要经过完整格式验证。 |
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
