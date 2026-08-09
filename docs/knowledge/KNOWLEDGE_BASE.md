# 版本化知识库与兼容性矩阵

阶段 6 的知识库把可复查的方案文件、适用范围、样本证据和扫描候选分开保存。它帮助研究人员在新版本二进制中寻找**可验证的下一步**，但不会按文件名猜测作品、不会自动应用过滤器，也不会把静态线索描述成已解密或已兼容。

## 目录和版本规则

选定目录的根文件必须是 `knowledge-base.json`。每个 `schemes` 条目引用同一目录树内的方案 JSON，并包含：

- `id` + 三段数字 `revision`：一个方案修订的不可变身份。新结论使用新修订；`supersedes` 只记录关系，绝不改写旧结论。
- `schemeSha256`、`algorithmId`、`algorithmVersion`：加载时同时校验实际方案文件哈希及内置方案描述符，防止元数据和参数悄然漂移。
- `status`：`ReferenceOnly`、`Candidate`、`Verified`、`Incompatible` 或 `Retired`。
- `applicability`：目标 ID、目标版本和限制说明。ID 是证据标签，不是游戏名或文件名匹配规则。
- `evidence`：样本版本、样本 SHA-256、复现命令和验证阶段。`Verified` 只能使用 `FormatValidated` 或 `ContentUsable` 的证据，且必须至少有一条记录。

顶层 `compatibility` 是目标版本与指定 `schemeId@schemeRevision` 的显式矩阵行。它同样不能跳过已验证证据。仓库中的 `plugins/knowledge-base.json` 仅登记合成的重复 XOR 参考方案，不对任何商业作品作出声明。

## 指纹与候选

可选 `fingerprint` 的全部条件必须同时命中：输入 SHA-256、PE 架构、导入模块、已提取字符串，以及 `ObservedFact` 静态分析发现。`HeuristicCandidate` 发现永远不能作为指纹条件的证据。

匹配输出的 `KnowledgeSchemeCandidate.Kind` 固定为 `HeuristicCandidate`，即使知识库条目的状态为 `Verified` 亦然。它只表示“可以在离线过滤器和格式验证流程中尝试这个指定修订”，不表示当前文件已能解密，也不会选择、执行或导出该方案。

## 命令

```powershell
kiriscope knowledge validate <knowledge-root>
kiriscope knowledge list <knowledge-root>
kiriscope knowledge match <knowledge-root> <binary>
kiriscope knowledge scan <knowledge-root> <input-directory> <new-scan-report.json>
kiriscope knowledge compare <left-scan-report.json> <right-scan-report.json> <new-comparison-report.json>
kiriscope report compare static <left-static-archive.json> <right-static-archive.json> <new-comparison-report.json>
```

`validate` 和 `list` 只读取知识库。`match` 只做一次静态分析并输出候选。`scan` 只遍历 `.exe`、`.dll`、`.tpm` 和 `.xp3`，按路径稳定排序，默认最多 1,024 个文件；每个二进制只做一次静态分析，XP3 只做只读容器探测。无法访问的文件会留在报告诊断中。它不写入输入树、不加载插件 DLL、不提取资源。

所有扫描和比较报告都写入新的 JSON 路径，拒绝覆盖既有报告；报告绑定知识库清单 SHA-256、输入文件 SHA-256、UTC 时间和等价复现命令。`compare` 不重新扫描，且只列出路径、输入哈希和候选修订的新增、删除或变化；不同知识库修订会产生警告，而不是被解释成兼容性变化。

`report compare static` 比较两份既有静态分析归档，不读取原始二进制。它列出输入 SHA-256/长度、PE 架构、导入模块以及带 `Kind` 的分析发现增减，所有条目都是事实差异，绝不提升为算法、密钥或兼容性结论。

## 提交前检查

1. 方案 JSON 在不修改核心代码的前提下可由内置加载器加载。
2. 方案修订、方案 SHA-256、参数来源和算法版本一致。
3. 每项“已验证”结论都绑定合法样本、版本、哈希、复现命令及完整格式验证。
4. 新旧修订并存，不能通过修改旧文件或覆盖旧报告改变历史结论。
5. 不提交游戏文件、商业资源、运行时内存、命令行密钥或尚未证实的参数。

外部内容过滤器的起点见 [插件模板](../../plugins/templates/content-filter/README.md)。外部 DLL 被视为受信任本机代码，当前版本不会自动发现或加载它们。
