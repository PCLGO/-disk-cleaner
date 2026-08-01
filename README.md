# Disk Cleaner Skill

[![Version](https://img.shields.io/badge/version-0.1.0--beta-blue.svg)](./CHANGELOG.md)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](./LICENSE)

> ⚠️ **免责声明（请先阅读）**：本项目主要由 AI 辅助编写，尚未经过完整的人工安全审计。请在充分理解源码、候选项身份和删除影响后使用；重要数据应另行备份。

## Windows 桌面软件：磁盘清理助手

仓库现已包含可独立运行的 C# WPF 桌面软件，产品名固定为“磁盘清理助手”。它基于 `.NET Framework 4.8`，扫描、规则判断和执行全部在本机完成，不包含在线 AI 接口，也不收集遥测。默认所有候选均不勾选，普通清理进入回收站；永久删除与清空回收站必须连续确认两次。

本机构建：

```powershell
.\build.ps1 -Configuration Release
```

生成的便携成品位于 `artifacts\DiskCleanupAssistant.exe`，旁边会生成 SHA-256 文件。完整功能、安全边界、构建与签名发布说明见 [`docs/APP.md`](docs/APP.md)。GitHub Actions 的普通 CI 产物明确标记为未签名、仅供测试；公开 Release 缺少可信代码签名证书或独立更新清单私钥时会直接失败。

面向 Codex 的 Windows 磁盘空间盘点 skill。它快速扫描 C 盘、D 盘或指定目录，找出缓存、临时文件、崩溃转储、旧日志、开发环境缓存、疑似遗留安装包，以及可选的内容完全相同的大文件。

核心原则是：**先生成只读候选报告，再由用户决定是否清理。**

## 能力

- 同时扫描 `C:`、`D:`，也支持只检查指定盘符或绝对目录。
- 先检查常见缓存位置，再进行有时间上限的广度扫描。
- 对候选项给出稳定 ID、风险等级、大小估算和扫描完整性。
- 在询问用户前说明候选究竟或疑似是什么、判断依据、删除影响、置信度和建议动作。
- 可选 SHA-256 重复文件检测，不以文件名相同冒充内容重复。
- 默认不删除、不移动、不修改被扫描盘中的任何内容。
- 标准 Codex skill 结构，可安装到 `C:\Users\<用户名>\.agents\skills\disk-cleaner`。

## 快速使用

在 Codex 中可以直接说：

```text
帮我快速检查 C 盘和 D 盘有哪些缓存、临时文件和疑似冗余，先给清单，不要删除。
```

也可以单独运行扫描器：

```powershell
python .\scripts\scan_candidates.py C: D: `
  --max-seconds 45 `
  --min-size-mb 50 `
  --output .\reports\disk-cleaner-report.json `
  --csv .\reports\disk-cleaner-report.csv
```

需要检查重复大文件时：

```powershell
python .\scripts\scan_candidates.py D: `
  --duplicates `
  --max-seconds 90 `
  --output .\reports\d-duplicates.json
```

扫描命令只会写入你指定的报告文件。它没有删除参数。

## 风险等级

- `low`：通常可重建的缓存或临时数据，仍需先关闭相关应用。
- `medium`：可能影响诊断，或需要重新下载、重新编译。
- `high`：用途未知、可能是备份/安装包，或需要决定保留哪个重复副本。

“潜在可释放空间”是候选项大小的估算，不代表一定应该删除，也不保证实际能释放相同空间。

仅凭路径名出现 `cache`、`temp`、`logs` 等字样并不足以授权删除。Codex 应先检查路径归属和必要的只读元数据；身份仍不清楚时继续调查，并明确标注推测，直到用户获得足够把握。

## 项目结构

```text
disk-cleaner/
├── app/                       # 磁盘清理助手 WPF 产品代码
├── app-tests/                 # 无第三方测试运行时的安全规则测试
├── DiskCleanupAssistant.sln
├── build.ps1                  # 构建、测试、单 EXE 打包及 SHA-256
├── SKILL.md
├── scripts/
│   └── scan_candidates.py
├── references/
│   └── windows-safety.md
├── evals/
│   └── evals.json
└── tests/
    └── test_scan_candidates.py
```

## 验证

```powershell
python -m unittest discover -s tests -p "test_*.py" -v
python -m py_compile scripts\scan_candidates.py
```

## 安全边界

实际清理前必须阅读 `references/windows-safety.md`。系统核心目录、程序安装目录、用户资料库、代码仓库、备份、虚拟磁盘和同步盘未知内容都不应直接删除。Windows Update、浏览器、游戏平台与包管理器缓存优先使用官方清理入口。

## License

MIT
