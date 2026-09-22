# Internal entry point for test-ui.ps1, already attached to the private desktop.
param([Parameter(Mandatory)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ui-test-report.ps1"
$request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
$runDirectory = $request.directory
$env:SG_UI_TEST_DIRECTORY = Join-Path $runDirectory 'settings'
$env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $runDirectory 'webview'
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false; RecentRoots = @() } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
$exitCode = 1
$result = @{ status = 'failed'; suite = $request.suite; started = [DateTime]::UtcNow.ToString('o') }
if ($request.suite -ne 'Workflows') { $result.skipped = @('System clipboard assertion: shared with the working desktop.') }
Start-Transcript -LiteralPath (Join-Path $runDirectory 'test.log') | Out-Null
try {
    $fixture = $request.fixtureRoot
    if ($request.suite -ne 'Tasks') {
        & "$PSScriptRoot/test-workflows-ui.ps1" -FixtureParent $runDirectory -ReportDirectory $runDirectory
        $fixtures = @(Get-ChildItem -LiteralPath $runDirectory -Directory -Filter 'sg-workflow-ui-*')
        if ($fixtures.Count -ne 1) { throw 'Expected exactly one workflow fixture.' }
        $fixture = Join-Path $fixtures[0].FullName 'root'
    }
    if ($request.suite -ne 'Workflows') {
        & "$PSScriptRoot/test-task-pane.ps1" -FixtureRoot $fixture -CheckRecovery -SkipClipboard -ReportDirectory $runDirectory
    }
    $result.status = 'passed'
    $result.fixtureRoot = $fixture
    $exitCode = 0
}
catch {
    $result.error = $_.Exception.Message
    Write-Host $_
    Write-Host $_.ScriptStackTrace
}
finally {
    $result.finished = [DateTime]::UtcNow.ToString('o')
    $result.scenarios = @(Read-UiScenarios $runDirectory -Interrupted)
    Write-UiResult $runDirectory $result
    Stop-Transcript | Out-Null
}
exit $exitCode
