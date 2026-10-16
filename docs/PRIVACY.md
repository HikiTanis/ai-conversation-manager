# Privacy

AI Conversation Manager is local-first and has no telemetry, analytics, advertising, account system, cloud upload, automatic update, or background synchronization feature. The installer may download Microsoft's WebView2 runtime when missing; this does not upload conversation data.

## Local data the application can read

Depending on the selected operation, the application can read:

- Codex session JSONL files and their metadata;
- DSH workspace metadata, session v4 JSONL/Zstd files, parent relationships, and archive state;
- the local Codex SQLite task index and subagent relationships;
- Codex desktop project/task state and exact sidebar cache records;
- local Codex error logs used to confirm an older partial-deletion candidate;
- project files selected for a `.codexproject` backup.

For Codex, inspection, backup, dry-run, and import are performed by the application's built-in native engine. The release package does not bundle or download a third-party migration executable. Conversation deletion and supported legacy-sidebar repair use a compatible locally available Codex CLI executable through its app-server protocol. Codex CLI 0.148.0 or later is this project's supported compatibility baseline, and the latest release is recommended; the application can also discover compatible runtimes bundled with Codex Desktop or the VS Code extension. The CLI is not required for inspection, backup, or import, and the application does not use it to submit prompts or start model work.

## Local data the application can write

- Desktop preview snapshots contain message text and navigation excerpts in `%LOCALAPPDATA%/CodexConversationManager/PreviewCache-v1`. They are unencrypted, expire after 24 hours, and are pruned on cache creation against an approximately 512 MiB budget (the current snapshot is retained). Source changes or deletion invalidate reuse; cached files are not immediately erased by source deletion.

- Formal `.codexchat` and `.codexproject` packages are written only to a folder selected by the user.
- App-trash records are stored inside the active Codex data directory and remain there until restored or permanently purged.
- Import and restoration can update session files, task indexes, and desktop project-association data.
- Before risky index or desktop-state writes, local safety copies can be stored under the active Codex data directory. They may contain task titles, Thread IDs, project paths, and index metadata; they are not formal conversation backups or app-trash records.
- Overwrite-mode project restoration can create a recovery ZIP inside the active Codex data directory.
- A selected project folder can be sent to the Windows Recycle Bin or permanently deleted only after explicit confirmation.

Compressed Codex `.jsonl.zst` sessions are currently read only at the index-metadata level. Their conversation contents are not previewed, formally backed up, or imported.

## Sensitive content

Formal backups, recovery ZIPs, and app-trash entries are not encrypted. They may contain complete prompts, responses, tool output, local paths, project source files, environment details, credentials, and secrets. Anyone who can read one of these files may be able to read that content.

Store packages securely, encrypt them before using an untrusted transfer channel, and permanently remove copies that are no longer needed. Never attach a real Codex home, backup package, session JSONL, SQLite database, project payload, access token, or unredacted screenshot to a public issue.

## DSH and preferences

DSH operations run the bundled JavaScript adapters in a locally installed Node.js subprocess. The app discovers a compatible DSH runtime or Node.js with Zstd support (22.15+ or 24+); it does not download a runtime. DSH archive changes update local session metadata; removal, recovery, package import, and project restoration write to the selected DSH home or selected project directory. DSH packages (`.dshchat`, `.dshproject`) and trash contain unencrypted native session data. Zstd content is decoded locally.

Provider and language preferences are saved under the current user's local application-data directory. DSH has its own trash and transaction records, separate from Codex. Closing the corresponding desktop client before mutations avoids concurrent writes. No real session data is needed for tests or bug reports.
