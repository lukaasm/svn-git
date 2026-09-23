# Checks badge clipping and expansion layout on a private desktop using only UI Automation.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/backup-layout-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-backup-layout-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Backup layout test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Backup layout test failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Backup layout. Artifacts: $ArtifactDirectory"
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
    throw 'Backup layout assertion timed out.'
}
function Assert-Header {
    $card = Find 'BackupWorktree_source'
    $bounds = $card.Current.BoundingRectangle
    $labels = @($card.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    if (!($labels | Where-Object { $_.Current.Name -eq 'Excluded' })) { throw 'Fixture did not create the second status badge.' }
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $r = $labels[$i].Current.BoundingRectangle
        if ($r.Bottom -gt $bounds.Bottom - 5 -or $r.Top -lt $bounds.Top -or $r.Height -lt 10) { throw "Clipped card label: $($labels[$i].Current.Name); label $r, card $bounds" }
        for ($j = $i + 1; $j -lt $labels.Count; $j++) {
            $other = $labels[$j].Current.BoundingRectangle
            if ($r.Left -lt $other.Right -and $other.Left -lt $r.Right -and $r.Top -lt $other.Bottom -and $other.Top -lt $r.Bottom) {
                throw "Overlapping labels: $($labels[$i].Current.Name) and $($labels[$j].Current.Name)"
            }
        }
    }
}
$process = $null; $window = $null; $originalConfig = $null; $configFile = $null; $confirmed = $null
Initialize-UiReport $ArtifactDirectory 'Backup layout'
try {
    if ($FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable workflow fixture.' }
    $configFile = Join-Path $FixtureRoot '.sg/sg.json'
    $originalConfig = [IO.File]::ReadAllText($configFile)
    $config = $originalConfig | ConvertFrom-Json
    $config.backup.excluded = @('source')
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configFile
    $gitStore = Join-Path $FixtureRoot '.sg'
    $confirmed = (& git --git-dir=$gitStore config --get branch.source.sgBackedUp)
    & git --git-dir=$gitStore config --unset branch.source.sgBackedUp
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('backup', ('"' + $FixtureRoot + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $resizeHost = $window; $transform = $null
    while ($resizeHost -and !$resizeHost.TryGetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern, [ref]$transform)) {
        $resizeHost = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($resizeHost)
    }
    if (!$transform -or !$transform.Current.CanResize) { throw 'The test window must support UI Automation resizing.' }
    Start-UiScenario 'Backup badges fit and expanding a card keeps the column stable'
    $search = Wait-For { $field = Find 'BackupSearch'; if ($field -and !$field.Current.IsOffscreen) { $field } }
    $resizeHost.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    $window = Wait-For { Get-TestAppWindow $process }
    $search = Wait-For { Find 'BackupSearch' }
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('source')
    $null = Wait-For { (Find 'BackupMatches').Current.Name -like '1 of *' }
    $card = Find 'BackupWorktree_source'
    $labels = @($card.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'card-collapsed.png')
    $bounds = $card.Current.BoundingRectangle
    $labels | ForEach-Object { @{ text = $_.Current.Name; bounds = $_.Current.BoundingRectangle.ToString() } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'bounds.json')
    $columnBefore = (Find 'BackupSearch').Current.BoundingRectangle
    $expander = $card.FindFirst([System.Windows.Automation.TreeScope]::Subtree,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true))
    $expansion = $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expansion.Expand()
    $null = Wait-For { Find 'BackupWorktreeSend_source' }
    Start-Sleep -Milliseconds 500
    $columnExpanded = (Find 'BackupSearch').Current.BoundingRectangle
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'card-expanded.png')
    $expansion.Collapse()
    Start-Sleep -Milliseconds 500
    $columnAfter = (Find 'BackupSearch').Current.BoundingRectangle
    @{ before = $columnBefore.ToString(); expanded = $columnExpanded.ToString(); after = $columnAfter.ToString() } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'column.json')
    if ([Math]::Abs($columnBefore.X - $columnExpanded.X) -gt 1 -or [Math]::Abs($columnBefore.Width - $columnExpanded.Width) -gt 1 -or [Math]::Abs($columnBefore.X - $columnAfter.X) -gt 1) { throw 'Expanding a worktree changes the backup column position or width.' }
    Assert-Header
    Start-UiScenario 'Confirmation text stays below both status badges'
    if (!$confirmed) { throw 'Fixture needs a recorded confirmation time.' }
    & git --git-dir=$gitStore config branch.source.sgBackedUp $confirmed
    (Find 'SettingsItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Find 'NavigationViewBackButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $card = Find 'BackupWorktree_source'
        $card -and @($card.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Confirmed here*' }).Count -gt 0
    }
    Assert-Header
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'card-confirmed.png')
    Start-UiScenario 'Badges also fit at the normal window size'
    $resizeHost.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    $window = Wait-For { Get-TestAppWindow $process }
    $null = Wait-For { Find 'BackupWorktree_source' }
    Assert-Header
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'card-normal.png')
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($originalConfig) { [IO.File]::WriteAllText($configFile, $originalConfig) }
    if ($confirmed) { & git --git-dir=$gitStore config branch.source.sgBackedUp $confirmed }
}
