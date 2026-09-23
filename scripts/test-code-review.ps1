# Native UI Automation and WebView DOM automation, isolated on a private desktop and disposable worktree.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/code-review-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-code-review-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Code review UI test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Code review UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Code review UI. Artifacts: $ArtifactDirectory"
    } finally { $desktop.Dispose() }
    return
}
. "$PSScriptRoot/ui-test-report.ps1"
. "$PSScriptRoot/ui-automation.ps1"
. "$PSScriptRoot/ui-webview.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$env:SG_UI_TEST_DIRECTORY = Join-Path $ArtifactDirectory 'settings'
$env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $ArtifactDirectory 'webview'
$port = New-TestWebViewPort
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$port"
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
function Find([string]$id, [switch]$Name) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($property, $id))
}
function Wait-For([scriptblock]$read) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Code review UI assertion timed out.'
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Test-VisibleThread {
    Invoke-TestWebView $browser "(() => { const p = document.querySelector('.sg-review-panel'), r = p?.getBoundingClientRect(); return p?.checkVisibility({visibilityProperty:true}) && r.height > 100 && !!document.elementFromPoint(r.x + 20, r.y + 20)?.closest('.sg-review-panel'); })()"
}
$process = $null; $window = $null; $made = $false; $browser = $null
$branch = 'ui-review-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
Initialize-UiReport $ArtifactDirectory 'Code review'
try {
    if ($FixtureRoot -notlike '*sg-workflow-ui-*') { throw 'Use a disposable workflow fixture.' }
    $config = Get-Content -LiteralPath (Join-Path $FixtureRoot '.sg/sg.json') -Raw | ConvertFrom-Json
    & $cli branch $branch --root $FixtureRoot --from $config.checkouts[0].name | Out-File (Join-Path $ArtifactDirectory 'setup.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create review fixture worktree.' }
    $made = $true
    $path = Join-Path $FixtureRoot $branch
    [IO.File]::WriteAllText((Join-Path $path 'review-example.cs'), "int count = 1;`n")
    $app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Start-UiScenario 'Leave code feedback without modifying source files'
    $comment = Wait-For { $c = Find 'CodeReviewComment'; if ($c -and $c.Current.IsEnabled) { $c } }
    Invoke-Control $comment
    Enter-Value (Wait-For { Find 'ReviewCommentBody' }) 'Please explain the initial count.'
    (Find 'ReviewCommentFirst').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(1)
    (Find 'ReviewCommentLast').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(1)
    Invoke-Control (Wait-For { Find 'Save comment' -Name })
    $null = Wait-For { Find 'Please explain the initial count.' -Name }
    if ([IO.File]::ReadAllText((Join-Path $path 'review-example.cs')) -ne "int count = 1;`n") { throw 'Commenting changed the source.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'open-comment.png')
    Complete-UiScenario
    Start-UiScenario 'Inline markers, file counts, and reply use the same persisted thread'
    $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    $null = Wait-For { Invoke-TestWebView $browser "document.querySelectorAll('.sg-review-glyph').length > 0" }
    $files = Find 'CodeReviewFiles'
    $files.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $null = Wait-For { Find 'review-example.cs   ·   1 open · 0 resolved' -Name }
    $files.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Invoke-Control (Wait-For { Find 'Show in code' -Name })
    $null = Wait-For { Invoke-TestWebView $browser "document.querySelector('.sg-review-body')?.textContent === 'Please explain the initial count.'" }
    $null = Wait-For { Test-VisibleThread }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'inline-comment.png')
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'inline-editor.png')
    $null = Invoke-TestWebView $browser "document.querySelector('.sg-review-heading button').click()"
    $null = Invoke-TestWebView $browser "(() => { const g=document.querySelector('.sg-review-glyph'), r=g.getBoundingClientRect(); for(const type of ['mousedown','mouseup','click']) g.dispatchEvent(new MouseEvent(type,{bubbles:true,clientX:r.x+r.width/2,clientY:r.y+r.height/2,button:0,buttons:type==='mousedown'?1:0})); })()"
    $null = Wait-For { Test-VisibleThread }
    $null = Invoke-TestWebView $browser "document.querySelector('[data-action=reply]').click()"
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'Literal markup: <img src=x onerror=alert(1)> remains plain text.'
    Invoke-Control (Find 'PrimaryButton')
    $null = Wait-For { Find 'Literal markup: <img src=x onerror=alert(1)> remains plain text.' -Name }
    $null = Wait-For { Test-VisibleThread }
    $null = Wait-For { Invoke-TestWebView $browser "document.querySelector('.sg-review-history')?.textContent.includes('<img src=x onerror=alert(1)>') && !document.querySelector('.sg-review-panel img')" }
    Complete-UiScenario
    Start-UiScenario 'Resolve, reveal history, and reopen feedback'
    $null = Invoke-TestWebView $browser "document.querySelector('[data-action=resolve]').click()"
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'One is the documented initial value; reviewed the current code.'
    Invoke-Control (Wait-For { Find 'PrimaryButton' })
    $null = Wait-For { Find 'No open comments. Choose All comments to see resolved feedback.' -Name }
    if (Invoke-TestWebView $browser "document.querySelectorAll('.sg-review-glyph,.sg-review-panel').length !== 0") { throw 'Resolved comment remained in the open filter.' }
    $filter = Find 'CodeReviewFilter'
    $filter.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $option = Wait-For { Find 'All comments' -Name }
    $option.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Control (Wait-For { Find 'Reopen' -Name })
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'Please add this rationale in code.'
    Invoke-Control (Wait-For { Find 'PrimaryButton' })
    $null = Wait-For { Find 'Please add this rationale in code.' -Name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'reopened-comment.png')
    Complete-UiScenario
    Start-UiScenario 'Changed anchors stay in saved context and switching files clears inline content'
    [IO.File]::WriteAllText((Join-Path $path 'review-example.cs'), "int count = 2;`n")
    [IO.File]::WriteAllText((Join-Path $path 'second.cs'), "// another file`n")
    Invoke-Control (Wait-For { Find 'Resolve' -Name })
    $null = Wait-For { Find 'Code changed since you opened this view. Review the refreshed context, then resolve.' -Name }
    if (Find 'ReviewAddressBody') { throw 'Resolve allowed code the reviewer had not seen.' }
    Invoke-Control (Find 'CodeReviewRefresh')
    $null = Wait-For { Find 'Saved anchor · open context to compare with current code' -Name }
    if (Invoke-TestWebView $browser "document.querySelectorAll('.sg-review-glyph,.sg-review-panel').length !== 0") { throw 'Changed anchor attached to the wrong code.' }
    $files = Find 'CodeReviewFiles'
    $files.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-For { Find 'second.cs   ·   0 open · 0 resolved' -Name }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { Find 'No comments for this file. Select code and choose Comment, or leave feedback on the whole file.' -Name }
    if (Invoke-TestWebView $browser "document.querySelectorAll('.sg-review-glyph,.sg-review-panel').length !== 0") { throw 'Previous file comments leaked into the next file.' }
    Complete-UiScenario
    Start-UiScenario 'Comments survive closing and reopening the app'
    $browser.Dispose(); $browser = $null
    Stop-Process -Id $process.Id; $process.WaitForExit()
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $null = Wait-For { Find 'Please add this rationale in code.' -Name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'after-restart.png')
    Complete-UiScenario
    Start-UiScenario 'Original-side comments group at their saved line and reveal from inline layout'
    $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    foreach ($body in 'Explain the old base.', 'Check compatibility with existing callers.') {
        & $cli review comment --root $FixtureRoot --worktree $path --file base.txt --side original --lines 1:1 --body $body | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create original-side fixture comments.' }
    }
    [IO.File]::WriteAllText((Join-Path $path 'base.txt'), "modified base`n")
    Invoke-Control (Find 'CodeReviewRefresh')
    $null = Wait-For { $c = Find 'CodeReviewComment'; if ($c -and $c.Current.IsEnabled) { $c } }
    $files = Find 'CodeReviewFiles'
    $files.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-For { Find 'base.txt   ·   2 open · 0 resolved' -Name }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { Find 'Explain the old base.' -Name }
    $inline = Find 'Inline' -Name
    $toggle = $inline.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    Invoke-Control (Wait-For { Find 'Show in code' -Name })
    $null = Wait-For { Test-VisibleThread }
    if (!(Invoke-TestWebView $browser "document.querySelectorAll('.editor.original .sg-review-thread').length === 2 && document.querySelectorAll('.editor.modified .sg-review-thread').length === 0")) { throw 'Original-side threads appeared on the wrong code or lost their grouping.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'original-threads.png')
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'original-editor.png')
    Complete-UiScenario
    & $cli review export --root $FixtureRoot --worktree $path --out (Join-Path $ArtifactDirectory 'review.json') | Out-Null
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    if ($browser) {
        try { Invoke-TestWebView $browser "JSON.stringify({nodes:Array.from(document.querySelectorAll('.sg-review-zone,.sg-review-panel')).map(p => ({name:p.className,rect:p.getBoundingClientRect().toJSON(),style:p.getAttribute('style'),visibility:getComputedStyle(p).visibility})),editors:monaco.editor.getEditors().map(e=>({model:e.getModel()?.uri.toString(),line2:e.getTopForLineNumber(2),spaces:e.getWhitespaces()}))})" | Set-Content (Join-Path $ArtifactDirectory 'inline-layout.json') } catch { }
    }
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($browser) { $browser.Dispose() }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($made) { & $cli rm $branch --root $FixtureRoot --force | Out-Null }
}
