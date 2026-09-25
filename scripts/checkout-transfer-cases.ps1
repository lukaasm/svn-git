# Runs on the harness's private desktop, using only its disposable Git/SVN fixtures.
$env:SG_UI_TEST_WINDOW_SIZE = '1100x1000'
function Enter-TransferValue([string]$id, [string]$text) { (Find $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Choose-Transfer([string]$id, [string]$text) {
    (Find $id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Select-Control (Wait-For { Find $text -Name })
}
[IO.File]::WriteAllText((Join-Path $checkout 'base.txt'), "Checkout content`n")
$null = New-Item -ItemType Directory -Path (Join-Path $checkout 'notes')
[IO.File]::WriteAllText((Join-Path $checkout 'notes/draft.txt'), "New notes`n")
Start-UiScenario 'Copy preview creates a new worktree and keeps checkout edits'
Launch 'transfer' $checkout
$null = Wait-For { Find 'TransferBranchName' }
Enter-TransferValue 'TransferBranchName' 'received'
Invoke-Control (Wait-For { $b=Find 'TransferPreview'; if ($b.Current.IsEnabled) { $b } })
$null = Wait-For { (Find 'TransferApply').Current.IsEnabled }
if (!(Row 'TransferFiles' '*base.txt*') -or !(Row 'TransferFiles' '*draft.txt*')) { throw 'Transfer file tree is incomplete.' }
Assert-Fits 'TransferApply'
Invoke-Control (Find 'TransferApply')
$null = Wait-For { Find 'TransferOpenWorktree' }
if ([IO.File]::ReadAllText((Join-Path $fixture 'received/base.txt')) -ne "Checkout content`n") { throw 'New worktree did not receive the edit.' }
if (![IO.File]::Exists((Join-Path $checkout 'notes/draft.txt'))) { throw 'Copy removed source.' }
Complete-UiScenario

# Keep the remainder on the same disposable root, with all data changes made by the tests above.

Start-UiScenario 'Conflicting destination blocks the transfer and changing options invalidates preview'
Choose-Transfer 'TransferDestinationKind' 'Existing worktree'
Choose-Transfer 'TransferExisting' 'feature'
if ((Find 'TransferApply').Current.IsEnabled) { throw 'Changed destination retained a valid plan.' }
Invoke-Control (Find 'TransferPreview')
$null = Wait-For { Find 'base.txt' -Name }
if ((Find 'TransferApply').Current.IsEnabled) { throw 'Conflicts should block transfer.' }
if ([IO.File]::ReadAllText((Join-Path $worktree 'base.txt')) -ne "Changed content`n") { throw 'Preview edited the destination.' }
Complete-UiScenario

Start-UiScenario 'Move requires confirmation and preserves existing worktree edits'
& svn revert (Join-Path $checkout 'base.txt') | Out-File $setupLog -Append
Check-Exit 'fixture source revert'
(Find 'TransferMove').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
Invoke-Control (Find 'TransferPreview')
$null = Wait-For { (Find 'TransferApply').Current.IsEnabled }
Invoke-Control (Find 'TransferApply')
Close-Dialog
if (![IO.File]::Exists((Join-Path $checkout 'notes/draft.txt'))) { throw 'Cancelling move changed source.' }
Invoke-Control (Find 'TransferApply')
Invoke-Control (Wait-For { Find 'PrimaryButton' })
$null = Wait-For { Find 'TransferOpenWorktree' }
if ([IO.File]::Exists((Join-Path $checkout 'notes/draft.txt'))) { throw 'Move did not clean source.' }
if ([IO.File]::ReadAllText((Join-Path $worktree 'notes/draft.txt')) -ne "New notes`n") { throw 'Move lost source bytes.' }
if ([IO.File]::ReadAllText((Join-Path $worktree 'base.txt')) -ne "Changed content`n") { throw 'Move lost destination edits.' }
Invoke-Control (Find 'TransferOpenWorktree')
$null = Wait-For { Row 'Files' '*draft.txt*' }
Complete-UiScenario

Start-UiScenario 'Rename worktree and branch together through Advanced actions'
Stop-App
Launch '' $fixture
$card = Wait-For { Find 'feature' -Name }
function Expand-Card($card) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
    $card.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $condition).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
}
Expand-Card $card
Expand-Card (Wait-For { Find 'AdvancedWorktreeActions' })
Invoke-Control (Wait-For { Find 'RenameWorktreeAction' })
Enter-TransferValue 'RenameWorktreeName' 'renamed-feature'
Invoke-Control (Find 'PrimaryButton')
$null = Wait-For { Find 'Rename worktree and branch?' -Name }
Close-Dialog
if (!(Test-Path -LiteralPath $worktree)) { throw 'Cancelling rename moved the folder.' }
Invoke-Control (Find 'RenameWorktreeAction')
Enter-TransferValue 'RenameWorktreeName' 'renamed-feature'
Invoke-Control (Find 'PrimaryButton')
$null = Wait-For { Find 'Rename worktree and branch?' -Name }
Invoke-Control (Find 'PrimaryButton')
$renamed = Join-Path $fixture 'renamed-feature'
$null = Wait-For { (Test-Path -LiteralPath $renamed) -and (Find 'renamed-feature' -Name) }
if (Test-Path -LiteralPath $worktree) { throw 'Rename left the old folder.' }
if ((& git -C $renamed branch --show-current) -ne 'renamed-feature') { throw 'Branch did not follow folder rename.' }
if ([IO.File]::ReadAllText((Join-Path $renamed 'base.txt')) -ne "Changed content`n") { throw 'Rename lost local edits.' }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'worktree-renamed.png')
Complete-UiScenario

Start-UiScenario 'Empty checkout refresh retains its empty-state layout'
Stop-App
Launch 'commit' $checkout
$clean = Wait-For { $c = Find 'The checkout is clean' -Name; if ($c -and !$c.Current.IsOffscreen) { $c } }
$before = $clean.Current.BoundingRectangle
for ($i = 0; $i -lt 3; $i++) {
    Invoke-Control (Find 'Refresh' -Name)
    $null = Wait-For { (Find 'Refresh' -Name).Current.IsEnabled }
    if ($clean.Current.IsOffscreen) { throw 'Refreshing a clean checkout hid its empty state.' }
    $filled = Find 'Files'
    if ($filled -and !$filled.Current.IsOffscreen -and $filled.Current.BoundingRectangle.Height -gt 0) { throw 'Refreshing a clean checkout flashed a populated layout.' }
    if ([Math]::Abs($clean.Current.BoundingRectangle.Top - $before.Top) -gt 1) { throw 'Empty checkout layout shifted on refresh.' }
}
Save-UiWindow $window (Join-Path $ArtifactDirectory 'checkout-empty.png')
Complete-UiScenario

function Shelf-Refs { (@(& git -C (Join-Path $fixture '.sg') for-each-ref '--format=%(refname) %(objectname)' refs/sg/shelf/) | Sort-Object) -join "`n" }
function Permanent-Choice {
    $choice = Wait-For { Find 'DiscardPermanently' }
    $toggle = $choice.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Permanent discard consent leaked from a previous dialog.' }
    $toggle.Toggle()
    $null = Wait-For { Find 'No recovery copy will be created. These changes cannot be brought back with Undo in SG.' -Name }
    if ((Find 'PrimaryButton').Current.Name -ne 'Discard permanently') { throw 'Permanent discard action does not name its consequence.' }
}

Start-UiScenario 'Permanent checkout discard warns, can be cancelled, and creates no recovery shelf'
$shelvesBefore = Shelf-Refs
[IO.File]::WriteAllText((Join-Path $checkout 'base.txt'), "Discard this edit`n")
[IO.File]::WriteAllText((Join-Path $checkout 'added.txt'), "Scheduled new file`n")
[IO.File]::WriteAllText((Join-Path $checkout 'loose.txt'), "Unversioned file`n")
& svn add (Join-Path $checkout 'added.txt') | Out-File $setupLog -Append
Check-Exit 'fixture scheduled addition'
Invoke-Control (Find 'Refresh' -Name)
$null = Wait-For { Row 'Files' '*added.txt*' }
Invoke-Control (Find 'All' -Name)
Invoke-Control (Find 'Discard' -Name)
Permanent-Choice
Close-Dialog
if ([IO.File]::ReadAllText((Join-Path $checkout 'base.txt')) -ne "Discard this edit`n" -or (Shelf-Refs) -ne $shelvesBefore) { throw 'Cancelling permanent discard changed files or shelves.' }
Invoke-Control (Find 'Discard' -Name)
Permanent-Choice
Save-UiWindow $window (Join-Path $ArtifactDirectory 'permanent-discard-warning.png')
Invoke-Control (Find 'PrimaryButton')
$null = Wait-For { Find 'The checkout is clean' -Name }
if ([IO.File]::ReadAllText((Join-Path $checkout 'base.txt')) -ne "Original content`n") { throw 'Permanent discard did not revert checkout.' }
if ((Test-Path -LiteralPath (Join-Path $checkout 'added.txt')) -or (Test-Path -LiteralPath (Join-Path $checkout 'loose.txt'))) { throw 'Permanent discard retained selected additions.' }
if ((Shelf-Refs) -ne $shelvesBefore -or (Find 'Undo' -Name)) { throw 'Permanent discard created recovery or offered Undo.' }
Complete-UiScenario

Start-UiScenario 'A subsequent checkout discard defaults to recovery and Undo restores the edit'
[IO.File]::WriteAllText((Join-Path $checkout 'base.txt'), "Recover this edit`n")
Invoke-Control (Find 'Refresh' -Name)
$null = Wait-For { Row 'Files' '*base.txt*' }
Invoke-Control (Find 'All' -Name)
Invoke-Control (Find 'Discard' -Name)
$toggle = (Wait-For { Find 'DiscardPermanently' }).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Permanent discard persisted into the next confirmation.' }
Invoke-Control (Find 'PrimaryButton')
$null = Wait-For { Find 'Undo' -Name }
if ((Shelf-Refs) -eq $shelvesBefore) { throw 'Default discard did not create recovery.' }
Invoke-Control (Find 'Undo' -Name)
$null = Wait-For { (Row 'Files' '*base.txt*') -and [IO.File]::ReadAllText((Join-Path $checkout 'base.txt')) -eq "Recover this edit`n" }
Complete-UiScenario

Start-UiScenario 'Permanent worktree discard removes staged renames and additions without a shelf'
Stop-App
& git -C $renamed mv keep.txt renamed.txt | Out-File $setupLog -Append
Check-Exit 'fixture staged rename'
& git -C $renamed add untracked.txt | Out-File $setupLog -Append
Check-Exit 'fixture staged addition'
Launch 'commit' $renamed
$null = Wait-For { (Find 'CommitButton').Current.IsEnabled }
Invoke-Control (Find 'AllButton')
Invoke-Control (Find 'AdvancedButton')
Invoke-Control (Wait-For { Find 'DiscardButton' })
Permanent-Choice
$shelvesBefore = Shelf-Refs
Invoke-Control (Find 'PrimaryButton')
$null = Wait-For { Find 'Nothing to commit' -Name }
if ([IO.File]::ReadAllText((Join-Path $renamed 'base.txt')) -ne "Original content`n") { throw 'Worktree discard did not restore tracked file.' }
if ((Test-Path -LiteralPath (Join-Path $renamed 'renamed.txt')) -or (Test-Path -LiteralPath (Join-Path $renamed 'untracked.txt'))) { throw 'Worktree discard retained staged rename or addition.' }
if ((Shelf-Refs) -ne $shelvesBefore -or (Find 'Undo' -Name)) { throw 'Permanent worktree discard created recovery or offered Undo.' }
Complete-UiScenario
