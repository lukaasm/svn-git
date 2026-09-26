# Shared by the CI runner and its fast, repository-free checks.
function Get-CiTestPlan {
    param([string[]]$DiscoveryLines, [ValidateRange(1, 16)][int]$ShardCount = 4, [hashtable]$Seconds)
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
    $classes = [Collections.Generic.SortedDictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
    foreach ($method in $methods) {
        $class = $method.Substring(0, $method.LastIndexOf('.'))
        if (!$classes.ContainsKey($class)) { $classes[$class] = [Collections.Generic.List[string]]::new() }
        $classes[$class].Add($method)
    }
    $shards = @(for ($shard = 0; $shard -lt $ShardCount; $shard++) {
        [pscustomobject]@{ number = $shard + 1; methods = [Collections.Generic.List[string]]::new(); caseCount = 0; seconds = 0.0 }
    })
    if ($Seconds -and $Seconds.Count -gt 0) {
        # By time, when a CI run has said how long each method takes: the longest first, each onto the
        # shard with the least so far. Counting cases put the backup tests, the slowest by far, on one
        # shard, and the run waited for it. A method CI has not timed yet weighs what the middle one does.
        $known = @($Seconds.Values | Sort-Object)
        $middle = [double]$known[[int][Math]::Floor($known.Count / 2)]
        $weight = @{}
        foreach ($method in $methods) { $weight[$method] = $Seconds.ContainsKey($method) ? [double]$Seconds[$method] : $middle }
        [string[]]$ordered = $methods.Clone()
        [Array]::Sort($ordered, [Comparison[string]]{
            param($left, $right)
            $heavier = $weight[$right].CompareTo($weight[$left])
            if ($heavier -ne 0) { return $heavier }
            return [StringComparer]::Ordinal.Compare($left, $right)
        })
        foreach ($method in $ordered) {
            $target = 0
            for ($i = 1; $i -lt $ShardCount; $i++) {
                if ($shards[$i].seconds -lt $shards[$target].seconds -or
                    ($shards[$i].seconds -eq $shards[$target].seconds -and $shards[$i].caseCount -lt $shards[$target].caseCount)) { $target = $i }
            }
            $shards[$target].methods.Add($method)
            $shards[$target].caseCount += $cases[$method]
            $shards[$target].seconds += $weight[$method]
        }
        return [pscustomobject]@{ methodCount = $methods.Count; caseCount = [int](($cases.Values | Measure-Object -Sum).Sum); shards = $shards }
    }
    # xUnit runs methods of one class sequentially. Balance each class separately, assigning its
    # largest theories first; a theory with many cases must not count as one ordinary test.
    foreach ($group in $classes.Values) {
        [string[]]$ordered = $group.ToArray()
        [Array]::Sort($ordered, [Comparison[string]]{
            param($left, $right)
            $weight = $cases[$right].CompareTo($cases[$left])
            if ($weight -ne 0) { return $weight }
            return [StringComparer]::Ordinal.Compare($left, $right)
        })
        $classLoads = [int[]]::new($ShardCount)
        foreach ($method in $ordered) {
            $target = 0
            for ($i = 1; $i -lt $ShardCount; $i++) {
                if ($classLoads[$i] -lt $classLoads[$target] -or
                    ($classLoads[$i] -eq $classLoads[$target] -and $shards[$i].caseCount -lt $shards[$target].caseCount)) { $target = $i }
            }
            $shards[$target].methods.Add($method)
            $shards[$target].caseCount += $cases[$method]
            $classLoads[$target] += $cases[$method]
        }
    }
    [pscustomobject]@{ methodCount = $methods.Count; caseCount = [int](($cases.Values | Measure-Object -Sum).Sum); shards = $shards }
}

function Get-CiTestSeconds {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path)) { return $null }
    $seconds = @{}
    foreach ($p in (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json).seconds.PSObject.Properties) { $seconds[$p.Name] = [double]$p.Value }
    $seconds
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
