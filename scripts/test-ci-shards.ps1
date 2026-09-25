# No Git/SVN fixtures or external test framework needed for the shard planner itself.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ci-test-plan.ps1"
function Assert($condition, [string]$message) { if (!$condition) { throw $message } }
function Assert-Throws([scriptblock]$action) {
    $threw = $false
    try { & $action | Out-Null } catch { $threw = $true }
    Assert $threw 'Expected an invalid plan/result to fail.'
}
$lines = @('Localized test discovery header') + @(1..40 | ForEach-Object { '    Example.Tests.Case.Test{0:d2}' -f $_ }) + @(
    '    Example.Tests.Theories.Value(input: "a|b=2")'
    '    Example.Tests.Theories.Value(input: "(x)")'
)
$plan = Get-CiTestPlan $lines 4
$reverse = [string[]]$lines.Clone(); [Array]::Reverse($reverse)
Assert (($plan | ConvertTo-Json -Depth 6 -Compress) -ceq ((Get-CiTestPlan $reverse 4) | ConvertTo-Json -Depth 6 -Compress)) 'Discovery order changed shard assignments.'
$all = @($plan.shards | ForEach-Object { $_.methods })
Assert ($all.Count -eq 41 -and @($all | Select-Object -Unique).Count -eq 41) 'Methods were duplicated or omitted.'
Assert (($plan.shards | Measure-Object caseCount -Sum).Sum -eq 42) 'Theory cases were omitted.'
Assert (@($plan.shards | Where-Object { $_.methods -contains 'Example.Tests.Theories.Value' }).Count -eq 1) 'Theory cases were split between shards.'
Assert-Throws { Get-CiTestPlan @('No tests') 4 }
Assert-Throws { Get-CiTestPlan @('    A custom display name') 1 }
Assert-Throws { Get-CiTestPlan @('    Example.Tests.OnlyTest') 4 }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('sg-ci-plan-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temp
$settingsPath = Join-Path $temp 'tests.runsettings'
$trxPath = Join-Path $temp 'tests.trx'
try {
    $theoryShard = $plan.shards | Where-Object { $_.methods -contains 'Example.Tests.Theories.Value' }
    Write-CiTestSettings $theoryShard $settingsPath
    [xml]$settings = Get-Content -LiteralPath $settingsPath -Raw
    $filter = $settings.RunSettings.RunConfiguration.TestCaseFilter
    Assert ($filter -like '*FullyQualifiedName=Example.Tests.Theories.Value*' -and !$filter.Contains('input:')) 'Theory arguments leaked into the test filter.'
    Assert-Throws { Write-CiTestSettings ([pscustomobject]@{ methods = @() }) $settingsPath }
    $expected = [pscustomobject]@{ methods = @('Example.Tests.Case.A', 'Example.Tests.Case.B'); caseCount = 2 }
    $valid = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="1" testName="A" outcome="Passed"/><UnitTestResult testId="2" testName="B" outcome="Passed"/></Results><TestDefinitions><UnitTest id="1"><TestMethod className="Example.Tests.Case" name="A"/></UnitTest><UnitTest id="2"><TestMethod className="Example.Tests.Case" name="B"/></UnitTest></TestDefinitions></TestRun>'
    $valid | Set-Content -LiteralPath $trxPath
    Assert-CiTestResults $expected $trxPath
    foreach ($invalid in @(
        $valid.Replace('<UnitTestResult testId="2" testName="B" outcome="Passed"/>', '')
        $valid.Replace('name="B"', 'name="Unexpected"')
        $valid.Replace('name="B"', 'name="A"')
        $valid.Replace('outcome="Passed"', 'outcome="NotExecuted"')
        $valid.Replace('outcome="Passed"', 'outcome="Failed"')
    )) {
        $invalid | Set-Content -LiteralPath $trxPath
        Assert-Throws { Assert-CiTestResults $expected $trxPath }
    }
} finally {
    # Only the two files created by this test and their now-empty directory.
    Remove-Item -LiteralPath $settingsPath, $trxPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temp
}
Write-Host 'PASS: shard coverage, stable assignment, theory grouping, exact filters, and incomplete/failed result rejection.'
