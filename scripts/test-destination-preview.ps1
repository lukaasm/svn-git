# Import/restore layout and cancellable Git reads, through UI Automation on a private desktop.
param([switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/destination-preview-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-destination-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(300)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Destination preview UI timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Destination preview UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Destination preview UI. Artifacts: $ArtifactDirectory"
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
function Find([string]$id, [switch]$Name, $within = $window) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $within.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($property, $id))
}
function Wait-For([scriptblock]$read, [string]$message = 'Destination preview assertion timed out.') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($process) { $process.Refresh(); if ($process.HasExited) { throw "Test app exited: $($process.ExitCode)" } }
        $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Select-Destination([string]$name) {
    (Find 'IntoBox').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-For { Find $name -Name -within (Find 'IntoBox') }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    # Closing the popup briefly replaces the accessibility provider for the page.
    $null = Wait-For { Find 'NameBox' }
}
function Set-CommandGate([string]$mode = 'hold') {
    $id = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $env:SG_UI_COMMAND_GATE 'request.txt'), ($mode + ':' + $id))
    Join-Path $env:SG_UI_COMMAND_GATE $id
}
function Release-CommandGate([string]$marker) {
    Remove-Item -LiteralPath (Join-Path $env:SG_UI_COMMAND_GATE 'request.txt') -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText(($marker + '.release'), '')
}
function Assert-Cancelled([string]$marker) {
    $childId = [int](Get-Content -LiteralPath ($marker + '.entered'))
    $null = Wait-For { !(Get-Process -Id $childId -ErrorAction SilentlyContinue) } 'The obsolete comparison left its Git process running.'
}
function Assert-Layout([bool]$narrow, [string]$action, [bool]$advanced = $false) {
    # ActionHint swaps its disabled wrapper after validation. Let the following layout settle.
    Start-Sleep -Milliseconds 250
    $a = (Find 'IntoBox').Current.BoundingRectangle
    $b = (Find 'NameBox').Current.BoundingRectangle
    if ($narrow -and $b.Top -lt $a.Bottom) { throw 'Narrow destination fields did not stack in checkout-first order.' }
    # ComboBox includes focus padding in its UIA bounds; compare centers, not outer top edges.
    if (!$narrow -and ([Math]::Abs(($a.Top + $a.Height / 2) - ($b.Top + $b.Height / 2)) -gt 2 -or $a.Right -ge $b.Left)) {
        throw "Wide destination fields are not adjacent: checkout $a, branch $b."
    }
    $bounds = $window.Current.BoundingRectangle
    $ids = @('IntoBox', 'NameBox', 'Summary', $action)
    if ($action -eq 'RestoreButton') { $ids += @('BackupNowButton', 'RestoreAdvanced') }
    if ($advanced) { $ids += @('WipBox', 'ForceBox', 'PruneButton') }
    foreach ($id in $ids) {
        $control = Find $id; $r = $control.Current.BoundingRectangle
        if (!$control -or $control.Current.IsOffscreen -or $r.Width -lt 20 -or $r.Height -lt 16 -or $r.Left -lt $bounds.Left -or $r.Right -gt $bounds.Right -or $r.Bottom -gt $bounds.Bottom) {
            throw "Clipped destination control: $id ($r), window $bounds"
        }
    }
    $primary = (Find $action).Current.BoundingRectangle
    $secondaryId = if ($action -eq 'RestoreButton') { 'BackupNowButton' } else { 'PickButton' }
    $secondary = (Find $secondaryId).Current.BoundingRectangle
    if ($primary.Top -gt $secondary.Top + 2 -or ([Math]::Abs($primary.Top - $secondary.Top) -le 2 -and $primary.Left -gt $secondary.Left)) {
        throw 'The primary import/restore action is not first.'
    }
    if ($narrow -and $action -eq 'RestoreButton' -and (Find 'CheckoutNav_Fort').Current.BoundingRectangle.Width -gt $bounds.Width * 0.15) {
        throw 'The sidebar did not collapse to an icon rail at narrow width.'
    }
}
function Stop-App {
    if ($script:process -and !$script:process.HasExited) { Stop-Process -Id $script:process.Id; $script:process.WaitForExit() }
    $script:process = $null; $script:window = $null
}
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
$process = $null; $window = $null
Initialize-UiReport $ArtifactDirectory 'Destination previews'
try {
    $setup = Join-Path $ArtifactDirectory 'setup.txt'
    $repository = Join-Path $ArtifactDirectory 'svnrepo'
    & svnadmin create $repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create SVN fixture.' }
    $url = ([Uri]($repository + '/')).AbsoluteUri.TrimEnd('/')
    & svnmucc -m 'Create destination fixture' mkdir "$url/trunk" 2>&1 | Out-File $setup
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed SVN fixture.' }
    $root = Join-Path $ArtifactDirectory 'root'
    & $cli init $root --no-fsmonitor 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture.' }
    & $cli checkout add --url "$url/trunk" --root $root --name Fort 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not add Fort.' }
    $seed = Join-Path $ArtifactDirectory 'seed.txt'
    [IO.File]::WriteAllText($seed, 'New SVN revision')
    & svnmucc -m 'Move destination forward' put $seed "$url/trunk/new.txt" 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not advance SVN fixture.' }
    & $cli checkout add --url "$url/trunk" --root $root --name Dashboard 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not add Dashboard.' }
    & $cli branch feature --from Fort --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create source worktree.' }
    $worktree = Join-Path $root 'feature'
    [IO.File]::WriteAllText((Join-Path $worktree 'feature.txt'), 'Saved local commit')
    & git -C $worktree add feature.txt
    & git -C $worktree -c user.name=UiTest -c user.email=ui@example.invalid commit -m 'Saved local commit' 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit source.' }
    $export = Join-Path $root 'feature.sgexport'
    & $cli export $worktree --out $export --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not export source.' }
    $backup = Join-Path $ArtifactDirectory 'backup.git'
    & git init --bare --quiet $backup
    & $cli backup set $backup --root $root 2>&1 | Out-File $setup -Append
    & $cli backup --worktree feature --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not back up source.' }

    $env:SG_UI_COMMAND_GATE = (New-Item -ItemType Directory -Path (Join-Path $ArtifactDirectory 'command-gate')).FullName
    [IO.File]::WriteAllText((Join-Path $env:SG_UI_COMMAND_GATE 'executable.txt'), (Get-Command git).Source)
    [IO.File]::WriteAllText((Join-Path $env:SG_UI_COMMAND_GATE 'match.txt'), '--format=%B')
    $source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'UiCommandGate.cs'))
    $project = Join-Path $env:SG_UI_COMMAND_GATE 'UiCommandGate.csproj'
    [IO.File]::WriteAllText($project, "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include=`"$source`" /></ItemGroup></Project>")
    & dotnet build $project --nologo -v quiet -m:1 -nr:false | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the command gate.' }
    $configFile = Join-Path $root '.sg/sg.json'
    $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    $config.gitExe = Join-Path $env:SG_UI_COMMAND_GATE 'bin/Debug/net10.0/UiCommandGate.exe'
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configFile

    foreach ($width in @(600, 1280)) {
        $env:SG_UI_TEST_WINDOW_SIZE = "${width}x900"
        foreach ($view in @('import', 'backup')) {
            $path = if ($view -eq 'import') { $export } else { $root }
            $action = if ($view -eq 'import') { 'ImportButton' } else { 'RestoreButton' }
            Start-UiScenario "$view destination fields and actions fit at width $width"
            $process = Start-Process -FilePath $app -ArgumentList @($view, ('"' + $path + '"')) -WorkingDirectory $root -WindowStyle Hidden -PassThru
            $window = Wait-For { Get-TestAppWindow $process }
            if ($view -eq 'backup') { Invoke-Control (Wait-For { Find 'BackupWorktreeOpen_feature' }) }
            $null = Wait-For { Find 'ComparisonSummary' }
            Select-Destination 'Fort'
            $namePattern = (Find 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $namePattern.SetValue('restored-feature')
            $clock.Stop()
            $baselineInputMs = $clock.ElapsedMilliseconds
            $script:uiReport.scenarios[$script:uiReport.scenarios.Count - 1].inputBaselineMs = $baselineInputMs
            $null = Wait-For { (Find $action).Current.IsEnabled }
            Assert-Layout ($width -eq 600) $action
            Save-UiWindow $window (Join-Path $ArtifactDirectory "$view-$width.png")
            Complete-UiScenario

            if ($view -eq 'backup') {
                Start-UiScenario "Restore defaults are uncluttered and advanced actions fit at width $width"
                foreach ($id in @('ForceBox', 'PruneButton')) {
                    $control = Find $id
                    if ($control -and !$control.Current.IsOffscreen) { throw "$id is exposed before opening Advanced." }
                }
                (Find 'RestoreAdvanced').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                $null = Wait-For { (Find 'ForceBox') -and !(Find 'ForceBox').Current.IsOffscreen }
                Assert-Layout ($width -eq 600) $action $true
                if ((Find 'WipBox').Current.IsEnabled) { throw 'A backup without saved edits offers to restore them.' }
                if ((Find 'WipBox').Current.HelpText -notlike '*no saved uncommitted*') { throw 'Disabled saved-edits option has no explanation.' }
                (Find 'ForceBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
                (Find 'RestoreAdvanced').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
                $null = Wait-For { (Find 'Replacement enabled' -Name) -and !(Find 'Replacement enabled' -Name).Current.IsOffscreen }
                Save-UiWindow $window (Join-Path $ArtifactDirectory "backup-advanced-$width.png")
                Complete-UiScenario
            }

            if ($width -eq 1280) {
                Start-UiScenario "$view remains editable during comparison and cancels obsolete reads"
                $marker = Set-CommandGate
                Select-Destination 'Dashboard'
                $null = Wait-For { Test-Path -LiteralPath ($marker + '.entered') }
                $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Comparing*Dashboard*' }
                if ((Find $action).Current.IsEnabled) { throw 'Action accepts an obsolete comparison.' }
                $namePattern = (Find 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $clock = [Diagnostics.Stopwatch]::StartNew()
                $namePattern.SetValue('responsive-name')
                $clock.Stop()
                $script:uiReport.scenarios[$script:uiReport.scenarios.Count - 1].inputMs = $clock.ElapsedMilliseconds
                # Compare against idle input: a private-desktop provider adds focus-wait latency.
                if ($clock.ElapsedMilliseconds -gt $baselineInputMs + 1000) { throw "Branch input took $($clock.ElapsedMilliseconds) ms while Git was held (idle $baselineInputMs ms)." }
                if ($namePattern.Current.Value -ne 'responsive-name') { throw 'Branch input did not change while Git was held.' }
                $childId = [int](Get-Content -LiteralPath ($marker + '.entered'))
                if (!(Get-Process -Id $childId -ErrorAction SilentlyContinue)) { throw 'Git was no longer held during the input check.' }
                $null = Wait-For { (Find $action).Current.HelpText -like '*Comparing*' }
                # Disarm the gate before changing destination; the old child must be cancelled, not released.
                Remove-Item -LiteralPath (Join-Path $env:SG_UI_COMMAND_GATE 'request.txt')
                Select-Destination 'Fort'
                Assert-Cancelled $marker
                $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Fort*Revisions match' }
                $null = Wait-For { (Find $action).Current.IsEnabled }
                Complete-UiScenario

                Start-UiScenario "$view comparison failures block submission and retry shows the correct revisions"
                $marker = Set-CommandGate 'fail'
                Select-Destination 'Dashboard'
                $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Retry the revision comparison*' }
                if ((Find $action).Current.IsEnabled) { throw 'Failed comparison permits submission.' }
                if (!(Find 'RetryComparison')) { throw 'Comparison failure has no recovery action.' }
                Release-CommandGate $marker
                Invoke-Control (Find 'RetryComparison')
                $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Dashboard*1 differs' }
                $null = Wait-For { (Find $action).Current.IsEnabled }
                if (!(Find 'Here r2' -Name)) { throw 'Comparison did not show the chosen destination revision.' }
                Save-UiWindow $window (Join-Path $ArtifactDirectory "$view-retry.png")
                Complete-UiScenario

                if ($view -eq 'backup') {
                    Start-UiScenario 'Leaving restore cancels the comparison and keeps navigation responsive'
                    $marker = Set-CommandGate
                    Select-Destination 'Fort'
                    $null = Wait-For { Test-Path -LiteralPath ($marker + '.entered') }
                    Remove-Item -LiteralPath (Join-Path $env:SG_UI_COMMAND_GATE 'request.txt')
                    Invoke-Control (Find 'NavigationViewBackButton')
                    Assert-Cancelled $marker
                    $null = Wait-For { Find 'BackupSearch' }
                    if (Find 'ComparisonSummary') { throw 'Old comparison returned after leaving restore.' }
                    Complete-UiScenario

                    Start-UiScenario 'Restore keeps destination and advanced choices after leaving during a read'
                    Invoke-Control (Find 'ForwardButton')
                    $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Fort*Revisions match' }
                    if ((Find 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'responsive-name') { throw 'Restore draft was lost.' }
                    if (!(Find 'Replacement enabled' -Name)) { throw 'Replacement choice was silently reset.' }
                    (Find 'RestoreAdvanced').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                    if ((Find 'ForceBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { throw 'Replacement choice was lost.' }
                    (Find 'ForceBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
                    Enter-Value (Find 'NameBox') ''
                    (Find 'ActivityItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                    $null = Wait-For { !(Find 'NameBox') } 'Activity did not open from the restored form.'
                    Invoke-Control (Find 'NavigationViewBackButton')
                    $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Fort*Revisions match' }
                    if ((Find 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '') { throw 'An intentionally empty draft was replaced by the source name.' }
                    if ((Find 'RestoreButton').Current.IsEnabled) { throw 'An empty branch name permits restore.' }
                    if ((Find 'RestoreAdvanced').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne 'Expanded') { throw 'Advanced expansion was lost.' }
                    Complete-UiScenario

                    Start-UiScenario 'Returning to a restore draft checks the destination again'
                    Enter-Value (Find 'NameBox') 'responsive-name'
                    Select-Destination 'Dashboard'
                    $null = Wait-For { (Find 'RestoreButton').Current.IsEnabled }
                    (Find 'ActivityItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                    $null = Wait-For { !(Find 'NameBox') } 'Activity could not be selected again after navigating back.'
                    & git -C $worktree branch responsive-name
                    if ($LASTEXITCODE -ne 0) { throw 'Could not create a competing destination.' }
                    Invoke-Control (Find 'NavigationViewBackButton')
                    $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Dashboard*1 differs' }
                    $null = Wait-For { (Find 'Summary').Current.Name -like '*already a branch*Advanced*' }
                    if ((Find 'RestoreButton').Current.IsEnabled) { throw 'Restore reused stale destination validation.' }
                    Save-UiWindow $window (Join-Path $ArtifactDirectory 'restore-draft.png')
                    Complete-UiScenario
                }
            }
            Stop-App
        }
    }
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally { Stop-App }
