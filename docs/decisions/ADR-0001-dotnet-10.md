# ADR-0001：采用 .NET 10 LTS

- 状态：已接受
- 日期：2026-08-09

## 背景

KiriScope 是 Windows 专用桌面工具，需要一套同时支持 WPF、CLI、异步大文件 I/O、测试和后续 x86/x64 辅助进程的长期运行时。早期计划曾写为 .NET 8，但本机已安装 .NET 10 SDK 与 Visual Studio Community 2026。

## 决定

核心库、CLI、测试和 WPF GUI 统一使用 C# / .NET 10。GUI 目标框架为 \`net10.0-windows\`；其余托管项目为 \`net10.0\`。

## 理由

- .NET 10 是当前 LTS，并与已安装的 Visual Studio 2026 工具链匹配。
- Core、IO、XP3 仍保持不依赖 WPF，便于 CLI、测试和未来辅助进程复用。
- 不引入跨平台 UI 抽象，因为当前产品边界明确为 Windows。

## 后果

- 项目使用 \`.slnx\` 解决方案格式，这是当前 .NET SDK 生成的默认格式。
- 所有新增代码必须在 Release 配置下零警告构建。
- 若未来引入仅支持旧框架的第三方库，应使用独立适配层，不降低核心目标框架。
