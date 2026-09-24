# Native UI Automation on a private desktop. Build Debug successfully before running.
param([switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/checkout-add-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-checkout-add-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Checkout UI test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Checkout UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Checkout UI. Artifacts: $ArtifactDirectory"
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
function Wait-For([scriptblock]$read, [string]$message = 'Checkout UI assertion timed out.') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Add-Checkout { Invoke-Control (Wait-For { $b = Find 'Add the checkout' -Name; if ($b -and $b.Current.IsEnabled) { $b } }) }
function Wait-Checkout([string]$name) {
    $null = Wait-For { $c = Find 'CoName'; !(Find 'FolderBox') -and $c -and $c.Current.Name -eq $name } ('Checkout registered but did not open ' + $name)
}
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
$process = $null; $window = $null
Initialize-UiReport $ArtifactDirectory 'Checkout registration'
try {
    # One tiny repository, a fresh sg root and an external working copy reproduce the reported flow.
    $repository = Join-Path $ArtifactDirectory 'svnrepo'
    & svnadmin create $repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create SVN fixture.' }
    $url = ([Uri]($repository + '/')).AbsoluteUri.TrimEnd('/')
    $file = Join-Path $ArtifactDirectory 'base.txt'
    [IO.File]::WriteAllText($file, "Checkout regression fixture`n")
    & svnmucc -m 'Create checkout fixture' mkdir "$url/trunk" put $file "$url/trunk/base.txt" | Out-File (Join-Path $ArtifactDirectory 'setup.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed SVN fixture.' }
    $fixture = Join-Path $ArtifactDirectory 'root'
    & $cli init $fixture --no-fsmonitor | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture root.' }
    $checkout = Join-Path $ArtifactDirectory 'external-checkout'
    & svn checkout "$url/trunk" $checkout | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out fixture.' }
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $fixture + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Start-UiScenario 'Adding an existing checkout opens its overview without a false ownership error'
    Invoke-Control (Wait-For { Find 'AddCheckoutButton' })
    Enter-Value (Wait-For { Find 'FolderBox' }) $checkout
    Enter-Value (Find 'NameBox') 'Dashboard'
    Add-Checkout
    $null = Wait-For { $config = Get-Content -LiteralPath (Join-Path $fixture '.sg/sg.json') -Raw | ConvertFrom-Json; $config.checkouts.name -contains 'Dashboard' } 'Checkout did not register.'
    $null = Wait-For {
        $errorText = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)) |
            Where-Object { $_.Current.Name -like '*Another sg root already holds this checkout*' } | Select-Object -First 1
        if ($errorText) { throw ('Checkout registered, but the form shows: ' + $errorText.Current.Name) }
        !(Find 'FolderBox') -and (Find 'NewWorktreeButton')
    } 'Checkout registered but did not redirect to its overview.'
    $config = Get-Content -LiteralPath (Join-Path $fixture '.sg/sg.json') -Raw | ConvertFrom-Json
    if (@($config.checkouts | Where-Object name -eq 'Dashboard').Count -ne 1) { throw 'Checkout was registered more than once.' }
    Wait-Checkout 'Dashboard'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'checkout-added.png')
    Complete-UiScenario

    Start-UiScenario 'A duplicate in this root has accurate inline guidance and cannot be submitted'
    Invoke-Control (Find 'AddCheckoutButton')
    Enter-Value (Wait-For { Find 'FolderBox' }) $checkout
    $null = Wait-For { Find "Already registered in this root as 'Dashboard'." -Name }
    if ((Find 'Add the checkout' -Name).Current.IsEnabled) { throw 'Duplicate checkout submission was enabled.' }
    Complete-UiScenario

    Start-UiScenario 'Adding from an SVN URL selects the newly created checkout rather than the old one'
    (Find 'Check out from an SVN URL' -Name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Enter-Value (Find 'UrlBox') "$url/trunk"
    Enter-Value (Find 'NameBox') 'FromUrl'
    Enter-Value (Find 'FolderBox') (Join-Path $ArtifactDirectory 'url-checkout')
    Add-Checkout
    Wait-Checkout 'FromUrl'
    if (!(Test-Path -LiteralPath (Join-Path $ArtifactDirectory 'url-checkout/base.txt'))) { throw 'URL checkout was not populated.' }
    Complete-UiScenario

    Start-UiScenario 'Finishing in the background retains the page the user navigated to'
    $background = Join-Path $ArtifactDirectory 'background-checkout'
    & svn checkout "$url/trunk" $background | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare background checkout.' }
    Invoke-Control (Find 'AddCheckoutButton')
    Enter-Value (Wait-For { Find 'FolderBox' }) $background
    Enter-Value (Find 'NameBox') 'Background'
    # Hold only this disposable root's repository lock to make navigation-before-completion deterministic.
    $gate = [IO.File]::Open((Join-Path $fixture '.sg/sg.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Add-Checkout
        $null = Wait-For { !(Find 'FolderBox').Current.IsEnabled }
        (Find 'ActivityItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { !(Find 'FolderBox') }
    } finally { $gate.Dispose() }
    $null = Wait-For { $config = Get-Content -LiteralPath (Join-Path $fixture '.sg/sg.json') -Raw | ConvertFrom-Json; $config.checkouts.name -contains 'Background' }
    $null = Wait-For { (Find 'TaskQueueSummary').Current.Name -like '*0 active*' }
    Start-Sleep -Milliseconds 800 # Allow the coalesced task-completion refresh to publish.
    if (Find 'NewWorktreeButton') { throw 'Background completion stole navigation from Activity.' }
    $selected = (Find 'ActivityItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
    if (!$selected) { throw 'Activity selection was lost after background completion.' }
    Complete-UiScenario

    Start-UiScenario 'A new root validates its first checkout and freezes submitted fields'
    Invoke-Control (Find 'RootMenu')
    Invoke-Control (Wait-For { Find 'New root' -Name })
    $newRoot = Join-Path $ArtifactDirectory 'new-root'
    Enter-Value (Wait-For { Find 'RootBox' }) $newRoot
    (Find 'FsMonitor').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Control (Find 'CreateRootButton')
    $null = Wait-For { $field = Find 'FolderBox'; $field -and $field.Current.IsEnabled }
    $first = Join-Path $ArtifactDirectory 'first-checkout'
    & svn checkout "$url/trunk" $first | Out-File (Join-Path $ArtifactDirectory 'setup.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the new root checkout.' }
    Enter-Value (Find 'FolderBox') $first
    Enter-Value (Find 'NameBox') 'First'
    $gate = [IO.File]::Open((Join-Path $newRoot '.sg/sg.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Add-Checkout
        $null = Wait-For { $folder = Find 'FolderBox'; $name = Find 'NameBox'; $folder -and $name -and !$folder.Current.IsEnabled -and !$name.Current.IsEnabled }
    } finally { $gate.Dispose() }
    $null = Wait-For { (Find 'Add the checkout' -Name).Current.HelpText -eq 'The first checkout has already been added.' }
    $config = Get-Content -LiteralPath (Join-Path $newRoot '.sg/sg.json') -Raw | ConvertFrom-Json
    if (@($config.checkouts).Count -ne 1 -or $config.checkouts[0].name -ne 'First') { throw 'New root did not register exactly the submitted checkout.' }
    if ((Find 'FolderBox').Current.IsEnabled) { throw 'Completed checkout fields became editable again.' }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
