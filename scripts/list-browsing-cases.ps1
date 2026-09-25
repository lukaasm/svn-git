# Runs on test-review-layout.ps1's private desktop and owns only its disposable repositories.
$env:SG_UI_TEST_WINDOW_SIZE = '1500x1000'
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Value($control) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Scroll-List([string]$id, [double]$percent) {
    $scroll = Wait-For { $list = Find $id; if ($list) { $s=$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if ($s.Current.VerticallyScrollable) { $s } } }
    $scroll.SetScrollPercent(-1, $percent)
    $null = Wait-For { [Math]::Abs($scroll.Current.VerticalScrollPercent - $percent) -lt 3 }
}
function List-Rows([string]$id) {
    (Find $id).FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
}
function Visible-Row([string]$id, [switch]$File) {
    $bounds = (Find $id).Current.BoundingRectangle
    List-Rows $id | Where-Object { !$_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Top -gt ($bounds.Top + 1) -and
        $_.Current.BoundingRectangle.Bottom -lt ($bounds.Bottom - 1) -and (!$File -or $_.Current.Name -like '*file-*.txt*') } | Select-Object -First 1
}
function Anchor([string]$id) {
    $row = Wait-For { Visible-Row $id }
    @{ name=$row.Current.Name; top=$row.Current.BoundingRectangle.Top - (Find $id).Current.BoundingRectangle.Top }
}
function Assert-Anchor([string]$id, $anchor) {
    $null = Wait-For {
        $row = Row $id $anchor.name
        $row -and !$row.Current.IsOffscreen -and [Math]::Abs(($row.Current.BoundingRectangle.Top - (Find $id).Current.BoundingRectangle.Top) - $anchor.top) -lt 3
    } "Reading row moved in $id ($($anchor.name))."
}
function Pick-Row([string]$id, [string]$match) {
    $row = Wait-For { Row $id $match }
    $row.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
    Select-Control $row
}
function Collapse-FirstFolder([string]$id) {
    Scroll-List $id 0
    $row = Wait-For { Row $id '*module-00*' }
    Invoke-Control (Find 'Open or close the folder' -Name -within $row)
    $null = Wait-For { !(Row $id '*file-000.txt*') }
}
function Settings-AndBack {
    Select-Control (Wait-For { Find 'SettingsItem' })
    Invoke-Control (Wait-For { Find 'NavigationViewBackButton' })
}

foreach ($i in 0..119) {
    $directory = Join-Path $worktree ('src/module-{0:d2}' -f [int][Math]::Floor($i / 12))
    $null = New-Item -ItemType Directory -Path $directory -Force
    [IO.File]::WriteAllText((Join-Path $directory ('file-{0:d3}.txt' -f $i)), "Original file $i`n")
}
& git -C $worktree add .
& git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm 'Browse files initial'
Check-Exit 'file browsing commit'
for ($i = 1; $i -le 35; $i++) {
    [IO.File]::WriteAllText((Join-Path $worktree 'base.txt'), "History $i`n")
    & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -qam "History $i"
    Check-Exit 'history fixture commit'
}
$remote = Join-Path $ArtifactDirectory 'backup.git'
& git init --bare $remote | Out-File $setupLog -Append
& $cli backup set $remote --root $fixture | Out-File $setupLog -Append
Check-Exit 'set fixture backup'
& $cli backup --root $fixture | Out-File $setupLog -Append
Check-Exit 'local fixture backup'
Get-ChildItem (Join-Path $worktree 'src') -Recurse -File | ForEach-Object { [IO.File]::AppendAllText($_.FullName, "Working edit`n") }

Start-UiScenario 'Code review retains collapsed folders, selection and scroll across navigation'
Launch 'code-review' $worktree
$null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -eq 'Files (123)' }
Collapse-FirstFolder 'CodeReviewFiles'
Scroll-List 'CodeReviewFiles' 45
$picked = Visible-Row 'CodeReviewFiles' -File
$pickedName = $picked.Current.Name
Select-Control $picked
$null = Wait-For { (Find 'TitleText').Current.Name -like '*file-*.txt' }
$fileTitle = (Find 'TitleText').Current.Name
Scroll-List 'CodeReviewFiles' 72
$reviewAnchor = Anchor 'CodeReviewFiles'
Invoke-Control (Find 'CodeReviewReadiness')
Invoke-Control (Wait-For { Find 'PART_BackButton' })
$null = Wait-For { (Find 'TitleText').Current.Name -eq $fileTitle }
Assert-Anchor 'CodeReviewFiles' $reviewAnchor
# The selected row may be virtualized outside the restored viewport; query the selection provider.
$selected = (Find 'CodeReviewFiles').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
if ($selected.Count -ne 1 -or $selected[0].Current.Name -ne $pickedName) { throw 'Review file selection was lost.' }
if (Row 'CodeReviewFiles' '*file-000.txt*') { throw 'Review folder reopened.' }
Save-UiWindow $window (Join-Path $ArtifactDirectory 'review-reading.png')
Complete-UiScenario

Start-UiScenario 'Review filtering retains folder choices and empty results across navigation'
Enter-Value (Find 'CodeReviewFileSearch') 'file-000'
$null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -like '*showing 1 of*' }
Enter-Value (Find 'CodeReviewFileSearch') ''
$null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -eq 'Files (123)' }
if (Row 'CodeReviewFiles' '*file-000.txt*') { throw 'Clearing a search lost the collapsed folder.' }
Enter-Value (Find 'CodeReviewFileSearch') 'missing-file-search'
$null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -like '*showing 0 of*' }
Invoke-Control (Find 'CodeReviewReadiness')
Invoke-Control (Wait-For { Find 'PART_BackButton' })
$null = Wait-For { (Find 'CodeReviewFileCount').Current.Name -like '*showing 0 of*' }
if ((Value (Find 'CodeReviewFileSearch')) -ne 'missing-file-search') { throw 'Review search was lost.' }
Complete-UiScenario
Stop-App

Start-UiScenario 'Backup comparison retains independent list positions away from the selected commit'
Launch 'overview' $fixture
Invoke-Control (Wait-For { Find 'BackupButton' })
$card = Wait-For { Find 'BackupWorktree_feature' }
$condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
$card.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $condition).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Invoke-Control (Wait-For { Find 'BackupWorktreeCompare_feature' })
$null = Wait-For { (Find 'ComparedAt').Current.Name -like 'feature*Compared*' }
Select-Control (List-Rows 'BackupCompareRemote' | Select-Object -First 1)
$null = Wait-For { (Find 'TitleText').Current.Name -like 'Backup*' }
$patchTitle = (Find 'PatchTitle').Current.Name
Scroll-List 'BackupCompareLocal' 45; Scroll-List 'BackupCompareRemote' 70
$localAnchor = Anchor 'BackupCompareLocal'; $remoteAnchor = Anchor 'BackupCompareRemote'
Settings-AndBack
$null = Wait-For { (Find 'TitleText').Current.Name -eq $patchTitle }
Assert-Anchor 'BackupCompareLocal' $localAnchor; Assert-Anchor 'BackupCompareRemote' $remoteAnchor
Save-UiWindow $window (Join-Path $ArtifactDirectory 'comparison-reading.png')
Complete-UiScenario

Start-UiScenario 'History retains a file tree per commit and restores it after navigation'
Invoke-Control (Find 'Local history' -Name)
$null = Wait-For { Find 'Commits' }
Pick-Row 'Commits' '*Browse files initial*'
$null = Wait-For { (Find 'FilesHeader').Current.Name -eq 'Files (123)' }
Collapse-FirstFolder 'Files'
Scroll-List 'Files' 48
Select-Control (Visible-Row 'Files' -File)
$null = Wait-For { (Find 'TitleText').Current.Name -like '*file-*.txt*' }
$historyTitle = (Find 'TitleText').Current.Name
Scroll-List 'Files' 72
$filesAnchor = Anchor 'Files'
Pick-Row 'Commits' '*History 35*'
$null = Wait-For { (Find 'FilesHeader').Current.Name -eq 'Files (1)' }
Pick-Row 'Commits' '*Browse files initial*'
$null = Wait-For { (Find 'TitleText').Current.Name -eq $historyTitle }
Assert-Anchor 'Files' $filesAnchor
if (Row 'Files' '*file-000.txt*') { throw 'Switching commits reopened the folder.' }
Scroll-List 'Commits' 40
$commitsAnchor = Anchor 'Commits'
Settings-AndBack
$null = Wait-For { (Find 'TitleText').Current.Name -eq $historyTitle }
Assert-Anchor 'Files' $filesAnchor; Assert-Anchor 'Commits' $commitsAnchor
Save-UiWindow $window (Join-Path $ArtifactDirectory 'history-reading.png')
Complete-UiScenario

Start-UiScenario 'History follows its reading row when a new commit arrives while away'
Select-Control (Find 'SettingsItem')
[IO.File]::WriteAllText((Join-Path $worktree 'base.txt'), "New commit while away`n")
& git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -qam 'New history while away'
Check-Exit 'history addition'
Invoke-Control (Wait-For { Find 'NavigationViewBackButton' })
$null = Wait-For { (Find 'TitleText').Current.Name -eq $historyTitle }
Assert-Anchor 'Commits' $commitsAnchor; Assert-Anchor 'Files' $filesAnchor
Complete-UiScenario

Start-UiScenario 'A removed history selection does not restore stale files or enable rewrite actions'
Select-Control (Find 'SettingsItem')
# Move only this disposable fixture's branch below the selected commit; leave all files on disk.
& git -C $worktree reset --soft 'HEAD~37' | Out-File $setupLog -Append
Check-Exit 'fixture history replacement'
Invoke-Control (Wait-For { Find 'NavigationViewBackButton' })
$null = Wait-For { (Find 'TitleText').Current.Name -eq 'pick a commit to see what it changed' }
if ((Find 'RewordButton').Current.IsEnabled -or (Find 'RevertButton').Current.IsEnabled -or (List-Rows 'Files').Count) { throw 'Missing commits left stale files or rewrite actions.' }
Complete-UiScenario
