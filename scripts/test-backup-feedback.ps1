# Replays captured activity on a private desktop; the source repository is read only.
param(
    [Parameter(Mandatory)][string]$FixtureRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [switch]$Baseline,
    [switch]$Worker,
    [string]$ArtifactDirectory
)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/backup-feedback-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
    if ($FixtureRoot -eq $SourceRoot -or $FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable UI fixture, separate from the source repository.' }
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -SourceRoot '" + $SourceRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    if ($Baseline) { $command += ' -Baseline' }
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-backup-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(150)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw "Backup feedback test timed out. See $ArtifactDirectory" } }
        if ($desktop.ExitCode -ne 0) { throw "Backup feedback test failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Backup feedback. Artifacts: $ArtifactDirectory"
    } finally { $desktop.Dispose() }
    return
}
. "$PSScriptRoot/ui-test-report.ps1"
. "$PSScriptRoot/ui-automation.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$env:SG_UI_TEST_DIRECTORY = Join-Path $ArtifactDirectory 'settings'
$env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $ArtifactDirectory 'webview'
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 1; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
$records = @(Get-ChildItem -LiteralPath (Join-Path $SourceRoot '.sg/operations') -Filter '*.json' | ForEach-Object {
    Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
})
if (!$records.Count) { throw 'The source must have saved activity to replay.' }
# Capture only presentation fields; no source repository or remote is opened by the app.
@($records | Select-Object kind,branch,steps,detail) | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'task-replay.json')
function Find([string]$id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Wait-For([string]$description, [scriptblock]$read, [int]$seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $description"
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
$process = $null; $window = $null; $originalConfig = $null
Initialize-UiReport $ArtifactDirectory 'Backup feedback'
try {
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For 'debug window' { Get-TestAppWindow $process }
    $null = Wait-For 'replay starts' { Test-Path -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'task-replay-started.txt') }
    $activeId = Get-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'task-replay-started.txt')
    Start-UiScenario 'Navigation stays responsive with full task history and continuous captured output'
    Invoke-Element (Find 'TaskQueueToggle')
    $null = Wait-For 'history is populated' { (Find 'TaskFilterSummary').Current.Name -eq '97 of 97 tasks' }
    $samples = @()
    for ($i = 0; $i -lt 3; $i++) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Select-Element (Find 'SettingsItem')
        $null = Wait-For 'settings visible while output streams' { $t = Find 'MinLength'; if ($t -and !$t.Current.IsOffscreen) { $t } }
        $samples += $watch.ElapsedMilliseconds
        Invoke-Element (Find 'NavigationViewBackButton')
        $null = Wait-For 'overview returns while output streams' { Find 'SyncButton' }
    }
    @{ navigationMilliseconds = $samples; records = $records.Count; retainedTasks = 97 } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'timings.json')
    if (($samples | Measure-Object -Maximum).Maximum -gt 3000) { throw "Navigation stalled under progress load: $samples ms" }
    Select-Element (Find 'SettingsItem')
    $null = Wait-For 'next interval shown' { (Find 'BackupNextRun').Current.HelpText }
    if (!$Baseline) {
        Start-UiScenario 'Skipped backup explains the blocker and opens its task'
        (Find 'TaskFilter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Select-Element (Wait-For 'finished tasks filter' { Find 'TaskFilter3' })
        $null = Wait-For 'scheduled backup skipped while replay is active' { (Find 'BackupSkippedReason').Current.Name } 80
        $reason = (Find 'BackupSkippedReason').Current.Name
        if ($reason -notlike '*Replay:*') { throw 'Skipped backup does not name the blocking task.' }
        Invoke-Element (Find 'NavigationViewBackButton')
        Invoke-Element (Wait-For 'backup action on overview' { Find 'BackupButton' })
        $null = Wait-For 'same skip visible on Backup' {
            $scroller = Find 'ContentScroll'; $scroll = $null
            if ($scroller -and $scroller.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 100) }
            $t = Find 'BackupSkippedReason'; if ($t -and !$t.Current.IsOffscreen -and $t.Current.Name -like '*Replay:*') { $t }
        }
        Invoke-Element (Find 'BackupBlockingTask')
        $null = Wait-For 'blocking task details visible' { $b = Find ('CopyTask_' + $activeId); if ($b -and !$b.Current.IsOffscreen) { $b } }
        Start-Sleep -Milliseconds 400
        Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-skipped.png')
    }
    Invoke-Element (Find ('CancelTask_' + $activeId))
    $null = Wait-For 'replay cancellation remains responsive' { (Find ('Task_' + $activeId)).Current.Name -like '*Cancelled*' }
    if (!$Baseline) {
        Start-UiScenario 'Finishing a blocker does not trigger an extra backup'
        $until = [DateTime]::UtcNow.AddSeconds(10)
        do {
            if ((Find 'TaskQueueSummary').Current.Name -notlike '*0 active*') { throw 'An operation started before the next interval.' }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $until)
        Select-Element (Find 'SettingsItem')
        (Wait-For 'backup interval setting' { Find 'BackupMinutes' }).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(0)
        $null = Wait-For 'automatic backups off' { (Find 'BackupNextRun').Current.Name -eq 'Automatic backups are off' }
        Invoke-Element (Find 'ClearFinishedTasks')
        $null = Wait-For 'cleared blocker has no dead link' { $b = Find 'BackupBlockingTask'; !$b -or $b.Current.IsOffscreen }

        Start-UiScenario 'Backup read failure retains useful content and retry recovers'
        # Restart with a missing local remote, then create it to exercise Retry. Only the disposable
        # fixture's config changes, and it is restored even when an assertion fails.
        Stop-Process -Id $process.Id; $process.WaitForExit(); $process = $null
        Remove-Item -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'task-replay.json')
        $configPath = Join-Path $FixtureRoot '.sg/sg.json'
        $originalConfig = [IO.File]::ReadAllText($configPath)
        $fixtureConfig = $originalConfig | ConvertFrom-Json
        $localRemote = [IO.Path]::GetFullPath($fixtureConfig.backup.url)
        $fixtureParent = [IO.Path]::GetFullPath((Split-Path $FixtureRoot)) + [IO.Path]::DirectorySeparatorChar
        if (!$localRemote.StartsWith($fixtureParent, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $localRemote -PathType Container)) { throw 'Retry test requires a local fixture remote.' }
        $retryRemote = Join-Path $FixtureRoot ('retry-remote-' + [Guid]::NewGuid().ToString('N') + '.git')
        $fixtureConfig.backup.url = $retryRemote
        $fixtureConfig | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath
        $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
        $window = Wait-For 'restarted debug window' { Get-TestAppWindow $process }
        Invoke-Element (Wait-For 'backup action' { Find 'BackupButton' })
        $null = Wait-For 'backup read error' { $b = Find 'BackupRetryRead'; if ($b -and !$b.Current.IsOffscreen) { $b } }
        if ((Find 'NameBox') -and !(Find 'NameBox').Current.IsOffscreen) { throw 'Restore form is visible before a backup can be selected.' }
        $null = Wait-For 'saved backup time survives read error' { (Find 'BackupNextRun').Current.Name -eq 'Automatic backups are off' }
        Start-Sleep -Milliseconds 500
        Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-read-error.png')
        & git clone --bare --quiet -- $localRemote $retryRemote
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the local retry remote.' }
        Invoke-Element (Find 'BackupRetryRead')
        $null = Wait-For 'retry loads worktrees' { $b = Find 'BranchesHeader'; if ($b -and !$b.Current.IsOffscreen -and $b.Current.Name -like 'Worktree*') { $b } } 40
        if ((Find 'NameBox') -and !(Find 'NameBox').Current.IsOffscreen) { throw 'Backup selected a restore destination without an explicit choice.' }
        Invoke-Element (Find 'BackupWorktreeOpen_source')
        $null = Wait-For 'restore form follows selection' { $b = Find 'NameBox'; if ($b -and !$b.Current.IsOffscreen) { $b } }
        if ((Find 'BackupRetryRead') -and !(Find 'BackupRetryRead').Current.IsOffscreen) { throw 'Retry error remained visible after a successful read.' }
    }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($originalConfig) { [IO.File]::WriteAllText((Join-Path $FixtureRoot '.sg/sg.json'), $originalConfig) }
}
