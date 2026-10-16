# AI Conversation Manager 2.0.0 · 桌面开发

本目录是正式桌面应用：React + TypeScript 界面、Tauri + Rust 宿主。用户文档见 [中文首页](../README.md)，技术架构见 [架构说明](../docs/ARCHITECTURE.md)。

## 开发环境

Windows x64、Node.js 24、.NET SDK 8、Rust stable MSVC、Microsoft C++ Build Tools（Desktop development with C++）。运行程序另需 .NET Framework 4.8 和 WebView2。

在仓库根目录运行：

```powershell
# 构建界面、引擎与桌面程序，运行前端／桥接／Rust 测试
.\desktop\scripts\build.ps1

# 完整正式发行检查，含中英文引擎自检、DSH 回归、安装与便携打包
.\scripts\package.ps1 -SkipUiTests
```

无交互桌面的 CI 跳过旧 WPF 窗口截图测试。需要验证保留的 WPF 窗口时可省略 SkipUiTests。桌面可运行目录在 `artifacts/desktop/`，发行文件在 `release/v2.0.0/`。仓库根 `VERSION` 与 npm、Cargo、Tauri 版本必须一致。

在 desktop 目录开发：

```powershell
npm ci
npm run prepare:engine
npm run desktop:dev
```

浏览器演示：`npm run dev`，使用明确标记的合成数据，不读取真实本机对话。

## 验证命令

```powershell
npm run build
npm test
npm run test:bridge
cargo test --manifest-path src-tauri/Cargo.toml --locked
```

`test:bridge` 使用隔离合成目录，验证 Codex／DSH 备份、检查、导入、合并、独立副本、删除、恢复，以及 Zstandard 和缓存失效。不向仓库放入真实数据。

## 模块

- `src/App.tsx`：项目与对话工作区、Provider 状态、维护与预览入口。
- `src/ConversationView.tsx`：连续虚拟滚动、完整数字导航、提问／回答悬停摘录、页面工作集。
- `src/Operations.tsx`：备份、导入、删除、回收站与恢复流程。
- `src/components/`、`src/tokens.css`、`src/style.css`：统一组件与视觉变量。
- `src-tauri/src/main.rs`：类型化 IPC、只读／操作命令白名单、进程隔离及关闭保护。
- `../src/CodexConversationManager/Providers/DesktopReadBridge.cs`：目录、预览、统计读取。
- `DesktopPreviewCache.cs`：源指纹、消息偏移、导航与跨进程缓存。
- `DesktopOperations.cs`／`DesktopPackages.cs`：复用 C# 及 Node 存储与事务。

读取缓存时只接受数据根内的既有源路径，并结合 Provider、项目、会话及文件指纹查找；未命中时重新通过目录定位。源变化后不混用不同快照。20 条为传输页大小，正文按需自动加载，单条消息超过 100,000 字符时标记截断。

## 打包

`desktop/scripts/package.ps1` 生成 NSIS 安装包、便携 ZIP 与 SHA-256 文件，附带 Node.js、C# 引擎依赖及许可证。已有最新正式构建可传 `-SkipBuild`；可用 `-NodePath` 指定支持 Zstandard 的 x64 Node。

默认正式入口为根目录 `scripts/package.ps1`，它还运行发布验证。不得把 `engine/`、`target/`、`dist/` 或真实缓存提交到 Git。

旧 UI 的逐项映射见 [核对表](../docs/DESKTOP-PARITY-AUDIT.md)。仍保留的功能边界见中文首页；目前没有跨平台包，存储层也未全部改为 Rust。
