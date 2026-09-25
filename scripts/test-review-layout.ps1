# Native UI Automation on a private desktop, with disposable Git and SVN repositories.
param([switch]$Worker, [string]$ArtifactDirectory, [switch]$NavigationOnly, [switch]$ReadingOnly,
    [ValidateSet('All', 'Commit', 'Merge')][string]$NavigationScope = 'All')
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/review-layout-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    if ($NavigationOnly) { $command += " -NavigationOnly -NavigationScope $NavigationScope" }
    if ($ReadingOnly) { $command += ' -ReadingOnly' }
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-review-layout-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(240)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Review layout UI test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Review layout UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Review layout UI. Artifacts: $ArtifactDirectory"
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
$env:SG_UI_TEST_WINDOW_SIZE = '600x900'
if ($ReadingOnly) {
    $port = New-TestWebViewPort
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$port"
}
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
function Find([string]$id, [switch]$Name, $within = $window) {
    # Do not let the diff's browser provider truncate searches for native siblings.
    $queue = [Collections.Generic.Queue[System.Windows.Automation.AutomationElement]]::new()
    $queue.Enqueue($within)
    while ($queue.Count) {
        $node = $queue.Dequeue()
        foreach ($child in $node.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            $key = if ($Name) { $child.Current.Name } else { $child.Current.AutomationId }
            if ($key -eq $id) { return $child }
            if ($child.Current.AutomationId -ne 'Web') { $queue.Enqueue($child) }
        }
    }
}
function Wait-For([scriptblock]$read, [string]$message = 'Review UI assertion timed out.') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        try { $value = & $read; if ($value) { return $value } }
        catch [System.Windows.Automation.ElementNotAvailableException] { } # Navigation replaces native subtrees.
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Row([string]$list, [string]$match) {
    $control = Find $list
    if (!$control) { return }
    $control.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)) |
        Where-Object { $_.Current.Name -like $match } | Select-Object -First 1
}
function Assert-Fits([string]$id) {
    $element = Wait-For { Find $id } "Missing control: $id"
    $r = $element.Current.BoundingRectangle; $bounds = $window.Current.BoundingRectangle
    if ($element.Current.IsOffscreen -or $r.Width -le 0 -or $r.Left -lt $bounds.Left -or $r.Right -gt $bounds.Right -or $r.Bottom -gt $bounds.Bottom) { throw "Clipped control: $id ($r) in $bounds" }
}
function Assert-Disabled([string]$id, [string]$reason) {
    $control = Wait-For { Find $id }
    if ($control.Current.IsEnabled -or $control.Current.HelpText -notlike $reason) { throw "Missing disabled reason on $id : $($control.Current.HelpText)" }
    $null = Wait-For {
        $hint = Find "DisabledHint_$id"
        $hint -and $hint.Current.IsKeyboardFocusable -and $hint.Current.HelpText -like $reason
    } "Disabled $id does not expose keyboard help."
}
function Close-Dialog {
    # InfoBars also expose CloseButton; the message dialog's action is specifically Cancel.
    Invoke-Control (Wait-For { Find 'Cancel' -Name })
    $null = Wait-For { !(Find 'Cancel' -Name) }
}
function Message-Box {
    $header = Find 'Commit message' -Name
    if (!$header) { return }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $panel = $walker.GetParent($walker.GetParent($header))
    $panel.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
}
function Stop-App {
    if ($script:process -and !$script:process.HasExited) { Stop-Process -Id $script:process.Id; $script:process.WaitForExit() }
    $script:window = $null
}
function Launch([string]$action, [string]$path) {
    $script:process = Start-Process -FilePath $app -ArgumentList @($action, ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $script:window = Wait-For { Get-TestAppWindow $script:process }
}
function Check-Exit([string]$step) { if ($LASTEXITCODE -ne 0) { throw "Fixture command failed: $step" } }
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
$process = $null; $window = $null
Initialize-UiReport $ArtifactDirectory 'Commit and Merge layout'
try {
    $setupLog = Join-Path $ArtifactDirectory 'setup.txt'
    $repository = Join-Path $ArtifactDirectory 'svnrepo'
    & svnadmin create $repository
    Check-Exit 'svnadmin create'
    $url = ([Uri]($repository + '/')).AbsoluteUri.TrimEnd('/')
    $file = Join-Path $ArtifactDirectory 'seed.txt'
    [IO.File]::WriteAllText($file, "Original content`n")
    & svnmucc -m 'Create review fixture' mkdir "$url/trunk" put $file "$url/trunk/base.txt" put $file "$url/trunk/keep.txt" mkdir "$url/branches" | Out-File $setupLog
    Check-Exit 'seed SVN'
    & svnmucc -m 'Copy source branch' cp 1 "$url/trunk" "$url/branches/source" | Out-File $setupLog -Append
    Check-Exit 'branch SVN'
    & svnmucc -m 'First source change' put $file "$url/branches/source/first.txt" | Out-File $setupLog -Append
    Check-Exit 'first source change'
    & svnmucc -m 'Second source change' put $file "$url/branches/source/second.txt" | Out-File $setupLog -Append
    Check-Exit 'second source change'
    $fixture = Join-Path $ArtifactDirectory 'root'
    & $cli init $fixture --no-fsmonitor 2>&1 | Out-File $setupLog -Append
    Check-Exit 'sg init'
    # Git's Windows worktree repair cannot rewrite a hidden .git link in these fixtures.
    & git -C (Join-Path $fixture '.sg') config core.hideDotFiles false
    Check-Exit 'fixture Git metadata'
    & $cli checkout add --url "$url/trunk" --root $fixture --name checkout 2>&1 | Out-File $setupLog -Append
    Check-Exit 'sg checkout add'
    & $cli branch feature --root $fixture --from checkout 2>&1 | Out-File $setupLog -Append
    Check-Exit 'sg branch'
    $worktree = Join-Path $fixture 'feature'
    & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty -m 'Local fixture commit' | Out-File $setupLog -Append
    Check-Exit 'local commit'
    [IO.File]::WriteAllText((Join-Path $worktree 'base.txt'), "Changed content`n")
    [IO.File]::WriteAllText((Join-Path $worktree 'keep.txt'), "Keep this edit`n")
    [IO.File]::WriteAllText((Join-Path $worktree 'untracked.txt'), "Untracked content`n")
    $checkout = (Get-Content -LiteralPath (Join-Path $fixture '.sg/sg.json') -Raw | ConvertFrom-Json).checkouts[0].path
    $checkoutBefore = (& svn status $checkout) -join "`n"

    if ($ReadingOnly) {
        . "$PSScriptRoot/diff-reading-cases.ps1"
        Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
        return
    }

    if ($NavigationOnly) {
        . "$PSScriptRoot/review-navigation-cases.ps1"
        Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
        return
    }

    Start-UiScenario 'Compact Commit exposes common actions and preserves the file selection across views'
    Launch 'commit' $worktree
    $null = Wait-For { (Find 'CommitButton').Current.IsEnabled }
    foreach ($id in @('CommitButton', 'AdvancedButton', 'RefreshButton', 'AllButton', 'NoneButton', 'ChangesTab', 'DiffTab')) { Assert-Fits $id }
    if (Find 'StageButton') { throw 'Staging is exposed before opening Advanced.' }
    Select-Control (Row 'Files' '*base.txt*')
    $null = Wait-For { $t = Find 'TitleText'; $t -and !$t.Current.IsOffscreen -and $t.Current.Name -like '*base.txt*' }
    if (!(Find 'DiffTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'Picking a Commit file did not open Diff.' }
    Assert-Fits 'TitleText'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'commit-diff-narrow.png')
    Select-Control (Find 'ChangesTab')
    if (!(Row 'Files' '*base.txt*').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'Commit file selection was lost.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'commit-narrow.png')
    Complete-UiScenario

    Start-UiScenario 'Commit advanced actions explain disabled states and visibly identify amend mode'
    Invoke-Control (Find 'NoneButton')
    Invoke-Control (Find 'AdvancedButton')
    foreach ($id in @('StageButton', 'ShelveButton', 'DiscardButton')) { Assert-Disabled $id '*Check at least one*'; Assert-Fits $id }
    Assert-Disabled 'UnstageButton' '*No checked file*'
    (Find 'Amend').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $null = Wait-For { Find 'Cancel amend' -Name }
    if (!(Find 'CommitButton').Current.IsEnabled -or (Find 'CommitLabel').Current.Name -ne 'Amend') { throw 'Message-only amend is not available.' }
    Assert-Fits 'AmendNotice'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'commit-amend-narrow.png')
    Invoke-Control (Find 'Cancel amend' -Name)
    Assert-Disabled 'CommitButton' '*Select at least one*'
    Complete-UiScenario

    Start-UiScenario 'Stage and unstage operate only on checked files through Advanced'
    $baseRow = Row 'Files' '*base.txt*'
    $check = $baseRow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox))
    $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Control (Find 'AdvancedButton'); Invoke-Control (Wait-For { Find 'StageButton' })
    $null = Wait-For { $staged = & git -C $worktree diff --cached --name-only; $staged -eq 'base.txt' }
    $null = Wait-For { (Find 'CommitButton').Current.IsEnabled }
    Invoke-Control (Find 'AdvancedButton')
    $null = Wait-For { (Find 'UnstageButton').Current.IsEnabled }
    Invoke-Control (Find 'UnstageButton')
    $null = Wait-For { !(& git -C $worktree diff --cached --name-only) }
    $null = Wait-For { (Find 'CommitButton').Current.IsEnabled }
    Invoke-Control (Find 'AdvancedButton')
    $null = Wait-For { !(Find 'UnstageButton').Current.IsEnabled }
    Assert-Disabled 'UnstageButton' '*No checked file*'
    Invoke-Control (Find 'AdvancedButton')
    Complete-UiScenario

    Start-UiScenario 'Commit message draft and selected changes survive compact and wide layout switches'
    Select-Control (Find 'ChangesTab')
    Invoke-Control (Find 'NoneButton')
    $baseRow = Row 'Files' '*base.txt*'
    $check = $baseRow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox))
    $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Control (Find 'CommitButton')
    $box = Wait-For { Message-Box }
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Commit only the selected file')
    Close-Dialog
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    $null = Wait-For { $t = Find 'DiffTab'; !$t -or $t.Current.IsOffscreen }
    foreach ($id in @('Files', 'TitleText', 'CommitButton', 'AdvancedButton')) { Assert-Fits $id }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'commit-wide.png')
    Invoke-Control (Find 'CommitButton')
    $box = Wait-For { Message-Box }
    if ($box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Commit only the selected file') { throw 'Commit draft was lost.' }
    Invoke-Control (Find 'PrimaryButton')
    $null = Wait-For { (& git -C $worktree log -1 --format=%s) -eq 'Commit only the selected file' }
    $committed = @(& git -C $worktree diff-tree --no-commit-id --name-only -r HEAD)
    if ($committed.Count -ne 1 -or $committed[0] -ne 'base.txt') { throw "Wrong files committed: $committed" }
    if (!(& git -C $worktree status --porcelain -- keep.txt untracked.txt)) { throw 'Unchecked edits disappeared.' }
    Stop-App
    Complete-UiScenario

    Start-UiScenario 'Compact Merge keeps reverse operations advanced and retains selected revisions'
    Launch 'merge' $checkout
    $null = Wait-For { (Find 'TestButton').Current.IsEnabled }
    foreach ($id in @('TargetBox', 'SourceBox', 'MergeButton', 'TestButton', 'AdvancedButton', 'RevisionsTab', 'PreviewTab')) { Assert-Fits $id }
    if (Find 'TakeOutButton') { throw 'Reverse merge is exposed before opening Advanced.' }
    Invoke-Control (Find 'AdvancedButton')
    Assert-Disabled 'TakeOutButton' '*Select the revisions*'
    # Opening the button's flyout again dismisses it without injected keyboard input.
    Invoke-Control (Find 'AdvancedButton')
    Select-Control (Row 'Revisions' '*Second source change*')
    $null = Wait-For { (Find 'MergeLabel').Current.Name -eq 'Merge r4' }
    Select-Control (Find 'PreviewTab'); Assert-Fits 'TitleText'
    Select-Control (Find 'RevisionsTab')
    if ((Find 'MergeLabel').Current.Name -ne 'Merge r4') { throw 'Merge revision selection was lost.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'merge-narrow.png')
    Complete-UiScenario

    Start-UiScenario 'Preview opens automatically and is invalidated when the merge selection changes'
    Invoke-Control (Find 'TestButton')
    $null = Wait-For { (Find 'TestButton').Current.IsEnabled -and (Find 'TitleText').Current.Name -like '*test only*' }
    if (!(Find 'PreviewTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'Preview did not open automatically.' }
    if (((& svn status $checkout) -join "`n") -ne $checkoutBefore) { throw 'Merge preview changed checkout files.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'merge-preview-narrow.png')
    Select-Control (Find 'RevisionsTab')
    Select-Control (Row 'Revisions' '*First source change*')
    Select-Control (Find 'PreviewTab')
    $null = Wait-For { (Find 'TitleText').Current.Name -eq 'Merge preview' }
    Complete-UiScenario

    Start-UiScenario 'Wide Merge confirms and applies only selected revisions without committing to SVN'
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    $null = Wait-For { $t = Find 'PreviewTab'; !$t -or $t.Current.IsOffscreen }
    foreach ($id in @('Revisions', 'TitleText', 'MergeButton', 'TestButton', 'AdvancedButton')) { Assert-Fits $id }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'merge-wide.png')
    Invoke-Control (Find 'MergeButton')
    $null = Wait-For { Find 'PrimaryButton' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'merge-confirm.png')
    Close-Dialog
    if (((& svn status $checkout) -join "`n") -ne $checkoutBefore) { throw 'Cancelling Merge changed checkout files.' }
    Invoke-Control (Find 'MergeButton'); $null = Wait-For { Find 'PrimaryButton' }
    Invoke-Control (Find 'PrimaryButton')
    $null = Wait-For { Test-Path -LiteralPath (Join-Path $checkout 'first.txt') }
    $null = Wait-For { (Find 'TestButton').Current.IsEnabled -and (Find 'OutcomeHeader').Current.Name -eq 'What the merge did' }
    if (Test-Path -LiteralPath (Join-Path $checkout 'second.txt')) { throw 'Merge included an unselected revision.' }
    if ((& svnlook youngest $repository) -ne '4') { throw 'Merge committed to SVN.' }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    Fail-UiScenario $_ $window
    Write-UiResult $ArtifactDirectory @{ status = 'failed'; error = $_.Exception.Message; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
    throw
} finally { Stop-App }
