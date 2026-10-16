AI Conversation Manager / AI 对话管理器
正式版 2.0.0 · Codex / DeepSeek Harness · Windows x64

【免安装版】
完整解压后运行 Start.cmd 或 conversation-manager-desktop.exe。
请保留同目录 engine 文件夹。Node.js 和依赖已随包附带，无需开发工具链。

【安装版】
运行 Setup.exe，支持中英文、当前用户安装和 Windows 应用卸载。
需要 Windows 10/11 x64、.NET Framework 4.8、WebView2。
安装版可下载缺失的 WebView2；免安装版需要已有 WebView2。

【升级】
2.0.0 全面升级桌面架构：React / TypeScript + Tauri / Rust，
复用 C# / Node 存储引擎。支持连续阅读、完整数字导航、悬停摘录、
持久预览缓存，以及 Codex 压缩历史预览、备份与迁移。
从旧版升级请安装或解压到新目录；旧 WPF 版本不会自动卸载。
不要让两个版本同时修改同一份数据。导入、删除和恢复前退出对应客户端。

【数据】
本包不包含个人会话或测试数据。备份、回收站与缓存未加密。
“设置与维护”可切换语言或指定数据目录。
免安装不表示所有设置与缓存都写入解压目录。
完整兼容边界与说明：
https://github.com/HikiTanis/ai-conversation-manager

Stable 2.0.0: extract the entire folder and run Start.cmd, or use the installer.
Keep engine/ beside the executable. Node.js is bundled.
Requires Windows 10/11 x64, .NET Framework 4.8 and WebView2.
Exit the corresponding client before writing. Existing backup formats remain supported.

Licenses:
LICENSE.txt — application
engine/THIRD-PARTY-NOTICES.txt — engine dependencies
engine/runtime/LICENSE-node.txt — Node.js and bundled components
