# AI Conversation Manager 2.0 architecture

## Desktop and compatibility engines

The stable desktop UI uses React/TypeScript with Tauri/Rust. Typed IPC invokes separate C#/.NET Framework 4.8 read and operation bridges; the C# provider adapter invokes a local Node process for DSH. Original WPF sources remain for compatibility and regression coverage. This is a full desktop architecture upgrade, not an all-Rust storage rewrite or a cross-platform release.

```text
React / TypeScript
  -> Tauri / Rust (window, file picker, command validation, child processes)
    -> --desktop-read (catalog, statistics, cached preview)
    -> --desktop-operation (backup, import, deletion, trash, repair, recovery)
      -> C# Codex storage / transactions / official deletion protocol
      -> Node DSH workspace / sessions / packages
```

The frontend has no arbitrary command execution interface. Rust validates provider, action and identity, limits read output to 32 MiB and read duration to 90 seconds. Write operations are not killed by the read timeout; a cross-process operation lock and window-close guard protect active management work.

## Source layout

- `desktop/src/`: shared workspace, components, design tokens, operations and virtual reader.
- `desktop/src-tauri/`: native host and typed commands.
- `src/CodexConversationManager/`: Codex engine, package validation, lineage, transactions, deletion, repair and retained WPF source.
- `Providers/DesktopReadBridge.cs`, `DesktopPreviewCache.cs`, `DesktopOperations.cs`, `DesktopPackages.cs`: stable desktop boundary.
- `Providers/Dsh/`: DSH controller and process/package transport.
- `adapters/dsh/`: Node workspace v2/session v4 storage, multi-frame Zstandard, archives, package validation and recovery.
- `desktop/tests/`, `desktop/src/*.test.tsx`: frontend and bridge tests.
- `tests/dsh/`, `tests/ui/`: storage and retained WPF regression tests.
- `scripts/`: engine build, checks, stable packaging and release validation.

## Provider isolation

The UI clears provider-specific selections and in-flight preview state on switches. Codex and DSH keep separate homes, package formats and import handlers. Codex uses CODEX_HOME or its default; DSH uses DSH_MANAGER_HOME, the selected directory or ~/.dsh. Neither converts histories into the other format. Both data roots are protected from project-folder deletion and restoration.

The retained WPF UI still uses window/controller replacement for provider changes. That implementation is separate from the released React state model.

## Conversation reading and compression

Codex rollout I/O uses RolloutStorage and ZstdSharp.Port for bounded streaming decoding. Compressed previews retain parsed display text because raw compressed offsets cannot seek to decoded messages. Backups snapshot and decode to byte-preserving JSONL before hashing. Imports verify raw archive hashes before decoding. Existing compressed destinations are recompressed into temporary files and atomically replaced under rollback records.

DesktopPreviewCache persists indexed messages and navigation under the user's LocalApplicationData/CodexConversationManager/PreviewCache-v1. Keys include provider, home, project/session IDs, source path, size, creation/modification times and language. A provided source path must stay inside the selected home and cannot traverse a reparse point. Cache misses resolve the session through the catalog; source changes invalidate snapshots.

Cached page reads seek length-prefixed records without decompressing the source. Snapshots expire after 24 hours; creation prunes older snapshots against a 512 MiB budget, retaining the current snapshot even if larger. ConversationView virtualizes all messages, prefetches nearby pages, bounds the page working set and rejects mixed revisions. Navigation contains one entry per user message with question and answer excerpts. Individual displayed messages are capped at 100,000 characters; backup contents are independent of that display cap.

## Transactions and recovery

Management requests validate paths, provider, identity and current scope before writing. Codex uses the existing official app-server deletion integration. DSH requires its client/background hosts to be stopped, takes a manager lock, records a transaction manifest and original workspace state, and revalidates plans. Rollback and recovery avoid overwriting unrelated newer state.

DSH archives update archivedSessionIds, remove pinning and preserve workspace order. Deletion includes descendants and moves owned records into batch trash; shared attachments, model logs and plugin state remain outside that scope. Imports track lineage and reject divergent merges.

Project restoration has its own conflict policy and overwrite backup. It is not one atomic transaction with conversation import; already restored project files can remain after a later conversation failure.

## Distribution and validation

The canonical VERSION must match npm, Cargo, Tauri and release notes. scripts/package.ps1 builds the app, runs frontend/Rust/bridge, C# bilingual and DSH tests, packages a per-user NSIS installer and portable ZIP, then verifies checksums, required runtime files and packaged versions. Node and third-party license notices are included. Stable matching v* tags publish a non-prerelease through GitHub Actions.

CI uses Windows, .NET 8, Node 24 and Rust stable MSVC. SkipUiTests excludes only interactive legacy WPF window/render tests. Fixtures are synthetic. DSH real-session writes followed by client restart/resumption remain unverified end to end. See README.md for exact compatibility and size limits.
