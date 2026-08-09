# CxEncryption 方案与验证

`builtin.cx-encryption` 是 KiriKiri CxEncryption 内容过滤器的参数化实现。它在每个 XP3 分段解压后运行，并使用条目 Adler-32 与已解压条目内的逻辑偏移；因此同一文件即使跨多个 XP3 分段，计算结果也保持一致。

当前支持两种公开可复现的程序随机序列：`standard` 和 `nana`。它们共享同一算法 ID，差异完全由方案参数表达。项目不会凭文件名自动挑选方案，也不会把某一作品名称当作参数来源的证明。

## 方案文件

方案文件为 JSON，必须显式给出算法 ID、算法版本和参数来源。`controlBlock` 是 1024 个小端 `uint32` 的 Cx 控制块：可以是 JSON 数组，也可以是 4096 字节的小端十六进制字符串。它应保存为对应 TPM 插件中控制块的去混淆字值，而非直接复制原始插件字节。

```json
{
  "id": "research.example-cx-standard",
  "displayName": "Research example (Cx standard)",
  "algorithmId": "builtin.cx-encryption",
  "algorithmVersion": "1.0",
  "parameterSource": {
    "kind": "verified-static-analysis",
    "reference": "sha256:<TPM-or-analysis-artifact>",
    "notes": "Bind this to the exact executable/plugin version and a retained derivation record."
  },
  "parameters": {
    "mask": "0x00000000",
    "offset": 0,
    "prologOrder": [0, 1, 2],
    "oddBranchOrder": [0, 1, 2, 3, 4, 5],
    "evenBranchOrder": [0, 1, 2, 3, 4, 5, 6, 7],
    "randomFamily": "standard",
    "controlBlock": "<8192 hexadecimal characters: 1024 little-endian uint32 values>"
  }
}
```

`randomFamily: "nana"` 时还必须添加 `randomSeed`。加载器会拒绝错误的排列、控制块长度、未知算法或版本不匹配，而不是静默降级。

## 候选评估

```powershell
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- `
  filter score <ciphertext> <scheme-a.json> <scheme-b.json> `
  --entry <xp3-entry-name> --adler32 <hex-or-decimal>
```

该命令会独立应用每个方案，输出方案和参数来源、密文/明文差异范围、检测到的资源格式、验证诊断和评分。只有 PNG、BMP、WAVE 或 JPEG 达到 `FormatValidated` 才被标为 `IsAccepted: true`；TLG、PSB、PIMG、Ogg 的当前“仅识别”状态仍会保留为低分证据，不会被当作成功解密。

`xp3 extract <archive> <output> --scheme <scheme.json>` 使用相同的方案加载器，并将方案描述和算法版本写入 JSON 报告。CxEncryption 方案必须有 XP3 索引提供的 Adler-32；缺失时会返回 `CX_ADLER32_REQUIRED`，不会生成伪成功输出。

## 适用范围与来源

实现以 GARbro 的 MIT 许可 CxEncryption 参考实现为依据进行了独立改写，许可证通知位于仓库根目录的 `THIRD_PARTY_NOTICES.md`。这只证明算法家族和参数格式的实现来源；任何具体作品支持声明仍必须绑定合法取得的样本版本、归档哈希、参数推导记录和格式验证结果。
