# Shared by the CI runner and its fast, repository-free checks.
function Get-CiTestPlan {
    param([string[]]$DiscoveryLines, [ValidateRange(1, 16)][int]$ShardCount = 4)
    $cases = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($line in $DiscoveryLines) {
        if ($line -notmatch '^ {4}\S') { continue }
        # Fail closed if the adapter starts emitting custom display names. Every discovered case
        # must belong to an exact method filter; theory arguments never become filter syntax.
        if ($line -notmatch '^ {4}(?<method>(?:[\p{L}_][\p{L}\p{N}_]*\.)+[\p{L}_][\p{L}\p{N}_]*)(?:\(.*\))?$') {
            throw "Cannot shard discovered test: $line"
        }
        $method = $Matches.method
        if (!$cases.ContainsKey($method)) { $cases[$method] = 0 }
        $cases[$method]++
    }
    if ($cases.Count -lt $ShardCount) { throw "Discovered only $($cases.Count) test methods for $ShardCount shards." }
    [string[]]$methods = @($cases.Keys)
    [Array]::Sort($methods, [StringComparer]::Ordinal)
    $shards = @(for ($shard = 0; $shard -lt $ShardCount; $shard++) {
        $selected = @(for ($i = $shard; $i -lt $methods.Count; $i += $ShardCount) { $methods[$i] })
        [pscustomobject]@{
            number = $shard + 1
            methods = $selected
            caseCount = [int](($selected | ForEach-Object { $cases[$_] } | Measure-Object -Sum).Sum)
        }
    })
    [pscustomobject]@{ methodCount = $methods.Count; caseCount = [int](($cases.Values | Measure-Object -Sum).Sum); shards = $shards }
}

function Write-CiTestSettings {
    param($Shard, [string]$Path)
    $filter = ($Shard.methods | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
    if (!$filter) { throw 'Refusing to run an empty test filter.' }
    $settings = [xml]'<RunSettings><RunConfiguration><TestCaseFilter /></RunConfiguration></RunSettings>'
    $settings.RunSettings.RunConfiguration.TestCaseFilter = $filter
    $settings.Save($Path)
}

function Assert-CiTestResults {
    param($Shard, [string]$TrxPath)
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $results = @($trx.TestRun.Results.UnitTestResult)
    if ($results.Count -ne $Shard.caseCount) { throw "Expected $($Shard.caseCount) cases; TRX contains $($results.Count)." }
    $definitions = @{}
    foreach ($test in $trx.TestRun.TestDefinitions.UnitTest) {
        $definitions[$test.id] = $test.TestMethod.className + '.' + $test.TestMethod.name
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expected = [Collections.Generic.HashSet[string]]::new([string[]]$Shard.methods, [StringComparer]::Ordinal)
    foreach ($result in $results) {
        $method = $definitions[$result.testId]
        if (!$method -or !$expected.Contains($method)) { throw "Unexpected test result: $($result.testName)" }
        $null = $seen.Add($method)
        if ($result.outcome -ne 'Passed') { throw "Test did not pass: $($result.testName) ($($result.outcome))" }
    }
    if (!$seen.SetEquals($expected)) { throw 'One or more planned test methods did not execute.' }
}

function Write-CiTestSummary {
    param($Shard, [string]$TrxPath, [double]$Seconds, [string]$Path)
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $timings = @($trx.TestRun.Results.UnitTestResult | ForEach-Object {
        [pscustomobject]@{ test = $_.testName; seconds = [Math]::Round([TimeSpan]::Parse($_.duration).TotalSeconds, 3); outcome = $_.outcome }
    } | Sort-Object seconds -Descending)
    $timings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Path, '.json')) -Encoding utf8
    $summary = @(
        "## Test shard $($Shard.number)"
        ''
        "$($timings.Count) cases / $($Shard.methods.Count) methods in $([Math]::Round($Seconds, 1)) seconds."
        ''
        '| Slowest test cases | Seconds | Result |'
        '| --- | ---: | --- |'
        $timings | Select-Object -First 10 | ForEach-Object { '| ' + $_.test.Replace('|', '\|') + ' | ' + $_.seconds + ' | ' + $_.outcome + ' |' }
    )
    $summary | Set-Content -LiteralPath $Path -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
}
