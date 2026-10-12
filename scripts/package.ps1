[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$SkipUiTests
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$canonicalVersion = [IO.File]::ReadAllText((Join-Path $repoRoot 'VERSION')).Trim()
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $canonicalVersion
}
if (-not [string]::Equals($Version, $canonicalVersion, [StringComparison]::Ordinal)) {
    throw "Requested version $Version does not match VERSION $canonicalVersion."
}

& (Join-Path $scriptRoot 'Verify-Release.ps1') -Version $Version

$releaseRoot = Join-Path $repoRoot 'release'
$buildRoot = Join-Path $repoRoot 'src\CodexConversationManager\bin\Release\net48'
$zipPath = Join-Path $releaseRoot "CodexConversationManager-Windows-v$Version.zip"
$stage = Join-Path $releaseRoot ('.stage-' + [Guid]::NewGuid().ToString('N'))
$candidateRoot = Join-Path $releaseRoot ('.candidate-' + [Guid]::NewGuid().ToString('N'))
$candidateZip = Join-Path $candidateRoot "CodexConversationManager-Windows-v$Version.zip"

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixedTimestamp = [DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $sourcePrefix = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\') + '\'
        $files = Get-ChildItem -LiteralPath $SourceRoot -File -Recurse | Sort-Object {
            $_.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
        }
        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
            $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp
            $input = [IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    & (Join-Path $scriptRoot 'build.ps1') -Configuration Release
    if (-not $SkipTests) {
        & (Join-Path $scriptRoot 'test.ps1') -NoBuild -SkipUiTests:$SkipUiTests
        & (Join-Path $scriptRoot 'test-providers.ps1') -NoBuild -SkipUiTests:$SkipUiTests
    }

    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $files = @(
        'CodexConversationManager.exe',
        'CodexConversationManager.exe.config',
        'CodexConversationManager.xaml',
        'DialogTheme.xaml',
        'backend.cjs',
        'packages.cjs'
    )
    foreach ($file in $files) {
        $source = Join-Path $buildRoot $file
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Required release file is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $file)
    }

    New-Item -ItemType Directory -Path (Join-Path $stage 'assets') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildRoot 'assets\app-icon.ico') -Destination (Join-Path $stage 'assets\app-icon.ico')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\Start.cmd') -Destination (Join-Path $stage 'Start.cmd')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'VERSION') -Destination (Join-Path $stage 'VERSION')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.zh-CN.md') -Destination (Join-Path $stage 'README.zh-CN.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE')

    New-Item -ItemType Directory -Path $candidateRoot -Force | Out-Null
    New-DeterministicZip -SourceRoot $stage -DestinationPath $candidateZip

    $zipHash = (Get-FileHash -LiteralPath $candidateZip -Algorithm SHA256).Hash
    $candidateHashFile = Join-Path $candidateRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($candidateHashFile, "$zipHash  $([IO.Path]::GetFileName($candidateZip))$([Environment]::NewLine)", [Text.UTF8Encoding]::new($false))

    # Validate the complete candidate before replacing the last known-good local release.
    & (Join-Path $scriptRoot 'Verify-Release.ps1') -Version $Version -PackagePath $candidateZip

    $hashFile = Join-Path $releaseRoot 'SHA256SUMS.txt'
    $previousZip = Join-Path $releaseRoot ('.previous-package-' + [Guid]::NewGuid().ToString('N') + '.zip')
    $previousHash = Join-Path $releaseRoot ('.previous-hash-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $hadCurrentZip = Test-Path -LiteralPath $zipPath
    try {
        if ($hadCurrentZip) {
            [IO.File]::Replace($candidateZip, $zipPath, $previousZip, $true)
        }
        else {
            Move-Item -LiteralPath $candidateZip -Destination $zipPath
        }
        try {
            if (Test-Path -LiteralPath $hashFile) {
                [IO.File]::Replace($candidateHashFile, $hashFile, $previousHash, $true)
            }
            else {
                Move-Item -LiteralPath $candidateHashFile -Destination $hashFile
            }
        }
        catch {
            if ($hadCurrentZip -and (Test-Path -LiteralPath $previousZip)) {
                [IO.File]::Replace($previousZip, $zipPath, $null, $true)
            }
            elseif (-not $hadCurrentZip -and (Test-Path -LiteralPath $zipPath)) {
                Remove-Item -LiteralPath $zipPath -Force
            }
            throw
        }

        # The verified current ZIP and checksum are now durable. Older versions
        # are removed only after that pair has been published successfully.
        foreach ($packagePattern in @(
            'CodexConversationManager-Windows-v*.zip',
            'CodexConversationMigrator-Windows-v*.zip'
        )) {
            Get-ChildItem -LiteralPath $releaseRoot -Filter $packagePattern -File |
                Where-Object { -not [string]::Equals($_.FullName, $zipPath, [StringComparison]::OrdinalIgnoreCase) } |
                Remove-Item -Force
        }
    }
    finally {
        foreach ($rollbackFile in @($previousZip, $previousHash)) {
            if (Test-Path -LiteralPath $rollbackFile) {
                Remove-Item -LiteralPath $rollbackFile -Force
            }
        }
    }

    & (Join-Path $scriptRoot 'Verify-Release.ps1') -Version $Version -PackagePath $zipPath

    Write-Host "Created $zipPath"
    Write-Host "SHA-256 $zipHash"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        if (-not [IO.Path]::GetFullPath($stage).StartsWith(([IO.Path]::GetFullPath($releaseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $stage" }
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    if (Test-Path -LiteralPath $candidateRoot) {
        if (-not [IO.Path]::GetFullPath($candidateRoot).StartsWith(([IO.Path]::GetFullPath($releaseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $candidateRoot" }
        Remove-Item -LiteralPath $candidateRoot -Recurse -Force
    }
}
