# Scenarios hosted by test-code-review.ps1 on its private desktop and disposable worktree.
Select-ReviewFile 'live.cs'
$null = Wait-For { Invoke-TestWebView $browser "typeof monaco !== 'undefined' && monaco.editor.getEditors().some(e=>e.getModel()?.getValue().includes('// Source line 180'))" }
$sourcePath = Join-Path $path 'live.cs'
$sourceText = [IO.File]::ReadAllText($sourcePath)
function Write-Source([string]$text) {
    $temporary = Join-Path $path 'live.tmp'
    [IO.File]::WriteAllText($temporary, $text)
    [IO.File]::Move($temporary, $sourcePath, $true)
}
function Wait-Source([string]$title) { $null = Wait-For { Find $title -Name } }
function Reload-Source([string]$expected) {
    Invoke-Control (Wait-For { $b = Find 'CodeReviewReloadCode'; if ($b -and $b.Current.IsEnabled) { $b } })
    $literal = ConvertTo-Json -Compress -InputObject $expected
    $null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getEditors().some(e=>e.getModel()?.getValue().includes($literal))" }
    $null = Wait-For { !(Find 'Code changed' -Name) -and !(Find 'Current code unavailable' -Name) }
    Start-Sleep -Milliseconds 300
}

Start-UiScenario 'Unrelated and identical saves leave the current review snapshot undisturbed'
Write-Source $sourceText
[IO.File]::WriteAllText((Join-Path $path 'second.cs'), "// unrelated edit`n")
Start-Sleep -Milliseconds 800
if (Find 'Code changed' -Name) { throw 'A same-content or unrelated save reported changed code.' }
Invoke-Control (Wait-For { Find ('ReviewShow_' + $liveId) })
$null = Wait-For { Test-VisibleThread }
$null = Invoke-TestWebView $browser "(() => { const e=monaco.editor.getEditors().find(e=>e.getModel()?.getValue().includes('// Source line 180')); e.setSelection(new monaco.Selection(52,2,53,8)); document.querySelector('.sg-review-history').scrollTop=80; window.sourceView={editor:e,model:e.getModel(),selection:JSON.stringify(e.getSelection()),top:e.getScrollTop(),history:document.querySelector('.sg-review-history').scrollTop}; })()"
Complete-UiScenario

Start-UiScenario 'Atomic source saves show a reload notice while retaining the displayed code'
$sourceText = $sourceText.Replace('// Source line 170', '// Agent changed line 170')
Write-Source $sourceText
Wait-Source 'Code changed'
if (!(Invoke-TestWebView $browser "window.sourceView.editor.getModel()===window.sourceView.model && window.sourceView.model.getValue().includes('// Source line 170') && JSON.stringify(window.sourceView.editor.getSelection())===window.sourceView.selection")) { throw 'The source save silently replaced the displayed snapshot or selection.' }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'source-changed.png')
Complete-UiScenario

Start-UiScenario 'Reload retains selection, editor scroll, inline discussion and history'
Reload-Source '// Agent changed line 170'
$state = Invoke-TestWebView $browser "(() => { const v=window.sourceView; return {selection:JSON.stringify(v.editor.getSelection())===v.selection,scroll:Math.abs(v.editor.getScrollTop()-v.top),history:document.querySelector('.sg-review-history') ? Math.abs(document.querySelector('.sg-review-history').scrollTop-v.history) : -1}; })()"
if (!$state.selection -or $state.scroll -gt 2 -or $state.history -lt 0 -or $state.history -gt 2) { throw "Source reload moved the reading state: $($state | ConvertTo-Json -Compress)" }
$state | ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'source-reload.json')
Save-TestWebView $browser (Join-Path $ArtifactDirectory 'source-reloaded.png')
Complete-UiScenario

Start-UiScenario 'Inserted lines preserve the code at the reading position and shift the selected range'
$null = Invoke-TestWebView $browser "(() => { const e=window.sourceView.editor, r=e.getVisibleRanges()[0]; window.readingLine=e.getModel().getLineContent(r.startLineNumber); })()"
$sourceText = "// New line A`n// New line B`n// New line C`n" + $sourceText
Write-Source $sourceText
Wait-Source 'Code changed'
Reload-Source '// New line A'
if (!(Invoke-TestWebView $browser "(() => { const e=window.sourceView.editor,s=e.getSelection(),r=e.getVisibleRanges()[0]; return s.startLineNumber===55 && s.endLineNumber===56 && e.getModel().getLineContent(r.startLineNumber)===window.readingLine; })()")) { throw 'Inserted lines lost the source selection or reading anchor.' }
Complete-UiScenario

Start-UiScenario 'Source notifications preserve an open draft and reload keeps it for explicit revalidation'
Invoke-Control (Find 'CodeReviewComment')
Enter-Value (Wait-For { Find 'ReviewCommentBody' }) 'Unpublished draft survives the source update.'
$sourceText = $sourceText.Replace('// Source line 160', '// Agent changed line 160')
Write-Source $sourceText
Start-Sleep -Milliseconds 650
if ((Find 'ReviewCommentBody').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Unpublished draft survives the source update.') { throw 'A source notification changed the draft text.' }
Invoke-Control (Find 'Keep draft' -Name)
Wait-Source 'Code changed'
Reload-Source '// Agent changed line 160'
Invoke-Control (Find 'CodeReviewComment')
$body = Wait-For { Find 'ReviewCommentBody' }
if ($body.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Unpublished draft survives the source update.') { throw 'Reload lost the saved draft.' }
$null = Wait-For { Find 'ReviewDraftConfirmCode' }
Invoke-Control (Find 'Discard draft' -Name)
Complete-UiScenario

Start-UiScenario 'Unreadable and deleted source retain the last good snapshot and recover after recreation'
$null = Invoke-TestWebView $browser "void(window.retainedSource=window.sourceView.editor.getModel())"
Write-Source "binary`0file"
Wait-Source 'Current code unavailable'
Invoke-Control (Find 'CodeReviewReloadCode')
Start-Sleep -Milliseconds 500
if (!(Invoke-TestWebView $browser "window.sourceView.editor.getModel()===window.retainedSource")) { throw 'Failed reload discarded the readable code.' }
[IO.File]::Delete($sourcePath)
Wait-Source 'File removed'
Invoke-Control (Find 'CodeReviewReloadCode')
Wait-Source 'Current code unavailable'
if (!(Invoke-TestWebView $browser "window.sourceView.editor.getModel()===window.retainedSource")) { throw 'Missing file discarded the readable code.' }
$sourceText = $sourceText.Replace('// Source line 150', '// File recreated')
Write-Source $sourceText
Wait-Source 'Code changed'
Reload-Source '// File recreated'
Complete-UiScenario

Start-UiScenario 'Changing files detaches source observation and prevents stale notices'
Select-ReviewFile 'second.cs'
$null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getModels().some(m=>m.getValue()==='// unrelated edit\n')" }
Write-Source ($sourceText + "// edit after switching files`n")
Start-Sleep -Milliseconds 700
if (Find 'Code changed' -Name) { throw 'The previous file triggered a notice in the new file.' }
Select-ReviewFile 'live.cs'
$null = Wait-For { Invoke-TestWebView $browser "monaco.editor.getModels().some(m=>m.getValue().includes('// edit after switching files'))" }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'source-return.png')
Complete-UiScenario
