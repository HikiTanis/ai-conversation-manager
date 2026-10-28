[CmdletBinding()]
param([switch]$SkipBuild, [string]$NodePath)
$ErrorActionPreference = 'Stop'
$desktopRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $desktopRoot
$config = Get-Content (Join-Path $desktopRoot 'src-tauri/tauri.conf.json') -Raw | ConvertFrom-Json
$version = $config.version
$releaseRoot = Join-Path $repoRoot "release/v$version"
$engine = Join-Path $desktopRoot 'src-tauri/engine'
$nodeRuntime = Join-Path $engine 'runtime/node.exe'
$nodeLicense = Join-Path $engine 'runtime/LICENSE-node.txt'
if ($NodePath -or !(Test-Path -LiteralPath $nodeRuntime) -or !(Test-Path -LiteralPath $nodeLicense)) {
    if (!$NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
    & $NodePath -e "process.exit(process.arch==='x64' && typeof require('node:zlib').zstdDecompressSync==='function' ? 0 : 1)"
    if ($LASTEXITCODE -ne 0) { throw 'Node must be x64 and support Zstandard.' }
    $nodeVersion = (& $NodePath --version).Trim()
    if ($nodeVersion -notmatch '^v\d+\.\d+\.\d+$') { throw 'Unsupported Node version string.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $nodeRuntime) -Force | Out-Null
    $licenseCandidate = $nodeLicense + '.download'
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/nodejs/node/$nodeVersion/LICENSE" -OutFile $licenseCandidate
    if ((Get-Item -LiteralPath $licenseCandidate).Length -lt 1000) { throw 'Node license download is incomplete.' }
    if ([IO.Path]::GetFullPath($NodePath) -ne [IO.Path]::GetFullPath($nodeRuntime)) { Copy-Item -LiteralPath $NodePath -Destination $nodeRuntime -Force }
    Move-Item -LiteralPath $licenseCandidate -Destination $nodeLicense -Force
}
& $nodeRuntime -e "process.exit(process.arch==='x64' && typeof require('node:zlib').zstdDecompressSync==='function' ? 0 : 1)"
if ($LASTEXITCODE -ne 0) { throw 'Bundled Node runtime must be x64 and support Zstandard.' }
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
foreach ($name in @('CodexConversationManager.exe','CodexConversationManager.exe.config','backend.cjs','packages.cjs')) {
    if (!(Test-Path -LiteralPath (Join-Path $engine $name))) { throw "Missing engine file: $name" }
}
$binary = Join-Path $desktopRoot 'src-tauri/target/release/conversation-manager-desktop.exe'
if (!(Test-Path -LiteralPath $binary)) { throw 'Build the desktop application before packaging.' }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Push-Location $desktopRoot
try {
    npx tauri bundle --bundles nsis --config src-tauri/tauri.package.conf.json --no-binary-patching
    if ($LASTEXITCODE -ne 0) { throw 'NSIS installer packaging failed.' }
} finally { Pop-Location }
$installer = Join-Path $desktopRoot "src-tauri/target/release/bundle/nsis/$($config.productName)_${version}_x64-setup.exe"
if (!(Test-Path -LiteralPath $installer)) { throw "Expected installer missing: $installer" }
$stage = Join-Path $releaseRoot ('.stage-' + [guid]::NewGuid().ToString('N'))
$folderName = "AI-Conversation-Manager-$version-Windows-x64"
$portable = Join-Path $stage $folderName
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $portable
Copy-Item -LiteralPath $engine -Destination (Join-Path $portable 'engine') -Recurse
Copy-Item -LiteralPath (Join-Path $desktopRoot 'packaging/Start.cmd') -Destination $portable
Copy-Item -LiteralPath (Join-Path $desktopRoot 'packaging/README.txt') -Destination $portable
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $portable 'LICENSE.txt')
$zip = Join-Path $releaseRoot "$folderName-Portable.zip"
$setup = Join-Path $releaseRoot "$folderName-Setup.exe"
Compress-Archive -LiteralPath $portable -DestinationPath $zip -CompressionLevel Optimal -Force
Copy-Item -LiteralPath $installer -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $desktopRoot 'packaging/README.txt') -Destination $releaseRoot -Force
$checksums = foreach ($file in @($setup, $zip)) { "$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))" }
$checksums | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
$notes = [IO.File]::ReadAllText((Join-Path $repoRoot "docs/releases/v$version.md"))
$updateAssets = foreach ($file in @($setup, $zip)) {
    $asset = Get-Item -LiteralPath $file
    @{
        name = $asset.Name
        browser_download_url = "https://github.com/HikiTanis/ai-conversation-manager/releases/download/v$version/$($asset.Name)"
        size = $asset.Length
        digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$updateManifest = @{tag_name="v$version"; draft=$false; prerelease=$false; body=$notes; published_at=[DateTime]::UtcNow.ToString('o'); assets=@($updateAssets)} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $releaseRoot 'update.json'), $updateManifest, [Text.UTF8Encoding]::new($false))
# Only delete the freshly generated staging folder inside this release directory.
$resolvedStage = [IO.Path]::GetFullPath($stage)
$releasePrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
if (!$resolvedStage.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging directory.' }
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Get-Item -LiteralPath $setup,$zip | Select-Object FullName,Length
