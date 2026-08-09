# 开发说明

## 前置条件

- .NET 10 SDK
- Visual Studio Community 2026，包含 .NET 桌面开发与 C++ 桌面开发工作负载

## 常用命令

\`\`\`powershell
dotnet restore .\KiriScope.slnx
dotnet build .\KiriScope.slnx -c Release
dotnet test .\KiriScope.slnx -c Release
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- version
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- verify <资源文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 list <XP3文件路径>
dotnet run --project .\src\KiriScope.Cli\KiriScope.Cli.csproj -- xp3 extract <XP3文件路径> <输出目录>
\`\`\`

## 质量门槛

- Release 构建为零警告、零错误。
- 合并任何格式解析或过滤器变更前必须增加最小回归测试。
- 不允许测试或开发命令向游戏输入目录写入数据。
