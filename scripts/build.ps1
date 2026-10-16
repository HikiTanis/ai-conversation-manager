[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'src/CodexConversationManager'
$project = Join-Path $source 'CodexConversationManager.csproj'
$localDotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$sdks = if (Get-Command $dotnet -ErrorAction SilentlyContinue) { & $dotnet --list-sdks }
if ($sdks) {
    & $dotnet build $project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
} else {
    $compiler = Join-Path $repoRoot '.tools/compiler/tasks/net472/csc.exe'
    if (!(Test-Path -LiteralPath $compiler)) { throw 'Install the .NET SDK or extract Microsoft.Net.Compilers.Toolset 4.12.0 to .tools/compiler.' }
    $framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
    $output = Join-Path $source "bin/$Configuration/net48"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $refs = @('System.dll','System.Core.dll','System.Web.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Microsoft.VisualBasic.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')
    $arguments = @('/nologo','/target:winexe','/platform:x64','/langversion:12','/unsafe+','/optimize+','/utf8output','/main:CodexConversationManager.Program',"/win32icon:$source/assets/app-icon.ico","/out:$output/CodexConversationManager.exe")
    $arguments += $refs | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    # The compiler-only path uses the same pinned packages as the SDK build.
    $packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $repoRoot '.tools/nuget' }
    $packageLibraries = @(
        'zstdsharp.port/0.8.8/lib/net462/ZstdSharp.dll',
        'system.memory/4.6.0/lib/net462/System.Memory.dll',
        'system.buffers/4.6.0/lib/net462/System.Buffers.dll',
        'system.numerics.vectors/4.6.0/lib/net462/System.Numerics.Vectors.dll',
        'system.runtime.compilerservices.unsafe/6.1.0/lib/net462/System.Runtime.CompilerServices.Unsafe.dll',
        'system.threading.tasks.extensions/4.5.4/lib/net461/System.Threading.Tasks.Extensions.dll'
    )
    foreach ($library in $packageLibraries) {
        $libraryPath = Join-Path $packageCache $library
        if (!(Test-Path -LiteralPath $libraryPath)) { throw "Missing dependency $library. Use the .NET SDK to restore packages before a compiler-only build." }
        $arguments += '/reference:' + $libraryPath
        Copy-Item -LiteralPath $libraryPath -Destination $output -Force
    }
    $arguments += Get-ChildItem -LiteralPath $source -Filter '*.cs' | Select-Object -ExpandProperty FullName
    $arguments += Get-ChildItem -LiteralPath (Join-Path $source 'Providers') -Filter '*.cs' -Recurse | Select-Object -ExpandProperty FullName
    $arguments += Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests/ui') -Filter '*.cs' | Select-Object -ExpandProperty FullName
    $version = ([IO.File]::ReadAllText((Join-Path $repoRoot 'VERSION'))).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid VERSION.' }
    $assemblyInfo = Join-Path $output 'GeneratedAssemblyInfo.cs'
    [IO.File]::WriteAllText($assemblyInfo, "[assembly: System.Reflection.AssemblyVersion(`"$version.0`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$version.0`")]`n[assembly: System.Reflection.AssemblyInformationalVersion(`"$version`")]`n[assembly: System.Reflection.AssemblyTitle(`"Codex Conversation Manager`")]`n[assembly: System.Reflection.AssemblyProduct(`"Codex Conversation Manager`")]", [Text.UTF8Encoding]::new($false))
    $arguments += $assemblyInfo
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    Copy-Item -LiteralPath "$source/CodexConversationManager.xaml","$source/DialogTheme.xaml","$repoRoot/adapters/dsh/backend.cjs","$repoRoot/adapters/dsh/packages.cjs" -Destination $output -Force
    Copy-Item -LiteralPath "$source/assets" -Destination $output -Recurse -Force
    Copy-Item -LiteralPath "$source/App.config" -Destination "$output/CodexConversationManager.exe.config" -Force
    Copy-Item -LiteralPath "$repoRoot/THIRD-PARTY-NOTICES.txt" -Destination $output -Force
}
Write-Host "Built: $source/bin/$Configuration/net48/CodexConversationManager.exe"
