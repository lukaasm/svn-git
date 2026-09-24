# Native UI Automation and WebView DOM checks on a private desktop. Build Debug successfully first.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/review-inbox-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-review-inbox-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(480)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Review inbox UI test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Review inbox UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Review inbox UI. Artifacts: $ArtifactDirectory"
    } finally { $desktop.Dispose() }
    return
}
. "$PSScriptRoot/ui-test-report.ps1"
. "$PSScriptRoot/ui-automation.ps1"
. "$PSScriptRoot/ui-webview.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$env:SG_UI_TEST_DIRECTORY = Join-Path $ArtifactDirectory 'settings'
$env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $ArtifactDirectory 'webview'
$port = New-TestWebViewPort
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$port"
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
function Find([string]$id, [switch]$Name) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($property, $id))
}
function Wait-For([scriptblock]$read) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Review inbox UI assertion timed out.'
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Pick([string]$id, [string]$option) {
    (Find $id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-For { Find $option -Name }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Wait-Count([string]$count) { $null = Wait-For { $c = Find 'ReviewInboxCount'; $c -and $c.Current.Name -eq $count } }
function Agent([string]$worktree, [string]$id, [string]$action, [string]$body) {
    $context = & $cli review thread $id --root $FixtureRoot --worktree $worktree | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read fixture feedback.' }
    & $cli review $action $id --root $FixtureRoot --worktree $worktree --body $body --expected-revision $context.thread.revision --version $context.version --actor 'Agent' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not address fixture feedback.' }
}
$process = $null; $window = $null; $browser = $null; $branches = @()
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
Initialize-UiReport $ArtifactDirectory 'Review inbox'
try {
    if ($FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable workflow fixture.' }
    $config = Get-Content -LiteralPath (Join-Path $FixtureRoot '.sg/sg.json') -Raw | ConvertFrom-Json
    $FixtureRoot = Join-Path $ArtifactDirectory 'sg-workflow-ui-inbox/root'
    & $cli init $FixtureRoot --no-fsmonitor | Out-File (Join-Path $ArtifactDirectory 'setup.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize isolated inbox fixture.' }
    & $cli checkout add --url $config.checkouts[0].url --root $FixtureRoot --name checkout | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated inbox checkout.' }
    $config = Get-Content -LiteralPath (Join-Path $FixtureRoot '.sg/sg.json') -Raw | ConvertFrom-Json
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0,8)
    foreach ($name in @("inbox-alpha-$suffix", "inbox-beta-$suffix")) {
        & $cli branch $name --root $FixtureRoot --from $config.checkouts[0].name | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
        if ($LASTEXITCODE -ne 0) { throw 'Could not create inbox fixture worktree.' }
        $branches += $name
    }
    $alpha = Join-Path $FixtureRoot $branches[0]; $beta = Join-Path $FixtureRoot $branches[1]
    $sourceFile = Join-Path $alpha 'review-example.cs'
    $source = ((1..180 | ForEach-Object { "// Source line $_" }) -join "`n") + "`n"
    [IO.File]::WriteAllText($sourceFile, $source)
    $process = Start-Process -FilePath $app -ArgumentList @('review-inbox', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Start-UiScenario 'Empty inbox provides useful content and discovers feedback without a manual refresh'
    $null = Wait-For { Find 'No review comments yet' -Name }
    $a = & $cli review comment --root $FixtureRoot --worktree $alpha --file review-example.cs --lines 50:50 --body 'Explain this boundary condition.' --actor 'Reviewer' | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not create alpha feedback.' }
    $b = & $cli review comment --root $FixtureRoot --worktree $beta --file base.txt --side original --lines 1:1 --body 'Confirm compatibility with the old base.' --actor 'Reviewer' | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not create beta feedback.' }
    Agent $beta $b.id resolve 'Compatibility verified by the agent.'
    Wait-Count 'Showing 1 of 1 comments'
    $null = Wait-For { Find '1 open · 1 resolved · 2 worktrees' -Name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'inbox-open.png')
    Complete-UiScenario

    Start-UiScenario 'Resolved results open the correct worktree, original line and thread'
    Pick 'ReviewInboxFilter' 'Resolved comments'
    $open = Wait-For { Find ('ReviewInboxOpen_' + $b.id) }
    Invoke-Control $open
    $null = Wait-For { Find 'Compatibility verified by the agent.' -Name }
    $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    $null = Wait-For { Invoke-TestWebView $browser "Array.from(document.querySelectorAll('.editor.original [data-thread-id]')).some(t => t.dataset.threadId === '$($b.id)')" }
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'resolved-target.png')
    $browser.Dispose(); $browser = $null
    Invoke-Control (Find 'PART_BackButton')
    Wait-Count 'Showing 1 of 1 comments'
    $selected = (Find 'ReviewInboxFilter').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selected[0].Current.Name -ne 'Resolved comments') { throw 'Back navigation lost the inbox status filter.' }
    Complete-UiScenario

    Start-UiScenario 'Changed anchors open saved context rather than the wrong source line'
    Pick 'ReviewInboxFilter' 'Open comments'
    [IO.File]::WriteAllText($sourceFile, $source.Replace('// Source line 50', '// Replaced boundary logic'))
    Invoke-Control (Wait-For { Find ('ReviewInboxOpen_' + $a.id) })
    $null = Wait-For { $button = Find 'CodeReviewBackToDiff'; $button -and !$button.Current.IsOffscreen }
    $null = Wait-For { Find 'Explain this boundary condition.' -Name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'saved-context.png')
    Invoke-Control (Find 'PART_BackButton')
    Wait-Count 'Showing 1 of 1 comments'
    Complete-UiScenario

    Start-UiScenario 'Agent updates arrive live, corrupt metadata retains feedback, and recovery clears the warning'
    Agent $alpha $a.id reply 'Checked the edge case and updated the source.'
    $null = Wait-For { $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)) | Where-Object { $_.Current.Name -like '*Checked the edge case and updated the source.*' } | Select-Object -First 1 }
    $identityPath = git -C $alpha rev-parse --path-format=absolute --git-path sg-review-id
    $identity = [IO.File]::ReadAllText($identityPath).Trim()
    $document = @(Get-ChildItem -LiteralPath (Join-Path $FixtureRoot '.sg') -Filter ($identity + '.json') -Recurse)[0].FullName
    $valid = [IO.File]::ReadAllText($document)
    try {
        [IO.File]::WriteAllText($document, 'incomplete')
        $null = Wait-For { Find 'Some feedback could not be refreshed' -Name }
        if (!(Find ('ReviewInboxOpen_' + $a.id))) { throw 'Invalid metadata cleared the last good feedback.' }
    } finally { [IO.File]::WriteAllText($document, $valid) }
    $null = Wait-For { !(Find 'Some feedback could not be refreshed' -Name) }
    Agent $alpha $a.id resolve 'Boundary behavior is now covered.'
    Wait-Count 'Showing 0 of 0 comments'
    Invoke-Control (Find 'ReviewInboxClearFilters')
    Wait-Count 'Showing 2 of 2 comments'
    Complete-UiScenario

    Start-UiScenario 'Large inbox limits initial rows, searches all feedback and restores its scroll on return'
    $data = [IO.File]::ReadAllText($document) | ConvertFrom-Json
    $template = $data.threads[0] | ConvertTo-Json -Depth 40
    $extra = 1..65 | ForEach-Object {
        $thread = $template | ConvertFrom-Json
        $thread.id = [Guid]::NewGuid().ToString('N')
        $event = $thread.events[0]; $event.id = [Guid]::NewGuid().ToString('N'); $event.parents = @()
        $event.body = "Inbox feedback number $_"; $event.at = [DateTimeOffset]::UtcNow.AddSeconds(-$_).ToString('O')
        $thread.events = @($event); $thread
    }
    $data.threads = @($data.threads) + @($extra)
    [IO.File]::WriteAllText($document, ($data | ConvertTo-Json -Depth 40))
    Enter-Value (Find 'ReviewInboxSearch') 'Inbox feedback number'
    Wait-Count 'Showing 40 of 65 comments'
    Invoke-Control (Find 'ReviewInboxMore')
    Wait-Count 'Showing 65 of 65 comments'
    $scroll = (Find 'ReviewInboxResults').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent(-1, 60)
    Start-Sleep -Milliseconds 300
    $before = $scroll.Current.VerticalScrollPercent
    $visible = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)) |
        Where-Object { $_.Current.AutomationId.StartsWith('ReviewInboxOpen_') -and !$_.Current.IsOffscreen } | Select-Object -First 1
    if (!$visible) { throw 'No visible result action at the reading position.' }
    Invoke-Control $visible
    $null = Wait-For { Find 'CodeReviewFiles' }
    Invoke-Control (Find 'PART_BackButton')
    Wait-Count 'Showing 65 of 65 comments'
    $null = Wait-For { [Math]::Abs((Find 'ReviewInboxResults').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticalScrollPercent - $before) -lt 2 }
    if ((Find 'ReviewInboxSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Inbox feedback number') { throw 'Back lost the search.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'inbox-return.png')
    Complete-UiScenario

    Start-UiScenario 'No-match recovery and per-worktree filtering remain usable at narrow width'
    Enter-Value (Find 'ReviewInboxSearch') 'no-feedback-matches-this'
    Wait-Count 'Showing 0 of 0 comments'
    Invoke-Control (Find 'ReviewInboxClearFilters')
    Wait-Count 'Showing 40 of 67 comments'
    Pick 'ReviewInboxWorktree' ($branches[1] + ' · 0 open')
    Wait-Count 'Showing 1 of 1 comments'
    $window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(960, 760)
    Start-Sleep -Milliseconds 300
    $button = (Find ('ReviewInboxOpen_' + $b.id)).Current.BoundingRectangle
    $bounds = (Find 'ReviewInboxResults').Current.BoundingRectangle
    if ($button.Right -gt $bounds.Right -or $button.Left -lt $bounds.Left) { throw 'The open-comment action is clipped in the narrow layout.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'inbox-narrow.png')
    Complete-UiScenario

    Start-UiScenario 'Review inbox is reachable from the main navigation'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $nav = Wait-For { Find 'ReviewInboxNav' }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { Find 'ReviewInboxSearch' }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($browser) { $browser.Dispose() }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    foreach ($branch in $branches) { & $cli rm $branch --root $FixtureRoot --force | Out-Null }
}
