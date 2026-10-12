# Security policy

## Supported versions

| Version | Supported |
| --- | --- |
| Latest release | Yes |
| Older releases | Upgrade first |

Codex local storage formats can change independently of this project. Reproduce a problem on the latest release before reporting it.

## Reporting a vulnerability

Use the repository's GitHub **Private vulnerability reporting** or private security-advisory feature. Do not open a public issue for path-traversal, arbitrary-file-write, unsafe-deletion, archive-validation, local-index-corruption, or release-supply-chain findings until a fix is available.

Include a minimal synthetic reproduction and affected version. Never send a real Codex home, backup package, session JSONL, SQLite database, access token, private path, or proprietary project.

## Security boundary

The application processes local archives and can write to a selected project folder and the current user's Codex data. Its built-in native engine validates archive and manifest paths, rejects duplicate or traversal entries, dangerous project roots, and reparse-point escapes, verifies session and project-payload hashes and statistics, checks conversation identity and source-project ownership, backs up relevant state before risky writes, limits deletion to resolved targets, and stops when the official Codex deletion protocol refuses an operation.

These controls reduce risk but do not make an untrusted backup package safe. Use **Inspect first (no writes)**, review the source of the package, keep independent backups, and fully exit Codex before formal import, deletion, or restoration.

## Release authenticity

The Windows application is currently not Authenticode-signed. Official release assets include `SHA256SUMS.txt`; verify the downloaded ZIP as shown in the README before extraction. A matching hash detects an altered download but does not by itself prove publisher identity.

The release package does not bundle or download a third-party migration executable. Codex inspection, backup, and import run in the application process. Deletion and supported legacy-sidebar repair require a compatible locally available Codex CLI executable. Codex CLI 0.148.0 or later is this project's supported compatibility baseline; using the latest release is recommended. The application can also discover compatible runtimes bundled with Codex Desktop or the VS Code extension.

Compressed Codex `.jsonl.zst` sessions are currently limited to index-metadata discovery and are excluded from content preview, formal backup, and import.

## DSH support

DSH Desktop compatibility was checked against 0.2.0-rc.2 storage (workspace v2, session v4 JSONL and concatenated Zstd frames). DSH requires a locally available Node.js runtime with Zstd support (22.15+ or 24+). Adapters run in a local subprocess and use separate packages, trash, and transaction records. Close DSH Desktop before archive, deletion, import, or restoration. Synthetic tests do not establish compatibility with every future desktop version.

Reports should name the selected provider, desktop version, Node.js version, and redacted error text. Never attach real DSH data or packages.
