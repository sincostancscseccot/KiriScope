# 源码目录

KiriScope 的实现已按职责拆分为下列项目；GUI 与 CLI 复用同一核心库，不通过彼此调用实现功能。

| 项目 | 职责 |
|---|---|
| `KiriScope.Core`、`KiriScope.IO` | 证据模型、诊断、项目元数据、哈希与安全输出路径。 |
| `KiriScope.Xp3` | XP3 探测、索引读取、提取与标准归档创建。 |
| `KiriScope.Resources` | PNG、BMP、TLG、PSB/PIMG、JPEG、WAVE 的验证、资源导出与受控转换。 |
| `KiriScope.Plugins.Abstractions`、`KiriScope.Filters.BuiltIn` | 稳定内容过滤器接口及内置 XOR、CxEncryption 实现。 |
| `KiriScope.Analysis`、`KiriScope.Integrations` | PE/插件目录静态研究、归档，以及可选 Ghidra headless 适配。 |
| `KiriScope.Runtime`、`KiriScope.Worker.Protocol`、`KiriScope.Worker.X86/X64` | 默认关闭的运行时证据协议、只读采集器和架构匹配辅助进程。 |
| `KiriScope.Knowledge` | 版本化知识库、指纹匹配、批量扫描与报告比较。 |
| `KiriScope.Cli`、`KiriScope.Gui` | 用户入口：可脚本化 CLI 与 WPF 桌面界面。 |

测试位于 `../tests/KiriScope.Core.Tests`。面向用户的功能状态与边界请看[当前可用能力](../docs/user-guide/CAPABILITIES.md)。
