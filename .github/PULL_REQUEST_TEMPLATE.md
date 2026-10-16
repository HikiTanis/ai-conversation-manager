## Summary

Describe the user-visible change and why it is needed.

## Safety and compatibility

- [ ] I considered project-path, archive, deletion, and Codex-index safety.
- [ ] I did not include real conversations, project data, user paths, IDs, screenshots, or secrets.
- [ ] New user-facing text is available in both Chinese and English.
- [ ] Backup-format changes are documented and legacy import behavior is considered.

## Verification

- [ ] `.\scripts\package.ps1 -SkipUiTests`
- [ ] Retained WPF tests when relevant: `.\scripts\test.ps1 -NoBuild`
- [ ] Relevant manual flow checked with synthetic data
