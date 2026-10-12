[CmdletBinding()]
param([switch]$NoBuild, [switch]$SkipUiTests)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
& node (Join-Path $PSScriptRoot 'make-provider-fixtures.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed.' }
& node --test (Join-Path $repoRoot 'tests/dsh/backend.test.cjs')
if ($LASTEXITCODE -ne 0) { throw 'DSH backend tests failed.' }
if ($SkipUiTests) { Write-Host 'Provider backend tests passed; interactive UI tests explicitly skipped.'; return }
$previousCodex = $env:CODEX_HOME
$previousDsh = $env:DSH_MANAGER_HOME
$previousOfficial = $env:CODEX_MIGRATOR_REAL_OFFICIAL_DELETE_TEST
$app = Join-Path $repoRoot 'src/CodexConversationManager/bin/Release/net48/CodexConversationManager.exe'
try {
    $env:CODEX_HOME = Join-Path $repoRoot 'artifacts/provider-fixtures/codex'
    $env:DSH_MANAGER_HOME = Join-Path $repoRoot 'artifacts/provider-fixtures/dsh'
    $env:CODEX_MIGRATOR_REAL_OFFICIAL_DELETE_TEST = '0'
    foreach ($language in @('zh-CN','en-US')) {
        foreach ($kind in @('provider-test','dsh-ui-test','self-test')) {
            $output = Join-Path $repoRoot "artifacts/unified-$kind-$language"
            $output = [IO.Path]::GetFullPath($output)
            $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (-not $output.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe test output: $output" }
            if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
            New-Item -ItemType Directory -Path $output -Force | Out-Null
            $report = Join-Path $output 'result.txt'
            $arguments = if ($kind -eq 'self-test') { "--self-test --report `"$report`" --language $language" } else { "--$kind `"$output`" --language $language" }
            $run = Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -PassThru
            if (!$run.WaitForExit(45000)) { $run.Kill(); throw "$kind timed out. See $output" }
            if ($run.ExitCode -ne 0) { throw "$kind failed. See $output" }
            if (!(Test-Path -LiteralPath $report)) { throw "$kind did not produce a result." }
            Write-Host "$kind ($language) passed."
        }
    }
} finally {
    $env:CODEX_HOME = $previousCodex
    $env:DSH_MANAGER_HOME = $previousDsh
    $env:CODEX_MIGRATOR_REAL_OFFICIAL_DELETE_TEST = $previousOfficial
}
