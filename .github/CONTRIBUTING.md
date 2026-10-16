# Contributing

Use synthetic histories and keep changes focused. The default README is Chinese; update README.en.md and release notes when behavior changes.

## Development

Windows x64, Node.js 24, .NET SDK 8, Rust stable MSVC and Microsoft C++ Build Tools are required.

```powershell
.\scripts\package.ps1 -SkipUiTests
```

This restores dependencies, builds the React/Tauri desktop and compatibility engine, runs frontend, Rust, bridge, bilingual engine and DSH regressions, and verifies installer/portable artifacts under release/v<version>/. Omit SkipUiTests on an interactive Windows desktop when testing retained WPF windows and render behavior.

See desktop/README.md and docs/ARCHITECTURE.md. VERSION, npm manifests, Cargo manifest/lock and Tauri config must agree. Release tags must match VERSION.

## Contribution rules

- Keep frontend changes in desktop/src, native commands in desktop/src-tauri, and storage compatibility in the C#/Node engines.
- Use invented IDs and isolated temporary homes. Do not commit real conversations, bundles, SQLite databases, absolute personal paths, secrets or screenshots of private data.
- Include Chinese and English for user-facing text.
- Preserve path boundaries, deletion scope review, stale-scope checks, operation locks and rollback behavior.
- Keep Codex and DSH importers and formats isolated. Codex writes .codexchat/.codexproject and accepts legacy .codexpack/.codexbundle; DSH uses .dshchat/.dshproject.
- Preserve .jsonl.zst reading, complete decoded backup, lineage rewriting and byte-preserving rollback tests.
- Do not commit node_modules, engine outputs, target, dist, runtime caches or release binaries. Distribute binaries through GitHub Releases.
- Describe changed behavior and actual validation in the PR. Retain known compatibility limits unless supported by new evidence.

Contributions are licensed under the repository's MIT license.
