# Native navigation and browser editor automation in test-review-layout.ps1's disposable fixture.
function Connect-Editor {
    if ($script:browser) { $script:browser.Dispose() }
    $script:browser = Wait-For { try { Connect-TestWebView $port } catch { $null } }
    $null = Wait-For { Invoke-TestWebView $browser "typeof monaco !== 'undefined' && monaco.editor.getEditors().length === 3" }
}
function Open-File([string]$name) {
    Select-Control (Wait-For { Find 'ChangesTab' })
    Select-Control (Wait-For { Row 'Files' "*$name*" })
    $null = Wait-For { (Find 'TitleText').Current.Name -like "$name*" }
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getEditors().some(e=>e.getModel()?.getValue().startsWith('Working $name line 1 '))" }
}
function Read-Editor {
    Invoke-TestWebView $browser @'
(() => {
  const editors = monaco.editor.getEditors().filter(e => e.getModel() && !e.getDomNode()?.closest('#t'));
  return editors.map(e=>({selection:e.getSelection(), top:e.getScrollTop(), left:e.getScrollLeft(), topLine:e.getVisibleRanges()[0]?.startLineNumber, lines:e.getModel().getLineCount()}));
})()
'@
}
function Position-Editor {
    $null = Invoke-TestWebView $browser @'
(() => {
  const editors = monaco.editor.getEditors().filter(e=>e.getModel() && !e.getDomNode()?.closest('#t'));
  editors.forEach((e,i)=>{
    e.setSelection(new monaco.Selection(246+i*2,12,242+i*2,5));
    e.setScrollPosition({scrollTop:e.getTopForLineNumber(230)+7,scrollLeft:90},monaco.editor.ScrollType.Immediate);
  });
})()
'@
    # Let Monaco synchronize the two sides; do not explicitly ask the host to capture anything.
    $null = Wait-For { $state = Read-Editor; $state.Count -eq 2 -and $state[1].top -gt 1000 -and $state[1].selection.positionLineNumber -eq 244 }
    Read-Editor
}
function Assert-Reading($expected, [int]$added = 0) {
    $null = Wait-For {
        $actual = Read-Editor
        $actual.Count -eq 2 -and $actual[1].selection.positionLineNumber -eq ($expected[1].selection.positionLineNumber + $added)
    } 'The modified-side caret was not restored.'
    $actual = Read-Editor
    if ($added -and $actual[1].topLine -ne ($expected[1].topLine + $added)) { throw 'Inserted lines lost the top visible code line.' }
    for ($i = 0; $i -lt 2; $i++) {
        $shift = if ($i -eq 1) { $added } else { 0 }
        if ($actual[$i].selection.selectionStartLineNumber -ne ($expected[$i].selection.selectionStartLineNumber + $shift) -or
            $actual[$i].selection.selectionStartColumn -ne $expected[$i].selection.selectionStartColumn -or
            $actual[$i].selection.positionColumn -ne $expected[$i].selection.positionColumn) { throw "Selection direction or column lost on side $i." }
        if ([Math]::Abs($actual[$i].left - $expected[$i].left) -gt 2) { throw "Horizontal scroll lost on side $i." }
        if (!$added -and [Math]::Abs($actual[$i].top - $expected[$i].top) -gt 2) { throw "Vertical scroll lost on side $i." }
    }
}
function Leave-Commit {
    Invoke-Control (Wait-For { Find 'AdvancedButton' }); Invoke-Control (Wait-For { Find 'ShelfButton' })
    $null = Wait-For { !(Find 'CommitButton') -and (Find 'PART_BackButton') }
}
function Return-Commit {
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { $b=Find 'CommitButton'; $b -and $b.Current.HelpText -notlike 'Wait for*' }
    Connect-Editor
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getEditors().some(e=>!e.getDomNode()?.closest('#t') && e.getModel()?.getValue().includes('Working base.txt line 500'))" }
}

$browser = $null
try {
    $settingsFile = Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json'
    $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName DiffCollapsed -NotePropertyValue $false -Force
    $settings | Add-Member -NotePropertyName DiffInline -NotePropertyValue $false -Force
    $settings | ConvertTo-Json | Set-Content $settingsFile
    foreach ($name in @('base.txt', 'keep.txt')) {
        $lines = 1..500 | ForEach-Object { "Original $name line $_ " + ('column ' * 35) }
        [IO.File]::WriteAllLines((Join-Path $worktree $name), $lines)
    }
    & git -C $worktree add base.txt keep.txt
    & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -m 'Long reading fixture' | Out-File $setupLog -Append
    Check-Exit 'reading fixture commit'
    foreach ($name in @('base.txt', 'keep.txt')) {
        $lines = 1..500 | ForEach-Object { "Working $name line $_ " + ('column ' * 35) }
        [IO.File]::WriteAllLines((Join-Path $worktree $name), $lines)
    }
    Start-UiScenario 'Unified patches retain scroll and selection across navigation'
    Launch 'commit' $worktree
    $null = Wait-For { (Find 'CommitButton').Current.IsEnabled }
    Select-Control (Wait-For { Find 'DiffTab' })
    Connect-Editor
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getEditors().some(e=>e.getDomNode()?.closest('#t') && e.getModel()?.getLineCount()>500)" }
    $unified = Invoke-TestWebView $browser "(() => { const e=monaco.editor.getEditors().find(e=>e.getDomNode()?.closest('#t')); e.setSelection(new monaco.Selection(206,12,203,5)); e.setScrollPosition({scrollTop:e.getTopForLineNumber(190)+7,scrollLeft:90},monaco.editor.ScrollType.Immediate); return {selection:e.getSelection(),top:e.getScrollTop(),left:e.getScrollLeft()}; })()"
    Leave-Commit
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { $b=Find 'CommitButton'; $b -and $b.Current.HelpText -notlike 'Wait for*' }
    Connect-Editor
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getEditors().some(e=>e.getDomNode()?.closest('#t') && e.getSelection()?.positionLineNumber===203)" }
    $restored = Invoke-TestWebView $browser "(() => {const e=monaco.editor.getEditors().find(e=>e.getDomNode()?.closest('#t')); return {selection:e.getSelection(),top:e.getScrollTop(),left:e.getScrollLeft()};})()"
    if ($restored.selection.selectionStartLineNumber -ne 206 -or [Math]::Abs($restored.top-$unified.top) -gt 2 -or [Math]::Abs($restored.left-$unified.left) -gt 2) { throw 'Unified reading position changed.' }
    Complete-UiScenario

    Start-UiScenario 'File switching keeps independent diff scroll and backwards selections'
    Open-File 'base.txt'
    $expected = Position-Editor
    Open-File 'keep.txt'
    $initial = Read-Editor
    if ($initial[1].selection.positionLineNumber -ne 1) { throw 'A different file inherited the previous caret.' }
    Open-File 'base.txt'
    Assert-Reading $expected
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'reading-file-return.png')
    Complete-UiScenario

    Start-UiScenario 'Back navigation restores reading positions after disposing the WebView'
    $oldTarget = Invoke-TestWebView $browser 'performance.timeOrigin'
    Leave-Commit; Return-Commit
    if ((Invoke-TestWebView $browser 'performance.timeOrigin') -eq $oldTarget) { throw 'Navigation kept the old editor alive.' }
    Assert-Reading $expected
    Save-TestWebView $browser (Join-Path $ArtifactDirectory 'reading-navigation-return.png')
    Complete-UiScenario

    Start-UiScenario 'Reading anchors follow inserted lines after a fresh repository read'
    Leave-Commit
    $path = Join-Path $worktree 'base.txt'
    [IO.File]::WriteAllText($path, "Inserted first line`nInserted second line`n" + [IO.File]::ReadAllText($path))
    Return-Commit
    Assert-Reading $expected 2
    Complete-UiScenario

    Start-UiScenario 'A shorter replacement clamps the caret without restoring old text'
    Leave-Commit
    [IO.File]::WriteAllText($path, "Short replacement`n")
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { $b=Find 'CommitButton'; $b -and $b.Current.HelpText -notlike 'Wait for*' }
    Connect-Editor
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getEditors().some(e=>e.getModel()?.getValue()==='Short replacement\n')" }
    $null = Wait-For { $state=Read-Editor; $state[1].selection.positionLineNumber -eq 2 -and $state[1].selection.positionColumn -eq 1 }
    Complete-UiScenario
} catch {
    if ($browser) {
        try {
            Invoke-TestWebView $browser "(() => ({models:monaco.editor.getModels().map(m=>({lines:m.getLineCount(),start:m.getValue().slice(0,100)})),editors:monaco.editor.getEditors().map(e=>({selection:e.getSelection(),top:e.getScrollTop(),left:e.getScrollLeft(),hasNode:!!e.getDomNode()}))}))()" | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDirectory 'reading-browser.json')
            Save-TestWebView $browser (Join-Path $ArtifactDirectory 'reading-browser.png')
        } catch { }
    }
    throw
} finally { if ($browser) { $browser.Dispose() } }
