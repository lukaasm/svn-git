# Rewrites tests/ci-timings.json from the test results of a CI run, so the next runs split the tests
# by how long they take instead of by how many there are. Run it after tests were added or got slower:
#   ./scripts/update-ci-timings.ps1                 the newest successful run on main
#   ./scripts/update-ci-timings.ps1 -RunId 123      that run
#   ./scripts/update-ci-timings.ps1 -Artifacts dir  results already downloaded (tests-*/tests.trx)
[CmdletBinding()]
param([long]$RunId, [string]$Artifacts)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (!$Artifacts) {
    if (!$RunId) {
        $RunId = gh run list --repo lukaasm/svn-git --branch main --status success --limit 1 --json databaseId --jq '.[0].databaseId'
        if (!$RunId) { throw 'No successful run on main to read timings from.' }
    }
    $Artifacts = Join-Path ([IO.Path]::GetTempPath()) "sg-ci-timings-$RunId"
    if (Test-Path -LiteralPath $Artifacts) { Remove-Item -LiteralPath $Artifacts -Recurse -Force }
    gh run download $RunId --repo lukaasm/svn-git --pattern 'tests-*' --dir $Artifacts
    if ($LASTEXITCODE -ne 0) { throw "Could not download the test results of run $RunId." }
}
$seconds = [Collections.Generic.SortedDictionary[string, double]]::new([StringComparer]::Ordinal)
$files = @(Get-ChildItem -LiteralPath $Artifacts -Recurse -Filter 'tests.trx')
if ($files.Count -eq 0) { throw "No tests.trx under $Artifacts." }
foreach ($file in $files) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
    $methods = @{}
    foreach ($test in $trx.TestRun.TestDefinitions.UnitTest) { $methods[$test.id] = $test.TestMethod.className + '.' + $test.TestMethod.name }
    # A theory's cases add up to its method: a method is the unit the shards are cut from.
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        $method = $methods[$result.testId]
        if (!$method) { continue }
        $seconds[$method] = ($seconds.ContainsKey($method) ? $seconds[$method] : 0) + [TimeSpan]::Parse($result.duration).TotalSeconds
    }
}
$rounded = [ordered]@{}
foreach ($pair in $seconds.GetEnumerator()) { $rounded[$pair.Key] = [Math]::Round($pair.Value, 1) }
$out = Join-Path $repo 'tests/ci-timings.json'
[ordered]@{ source = $(if ($RunId) { "run $RunId" } else { $Artifacts }); seconds = $rounded } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $out -Encoding utf8
Write-Host "$($rounded.Count) methods from $($files.Count) result files written to $out"
