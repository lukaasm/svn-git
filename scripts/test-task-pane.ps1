# Programmatic Windows UI Automation smoke test; never moves the pointer or sends keystrokes.
# Run against a disposable sg root with at least one checkout. The successful worktree is retained.
param(
    [Parameter(Mandatory)][string]$FixtureRoot,
    [string]$ScreenshotDirectory,
    [string]$ReportDirectory,
    [switch]$CheckRecovery,
    [switch]$SkipClipboard,
    [string]$AppExe = "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
. "$PSScriptRoot/ui-automation.ps1"
. "$PSScriptRoot/ui-test-report.ps1"
$appPath = (Resolve-Path -LiteralPath $AppExe).Path
$rootPath = (Resolve-Path -LiteralPath $FixtureRoot).Path
if ($appPath -notmatch '\\Debug\\') { throw 'Use an isolated Debug build, not the installed app.' }
if (!$env:SG_UI_TEST_DIRECTORY -and (Get-Process sg-ui -ErrorAction SilentlyContinue | Where-Object Path -eq $appPath)) { throw 'Close the existing debug instance before running this test.' }
$config = Get-Content -Raw -LiteralPath (Join-Path $rootPath '.sg/sg.json') | ConvertFrom-Json
if (!$config.checkouts.Count) { throw 'The disposable root needs a checkout.' }
$settingsPath = if ($env:SG_UI_TEST_DIRECTORY) { Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json' } else { Join-Path $env:LOCALAPPDATA 'sg/app.json' }
$previousSettings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json } else { $null }
$script:window = $null
$process = $null
$heldLock = $null
$recoveryFile = $null

function Find-Element($property, $value) {
    $condition = [System.Windows.Automation.PropertyCondition]::new($property, $value)
    $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function By-Id([string]$id) { Find-Element ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $id }
function By-Name([string]$name) { Find-Element ([System.Windows.Automation.AutomationElement]::NameProperty) $name }
function Wait-For([string]$description, [scriptblock]$read) {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        try { $value = & $read; if ($value) { return $value } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Timed out: $description"
}
function Invoke-Element($element) {
    Write-Host ("Invoke: " + $element.Current.Name + " (" + $element.Current.AutomationId + ")")
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Select-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Start-Worktree([string]$name, [switch]$CheckValidation, [switch]$Retry) {
    if ($Retry) { Invoke-Element (Wait-For 'review and retry action' { By-Name 'Review and retry' }) }
    else { Invoke-Element (Wait-For 'enabled new worktree action' { $b = By-Name 'New worktree'; if ($b -and $b.Current.IsEnabled -and !$b.Current.IsOffscreen) { $b } }) }
    $branchInput = Wait-For 'branch name' { By-Name 'Branch name' }
    if ($Retry -and $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $name) { throw 'Worktree retry lost the submitted name.' }
    if ($CheckValidation) {
        foreach ($case in @(
            @{ Name = ''; Help = '*Give the branch a name*' },
            @{ Name = 'bad..name'; Help = '*valid Git branch name*' },
            @{ Name = $config.checkouts[0].name; Help = '*destination folder already exists*' }
        )) {
            $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($case.Name)
            $null = Wait-For 'new worktree validation' {
                $button = By-Id 'PrimaryButton'
                $summary = By-Id 'NewWorktreeSummary'
                $button -and !$button.Current.IsEnabled -and $summary.Current.Name -like $case.Help -and $branchInput.Current.HelpText -like $case.Help
            }
        }
        $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('invalid name')
    }
    if ($CheckValidation -or $Retry) {
        $minimal = By-Name ('Minimal: leave out the optional folders (' + ($config.checkouts[0].optional -join ', ') + ')')
        $without = By-Name 'Also leave out (folders, one per line)'
        if ($CheckValidation) {
            $minimal.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            $without.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('not-in-fixture')
        } else {
            if ($minimal.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On -or
                $without.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'not-in-fixture') { throw 'Worktree retry lost submitted options.' }
        }
    }
    $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
    $create = Wait-For 'enabled create button' { $b = By-Id 'PrimaryButton'; if ($b -and $b.Current.IsEnabled) { $b } }
    Invoke-Element $create
}
function Select-TaskFilter([int]$index) {
    (Wait-For 'task filter control' { By-Id 'TaskFilter' }).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Select-Element (Wait-For 'task filter item' { By-Id ('TaskFilter' + $index) })
}
function Task-Buttons {
    $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)) |
        Where-Object { $_.Current.AutomationId -like 'Task_*' }
}

# Optional target-window captures for visual review, located through UI Automation.
function Save-Window([string]$name) {
    if (!$ScreenshotDirectory) { return }
    Save-UiWindow $script:window (Join-Path $ScreenshotDirectory ($name + '.png'))
}

Initialize-UiReport $ReportDirectory 'Tasks'
try {
    Start-UiScenario 'Startup and fixture setup'
    # Keep scheduled backups from racing the operation this test deliberately holds at a lock.
    $testSettings = if ($previousSettings) { $previousSettings | ConvertTo-Json -Depth 20 | ConvertFrom-Json } else { [pscustomobject]@{} }
    $testSettings | Add-Member -NotePropertyName BackupMinutes -NotePropertyValue 0 -Force
    $testSettings | Add-Member -NotePropertyName RecentRoots -NotePropertyValue @($previousSettings.RecentRoots | Where-Object { $_ }) -Force
    $null = New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force
    $testSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    if ($CheckRecovery) {
        $recoveryId = [Guid]::NewGuid().ToString('N')
        $recoveryFolder = Join-Path $rootPath '.sg/operations'
        $null = New-Item -ItemType Directory -Path $recoveryFolder -Force
        $recoveryFile = Join-Path $recoveryFolder ($recoveryId + '.json')
        # A saved review record is sufficient to exercise startup recovery without changing repository data.
        @{ id = $recoveryId; kind = 'UI automation recovery'; branch = 'recovery-fixture';
           checkout = $config.checkouts[0].name; path = $config.checkouts[0].path;
           phase = 'needsReview'; detail = 'Saved edits need review after interruption.' } |
            ConvertTo-Json | Set-Content -LiteralPath $recoveryFile -Encoding utf8
    }
    $process = Start-Process -FilePath $appPath -ArgumentList @('overview', ('"' + $rootPath + '"')) -PassThru
    $script:window = Wait-For 'debug window' { Get-TestAppWindow $process }
    $sync = Wait-For 'checkout overview' { By-Id 'SyncButton' }
    if ($CheckRecovery) {
        Start-UiScenario 'Startup recovery'
        $button = Wait-For 'startup recovery action' { $b = By-Id 'RecoveryButton'; if ($b -and !$b.Current.IsOffscreen) { $b } }
        if ($button.Current.Name -ne 'Review saved edits') { throw 'Recovery action does not match the saved phase.' }
        Save-Window 'recovery'
        Invoke-Element $button
        $null = Wait-For 'saved recovery details' { By-Name 'Saved edits need review after interruption.' }
        Invoke-Element (By-Id 'NavigationViewBackButton')
        $null = Wait-For 'overview after recovery' { By-Id 'SyncButton' }
        Remove-Item -LiteralPath $recoveryFile
        $recoveryFile = $null
        Start-Sleep -Milliseconds 400
        Invoke-Element (By-Id 'RefreshButton')
        $null = Wait-For 'recovery notice clears after reconciliation' { $b = By-Id 'RecoveryButton'; !$b -or $b.Current.IsOffscreen }
    }
    Start-Sleep -Milliseconds 400
    Start-UiScenario 'Worktree placeholder and collision gating'
    Save-Window 'overview'
    $heldLock = [IO.File]::Open((Join-Path $rootPath '.sg/sg.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $name = 'task-ui-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    Start-Worktree $name -CheckValidation
    $null = Wait-For 'immediate worktree placeholder' { By-Name ($name + ' · Preparing worktree') }
    $null = Wait-For 'conflicting sync disabled' { !(By-Id 'SyncButton').Current.IsEnabled }
    $null = Wait-For 'disabled sync explains the blocking task' { (By-Id 'SyncButton').Current.HelpText -like "*$name*cancel*Tasks*" }
    if ((By-Id 'NewWorktreeButton').Current.HelpText -notlike "*$name*Tasks*") { throw 'New worktree has no blocking explanation.' }
    Invoke-Element (By-Id 'MoreButton')
    Invoke-Element (Wait-For 'availability explanation' { By-Id 'UnavailableActionsButton' })
    $null = Wait-For 'action explanation dialog' { By-Name 'Unavailable actions' }
    Invoke-Element (Wait-For 'close explanation' { By-Id 'CloseButton' })
    $footer = By-Id 'TaskQueueToggle'
    $contentLeft = (By-Id 'Crumbs').Current.BoundingRectangle.Left
    if ([Math]::Abs($footer.Current.BoundingRectangle.Left - $contentLeft) -gt 2) { throw 'Task footer is not aligned with the page content.' }
    $strip = By-Id 'StatusText'
    if ($strip -and !$strip.Current.IsOffscreen) { throw 'Duplicate page progress strip is still visible.' }
    Save-Window 'blocked'
    Invoke-Element $footer
    Select-TaskFilter 1
    $null = Wait-For 'active filter retains running work' { (By-Id 'TaskFilterSummary').Current.Name -eq '1 of 1 tasks' }
    if ((By-Id 'ClearFinishedTasks').Current.IsEnabled) { throw 'Clear finished is enabled with no finished results.' }
    Start-UiScenario 'Navigation and task cancellation'
    Select-Element (Wait-For 'settings navigation item' { By-Id 'SettingsItem' })
    $null = Wait-For 'progress survives navigation' { (By-Id 'TaskQueueSummary').Current.Name -like '*1 active*' }
    $null = Wait-For 'repository settings disabled' { $b = By-Id 'MinLength'; $b -and !$b.Current.IsEnabled }
    if (!(By-Id 'Verbose').Current.IsEnabled) { throw 'Unrelated settings were disabled.' }
    Invoke-Element (Wait-For 'global task cancellation' { By-Name 'Cancel task' })
    Select-TaskFilter 0
    $null = Wait-For 'cancellation result retained' { Task-Buttons | Where-Object { $_.Current.Name -like 'Cancelled*' } }
    $cancelled = Task-Buttons | Where-Object { $_.Current.Name -like 'Cancelled*' } | Select-Object -First 1
    Invoke-Element $cancelled
    $null = Wait-For 'queued cancellation explains that no work started' {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'No work started. Cancelled while waiting for repository access.*' } | Select-Object -First 1
    }
    if (!$SkipClipboard) {
        $copyId = 'CopyTask_' + $cancelled.Current.AutomationId.Substring(5)
        Invoke-Element (By-Id $copyId)
        $null = Wait-For 'copy feedback' { By-Name 'Task details copied.' }
        $report = Get-Clipboard -Raw
        if (!$report.Contains('Status: Cancelled') -or !$report.Contains($rootPath) -or
            !$report.Contains($name) -or !$report.Contains('No work started.') -or !$report.Contains('Started: ')) {
            throw 'Copied task report is missing its status, context, or result.'
        }
    } else { Write-Host 'SKIP: system clipboard check (shared with the working desktop).' }
    Invoke-Element $cancelled
    $null = Wait-For 'repository settings enabled again' { (By-Id 'MinLength').Current.IsEnabled }
    if (Test-Path -LiteralPath (Join-Path $rootPath $name)) { throw 'Cancelled waiting task created a folder.' }
    $heldLock.Dispose(); $heldLock = $null

    # A normal completion must replace its ghost, keep its receipt, and leave navigation alone.
    Start-UiScenario 'Retry and successful worktree creation'
    Invoke-Element (By-Id 'NavigationViewBackButton')
    $null = Wait-For 'back on overview' { By-Id 'SyncButton' }
    # WinUI's navigation entrance animation temporarily rejects InvokePattern.
    Start-Sleep -Milliseconds 400
    Start-Worktree $name -Retry
    $null = Wait-For 'successful result retained' { Task-Buttons | Where-Object { $_.Current.Name -like '*Completed*' -and $_.Current.Name -like "*$name*" } }
    if (!(Test-Path -LiteralPath (Join-Path $rootPath $name))) { throw 'Successful task did not create its worktree.' }
    $null = Wait-For 'placeholder replaced' { !(By-Name ($name + ' · Preparing worktree')) }
    $null = Wait-For 'sync enabled after completion' { (By-Id 'SyncButton').Current.IsEnabled }
    if ((By-Id 'SyncButton').Current.HelpText -like '*Unavailable while*') { throw 'Completed task left a stale disabled reason.' }
    $completed = Task-Buttons | Where-Object { $_.Current.Name -like '*Completed*' -and $_.Current.Name -like "*$name*" } | Select-Object -First 1
    Invoke-Element $completed
    $resultId = 'TaskResult_' + $completed.Current.AutomationId.Substring(5)
    $resultAction = Wait-For 'completed worktree action' { By-Id $resultId }
    if ($resultAction.Current.Name -ne 'Open folder' -or !$resultAction.Current.IsEnabled) { throw 'Completed worktree has no enabled folder action.' }
    if ($resultAction.Current.HelpText -ne (Join-Path $rootPath $name)) { throw 'Folder action points to the wrong worktree.' }
    Select-TaskFilter 1
    $null = Wait-For 'active filter becomes empty after completion' { (By-Id 'TaskFilterSummary').Current.Name -eq '0 of 2 tasks' }
    if (Task-Buttons | Where-Object { !$_.Current.IsOffscreen }) { throw 'Active filter shows finished results.' }
    Select-TaskFilter 2
    $null = Wait-For 'attention filter empty state' { By-Name 'No tasks need attention.' }
    Select-TaskFilter 3
    $null = Wait-For 'finished filter retains both receipts' { (By-Id 'TaskFilterSummary').Current.Name -eq '2 of 2 tasks' }
    Select-TaskFilter 0
    $null = Wait-For 'expanded result survives filtering' { $b = By-Id $resultId; $b -and !$b.Current.IsOffscreen }
    Start-UiScenario 'Existing destination recovery'
    Invoke-Element (Wait-For 'new worktree action' { By-Id 'NewWorktreeButton' })
    $branchInput = Wait-For 'branch name for duplicate check' { By-Name 'Branch name' }
    $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
    $null = Wait-For 'existing branch blocked before creation' {
        $button = By-Id 'PrimaryButton'
        $button -and !$button.Current.IsEnabled -and $branchInput.Current.HelpText -like '*already a branch*'
    }
    $existing = Wait-For 'new worktree existing destination action' { By-Id 'ExistingDestinationAction' }
    if ($existing.Current.Name -ne 'Open folder' -or $existing.Current.HelpText -ne (Join-Path $rootPath $name)) { throw 'New worktree points to the wrong existing destination.' }
    $destinationRecoveryId = [Guid]::NewGuid().ToString('N')
    $recoveryFolder = Join-Path $rootPath '.sg/operations'
    $null = New-Item -ItemType Directory -Path $recoveryFolder -Force
    $recoveryFile = Join-Path $recoveryFolder ($destinationRecoveryId + '.json')
    @{ id = $destinationRecoveryId; kind = 'Destination recovery'; branch = $name;
       checkout = $config.checkouts[0].name; path = (Join-Path $rootPath $name);
       phase = 'needsReview'; detail = 'Saved edits for destination navigation.' } |
        ConvertTo-Json | Set-Content -LiteralPath $recoveryFile -Encoding utf8
    $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name + '-other')
    $branchInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
    Invoke-Element (Wait-For 'destination recovery action' {
        $action = By-Id 'ExistingDestinationAction'; if ($action -and $action.Current.Name -eq 'Review update') { $action }
    })
    $null = Wait-For 'dialog closes and recovery opens' { By-Name 'Saved edits for destination navigation.' }
    Remove-Item -LiteralPath $recoveryFile
    $recoveryFile = $null
    Invoke-Element (By-Id 'NavigationViewBackButton')
    $null = Wait-For 'back on overview after destination recovery' { By-Id 'SyncButton' }
    Save-Window 'completed'
    Start-UiScenario 'Clear task history'
    Invoke-Element (By-Id 'ClearFinishedTasks')
    $null = Wait-For 'clear finished updates list and summary' { (By-Id 'TaskFilterSummary').Current.Name -eq '0 of 0 tasks' }
    if ((By-Id 'ClearFinishedTasks').Current.IsEnabled) { throw 'Clear finished remained enabled for an empty queue.' }
    Start-UiScenario 'Advanced worktree actions'
    $branch = Wait-For 'created branch card' { By-Name $name }
    $parent = $branch
    $scroll = $null
    while ($parent) {
        if ($parent.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) { break }
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
    }
    if ($scroll) { $scroll.SetScrollPercent(-1, 100) }
    $expandable = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
    $branchControl = $branch.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $expandable)
    if (!$branchControl) { throw 'Worktree card has no accessible expansion control.' }
    $expand = $branchControl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    if ($scroll) { $scroll.SetScrollPercent(-1, 100) }
    $advanced = Wait-For 'advanced worktree menu' { By-Id 'AdvancedWorktreeActions' }
    $advancedControl = $advanced.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $expandable)
    $advancedPattern = $advancedControl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($advancedPattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) { throw 'Advanced options must start collapsed.' }
    $advancedPattern.Expand()
    if ($scroll) { $scroll.SetScrollPercent(-1, 100) }
    $review = Wait-For 'visible review action in expanded menu' {
        $action = By-Id 'ReviewReadinessAction'
        if ($action) {
            $bounds = $action.Current.BoundingRectangle
            $viewport = $parent.Current.BoundingRectangle
            if ($bounds.Height -gt 0 -and $bounds.Top -ge $viewport.Top -and $bounds.Bottom -le $viewport.Bottom) { return $action }
        }
        if ($scroll) { $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::SmallDecrement) }
    }
    if ($ReportDirectory) { try { Save-UiWindow $script:window (Join-Path $ReportDirectory 'advanced-menu.png') } catch { Write-Host "Optional preview capture: $_" } }
    Invoke-Element $review
    $null = Wait-For 'review page opens from advanced menu' { By-Name 'Run local checks' }
    Invoke-Element (By-Id 'NavigationViewBackButton')
    $null = Wait-For 'overview returns after advanced navigation' { By-Id 'SyncButton' }
    if ($ReportDirectory) { try { Save-UiWindow $script:window (Join-Path $ReportDirectory 'worktree-menu.png') } catch { Write-Host "Optional preview capture: $_" } }
    Complete-UiScenario
    Write-Output 'PASS: placeholder, collision gating, navigation, independent controls, cancellation, retained results, completion.'
}
catch {
    Fail-UiScenario $_ $script:window
    Write-Host $_.ScriptStackTrace
    throw
}
finally {
    if ($heldLock) { $heldLock.Dispose() }
    if ($recoveryFile -and (Test-Path -LiteralPath $recoveryFile)) { Remove-Item -LiteralPath $recoveryFile }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id }
    # Restore only navigation preferences changed by this test; preserve unrelated settings.
    if (Test-Path -LiteralPath $settingsPath) {
        $current = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        if ($current.LastRoot -eq $rootPath) {
            $current.LastRoot = $previousSettings.LastRoot
            $current.RecentRoots = @(if ($previousSettings) { $previousSettings.RecentRoots })
        }
        if ($current.BackupMinutes -eq 0) {
            $current.BackupMinutes = if ($previousSettings -and $null -ne $previousSettings.BackupMinutes) { $previousSettings.BackupMinutes } else { 15 }
        }
        $current | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    }
}
