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
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
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
function Set-Field([string]$id, [string]$value) { (Find $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Entries {
    @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Steps and checkpoint'),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true))))
}
$process = $null; $window = $null; $created = [Collections.Generic.List[string]]::new()
$originalConfig = $null; $listener = $null; $client = $null
Initialize-UiReport $ArtifactDirectory 'Browsing'
try {
    $records = @(Get-ChildItem -LiteralPath (Join-Path $SourceRoot '.sg/operations') -Filter '*.json' | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json })
    if (!$records.Count) { throw 'Source needs saved activity.' }
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
    (Find 'ActivityFilter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $attention = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Needs attention'))
    Select-Element $attention
    $null = Wait-For 'attention filter finds older unfinished record' { (Entries).Count -eq 1 }
    Invoke-Element (Find 'ActivityRefresh')
    $null = Wait-For 'refresh keeps the history filter' { (Entries).Count -eq 1 -and (Find 'ActivityResults').Current.Name -like '*1 of 1*' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'activity-filter.png')
    Start-UiScenario 'Backup search keeps restore selection explicit'
    Invoke-Element (Find 'NavigationViewBackButton')
    Invoke-Element (Wait-For 'backup action' { Find 'BackupButton' })
    $null = Wait-For 'backup entries loaded' { $field = Find 'BackupSearch'; if ($field -and !$field.Current.IsOffscreen) { $field } }
    Set-Field 'BackupSearch' 'no-backup-matches-this-search'
    $null = Wait-For 'backup search empty state' { (Find 'BackupMatches').Current.Name -like 'No matching*' }
    if ((Find 'NameBox') -and !(Find 'NameBox').Current.IsOffscreen) { throw 'Search left a hidden restore target actionable.' }
    Set-Field 'BackupSearch' ''
    $null = Wait-For 'backup search cleared' { (Find 'BackupMatches').Current.Name -like '*backup items' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-search.png')

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
    foreach ($path in $created) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
}
