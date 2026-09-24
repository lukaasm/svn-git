# Native UI Automation on a private desktop. Build Debug successfully first.
param([Parameter(Mandatory)][string]$FixtureRoot, [Parameter(Mandatory)][string]$SourceRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/ui-polish-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $command += " -SourceRoot '" + $SourceRoot.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-ui-polish-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(480)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'UI polish test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "UI polish failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: UI polish. Artifacts: $ArtifactDirectory"
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
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($property, $id))
}
function Wait-For([scriptblock]$read) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'UI polish assertion timed out.'
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Need-Help([string]$id, [string]$text) {
    $button = Wait-For { $b = Find $id; if ($b -and !$b.Current.IsEnabled -and $b.Current.HelpText -like $text) { $b } }
    $hint = Wait-For { $h = Find ('DisabledHint_' + $id); if ($h -and $h.Current.IsKeyboardFocusable -and $h.Current.HelpText -like $text) { $h } }
    $hint.SetFocus()
    $null = Wait-For { Find $button.Current.HelpText -Name }
    return $button.Current.HelpText
}
$process = $null; $window = $null; $gate = $null
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
$timings = [Collections.Generic.List[object]]::new()
Initialize-UiReport $ArtifactDirectory 'UI polish'
try {
    if ($FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable workflow fixture.' }
    $fixtureConfig = Get-Content -LiteralPath (Join-Path $FixtureRoot '.sg/sg.json') -Raw | ConvertFrom-Json
    # Real checkout labels only; all SVN URLs, paths and operations belong to this new local fixture.
    $names = @((Get-Content -LiteralPath (Join-Path $SourceRoot '.sg/sg.json') -Raw | ConvertFrom-Json).checkouts.name)
    $root = Join-Path $ArtifactDirectory 'sg-workflow-ui-polish/root'
    & $cli init $root --no-fsmonitor | Out-File (Join-Path $ArtifactDirectory 'setup.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture root.' }
    foreach ($name in $names) {
        & $cli checkout add --url $fixtureConfig.checkouts[0].url --root $root --name $name | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
        if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture checkout.' }
    }
    & $cli branch polish-review --root $root --from $names[0] | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture worktree.' }
    $worktree = Join-Path $root 'polish-review'
    [IO.File]::WriteAllText((Join-Path $worktree 'review.cs'), "// Review fixture`n")
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Start-UiScenario 'Real checkout names remain identifiable in expanded and collapsed navigation'
    foreach ($name in $names) {
        $nav = Wait-For { Find ('CheckoutNav_' + $name) }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { (Find 'CoName').Current.Name -eq $name }
        $timings.Add(@{ action = 'Select checkout'; name = $name; milliseconds = $clock.ElapsedMilliseconds })
        if (!(Find ('CheckoutIcon_' + $name))) { throw 'Checkout identity is missing.' }
    }
    Invoke-Control (Find 'TogglePaneButton')
    foreach ($name in $names) {
        $nav = Find ('CheckoutNav_' + $name)
        $icon = Find ('CheckoutIcon_' + $name)
        if (!$icon -or $icon.Current.IsOffscreen) { throw ('Collapsed icon is not visible: ' + $name) }
        if ($icon.Current.BoundingRectangle.Width -lt 16) { throw ('Collapsed initials were clipped: ' + $name) }
        $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { (Find 'CoName').Current.Name -eq $name }
    }
    Invoke-Control (Find 'TogglePaneButton')
    Complete-UiScenario

    Start-UiScenario 'Disabled checkout actions expose reasons and a keyboard tooltip'
    Invoke-Control (Find 'AddCheckoutButton')
    $button = Wait-For { $b = Find 'Add the checkout' -Name; if ($b -and $b.Current.HelpText -eq 'Choose an SVN working-copy folder.') { $b } }
    $hint = Find 'DisabledHint_AddButton'
    if (!$hint.Current.IsKeyboardFocusable) { throw 'Disabled action explanation is not keyboard accessible.' }
    $hint.SetFocus()
    $null = Wait-For { Find 'Choose an SVN working-copy folder.' -Name }
    Complete-UiScenario

    Start-UiScenario 'Rapid folder edits keep validation current and leave navigation responsive'
    $folder = Find 'FolderBox'
    foreach ($value in @((Join-Path $root $names[0]), (Join-Path $root 'not-a-checkout'), (Join-Path $root $names[-1]))) {
        $clock = [Diagnostics.Stopwatch]::StartNew(); Enter-Value $folder $value
        $timings.Add(@{ action = 'Edit checkout path'; milliseconds = $clock.ElapsedMilliseconds })
    }
    $null = Wait-For { (Find 'Add the checkout' -Name).Current.HelpText -eq "Already registered in this root as '$($names[-1])'." }
    Enter-Value $folder (Join-Path $root 'missing-folder')
    $null = Wait-For { (Find 'Add the checkout' -Name).Current.HelpText -eq 'No such folder.' }
    if ((Find 'Add the checkout' -Name).Current.IsEnabled) { throw 'Stale valid input enabled submission.' }
    Complete-UiScenario

    Start-UiScenario 'Task blocking explains the collision and restores the ordinary disabled reason'
    $extra = Join-Path $ArtifactDirectory 'external'
    & svn checkout $fixtureConfig.checkouts[0].url $extra | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create external working copy.' }
    Enter-Value $folder $extra
    Enter-Value (Find 'NameBox') 'Extra'
    $button = Wait-For { $b = Find 'Add the checkout' -Name; if ($b -and $b.Current.IsEnabled) { $b } }
    $gate = [IO.File]::Open((Join-Path $root '.sg/sg.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Invoke-Control $button
    (Find ('CheckoutNav_' + $names[0])).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Need-Help 'SvnCommitButton' '*in progress*'
    $gate.Dispose(); $gate = $null
    $null = Wait-For { (Find 'TaskQueueSummary').Current.Name -like '*0 active*' }
    $null = Need-Help 'SvnCommitButton' '*no*'
    Complete-UiScenario

    Start-UiScenario 'Review navigation explains unavailable actions without starting a task'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $worktree + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $null = Need-Help 'ReviewNextOpen' 'There are no open comments*'
    $null = Need-Help 'ReviewPreviousOpen' 'There are no open comments*'
    Complete-UiScenario
    $timings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'timings.json')
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory); timings = @($timings.ToArray()) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($gate) { $gate.Dispose() }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
