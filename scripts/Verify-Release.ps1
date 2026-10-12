[CmdletBinding()]
param(
    [string]$Version,
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$versionFile = Join-Path $repoRoot 'VERSION'

function Assert-FileContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description is missing: $Path"
    }
    $content = [IO.File]::ReadAllText($Path)
    if ($content.IndexOf($Text, [StringComparison]::Ordinal) -lt 0) {
        throw "$Description does not match VERSION ($Version): $Path"
    }
}

if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "VERSION is missing: $versionFile"
}

$canonicalVersion = [IO.File]::ReadAllText($versionFile).Trim()
if ($canonicalVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must use stable semantic version form MAJOR.MINOR.PATCH: $canonicalVersion"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $canonicalVersion
}
if (-not [string]::Equals($Version, $canonicalVersion, [StringComparison]::Ordinal)) {
    throw "Requested version $Version does not match VERSION $canonicalVersion."
}

$windowVersionText = 'Text="v' + $Version + '"'
$protocolVersionExpression = 'typeof(CodexAppServerThreadDeletion).Assembly.GetName().Version?.ToString(3)'
Assert-FileContains (Join-Path $repoRoot 'src\CodexConversationManager\CodexConversationManager.xaml') $windowVersionText 'Window version label'
Assert-FileContains (Join-Path $repoRoot 'src\CodexConversationManager\DeletionDialogs.cs') 'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DialogTheme.xaml")' 'Dialog theme resource path'
Assert-FileContains (Join-Path $repoRoot 'src\CodexConversationManager\CodexAppServerThreadDeletion.cs') $protocolVersionExpression 'Codex app-server client version source'
Assert-FileContains (Join-Path $repoRoot 'README.md') "CodexConversationManager-Windows-v$Version.zip" 'English download instructions'
Assert-FileContains (Join-Path $repoRoot 'README.zh-CN.md') "CodexConversationManager-Windows-v$Version.zip" 'Chinese download instructions'
Assert-FileContains (Join-Path $repoRoot 'README.md') 'Codex CLI 0.148.0 or later' 'English Codex CLI compatibility requirement'
Assert-FileContains (Join-Path $repoRoot 'README.zh-CN.md') 'Codex CLI 0.148.0 或更高版本' 'Chinese Codex CLI compatibility requirement'
Assert-FileContains (Join-Path $repoRoot 'CHANGELOG.md') "## [$Version]" 'Changelog release section'

$releaseNotes = Join-Path $repoRoot "docs\releases\v$Version.md"
if (-not (Test-Path -LiteralPath $releaseNotes)) {
    throw "Release notes are missing for VERSION $($Version): $releaseNotes"
}
Assert-FileContains $releaseNotes 'Codex CLI 0.148.0 or later' 'Release-note Codex CLI compatibility requirement'
Assert-FileContains (Join-Path $repoRoot '.github\workflows\release.yml') 'gh release list' 'Non-failing GitHub release existence check'

$projectFile = Join-Path $repoRoot 'src\CodexConversationManager\CodexConversationManager.csproj'
Assert-FileContains $projectFile '..\..\VERSION' 'MSBuild VERSION source'

$builtExe = Join-Path $repoRoot 'src\CodexConversationManager\bin\Release\net48\CodexConversationManager.exe'
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    if (-not (Test-Path -LiteralPath $builtExe)) {
        throw "Release executable is missing: $builtExe"
    }
    $builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($builtExe).FileVersion
    if (-not [string]::Equals($builtVersion, "$Version.0", [StringComparison]::Ordinal)) {
        throw "Built EXE version $builtVersion does not match VERSION $Version."
    }

    $resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $resolvedPackage)) {
        throw "Release ZIP is missing: $resolvedPackage"
    }
    $expectedPackageName = "CodexConversationManager-Windows-v$Version.zip"
    if (-not [string]::Equals([IO.Path]::GetFileName($resolvedPackage), $expectedPackageName, [StringComparison]::Ordinal)) {
        throw "Release ZIP name does not match VERSION: $resolvedPackage"
    }

    $releaseRoot = Split-Path -Parent $resolvedPackage
    $releaseZips = @(Get-ChildItem -LiteralPath $releaseRoot -Filter 'CodexConversationManager-Windows-v*.zip' -File)
    if ($releaseZips.Count -ne 1) {
        throw "Expected exactly one versioned release ZIP, found $($releaseZips.Count)."
    }

    $actualHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
    $hashFile = Join-Path $releaseRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $hashFile)) {
        throw "SHA256SUMS.txt is missing: $hashFile"
    }
    $expectedHashLine = "$actualHash  $expectedPackageName"
    $hashLines = @(Get-Content -LiteralPath $hashFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($hashLines.Count -ne 1 -or -not [string]::Equals($hashLines[0].Trim(), $expectedHashLine, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA256SUMS.txt does not match the release ZIP."
    }

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('codex-migrator-release-verify-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $temporaryRoot

        $requiredFiles = @(
            'CodexConversationManager.exe',
            'CodexConversationManager.exe.config',
            'CodexConversationManager.xaml',
            'DialogTheme.xaml',
            'backend.cjs',
            'packages.cjs',
            'assets\app-icon.ico',
            'Start.cmd',
            'VERSION',
            'README.md',
            'README.zh-CN.md',
            'LICENSE'
        )
        foreach ($relativePath in $requiredFiles) {
            $candidate = Join-Path $temporaryRoot $relativePath
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                throw "Release ZIP is missing required file: $relativePath"
            }
        }

        $actualFiles = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Recurse | ForEach-Object {
            $_.FullName.Substring($temporaryRoot.Length + 1).Replace('/', '\')
        })
        $unexpectedFiles = @($actualFiles | Where-Object { $requiredFiles -notcontains $_ })
        if ($actualFiles.Count -ne $requiredFiles.Count -or $unexpectedFiles.Count -gt 0) {
            throw "Release ZIP must contain only the portable application and concise user documentation. Unexpected files: $($unexpectedFiles -join ', ')"
        }

        $forbiddenCctEntries = @(Get-ChildItem -LiteralPath $temporaryRoot -Force -Recurse | Where-Object {
            $relativePath = $_.FullName.Substring($temporaryRoot.Length + 1).Replace('/', '\')
            $_.Name -ieq 'cct.exe' -or
            [string]::Equals($relativePath, 'third_party\cct', [StringComparison]::OrdinalIgnoreCase) -or
            $relativePath.StartsWith('third_party\cct\', [StringComparison]::OrdinalIgnoreCase)
        })
        if ($forbiddenCctEntries.Count -gt 0) {
            $forbiddenPaths = ($forbiddenCctEntries | ForEach-Object {
                $_.FullName.Substring($temporaryRoot.Length + 1)
            }) -join ', '
            throw "Release ZIP must not contain the retired cct dependency: $forbiddenPaths"
        }

        $packagedExe = Join-Path $temporaryRoot 'CodexConversationManager.exe'
        $packagedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($packagedExe).FileVersion
        if (-not [string]::Equals($packagedVersion, "$Version.0", [StringComparison]::Ordinal)) {
            throw "Packaged EXE version $packagedVersion does not match VERSION $Version."
        }
        $binaryText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($packagedExe))
        $absolutePdb = [regex]::Match($binaryText, '[A-Za-z]:[\\/][^\x00\r\n]{1,300}\.pdb', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($absolutePdb.Success) {
            throw "Packaged EXE exposes an absolute PDB path: $($absolutePdb.Value)"
        }

        $markdownFiles = Get-ChildItem -LiteralPath $temporaryRoot -Filter '*.md' -File -Recurse
        foreach ($markdownFile in $markdownFiles) {
            $markdown = [IO.File]::ReadAllText($markdownFile.FullName)
            foreach ($match in [regex]::Matches($markdown, '\[[^\]]+\]\(([^)]+)\)')) {
                $target = $match.Groups[1].Value.Trim()
                if ($target -match '^(https?://|mailto:|#)') {
                    continue
                }
                if ($target.StartsWith('<') -and $target.EndsWith('>')) {
                    $target = $target.Substring(1, $target.Length - 2)
                }
                $target = ($target -split '#', 2)[0]
                if ([string]::IsNullOrWhiteSpace($target)) {
                    continue
                }
                $target = [Uri]::UnescapeDataString($target).Replace('/', [IO.Path]::DirectorySeparatorChar)
                $resolvedLink = [IO.Path]::GetFullPath((Join-Path $markdownFile.DirectoryName $target))
                if (-not (Test-Path -LiteralPath $resolvedLink)) {
                    $markdownRelative = $markdownFile.FullName.Substring($temporaryRoot.Length + 1)
                    throw "Broken packaged Markdown link: $markdownRelative -> $target"
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) {
            if (-not [IO.Path]::GetFullPath($temporaryRoot).StartsWith(([IO.Path]::GetFullPath(([IO.Path]::GetTempPath())).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $temporaryRoot" }
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}

Write-Host "Release verification passed for v$Version."
