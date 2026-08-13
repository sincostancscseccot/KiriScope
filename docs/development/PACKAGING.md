# Windows 发布：便携包与安装包

KiriScope 的正式 Windows 发布包含同一份自包含的 x64 GUI/CLI、x64 worker 和 x86 worker：

- 便携包：ZIP，解压后运行 `KiriScope.Gui.exe` 或 `KiriScope.Cli.exe`；
- 安装包：Inno Setup EXE，安装到当前用户的程序文件目录，可选桌面快捷方式。

发布脚本不会覆盖既有版本目录。先选择一个新的语义化版本号：

```powershell
.\packaging\Build-Release.ps1 -Version 0.1.0
```

输出位于 `artifacts\releases\<version>\`。脚本默认先运行全部测试，然后分别发布 GUI、CLI、x64 worker 与 x86 worker。它会验证四个启动文件存在，生成 ZIP，并检查 ZIP 中包含 GUI、CLI、双架构 worker 与知识库，同时拒绝把插件模板的 `bin/obj` 构建缓存带进发布包；随后调用 Inno Setup 编译安装包。每个版本目录都是新建目录；脚本拒绝覆盖已有版本。

构建完成后会额外生成 `KiriScope-<version>-release-manifest.json`。它记录 ZIP、安装包及四个 KiriScope 可执行文件的 SHA-256、大小和 Authenticode 状态。安装包语言包含英语、简体中文和日语；简体中文覆盖常规向导页面与 KiriScope 专用文字，少见的安装引擎诊断会安全回退到 Inno Setup 的英文默认文字。

默认从下列位置发现 Inno Setup 6：

```text
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
C:\Program Files\Inno Setup 6\ISCC.exe
C:\Users\<user>\AppData\Local\Programs\Inno Setup 6\ISCC.exe
```

也可传入其他路径：

```powershell
.\packaging\Build-Release.ps1 -Version 0.1.0 -InnoSetupCompilerPath C:\tools\InnoSetup\ISCC.exe
```

## 单文件 GUI EXE

若只需要桌面 GUI，可创建一个可单独复制和运行的 x64 EXE：

```powershell
.\packaging\Build-GuiSingleFile.ps1 -Version 0.1.0-preview.25
```

脚本会在全新的 `artifacts\releases\<version>\` 目录中只留下 `KiriScope.Gui-<version>-win-x64.exe`。它是自包含的，不要求目标机器预先安装 .NET，也不包含 CLI。与普通发布不同，x86/x64 运行时 worker 已作为资源嵌入 GUI；仅当用户填写 PID、勾选授权并启动运行时采集时，GUI 才会将架构匹配的 worker 解压到当前用户的临时目录，校验 SHA-256 后使用。普通资源检查、提取和转换不会产生这些临时文件。

单文件 GUI 目前仅支持 `win-x64`。它会因为嵌入运行时与两个 worker 而明显大于普通 GUI 主程序；若需要 CLI、安装程序或可见的 worker 文件，应使用上面的完整便携包/安装包流程。

## 代码签名与验证

发布脚本不会创建、导出或提交私钥。若当前用户证书库 `Cert:\CurrentUser\My` 中已有带私钥的代码签名证书，可显式传入其 40 位指纹：

```powershell
.\packaging\Build-Release.ps1 `
  -Version 0.1.0 `
  -CodeSigningCertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567 `
  -TimestampServer http://timestamp.digicert.com
```

这会在打包前签署 GUI、CLI、x64/x86 worker，并在 Inno Setup 完成后签署安装包。未传入证书时，构建仍成功，但清单会将可执行文件的 Authenticode 状态记录为 `NotSigned`；ZIP 的状态是 `NotApplicable`。任何下载者都可在不信任发布者声明的前提下复核：

```powershell
Get-FileHash -LiteralPath .\KiriScope-Setup-<version>-win-x64.exe -Algorithm SHA256
Get-AuthenticodeSignature -FilePath .\KiriScope-Setup-<version>-win-x64.exe |
  Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

`Build-GuiSingleFile.ps1` 也接受相同的 `-CodeSigningCertificateThumbprint` 和 `-TimestampServer` 参数；其输出只有一个 EXE，因此可直接对该文件验证签名和 SHA-256。

发布前应在一台没有项目构建输出、但有 Windows x64 的机器或干净目录验证：GUI 能启动，`KiriScope.Cli.exe version` 能运行，且显式运行时采集能找到同包的架构匹配 worker。发布包不包含任何游戏、解密结果、进程转储或私有样本。
