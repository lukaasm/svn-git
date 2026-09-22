# Tests workflow presentation on a private desktop. Use a disposable workflow fixture with a source branch and a local backup remote.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/presentation-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-report-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Presentation test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Presentation test failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Activity, Coverage, Review, and Storage presentation. Artifacts: $ArtifactDirectory"
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
$process = $null
$window = $null
function Wait-For([scriptblock]$read) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try { $value = & $read; if ($value) { return $value } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Presentation assertion timed out.'
}
function Find([string]$name, [switch]$Id) {
    $prop = if ($Id) { [System.Windows.Automation.AutomationElement]::AutomationIdProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($prop, $name))
}
function Invoke([string]$name, [switch]$Id) {
    (Wait-For { Find $name -Id:$Id }).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Field([string]$id, [string]$value) {
    (Wait-For { Find $id -Id }).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Expand($element) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
    $control = $element.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $condition)
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) { throw 'Details must start collapsed.' }
    $pattern.Expand()
}
Initialize-UiReport $ArtifactDirectory 'Presentation'
try {
    $reviewDirectory = Join-Path $FixtureRoot '.sg/reviews'
    $reviewHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('source'))).ToLowerInvariant()
    $reviewPath = Join-Path $reviewDirectory ($reviewHash + '.json')
    $originalReview = if (Test-Path -LiteralPath $reviewPath) { [IO.File]::ReadAllText($reviewPath) } else { $null }
    $null = New-Item -ItemType Directory -Path $reviewDirectory -Force
    $sampleOutput = "Build failed`nMissing test dependency`n" + ((1..60 | ForEach-Object { "Detail line $_" }) -join "`n") + "`nBUILD failed again"
    @{ branch = 'source'; head = 'recorded-version'; snapshot = 'recorded-snapshot'; version = 'old'; configuration = 'old';
        checked = '2020-01-01T12:00:00Z'; files = @(); checks = @(
            @{ name = 'Build'; command = 'example-build --verify'; exitCode = 2; seconds = 1.2; output = $sampleOutput },
            @{ name = ''; command = 'example-lint'; exitCode = 0; seconds = 0.3; output = '' }
        ) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reviewPath -Encoding utf8
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Start-UiScenario 'Activity status and collapsed details'
    (Wait-For { Find 'ActivityItem' -Id }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Expand (Wait-For { Find 'Steps and checkpoint' })
    $null = Wait-For { Find 'View branch history' }
    $beforeRecovery = @(Get-ChildItem -LiteralPath $FixtureRoot -Directory -Filter '*-recovered-*').Count
    Invoke 'Restore commits to a separate branch'
    $null = Wait-For { Find 'Restore checkpoint to a new branch?' }
    $dialogText = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like 'This will:*Create branch*Create its worktree*Restore the recorded commits*' } | Select-Object -First 1
    if (!$dialogText) { throw 'Recovery confirmation must explain its branch, destination, and checkpoint.' }
    Invoke 'CloseButton' -Id
    if (@(Get-ChildItem -LiteralPath $FixtureRoot -Directory -Filter '*-recovered-*').Count -ne $beforeRecovery) { throw 'Cancelling recovery created a worktree.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'activity.png')
    Invoke 'NavigationViewBackButton' -Id
    $branch = Wait-For { Find 'source' }
    $parent = $branch
    $scroll = $null
    while ($parent) {
        if ($parent.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) { break }
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
    }
    if ($scroll) { $scroll.SetScrollPercent(-1, 100) }
    Expand $branch
    $advanced = Wait-For { if ($scroll) { $scroll.SetScrollPercent(-1, 100) }; Find 'AdvancedWorktreeActions' -Id }
    Expand $advanced
    $button = Wait-For {
        $action = Find 'BackupCoverageAction' -Id
        if ($action -and $action.Current.BoundingRectangle.Height -gt 0 -and $action.Current.BoundingRectangle.Top -ge $parent.Current.BoundingRectangle.Top) { return $action }
        if ($scroll) { $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::SmallDecrement) }
    }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-UiScenario 'Coverage check and restore distinction'
    Invoke 'Check current coverage'
    $null = Wait-For { Find 'Restore not verified' }
    $advanced = Wait-For { Find 'Restore testing and handoff' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'coverage.png')
    Expand $advanced
    $null = Wait-For { Find 'Test restore in a separate branch' }
    Start-UiScenario 'Review readiness and collapsed configuration'
    Invoke 'Review readiness'
    $null = Wait-For { Find 'Run local checks' }
    $null = Wait-For { Find 'Changed since review' }
    $summary = Wait-For { Find 'ReviewCheckSummary' -Id }
    if ($summary.Current.Name -notlike '1 passed · 1 failed*') { throw 'Incorrect recorded check totals.' }
    Start-UiScenario 'Recorded check summaries and output navigation'
    Invoke 'ReviewCheckOutput_0' -Id
    $output = Wait-For { Find 'ReviewCheckOutputText' -Id }
    $value = $output.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if (($value.Current.Value -replace "`r`n?", "`n") -ne $sampleOutput -or !$value.Current.IsReadOnly) {
        throw "Recorded output was not preserved as read-only text: readonly=$($value.Current.IsReadOnly); actual=$($value.Current.Value | ConvertTo-Json -Compress); expected=$($sampleOutput | ConvertTo-Json -Compress)"
    }
    $null = Wait-For { Find 'Failed · exit code 2' }
    Set-Field 'OutputSearch' 'build'
    $null = Wait-For { Find 'Match 1 of 2 · line 1' }
    Invoke 'OutputNextMatch' -Id
    $null = Wait-For { Find 'Match 2 of 2 · line 63' }
    $selection = $output.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).GetSelection()[0]
    if ($selection.GetText(-1) -ne 'BUILD') { throw 'Search did not select the matching text.' }
    $null = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'VerticalScrollBar')
        $bar = $output.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($bar) {
            $range = $bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current
            $range.Value -gt ($range.Maximum - $range.Minimum) / 2
        }
    }
    Start-Sleep -Milliseconds 300
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'output-search.png')
    Invoke 'OutputNextMatch' -Id
    $null = Wait-For { Find 'Match 1 of 2 · line 1' }
    Invoke 'OutputPreviousMatch' -Id
    $null = Wait-For { Find 'Match 2 of 2 · line 63' }
    Set-Field 'OutputSearch' '.*'
    $null = Wait-For { Find 'No matches' }
    if ((Find 'OutputNextMatch' -Id).Current.IsEnabled) { throw 'Navigation must be disabled without matches.' }
    Set-Field 'OutputSearch' ''
    if ((Find 'OutputPreviousMatch' -Id).Current.IsEnabled) { throw 'Clearing search must disable navigation.' }
    Set-Field 'OutputSearch' 'build'
    Expand (Wait-For { Find 'Command' })
    $null = Wait-For { Find 'example-build --verify' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'check-output.png')
    Invoke 'NavigationViewBackButton' -Id
    Invoke 'ReviewCheckOutput_1' -Id
    $null = Wait-For { Find 'No output was captured.' }
    $null = Wait-For { Find 'Check 2' }
    Invoke 'NavigationViewBackButton' -Id
    $null = Wait-For { Find 'ReviewCheckSummary' -Id }
    $config = Wait-For { Find 'Configure local checks' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'review.png')
    Expand $config
    $null = Wait-For { Find 'Save local check configuration' }
    Start-UiScenario 'Editable check list preserves literal arguments and order'
    $configPath = Join-Path $FixtureRoot '.sg/sg.json'
    $originalConfig = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
    if ($originalConfig.reviewChecks.Count -gt 0) { throw 'Use a disposable fixture with no configured review checks.' }
    Invoke 'AddReviewCheck' -Id
    $null = Wait-For { $save = Find 'SaveReviewChecks' -Id; $save -and !$save.Current.IsEnabled }
    Set-Field 'ReviewCheckName_0' 'Literal arguments'
    Set-Field 'ReviewCheckExecutable_0' 'never-run-this-test-command.exe'
    Invoke 'AddReviewArgument_0' -Id
    Set-Field 'ReviewCheckArgument_0_0' 'folder with spaces'
    Invoke 'AddReviewArgument_0' -Id
    Set-Field 'ReviewCheckArgument_0_1' '"quoted value"'
    Invoke 'AddReviewArgument_0' -Id
    Invoke 'AddReviewCheck' -Id
    Set-Field 'ReviewCheckName_1' 'First check'
    Set-Field 'ReviewCheckExecutable_1' 'another-never-run-command.exe'
    Invoke 'MoveReviewCheckUp_1' -Id
    Invoke 'SaveReviewChecks' -Id
    $null = Wait-For {
        $saved = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
        $saved.reviewChecks.Count -eq 2 -and $saved.reviewChecks[0].name -eq 'First check' -and
        $saved.reviewChecks[1].arguments.Count -eq 3 -and
        $saved.reviewChecks[1].arguments[0] -eq 'folder with spaces' -and
        $saved.reviewChecks[1].arguments[1] -eq '"quoted value"' -and $saved.reviewChecks[1].arguments[2] -eq ''
    }
    Expand (Wait-For {
        $section = Find 'Configure local checks'
        if ($section) {
            $pattern = $section.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { return $section }
        }
    })
    $null = Wait-For { Find 'ReviewCheckExecutable_1' -Id }
    $ancestor = Find 'ReviewCheckExecutable_0' -Id
    while ($ancestor) {
        $scroll = $null
        if ($ancestor.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) {
            $scroll.SetScrollPercent(-1, 50)
            break
        }
        $ancestor = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor)
    }
    Start-Sleep -Milliseconds 500 # Allow the expansion animation to finish before capturing pixels.
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'check-editor.png')
    Invoke 'RemoveReviewCheck_1' -Id
    Invoke 'RemoveReviewCheck_0' -Id
    Invoke 'SaveReviewChecks' -Id
    $null = Wait-For { (Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json).reviewChecks.Count -eq 0 }
    Invoke 'NavigationViewBackButton' -Id
    $null = Wait-For { Find 'Check current coverage' }
    Invoke 'Open backup restore preview'
    $null = Wait-For { Find 'BackupNowButton' -Id }
    Start-UiScenario 'Storage previews and recovery navigation'
    (Wait-For { Find 'StorageItem' -Id }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $archive = Wait-For { Find 'Archive options · source' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'storage.png')
    Expand $archive
    $null = Wait-For { Find 'Archive and remove this worktree' }
    Invoke 'View recovery checkpoints'
    $null = Wait-For { Find 'Steps and checkpoint' }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($reviewPath) {
        if ($null -ne $originalReview) { [IO.File]::WriteAllText($reviewPath, $originalReview) }
        elseif (Test-Path -LiteralPath $reviewPath) { Remove-Item -LiteralPath $reviewPath }
    }
    if ($originalConfig) {
        $current = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
        $current | Add-Member -NotePropertyName reviewChecks -NotePropertyValue @($originalConfig.reviewChecks) -Force
        $current | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding utf8
    }
}
