# Uses captured presentation data only, inside a disposable fixture on a private desktop.
param([Parameter(Mandatory)][string]$FixtureRoot, [Parameter(Mandatory)][string]$SourceRoot,
    [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
    if ($FixtureRoot -eq $SourceRoot -or $FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable workflow fixture, separate from the source.' }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/browsing-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -SourceRoot '" + $SourceRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-browse-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(360)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw "Browsing test timed out. See $ArtifactDirectory" } }
        if ($desktop.ExitCode -ne 0) { throw "Browsing test failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Browsing. Artifacts: $ArtifactDirectory"
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
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
function Find([string]$id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Wait-For([string]$description, [scriptblock]$read, [int]$seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do { $v = & $read; if ($v) { return $v }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $description"
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Set-Field([string]$id, [string]$value) { (Wait-For "$id input" { Find $id }).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Entries {
    @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Steps and checkpoint'),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true))))
}
function Backup-Cards {
    @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.AutomationId -like 'BackupWorktree_*' })
}
function Filter-Backups([string]$label) {
    (Find 'BackupFilter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $option = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $label),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)))
    Select-Element $option
}
function Update-FixtureRefs([string[]]$commands) {
    # Git's text protocol requires LF; the Windows PowerShell native pipeline adds CRLF.
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($arg in @('-C', $localRemote, 'update-ref', '--stdin')) { $start.ArgumentList.Add($arg) }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardOutput = $true
    $git = [Diagnostics.Process]::Start($start)
    try {
        $errorRead = $git.StandardError.ReadToEndAsync(); $outputRead = $git.StandardOutput.ReadToEndAsync()
        $git.StandardInput.Write(($commands -join "`n") + "`n"); $git.StandardInput.Close()
        $git.WaitForExit()
        $errorText = $errorRead.GetAwaiter().GetResult(); $null = $outputRead.GetAwaiter().GetResult()
        if ($git.ExitCode -ne 0) { throw "Fixture ref update failed: $errorText" }
    } finally { $git.Dispose() }
}
$process = $null; $window = $null; $created = [Collections.Generic.List[string]]::new()
$originalConfig = $null; $listener = $null; $client = $null
$previewRef = $null; $localRemote = $null
$backupNames = [Collections.Generic.List[string]]::new()
Initialize-UiReport $ArtifactDirectory 'Browsing'
try {
    $fixtureConfig = Get-Content -Raw -LiteralPath (Join-Path $FixtureRoot '.sg/sg.json') | ConvertFrom-Json
    $localRemote = [IO.Path]::GetFullPath($fixtureConfig.backup.url)
    $fixtureParent = [IO.Path]::GetFullPath((Split-Path $FixtureRoot)) + [IO.Path]::DirectorySeparatorChar
    if (!$localRemote.StartsWith($fixtureParent, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $localRemote -PathType Container) -or $fixtureConfig.backup.prefix) { throw 'Preview test requires an unprefixed local fixture remote.' }
    $tip = (& git -C $localRemote rev-parse refs/heads/source).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Fixture needs its source backup.' }
    $previewName = 'preview-' + [Guid]::NewGuid().ToString('N')
    $previewRef = 'refs/heads/' + $previewName
    & git -C $localRemote update-ref $previewRef $tip
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the disposable preview ref.' }
    $records = @(Get-ChildItem -LiteralPath (Join-Path $SourceRoot '.sg/operations') -Filter '*.json' | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json })
    if (!$records.Count) { throw 'Source needs saved activity.' }
    $backupPrefix = 'zz-browse-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    for ($i = 0; $i -lt 180; $i++) {
        # Keep recognizable real names, but only alias disposable fixture commits. Never fetch source histories.
        $label = [regex]::Replace([string]$records[$i % $records.Count].branch, '[^a-zA-Z0-9_-]', '-')
        if ($label.Length -gt 120) { $label = $label.Substring(0, 120) }
        $backupNames.Add(('{0}-{1:d3}-{2}' -f $backupPrefix, $i, $label))
    }
    Update-FixtureRefs @($backupNames | ForEach-Object { "create refs/heads/$_ $tip" })
    for ($i = 0; $i -lt 240; $i++) {
        $sample = $records[$i % $records.Count]
        $id = [Guid]::NewGuid().ToString('N')
        $path = Join-Path $FixtureRoot ('.sg/operations/' + $id + '.json')
        $created.Add($path)
        # Paths and checkpoint refs are deliberately not copied from the user's workspace.
        @{ id = $id; kind = $sample.kind; branch = ($sample.branch + '-replay-' + $i); checkout = 'fixture'; path = '';
            before = ''; phase = $(if ($i -eq 239) { 7 } else { 8 }); steps = @($sample.steps); detail = $sample.detail;
            updated = [DateTimeOffset]::UtcNow.AddMinutes(-$i).ToString('O') } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path
    }
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For 'debug window' { Get-TestAppWindow $process }
    Start-UiScenario 'Large activity opens with a bounded first page'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Select-Element (Find 'ActivityItem')
    $null = Wait-For 'activity entries' { Entries }
    $count = (Entries).Count
    @{ activityMilliseconds = $watch.ElapsedMilliseconds; renderedEntries = $count; replayedRecords = 240 } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'timings.json')
    if ($count -ne 20) { throw "Activity created $count entries; expected a first page of 20." }
    $checkpoint = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Branch checkpoint: Not recorded')
    if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $checkpoint)) { throw 'Collapsed checkpoint details were rendered eagerly.' }
    (Entries)[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $null = Wait-For 'checkpoint details load on demand' { $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $checkpoint) }
    Start-UiScenario 'Search includes records beyond the first page and has an empty state'
    Set-Field 'ActivitySearch' 'replay-239'
    $null = Wait-For 'older matching record' { (Entries).Count -eq 1 -and (Find 'ActivityResults').Current.Name -like '*1 of 1*' }
    Set-Field 'ActivitySearch' 'no-record-matches-this-search'
    $null = Wait-For 'search empty state' { (Find 'ActivityResults').Current.Name -like 'No matching*' }
    if ((Entries).Count -ne 0) { throw 'Unmatched history remains visible.' }
    Set-Field 'ActivitySearch' ''
    $null = Wait-For 'first page restored' { (Entries).Count -eq 20 }
    Invoke-Element (Find 'ActivityMore')
    $null = Wait-For 'second page appended' { (Entries).Count -eq 40 }
    Start-UiScenario 'Activity restores search, loaded history, and scroll position on return'
    Set-Field 'ActivitySearch' 'replay-'
    $null = Wait-For 'filtered first page' { (Entries).Count -eq 20 }
    Invoke-Element (Find 'ActivityMore')
    $null = Wait-For 'filtered second page' { (Entries).Count -eq 40 }
    (Entries)[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $scrollParent = (Find 'ActivitySearch')
    $scroll = $null
    while ($scrollParent) {
        if ($scrollParent.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) { break }
        $scrollParent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($scrollParent)
    }
    if (!$scroll) { throw 'Activity needs a scrolling history.' }
    # Expansion can finish a bring-into-view request after UIA returns; wait for the requested scroll.
    $null = Wait-For 'activity scrolled before leaving' {
        $scroll.SetScrollPercent(-1, 60)
        Start-Sleep -Milliseconds 500 # Confirm the position survives the expander's deferred bring-into-view.
        $scroll.Current.VerticalScrollPercent -gt 50
    }
    $beforeScroll = $scroll.Current.VerticalScrollPercent
    Select-Element (Find 'SettingsItem')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'activity search retained on return' { $field = Find 'ActivitySearch'; $field -and $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'replay-' }
    $null = Wait-For 'loaded history retained' { (Entries).Count -eq 40 }
    if ((Entries)[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne 'Expanded') { throw 'Expanded activity details were lost on return.' }
    $scrollParent = Find 'ActivitySearch'; $scroll = $null
    while ($scrollParent) {
        if ($scrollParent.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) { break }
        $scrollParent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($scrollParent)
    }
    $null = Wait-For 'activity scroll retained' { $scroll.Current.VerticalScrollPercent -gt 50 }
    @{ before = $beforeScroll; after = $scroll.Current.VerticalScrollPercent } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'scroll.json')
    $scroll.SetScrollPercent(-1, 0)
    Set-Field 'ActivitySearch' ''
    (Find 'ActivityFilter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $attention = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Needs attention'))
    Select-Element $attention
    $null = Wait-For 'attention filter finds older unfinished record' { (Entries).Count -eq 1 }
    Invoke-Element (Find 'ActivityRefresh')
    $null = Wait-For 'refresh keeps the history filter' { (Entries).Count -eq 1 -and (Find 'ActivityResults').Current.Name -like '*1 of 1*' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'activity-filter.png')
    Start-UiScenario 'Large backup catalogs render in batches and retain their filter and loaded cards'
    Invoke-Element (Find 'NavigationViewBackButton')
    $watch.Restart()
    Invoke-Element (Wait-For 'backup action' { Find 'BackupButton' })
    $null = Wait-For 'backup entries loaded' { $field = Find 'BackupSearch'; if ($field -and !$field.Current.IsOffscreen) { $field } }
    $null = Wait-For 'bounded first backup page' { (Backup-Cards).Count -eq 20 }
    @{ backupMilliseconds = $watch.ElapsedMilliseconds; renderedCards = (Backup-Cards).Count; replayedBackups = $backupNames.Count } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'backup-timings.json')
    Invoke-Element (Find 'MoreWorktrees')
    $null = Wait-For 'second backup page appended' { (Backup-Cards).Count -eq 40 }
    Set-Field 'BackupSearch' $backupNames[179]
    $null = Wait-For 'backup search includes the final catalog item' { (Backup-Cards).Count -eq 1 -and (Find ('BackupWorktree_' + $backupNames[179])) }
    Set-Field 'BackupSearch' $backupPrefix
    $null = Wait-For 'large name search resets the page size' { (Backup-Cards).Count -eq 20 }
    Filter-Backups 'Local only'
    $null = Wait-For 'remote copies excluded by local filter' { (Find 'BackupMatches').Current.Name -like 'No matching*' }
    Filter-Backups 'Remote only'
    $null = Wait-For 'remote filter restores matching cards' { (Backup-Cards).Count -eq 20 }
    Invoke-Element (Find 'MoreWorktrees')
    $null = Wait-For 'remote filter second page' { (Backup-Cards).Count -eq 40 }
    Select-Element (Find 'SettingsItem')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'backup loaded count retained on return' { (Backup-Cards).Count -eq 40 }
    $selection = (Find 'BackupFilter').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection[0].Current.Name -ne 'Remote only') { throw 'Backup status filter was lost on return.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-large-catalog.png')
    Filter-Backups 'All worktrees'
    $null = Wait-For 'all-worktrees filter resets the batch' { (Backup-Cards).Count -eq 20 }

    Start-UiScenario 'Backup search keeps restore selection explicit'
    Set-Field 'BackupSearch' 'no-backup-matches-this-search'
    $null = Wait-For 'backup search empty state' { (Find 'BackupMatches').Current.Name -like 'No matching*' }
    if ((Find 'NameBox') -and !(Find 'NameBox').Current.IsOffscreen) { throw 'Search left a hidden restore target actionable.' }
    Select-Element (Find 'SettingsItem')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'backup search retained on return' { $field = Find 'BackupSearch'; $field -and $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'no-backup-matches-this-search' }
    $null = Wait-For 'backup results retain search' { $count = Find 'BackupMatches'; $count -and $count.Current.Name -like 'No matching*' }
    Set-Field 'BackupSearch' ''
    $null = Wait-For 'backup search cleared' { (Find 'BackupMatches').Current.Name -like '*worktrees*saved edits*' }
    $card = Find 'BackupWorktree_source'
    $expandable = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
    $card.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $expandable).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $null = Wait-For 'worktree actions expand' { Find 'BackupWorktreeSend_source' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-search.png')
    $backupScroll = (Find 'ContentScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $null = Wait-For 'backup scrolled before leaving' { $backupScroll.SetScrollPercent(-1, 50); $backupScroll.Current.VerticalScrollPercent -gt 40 }
    Select-Element (Find 'SettingsItem')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'backup scroll restored' {
        $scroller = Find 'ContentScroll'
        $scroller -and $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticalScrollPercent -gt 40
    }
    $null = Wait-For 'expanded backup worktree retained' { Find 'BackupWorktreeSend_source' }

    Start-UiScenario 'Compare selected histories, inspect patches, and retain search and selection'
    Invoke-Element (Find 'BackupWorktreeCompare_source')
    $null = Wait-For 'comparison loaded' { $label = Find 'ComparedAt'; $label -and $label.Current.Name -like 'source*Compared*' }
    $listItem = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)
    $localCommit = (Find 'BackupCompareLocal').FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listItem)
    $remoteCommit = (Find 'BackupCompareRemote').FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listItem)
    if (!$localCommit -or !$remoteCommit) { throw 'Comparison needs both local and saved commit histories.' }
    Select-Element $localCommit
    $null = Wait-For 'local patch loaded' { $title = Find 'TitleText'; $title -and $title.Current.Name -like 'Local*' }
    Select-Element $remoteCommit
    $null = Wait-For 'backup patch loaded' { $title = Find 'TitleText'; $title -and $title.Current.Name -like 'Backup*' }
    $selectedTitle = (Find 'PatchTitle').Current.Name
    Set-Field 'BackupCompareSearch' 'no-commit-matches-this-search'
    $null = Wait-For 'search clears selected patch' { (Find 'PatchTitle').Current.Name -eq 'Select a commit to inspect its changes' }
    $null = Wait-For 'local search empty state' { (Find 'LocalEmpty').Current.Name -like 'No local commits match*' }
    Set-Field 'BackupCompareSearch' ''
    $null = Wait-For 'saved history restored after clearing search' { (Find 'BackupCompareRemote').FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listItem) }
    Select-Element ((Find 'BackupCompareRemote').FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listItem))
    $null = Wait-For 'same saved commit selected again' { (Find 'PatchTitle').Current.Name -eq $selectedTitle }
    Select-Element (Find 'SettingsItem')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'comparison selection restored on return' { $title = Find 'TitleText'; $title -and $title.Current.Name -eq $selectedTitle }
    Start-Sleep -Milliseconds 500
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-comparison.png')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'return to backup worktrees' { Find 'BackupSearch' }

    Start-UiScenario 'Backup previews reject changed versions and retry only the selected item'
    (Find 'ContentScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 0)
    Set-Field 'BackupSearch' $previewName
    $null = Wait-For 'one matching preview branch' { (Find 'BackupMatches').Current.Name -like '1 of *' }
    # Hold the fixture lock so the selected catalog version is captured before its history is fetched.
    $previewLock = [IO.File]::Open((Join-Path $FixtureRoot '.sg/sg.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Invoke-Element (Find ('BackupWorktreeOpen_' + $previewName))
        $null = Wait-For 'selected preview starts' {
            $loading = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Loading saved version…'))
            $loading -and !$loading.Current.IsOffscreen
        }
        $null = Wait-For 'shared page read progress' { $reading = Find 'PageReadProgress'; $reading -and !$reading.Current.IsOffscreen }
        & git -C $localRemote update-ref $previewRef ($tip + '^')
        if ($LASTEXITCODE -ne 0) { throw 'Could not move the disposable preview ref.' }
    } finally { $previewLock.Dispose() }
    $null = Wait-For 'changed preview error' { $button = Find 'BackupRetryPreview'; $button -and !$button.Current.IsOffscreen }
    if ((Find 'NameBox') -and !(Find 'NameBox').Current.IsOffscreen) { throw 'Changed backup enabled a restore form.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-preview-error.png')
    & git -C $localRemote update-ref $previewRef $tip
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the disposable preview ref.' }
    Invoke-Element (Find 'BackupRetryPreview')
    $null = Wait-For 'retried preview shows selected branch' { $field = Find 'NameBox'; $field -and !$field.Current.IsOffscreen -and $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $previewName }
    $null = Wait-For 'selected preview validates restore' { (Find 'RestoreButton').Current.IsEnabled }
    $null = Wait-For 'shared progress ends after reading' { $reading = Find 'PageReadProgress'; !$reading -or $reading.Current.IsOffscreen }
    if ((Find 'BackupRetryPreview') -and !(Find 'BackupRetryPreview').Current.IsOffscreen) { throw 'Successful preview retained its error.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-preview.png')
    Invoke-Element (Find 'NavigationViewBackButton')
    $null = Wait-For 'worktree list returns' { $field = Find 'BackupSearch'; $field -and !$field.Current.IsOffscreen }
    Set-Field 'BackupSearch' 'no-backup-matches-this-search'
    $null = Wait-For 'search clears selected preview' { $field = Find 'NameBox'; !$field -or $field.Current.IsOffscreen }

    Start-UiScenario 'Leaving Backup terminates a stalled Git read without creating a task'
    Stop-Process -Id $process.Id; $process.WaitForExit(); $process = $null
    $configPath = Join-Path $FixtureRoot '.sg/sg.json'
    $originalConfig = [IO.File]::ReadAllText($configPath)
    $config = $originalConfig | ConvertFrom-Json
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $config.backup.url = 'git://127.0.0.1:' + $listener.LocalEndpoint.Port + '/stalled-test.git'
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath
    $accept = $listener.AcceptTcpClientAsync()
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For 'restarted debug window' { Get-TestAppWindow $process }
    Invoke-Element (Wait-For 'backup action for stalled read' { Find 'BackupButton' })
    $null = Wait-For 'local Git request reaches server' { $accept.IsCompleted }
    $client = $accept.GetAwaiter().GetResult()
    $null = Wait-For 'Git request bytes' { $client.Available -gt 0 }
    $buffer = [byte[]]::new(4096)
    $null = $client.GetStream().Read($buffer, 0, $client.Available)
    # Never reply: ls-remote remains blocked until navigation cancels its process.
    $closed = $client.GetStream().ReadAsync($buffer, 0, $buffer.Length)
    $watch.Restart()
    Select-Element (Find 'ActivityItem')
    $null = Wait-For 'Git connection closed by navigation' { $closed.IsCompleted } 5
    try { if ($closed.GetAwaiter().GetResult() -ne 0) { throw 'The stalled server received unexpected extra data.' } }
    catch [IO.IOException] { } # Reset or EOF both mean that the Git process disconnected.
    @{ cancelMilliseconds = $watch.ElapsedMilliseconds } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'cancellation.json')
    $null = Wait-For 'activity loads after cancellation' { Find 'ActivitySearch' }
    if ((Find 'TaskQueueSummary').Current.Name -notlike '*0 active*0 finished*') { throw 'A browsing read created a task receipt.' }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($client) { $client.Dispose() }
    if ($listener) { $listener.Stop() }
    if ($originalConfig) { [IO.File]::WriteAllText((Join-Path $FixtureRoot '.sg/sg.json'), $originalConfig) }
    if ($previewRef) { & git -C $localRemote update-ref -d $previewRef }
    if ($backupNames.Count) { Update-FixtureRefs @($backupNames | ForEach-Object { "delete refs/heads/$_" }) }
    foreach ($path in $created) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
}
