# Backup formats and import behavior

The application distinguishes user-created backup packages from the app trash. They serve different purposes and should not be managed as interchangeable backups.

For Codex, inspection, package creation, dry-run validation, and import are implemented by the application's built-in native engine. These operations do not invoke or require a third-party migration executable. Codex CLI 0.148.0 or later is used only for conversation deletion and supported legacy-sidebar repair; the latest CLI release is recommended.

## Storage types at a glance

| Type | User-managed | Project files | Main conversations | Subagent conversations | Purpose |
| --- | --- | --- | --- | --- | --- |
| `.codexchat` | Yes | No | Selected records | No | Conversation-only migration or archive |
| `.codexproject` | Yes | Yes | All linked records in selected projects | Yes | Complete project-workspace migration |
| App trash | Through the application | No | Selected/affected records | Selected/affected records | Recoverable local removal |

Moving a conversation to the app trash is not the same as creating a formal backup package, and it does not materially reclaim space from the drive containing the Codex data directory. Permanently purging a confirmed-unneeded trash entry releases its stored session data.

## Formal package container

`.codexchat` and `.codexproject` are ZIP containers with a `manifest.json`. They also contain one or more internal `.codexbundle` files; `.codexproject` additionally contains one or more `project-files.zip` payloads.

The current writer uses these manifest schema values:

| Package shape | Current schema |
| --- | --- |
| One-project `.codexchat` | 2 |
| One-project `.codexproject` | 3 |
| Multi-project `.codexchat` or `.codexproject` | 5 |

The schema number is an internal compatibility marker, not an invitation to edit a package manually. Import also supports earlier manifests when their required fields can be validated.

The manifest records source-project identity, bundle ownership, session lineage, titles, timestamps, subagent status, project payload statistics, and the SHA-256 of each project-file payload. During inspection and import, the native engine verifies the archive entry count and expanded size, declared and embedded conversation IDs, session and project-payload SHA-256 values, bundle/project ownership, duplicate and traversal paths, source working directories, destination boundaries, payload statistics, and archive structure before writing project files or Codex data. Invalid packages fail before commit, and interrupted writes use transaction snapshots for rollback.

## `.codexchat`

A conversation-only package contains selected **main conversations** and package metadata, but no project-directory payload or subagent conversations. Typical uses include:

- copying a project folder manually and migrating only its conversations;
- reconnecting conversations after a project is renamed or moved on the same computer;
- archiving selected main conversations without duplicating project files.

Selections can span multiple projects. During import, each source project is mapped to its actual destination folder. Conversation files are placed in the current user's normal Codex data directory.

## `.codexproject`

A complete project package contains one or more project-directory payloads, empty-directory metadata, all linked main and subagent conversations, checksums, and source-lineage metadata.

Project payloads preserve ordinary file contents, directory structure, empty directories, and last-write timestamps. Directory junctions, symbolic links, and other reparse points are skipped. NTFS access-control lists, alternate data streams, and other filesystem-specific metadata are not transferred.

During restore, each project is mapped to a destination folder selected by the user, while conversation files are imported into the current user's normal Codex data directory.

## Project-path reassociation

Import can rewrite recorded working-directory paths and perform targeted updates to the local Codex task index and desktop project association. This allows conversations to be associated with a copied, renamed, or relocated project instead of remaining tied to the source path.

An identical source and destination path is treated as an intentional no-op mapping. A different destination causes the native importer to rewrite only the structured working-directory fields that belong to the imported session.

## Conversation identity modes

### Merge by original ID

Matching is scoped to the destination project plus the source conversation's original ID. The first import receives a local Thread ID and retains source-lineage metadata. Later imports can locate the same lineage and merge new content without sharing a session file with an unrelated project.

This supports round-trip work: migrate from computer A to B, continue on B, create another backup, and merge later content back into the corresponding project on A.

### Copy as new conversations

Every import creates fresh Thread IDs and independent conversation files. When a package contains subagents, main/subagent parent-child IDs are rewritten together. Deleting one copy does not delete another copy.

### Codex history modes

Each imported session keeps its recorded `legacy` or `paginated` history mode. For paginated sessions, import also requires valid JSONL, integer `ordinal` values starting at zero without gaps, and `turn_context` when history records are present. The targeted index write verifies the stored mode together with the session path and project association. Unknown future modes and structurally incomplete paginated sessions fail before a successful import is reported.

Full paginated-history reading and resumption remain dependent on the destination Codex version. Keep the source package until imported conversations have been opened and verified in Codex. The current [Codex app-server documentation](https://developers.openai.com/codex/app-server) describes this capability as experimental and not yet available for full-history reads or resume.

## Legacy inputs

`.codexpack` and raw `.codexbundle` files remain supported for import compatibility. New user-created backups use `.codexchat` or `.codexproject` so their purpose is visible from the extension.

Legacy formats are import-only: the application does not create new `.codexpack` or standalone `.codexbundle` backups.

## Compressed-session limitation

Codex sessions stored as `.jsonl.zst` are currently discoverable only through their index metadata. Their compressed conversation content is not decoded by this release, so these records do not support content preview, creation of a formal `.codexchat`/`.codexproject` backup, or import. Keep the original compressed file and use an uncompressed `.jsonl` source for workflows that require conversation contents.

## Project-file conflict modes

- **Require an empty destination**: safest default; restoration stops if the folder is not empty.
- **Keep existing files**: adds missing files and leaves same-name destination files unchanged.
- **Overwrite with backup**: creates a recovery ZIP before overwriting same-name files.

Archive paths, destination boundaries, drive roots, protected folders, free space, duplicate paths, Windows-reserved names, and reparse points are validated before project files are written.

## Resource limits

To keep inspection and import bounded when a package is damaged or untrusted, this release applies these hard limits:

- an outer formal package, an inner conversation bundle, or a project payload may contain at most 50,000 entries and expand to at most 128 GB;
- a conversation-bundle or project-payload entry may expand to at most 32 GB;
- outer and conversation metadata files are limited to 16 MB;
- a JSONL record examined during project-path rewriting is limited to 64 MB;
- entries of 16 MB or larger with an expansion ratio above 10,000:1 are rejected.

Backup creation applies the matching project-payload limits so the application does not intentionally produce a project package that it cannot later restore. These limits are safety boundaries, not recommended package sizes; very large packages still require enough temporary and destination disk space.

## Confidentiality

Formal packages and app-trash records are not encrypted. They may contain complete prompts, responses, source code, command output, local paths, environment details, credentials, and secrets. Inspect and transfer them as sensitive files, and never attach a real package to a public issue.

## DeepSeek Harness formats (v1.1.0)

The sections above describe the existing Codex engine. DSH uses the same outer ZIP/path-validated project payload layer, but a separate conversation manifest (`schema: 1`, `engine: deepseek-harness`). Its schema numbering is independent of Codex schemas.

- `.dshchat`: selected main conversations across projects, no project payload.
- `.dshproject`: selected project directories plus associated main/subagent sessions.
- `native_sessions` records session identity/origin, cwd, parent, archive/pin state, log name and a SHA-256/length file inventory. Session folders are stored under numbered package folders; native DSH directory encoding is recreated on import.
- Project payload ZIPs use the shared project-file validation/conflict implementation. Only the selected provider's package is accepted; changing the filename cannot convert its manifest.
- Merge matches original identity within the destination project, accepts matching/prefix-continuation event histories and rejects forks. Independent copy gives each session a fresh ID and remaps included parent references. A parent absent from the package is detached with a warning.
- The local `conversation-manager-lineage.json` retains origin information. DSH `conversation-manager-trash` contains deletion batches and write/recovery records; it is not a user-managed formal backup.
- DSH supports session v4 `.jsonl` and concatenated `.jsonl.zstd` frames, unlike Codex's metadata-only `.jsonl.zst`. Each encoded/decoded log is capped at 128 MiB; previews at 2 million characters; packages at 10,000 sessions and 50,000 declared files per session, in addition to outer ZIP/project limits.
- Import stages new session data, records original workspace/lineage and moved originals, revalidates the plan, then commits. Recovery refuses to remove changed imported data or overwrite unrelated new workspace state.
- Project restore and conversation import are separate operations: a subsequent conversation failure can leave already restored project files in place. Overwrite recovery paths are reported.

DSH compatibility is based on Desktop 0.2.0-rc.2, workspace v2 and session v4. Synthetic round-trip/rollback tests do not replace opening and resuming the imported session in the destination client; retain source packages until verified.
