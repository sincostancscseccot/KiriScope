# 运行时证据采集

阶段 5 的运行时组件默认关闭。它只用于用户拥有或获得授权的本地进程，并且首个版本仅收集进程与模块元数据；它不会启动、暂停、终止、注入目标进程，不读取目标进程内存，也不会自动启动 ProcMon、System Informer 或其驱动。

## 明确启用的进程快照

```powershell
kiriscope analyze runtime inspect <pid>
kiriscope analyze runtime snapshot <pid> <new-report.json> --enable-runtime-capture
```

`inspect` 只查询并显示 PID 架构与后续动作，绝不启动 worker。`--enable-runtime-capture` 缺失时，`snapshot` 以退出码 2 拒绝执行，不创建归档，也不启动 worker。启用后控制端先以查询权限核实 PID 架构，然后选择随 CLI 部署的 x86 或 x64 worker。控制端与 worker 使用版本化、单消息 JSON 标准输入/输出协议；每个请求有唯一 ID，控制端会验证响应的请求 ID、目标 PID 和 worker 架构。

归档以新文件方式创建，并包含：目标 PID、名称、会话、启动/观察时点、进程映像和每个已读取模块的路径、长度、SHA-256、基址和映像大小；worker 二进制的绝对路径、期望架构、长度、SHA-256；以及超时、进程输出和诊断。默认 worker 超时为两分钟；超时或 worker 崩溃会结束 worker 进程树并仍写入失败归档。输入和目标进程不会被修改。

GUI 也提供同样的入口：必须填写 PID、勾选授权确认并选择一个不存在的 JSON 文件后，按钮才会启动隔离 worker。GUI 直接使用运行时服务，不通过模拟 CLI 运行。

## 离线文件访问证据

KiriScope 不自动调用 ProcMon。用户可自行使用受信任工具手动观察、导出 CSV，再显式导入：

```powershell
kiriscope analyze runtime import-procmon <pid> <procmon.csv> <new-report.json> --enable-runtime-capture
kiriscope analyze runtime compare-procmon <pid> <before.csv> <after.csv> <new-report.json> --enable-runtime-capture
```

导入器读取常见 ProcMon CSV 列 `Time of Day`、`Process Name`、`PID`、`Operation`、`Path`、`Result` 和 `Detail`，但只保留指定 PID 的文件系统操作（如 `CreateFile`、`ReadFile`、`QueryDirectory`）。它记录源 CSV 的绝对路径、长度、SHA-256、列映射和原始行号；不复制或写回 CSV，也不支持直接读取 `.PML`。缺列、坏 PID 或超过上限的事件会成为明确诊断。

`compare-procmon` 只比较两个已导入 CSV 的操作/路径/结果计数并保存两侧源哈希，不会重放、启动或附加任何进程。

## 从运行时观察回归为离线验证

运行时记录是 `ObservedFact` 的来源，不是算法、密钥或作品兼容性的结论。将发现提升为候选时，必须保留对应归档和输入哈希，并完成以下闭环：

1. 在合法样本上记录资源路径、版本、时间窗和运行时/静态证据。
2. 将候选参数写入带来源说明的方案 JSON，而不是硬编码进 XP3 核心。
3. 使用 `kiriscope filter score` 对照明密文并通过完整格式验证。
4. 仅把通过独立离线回归测试的结果加入方案库候选；仍须限定适用版本和样本。

完整商业资源、进程转储、内存捕获、密钥材料或用户导出的 PML/CSV 都不得加入仓库。
