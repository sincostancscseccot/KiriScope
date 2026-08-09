# PSB/PIMG：只读结构验证与安全资源导出

KiriScope 将 PSB/PIMG 作为容器和结构研究对象处理。它不会修改输入文件，也不会因文件名或魔数而宣称图像已经解码。

## `verify`

`kiriscope verify <file>` 对可识别的明文 M2 PSB 读取并验证：

- PSB 版本、头部偏移和适用的 Adler-32 头校验；
- 名称表与根对象字典键；
- 根对象中直接引用的资源索引及已验证的数据范围；
- 根对象中直接编码的无符号整数，例如常见的 `width` 和 `height`。

根键同时具有 `layers`、`width` 和 `height` 时，报告会给出 `PSB_PIMG_SIGNATURE_IDENTIFIED`。这只表示 PIMG 的结构签名已观察到；没有解析所有图层值、合成图层、解码内嵌 TLG，因而证据等级仍为 `ContainerIdentified`。

## `psb profile`

`kiriscope psb profile <input.psb-or-pimg>` 不创建输出。它只读取每个已映射的直接根资源的前 38 字节，报告格式签名；对其中的 TLG，还会尝试验证已经包含在这段头部内的版本、尺寸和通道元数据。最多报告 10,000 个直接资源。它不复制、解码或修改资源数据。

## 提取已验证资源

```powershell
kiriscope psb extract <input.psb-or-pimg> <root-resource-name> <new-output-file>
kiriscope psb extract-all <input.psb-or-pimg> <new-output-directory>
```

`extract` 只复制指定的、位于已验证资源范围内的根资源。`extract-all` 只复制所有这类**直接根资源**；它不递归猜测嵌套对象中的引用。批量结果的 JSON 会记录原始资源名、资源索引、输出路径、字节数和 SHA-256。

安全约束：

- 输出必须不存在，且位于输入文件目录树之外；
- 批量输出先写入唯一临时目录，只有全部复制完成且输入文件未改变时才提升为目标目录；
- 资源名会转换为受限的扁平文件名，并前置资源索引和根键位置，避免路径逃逸或同名覆盖；
- 批量导出最多 10,000 个资源、总计 4 GiB；
- 任何失败都会清理未完成的临时导出，不会修改输入。

导出原始资源不等于它们能够由目标游戏加载，也不等于 PIMG 已正确合成。若需要图层合成或 TLG6 原生解码，应提供可再分发或已授权的最小样本与期望渲染结果，以便建立独立的格式验证回归。
