# KiriScope 文档目录

当前可下载版本为 [0.1.0-preview.24](https://github.com/sincostancscseccot/KiriScope/releases/tag/v0.1.0-preview.24)。它已在一组获授权的受保护 XP3 样本上验证了真实路径恢复，并把“已写入但索引校验非标准”的运行时流交由内容识别与结构验证处理，避免将完整捕获误报为失败；适用范围和未知变体的限制以[当前可用能力](user-guide/CAPABILITIES.md)为准。

## 先读这里

- **最终产品体验与验收标准**：[一键式 KiriKiri 解包器：产品契约](product/ONE_CLICK_UNPACKER.md)。它明确普通用户只需选择输入、资源类型和新导出位置；方案 JSON 仅是内部兼容数据。
- **下一轮实际开工顺序**：[一键式解包器开工清单](development/RESTART_PLAN.md)。
- **当前已实现能力与边界**：[用户指南：当前可用能力](user-guide/CAPABILITIES.md)。这是当前版本的事实，不应与最终产品目标混淆。

阶段 6 的版本化方案知识库、兼容性矩阵、只读批量扫描和报告比较规则见 [knowledge/KNOWLEDGE_BASE.md](knowledge/KNOWLEDGE_BASE.md)。

如果要判断一项功能是否已经可用、它能产生什么证据以及有哪些边界，请先阅读[用户指南：当前可用能力](user-guide/CAPABILITIES.md)。

本目录保存项目的正式设计依据。实现行为与文档冲突时，应先确认并记录决策，再更新实现。

## 当前文档

- `product/`：面向最终用户的产品契约、主流程与验收场景。
- `PROJECT_PLAN.md`：早期架构、阶段路线图和已完成实施记录；当前产品方向以 `product/` 为准。
- `development/RESTART_PLAN.md`：重新对齐后的一键式解包器开工清单。
- `architecture/`：组件边界、数据流、插件协议、运行时辅助进程设计。
- `research/`：XP3、KiriKiri、内容过滤器和作品方案的证据化研究记录。
- `filters/`：可配置内容过滤器、方案文件格式和候选验证约束；当前 CxEncryption 说明见 `filters/CX_ENCRYPTION.md`。
- `analysis/`：PE、插件目录、字符串/常量观察与启发式候选报告的边界和使用方式。
- `integrations/`：可选外部工具适配规范；当前包含 Ghidra headless 的隔离项目与归档规则。
- `runtime/`：默认关闭的运行时 worker、离线 ProcMon 证据导入、归档与回归规则。
- `formats/XP3_PACKING.md`：阶段 7 的标准、无覆盖 XP3 重打包范围与限制。
- `user-guide/CAPABILITIES.md`：当前已实现功能、格式支持状态、命令分组与明确的未支持事项。
- `user-guide/LOOSE_FILE_OVERLAY.md`：阶段 7 的只读松散文件覆盖计划与限制。
- `user-guide/GETTING_STARTED.md`：安装、首次只读分析、受控导出、静态分析与运行时证据采集的快速教程。

## 后续文档目录

功能实现开始后按需增加：

```text
docs/
├─ decisions/       # ADR：重要技术决策及其理由
├─ product/         # 最终用户体验与验收标准
├─ formats/         # XP3、TLG、PSB、PIMG 等格式说明
├─ filters/         # 内容过滤器与参数推导说明
├─ integrations/    # Ghidra、DIE、FreeMote 等适配规范
├─ user-guide/      # GUI 与 CLI 使用手册
└─ development/     # 构建、调试、测试与发布流程
```

所有研究结论至少记录样本身份、证据、验证方法、适用范围和失败条件。
