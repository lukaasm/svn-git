# Exercises launcher exit propagation and cleanup without opening an app or touching user input.
$ErrorActionPreference = 'Stop'
if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
$runDirectory = Join-Path $PSScriptRoot ('../TestResults/UI/launcher-' + [Guid]::NewGuid().ToString('N'))
$runDirectory = (New-Item -ItemType Directory -Path $runDirectory).FullName
function Encoded([string]$command) { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)) }
function Literal([string]$value) { "'" + $value.Replace("'", "''") + "'" }
$executable = Join-Path $PSHOME 'pwsh.exe'
$desktop = [UiTestDesktop]::new($executable, (Encoded 'exit 7'), $runDirectory, ('sg-exit-' + [Guid]::NewGuid().ToString('N')))
try {
    if (!$desktop.Wait(10000) -or $desktop.ExitCode -ne 7) { throw 'Worker exit code was lost.' }
} finally { $desktop.Dispose() }
Write-Host 'PASS: worker exit code is preserved.'

$childFile = Join-Path $runDirectory 'child.pid'
$command = '$child = Start-Process -WindowStyle Hidden -FilePath ' + (Literal $executable) +
    ' -ArgumentList @(''-NoProfile'', ''-NonInteractive'', ''-EncodedCommand'', ''' + (Encoded 'Start-Sleep -Seconds 120') +
    ''') -PassThru; $child.Id | Set-Content -LiteralPath ' + (Literal $childFile) + '; Start-Sleep -Seconds 120'
$desktop = [UiTestDesktop]::new($executable, (Encoded $command), $runDirectory, ('sg-cleanup-' + [Guid]::NewGuid().ToString('N')))
$child = $null
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path -LiteralPath $childFile)) {
        if ([DateTime]::UtcNow -ge $deadline -or $desktop.Wait(100)) { throw 'Probe child did not start.' }
    }
    $child = Get-Process -Id ([int](Get-Content -LiteralPath $childFile))
    if ($desktop.Wait(100)) { throw 'Waiting worker unexpectedly exited.' }
} finally { $desktop.Dispose() }
if (!$child.WaitForExit(10000)) { throw 'Closing the desktop job left its child process running.' }
Write-Host 'PASS: cancellation cleans up the worker and its child process.'

# A missing fixture configuration fails inside the worker, not in the public runner's preflight.
$failureOutput = & $executable -NoProfile -File "$PSScriptRoot/test-ui.ps1" -Suite Tasks -FixtureRoot $runDirectory -OutputDirectory $runDirectory 2>&1
if ($LASTEXITCODE -eq 0) { throw 'The runner reported a failed suite as successful.' }
$failedRuns = @(Get-ChildItem -LiteralPath $runDirectory -Directory -Filter 'sg-ui-*')
if ($failedRuns.Count -ne 1) { throw 'Expected one failed run artifact directory.' }
$failedRun = $failedRuns[0]
$result = Get-Content -Raw -LiteralPath (Join-Path $failedRun.FullName 'result.json') | ConvertFrom-Json
if ($result.status -ne 'failed' -or !$result.error) { throw 'The failed suite did not retain a diagnostic result.' }
Write-Host 'PASS: suite failure returns a nonzero exit and retains its diagnostic result.'
$timeoutDirectory = Join-Path $runDirectory 'timeout'
$timeoutOutput = & $executable -NoProfile -File "$PSScriptRoot/test-ui.ps1" -Suite Workflows -TimeoutSeconds 1 -OutputDirectory $timeoutDirectory 2>&1
if ($LASTEXITCODE -eq 0) { throw 'The runner reported a timed-out suite as successful.' }
$timeoutRun = @(Get-ChildItem -LiteralPath $timeoutDirectory -Directory -Filter 'sg-ui-*')
if ($timeoutRun.Count -ne 1) { throw 'Expected one timeout artifact directory.' }
$timeoutResult = Get-Content -Raw -LiteralPath (Join-Path $timeoutRun[0].FullName 'result.json') | ConvertFrom-Json
if ($timeoutResult.status -ne 'failed' -or $timeoutResult.error -notlike '*timed out*') { throw 'The timeout outcome was not recorded.' }
Write-Host 'PASS: deadline expiry returns a nonzero exit and retains a timeout result.'
Write-Output "All UI runner lifecycle checks passed. Artifacts: $runDirectory"
