[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$SkipUiTests
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$app = Join-Path $repoRoot 'src\CodexConversationManager\bin\Release\net48\CodexConversationManager.exe'
$artifactRoot = Join-Path $repoRoot 'artifacts\test'
$temporaryCodexHome = Join-Path ([IO.Path]::GetTempPath()) ('codex-migrator-test-home-' + [Guid]::NewGuid().ToString('N'))
$previousCodexHome = $env:CODEX_HOME

function Invoke-MigratorTest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [string]$ExpectedOutput
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $app
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = Split-Path -Parent $app
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process.WaitForExit(180000)) {
        try { $process.Kill() } catch {}
        throw "$Name timed out after 180 seconds."
    }
    if ($process.ExitCode -ne 0) {
        throw "$Name failed with exit code $($process.ExitCode)."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedOutput)) {
        if (-not (Test-Path -LiteralPath $ExpectedOutput -PathType Leaf)) {
            throw "$Name did not create its expected output: $ExpectedOutput"
        }
        if ((Get-Item -LiteralPath $ExpectedOutput).Length -eq 0) {
            throw "$Name created an empty output: $ExpectedOutput"
        }
    }
    Write-Host "$Name passed."
}
function Initialize-TestCodexHome {
    param([Parameter(Mandatory = $true)][string]$CodexHome)

    $encoding = [Text.UTF8Encoding]::new($false)
    $projectPath = Join-Path $CodexHome 'sample-project'
    $sessionPath = Join-Path $CodexHome 'sessions\2026\08\28'
    New-Item -ItemType Directory -Path (Join-Path $projectPath 'src') -Force | Out-Null
    New-Item -ItemType Directory -Path $sessionPath -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $projectPath 'src\sample.txt'), 'Synthetic render-test project payload.', $encoding)

    $mainId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $subagentId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    $mainMeta = @{
        timestamp = '2026-08-28T03:00:00Z'
        type = 'session_meta'
        payload = @{
            id = $mainId
            timestamp = '2026-08-28T03:00:00Z'
            cwd = $projectPath
            originator = 'codex-desktop'
            cli_version = 'render-test'
            source = 'cli'
            model_provider = 'openai'
            history_mode = 'legacy'
        }
    }
    $mainUser = @{
        timestamp = '2026-08-28T03:00:01Z'
        type = 'response_item'
        payload = @{
            type = 'message'
            role = 'user'
            content = @(@{ type = 'input_text'; text = '检查项目备份与对话管理界面' })
        }
    }
    $mainAssistant = @{
        timestamp = '2026-08-28T03:00:02Z'
        type = 'response_item'
        payload = @{
            type = 'message'
            role = 'assistant'
            content = @(@{ type = 'output_text'; text = '这是用于本地截图回归的合成回答。' })
        }
    }
    $subagentMeta = @{
        timestamp = '2026-08-28T03:01:00Z'
        type = 'session_meta'
        payload = @{
            id = $subagentId
            session_id = $mainId
            parent_thread_id = $mainId
            thread_source = 'subagent'
            timestamp = '2026-08-28T03:01:00Z'
            cwd = $projectPath
            originator = 'codex-desktop'
            cli_version = 'render-test'
            source = @{ subagent = @{ thread_spawn = @{ parent_thread_id = $mainId } } }
            model_provider = 'openai'
            history_mode = 'legacy'
        }
    }
    $subagentUser = @{
        timestamp = '2026-08-28T03:01:01Z'
        type = 'response_item'
        payload = @{
            type = 'message'
            role = 'user'
            content = @(@{ type = 'input_text'; text = '检查子代理对话、大小、路径和批量选择' })
        }
    }
    $subagentAssistant = @{
        timestamp = '2026-08-28T03:01:02Z'
        type = 'response_item'
        payload = @{
            type = 'message'
            role = 'assistant'
            content = @(@{ type = 'output_text'; text = '子代理合成记录已准备。' })
        }
    }

    $mainFile = Join-Path $sessionPath "rollout-2026-08-28T03-00-00-$mainId.jsonl"
    $subagentFile = Join-Path $sessionPath "rollout-2026-08-28T03-01-00-$subagentId.jsonl"
    [IO.File]::WriteAllLines($mainFile, @(
        ($mainMeta | ConvertTo-Json -Depth 20 -Compress),
        ($mainUser | ConvertTo-Json -Depth 20 -Compress),
        ($mainAssistant | ConvertTo-Json -Depth 20 -Compress)
    ), $encoding)
    [IO.File]::WriteAllLines($subagentFile, @(
        ($subagentMeta | ConvertTo-Json -Depth 20 -Compress),
        ($subagentUser | ConvertTo-Json -Depth 20 -Compress),
        ($subagentAssistant | ConvertTo-Json -Depth 20 -Compress)
    ), $encoding)
}

function Add-LongConversationFixture {
    param([Parameter(Mandatory = $true)][string]$CodexHome)

    $encoding = [Text.UTF8Encoding]::new($false)
    $mainId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $mainFile = Join-Path $CodexHome "sessions\2026\08\28\rollout-2026-08-28T03-00-00-$mainId.jsonl"
    $lines = [Collections.Generic.List[string]]::new()
    $longBody = '超长单条消息开始' + [Environment]::NewLine + (($(
        for ($lineNumber = 1; $lineNumber -le 450; $lineNumber++) {
            "第 $lineNumber 行：用于验证一条消息超过窗口高度时仍可逐像素滚动查看。"
        }
    )) -join [Environment]::NewLine) + [Environment]::NewLine + '超长单条消息结束'
    for ($ordinal = 3; $ordinal -le 1305; $ordinal++) {
        $isUser = ($ordinal % 2) -eq 1
        $message = @{
            timestamp = ([DateTime]'2026-08-28T03:00:00Z').AddSeconds($ordinal).ToString('yyyy-MM-ddTHH:mm:ssZ')
            type = 'response_item'
            payload = @{
                type = 'message'
                role = $(if ($isUser) { 'user' } else { 'assistant' })
                content = @(@{
                    type = $(if ($isUser) { 'input_text' } else { 'output_text' })
                    text = $(if ($ordinal -eq 3) { $longBody } else { "长对话消息 $ordinal" })
                })
            }
        }
        $lines.Add(($message | ConvertTo-Json -Depth 20 -Compress))
    }
    [IO.File]::AppendAllLines($mainFile, $lines, $encoding)
}

try {
    if (-not $NoBuild) {
        & (Join-Path $scriptRoot 'build.ps1') -Configuration Release
    }
    if (-not (Test-Path -LiteralPath $app)) {
        throw "Built application was not found: $app"
    }

    if (Test-Path -LiteralPath $artifactRoot) {
        if (-not [IO.Path]::GetFullPath($artifactRoot).StartsWith(([IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $artifactRoot" }
        Remove-Item -LiteralPath $artifactRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
	Initialize-TestCodexHome -CodexHome $temporaryCodexHome
    New-Item -ItemType Directory -Path $temporaryCodexHome -Force | Out-Null
    $env:CODEX_HOME = $temporaryCodexHome

    $zhReport = Join-Path $artifactRoot 'selftest-zh-CN.txt'
    $enReport = Join-Path $artifactRoot 'selftest-en-US.txt'
    $zhChrome = Join-Path $artifactRoot 'chrome-zh-CN.txt'
    $enChrome = Join-Path $artifactRoot 'chrome-en-US.txt'

    Invoke-MigratorTest "--self-test --language zh-CN --report `"$zhReport`"" 'Chinese self-test' $zhReport
    Invoke-MigratorTest "--self-test --language en-US --report `"$enReport`"" 'English self-test' $enReport
    if ($SkipUiTests) {
        Write-Host 'Desktop-dependent window and render tests skipped by explicit request.'
        return
    }
    Invoke-MigratorTest "--chrome-test `"$zhChrome`" --language zh-CN" 'Chinese window chrome test' $zhChrome
    Invoke-MigratorTest "--chrome-test `"$enChrome`" --language en-US" 'English window chrome test' $enChrome

    $renderCases = @(
        [pscustomobject]@{ File = 'render-default-backup-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese default conversation-backup render test' },
        [pscustomobject]@{ File = 'render-project-backup-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese project-backup render test' },
        [pscustomobject]@{ File = 'render-conversation-backup-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese conversation-backup render test' },
        [pscustomobject]@{ File = 'render-main-selection-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese main-selection render test' },
        [pscustomobject]@{ File = 'render-subagent-selection-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese subagent-selection render test' },
        [pscustomobject]@{ File = 'render-import-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese conversation-only import render test' },
        [pscustomobject]@{ File = 'render-import-progress-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese import-progress render test' },
        [pscustomobject]@{ File = 'render-preview-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese conversation-preview render test' },
        [pscustomobject]@{ File = 'render-preview-responsive-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese responsive conversation-preview render test' },
        [pscustomobject]@{ File = 'render-preview-main-window-max-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese preview inside maximized main-window render test' },
        [pscustomobject]@{ File = 'render-preview-subagent-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese subagent-preview render test' },
        [pscustomobject]@{ File = 'render-project-backup-compact-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese compact-window render test' },
        [pscustomobject]@{ File = 'render-import-project-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese import render test' },
        [pscustomobject]@{ File = 'render-import-project-compact-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese compact import render test' },
        [pscustomobject]@{ File = 'render-dialog-theme-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese dialog-theme render test' },
        [pscustomobject]@{ File = 'render-paginated-completion-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese paginated-completion render test' },
        [pscustomobject]@{ File = 'render-default-backup-en-US.png'; Language = 'en-US'; Name = 'English default conversation-backup render test' },
        [pscustomobject]@{ File = 'render-project-backup-en-US.png'; Language = 'en-US'; Name = 'English project-backup render test' },
        [pscustomobject]@{ File = 'render-project-backup-compact-en-US.png'; Language = 'en-US'; Name = 'English compact-window render test' },
        [pscustomobject]@{ File = 'render-conversation-backup-en-US.png'; Language = 'en-US'; Name = 'English conversation-backup render test' },
        [pscustomobject]@{ File = 'render-main-selection-en-US.png'; Language = 'en-US'; Name = 'English main-selection render test' },
        [pscustomobject]@{ File = 'render-subagent-selection-en-US.png'; Language = 'en-US'; Name = 'English subagent-selection render test' },
        [pscustomobject]@{ File = 'render-import-en-US.png'; Language = 'en-US'; Name = 'English conversation-only import render test' },
        [pscustomobject]@{ File = 'render-preview-en-US.png'; Language = 'en-US'; Name = 'English conversation-preview render test' },
        [pscustomobject]@{ File = 'render-preview-responsive-en-US.png'; Language = 'en-US'; Name = 'English responsive conversation-preview render test' },
        [pscustomobject]@{ File = 'render-preview-main-window-max-en-US.png'; Language = 'en-US'; Name = 'English preview inside maximized main-window render test' },
        [pscustomobject]@{ File = 'render-import-project-en-US.png'; Language = 'en-US'; Name = 'English import render test' },
        [pscustomobject]@{ File = 'render-dialog-theme-en-US.png'; Language = 'en-US'; Name = 'English dialog-theme render test' },
        [pscustomobject]@{ File = 'render-paginated-completion-en-US.png'; Language = 'en-US'; Name = 'English paginated-completion render test' }
    )
    foreach ($renderCase in $renderCases) {
        $renderPath = Join-Path $artifactRoot $renderCase.File
        Invoke-MigratorTest "--render-test `"$renderPath`" --language $($renderCase.Language)" $renderCase.Name $renderPath
        $png = [IO.File]::ReadAllBytes($renderPath)
        if ($png.Length -lt 8 -or $png[0] -ne 137 -or $png[1] -ne 80 -or $png[2] -ne 78 -or $png[3] -ne 71) {
            throw "$($renderCase.Name) did not create a valid PNG."
        }
    }

    Add-LongConversationFixture -CodexHome $temporaryCodexHome
    $longRenderCases = @(
        [pscustomobject]@{ File = 'render-preview-long-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese complete long-conversation render test' },
        [pscustomobject]@{ File = 'render-preview-long-rail-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese user-message navigation rail render test' },
        [pscustomobject]@{ File = 'render-preview-long-main-window-max-zh-CN.png'; Language = 'zh-CN'; Name = 'Chinese long preview inside maximized main-window render test' },
        [pscustomobject]@{ File = 'render-preview-long-en-US.png'; Language = 'en-US'; Name = 'English complete long-conversation render test' },
        [pscustomobject]@{ File = 'render-preview-long-rail-en-US.png'; Language = 'en-US'; Name = 'English user-message navigation rail render test' },
        [pscustomobject]@{ File = 'render-preview-long-main-window-max-en-US.png'; Language = 'en-US'; Name = 'English long preview inside maximized main-window render test' }
    )
    foreach ($renderCase in $longRenderCases) {
        $renderPath = Join-Path $artifactRoot $renderCase.File
        Invoke-MigratorTest "--render-test `"$renderPath`" --language $($renderCase.Language)" $renderCase.Name $renderPath
        $png = [IO.File]::ReadAllBytes($renderPath)
        if ($png.Length -lt 8 -or $png[0] -ne 137 -or $png[1] -ne 80 -or $png[2] -ne 78 -or $png[3] -ne 71) {
            throw "$($renderCase.Name) did not create a valid PNG."
        }
    }

    $errorReports = @(Get-ChildItem -LiteralPath $artifactRoot -Filter '*.error.txt' -File)
    if ($errorReports.Count -gt 0) {
        throw "Render tests produced error reports: $($errorReports.Name -join ', ')"
    }
}
finally {
    $env:CODEX_HOME = $previousCodexHome
    if (Test-Path -LiteralPath $temporaryCodexHome) {
        if (-not [IO.Path]::GetFullPath($temporaryCodexHome).StartsWith(([IO.Path]::GetFullPath(([IO.Path]::GetTempPath())).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $temporaryCodexHome" }
        Remove-Item -LiteralPath $temporaryCodexHome -Recurse -Force
    }
}
