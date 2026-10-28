[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$binary = Join-Path $repo 'desktop/src-tauri/target/release/conversation-manager-desktop.exe'
if (!(Test-Path -LiteralPath $binary)) { throw 'Build the release executable first.' }
$work = Join-Path $repo ('artifacts/updater-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$source = @'
using System;
using System.IO;
using System.Threading;
class Fixture {
    static void Main(string[] args) {
        if (args.Length == 1 && args[0] == "--wait") { Thread.Sleep(3000); return; }
        if (args.Length > 0) { File.WriteAllLines("setup-args.txt", args); return; }
        File.WriteAllText("restarted.txt", "new application started");
    }
}
'@
$sourcePath = Join-Path $work 'Fixture.cs'
[IO.File]::WriteAllText($sourcePath, $source)
$fixture = Join-Path $work 'Fixture.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:winexe "/out:$fixture" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
foreach ($installed in @($false,$true)) {
    $case = Join-Path $work $(if ($installed) { 'installed' } else { 'portable' })
    $target = Join-Path $case 'app with spaces'
    $handoff = Join-Path $case 'handoff'
    $payload = Join-Path $handoff 'payload'
    New-Item -ItemType Directory -Path $target,$handoff,(Join-Path $payload 'engine') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $target 'conversation-manager-desktop.exe'), 'old application')
    [IO.File]::WriteAllText((Join-Path $target 'user-backup.txt'), 'preserve user file')
    Copy-Item -LiteralPath $binary -Destination (Join-Path $handoff 'update-helper.exe')
    $files = @()
    $packageHash = ''
    if ($installed) {
        Copy-Item -LiteralPath $fixture -Destination (Join-Path $handoff 'setup.exe')
        $packageHash = (Get-FileHash -LiteralPath (Join-Path $handoff 'setup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    } else {
        Copy-Item -LiteralPath $fixture -Destination (Join-Path $payload 'conversation-manager-desktop.exe')
        [IO.File]::WriteAllText((Join-Path $payload 'engine/CodexConversationManager.exe'), 'fixture engine')
        foreach ($relative in @('conversation-manager-desktop.exe','engine/CodexConversationManager.exe')) {
            $files += @{path=$relative; sha256=(Get-FileHash -LiteralPath (Join-Path $payload $relative) -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    }
    $parent = Start-Process -FilePath $fixture -ArgumentList '--wait' -WindowStyle Hidden -PassThru
    $manifest = @{parent=$parent.Id; target=$target; installed=$installed; package_hash=$packageHash; files=@($files)} | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $handoff 'handoff.json'), $manifest)
    $helper = Start-Process -FilePath (Join-Path $handoff 'update-helper.exe') -ArgumentList '--apply-update' -WorkingDirectory $handoff -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
    if ([IO.File]::ReadAllText((Join-Path $target 'conversation-manager-desktop.exe')) -ne 'old application') { throw 'Replaced files before the parent exited.' }
    if (!$helper.WaitForExit(20000)) { $helper.Kill(); throw "Helper timed out. Inspect $handoff" }
    if (Test-Path -LiteralPath (Join-Path $handoff 'error.txt')) { throw ([IO.File]::ReadAllText((Join-Path $handoff 'error.txt'))) }
    if ($installed) {
        $arguments = [IO.File]::ReadAllLines((Join-Path $handoff 'setup-args.txt'))
        if (($arguments -join '|') -ne "/S|/UPDATE|/R|/D=$target") { throw 'Installer arguments did not preserve the installation path.' }
    } else {
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while (!(Test-Path -LiteralPath (Join-Path $target 'restarted.txt')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath (Join-Path $target 'restarted.txt'))) { throw 'Updated application did not restart.' }
        if ([IO.File]::ReadAllText((Join-Path $target ".update-backup-$($parent.Id)/conversation-manager-desktop.exe")) -ne 'old application') { throw 'Old executable backup is missing.' }
    }
    if ([IO.File]::ReadAllText((Join-Path $target 'user-backup.txt')) -ne 'preserve user file') { throw 'Unmanaged file changed.' }
    Write-Host "Updater smoke passed: installed=$installed"
}
Write-Host "Isolated updater artifacts: $work"
