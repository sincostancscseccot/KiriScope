# 松散文件覆盖计划

`overlay plan` 比较一个参考目录和一个候选覆盖目录的相对路径、长度与 SHA-256，并把结果写成新的 JSON 报告。

```powershell
kiriscope overlay plan <reference-directory> <override-directory> <new-report.json>
```

每个覆盖文件会标记为：

- `Added`：参考目录没有同路径文件；
- `Replaced`：同路径文件存在但 SHA-256 不同；
- `Identical`：同路径文件内容相同；
- `Conflict`：同一路径在参考目录中是目录，不能作为安全的文件替换目标。

该命令只读两个输入目录。报告也必须位于两者之外，使用临时文件和无覆盖移动落盘，记录逐文件哈希和等价复现命令。

它不复制、部署、删除或修改任何文件，也不会声称目标 KiriKiri 构建支持松散文件覆盖。实际加载优先级应先通过用户自有或已授权的副本，在隔离目录中手动验证；成功后再将观察和版本哈希登记为研究/知识库证据。
