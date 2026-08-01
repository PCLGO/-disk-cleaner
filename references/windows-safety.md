# Windows 安全边界

在任何实际清理前读取本文件。扫描命中只是候选，不是删除许可。

## 永不直接删除

- `C:\Windows\System32`
- `C:\Windows\WinSxS`
- `C:\Windows\Installer`
- `C:\Program Files`
- `C:\Program Files (x86)`
- EFI、恢复分区、System Volume Information
- 用户的 Desktop、Documents、Downloads、Pictures、Music、Videos、OneDrive 等数据目录中的未知内容
- Git 仓库中的源文件和未提交内容
- 备份、系统映像、虚拟磁盘、数据库文件、密钥、凭据和模型权重

其他盘符上名称相似的目录也不能因为不在 C 盘就降低风险。

## 优先使用官方清理入口

- Windows 临时文件、Windows Update、Delivery Optimization：Windows 设置中的“系统 > 存储 > 临时文件”或“存储感知”。
- Microsoft Store、游戏平台、浏览器：应用自带的缓存管理或修复功能。
- npm、pip、NuGet、Gradle、Cargo：在确认重建成本后使用各自的 cache clean/prune 命令。
- 回收站：由用户确认后通过 Windows 界面清空。

## 执行前检查

1. 报告生成时间仍然足够新。
2. 用户按候选 ID 或精确路径确认了本批目标。
3. 重新解析后的绝对路径仍在预期盘符。
4. 目标不是符号链接、junction、挂载点或保护路径的父目录。
5. 目标应用与后台进程已关闭。
6. 对不可重建内容有可验证备份。
7. 优先移入回收站；永久删除必须单独说明并再次确认。

## 风险解释

- `low`：高度可能可重建，但可能造成首次启动变慢或重新登录。
- `medium`：可能可重建或可归档，但清理会带来下载、编译、诊断信息丢失等成本。
- `high`：内容价值未知或涉及重复副本选择，必须由用户逐项判断。
