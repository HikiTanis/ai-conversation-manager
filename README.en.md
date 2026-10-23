# AI Conversation Manager

**A Windows desktop application for Codex and DeepSeek Harness (DSH) conversations, projects and backups.**

[简体中文](README.md) · **Stable v2.0.1** · [Download](https://github.com/HikiTanis/ai-conversation-manager/releases/tag/v2.0.1)

Version 2.0.0 is a full desktop architecture upgrade: React + TypeScript UI, a Tauri + Rust host, and separate read/operation bridges to the retained C# and Node storage engines. The product replaces the Codex Conversation Manager name; the repository is now named ai-conversation-manager. This is an independent community project, unaffiliated with OpenAI or DeepSeek.

> **v2.0.1 is a data-safety fix; upgrading from earlier versions is recommended.** Sidebar checks are read-only, with additional import, trash and DSH safeguards. See [release notes](docs/releases/v2.0.1.md) and [audit](docs/DATA-SAFETY-AUDIT.md).

## Downloads

- `AI-Conversation-Manager-2.0.1-Windows-x64-Setup.exe`: per-user installer.
- `AI-Conversation-Manager-2.0.1-Windows-x64-Portable.zip`: extract the entire folder and run `Start.cmd`.
- `SHA256SUMS.txt`: verify both downloads with `Get-FileHash -Algorithm SHA256`.

Requires Windows 10/11 x64, .NET Framework 4.8 and WebView2. The installer downloads WebView2 if missing. Node.js and its license are bundled; retain the whole `engine/` directory. No development toolchain is needed to run the application. Binaries are not Authenticode-signed.

Install or extract into a new directory when upgrading from 1.x. Existing data and backup formats remain compatible. The old WPF app is not automatically uninstalled; do not run simultaneous writes from both versions.

## What's new

- Rebuilt desktop architecture and a shared Codex/DSH workspace.
- Continuous virtualized conversation reading with automatic page loading.
- Full-history numbered question navigation; hover/focus previews show question and answer excerpts.
- Persistent parsed-message cache avoids repeated scans and decompression across reads and app launches; changed or deleted source files invalidate it.
- Codex `.jsonl.zst` preview, complete decoded backups, compressed imports, lineage remapping and transactional merges.
- Cross-project selection, package inspection, mapping, merge/copy, project-file restoration, reviewed deletion, trash and recovery.
- Stable installer and portable distributions with runtime dependencies, notices and SHA-256 checksums.

## Compatibility

Codex uses `.codexchat` / `.codexproject`; DSH uses `.dshchat` / `.dshproject`. Legacy Codex `.codexpack` / `.codexbundle` imports remain supported. Packages cannot be cross-imported between providers. Codex archive changes still use the Codex client; DSH archive changes are available here.

Codex deletion requires **Codex CLI 0.148.0 or later**. Codex rollout decoding is limited to 8 GiB; identity rewriting retains a 512 MiB per-entry limit. New imports use JSONL; existing compressed destinations stay compressed. Backups preserve full decoded content.

DSH targets Desktop 0.2.0-rc.2, workspace v2 / session v4, with 128 MiB log limits and a 2-million-character preview limit. Real-session writes followed by client restart/resumption have not been verified end to end; automated write tests use synthetic data.

Desktop previews truncate individual messages above 100,000 characters with a visible label. This does not truncate backups. Cached snapshots expire after 24 hours and use an approximately 512 MiB cleanup budget; the current snapshot can exceed it.

Exit the corresponding client before imports, deletion or recovery, and exit DSH before archive changes. Initialize destinations with their client first. Keep source backups until imported conversations open successfully. Conversation rollback does not fully roll back project files already restored; failures report those files and overwrite backups. Junctions, symlinks, ACLs and alternate data streams are not preserved.

This release is Windows-only and retains the C#/Node engines. Backups, trash and caches contain unencrypted local content; never attach real histories or secrets to public issues.

## Development

Windows x64, Node.js 24, .NET SDK 8, stable MSVC Rust and Microsoft C++ Build Tools are required.

```powershell
.\scripts\package.ps1 -SkipUiTests
```

Outputs: `release/v2.0.0/`. See [desktop development](desktop/README.md), [architecture](docs/ARCHITECTURE.md), [release notes](docs/releases/v2.0.0.md), [contributing](.github/CONTRIBUTING.md), [MIT license](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.txt).
