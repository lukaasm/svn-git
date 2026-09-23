# Reproduces a slow Pull read on a private desktop using only UI Automation.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/pull-loading-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-pull-loading-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(150)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Pull test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Pull test failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Pull feedback. Artifacts: $ArtifactDirectory"
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
function Find([string]$id, [switch]$Name) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($property, $id))
}
function Wait-For([scriptblock]$read, [int]$seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Pull feedback assertion timed out.'
}
$process = $null; $heldLock = $null; $window = $null
$snapshot = $null; $gitStore = $null; $listener = $null; $client = $null; $brokenRecord = $null
Initialize-UiReport $ArtifactDirectory 'Pull'
try {
    if ($FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable workflow fixture.' }
    $branch = Join-Path $FixtureRoot 'imported'
    if (!(Test-Path -LiteralPath (Join-Path $branch '.git'))) { throw 'Fixture needs its imported worktree.' }
    Start-UiScenario 'Pull immediately shows loading feedback while waiting for repository access'
    $heldLock = [IO.File]::Open((Join-Path $FixtureRoot '.sg/sg.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('rebase', ('"' + $branch + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = Wait-For { $state = Find 'WorkflowLoading'; $state -and !$state.Current.IsOffscreen } 3
    @{ loadingMilliseconds = $watch.ElapsedMilliseconds } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'timings.json')
    if (Find 'PullFromSvnButton') { throw 'Pull is actionable before its plan is ready.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'pull-loading.png')
    $heldLock.Dispose(); $heldLock = $null
    $null = Wait-For { Find 'PullFromSvnButton' } 45
    $null = Wait-For { $state = Find 'WorkflowLoading'; !$state -or $state.Current.IsOffscreen }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'pull-ready.png')

    Start-UiScenario 'Local details hydrate before a slow SVN server and navigation cancels the read'
    $gitStore = Join-Path $FixtureRoot '.sg'
    $snapshot = (& git --git-dir=$gitStore rev-parse refs/remotes/svn/checkout).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Fixture needs the checkout snapshot.' }
    $tree = (& git --git-dir=$gitStore rev-parse ($snapshot + '^{tree}')).Trim()
    $body = (& git --git-dir=$gitStore log -1 --format=%B $snapshot) -join "`n"
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $body = [regex]::Replace($body, '(?:https?|file)://\S+', "http://127.0.0.1:$port/slow-svn")
    $messageFile = Join-Path $ArtifactDirectory 'snapshot-message.txt'
    [IO.File]::WriteAllText($messageFile, $body)
    $slowSnapshot = (& git --git-dir=$gitStore commit-tree $tree -p $snapshot -F $messageFile).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare delayed-server snapshot.' }
    & git --git-dir=$gitStore update-ref refs/remotes/svn/checkout $slowSnapshot $snapshot
    if ($LASTEXITCODE -ne 0) { throw 'Could not install disposable delayed-server snapshot.' }
    $accept = $listener.AcceptTcpClientAsync()
    (Find 'Refresh pull plan' -Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $accept.IsCompleted -and (Find 'Checking SVN revisions…' -Name) } 30
    $client = $accept.GetAwaiter().GetResult()
    if (!(Find 'Review local commits' -Name) -or !(Find 'Branch edits: clean' -Name)) { throw 'Local preview did not hydrate before the server replied.' }
    if (Find 'PullFromSvnButton') { throw 'An incomplete plan offered Pull.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'pull-streaming.png')
    $watch.Restart()
    (Find 'SettingsItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { try { $probe = [IO.File]::Open((Join-Path $FixtureRoot '.sg/sg.lock'), 'Open', 'ReadWrite', 'None'); $probe.Dispose(); $true } catch { $false } } 5
    @{ navigationCancellationMilliseconds = $watch.ElapsedMilliseconds } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'cancellation.json')
    & git --git-dir=$gitStore update-ref refs/remotes/svn/checkout $snapshot
    $snapshot = $null
    $client.Dispose(); $client = $null; $listener.Stop(); $listener = $null
    (Find 'NavigationViewBackButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { Find 'PullFromSvnButton' } 30

    Start-UiScenario 'A failed Pull preview offers an inline retry and recovers'
    $brokenRecord = Join-Path $gitStore ('operations/' + [Guid]::NewGuid().ToString('N') + '.json')
    '{ invalid fixture record' | Set-Content -LiteralPath $brokenRecord
    (Find 'Refresh pull plan' -Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { Find 'PullPlanError' }
    if (Find 'PullFromSvnButton') { throw 'Failed preview retained an actionable plan.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'pull-error.png')
    Remove-Item -LiteralPath $brokenRecord
    $brokenRecord = $null
    (Find 'Retry' -Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { Find 'PullFromSvnButton' } 30
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($heldLock) { $heldLock.Dispose() }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($snapshot) { & git --git-dir=$gitStore update-ref refs/remotes/svn/checkout $snapshot }
    if ($client) { $client.Dispose() }
    if ($listener) { $listener.Stop() }
    if ($brokenRecord -and (Test-Path -LiteralPath $brokenRecord)) { Remove-Item -LiteralPath $brokenRecord }
}

