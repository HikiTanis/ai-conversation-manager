# Releasing

This checklist is for maintainers preparing a public GitHub release. Run it from a clean checkout on Windows.

## 1. Prepare the version

`VERSION` is the authoritative version for packaging, CI, and generated assembly metadata. It must contain one stable semantic version such as `1.0.0`.

For a new release:

1. Update `VERSION`.
2. Update the visible version label when `scripts/Verify-Release.ps1` identifies it. The app-server client version is derived automatically from the built assembly.
3. Add `docs/releases/v<version>.md`.
4. Move completed entries from `[Unreleased]` into a dated version section in `CHANGELOG.md`.
5. Update version-specific download names in both README files.

Run the source-level consistency check:

```powershell
$version = (Get-Content .\VERSION -Raw).Trim()
.\scripts\Verify-Release.ps1 -Version $version
```

Do not continue until it passes.

## 2. Build, test, and package

The complete local release command is:

```powershell
.\scripts\package.ps1
```

It performs the following work:

- verifies source and documentation version consistency;
- restores the pinned NuGet dependencies, including `Microsoft.NETFramework.ReferenceAssemblies.net48`;
- creates a Release build;
- runs Chinese and English functional, window-chrome, and render tests in a temporary synthetic Codex home;
- runs the DSH Node.js regression suite plus provider switching and DSH UI tests using synthetic homes (`scripts/test-providers.ps1`);
- creates a deterministic candidate ZIP containing only the portable application, concise bilingual README files, version, and license;
- creates the candidate SHA-256 checksum;
- verifies the ZIP file list, Markdown links, EXE version, hash, and absence of an absolute PDB path;
- atomically publishes and verifies the current ZIP plus `SHA256SUMS.txt`, then removes older local ZIP versions only after that pair is durable.

Source packaging requires Windows, .NET SDK 8.x, Node.js 24+, and PowerShell 5.1 or newer. The pinned reference-assemblies package removes the need for a separately installed .NET Framework 4.8 Targeting Pack. Codex uses its built-in native engine; DSH uses the repository adapters in a local Node.js subprocess, so the build and release package must not download, invoke, or contain a separate conversation-transfer executable.

Review `artifacts/test/` and `artifacts/unified-*/`; every self-test and chrome report must pass, every expected PNG must be present, and no `*.error.txt` file may exist.

Before the first public release, enable **Private vulnerability reporting** under the repository's GitHub security settings so `.github/SECURITY.md` points to a working private channel.

## 3. Inspect the release assets

Confirm that `release/` contains exactly:

- `CodexConversationManager-Windows-v<version>.zip`
- `SHA256SUMS.txt`
- the tracked `.gitkeep`

After extraction, the ZIP itself must contain exactly:

- `CodexConversationManager.exe`
- `CodexConversationManager.exe.config`
- `CodexConversationManager.xaml`
- `DialogTheme.xaml`
- `backend.cjs`
- `packages.cjs`
- `assets/app-icon.ico`
- `Start.cmd`
- `VERSION`
- `README.md`
- `README.zh-CN.md`
- `LICENSE`

Verify the checksum independently:

```powershell
$version = (Get-Content .\VERSION -Raw).Trim()
$zip = ".\release\CodexConversationManager-Windows-v$version.zip"
$expected = ((Get-Content .\release\SHA256SUMS.txt -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Release checksum mismatch.' }
```

Open the extracted package on a clean test account when practical. Check startup, language switching, backup inspection, and a synthetic import. Never use real private conversations or project data in release evidence.

The binaries are currently not Authenticode-signed. Keep the unsigned-package and SHA-256 verification notice in both README files and the release notes until signing is introduced.

## 4. Commit and tag

Before tagging:

```powershell
git status --short
git diff --check
```

Commit all intended source and documentation changes. Confirm that the Git author email is safe to publish.

Create an annotated tag only after the final commit exists:

```powershell
$version = (Get-Content .\VERSION -Raw).Trim()
git tag -a "v$version" -m "Codex Conversation Manager v$version"
```

If an unpublished local tag already points to an older preparatory commit, remove and recreate it before any push. Never move or replace a tag that has already been published.

## 5. Push and verify GitHub Release

After configuring the intended `origin` remote:

```powershell
$version = (Get-Content .\VERSION -Raw).Trim()
git push origin main
# Wait for the branch build to pass before publishing the tag.
git push origin "v$version"
```

The tag workflow rejects a tag that does not exactly match `VERSION`, rebuilds and retests from the tagged commit, and uses `docs/releases/v<version>.md` as the GitHub Release body.

After the workflow finishes, verify:

- the workflow is green;
- the release title and notes are correct;
- the ZIP and `SHA256SUMS.txt` assets are present;
- the downloaded asset hash matches;
- the README download instructions refer to the published version.

If any check fails, fix it in a new commit and release a new version. Do not silently replace public release assets or move a published tag.
