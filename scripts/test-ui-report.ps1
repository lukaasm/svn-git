# Verifies diagnostics against a real WinUI window on a private desktop; the assertion failure is intentional.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/report-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-report-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Report probe timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Report probe failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: screenshots, scenario durations, original errors, missing-window diagnostics, interrupted journal. Artifacts: $ArtifactDirectory"
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
try {
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $FixtureRoot + '"')) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    $window = $null
    do {
        $window = Get-TestAppWindow $process
        if ($window) {
            $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SyncButton'))
            if ($control -and !$control.Current.IsOffscreen) { break }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$control) { throw 'Probe did not reach the checkout overview.' }
    Initialize-UiReport $ArtifactDirectory 'Probe'
    Start-UiScenario 'Successful assertion'
    Start-Sleep -Milliseconds 25
    Start-UiScenario 'Intentional assertion failure'
    try { throw 'Expected diagnostic probe failure' } catch { Fail-UiScenario $_ $window }
    Start-UiScenario 'Failure without a window'
    try { throw 'Original missing-window failure' } catch { Fail-UiScenario $_ $null }
    Start-UiScenario 'Interrupted assertion'
    $scenarios = @(Read-UiScenarios $ArtifactDirectory -Interrupted)
    if ($scenarios.Count -ne 4 -or $scenarios[0].status -ne 'passed' -or $scenarios[0].durationMs -lt 20) { throw 'Successful scenario timing was lost.' }
    if ($scenarios[1].status -ne 'failed' -or $scenarios[1].error -ne 'Expected diagnostic probe failure' -or !$scenarios[1].screenshot -or !$scenarios[1].automationTree) { throw 'Failure diagnostics are incomplete.' }
    if ($scenarios[2].error -ne 'Original missing-window failure' -or !$scenarios[2].screenshotError) { throw 'Capture failure replaced the original assertion.' }
    if ($scenarios[3].status -ne 'interrupted' -or $scenarios[3].durationMs -lt 0) { throw 'Interrupted scenario was not recovered.' }
    $png = [Drawing.Bitmap]::new((Join-Path $ArtifactDirectory $scenarios[1].screenshot))
    try {
        if ($png.Width -lt 100 -or $png.Height -lt 100) { throw 'Screenshot has invalid dimensions.' }
        $colors = [Collections.Generic.HashSet[int]]::new()
        for ($x = 0; $x -lt $png.Width; $x += 20) { for ($y = 0; $y -lt $png.Height; $y += 20) { $null = $colors.Add($png.GetPixel($x, $y).ToArgb()) } }
        if ($colors.Count -lt 10) { throw 'Screenshot is blank or contains no useful window content.' }
    } finally { $png.Dispose() }
    Write-UiResult $ArtifactDirectory @{ status = 'failed'; error = 'Expected diagnostic probe failure'; scenarios = $scenarios }
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally { if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id } }
