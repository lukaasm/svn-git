# Runs UI Automation on a private Windows desktop; never switches to it or injects input.
param(
    [ValidateSet('All', 'Workflows', 'Tasks')][string]$Suite = 'All',
    [string]$FixtureRoot,
    [string]$OutputDirectory = "$PSScriptRoot/../TestResults/UI",
    [ValidateRange(1, 7200)][int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ui-test-report.ps1"
if (!$IsWindows) { throw 'UI Automation requires Windows and PowerShell 7.' }
if ($Suite -eq 'Tasks' -and !$FixtureRoot) { throw 'Tasks requires -FixtureRoot pointing to a disposable checkout fixture.' }
if ($FixtureRoot) { $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path }
$app = "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe"
if (!(Test-Path -LiteralPath $app)) { throw 'Build the Debug app before running UI tests.' }
if ($Suite -ne 'Tasks' -and !(Test-Path -LiteralPath "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.dll")) { throw 'Build the Debug CLI before running workflow tests.' }
$runId = 'sg-ui-' + [Guid]::NewGuid().ToString('N')
$runDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $runId))
$null = New-Item -ItemType Directory -Path $runDirectory
$request = @{ suite = $Suite; fixtureRoot = $FixtureRoot; directory = $runDirectory }
$requestPath = Join-Path $runDirectory 'request.json'
$request | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8
$worker = Join-Path $PSScriptRoot 'ui-test-worker.ps1'
# Single-quote PowerShell literals, then encode the whole command; paths are never shell syntax.
$command = "& '" + $worker.Replace("'", "''") + "' -RequestPath '" + $requestPath.Replace("'", "''") + "'"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$desktop = $null
$resultPath = Join-Path $runDirectory 'result.json'
Write-Host "UI tests on private desktop. Logs and results: $runDirectory"
try {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), $encoded, (Resolve-Path "$PSScriptRoot/..").Path, $runId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!$desktop.Wait(200)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "UI tests timed out after $TimeoutSeconds seconds." }
    }
    if ($desktop.ExitCode -ne 0) { throw "UI tests failed (exit $($desktop.ExitCode)). See $runDirectory/test.log" }
    if (!(Test-Path -LiteralPath $resultPath)) { throw 'UI test worker exited without a result.' }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.status -ne 'passed') { throw 'UI test worker did not report success.' }
    Write-Output "PASS: $Suite. Results: $resultPath"
}
catch {
    $failure = $_
    # Stop the worker before recording timeout/setup failure, so it cannot overwrite the outcome.
    if ($desktop) { $desktop.Dispose(); $desktop = $null }
    $reported = $null
    if (Test-Path -LiteralPath $resultPath) {
        try { $reported = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } catch { }
    }
    if ($reported.status -ne 'failed') {
        Write-UiResult $runDirectory @{ status = 'failed'; error = $failure.Exception.Message; suite = $Suite; scenarios = @(Read-UiScenarios $runDirectory -Interrupted) }
    }
    throw $failure
}
finally { if ($desktop) { $desktop.Dispose() } }
