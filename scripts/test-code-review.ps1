# Native UI Automation and WebView DOM automation, isolated on a private desktop and disposable worktree.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory, [switch]$LiveOnly, [switch]$EditorOnly)
$ErrorActionPreference = 'Stop'
if ($EditorOnly) { $LiveOnly = $true }
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/code-review-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    if ($LiveOnly) { $command += ' -LiveOnly' }
    if ($EditorOnly) { $command += ' -EditorOnly' }
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-code-review-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(480)
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
function Select-ReviewFile([string]$name) {
    $row = Wait-For { (Find 'CodeReviewFiles').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)) | Where-Object { $_.Current.Name.Trim() -eq $name } | Select-Object -First 1 }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
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
    if (!$LiveOnly) {
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
    $null = Wait-For { Find '1 open' -Name }
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
    Select-ReviewFile 'second.cs'
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
    Select-ReviewFile 'base.txt'
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
    Start-UiScenario 'Usernames and review states receive the shared palette in inline threads'
    $null = Wait-For { Invoke-TestWebView $browser "(() => { const a=Array.from(document.querySelectorAll('.sg-review-author')); return a.length >= 2 && a.every(e=>getComputedStyle(e).color === getComputedStyle(a[0]).color) && a[0].style.color.startsWith('var(--sg-user-') && getComputedStyle(document.documentElement).getPropertyValue('--sg-review-open').trim().length > 0; })()" }
    Complete-UiScenario
    Start-UiScenario 'Comment drafts retain text and range across closing the composer and restarting'
    Invoke-Control (Find 'CodeReviewComment')
    Enter-Value (Wait-For { Find 'ReviewCommentBody' }) 'Draft: document the replacement base.'
    (Find 'ReviewCommentFirst').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(1)
    (Find 'ReviewCommentLast').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(1)
    # Close immediately, before the autosave debounce; Closing must flush it.
    Invoke-Control (Find 'Keep draft' -Name)
    $null = Wait-For { Find 'Resume draft' -Name }
    $browser.Dispose(); $browser = $null
    Stop-Process -Id $process.Id; $process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $path 'base.txt'), "new replacement base`n")
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Invoke-Control (Wait-For { Find 'Resume draft' -Name })
    $body = Wait-For { Find 'ReviewCommentBody' }
    if ($body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Draft: document the replacement base.') { throw 'Draft text was lost after restart.' }
    if ((Find 'ReviewCommentFirst').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 1) { throw 'Draft range was lost.' }
    if ((Find 'PrimaryButton').Current.IsEnabled) { throw 'Stale draft can submit without reviewing changed code.' }
    (Find 'ReviewDraftConfirmCode').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Control (Wait-For { $c = Find 'PrimaryButton'; if ($c.Current.IsEnabled) { $c } })
    $null = Wait-For { Find 'Draft: document the replacement base.' -Name }
    if (Find 'Resume draft' -Name) { throw 'Published comment left its draft behind.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'draft-published.png')
    Complete-UiScenario
    Start-UiScenario 'Response drafts resume and discard without changing the published discussion'
    Invoke-Control (Wait-For { Find 'Reply' -Name })
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'An unfinished response for later.'
    Invoke-Control (Find 'Keep draft' -Name)
    Invoke-Control (Wait-For { Find 'Reply' -Name })
    $body = Wait-For { Find 'ReviewAddressBody' }
    if ($body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'An unfinished response for later.') { throw 'Response draft was lost.' }
    Invoke-Control (Find 'Discard draft' -Name)
    Invoke-Control (Wait-For { Find 'Reply' -Name })
    $body = Wait-For { Find 'ReviewAddressBody' }
    if ($body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '') { throw 'Discarded response draft returned.' }
    Invoke-Control (Find 'Discard draft' -Name)
    Complete-UiScenario
    Start-UiScenario 'Concurrent feedback rejects stale publication while retaining the response draft'
    $reply = Wait-For { Find 'Reply' -Name }
    $threadId = $reply.Current.AutomationId.Substring('ReviewReply_'.Length)
    Invoke-Control $reply
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'My response must survive a concurrent update.'
    $context = & $cli review thread $threadId --root $FixtureRoot --worktree $path | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read concurrency fixture thread.' }
    & $cli review reply $threadId --root $FixtureRoot --worktree $path --body 'Another reviewer replied while the composer was open.' --expected-revision $context.thread.revision --actor 'Another reviewer' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not update concurrency fixture thread.' }
    Invoke-Control (Find 'PrimaryButton')
    $null = Wait-For { $e = Find 'ReviewDraftError'; $e -and !$e.Current.IsOffscreen }
    $body = Find 'ReviewAddressBody'
    if (!$body -or $body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'My response must survive a concurrent update.') { throw 'Failed submission lost its draft.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'retained-after-failure.png')
    Invoke-Control (Find 'Keep draft' -Name)
    $null = Wait-For { Find 'Another reviewer replied while the composer was open.' -Name }
    Invoke-Control (Wait-For { Find ('ReviewReply_' + $threadId) })
    $body = Wait-For { Find 'ReviewAddressBody' }
    if ($body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'My response must survive a concurrent update.') { throw 'Failed response did not restore.' }
    Invoke-Control (Find 'PrimaryButton')
    $null = Wait-For { Find 'My response must survive a concurrent update.' -Name }
    Complete-UiScenario
    Start-UiScenario 'Next and previous open comments cross files and reveal uncertain saved context'
    & $cli review comment --root $FixtureRoot --worktree $path --file second.cs --body 'Whole-file feedback on the second file.' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create cross-file fixture comment.' }
    Invoke-Control (Find 'CodeReviewRefresh')
    $null = Wait-For { $c = Find 'ReviewNextOpen'; if ($c -and $c.Current.IsEnabled) { $c } }
    # Starting from a base.txt thread, traverse the three base comments and the other two files.
    $seenMoved = $false; $seenSecond = $false
    for ($i = 0; $i -lt 5; $i++) {
        Invoke-Control (Find 'ReviewNextOpen')
        $null = Wait-For { (Find 'ReviewPosition').Current.Name -like 'Open comment * of 5' }
        $null = Wait-For { $loading = Find 'CodeReviewLoading'; !$loading -or $loading.Current.IsOffscreen }
        if (Find 'Please explain the initial count.' -Name) { $seenMoved = $true; $null = Wait-For { Find 'CodeReviewBackToDiff' } }
        if (Find 'Whole-file feedback on the second file.' -Name) { $seenSecond = $true }
        Start-Sleep -Milliseconds 150
    }
    if (!$seenMoved -or !$seenSecond) { throw 'Open comment navigation did not traverse every file.' }
    Invoke-Control (Find 'ReviewPreviousOpen')
    $null = Wait-For { (Find 'ReviewPosition').Current.Name -like 'Open comment * of 5' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'cross-file-navigation.png')
    Complete-UiScenario
    & $cli review export --root $FixtureRoot --worktree $path --out (Join-Path $ArtifactDirectory 'review.json') | Out-Null
    Start-UiScenario 'Selecting the Push to SVN upper bound preserves a long commit list scroll position'
    if ($browser) { $browser.Dispose(); $browser = $null }
    Stop-Process -Id $process.Id; $process.WaitForExit()
    git -C $path add --all
    git -C $path -c user.name=Reviewer -c user.email=reviewer@example.test commit -qm 'Review fixture changes'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit test files.' }
    for ($i = 1; $i -le 45; $i++) {
        [IO.File]::AppendAllText((Join-Path $path 'base.txt'), "line $i`n")
        git -C $path -c user.name=Reviewer -c user.email=reviewer@example.test commit -qam "Scroll fixture $i"
        if ($LASTEXITCODE -ne 0) { throw 'Could not build long-history fixture.' }
    }
    $process = Start-Process -FilePath $app -ArgumentList @('push', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $commits = Wait-For { $c = Find 'Commits'; if ($c -and $c.Current.IsEnabled -and (Find 'Header').Current.Name -like '46 commit*') { $c } }
    $scroll = $commits.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent(-1, 60)
    $null = Wait-For { $scroll.Current.VerticalScrollPercent -gt 50 }
    $bounds = $commits.Current.BoundingRectangle
    $items = $commits.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $visible = @($items | Where-Object { !$_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Top -gt $bounds.Top + 40 -and $_.Current.BoundingRectangle.Bottom -lt $bounds.Bottom - 10 })
    if (!$visible.Count) { throw 'No fully visible commit available for upper-bound selection.' }
    $picked = $visible[[int]($visible.Count / 2)]
    $before = $scroll.Current.VerticalScrollPercent
    $picked.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { (Find 'Header').Current.Name -like 'Sending * of 46 commit*' }
    Start-Sleep -Milliseconds 500
    $after = $scroll.Current.VerticalScrollPercent
    if ([Math]::Abs($after - $before) -gt 1 -or $picked.Current.IsOffscreen) { throw "Selecting the upper bound moved the commit list: $before -> $after." }
    @{ before = $before; after = $after; sending = (Find 'Header').Current.Name } | ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'push-scroll.json')
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'push-scroll.png')
    Complete-UiScenario
    } else {
        [IO.File]::WriteAllText((Join-Path $path 'base.txt'), "new replacement base`n")
        [IO.File]::WriteAllText((Join-Path $path 'second.cs'), "// another file`n")
        foreach ($comment in @(
            @{ file = 'base.txt'; side = 'original'; body = 'Explain the old base.' },
            @{ file = 'base.txt'; side = 'modified'; body = 'Review the replacement.' },
            @{ file = 'base.txt'; side = 'modified'; body = 'Check the replacement.' },
            @{ file = 'review-example.cs'; side = 'modified'; body = 'Please explain the initial count.' },
            @{ file = 'second.cs'; side = 'modified'; body = 'Whole-file feedback on the second file.' }
        )) {
            & $cli review comment --root $FixtureRoot --worktree $path --file $comment.file --side $comment.side --lines 1:1 --body $comment.body | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not create focused live-review fixture.' }
        }
    }
    # Agent writes use the public CLI against the disposable fixture while its UI stays open.
    if ($process) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $path 'live.cs'), ((1..180 | ForEach-Object { "// Source line $_" }) -join "`n") + "`n")
    $live = & $cli review comment --root $FixtureRoot --worktree $path --file live.cs --lines 50:50 --body 'Keep this discussion in view.' --actor 'Reviewer' | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not create live fixture comment.' }
    $liveId = $live.id
    function Send-AgentFeedback([string]$action, [string]$text, [string]$id = $liveId) {
        $context = & $cli review thread $id --root $FixtureRoot --worktree $path | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Could not read live fixture thread.' }
        & $cli review $action $id --root $FixtureRoot --worktree $path --body $text --expected-revision $context.thread.revision --version $context.version --actor 'Agent' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not update live fixture thread.' }
    }
    for ($i = 1; $i -le 6; $i++) { Send-AgentFeedback reply "Earlier feedback $i. Preserve this history while more replies arrive." }
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Select-ReviewFile 'live.cs'
    $null = Wait-For { Find 'Keep this discussion in view.' -Name }
    $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    if (!$EditorOnly) {
    Start-UiScenario 'Agent replies appear live without replacing code, selection, discussion or editor scroll'
    Invoke-Control (Wait-For { Find ('ReviewShow_' + $liveId) })
    $null = Wait-For { Test-VisibleThread }
    $null = Invoke-TestWebView $browser "(() => { const e=monaco.editor.getEditors().find(e=>e.getModel()?.getValue().includes('// Source line 180')); e.setSelection(new monaco.Selection(52,2,53,8)); const h=document.querySelector('.sg-review-history'); h.scrollTop=80; window.liveView={editor:e,model:e.getModel(),top:e.getScrollTop(),selection:JSON.stringify(e.getSelection()),history:h.scrollTop}; })()"
    $discussion = (Find 'CodeReviewDiscussion').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $discussion.SetScrollPercent(-1, 25)
    Start-Sleep -Milliseconds 200
    $cardTop = (Find ('ReviewThread_' + $liveId)).Current.BoundingRectangle.Top
    Send-AgentFeedback reply 'An agent reply arrived without Refresh.'
    $null = Wait-For { Find 'An agent reply arrived without Refresh.' -Name }
    $null = Wait-For { Invoke-TestWebView $browser "document.querySelector('.sg-review-history')?.textContent.includes('An agent reply arrived without Refresh.')" }
    $state = Invoke-TestWebView $browser "(() => { const v=window.liveView; return {sameModel:v.editor.getModel()===v.model,selection:JSON.stringify(v.editor.getSelection())===v.selection,scroll:Math.abs(v.editor.getScrollTop()-v.top),history:Math.abs(document.querySelector('.sg-review-history').scrollTop-v.history)}; })()"
    if (!$state.sameModel -or !$state.selection -or $state.scroll -gt 2 -or $state.history -gt 2) { throw "Live feedback moved the code: $($state | ConvertTo-Json -Compress)" }
    if ([Math]::Abs((Find ('ReviewThread_' + $liveId)).Current.BoundingRectangle.Top - $cardTop) -gt 2) { throw 'Live feedback moved the native discussion anchor.' }
    $state | ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'live-scroll.json')
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'live-reply.png')
    Complete-UiScenario
    Start-UiScenario 'Feedback on another file preserves the selected file and unchanged inline panel'
    $null = Invoke-TestWebView $browser "window.livePanel=document.querySelector('.sg-review-panel')"
    & $cli review comment --root $FixtureRoot --worktree $path --file second.cs --body 'Another file received agent feedback.' --actor 'Agent' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not add feedback on another file.' }
    $null = Wait-For { (Find 'ReviewPosition').Current.Name -like '* of 7' }
    if (!(Invoke-TestWebView $browser "document.querySelector('.sg-review-panel')===window.livePanel && window.liveView.editor.getModel()===window.liveView.model")) { throw 'Unrelated feedback rebuilt the active discussion.' }
    Complete-UiScenario
    Start-UiScenario 'Live feedback waits for the composer and preserves its unpublished draft'
    Invoke-Control (Find ('ReviewReply_' + $liveId))
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'An unfinished draft while the agent works.'
    Send-AgentFeedback reply 'Feedback received while the draft was open.'
    $null = Wait-For { Find 'New feedback waiting' -Name }
    if ((Find 'ReviewAddressBody').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'An unfinished draft while the agent works.') { throw 'Agent feedback changed the draft.' }
    if (Invoke-TestWebView $browser "document.querySelector('.sg-review-history')?.textContent.includes('Feedback received while the draft was open.')") { throw 'Discussion was replaced underneath an open composer.' }
    Invoke-Control (Find 'Keep draft' -Name)
    $null = Wait-For { Find 'Feedback received while the draft was open.' -Name }
    Invoke-Control (Find ('ReviewReply_' + $liveId))
    $body = Wait-For { Find 'ReviewAddressBody' }
    if ($body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'An unfinished draft while the agent works.') { throw 'Live refresh lost the saved draft.' }
    Invoke-Control (Find 'Discard draft' -Name)
    Complete-UiScenario
    Start-UiScenario 'An agent resolution remains visible under the open filter with its result and reopen action'
    Send-AgentFeedback resolve 'Agent verified the current code and resolved this feedback.'
    $null = Wait-For { Find 'Agent verified the current code and resolved this feedback.' -Name }
    $null = Wait-For { Invoke-TestWebView $browser "document.querySelector('[data-action=reopen]') !== null && document.querySelector('.sg-review-history').textContent.includes('Agent verified the current code')" }
    if (!(Find ('ReviewAddress_' + $liveId)).Current.Name.Contains('Reopen')) { throw 'Resolved selected thread lost its native reopen action.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'live-resolution.png')
    Complete-UiScenario
    Start-UiScenario 'Invalid external metadata retains the last good discussion and recovers on the next write'
    $identityPath = git -C $path rev-parse --path-format=absolute --git-path sg-review-id
    if ($LASTEXITCODE -ne 0) { throw 'Could not find the fixture review identity.' }
    $identity = [IO.File]::ReadAllText($identityPath).Trim()
    $reviewFile = @(Get-ChildItem -LiteralPath (Join-Path $FixtureRoot '.sg') -Filter ($identity + '.json') -Recurse)[0].FullName
    if (!$reviewFile) { throw 'Could not locate the disposable fixture review document.' }
    $validReview = [IO.File]::ReadAllText($reviewFile)
    try {
        [IO.File]::WriteAllText($reviewFile, 'incomplete')
        $null = Wait-For { Find 'Live updates paused · Refresh to retry' -Name }
        if (!(Find 'Agent verified the current code and resolved this feedback.' -Name)) { throw 'Invalid metadata cleared the last good discussion.' }
    } finally { [IO.File]::WriteAllText($reviewFile, $validReview) }
    $null = Wait-For { Find 'Live updates' -Name }
    Send-AgentFeedback reopen 'Valid agent feedback resumes after metadata recovery.'
    $null = Wait-For { Find 'Valid agent feedback resumes after metadata recovery.' -Name }
    Complete-UiScenario
    Start-UiScenario 'Returning from another page catches feedback written while review was hidden'
    Invoke-Control (Find 'CodeReviewReadiness')
    $null = Wait-For { !$process.HasExited -and !(Find 'CodeReviewFiles') }
    Send-AgentFeedback reply 'Agent replied while the review page was hidden.'
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { Find 'Agent replied while the review page was hidden.' -Name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'live-return.png')
    Complete-UiScenario
    }
    Start-UiScenario 'Editor context menu creates feedback on the selected lines and correct diff side'
    $browser.Dispose(); $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    # Monaco's context-view host can use a shadow root; inspect its actual menu, not the native toolbar action.
    $null = Invoke-TestWebView $browser "window.reviewMenuItem = function find(root=document) { for (const n of root.querySelectorAll('*')) { if(n.matches('.action-label') && n.textContent.trim()==='Comment on selected lines' && n.checkVisibility({visibilityProperty:true}) && n.getBoundingClientRect().width > 0) return n; if(n.shadowRoot) { const found=find(n.shadowRoot); if(found) return found; } } return null; }"
    foreach ($side in 'modified', 'original') {
        if ($side -eq 'original') { Select-ReviewFile 'base.txt'; $null = Wait-For { Find 'Explain the old base.' -Name } }
        $first = if ($side -eq 'modified') { 49 } else { 1 }
        $end = if ($side -eq 'modified') { 53 } else { 1 }
        $last = if ($side -eq 'modified') { 52 } else { 1 }
        $modelText = if ($side -eq 'modified') { '// Source line 180' } else { 'same change on both sides' }
        $null = Wait-For { Invoke-TestWebView $browser "typeof monaco !== 'undefined' && monaco.editor.getEditors().some(e=>e.getModel()?.getValue().includes('$modelText'))" }
        $null = Invoke-TestWebView $browser "(() => { const e=monaco.editor.getEditors().find(e=>e.getModel()?.getValue().includes('$modelText')); e.setSelection(new monaco.Selection($first,1,$end,1)); e.focus(); e.trigger('test','editor.action.showContextMenu',{}); })()"
        $null = Wait-For { Invoke-TestWebView $browser "window.reviewMenuItem() !== null" }
        Save-TestWebView $browser (Join-Path $ArtifactDirectory "context-menu-$side.png")
        $point = Invoke-TestWebView $browser "(() => { const r=window.reviewMenuItem().getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()"
        $null = Invoke-TestWebViewProtocol $browser 'Input.dispatchMouseEvent' @{ type='mouseMoved'; x=$point.x; y=$point.y; button='none' }
        $null = Invoke-TestWebViewProtocol $browser 'Input.dispatchMouseEvent' @{ type='mousePressed'; x=$point.x; y=$point.y; button='left'; clickCount=1 }
        $null = Invoke-TestWebViewProtocol $browser 'Input.dispatchMouseEvent' @{ type='mouseReleased'; x=$point.x; y=$point.y; button='left'; clickCount=1 }
        $body = Wait-For { Find 'ReviewCommentBody' }
        if ((Find 'ReviewCommentFirst').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne $first -or (Find 'ReviewCommentLast').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne $last) { throw 'Context menu did not preserve the selected line range.' }
        $selection = (Find 'ReviewCommentSide').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($selection[0].Current.Name -ne $side) { throw 'Context menu used the wrong side of the diff.' }
        Enter-Value $body "Context menu feedback on $side lines."
        Invoke-Control (Find 'PrimaryButton')
        $null = Wait-For { Find "Context menu feedback on $side lines." -Name }
    }
    Complete-UiScenario
    Start-UiScenario 'Review file tree groups and filters many paths while retaining the displayed code'
    $folder = Join-Path $path 'src/module'
    $null = New-Item -ItemType Directory -Path $folder -Force
    1..80 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $folder "sample-$_.cs"), "// Example $_`n") }
    Invoke-Control (Find 'CodeReviewRefresh')
    $null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -eq 'Files (84)' }
    Enter-Value (Find 'CodeReviewFileSearch') 'sample-80'
    $null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -eq 'Files, showing 1 of 84' }
    Select-ReviewFile 'src/module/sample-80.cs'
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getModels().some(m=>m.getValue()==='// Example 80\n')" }
    Enter-Value (Find 'CodeReviewFileSearch') 'nothing-matches-this'
    $null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -eq 'Files, showing 0 of 84' }
    if (!(Invoke-TestWebView $browser "monaco.editor.getModels().some(m=>m.getValue()==='// Example 80\n')")) { throw 'Filtering files replaced the displayed source.' }
    # Cross-file navigation clears a filter that hides its target and selects the matching tree row.
    Invoke-Control (Find 'ReviewNextOpen')
    $null = Wait-For { (Find 'CodeReviewFileSearch').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '' }
    $null = Wait-For { $pickedFiles = (Find 'CodeReviewFiles').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection(); $pickedFiles.Count -eq 1 -and !$pickedFiles[0].Current.IsOffscreen }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'file-tree.png')
    Complete-UiScenario
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
