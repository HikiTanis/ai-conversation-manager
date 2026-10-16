[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$NoBuild) { & (Join-Path $repoRoot 'scripts/build.ps1') }
$source = Join-Path $repoRoot 'src/CodexConversationManager/bin/Release/net48'
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'src-tauri/engine'
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($name in @('CodexConversationManager.exe','CodexConversationManager.exe.config','backend.cjs','packages.cjs','THIRD-PARTY-NOTICES.txt')) {
 Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
}
Get-ChildItem -LiteralPath $source -Filter '*.dll' -File | Copy-Item -Destination $target -Force
Write-Host "Prepared compatibility engine: $target"
