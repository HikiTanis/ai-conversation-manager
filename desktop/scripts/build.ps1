[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$desktopRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $desktopRoot
# Refresh a configured user installation for terminals opened before setup.
if (!(Get-Command cargo -ErrorAction SilentlyContinue)) {
    $userCargo = [Environment]::GetEnvironmentVariable('CARGO_HOME', 'User')
    $userRustup = [Environment]::GetEnvironmentVariable('RUSTUP_HOME', 'User')
    if ($userCargo -and (Test-Path -LiteralPath (Join-Path $userCargo 'bin/cargo.exe'))) {
        $env:CARGO_HOME = $userCargo
        if ($userRustup) { $env:RUSTUP_HOME = $userRustup }
        $env:PATH = (Join-Path $userCargo 'bin') + ';' + $env:PATH
    }
}
# Retain support for optional project-local installations.
$localCargo = Join-Path $repoRoot '.tools/rust/cargo'
if (!(Get-Command cargo -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $localCargo 'bin/cargo.exe'))) {
    $env:CARGO_HOME = $localCargo
    $env:RUSTUP_HOME = Join-Path $repoRoot '.tools/rust/rustup'
    $env:PATH = (Join-Path $localCargo 'bin') + ';' + $env:PATH
}
if (Test-Path -LiteralPath (Join-Path $repoRoot '.tools/dotnet/dotnet.exe')) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget'
}
if (!(Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'Install Rust stable MSVC and Microsoft C++ Build Tools. See desktop/README.md.' }
if (!$env:CARGO_BUILD_JOBS) { $env:CARGO_BUILD_JOBS = '2' }
Push-Location $desktopRoot
try {
    npm ci --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    & (Join-Path $PSScriptRoot 'prepare-engine.ps1')
    if (!$SkipTests) {
        npm test
        if ($LASTEXITCODE -ne 0) { throw 'React tests failed' }
        npm run test:bridge
        if ($LASTEXITCODE -ne 0) { throw 'Compatibility bridge tests failed' }
        cargo test --manifest-path src-tauri/Cargo.toml --locked
        if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed' }
    }
    npm run desktop:build
    if ($LASTEXITCODE -ne 0) { throw 'Tauri build failed' }
    if (!$SkipTests) { & (Join-Path $desktopRoot 'tests/updater-smoke.ps1') }
    $destination = Join-Path $repoRoot 'artifacts/desktop'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $desktopRoot 'src-tauri/target/release/conversation-manager-desktop.exe') -Destination $destination -Force
    $engineDestination = Join-Path $destination 'engine'
    New-Item -ItemType Directory -Path $engineDestination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $desktopRoot 'src-tauri/engine') -File | Copy-Item -Destination $engineDestination -Force
    Copy-Item -LiteralPath (Join-Path $desktopRoot 'README.md') -Destination $destination -Force
    Write-Host "AI Conversation Manager ready: $destination"
} finally { Pop-Location }
