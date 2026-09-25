# Runs inside test-review-layout.ps1's disposable fixture and private desktop.
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Value($control) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function File-Check([string]$path) {
    (Row 'Files' "*$path*").FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox))
}
function Toggle-File([string]$path) { (File-Check $path).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Checked-File([string]$path) { (File-Check $path).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On' }
function Open-Shelf {
    Invoke-Control (Wait-For { Find 'AdvancedButton' }); Invoke-Control (Wait-For { Find 'ShelfButton' })
    $null = Wait-For { !(Find 'CommitButton') -and (Find 'PART_BackButton') }
}
function Back-ToCommit {
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { $b = Find 'CommitButton'; $b -and $b.Current.HelpText -notlike 'Wait for*' }
}
function Open-CheckoutChanges {
    Invoke-Control (Wait-For { Find 'AdvancedButton' }); Invoke-Control (Wait-For { Find 'CheckoutChangesButton' })
    $null = Wait-For { !(Find 'TestButton') -and (Find 'PART_BackButton') }
}
function Back-ToMerge {
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { $b = Find 'TestButton'; $b -and $b.Current.HelpText -notlike 'Wait for*' }
}
function Choose-Source([string]$name) {
    (Find 'SourceBox').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $selectionFailure = $null
    try { Select-Control (Wait-For { Find $name -Name -within (Find 'SourceBox') }) }
    catch { $selectionFailure = $_ } # WinUI may invalidate the popup provider before Select returns.
    try {
        $null = Wait-For { (Find 'TestButton').Current.IsEnabled -and (Find 'RevisionsHeader').Current.Name -eq "Revisions on $name" }
    } catch {
        if ($selectionFailure) { throw $selectionFailure }
        throw
    }
}

if ($NavigationScope -ne 'Merge') {
Start-UiScenario 'Commit restores its draft, filter, checked files, open diff and compact pane'
Launch 'commit' $worktree
$null = Wait-For { (Find 'CommitButton').Current.IsEnabled }
Invoke-Control (Wait-For { Find 'NoneButton' }); Toggle-File 'base.txt'
Invoke-Control (Wait-For { Find 'CommitButton' })
Enter-Value (Wait-For { Message-Box }) 'Navigation draft with selected files'
Close-Dialog
Enter-Value (Find 'Filter') 'base'
$null = Wait-For { (Find 'FilesHeader').Current.Name -like '*showing 1 of*' }
Select-Control (Wait-For { Row 'Files' '*base.txt*' })
$null = Wait-For { (Find 'DiffTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
Open-Shelf
# A newly staged file must not become checked just because it appeared while the page was away.
[IO.File]::WriteAllText((Join-Path $worktree 'appeared.txt'), "New while away`n")
& git -C $worktree add appeared.txt
Check-Exit 'stage newly appeared file'
Back-ToCommit
if (!(Find 'DiffTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'Commit compact pane was lost.' }
$null = Wait-For { (Find 'TitleText').Current.Name -like '*base.txt*' }
Select-Control (Wait-For { Find 'ChangesTab' })
if ((Value (Wait-For { Find 'Filter' })) -ne 'base') { throw 'Commit filter was lost.' }
if ((Find 'CommitLabel').Current.Name -ne 'Commit 1 file') { throw 'Commit selection widened while away.' }
Invoke-Control (Wait-For { Find 'CommitButton' })
if ((Value (Wait-For { Message-Box })) -ne 'Navigation draft with selected files') { throw 'Commit draft was lost across navigation.' }
Close-Dialog
Select-Control (Wait-For { Find 'ChangesTab' })
Enter-Value (Find 'Filter') ''
$null = Wait-For { Row 'Files' '*appeared.txt*' }
if (!(Checked-File 'base.txt') -or (Checked-File 'keep.txt') -or (Checked-File 'appeared.txt') -or (Checked-File 'untracked.txt')) { throw 'Checked-file choices were not restored exactly.' }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'commit-restored.png')
Complete-UiScenario

Start-UiScenario 'An explicit empty Commit selection survives navigation'
Invoke-Control (Wait-For { Find 'NoneButton' })
Open-Shelf; Back-ToCommit
Assert-Disabled 'CommitButton' '*Select at least one*'
if (Checked-File 'base.txt') { throw 'Returning silently checked a file.' }
Complete-UiScenario

Start-UiScenario 'Amend is restored for the same commit and cancelled when HEAD changes'
Invoke-Control (Wait-For { Find 'AdvancedButton' })
(Find 'Amend').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
$null = Wait-For { Find 'Cancel amend' -Name }
Open-Shelf; Back-ToCommit
if ((Find 'CommitLabel').Current.Name -ne 'Amend') { throw 'Amend mode was lost for an unchanged commit.' }
Open-Shelf
& git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty -m 'Commit made while page was away' | Out-File $setupLog -Append
Check-Exit 'advance fixture HEAD'
Back-ToCommit
$null = Wait-For { Find 'DraftNotice' }
Assert-Disabled 'CommitButton' '*Select at least one*'
if (Find 'Cancel amend' -Name) { throw 'Amend still targets a different commit.' }
Toggle-File 'base.txt'
Invoke-Control (Wait-For { Find 'CommitButton' })
if ((Value (Wait-For { Message-Box })) -ne 'Navigation draft with selected files') { throw 'Changing HEAD lost the unsent draft.' }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'amend-head-changed.png')
Close-Dialog
Complete-UiScenario

Start-UiScenario 'A successfully committed message is not resurrected on return'
Invoke-Control (Wait-For { Find 'CommitButton' }); $null = Wait-For { Find 'PrimaryButton' }; Invoke-Control (Wait-For { Find 'PrimaryButton' })
$null = Wait-For { (& git -C $worktree log -1 --format=%s) -eq 'Navigation draft with selected files' }
$null = Wait-For { (Find 'AllButton').Current.IsEnabled -and (Find 'CommitLabel').Current.Name -eq 'Commit' }
Open-Shelf; Back-ToCommit
Toggle-File 'keep.txt'; Invoke-Control (Wait-For { Find 'CommitButton' })
if ((Value (Wait-For { Message-Box })) -eq 'Navigation draft with selected files') { throw 'A committed draft came back.' }
Close-Dialog
Stop-App
Complete-UiScenario
}
if ($NavigationScope -eq 'Commit') { return }

# The source must not be the first combo item, otherwise a default selection could masquerade as restore.
& svnmucc -m 'Another source branch' cp 1 "$url/trunk" "$url/branches/another" | Out-File $setupLog -Append
Check-Exit 'second merge source'
Start-UiScenario 'Merge restores its source, exact revision and compact pane with a fresh preview'
Launch 'merge' $checkout
$null = Wait-For { (Find 'TestButton').Current.IsEnabled }
Choose-Source 'source'
Select-Control (Row 'Revisions' '*Second source change*')
Invoke-Control (Wait-For { Find 'TestButton' })
$null = Wait-For { (Find 'TestButton').Current.IsEnabled -and (Find 'TitleText').Current.Name -like '*test only*' }
Open-CheckoutChanges; Back-ToMerge
$null = Wait-For { (Find 'TestButton').Current.IsEnabled }
if ((Find 'MergeLabel').Current.Name -ne 'Merge r4') { throw 'Merge revision or source was lost.' }
if (!(Find 'PreviewTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'Merge compact pane was lost.' }
if ((Find 'TitleText').Current.Name -ne 'Merge preview') { throw 'An old merge preview was presented as current.' }
Select-Control (Wait-For { Find 'RevisionsTab' })
if (!(Row 'Revisions' '*Second source change*').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'The exact merge revision was not restored.' }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'merge-restored.png')
Complete-UiScenario

Start-UiScenario 'Missing merge revisions block actions instead of widening the scope'
Open-CheckoutChanges
& svnmucc -m 'Recreate source with different history' rm "$url/branches/source" cp 1 "$url/trunk" "$url/branches/source" | Out-File $setupLog -Append
Check-Exit 'replace source history'
Back-ToMerge
$null = Wait-For { (Find 'ClearPickButton').Current.Name -eq 'Use all revisions' }
Assert-Disabled 'MergeButton' '*some saved selections*'
Assert-Disabled 'TestButton' '*some saved selections*'
Save-UiWindow $window (Join-Path $ArtifactDirectory 'merge-missing-revision.png')
Invoke-Control (Wait-For { Find 'ClearPickButton' })
$null = Wait-For { (Find 'TestButton').Current.IsEnabled }
Complete-UiScenario

Start-UiScenario 'A missing merge source requires an explicit replacement'
Open-CheckoutChanges
& svnmucc -m 'Remove source branch' rm "$url/branches/source" | Out-File $setupLog -Append
Check-Exit 'remove merge source'
Back-ToMerge
Assert-Disabled 'MergeButton' '*Choose both*'
$null = Wait-For { Find 'SelectionNotice' }
Choose-Source 'another'
if (!(Find 'MergeButton').Current.IsEnabled) { throw 'Explicit source choice did not recover merge planning.' }
Complete-UiScenario
