# Real local SVN/Git fixtures; GUI actions use Windows UI Automation patterns only.
param(
    [string]$ReportDirectory,
    [string]$FixtureParent = $env:TEMP,
    [string]$AppExe = "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe",
    [string]$CliDll = "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.dll"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
. "$PSScriptRoot/ui-automation.ps1"
. "$PSScriptRoot/ui-test-report.ps1"
$appPath = (Resolve-Path -LiteralPath $AppExe).Path
$cliPath = (Resolve-Path -LiteralPath $CliDll).Path
if ($appPath -notmatch '\\Debug\\') { throw 'Use an isolated Debug app.' }
if (!$env:SG_UI_TEST_DIRECTORY -and (Get-Process sg-ui -ErrorAction SilentlyContinue | Where-Object Path -eq $appPath)) { throw 'Close the existing Debug app first.' }
$fixture = Join-Path (Resolve-Path -LiteralPath $FixtureParent).Path ('sg-workflow-ui-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$root = Join-Path $fixture 'root'
$checkout = Join-Path $root 'checkout'
$repo = Join-Path $fixture 'svnrepo'
$backup = Join-Path $fixture 'backup.git'
$settingsPath = if ($env:SG_UI_TEST_DIRECTORY) { Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json' } else { Join-Path $env:LOCALAPPDATA 'sg/app.json' }
$previous = if (Test-Path -LiteralPath $settingsPath) { Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json } else { $null }
$script:process = $null
$script:window = $null
function Run([string]$exe, [string[]]$arguments) {
    $output = & $exe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$exe failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)" }
    $output -join "`n"
}
function Sg([string[]]$arguments) { Run 'dotnet' (@($cliPath) + $arguments + @('--root', $root)) }
function Wait-For([string]$description, [scriptblock]$read) {
    $deadline = (Get-Date).AddSeconds(60)
    do {
        try { $observed = & $read; if ($observed) { return $observed } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Timed out: $description"
}
function Find-Ui([string]$value, [switch]$Name, [switch]$Invokable, [switch]$IncludeOffscreen) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    # WebView's provider can stall or truncate FindAll before the native footer. Walk native controls
    # without entering the embedded browser; all workflow actions belong to the XAML host.
    function Find-Native($parent) {
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $child = $walker.GetFirstChild($parent)
        while ($child) {
            if ($child.GetCurrentPropertyValue($property) -eq $value -and
                ($IncludeOffscreen -or $Invokable -or !$child.Current.IsOffscreen) -and
                (!$Invokable -or $child.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty))) { return $child }
            if ($child.Current.AutomationId -ne 'Web') {
                $match = Find-Native $child
                if ($match) { return $match }
            }
            $child = $walker.GetNextSibling($child)
        }
    }
    Find-Native $script:window
}
function Invoke-Ui([string]$value, [switch]$Name) {
    $element = Wait-For "enabled $value" { $b = Find-Ui $value -Name:$Name -Invokable; if ($b -and $b.Current.IsEnabled) { $b } }
    if ($element.Current.IsOffscreen) {
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element)
        while ($parent) {
            $pattern = $null
            if ($parent.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and $pattern.Current.VerticallyScrollable) {
                $pattern.SetScrollPercent(-1, 100)
                break
            }
            $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
        }
        $null = Wait-For "visible $value" { !$element.Current.IsOffscreen }
    }
    Write-Host "Invoke: $value"
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Ui([string]$id, [string]$value) {
    (Wait-For $id { Find-Ui $id }).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Expand-Worktree([string]$name) {
    $card = Wait-For 'worktree card' { Find-Ui ('BackupWorktree_' + $name) -IncludeOffscreen }
    $expander = $card.FindFirst([System.Windows.Automation.TreeScope]::Subtree,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true))
    if (!$expander) { throw "Worktree card has no accessible expander: $name" }
    $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
}
function Assert-BackupStatus([string]$name, [string]$status, [bool]$confirmed = $false) {
    $null = Wait-For "$name status: $status" {
        $card = Find-Ui ('BackupWorktree_' + $name) -IncludeOffscreen
        if (!$card) { return $false }
        $label = $card.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $status))
        if (!$label) { return $false }
        # The badge is a glyph; its words are its name and its sentence, with when it was confirmed, is its help.
        !$confirmed -or $label.Current.HelpText -like '*Confirmed here *'
    }
}
function Assert-Blocked([string]$buttonId, [string]$explanation) {
    $null = Wait-For "validation: $explanation" {
        $button = Find-Ui $buttonId
        $summary = Find-Ui 'Summary'
        $button -and !$button.Current.IsEnabled -and $summary -and $summary.Current.Name -like $explanation -and
            $button.Current.HelpText -like $explanation
    }
}
function Cancel-QueuedForm([string]$buttonId, [string]$labelId, [string]$retryLabel, [string[]]$fields, [int]$finishedCount) {
    $savedName = (Find-Ui 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    $enabled = @{}
    foreach ($field in $fields) { $enabled[$field] = (Find-Ui $field -IncludeOffscreen).Current.IsEnabled }
    $formLock = [IO.File]::Open((Join-Path $root '.sg/sg.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Invoke-Ui $buttonId
        Invoke-Ui 'PrimaryButton'
        foreach ($field in $fields) {
            $null = Wait-For "locked form field $field" { $element = Find-Ui $field -IncludeOffscreen; $element -and !$element.Current.IsEnabled -and $element.Current.HelpText -like 'Inputs are locked*Tasks*' }
        }
        Invoke-Ui 'TaskQueueToggle'
        Invoke-Ui 'Cancel task' -Name
        Wait-Receipt $finishedCount
        $null = Wait-For 'retry action after cancellation' {
            $button = Find-Ui $buttonId
            $button -and $button.Current.IsEnabled -and (Find-Ui $labelId).Current.Name -eq $retryLabel -and $button.Current.Name -eq $retryLabel
        }
        foreach ($field in $fields) {
            if ((Find-Ui $field -IncludeOffscreen).Current.IsEnabled -ne $enabled[$field]) { throw "Input availability changed after cancellation: $field" }
        }
        if ((Find-Ui 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $savedName) { throw 'Cancelled form lost its submitted name.' }
        Invoke-Ui 'TaskQueueToggle'
        Set-Ui 'NameBox' ($savedName + '-different')
        $null = Wait-For 'edited destination is a new request' {
            $button = Find-Ui $buttonId
            $button -and $button.Current.IsEnabled -and $button.Current.Name -notlike 'Retry*' -and $button.Current.Name -eq (Find-Ui $labelId).Current.Name
        }
        Set-Ui 'NameBox' $savedName
        $null = Wait-For 'original destination remains retryable' { (Find-Ui $buttonId).Current.Name -eq $retryLabel }
    } finally { $formLock.Dispose() }
}
function Assert-DestinationAction([string]$label, [string]$path) {
    $null = Wait-For "destination action: $label" {
        $action = Find-Ui 'ExistingDestinationAction'
        $action -and $action.Current.IsEnabled -and $action.Current.Name -eq $label -and $action.Current.HelpText -eq $path
    }
}
function Stop-App {
    if ($script:process -and !$script:process.HasExited) { Stop-Process -Id $script:process.Id; $script:process.WaitForExit() }
    $script:process = $null
    $script:window = $null
}
function Start-App([string]$action, [string]$path) {
    Stop-App
    $script:process = Start-Process -FilePath $appPath -ArgumentList @($action, ('"' + $path + '"')) -WorkingDirectory $root -PassThru
    $script:window = Wait-For 'app window' { Get-TestAppWindow $script:process }
}
function Wait-Receipt([int]$count = 1) {
    $null = Wait-For 'finished task receipt' {
        $summary = Find-Ui 'TaskQueueSummary'
        $summary -and $summary.Current.Name -like "*0 active*$count finished*"
    }
}
function Commit-File([string]$branch, [string]$file, [string]$content) {
    $path = Join-Path $root $branch
    [IO.File]::WriteAllText((Join-Path $path $file), $content)
    $null = Run 'git' @('-C', $path, 'add', '--', $file)
    $null = Run 'git' @('-C', $path, '-c', 'user.name=UI Test', '-c', 'user.email=ui-test@example.invalid', 'commit', '-m', ('Test ' + $file))
}
Initialize-UiReport $ReportDirectory 'Workflows'
try {
    Start-UiScenario 'Fixture setup'
    # Keep scheduled backups from racing the operation this test deliberately holds at a lock.
    $testSettings = if ($previous) { $previous | ConvertTo-Json -Depth 20 | ConvertFrom-Json } else { [pscustomobject]@{} }
    $testSettings | Add-Member -NotePropertyName BackupMinutes -NotePropertyValue 0 -Force
    $testSettings | Add-Member -NotePropertyName RecentRoots -NotePropertyValue @($previous.RecentRoots | Where-Object { $_ }) -Force
    $null = New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force
    $testSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    $null = New-Item -ItemType Directory -Path $root
    $null = Run 'svnadmin' @('create', $repo)
    $url = ([Uri]($repo + '/')).AbsoluteUri.TrimEnd('/')
    $stage = Join-Path $fixture 'seed'
    $null = New-Item -ItemType Directory -Path $stage
    [IO.File]::WriteAllText((Join-Path $stage 'base.txt'), "base`n")
    $null = Run 'svn' @('import', '--non-interactive', $stage, ($url + '/trunk'), '-m', 'Initial fixture')
    $null = Run 'dotnet' @($cliPath, 'init', $root, '--no-fsmonitor')
    $null = Sg @('checkout', 'add', '--url', ($url + '/trunk'), $checkout, '--name', 'checkout')
    $null = Run 'git' @('init', '--bare', $backup)
    $null = Sg @('backup', 'set', $backup)
    $null = Sg @('branch', 'source', '--from', 'checkout')
    Commit-File 'source' 'feature.txt' "imported feature`n"
    $export = Join-Path $root 'source.sgexport'
    $null = Sg @('export', (Join-Path $root 'source'), '--out', $export)
    $sourceMoved = Join-Path $root 'source-moved'
    $store = (Run 'git' @('-C', (Join-Path $root 'source'), 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
    # Windows can reject Git's rewrite of a hidden .git link during worktree move.
    # Change only this disposable fixture's link, without changing the user's Git configuration.
    $sourceLink = Get-Item -Force -LiteralPath (Join-Path $root 'source/.git')
    $sourceLink.Attributes = $sourceLink.Attributes -band (-bnot [IO.FileAttributes]::Hidden)
    $null = Run 'git' @('--git-dir', $store, 'worktree', 'move', (Join-Path $root 'source'), $sourceMoved)
    $null = Run 'git' @('-C', $sourceMoved, 'branch', 'unattached', 'HEAD')

    # Import through the actual preview and confirmation UI, then verify its materialized files.
    Start-UiScenario 'Export import and retry'
    Start-App 'import' $export
    $null = Wait-For 'checkout precedes import branch name' { (Find-Ui 'IntoBox').Current.BoundingRectangle.Left -lt (Find-Ui 'NameBox').Current.BoundingRectangle.Left }
    Set-Ui 'NameBox' 'source'
    Assert-Blocked 'ImportButton' '*already a branch*'
    Assert-DestinationAction 'Open folder' $sourceMoved
    Set-Ui 'NameBox' 'unattached'
    Assert-Blocked 'ImportButton' '*already a branch*'
    if (Find-Ui 'ExistingDestinationAction') { throw 'Branch without a worktree offers a guessed folder.' }
    Set-Ui 'NameBox' 'pending-import'
    Set-Ui 'NameBox' ''
    Assert-Blocked 'ImportButton' '*Give the branch a name*'
    Set-Ui 'NameBox' 'bad..name'
    Assert-Blocked 'ImportButton' '*valid Git branch name*'
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'occupied')
    Set-Ui 'NameBox' 'occupied'
    Assert-Blocked 'ImportButton' '*destination folder already exists*'
    Assert-DestinationAction 'Open folder' (Join-Path $root 'occupied')
    Set-Ui 'NameBox' 'invalid name'
    Set-Ui 'NameBox' 'imported'
    # The preview stays valid, but execution must report a missing source and retain a retryable form.
    [IO.File]::Move($export, $export + '.held')
    try {
        Invoke-Ui 'ImportButton'
        Invoke-Ui 'PrimaryButton'
        Wait-Receipt
        $null = Wait-For 'retry action after import failure' { (Find-Ui 'ImportLabel').Current.Name -eq 'Retry import' }
    } finally { [IO.File]::Move($export + '.held', $export) }
    Cancel-QueuedForm 'ImportButton' 'ImportLabel' 'Retry import' @('NameBox', 'IntoBox', 'PickButton') 2
    Invoke-Ui 'ImportButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt 3
    Assert-Blocked 'ImportButton' 'Done.*'
    $null = Wait-For 'shared import result on the page' {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'imported: *commits imported.*' -and $_.Current.Name.Contains((Join-Path $root 'imported')) } |
            Select-Object -First 1
    }
    $imported = Join-Path $root 'imported'
    if ([IO.File]::ReadAllText((Join-Path $imported 'feature.txt')) -ne "imported feature`n") { throw 'Import lost exported content.' }
    if ((Run 'git' @('-C', $imported, 'status', '--porcelain')).Trim()) { throw 'Imported worktree is dirty.' }
    Write-Host 'PASS: sgexport import preview, confirmation, task receipt, and file content.'
    Stop-App

    # Sync real changes from a second SVN working copy, then update a branch through the GUI.
    Start-UiScenario 'Pull from SVN'
    $other = Join-Path $fixture 'other'
    $null = Run 'svn' @('checkout', '--non-interactive', ($url + '/trunk'), $other)
    [IO.File]::WriteAllText((Join-Path $other 'base.txt'), "fresh SVN content`n")
    $null = Run 'svn' @('commit', '--non-interactive', $other, '-m', 'Fresh upstream content')
    Start-App 'rebase' $imported
    # The revisions and the log link start folded away; the page keeps them open across navigation.
    $revisions = Wait-For 'structured update plan' { Find-Ui 'PullRevisions' }
    $revisions.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $null = Wait-For 'SVN revisions unfolded' { Find-Ui 'View SVN log' -Name -Invokable }
    if ($ReportDirectory) { try { Save-UiWindow $script:window (Join-Path $ReportDirectory 'update-plan.png') } catch { Write-Host "Optional preview capture: $_" } }
    Invoke-Ui 'Review local commits' -Name
    $null = Wait-For 'local commit log opened' { Find-Ui 'Commits' }
    Invoke-Ui 'NavigationViewBackButton'
    $null = Wait-For 'update plan restored after log navigation' { Find-Ui 'Refresh pull plan' -Name -Invokable }
    Invoke-Ui 'View SVN log' -Name
    $null = Wait-For 'SVN log opened' { Find-Ui 'Revisions' }
    Invoke-Ui 'NavigationViewBackButton'
    $null = Wait-For 'update plan restored after SVN navigation' { Find-Ui 'Refresh pull plan' -Name -Invokable }
    Invoke-Ui 'PullFromSvnButton'
    Wait-Receipt
    $null = Run 'git' @('-C', $imported, 'merge-base', '--is-ancestor', 'svn/checkout', 'HEAD')
    if ([IO.File]::ReadAllText((Join-Path $imported 'base.txt')) -ne "fresh SVN content`n") { throw 'Update missed fresh SVN content.' }
    if ([IO.File]::ReadAllText((Join-Path $imported 'feature.txt')) -ne "imported feature`n") { throw 'Update lost branch content.' }
    Write-Host 'PASS: update from fresh SVN retains local commits and advances the base.'
    Stop-App

    # Make the first replayed commit empty using real Git, followed by a commit that must survive Skip.
    Start-UiScenario 'Skip an empty commit'
    $null = Sg @('branch', 'empty-step', '--from', 'checkout')
    Commit-File 'empty-step' 'base.txt' "same change on both sides`n"
    Commit-File 'empty-step' 'later.txt' "remaining commit survives`n"
    [IO.File]::WriteAllText((Join-Path $other 'base.txt'), "same change on both sides`n")
    $null = Run 'svn' @('commit', '--non-interactive', $other, '-m', 'Apply equivalent upstream change')
    $null = Sg @('sync', 'checkout')
    $empty = Join-Path $root 'empty-step'
    $stopped = & git -C $empty -c user.name='UI Test' -c user.email='ui-test@example.invalid' rebase --reapply-cherry-picks --empty=stop svn/checkout 2>&1
    if ($LASTEXITCODE -eq 0) { throw 'Fixture did not stop on an empty commit.' }
    if ((Run 'git' @('-C', $empty, 'diff', '--name-only', '--diff-filter=U')).Trim()) { throw 'Fixture unexpectedly has conflicting files.' }
    Start-App 'resolve' $empty
    Invoke-Ui 'SkipButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt
    if ([IO.File]::ReadAllText((Join-Path $empty 'later.txt')) -ne "remaining commit survives`n") { throw 'Skip lost remaining commits.' }
    $gitDir = (Run 'git' @('-C', $empty, 'rev-parse', '--absolute-git-dir')).Trim()
    if (Test-Path -LiteralPath (Join-Path $gitDir 'rebase-merge')) { throw 'Rebase is still paused after Skip.' }
    Write-Host 'PASS: zero-conflict empty commit has an enabled Skip action and replays later commits.'
    Stop-App

    # Back up via GUI to a local bare repository, checking content rather than just a success label.
    Start-UiScenario 'Create backup'
    Start-App 'backup' $root
    # Every worktree at once is the rare action, behind the page's "..." like the checkout's own.
    Invoke-Ui 'MoreButton'
    Invoke-Ui 'BackupAll'
    Wait-Receipt
    # Backups rewrite history, so verify the tree content rather than comparing commit IDs.
    $remoteFeature = Run 'git' @('--git-dir', $backup, 'show', 'refs/heads/imported:feature.txt')
    if ($remoteFeature.Trim() -ne 'imported feature') { throw 'Backup lost branch content.' }
    Write-Host 'PASS: backup task publishes branch content to the local backup repository.'

    Start-UiScenario 'Restore backup and retry'
    Invoke-Ui 'BackupWorktreeOpen_imported'
    $null = Wait-For 'checkout precedes restore branch name' { (Find-Ui 'IntoBox').Current.BoundingRectangle.Left -lt (Find-Ui 'NameBox').Current.BoundingRectangle.Left }
    Set-Ui 'NameBox' 'imported'
    Assert-Blocked 'RestoreButton' '*already a branch*'
    Assert-DestinationAction 'Open folder' (Join-Path $root 'imported')
    # Replacing a branch is an advanced restore option, folded away until asked for.
    $advanced = Wait-For 'advanced restore options' { Find-Ui 'RestoreAdvanced' }
    $advanced.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $force = Wait-For 'replace existing option' { Find-Ui 'ForceBox' }
    $force.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $null = Wait-For 'replacement explains recovery' {
        $button = Find-Ui 'RestoreButton'
        $button -and $button.Current.IsEnabled -and $button.Current.HelpText -like '*preserved under a recovery name*'
    }
    $force.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Assert-Blocked 'RestoreButton' '*already a branch*'
    Set-Ui 'NameBox' 'pending-restore'
    Set-Ui 'NameBox' ''
    Assert-Blocked 'RestoreButton' '*Give the branch a name*'
    Set-Ui 'NameBox' 'bad..name'
    Assert-Blocked 'RestoreButton' '*valid Git branch name*'
    Set-Ui 'NameBox' 'occupied'
    Assert-Blocked 'RestoreButton' '*destination folder already exists*'
    Set-Ui 'NameBox' 'invalid name'
    Set-Ui 'NameBox' 'restored-backup'
    Cancel-QueuedForm 'RestoreButton' 'RestoreLabel' 'Retry restore' @('NameBox', 'IntoBox', 'WipBox', 'ForceBox') 2
    Set-Ui 'NameBox' 'imported'
    $force.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $null = Wait-For 'replacement keeps an explicit overwrite label after a cancelled restore' {
        $button = Find-Ui 'RestoreButton'
        $button -and $button.Current.IsEnabled -and $button.Current.Name -like 'Overwrite with*' -and $button.Current.Name -eq (Find-Ui 'RestoreLabel').Current.Name
    }
    Cancel-QueuedForm 'RestoreButton' 'RestoreLabel' 'Retry overwrite with 1 commit(s)' @('NameBox', 'IntoBox', 'WipBox', 'ForceBox') 3
    $force.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Set-Ui 'NameBox' 'restored-backup'
    Invoke-Ui 'RestoreButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt 4
    Assert-Blocked 'RestoreButton' 'Done.*'
    $null = Wait-For 'shared restore result on the page' {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'restored-backup: *commits recovered.*' -and $_.Current.Name.Contains((Join-Path $root 'restored-backup')) } |
            Select-Object -First 1
    }
    $restored = Join-Path $root 'restored-backup'
    if ([IO.File]::ReadAllText((Join-Path $restored 'feature.txt')) -ne "imported feature`n") { throw 'Backup restore lost branch content.' }
    if ([IO.File]::ReadAllText((Join-Path $restored 'base.txt')) -ne "same change on both sides`n") { throw 'Backup restore missed the fresh snapshot.' }
    $null = Run 'git' @('-C', $restored, 'merge-base', '--is-ancestor', 'svn/checkout', 'HEAD')
    Write-Host 'PASS: backup restore merges the saved branch onto the fresh SVN snapshot.'
    # The retained backup receipt must return to its page after navigating elsewhere.
    Start-UiScenario 'Navigate from a task receipt'
    (Find-Ui 'SettingsItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Ui 'TaskQueueToggle'
    $backupTask = Wait-For 'backup receipt' {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)) |
            Where-Object { $_.Current.AutomationId -like 'Task_*' -and $_.Current.Name -like '*Completed · backup all worktrees ·*' } | Select-Object -First 1
    }
    $backupTask.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Invoke-Ui ('TaskResult_' + $backupTask.Current.AutomationId.Substring(5))
    $null = Wait-For 'backup page reopened from receipt' { Find-Ui 'BackupActions' }
    Write-Host 'PASS: retained task action reopens Backup after navigation.'
    Stop-App
    # A real add/add conflict keeps the import resumable, even after editing the destination form.
    Start-UiScenario 'Resume a paused import'
    [IO.File]::WriteAllText((Join-Path $other 'feature.txt'), "different upstream feature`n")
    $null = Run 'svn' @('add', (Join-Path $other 'feature.txt'))
    $null = Run 'svn' @('commit', '--non-interactive', $other, '-m', 'Conflicting upstream feature')
    $null = Sg @('sync', 'checkout')
    Start-App 'import' $export
    Set-Ui 'NameBox' 'paused-import'
    Invoke-Ui 'ImportButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt
    $null = Wait-For 'paused import guidance' {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'paused-import: *remaining commits are queued*' } | Select-Object -First 1
    }
    Set-Ui 'NameBox' 'different-form-name'
    Invoke-Ui 'ResolveButton'
    $null = Wait-For 'resolver retains original import destination' {
        $subtitle = Find-Ui 'PART_SubtitleText'
        $subtitle -and $subtitle.Current.Name.Contains((Join-Path $root 'paused-import'))
    }
    Invoke-Ui 'PART_BackButton'
    # The page keeps the destination draft across navigation, so the edited name comes back with it.
    $null = Wait-For 'import preview finishes reloading after back, with its draft' {
        $field = Find-Ui 'NameBox'
        $field -and $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'different-form-name'
    }
    Set-Ui 'NameBox' 'paused-import'
    Assert-Blocked 'ImportButton' '*already a branch*'
    Assert-DestinationAction 'Review replay' (Join-Path $root 'paused-import')
    Invoke-Ui 'ExistingDestinationAction'
    $null = Wait-For 'destination action opens the original replay' {
        $subtitle = Find-Ui 'PART_SubtitleText'
        $subtitle -and $subtitle.Current.Name.Contains((Join-Path $root 'paused-import'))
    }
    Invoke-Ui 'SkipButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt 2
    $paused = Join-Path $root 'paused-import'
    if ((Run 'git' @('-C', $paused, 'diff', '--name-only', '--diff-filter=U')).Trim()) { throw 'Import still has unmerged files after Skip.' }
    if (Test-Path -LiteralPath (Join-Path $root 'different-form-name')) { throw 'Resume used the edited destination.' }
    Write-Host 'PASS: paused import keeps shared replay guidance and resumes its original destination.'

    Start-UiScenario 'Back up one local worktree without sending unrelated changes'
    Stop-App
    $null = Sg @('branch', 'scoped-local', '--from', 'checkout')
    Commit-File 'scoped-local' 'scoped.txt' "selected worktree`n"
    Commit-File 'imported' 'unsent.txt' "another worktree stays local`n"
    $beforeOther = (Run 'git' @('--git-dir', $backup, 'rev-parse', 'refs/heads/imported')).Trim()
    Start-App 'backup' $root
    Set-Ui 'BackupSearch' 'scoped-local'
    $null = Wait-For 'local worktree search finishes' { (Find-Ui 'BackupMatches').Current.Name -like '1 of *' }
    Assert-BackupStatus 'scoped-local' 'Local only'
    Expand-Worktree 'scoped-local'
    $null = Wait-For 'local-only restore is unavailable' { $b = Find-Ui 'BackupWorktreeRestore_scoped-local' -IncludeOffscreen; $b -and !$b.Current.IsEnabled }
    Invoke-Ui 'BackupWorktreeSend_scoped-local'
    Wait-Receipt
    if ((Run 'git' @('--git-dir', $backup, 'show', 'refs/heads/scoped-local:scoped.txt')).Trim() -ne 'selected worktree') { throw 'Scoped backup did not publish its worktree.' }
    if ((Run 'git' @('--git-dir', $backup, 'rev-parse', 'refs/heads/imported')).Trim() -ne $beforeOther) { throw 'Scoped backup sent unrelated work.' }
    Assert-BackupStatus 'scoped-local' 'Commits saved' $true
    if ($ReportDirectory) { Save-UiWindow $script:window (Join-Path $ReportDirectory 'worktree-backups.png') }
    Invoke-Ui 'TaskQueueToggle'
    $task = Wait-For 'scoped backup receipt' {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -like 'Task_*' -and $_.Current.Name -like '*Completed · backup scoped-local ·*' } | Select-Object -First 1
    }
    $task.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Invoke-Ui ('TaskResult_' + $task.Current.AutomationId.Substring(5))
    $null = Wait-For 'receipt returns to selected worktree options' { $field = Find-Ui 'NameBox'; $field -and $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'scoped-local' }
    if ($ReportDirectory) { Save-UiWindow $script:window (Join-Path $ReportDirectory 'selected-backup.png') }

    Start-UiScenario 'Prune one remote worktree with an exact confirmation'
    Stop-App
    $tip = (Run 'git' @('--git-dir', $backup, 'rev-parse', 'refs/heads/scoped-local')).Trim()
    $null = Run 'git' @('--git-dir', $backup, 'update-ref', 'refs/heads/remote-selected', $tip)
    $null = Run 'git' @('--git-dir', $backup, 'update-ref', 'refs/heads/remote-untouched', $tip)
    Start-App 'backup' $root
    Set-Ui 'BackupSearch' 'remote-selected'
    $null = Wait-For 'remote worktree search finishes' { (Find-Ui 'BackupMatches').Current.Name -like '1 of *' }
    Expand-Worktree 'remote-selected'
    $null = Wait-For 'remote-only backup is unavailable' { $b = Find-Ui 'BackupWorktreeSend_remote-selected' -IncludeOffscreen; $b -and !$b.Current.IsEnabled }
    Invoke-Ui 'BackupWorktreePrune_remote-selected'
    $null = Wait-For 'selected prune confirmation' { Find-Ui 'PrimaryButton' }
    $texts = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { !$_.Current.IsOffscreen -and $_.Current.Name -like 'Delete these*' } | ForEach-Object { $_.Current.Name }
    if (($texts -join '') -notlike '*refs/heads/remote-selected*' -or ($texts -join '') -like '*refs/heads/remote-untouched*') { throw 'Prune confirmation does not isolate the selected backup.' }
    if ($ReportDirectory) { Save-UiWindow $script:window (Join-Path $ReportDirectory 'selected-prune.png') }
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt
    $refs = Run 'git' @('--git-dir', $backup, 'for-each-ref', '--format=%(refname)')
    if ($refs.Contains('refs/heads/remote-selected') -or !$refs.Contains('refs/heads/remote-untouched')) { throw 'Prune changed the wrong remote worktree.' }
    Complete-UiScenario
    Write-Output "All workflow UI Automation checks passed. Fixture retained at $fixture"
}
catch {
    Fail-UiScenario $_ $script:window
    Write-Host $_.ScriptStackTrace
    Write-Host "Fixture retained for diagnosis: $fixture"
    throw
}
finally {
    Stop-App
    if (Test-Path -LiteralPath $settingsPath) {
        $current = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        if ($current.LastRoot -eq $root) {
            $current.LastRoot = $previous.LastRoot
            $current.RecentRoots = @(if ($previous) { $previous.RecentRoots })
        }
        if ($current.BackupMinutes -eq 0) {
            $current.BackupMinutes = if ($previous -and $null -ne $previous.BackupMinutes) { $previous.BackupMinutes } else { 15 }
        }
        $current | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    }
}
