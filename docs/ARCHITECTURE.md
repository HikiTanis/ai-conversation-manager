# Architecture

The application is one WPF/.NET Framework 4.8 process with a shared interface and two storage providers. It does not translate conversations between providers.

## Source layout

- `src/CodexConversationManager`: original window, reusable dialogs/models, Codex catalog, reader, import transactions, official deletion and sidebar maintenance.
- `src/CodexConversationManager/Providers`: selected-provider context, window switching and deletion/trash routing.
- `src/CodexConversationManager/Providers/Dsh`: DSH list/preview adapter, archive commands, package bridge and local Node process transport.
- `adapters/dsh/backend.cjs`: workspace v2/session v4 scanning, concatenated Zstandard reading, archive state, transactional deletion, recovery and process guards.
- `adapters/dsh/packages.cjs`: DSH package validation, hashes, identity/path remapping and import transactions.
- `tests/ui`: synthetic provider-switch and DSH WPF regression entry points compiled into the diagnostic executable, consistent with the existing Codex self-test entry points.
- `tests/dsh`: Node tests using temporary fictional data; no real session fixtures.
- `scripts`: SDK build, fixture generation, tests, deterministic packaging and version/package checks.
- `packaging`: portable launcher. The root launcher is for source checkouts.

The previous independent DSH prototype, local SDK/compiler tools, screenshots, fixtures and compiled releases are local artifacts, not a second maintained product or published source.

## Provider boundary

`BackendContext` selects Codex or DSH. Changing provider uses the original language-switch window replacement pattern: create a new controller, retain window placement/current page, clear selection/search/import state, then close the old controller. The last provider is stored in the user's local application settings. Switching is blocked during busy operations.

Codex refresh/import/repair retain their original implementation. DSH branches before those operations and uses its adapter; shared deletion/trash commands route through `ProviderStorage`. Project file backup and restore reuse the existing path-validated ZIP implementation. Both providers' data directories are protected from project-directory deletion/restore.

Codex uses `CODEX_HOME` or its normal default. DSH uses `DSH_MANAGER_HOME`, the chosen data folder, or `~/.dsh`. Neither selector combines data from both roots. Backup extension and manifest validation prevent cross-provider imports.

## DSH process and transaction model

The C# bridge starts a discovered local Node runtime with a JSON request on standard input. The backend returns a structured result/error on standard output; it uses Node built-in modules only. The release does not download Node at runtime.

Read operations are local. Mutations require the DSH desktop/background hosts to be stopped, take a manager lock, validate current state and write a transaction manifest plus original workspace snapshot. Plans are revalidated before writes. Failed moves attempt rollback; interrupted transactions remain available to the recovery command. Recovery refuses to overwrite unrelated newer state.

Archive changes update native `archivedSessionIds`, remove pinning and retain workspace membership order. Deletion includes descendants and moves owned session folders/cache entries into batch trash. Shared attachments, model logs and plugins remain outside deletion scope. Imports track lineage separately and accept only identical/prefix-compatible histories for merge.

Project restore has its own conflict policy and overwrite backup. It is not one atomic transaction with conversation import; partially restored project files may remain after a later failure.

## Validation

`scripts/test.ps1` retains original Codex safety/format/rollback and bilingual desktop tests. `scripts/test-providers.ps1` adds DSH storage/package tests and repeated Codex → DSH → Codex transitions, isolation, archive filters, preview and package inspection.

CI supplies .NET 8 and Node 24. `-SkipUiTests` skips desktop-dependent regressions only; backend and functional tests still run. All release evidence uses synthetic data. DSH live writes/client restart have not been verified end to end.
