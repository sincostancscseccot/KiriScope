# 快速上手

KiriScope 是 Windows 上的 KiriKiri 资源分析工作台。请只分析你拥有或已获授权的副本。默认工作流是只读的；任何生成文件的命令都要求一个**不存在**、且不位于输入目录内的输出路径。若尚不确定某个格式或命令是否已经支持，请先查看[当前可用能力](CAPABILITIES.md)。

## 选择启动方式

### 便携版或安装版

安装版完成后，从开始菜单启动 `KiriScope`；便携版解压后运行 `KiriScope.Gui.exe`。命令行程序位于同一目录的 `KiriScope.Cli.exe`，不需要额外安装 .NET 运行时。

先确认版本：

```powershell
.\KiriScope.Cli.exe version
```

### 单文件 GUI

若获得 `KiriScope.Gui-<version>-win-x64.exe`，可直接运行该文件，不需要预装 .NET。它只提供 GUI；需要 CLI 时请使用便携版或安装版。单文件版本在普通资源分析时不会写入旁文件；只有你显式授权运行时采集时，才会把内嵌 worker 临时展开到当前用户的临时目录。

### 从源码运行

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet restore .\KiriScope.slnx
dotnet build .\KiriScope.slnx -c Release
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- version
```

## 第一次分析

用 GUI 时，在“资源验证”标签点击 **打开资源…**，选择一个 PNG、BMP、TLG、PSB/PIMG 或其他资源。左侧会显示证据等级和诊断；只有在安全转换可用时，**将所选资源转换为 PNG…** 才会启用。

CLI 的等价只读命令：

```powershell
# 识别文件、计算哈希并探测容器
.\KiriScope.Cli.exe probe "D:\AuthorizedGame\data.xp3"

# 验证单个资源的结构
.\KiriScope.Cli.exe verify "D:\AuthorizedGame\image.tlg"

# 读取 XP3 索引；不会提取条目
.\KiriScope.Cli.exe xp3 list "D:\AuthorizedGame\data.xp3"

# 读取 PSB/PIMG 的根资源与嵌入格式元数据
.\KiriScope.Cli.exe psb profile "D:\AuthorizedGame\scene.pimg"
```

`verify` 的成功等级很重要：`ContainerIdentified` 只表示结构被识别；`FormatValidated` 才表示完成相应格式的完整验证。TLG6 当前只验证容器和元数据，不宣称已完成原生像素解码。

### GUI：XP3 归档发现、方案验证与导出

对于标准或未加密内容，优先使用首页的“一键解包”标签：选择游戏目录、独立 XP3 或完整游戏 ZIP，选择“全部/图片/音频/脚本/其他”以及全新的导出目录，再点击 **开始解包**。ZIP 会作为只读虚拟游戏目录处理，程序只会按需临时暂存内部 XP3；需要密码、损坏、路径逃逸或不受支持的压缩包会被拒绝并说明原因。未知或已标记加密条目会保留跳过原因，而不会伪称解密成功。

从 `0.1.0-preview.19` 起，内置的已验证静态 Cx 配置会先在**当前输入**中用至少两条已标记条目的索引校验值交叉证明后才启用；它不会按游戏目录名或作品名猜测。支持的 XP3 v3 `hnfn` 名称表会恢复原始资源路径；如同一归档含有多个相同原始路径，首项保留原名，之后的文件会得到稳定的 `__duplicate-002`、`__duplicate-003` 等后缀。若名称表、过滤器或验证无法可靠成立，结果会明确显示跳过或需要兼容配置，而不是生成看似成功的乱码名称。

若必须回退到游戏自身运行时，`0.1.0-preview.21` 会保留可启动启动器的优先级；`VERSION.dll` 导入与保护段特征仅供诊断，不会取代实际安装环境中的启动兼容性。任务会在启动器退出后继续观察其子进程，直到捕获完成或整条启动链结束。结果会显示实际选择的 EXE、观察到的启动链，以及枚举数、代理声明的捕获数和落盘文件数，便于区分启动器链、代理路径与写入阶段的问题。

完成后可直接点击 **打开导出目录**。结果还会显示按内容签名识别到的格式、通过结构验证的数量、尚无验证器或超过验证大小上限的数量，以及“路径扩展名类别与实际内容不一致”的条目；这些差异是审查线索，不会改写原始导出内容。

如果随程序提供的受信任知识库中存在与游戏目录、独立 XP3 或完整游戏 ZIP 内相关文件 **精确 SHA-256** 匹配的已验证配置，界面会自动应用它，并在结果中记录配置 ID、修订版和命中的输入指纹；不会要求你选择 scheme JSON。ZIP 只会按需临时处理包内 XP3、EXE 或 DLL 用于指纹计算。弱匹配、未验证配置或多个方案并列时，程序不会自动选择。

切换到“XP3 归档与方案”标签，按以下顺序操作：

1. 点击 **选择游戏目录…** 以只读发现其中的 `.xp3`，或点击 **选择 XP3 文件…**。
2. 选择归档后点击 **读取所选归档索引**。列表会区分“已标记加密”和“未标记加密”的条目；这只是归档标志，并不自动推断某个游戏的算法。
3. 若要处理已标记加密条目，先选择一个较小的加密条目，再点击 **选择方案…** 指定已有的 scheme JSON，随后点击 **验证所选方案**。只有输出达到 `FormatValidated`，界面才会允许将这个方案用于全量导出。
4. 选择游戏目录之外的父目录，界面会生成一个尚不存在的导出目录名；点击 **导出全部条目**。没有方案时只会导出未标记加密条目，其他条目会明确记录为跳过。

该向导不寻找密钥、不根据文件名自动选择方案，也不把“容器识别”或启发式观察称为解密成功。若没有能通过完整格式验证的、来源明确的方案，请保留索引、样本哈希和诊断，改用 CLI 的静态分析与知识库工作流继续研究。

## 生成新文件

下面的命令只写入你指定的新输出位置。请不要把输出放进游戏目录或输入目录树。

```powershell
# 从未标记为加密的 XP3 中提取到一个全新目录
.\KiriScope.Cli.exe xp3 extract "D:\AuthorizedGame\data.xp3" "D:\KiriScopeOutput\data"

# 一键扫描游戏目录或完整游戏 ZIP，仅导出图片到全新目录
.\KiriScope.Cli.exe unpack "D:\AuthorizedGame" "D:\KiriScopeOutput\game-images" --category images
.\KiriScope.Cli.exe unpack "D:\AuthorizedGame\Game.zip" "D:\KiriScopeOutput\game-images-from-zip" --category images

# 高级自动化：使用一份受信任、哈希绑定的知识库；无需传入 scheme JSON
.\KiriScope.Cli.exe unpack "D:\AuthorizedGame" "D:\KiriScopeOutput\game" --knowledge-root "D:\KiriScopeKnowledge"

# 为未知变体创建只读研究包；可选的运行时报告必须是已通过其他显式授权流程创建的既有文件
.\KiriScope.Cli.exe research package "D:\AuthorizedGame" "D:\KiriScopeReports\game-research.json" `
  --knowledge-root "D:\KiriScopeKnowledge" `
  --runtime-evidence "D:\KiriScopeReports\runtime.json"

# 导出 PIMG/PSB 的所有可验证根资源
.\KiriScope.Cli.exe psb extract-all "D:\AuthorizedGame\scene.pimg" "D:\KiriScopeOutput\scene"

# 转换标准、未加密 TLG5；输出 PNG 必须尚不存在
.\KiriScope.Cli.exe convert tlg5-to-png "D:\AuthorizedGame\image.tlg" "D:\KiriScopeOutput\image.png"
```

对作品专用的过滤方案，使用已记录来源和版本的 scheme JSON；不要把推测出的密钥或未验证参数当作兼容性结论：

```powershell
.\KiriScope.Cli.exe xp3 extract "D:\AuthorizedGame\data.xp3" "D:\KiriScopeOutput\filtered" `
  --scheme ".\plugins\schemes\reference-repeating-xor.scheme.json"
```

## 静态分析与运行时证据

静态分析不会执行输入二进制：

```powershell
.\KiriScope.Cli.exe analyze pe "D:\AuthorizedGame\Game.exe"
.\KiriScope.Cli.exe analyze archive "D:\AuthorizedGame\Game.exe" "D:\KiriScopeReports\game-static.json"
```

运行时采集默认关闭，仅采集你拥有或获授权进程的进程/模块元数据；它不注入、不读内存、不暂停或结束目标：

```powershell
.\KiriScope.Cli.exe analyze runtime inspect 1234
.\KiriScope.Cli.exe analyze runtime snapshot 1234 "D:\KiriScopeReports\runtime.json" --enable-runtime-capture
```

如需将静态分析、XP3 摘要、知识库候选和已有运行时报告关联为一次可复现研究，可使用上方的 `research package`。该命令不会触发运行时采集；`--runtime-evidence` 只会写入你明确提供的既有报告的路径、大小和 SHA-256。研究包会移除静态分析中的原始二进制字符串，避免把可打印内容复制进可共享的 JSON。

GUI 中也可切换到 **高级研究** 页：选择已授权的游戏目录和一个全新的报告路径；如有需要，再关联由既有显式授权流程生成的运行时报告。该页不会启动游戏或运行时采集。

如需文件访问线索，请自行用受信任工具导出 ProcMon CSV，再离线导入。KiriScope 不会自动启动 ProcMon 或加载其驱动。详见[运行时证据采集](../runtime/RUNTIME_EVIDENCE.md)。

## 下一步

- [PSB/PIMG 结构验证与导出](../formats/PSB_PIMG.md)
- [XP3 重打包的范围与限制](../formats/XP3_PACKING.md)
- [内容过滤器与方案验证](../filters/CX_ENCRYPTION.md)
- [知识库与批量只读扫描](../knowledge/KNOWLEDGE_BASE.md)
- [Windows 便携版、安装包和签名验证](../development/PACKAGING.md)

请保留输入哈希、工具版本、方案文件和输出 JSON 报告。它们比文件名更适合作为可复现研究结论的依据。
