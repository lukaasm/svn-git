# Reproduces a slow Storage read on a private desktop using only UI Automation.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/storage-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-storage-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Storage test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Storage test failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Storage feedback. Artifacts: $ArtifactDirectory"
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
    throw 'Storage feedback assertion timed out.'
}
$process = $null; $heldLock = $null; $window = $null
Initialize-UiReport $ArtifactDirectory 'Storage'
try {
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $null = Wait-For { Find 'SyncButton' }
    Start-UiScenario 'Storage retains content while repository access is delayed'
    $heldLock = [IO.File]::Open((Join-Path $FixtureRoot '.sg/sg.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    (Find 'StorageItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { Find 'StorageLoading' } 5
    Start-Sleep -Milliseconds 300
    try { Save-UiWindow $window (Join-Path $ArtifactDirectory 'storage-loading.png') } catch { Write-Host "Optional preview capture: $_" }
    $heldLock.Dispose(); $heldLock = $null
    $null = Wait-For { Find 'StorageSummary' } 30
    Start-UiScenario 'Storage read errors retain a fallback and retry'
    $operationFolder = Join-Path $FixtureRoot '.sg/operations'
    $null = New-Item -ItemType Directory -Path $operationFolder -Force
    $brokenRecord = Join-Path $operationFolder ([Guid]::NewGuid().ToString('N') + '.json')
    '{ invalid fixture record' | Set-Content -LiteralPath $brokenRecord -Encoding utf8
    (Find 'SettingsItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Find 'StorageItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { Find 'Could not read storage information. Retry the scan or open the error log for details.' -Name }
    $retry = Wait-For { Find 'Retry' -Name }
    try { Save-UiWindow $window (Join-Path $ArtifactDirectory 'storage-error.png') } catch { Write-Host "Optional preview capture: $_" }
    Remove-Item -LiteralPath $brokenRecord
    $brokenRecord = $null
    $retry.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { Find 'StorageSummary' } 30
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($brokenRecord -and (Test-Path -LiteralPath $brokenRecord)) { Remove-Item -LiteralPath $brokenRecord }
    if ($heldLock) { $heldLock.Dispose() }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
