# Run the same deterministic shard locally or on a disposable CI runner.
[CmdletBinding()]
param(
    [ValidateRange(1, 16)][int]$Shard = 1,
    [ValidateRange(1, 16)][int]$ShardCount = 4,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
if ($Shard -gt $ShardCount) { throw 'Shard must be between 1 and ShardCount.' }
. "$PSScriptRoot/ci-test-plan.ps1"
$repo = Split-Path $PSScriptRoot
$project = Join-Path $repo 'tests/Sg.Core.Tests/Sg.Core.Tests.csproj'
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo "TestResults/CI/shard-$Shard-$([Guid]::NewGuid().ToString('N'))" }
$null = New-Item -ItemType Directory -Path $ResultsDirectory -Force
$ResultsDirectory = (Resolve-Path -LiteralPath $ResultsDirectory).Path
if (!$NoBuild) {
    & dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Test build failed ($LASTEXITCODE)." }
}
$discovery = @(& dotnet test $project -c $Configuration --no-build --no-restore --list-tests --nologo 2>&1 | ForEach-Object { "$_" })
$discovery | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'discovery.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw "Test discovery failed ($LASTEXITCODE)." }
$plan = Get-CiTestPlan -DiscoveryLines $discovery -ShardCount $ShardCount
$plan | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'plan.json') -Encoding utf8
$selected = $plan.shards[$Shard - 1]
Write-Host "Shard $Shard/$ShardCount : $($selected.caseCount) of $($plan.caseCount) cases, $($selected.methods.Count) of $($plan.methodCount) methods."
$settings = Join-Path $ResultsDirectory 'tests.runsettings'
Write-CiTestSettings -Shard $selected -Path $settings
$trx = Join-Path $ResultsDirectory 'tests.trx'
# Reusing a results folder must never let a failed/aborted run validate an earlier TRX.
if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx }
$timer = [Diagnostics.Stopwatch]::StartNew()
& dotnet test $project -c $Configuration --no-build --no-restore --nologo --settings $settings --logger 'trx;LogFileName=tests.trx' --results-directory $ResultsDirectory
$testExitCode = $LASTEXITCODE
$timer.Stop()
if (Test-Path -LiteralPath $trx) {
    Write-CiTestSummary -Shard $selected -TrxPath $trx -Seconds $timer.Elapsed.TotalSeconds -Path (Join-Path $ResultsDirectory 'timings.md')
}
if ($testExitCode -ne 0) { throw "Test shard failed ($testExitCode). See $ResultsDirectory." }
Assert-CiTestResults -Shard $selected -TrxPath $trx
Write-Host "PASS: every planned case passed. Results: $ResultsDirectory"
