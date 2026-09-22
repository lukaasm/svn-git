# Real local SVN/Git fixtures; GUI actions use Windows UI Automation patterns only.
param(
    [string]$FixtureParent = $env:TEMP,
    [string]$AppExe = "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe",
    [string]$CliDll = "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.dll"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$appPath = (Resolve-Path -LiteralPath $AppExe).Path
$cliPath = (Resolve-Path -LiteralPath $CliDll).Path
if ($appPath -notmatch '\\Debug\\') { throw 'Use an isolated Debug app.' }
if (Get-Process sg-ui -ErrorAction SilentlyContinue | Where-Object Path -eq $appPath) { throw 'Close the existing Debug app first.' }
$fixture = Join-Path (Resolve-Path -LiteralPath $FixtureParent).Path ('sg-workflow-ui-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$root = Join-Path $fixture 'root'
$checkout = Join-Path $root 'checkout'
$repo = Join-Path $fixture 'svnrepo'
$backup = Join-Path $fixture 'backup.git'
$settingsPath = Join-Path $env:LOCALAPPDATA 'sg/app.json'
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
function Find-Ui([string]$value, [switch]$Name, [switch]$Invokable) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($property, $value)) |
        Where-Object { ($Invokable -or !$_.Current.IsOffscreen) -and (!$Invokable -or $_.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty)) } | Select-Object -First 1
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
function Assert-Blocked([string]$buttonId, [string]$explanation) {
    $null = Wait-For "validation: $explanation" {
        $button = Find-Ui $buttonId
        $summary = Find-Ui 'Summary'
        $button -and !$button.Current.IsEnabled -and $summary -and $summary.Current.Name -like $explanation -and
            $button.Current.HelpText -like $explanation
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
    $script:window = Wait-For 'app window' {
        $script:process.Refresh()
        if ($script:process.HasExited) { throw 'App exited during startup.' }
        if ($script:process.MainWindowHandle -ne 0) { [System.Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle) }
    }
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
try {
    # Keep scheduled backups from racing the operation this test deliberately holds at a lock.
    $testSettings = if ($previous) { $previous | ConvertTo-Json -Depth 20 | ConvertFrom-Json } else { [pscustomobject]@{} }
    $testSettings | Add-Member -NotePropertyName BackupMinutes -NotePropertyValue 0 -Force
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

    # Import through the actual preview and confirmation UI, then verify its materialized files.
    Start-App 'import' $export
    Set-Ui 'NameBox' 'source'
    Assert-Blocked 'ImportButton' '*already a branch*'
    Set-Ui 'NameBox' ''
    Assert-Blocked 'ImportButton' '*Give the branch a name*'
    Set-Ui 'NameBox' 'bad..name'
    Assert-Blocked 'ImportButton' '*valid Git branch name*'
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'occupied')
    Set-Ui 'NameBox' 'occupied'
    Assert-Blocked 'ImportButton' '*destination folder already exists*'
    Set-Ui 'NameBox' 'invalid name'
    Set-Ui 'NameBox' 'imported'
    Invoke-Ui 'ImportButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt
    $imported = Join-Path $root 'imported'
    if ([IO.File]::ReadAllText((Join-Path $imported 'feature.txt')) -ne "imported feature`n") { throw 'Import lost exported content.' }
    if ((Run 'git' @('-C', $imported, 'status', '--porcelain')).Trim()) { throw 'Imported worktree is dirty.' }
    Write-Host 'PASS: sgexport import preview, confirmation, task receipt, and file content.'
    Stop-App

    # Sync real changes from a second SVN working copy, then update a branch through the GUI.
    $other = Join-Path $fixture 'other'
    $null = Run 'svn' @('checkout', '--non-interactive', ($url + '/trunk'), $other)
    [IO.File]::WriteAllText((Join-Path $other 'base.txt'), "fresh SVN content`n")
    $null = Run 'svn' @('commit', '--non-interactive', $other, '-m', 'Fresh upstream content')
    Start-App 'rebase' $imported
    Invoke-Ui 'Update branch' -Name
    Wait-Receipt
    $null = Run 'git' @('-C', $imported, 'merge-base', '--is-ancestor', 'svn/checkout', 'HEAD')
    if ([IO.File]::ReadAllText((Join-Path $imported 'base.txt')) -ne "fresh SVN content`n") { throw 'Update missed fresh SVN content.' }
    if ([IO.File]::ReadAllText((Join-Path $imported 'feature.txt')) -ne "imported feature`n") { throw 'Update lost branch content.' }
    Write-Host 'PASS: update from fresh SVN retains local commits and advances the base.'
    Stop-App

    # Make the first replayed commit empty using real Git, followed by a commit that must survive Skip.
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
    Start-App 'backup' $root
    Invoke-Ui 'Back up now' -Name
    Wait-Receipt
    # Backups rewrite history, so verify the tree content rather than comparing commit IDs.
    $remoteFeature = Run 'git' @('--git-dir', $backup, 'show', 'refs/heads/imported:feature.txt')
    if ($remoteFeature.Trim() -ne 'imported feature') { throw 'Backup lost branch content.' }
    Write-Host 'PASS: backup task publishes branch content to the local backup repository.'

    $branches = Wait-For 'backed-up branches' { Find-Ui 'Branches' }
    $item = Wait-For 'imported backup entry' {
        $text = $branches.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'imported'))
        if ($text) {
            $node = $text
            while ($node -and $node.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
                $node = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($node)
            }
            $node
        }
    }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Set-Ui 'NameBox' 'bad..name'
    Assert-Blocked 'RestoreButton' '*valid Git branch name*'
    Set-Ui 'NameBox' 'occupied'
    Assert-Blocked 'RestoreButton' '*destination folder already exists*'
    Set-Ui 'NameBox' 'restored-backup'
    Invoke-Ui 'RestoreButton'
    Invoke-Ui 'PrimaryButton'
    Wait-Receipt 2
    $restored = Join-Path $root 'restored-backup'
    if ([IO.File]::ReadAllText((Join-Path $restored 'feature.txt')) -ne "imported feature`n") { throw 'Backup restore lost branch content.' }
    if ([IO.File]::ReadAllText((Join-Path $restored 'base.txt')) -ne "same change on both sides`n") { throw 'Backup restore missed the fresh snapshot.' }
    $null = Run 'git' @('-C', $restored, 'merge-base', '--is-ancestor', 'svn/checkout', 'HEAD')
    Write-Host 'PASS: backup restore merges the saved branch onto the fresh SVN snapshot.'
    Write-Output "All workflow UI Automation checks passed. Fixture retained at $fixture"
}
catch {
    Write-Host $_.ScriptStackTrace
    if ($script:window) {
        $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { if ($_.Current.AutomationId -or $_.Current.Name) { Write-Host ($_.Current.AutomationId + ' | ' + $_.Current.Name + ' | enabled=' + $_.Current.IsEnabled + ' | offscreen=' + $_.Current.IsOffscreen) } }
    }
    Write-Host "Fixture retained for diagnosis: $fixture"
    throw
}
finally {
    Stop-App
    if (Test-Path -LiteralPath $settingsPath) {
        $current = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        if ($current.LastRoot -eq $root) {
            $current.LastRoot = $previous.LastRoot
            $current.RecentRoots = if ($previous) { $previous.RecentRoots } else { @() }
        }
        if ($current.BackupMinutes -eq 0) {
            $current.BackupMinutes = if ($previous -and $null -ne $previous.BackupMinutes) { $previous.BackupMinutes } else { 15 }
        }
        $current | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    }
}
