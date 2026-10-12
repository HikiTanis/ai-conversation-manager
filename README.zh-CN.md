# Codex Conversation Manager

**一个 Windows 工具，统一管理 Codex 与 DeepSeek Harness（DSH）的对话和项目。**

[English](README.md) · 当前版本：**v1.1.0** · [下载最新版](https://github.com/HikiTanis/codex-conversation-manager/releases/tag/v1.1.0)

在原版界面右上方切换 **Codex / DSH**，项目、对话、预览、回收站及备份导入自动使用对应数据源。程序记住上次选择；切换时清空勾选、搜索和待导入路径，操作进行中禁止切换。

本项目为非官方社区工具，与 OpenAI、DeepSeek 无关联，也未获得其背书。

## 支持什么

| 功能 | Codex | DSH |
| --- | --- | --- |
| 项目分组、路径、大小、搜索、主对话与子代理分类 | 支持 | 支持 |
| 全部／未归档／已归档筛选 | 支持 | 支持 |
| 批量归档、取消归档 | 请在 Codex 中操作 | 支持，使用原生归档状态 |
| 消息预览与用户消息导航 | 未压缩 JSONL，完整历史查看 | session v4 JSONL、多帧 Zstandard；上限见下文 |
| 批量删除、回收站、恢复、永久删除 | 沿用官方删除接口适配 | 离线操作本地会话与工作区 |
| 删除父对话时包含关联子代理 | 支持 | 支持 |
| 同时处理项目目录 | Windows 回收站或永久删除 | 同一操作流程 |
| 仅对话备份 | `.codexchat` | `.dshchat` |
| 项目＋对话备份 | `.codexproject` | `.dshproject` |
| 导入前检查、目录映射、同源合并、独立复制 | 支持 | 支持；历史分叉时拒绝合并 |
| 旧版备份导入 | `.codexpack`、`.codexbundle` | 不支持 |
| Codex 侧边栏残留检测和修复 | 支持 | 不适用 |
| 恢复 DSH 中断操作 | 不适用 | 支持 |

中英文共用原版布局；DSH 的部分操作提示和诊断信息仍为中文。切换数据源不等于跨平台转换，Codex 与 DSH 的备份包不能互相导入。

## 下载与启动

1. 从上方 Release 下载 `CodexConversationManager-Windows-v1.1.0.zip` 和 `SHA256SUMS.txt`。
2. 运行 `(Get-FileHash .\CodexConversationManager-Windows-v1.1.0.zip -Algorithm SHA256).Hash`，与校验文件比较。
3. 将 ZIP 完整解压到新目录，保留 EXE、XAML、`assets`、`backend.cjs` 和 `packages.cjs`，不要混用不同版本文件。
4. 双击 `Start.cmd` 或 `CodexConversationManager.exe`，选择 **Codex** 或 **DSH**。

便携版无需安装，没有遥测、云同步或自动更新。目前未进行 Authenticode 签名，Windows 可能显示未知发布者提示。

### 运行环境

- 64 位 Windows 10/11、.NET Framework 4.8 Runtime。
- **Codex：** 查看、备份和导入使用内置引擎；删除对话及修复侧边栏需要 **Codex CLI 0.148.0 或更高版本**。这是本项目的兼容基线，程序也会查找 Desktop／VS Code 扩展自带的兼容运行时。
- **DSH：** 需要支持 Zstandard 的 Node.js 22.15+ 或 24+。程序查找 DSH 自带运行时、EXE 旁的 `runtime/node.exe` 以及 PATH。发布 ZIP 不包含 Node.js。
- Codex 使用 `CODEX_HOME` 或默认目录。DSH 默认读取 `%USERPROFILE%/.dsh`，可通过“数据目录”或 `DSH_MANAGER_HOME` 指定。

导入、删除、恢复前完全退出对应客户端；修改 DSH 归档状态也需退出 DSH。操作完成前不要重新启动客户端。

## 常见操作

- **查看与清理：** 选择项目、主对话或子代理，再按归档状态筛选、预览、勾选和删除。删除父对话会包含关联后代；软件回收站用于恢复误删，移入回收站不会释放备份仍占用的空间。
- **项目改名或移动：** 先创建仅对话备份，移动项目后把备份导入新位置。先检查，再导入。同源合并仅在目标项目内匹配；独立复制创建新编号和独立文件。
- **项目与对话一起迁移：** 选择“项目＋对话”，勾选项目备份。在目标电脑选择还原目录及文件冲突策略，检查通过后导入。
- **DSH 归档：** 勾选当前显示的会话，点击“归档所选”或“取消归档”。归档取消置顶，但保留工作区顺序；取消归档不会恢复旧的置顶状态。

仅对话包包含跨项目勾选的主对话；项目包包含项目文件及全部关联主对话和子代理。删除源会话不会删除已创建的正式备份包。

## 兼容范围与限制

- Codex `.jsonl.zst` 仅显示索引信息，不支持内容预览、正式备份或导入。未压缩的 legacy／paginated 历史沿用原有完整性检查。
- DSH 适配依据为 Desktop **0.2.0-rc.2**、workspace v2、session v4。未知格式、重复编号、链接路径、扫描不完整时拒绝写入。自定义 session/storage root 与第三方插件状态不在支持范围。
- DSH 日志压缩文件及解压内容上限各 128 MiB，预览最多 200 万字符。删除会话不会擦除共享附件、模型日志或插件存储。
- DSH 已有合成数据写入、回滚测试和本机只读验证；尚未对真实会话写入及客户端重启、续聊完成端到端验证。请在目标客户端打开验证后再处理源备份。
- 项目包保留普通文件、空目录和修改时间，不迁移目录联接、符号链接、ACL 或备用数据流。可要求目标为空、保留现有文件或覆盖前备份。
- 会话导入有回滚记录；如果项目文件已还原、后续会话导入失败，不会完整自动撤销已还原项目文件。请保留错误信息中的目录和覆盖前备份。
- 备份、回收站和恢复文件未加密，可能包含提示词、密钥和项目源码。不要把真实数据发到公开 Issue。

## 项目结构与开发

```text
src/CodexConversationManager/   共用 WPF 界面和原 Codex 引擎
  Providers/                   数据源选择及操作分发
    Dsh/                       DSH 窗口、进程和备份适配
adapters/dsh/                  DSH 原生存储与备份 Node 后端
tests/ui/                      虚构数据的界面／切换回归
tests/dsh/                     存储、归档、回滚测试
scripts/                       构建、测试、打包、发布校验
packaging/                     发布包启动入口
docs/                          格式、隐私、架构及版本说明
```

源码构建需要 Windows、.NET SDK 8.x、Node.js 24、PowerShell 5.1+；.NET Framework 引用程序集通过 NuGet 恢复。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -NoBuild
.\scripts\test-providers.ps1 -NoBuild
.\scripts\package.ps1
```

源码目录的 `Start.cmd` 运行 Release 构建，缺失时自动构建；发布包使用独立便携入口。`.tools`、`artifacts`、编译产物和生成的发布附件均不提交。此前独立 DSH 样机不再作为第二套产品发布。

详细说明位于源码仓库的 `docs/ARCHITECTURE.md`、`docs/BACKUP_FORMATS.md`、`docs/PRIVACY.md`、`docs/RELEASING.md`、`docs/releases/v1.1.0.md` 和 `CHANGELOG.md`。

采用 [MIT 许可证](LICENSE)。
