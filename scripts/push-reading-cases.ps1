# Exercises Push through native UI Automation and its own WebView, on a private desktop.
# No Push button is invoked; only disposable fixture commits are created.
$env:SG_UI_TEST_WINDOW_SIZE = '1700x1100'
$settingsFile = Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json'
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
$settings | Add-Member -NotePropertyName DiffCollapsed -NotePropertyValue $false -Force
$settings | Add-Member -NotePropertyName DiffInline -NotePropertyValue $false -Force
$settings | ConvertTo-Json | Set-Content $settingsFile
foreach ($name in @('base.txt', 'keep.txt', 'small.txt', 'middle.txt')) {
    $lines = [IO.File]::ReadAllLines((Join-Path $checkout $name))
    $first = if ($name -eq 'middle.txt') { 399 } else { 4 }
    $lines[$first] = "Changed $name first hunk " + ('column ' * 35)
    if ($name -eq 'base.txt') { $lines[$lines.Length - 5] = 'Changed base.txt last hunk' }
    [IO.File]::WriteAllLines((Join-Path $worktree $name), $lines)
}
& git -C $worktree add base.txt keep.txt small.txt middle.txt
Check-Exit 'stage push reading fixture'
& git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -m 'Review long and short changes' | Out-File $setupLog -Append
Check-Exit 'commit push reading fixture'
$svnRevision = & svnlook youngest $repository
$browser = $null
function Push-Editors([string]$body) {
    Invoke-TestWebView $browser ("(() => { const editors=monaco.editor.getEditors().filter(e=>e.getModel() && !e.getDomNode()?.closest('#t')); " + $body + ' })()')
}
function Pick-PushFile([string]$name) {
    Select-Control (Wait-For { Row 'Files' "*$name*" })
    $null = Wait-For { (Find 'TitleText').Current.Name -like "$name*" }
    $null = Wait-For { Push-Editors "return editors.length===2 && editors[1].getModel().getValue().includes('Changed $name first hunk');" }
    # Model text arrives before Monaco finishes its diff and the queued viewport update.
    $null = Wait-For { Invoke-TestWebView $browser "window.sgCaptureReading()?.key.includes('$name')" }
}
function Push-ScrollBottom {
    $null = Push-Editors "editors.forEach(e=>{e.setPosition({lineNumber:e.getModel().getLineCount(),column:1});e.revealLine(e.getModel().getLineCount());e.setScrollPosition({scrollTop:e.getScrollHeight(),scrollLeft:120},monaco.editor.ScrollType.Immediate);});"
    $null = Wait-For { Push-Editors 'return editors[1].getScrollTop()>100;' } 'Fixture did not scroll below the first change.'
}
function Assert-PushTop([int]$firstChange = 5) {
    $null = Wait-For {
        Push-Editors "const at=editors[1]?.getScrolledVisiblePosition({lineNumber:$firstChange,column:1}); return editors.length===2 && editors.every(e=>e.getScrollTop()<2 && e.getScrollLeft()<2) && at && at.top>=0 && at.top<editors[1].getLayoutInfo().height;"
    } 'Switching a Push file did not return to its first change.'
}
try {
    Launch 'push' $worktree
    $null = Wait-For { (Find 'Header').Current.Name -like '*commit*' }
    $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    $null = Wait-For { Invoke-TestWebView $browser "typeof monaco!=='undefined' && monaco.editor.getEditors().length===3" }

    Start-UiScenario 'Push opens a new small diff at the top after scrolling a long file'
    Pick-PushFile 'base.txt'; Push-ScrollBottom
    Pick-PushFile 'small.txt'; Assert-PushTop
    Complete-UiScenario

    Start-UiScenario 'Push restarts a previously viewed file at the top'
    Pick-PushFile 'keep.txt'; Push-ScrollBottom
    Pick-PushFile 'base.txt'; Push-ScrollBottom
    Pick-PushFile 'keep.txt'; Assert-PushTop
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'push-reading-expanded.png')
    Complete-UiScenario

    Start-UiScenario 'Back navigation restores the current Push reading position'
    Push-ScrollBottom
    $expectedTop = Push-Editors 'return editors[1].getScrollTop();'
    Invoke-Control (Find 'AdvancedButton')
    Invoke-Control (Wait-For { Find 'ReadinessButton' })
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $browser.Dispose(); $browser = $null
    $browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    $null = Wait-For { Invoke-TestWebView $browser "typeof monaco!=='undefined' && monaco.editor.getEditors().length===3" }
    $null = Wait-For { Push-Editors "return editors.length===2 && editors[1].getModel().getValue().includes('Changed keep.txt first hunk') && Math.abs(editors[1].getScrollTop()-$expectedTop)<2;" } 'Back navigation lost the Push reading position.'
    Complete-UiScenario

    foreach ($inline in @($false, $true)) {
        Start-UiScenario "Collapsed Push shows the first change after switching files (inline=$inline)"
        if (!$inline) { (Find 'CollapsedToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        else { (Find 'InlineToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        Pick-PushFile 'base.txt'; Push-ScrollBottom
        Pick-PushFile 'keep.txt'; Assert-PushTop
        Pick-PushFile 'small.txt'; Assert-PushTop
        Pick-PushFile 'middle.txt'; Assert-PushTop 400
        Save-TestWebView $browser (Join-Path $ArtifactDirectory "push-reading-collapsed-$inline.png")
        Complete-UiScenario
    }
    if ((& svnlook youngest $repository) -ne $svnRevision) { throw 'Reviewing files unexpectedly wrote to SVN.' }
} catch {
    if ($browser) {
        try {
            Push-Editors 'return editors.map(e=>({top:e.getScrollTop(),left:e.getScrollLeft(),selection:e.getSelection(),visible:e.getVisibleRanges(),lines:e.getModel().getLineCount()}));' |
                ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDirectory 'push-reading-state.json')
            Save-TestWebView $browser (Join-Path $ArtifactDirectory 'push-reading-failure.png')
        } catch { }
    }
    throw
} finally { if ($browser) { $browser.Dispose() } }
