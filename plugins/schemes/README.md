# 内容过滤器方案库

可将方案修订登记到上级 `plugins/knowledge-base.json`，以便同时绑定方案 SHA-256、算法版本、样本证据和适用版本。修改既有方案文件会导致其已登记修订的哈希校验失败；请新建修订而非覆写旧结论。具体规则见 [知识库文档](../../docs/knowledge/KNOWLEDGE_BASE.md)。

每个方案文件都描述一个算法的具体参数集和参数来源。方案不是“按游戏名称猜测”的别名；提交新方案前应保留适用样本的版本、哈希、导出命令和至少一个完整格式验证结果。

当前内置算法：

- `builtin.repeating-xor`：仅用于验证流水线和研究样本。
- `builtin.cx-encryption`：KiriKiri CxEncryption 的 `standard`/`nana` 参数化实现；格式见 `docs/filters/CX_ENCRYPTION.md`。

`reference-repeating-xor.scheme.json` 是可运行但不对应任何商业作品的最小示例。不要将命令行密钥或未证实的参数写入报告之外的共享方案库。

`cx-9nine-kokonotsu.scheme.json` is registered through `../static-filter-profiles.json`. It is not selected by its descriptive name: the one-click unpack flow requires two independent Adler-32 proofs from marked entries in the input selected by the user. The profile is source-bound by SHA-256 in that manifest; changing its parameters requires a new revision and matching manifest hash.
