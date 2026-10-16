[CmdletBinding()]
param([string]$Version, [switch]$SkipTests, [switch]$SkipUiTests)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Version $Version
& (Join-Path $repoRoot 'desktop/scripts/build.ps1') -SkipTests:$SkipTests
if (!$SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -NoBuild -SkipUiTests:$SkipUiTests
    & (Join-Path $PSScriptRoot 'test-providers.ps1') -NoBuild -SkipUiTests:$SkipUiTests
}
& (Join-Path $repoRoot 'desktop/scripts/package.ps1') -SkipBuild
$versionValue = [IO.File]::ReadAllText((Join-Path $repoRoot 'VERSION')).Trim()
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Version $versionValue -PackagePath (Join-Path $repoRoot "release/v$versionValue/AI-Conversation-Manager-$versionValue-Windows-x64-Portable.zip")
