[CmdletBinding()]
param([string]$Version, [string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$canonical = [IO.File]::ReadAllText((Join-Path $repoRoot 'VERSION')).Trim()
if (!$Version) { $Version = $canonical }
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -ne $canonical) { throw 'Release version must match VERSION and be a stable MAJOR.MINOR.PATCH.' }
foreach ($relative in @('desktop/package.json','desktop/src-tauri/tauri.conf.json')) {
    $json = Get-Content (Join-Path $repoRoot $relative) -Raw | ConvertFrom-Json
    if ($json.version -ne $Version) { throw "Version mismatch: $relative" }
}
$lock = [IO.File]::ReadAllText((Join-Path $repoRoot 'desktop/package-lock.json'))
if ([regex]::Match($lock, '"version"\s*:\s*"([^"]+)"').Groups[1].Value -ne $Version) { throw 'npm lock version mismatch.' }
foreach ($relative in @('desktop/src-tauri/Cargo.toml','desktop/src-tauri/Cargo.lock')) {
    $content = [IO.File]::ReadAllText((Join-Path $repoRoot $relative))
    $pattern = 'name = "conversation-manager-desktop"\r?\nversion = "' + [regex]::Escape($Version) + '"'
    if ($content -notmatch $pattern) { throw "Version mismatch: $relative" }
}
foreach ($relative in @('README.md','README.en.md','CHANGELOG.md',"docs/releases/v$Version.md")) {
    $content = [IO.File]::ReadAllText((Join-Path $repoRoot $relative))
    if (!$content.Contains($Version)) { throw "Missing release version: $relative" }
}
if ($PackagePath) {
    $package = Get-Item -LiteralPath $PackagePath
    $base = "AI-Conversation-Manager-$Version-Windows-x64"
    if ($package.Name -ne "$base-Portable.zip") { throw 'Unexpected package name.' }
    $setup = Join-Path $package.DirectoryName "$base-Setup.exe"
    $hashes = Get-Content (Join-Path $package.DirectoryName 'SHA256SUMS.txt')
    foreach ($file in @($package.FullName,$setup)) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        $name = [IO.Path]::GetFileName($file)
        if (!($hashes | Where-Object { $_ -ieq "$hash  $name" })) { throw "Checksum mismatch: $name" }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        $required = @('conversation-manager-desktop.exe','Start.cmd','README.txt','LICENSE.txt',
            'engine/CodexConversationManager.exe','engine/CodexConversationManager.exe.config',
            'engine/backend.cjs','engine/packages.cjs','engine/ZstdSharp.dll',
            'engine/System.Memory.dll','engine/System.Buffers.dll','engine/System.Numerics.Vectors.dll',
            'engine/System.Runtime.CompilerServices.Unsafe.dll','engine/System.Threading.Tasks.Extensions.dll',
            'engine/THIRD-PARTY-NOTICES.txt','engine/runtime/node.exe','engine/runtime/LICENSE-node.txt')
        foreach ($file in $required) {
            if ($entries -notcontains "$base/$file") { throw "Package is missing: $file" }
        }
        foreach ($entry in $entries) {
            if (!$entry.StartsWith("$base/") -or $entry -match '(^|/)\.\.?(/|$)|\.(jsonl|zst|sqlite|sqlite3|pdb|codexchat|codexproject|dshchat|dshproject)$|(^|/)(node_modules|\.git|\.env)(/|$)') { throw "Unexpected private/development entry: $entry" }
        }
        $temp = Join-Path ([IO.Path]::GetTempPath()) ('aicm-version-' + [guid]::NewGuid().ToString('N') + '.exe')
        try {
            foreach ($file in @('conversation-manager-desktop.exe','engine/CodexConversationManager.exe')) {
                $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq "$base/$file" } | Select-Object -First 1
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$temp,$true)
                $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($temp)
                $numericVersion = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"
                if ($numericVersion -ne $Version -or $info.FilePrivatePart -ne 0) { throw "Packaged executable version mismatch: $file" }
            }
        } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
    } finally { $archive.Dispose() }
}
Write-Host "Release verification passed for v$Version."
