# Shared scenario journal and window-only diagnostics for both UI Automation suites.
function Initialize-UiReport([string]$directory, [string]$suite) {
    if (!$directory) { $directory = Join-Path $PSScriptRoot ('../TestResults/UI/direct-' + [Guid]::NewGuid().ToString('N')) }
    $script:uiReport = @{ directory = [IO.Path]::GetFullPath($directory); suite = $suite; scenarios = [Collections.Generic.List[object]]::new(); clock = $null }
    $null = New-Item -ItemType Directory -Path (Join-Path $script:uiReport.directory 'scenarios') -Force
}

function Write-UiJournal {
    $path = Join-Path $script:uiReport.directory ('scenarios/' + $script:uiReport.suite + '.json')
    $json = ConvertTo-Json -InputObject @($script:uiReport.scenarios.ToArray()) -Depth 8
    [IO.File]::WriteAllText($path + '.tmp', $json)
    [IO.File]::Move($path + '.tmp', $path, $true)
}

function Start-UiScenario([string]$name) {
    Complete-UiScenario
    $script:uiReport.scenarios.Add([ordered]@{ suite = $script:uiReport.suite; name = $name; status = 'running'; started = [DateTime]::UtcNow.ToString('o') })
    $script:uiReport.clock = [Diagnostics.Stopwatch]::StartNew()
    Write-UiJournal
}

function Complete-UiScenario {
    if (!$script:uiReport.clock) { return }
    $scenario = $script:uiReport.scenarios[$script:uiReport.scenarios.Count - 1]
    $scenario.status = 'passed'
    $scenario.durationMs = $script:uiReport.clock.ElapsedMilliseconds
    $scenario.finished = [DateTime]::UtcNow.ToString('o')
    $script:uiReport.clock = $null
    Write-UiJournal
}

function Save-UiWindow($window, [string]$path) {
    if (!$window) { throw 'No test window is available.' }
    if (!('UiReportCapture' -as [type])) {
        Add-Type -AssemblyName System.Drawing
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UiReportCapture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
'@
    }
    $bounds = $window.Current.BoundingRectangle
    if ([double]::IsInfinity($bounds.Width) -or [double]::IsInfinity($bounds.Height) -or $bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'The test window has no drawable bounds.' }
    $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
    $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try {
                if (![UiReportCapture]::PrintWindow([IntPtr]$window.Current.NativeWindowHandle, $dc, 2)) { throw 'Windows could not capture the test window.' }
            } finally { $graphics.ReleaseHdc($dc) }
        } finally { $graphics.Dispose() }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

function Fail-UiScenario($failure, $window) {
    # Diagnostics are best effort: never replace the assertion that failed.
    try {
        if (!$script:uiReport.clock) { Start-UiScenario 'Suite failure' }
        $scenario = $script:uiReport.scenarios[$script:uiReport.scenarios.Count - 1]
        $scenario.status = 'failed'
        $scenario.error = $failure.Exception.Message
        $scenario.stack = $failure.ScriptStackTrace
        $scenario.durationMs = $script:uiReport.clock.ElapsedMilliseconds
        $scenario.finished = [DateTime]::UtcNow.ToString('o')
        $script:uiReport.clock = $null
        # Persist the failure before calling providers that could hang on a crashed app.
        Write-UiJournal
        $prefix = 'diagnostics/' + $script:uiReport.suite + '-' + $script:uiReport.scenarios.Count
        try {
            Save-UiWindow $window (Join-Path $script:uiReport.directory ($prefix + '.png'))
            $scenario.screenshot = $prefix + '.png'
        } catch { $scenario.screenshotError = $_.Exception.Message }
        Write-UiJournal
        try {
            if (!$window) { throw 'No test window is available.' }
            $lines = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object { $_.Current.AutomationId + ' | ' + $_.Current.Name + ' | enabled=' + $_.Current.IsEnabled + ' | offscreen=' + $_.Current.IsOffscreen }
            $path = Join-Path $script:uiReport.directory ($prefix + '.txt')
            $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
            $lines | Set-Content -LiteralPath $path
            $scenario.automationTree = $prefix + '.txt'
        } catch { $scenario.automationTreeError = $_.Exception.Message }
        Write-UiJournal
    } catch { Write-Warning ('Could not save failure diagnostics: ' + $_.Exception.Message) }
}

function Read-UiScenarios([string]$directory, [switch]$Interrupted) {
    $journalDirectory = Join-Path $directory 'scenarios'
    if (!(Test-Path -LiteralPath $journalDirectory)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $journalDirectory -Filter '*.json' | Sort-Object Name) {
        foreach ($scenario in @(Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json)) {
            if ($Interrupted -and $scenario.status -eq 'running') {
                $scenario.status = 'interrupted'
                $started = ([DateTime]$scenario.started).ToUniversalTime()
                $scenario | Add-Member -NotePropertyName durationMs -NotePropertyValue ([long]([DateTime]::UtcNow - $started).TotalMilliseconds) -Force
                $scenario | Add-Member -NotePropertyName finished -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
                $scenario | Add-Member -NotePropertyName screenshotError -NotePropertyValue 'Worker stopped before failure capture; no screenshot is available.' -Force
            }
            $scenario
        }
    }
}

function Write-UiResult([string]$directory, $result) {
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $directory 'result.json')
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# UI tests: ' + $result.status)
    $lines.Add('')
    if ($result.error) { $lines.Add('Failure: ' + $result.error); $lines.Add('') }
    $lines.Add('| Suite | Scenario | Status | Seconds | Diagnostics |')
    $lines.Add('| --- | --- | --- | ---: | --- |')
    foreach ($scenario in $result.scenarios) {
        $links = @()
        if ($scenario.screenshot) { $links += '[Screenshot](' + $scenario.screenshot + ')' }
        if ($scenario.automationTree) { $links += '[UI Automation tree](' + $scenario.automationTree + ')' }
        $name = $scenario.name.Replace('|', '\|').Replace("`n", ' ')
        $seconds = ([double]$scenario.durationMs / 1000).ToString('0.000', [Globalization.CultureInfo]::InvariantCulture)
        $lines.Add('| ' + $scenario.suite + ' | ' + $name + ' | ' + $scenario.status + ' | ' + $seconds + ' | ' + ($links -join ', ') + ' |')
    }
    foreach ($scenario in $result.scenarios) {
        $name = $scenario.name
        if ($scenario.error) { $lines.Add(''); $lines.Add('Failure in ' + $name + ': ' + $scenario.error); $lines.Add('') }
        if ($scenario.screenshotError) { $lines.Add(''); $lines.Add('Screenshot unavailable: ' + $scenario.screenshotError); $lines.Add('') }
    }
    if ($result.skipped) { $lines.Add(''); $lines.Add('Skipped: ' + ($result.skipped -join '; ')) }
    $lines | Set-Content -LiteralPath (Join-Path $directory 'report.md')
}
