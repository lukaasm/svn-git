# Programmatic Windows UI Automation smoke test; never moves the pointer or sends keystrokes.
# Run against a disposable sg root with at least one checkout. The successful worktree is retained.
param(
    [Parameter(Mandatory)][string]$FixtureRoot,
    [string]$ScreenshotDirectory,
    [switch]$CheckRecovery,
    [string]$AppExe = "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$appPath = (Resolve-Path -LiteralPath $AppExe).Path
$rootPath = (Resolve-Path -LiteralPath $FixtureRoot).Path
if ($appPath -notmatch '\\Debug\\') { throw 'Use an isolated Debug build, not the installed app.' }
if (Get-Process sg-ui -ErrorAction SilentlyContinue | Where-Object Path -eq $appPath) { throw 'Close the existing debug instance before running this test.' }
$config = Get-Content -Raw -LiteralPath (Join-Path $rootPath '.sg/sg.json') | ConvertFrom-Json
if (!$config.checkouts.Count) { throw 'The disposable root needs a checkout.' }
$settingsPath = Join-Path $env:LOCALAPPDATA 'sg/app.json'
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
function Start-Worktree([string]$name) {
    Invoke-Element (Wait-For 'enabled new worktree action' { $b = By-Name 'New worktree'; if ($b -and $b.Current.IsEnabled -and !$b.Current.IsOffscreen) { $b } })
    $input = Wait-For 'branch name' { By-Name 'Branch name' }
    $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
    $create = Wait-For 'enabled create button' { $b = By-Id 'PrimaryButton'; if ($b -and $b.Current.IsEnabled) { $b } }
    Invoke-Element $create
}
function Task-Buttons {
    $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)) |
        Where-Object { $_.Current.AutomationId -like 'Task_*' }
}

# Optional target-window captures for visual review, located through UI Automation.
function Save-Window([string]$name) {
    if (!$ScreenshotDirectory) { return }
    if (!("TaskPaneWindowCapture" -as [type])) {
        Add-Type -AssemblyName System.Drawing
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TaskPaneWindowCapture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@
    }
    $null = New-Item -ItemType Directory -Path $ScreenshotDirectory -Force
    $rect = Wait-For 'window ready for capture' {
        $bounds = $script:window.Current.BoundingRectangle
        if (![double]::IsInfinity($bounds.Width) -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) { $bounds }
    }
    $bitmap = [Drawing.Bitmap]::new([int]$rect.Width, [int]$rect.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (![TaskPaneWindowCapture]::PrintWindow($process.MainWindowHandle, $hdc, 2)) { throw 'Window capture failed.' }
    } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $ScreenshotDirectory ($name + '.png')), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

try {
    # Keep scheduled backups from racing the operation this test deliberately holds at a lock.
    $testSettings = if ($previousSettings) { $previousSettings | ConvertTo-Json -Depth 20 | ConvertFrom-Json } else { [pscustomobject]@{} }
    $testSettings | Add-Member -NotePropertyName BackupMinutes -NotePropertyValue 0 -Force
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
    $script:window = Wait-For 'debug window' {
        $process.Refresh()
        if ($process.HasExited) { throw 'Debug app exited before exposing a window.' }
        if ($process.MainWindowHandle -ne 0) { [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle) }
    }
    $sync = Wait-For 'checkout overview' { By-Id 'SyncButton' }
    if ($CheckRecovery) {
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
    Save-Window 'overview'
    $heldLock = [IO.File]::Open((Join-Path $rootPath '.sg/sg.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $name = 'task-ui-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    Start-Worktree $name
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
    Select-Element (By-Id 'SettingsItem')
    $null = Wait-For 'progress survives navigation' { (By-Id 'TaskQueueSummary').Current.Name -like '*1 active*' }
    $null = Wait-For 'repository settings disabled' { $b = By-Id 'MinLength'; $b -and !$b.Current.IsEnabled }
    if (!(By-Id 'Verbose').Current.IsEnabled) { throw 'Unrelated settings were disabled.' }
    Invoke-Element (Wait-For 'global task cancellation' { By-Name 'Cancel task' })
    $null = Wait-For 'cancellation result retained' { Task-Buttons | Where-Object { $_.Current.Name -like 'Cancelled*' } }
    $null = Wait-For 'repository settings enabled again' { (By-Id 'MinLength').Current.IsEnabled }
    if (Test-Path -LiteralPath (Join-Path $rootPath $name)) { throw 'Cancelled waiting task created a folder.' }
    $heldLock.Dispose(); $heldLock = $null

    # A normal completion must replace its ghost, keep its receipt, and leave navigation alone.
    Invoke-Element (By-Id 'NavigationViewBackButton')
    $null = Wait-For 'back on overview' { By-Id 'SyncButton' }
    # WinUI's navigation entrance animation temporarily rejects InvokePattern.
    Start-Sleep -Milliseconds 400
    Start-Worktree $name
    $null = Wait-For 'successful result retained' { Task-Buttons | Where-Object { $_.Current.Name -like '*Completed*' -and $_.Current.Name -like "*$name*" } }
    if (!(Test-Path -LiteralPath (Join-Path $rootPath $name))) { throw 'Successful task did not create its worktree.' }
    $null = Wait-For 'placeholder replaced' { !(By-Name ($name + ' · Preparing worktree')) }
    $null = Wait-For 'sync enabled after completion' { (By-Id 'SyncButton').Current.IsEnabled }
    if ((By-Id 'SyncButton').Current.HelpText -like '*Unavailable while*') { throw 'Completed task left a stale disabled reason.' }
    Save-Window 'completed'
    Write-Output 'PASS: placeholder, collision gating, navigation, independent controls, cancellation, retained results, completion.'
}
catch {
    Write-Host $_.ScriptStackTrace
    if ($script:window) {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { if ($_.Current.AutomationId -or $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
                Write-Host ($_.Current.AutomationId + " | " + $_.Current.Name + " | enabled=" + $_.Current.IsEnabled + " | offscreen=" + $_.Current.IsOffscreen)
            } }
    }
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
            $current.RecentRoots = if ($previousSettings) { $previousSettings.RecentRoots } else { @() }
        }
        if ($current.BackupMinutes -eq 0) {
            $current.BackupMinutes = if ($previousSettings -and $null -ne $previousSettings.BackupMinutes) { $previousSettings.BackupMinutes } else { 15 }
        }
        $current | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    }
}
