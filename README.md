# AI Conversation Manager · AI 对话管理器

**在一个 Windows 桌面应用里管理 Codex 与 DeepSeek Harness（DSH）的对话、项目和备份。**

[English](README.en.md) · **正式版 v2.0.0** · [下载安装包](https://github.com/HikiTanis/ai-conversation-manager/releases/tag/v2.0.0) · [完整更新说明](docs/releases/v2.0.0.md)

2.0.0 是桌面架构全面升级的正式版本：界面改用 **React + TypeScript**，桌面宿主改用 **Tauri + Rust**，通过独立读写桥接复用经过验证的 C#／Node 存储与事务引擎。新版统一了 Codex 与 DSH 的使用流程，增加压缩会话支持、连续阅读、完整数字导航与持久缓存。旧版名称 Codex Conversation Manager 更名为 AI Conversation Manager；仓库已更名为 ai-conversation-manager。

本项目是独立社区工具，与 OpenAI、DeepSeek 无隶属关系。

## 下载与运行

在 [v2.0.0 Releases](https://github.com/HikiTanis/ai-conversation-manager/releases/tag/v2.0.0) 下载：

| 文件 | 用途 |
| --- | --- |
| `AI-Conversation-Manager-2.0.0-Windows-x64-Setup.exe` | 正式安装包，中英文安装向导，按当前用户安装 |
| `AI-Conversation-Manager-2.0.0-Windows-x64-Portable.zip` | 免安装版，完整解压后运行 `Start.cmd` |
| `SHA256SUMS.txt` | 安装包与 ZIP 的 SHA-256 校验值 |

运行环境：**Windows 10/11 x64、.NET Framework 4.8、Microsoft Edge WebView2 Runtime**。安装包会在缺少 WebView2 时联网安装；免安装版使用系统已有的 WebView2。Node.js 运行时随包附带，普通使用不需要 Rust、C++、Node.js 或 .NET SDK。主程序旁的 `engine/` 必须保留。

程序尚未进行 Authenticode 签名，Windows 可能显示未知发布者提示。可通过 PowerShell 的 `Get-FileHash -Algorithm SHA256 文件路径` 核对下载文件。

从 1.x 升级：安装到新目录或完整解压新版，再选择原有 Codex／DSH 数据目录。无需转换原始会话或旧备份。新版不会自动卸载旧 WPF 程序；请避免两个版本同时修改同一份数据。

## 2.0.0 的主要更新

- **桌面架构全面升级**：React／TypeScript 负责界面，Tauri／Rust 负责桌面窗口、原生文件选择与受限命令，C#／Node 负责数据兼容与事务。
- **统一工作区**：项目侧栏、主对话／子代理、归档筛选、跨项目勾选、备份、导入、回收站和维护入口。
- **连续阅读长会话**：正文连续下滑，自动按需读取；虚拟列表只渲染附近消息，减少长会话的页面负担。
- **完整数字导航**：覆盖整个会话，点击可直接跳转；鼠标悬停或键盘聚焦显示提问与回答摘录。
- **持久预览缓存**：缓存解析后的消息和导航，翻页与再次打开时复用，避免重复扫描和解压；文件变化或删除后失效。
- **Codex Zstandard 支持**：读取 `.jsonl.zst`，支持预览、完整备份、导入、父子关系重写与向已有压缩会话合并。
- **正式安装与便携分发**：提供 NSIS 安装程序、便携 ZIP、运行时、第三方许可和校验文件。

## 支持哪些操作

| 功能 | Codex | DSH |
| --- | --- | --- |
| 项目分组、搜索、路径与大小统计 | 支持 | 支持 |
| 主对话／子代理、归档筛选 | 支持 | 支持 |
| 归档／取消归档 | 通过 Codex 客户端操作 | 支持 |
| JSONL／Zstandard 会话阅读 | 支持 | 支持多帧 Zstandard |
| 仅对话备份 | `.codexchat` | `.dshchat` |
| 项目与对话备份 | `.codexproject` | `.dshproject` |
| 导入检查、路径映射、智能合并、独立副本 | 支持 | 支持 |
| 删除范围预览、后代联动、回收站恢复与清除 | 支持 | 支持 |
| 旧格式导入 | `.codexpack`／`.codexbundle` | 不适用 |
| 侧栏残留修复／中断操作恢复 | Codex 侧栏修复 | DSH 中断恢复 |

两个来源各自管理，**不进行 Codex 与 DSH 格式互转**。导入目标需要先由对应客户端初始化。

## 常用流程

1. **查看与整理**：选择 Codex 或 DSH → 选择项目 → 筛选主对话／子代理与归档状态 → 打开阅读或勾选操作。
2. **迁移对话**：跨项目勾选主对话 → 创建仅对话备份 → 在目标位置检查备份 → 选择路径映射及合并／独立副本 → 导入。
3. **迁移整个项目**：创建完整项目备份 → 选择项目与文件 → 在目标位置配置文件冲突策略 → 检查后导入。
4. **删除与恢复**：查看实际删除范围及关联后代 → 确认执行 → 在软件回收站恢复或清除。项目永久删除还需要输入目录名。

执行导入、删除或恢复前请退出对应客户端，DSH 归档修改也需退出 DSH。操作完成前不要重新打开客户端。源备份应保留到目标客户端能够正常打开会话为止。

## 兼容范围与边界

- Codex 的读取、备份和导入使用内置引擎；删除与侧栏修复要求 **Codex CLI 0.148.0 或更高版本**，也会尝试发现兼容的客户端内置运行时。
- Codex 压缩会话备份保存完整解压 JSONL 与校验值。新导入使用 JSONL，合并到已有压缩目标时保持压缩。单条 rollout 解压上限 8 GiB，涉及 ID 重写的单项上限 512 MiB。
- DSH 适配基线为 Desktop 0.2.0-rc.2、workspace v2／session v4；单日志读取／解压上限 128 MiB，预览总字符上限 200 万。真实 DSH 会话写入后重启与继续对话尚未完成端到端验证；自动化使用合成数据。
- 桌面预览单条消息最多显示 100,000 字符，超过时明确标记截断；这不等于备份内容被截断。缓存保存于用户本地应用数据目录，24 小时过期，按约 512 MiB 预算清理（当前单个快照可能超过预算）。
- 目前只提供 Windows x64。架构升级不代表存储引擎已经全部改写成 Rust，也不代表已支持 macOS／Linux。
- 会话导入有回滚记录；项目文件恢复与会话导入不是一个完整原子事务。会话失败后，已经恢复的项目文件及覆盖备份会在错误信息中列出。
- 项目备份保留普通文件、空目录与修改时间，不保留符号链接、联接、ACL 或备用数据流。未知格式、路径歧义等会阻止写入。
- 备份、回收站与预览缓存可能包含对话正文和项目内容，未加密；请不要将真实数据提交到公共仓库或 Issue。

## 从源码构建

开发环境：Windows x64、Node.js 24、.NET SDK 8、Rust stable MSVC、Microsoft C++ Build Tools（Desktop development with C++）。

```powershell
# 完整构建、自动化测试与正式打包；无交互桌面的 CI 使用此参数
.\scripts\package.ps1 -SkipUiTests
```

产物在 `release/v2.0.0/`。开发运行、模块划分和独立验证命令见 [开发说明](desktop/README.md)、[架构说明](docs/ARCHITECTURE.md) 和 [贡献指南](.github/CONTRIBUTING.md)。

```text
desktop/                      React / TypeScript 界面与 Tauri / Rust 宿主
src/CodexConversationManager/  C# 兼容引擎、事务与保留的旧 WPF 源码
adapters/dsh/                 DSH Node 存储与备份适配器
tests/                        引擎、Provider 与旧界面回归测试
scripts/                      构建、测试、打包及版本验证
docs/                         架构、格式、隐私及版本记录
```

项目采用 [MIT 许可证](LICENSE)。运行时与依赖许可见 [第三方声明](THIRD-PARTY-NOTICES.txt)。
