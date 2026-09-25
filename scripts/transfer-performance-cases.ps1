# Private-desktop UI Automation against a large disposable checkout; no desktop input injection.
$env:SG_UI_TEST_WINDOW_SIZE = '1100x1000'
$trackedDirectory = Join-Path $checkout 'large preview'
$newDirectory = Join-Path $checkout 'new files'
$null = New-Item -ItemType Directory -Path $trackedDirectory
$null = New-Item -ItemType Directory -Path $newDirectory
for ($i = 0; $i -lt 512; $i++) { [IO.File]::WriteAllText((Join-Path $trackedDirectory ('tracked {0:D4}.txt' -f $i)), "Original $i`n") }
& svn add $trackedDirectory | Out-File $setupLog -Append
Check-Exit 'add large fixture'
& svn commit $trackedDirectory -m 'Seed large transfer preview' | Out-File $setupLog -Append
Check-Exit 'commit large fixture'
& $cli sync checkout --root $fixture | Out-File $setupLog -Append
Check-Exit 'snapshot large fixture'
for ($i = 0; $i -lt 512; $i++) {
    [IO.File]::WriteAllText((Join-Path $trackedDirectory ('tracked {0:D4}.txt' -f $i)), "Edited $i`n")
    [IO.File]::WriteAllText((Join-Path $newDirectory ('draft {0:D4}.txt' -f $i)), "New $i`n")
}
function Set-PreviewName([string]$name) { (Find 'TransferBranchName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name) }
function Preview-Running { $c = Find 'TransferCancelPreview'; if ($c -and !$c.Current.IsOffscreen) { $c } }
Start-UiScenario 'Large preview keeps destination controls responsive and cancels stale options'
Launch 'transfer' $checkout
$null = Wait-For { Find 'TransferBranchName' }
$idleNameValue = (Find 'TransferBranchName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$idleEditTime = [Diagnostics.Stopwatch]::StartNew()
$idleNameValue.SetValue('large-first')
$idleEditTime.Stop()
Invoke-Control (Wait-For { $b = Find 'TransferPreview'; if ($b.Current.IsEnabled) { $b } })
$null = Wait-For { Preview-Running } 'The large preview did not expose cancellation.'
if (!(Find 'TransferBranchName').Current.IsEnabled -or !(Find 'TransferDestinationKind').Current.IsEnabled) { throw 'Preview disabled its destination controls.' }
$nameValue = (Find 'TransferBranchName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$editTime = [Diagnostics.Stopwatch]::StartNew()
$nameValue.SetValue('large-second')
$editTime.Stop()
# WinUI's UIA ValuePattern itself has an idle round-trip cost. Measure added delay during a read.
if ($editTime.ElapsedMilliseconds - $idleEditTime.ElapsedMilliseconds -gt 1000) { throw "Editing during preview took $($editTime.ElapsedMilliseconds) ms (idle: $($idleEditTime.ElapsedMilliseconds) ms)." }
$null = Wait-For { !(Preview-Running) -and (Find 'TransferPreview').Current.IsEnabled }
if ((Find 'TransferApply').Current.IsEnabled) { throw 'An obsolete preview enabled apply after the destination changed.' }
Complete-UiScenario

Start-UiScenario 'Large preview cancels explicitly, then shows all files with usable filtering'
Invoke-Control (Find 'TransferPreview')
Invoke-Control (Wait-For { Preview-Running })
$null = Wait-For { !(Preview-Running) -and (Find 'TransferPreview').Current.IsEnabled }
if ((Find 'TransferApply').Current.IsEnabled) { throw 'A cancelled preview enabled apply.' }
$previewTime = [Diagnostics.Stopwatch]::StartNew()
Invoke-Control (Find 'TransferPreview')
$null = Wait-For { (Find 'TransferApply').Current.IsEnabled } 'The 1,024-file preview did not finish in 20 seconds.'
$previewTime.Stop()
$null = Wait-For { Find '1024 files · 0 conflicts · 0 left behind' -Name }
$filter = Find 'TransferFileFilter'
$filter.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('draft 0511')
$null = Wait-For { Row 'TransferFiles' '*draft 0511.txt*' }
Assert-Fits 'TransferApply'
Save-UiWindow $window (Join-Path $ArtifactDirectory 'large-transfer-preview.png')
if (Test-Path -LiteralPath (Join-Path $fixture 'large-second')) { throw 'Preview created a worktree.' }
$shelves = & git -C (Join-Path $fixture '.sg') for-each-ref refs/sg/shelf/
if ($shelves) { throw 'Preview created recovery shelves.' }
if ([IO.File]::ReadAllText((Join-Path $trackedDirectory 'tracked 0511.txt')) -ne "Edited 511`n") { throw 'Preview changed checkout files.' }
@{ files = 1024; previewMs = $previewTime.ElapsedMilliseconds; editResponseMs = $editTime.ElapsedMilliseconds; idleEditResponseMs = $idleEditTime.ElapsedMilliseconds } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'preview-timing.json')
Complete-UiScenario
