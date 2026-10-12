# Codex Conversation Manager

**One Windows app for Codex and DeepSeek Harness (DSH) conversations and projects.**

[简体中文](README.zh-CN.md) · Current release: **v1.1.0** · [Download](https://github.com/HikiTanis/codex-conversation-manager/releases/tag/v1.1.0)

Switch **Codex / DSH** in the top-right of the original manager UI. Projects, conversations, previews, trash and backup/import use the selected provider. The app remembers the last provider; switching clears selections, search and pending import paths. Switching is disabled during an operation.

This is an unofficial community tool, not affiliated with OpenAI or DeepSeek.

## Supported features

| Capability | Codex | DSH |
| --- | --- | --- |
| Project grouping, paths, sizes, search, main/subagent views | Yes | Yes |
| All / Active / Archived classification | Yes | Yes |
| Archive / unarchive selected conversations | Use Codex itself | Yes, native archive state |
| Message preview and user-message navigation | Uncompressed JSONL; full-history viewer | Session v4 JSONL and multi-frame Zstandard; preview limit below |
| Batch deletion, trash, restore, permanent removal | Existing official-protocol integration | Offline local session/workspace adapter |
| Delete descendants with a parent | Yes | Yes |
| Optional project-folder handling | Windows Recycle Bin or permanent deletion | Same workflow |
| Conversation-only backup | `.codexchat` | `.dshchat` |
| Projects + conversations backup | `.codexproject` | `.dshproject` |
| Inspect before import, destination mapping, merge/copy | Yes | Yes; merge rejects divergent history |
| Legacy backup import | `.codexpack`, `.codexbundle` | No |
| Stale Codex sidebar detection/repair | Yes | Not applicable |
| Interrupted DSH operation recovery | Not applicable | Yes |

Chinese and English share the same window layout. Some DSH action prompts and diagnostic messages remain Chinese. Provider selection does **not** convert Codex conversations to DSH or vice versa; their packages cannot be cross-imported.

## Download and run

1. Download `CodexConversationManager-Windows-v1.1.0.zip` and `SHA256SUMS.txt` from the release above.
2. Compare `(Get-FileHash .\CodexConversationManager-Windows-v1.1.0.zip -Algorithm SHA256).Hash` with `SHA256SUMS.txt`.
3. Extract the complete ZIP into a new folder. Keep the EXE, XAML, `assets`, `backend.cjs` and `packages.cjs` together.
4. Double-click `Start.cmd` or `CodexConversationManager.exe`, then select **Codex** or **DSH**.

The portable application is not Authenticode-signed; Windows may show an unknown-publisher warning. It has no installer, telemetry, cloud sync or automatic updater.

### Requirements

- 64-bit Windows 10/11 and .NET Framework 4.8 Runtime.
- **Codex:** inspection, preview, backup and import use the built-in native engine. Conversation deletion and sidebar repair require **Codex CLI 0.148.0 or later** (this project's compatibility baseline). Compatible Desktop/VS Code bundled runtimes can be discovered automatically.
- **DSH:** a local Node.js runtime with Zstandard support: Node.js 22.15+ or 24+. The manager checks DSH's bundled runtime, `runtime/node.exe` beside the EXE, and PATH. Node.js is not included in the release ZIP.
- Codex uses `CODEX_HOME` or its normal default. DSH uses `%USERPROFILE%/.dsh`, the **Data folder** chooser, or `DSH_MANAGER_HOME`.

Fully exit the corresponding client before import, deletion or restoration, and exit DSH before archive changes. Do not restart it while an operation is running.

## Common workflows

- **Review and clean up:** select a project, choose main conversations or subagents, apply the archive filter, preview records, then select and delete. Parent deletion includes linked descendants. App trash supports recovery; moving to trash does not free the space retained by its backup.
- **Move or rename a project:** create a conversation-only backup, move the project folder, then import into its new location. Inspect first. Merge identifies the original conversation within the destination project; copy creates independent IDs/files.
- **Move everything together:** choose Projects + conversations, select projects, and create a project package. On the destination computer, choose the restore directory and file-conflict policy, inspect, then import.
- **DSH archives:** select visible records and choose Archive selected or Unarchive. Archiving removes pinning but retains workspace order. Unarchiving does not reinstate an old pin.

Conversation-only packages contain selected main conversations across projects. Project packages include project files and all associated main/subagent conversations. Backups remain independent files even if the source conversation is later deleted.

## Compatibility and limits

- Codex `.jsonl.zst` is metadata-only: no content preview, formal backup or import. Legacy and validated paginated uncompressed histories retain the existing compatibility checks.
- DSH is adapted to Desktop **0.2.0-rc.2**, workspace v2 and session v4. Unknown formats, ambiguous IDs, link paths and incomplete scans block writes. Custom session/storage roots and third-party plugin state are unsupported.
- DSH reads logs up to 128 MiB compressed and decompressed, and displays up to 2 million preview characters. Shared attachments, model logs and plugin storage are not erased when a session is deleted.
- DSH has synthetic write/rollback tests and local read-only verification. Real-session writes and client restart/resumption have **not** been validated end to end. Keep the source backup until the destination client can open the imported history.
- Project archives preserve ordinary files, empty directories and modification times, not junctions, symlinks, ACLs or alternate data streams. Restore supports empty destination, keep existing, or overwrite with a recovery backup.
- Conversation import has rollback records. Project files already restored are not fully rolled back if a later conversation import fails; retain the reported directories and overwrite backups.
- Backups, trash and recovery files are unencrypted and can contain prompts, secrets and project source. Do not post real data in public issues.

## Repository and development

```text
src/CodexConversationManager/   Shared WPF UI and existing Codex engine
  Providers/                   Provider selection and operation routing
    Dsh/                       DSH controller, process and package adapters
adapters/dsh/                  Node backend, native DSH storage and packages
tests/ui/                     Synthetic WPF/provider regression entry points
tests/dsh/                    Node storage, archive and rollback tests
scripts/                       Build, test, package and release verification
packaging/                     Portable launcher
docs/                          Formats, privacy, architecture and releases
```

Source builds use Windows, .NET SDK 8.x, Node.js 24 and PowerShell 5.1+. The .NET Framework reference assemblies are restored from NuGet.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -NoBuild
.\scripts\test-providers.ps1 -NoBuild
.\scripts\package.ps1
```

From a source checkout, root `Start.cmd` runs the Release build (building it if missing). Release ZIPs use the separate portable launcher. Local `.tools`, `artifacts`, build output and generated release assets are ignored; no real conversation data belongs in source control.

See `docs/ARCHITECTURE.md`, `docs/BACKUP_FORMATS.md`, `docs/PRIVACY.md`, `docs/RELEASING.md`, `docs/releases/v1.1.0.md` and `CHANGELOG.md` in the source repository. Licensed under the [MIT License](LICENSE).
