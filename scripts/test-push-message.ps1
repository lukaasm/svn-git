# Native UI Automation on a private desktop. Build Debug successfully before running.
# Creates a local SVN fixture; no push or server write is performed by the UI.
param([switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/push-message-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-push-message-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Push message UI test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Push message UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Push message UI. Artifacts: $ArtifactDirectory"
    } finally { $desktop.Dispose() }
    return
}
. "$PSScriptRoot/ui-test-report.ps1"
. "$PSScriptRoot/ui-automation.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$env:SG_UI_TEST_DIRECTORY = Join-Path $ArtifactDirectory 'settings'
$env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $ArtifactDirectory 'webview'
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
function Find([string]$id, [switch]$Name, $within = $window) {
    # WebView's provider can truncate a descendant search before later native siblings.
    # Traverse native controls breadth first, without entering the diff's browser subtree.
    $queue = [Collections.Generic.Queue[System.Windows.Automation.AutomationElement]]::new()
    $queue.Enqueue($within)
    while ($queue.Count) {
        $node = $queue.Dequeue()
        foreach ($child in $node.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            $key = if ($Name) { $child.Current.Name } else { $child.Current.AutomationId }
            if ($key -eq $id) { return $child }
            if ($child.Current.AutomationId -ne 'Web') { $queue.Enqueue($child) }
        }
    }
}
function Wait-For([scriptblock]$read, [string]$message = 'Push message UI assertion timed out.') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Read-Value($control) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Select-Range([int]$count) {
    $rows = (Find 'Commits').FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $rows[3 - $count].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { (Find 'Header').Current.Name -like "Sending $count of 3 commit*" }
}
function Open-Message {
    $button = Wait-For { $b = Find 'PushButton'; if ($b -and $b.Current.IsEnabled) { $b } }
    Invoke-Control $button
    $header = Wait-For { Find 'Commit message for SVN' -Name }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $panel = $walker.GetParent($walker.GetParent($header))
    Wait-For {
        $panel.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
    }
}
function Close-Message {
    Invoke-Control (Find 'CloseButton')
    $null = Wait-For { !(Find 'Commit message for SVN' -Name) }
}
function Assert-Message($box, [int]$count) {
    $text = Read-Value $box
    $text | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "message-$count.json")
    # Compare complete messages, including bodies and ordering, ignoring only native line endings.
    $expected = (1..$count | ForEach-Object { "Change $_`n`nDetails for change $_" }) -join "`n`n"
    if ($text.Replace("`r`n", "`n").Replace("`r", "`n").Trim() -ne $expected) {
        throw "Message does not match the selected $count commit(s). Actual: $($text | ConvertTo-Json -Compress)"
    }
}
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
$process = $null; $window = $null
Initialize-UiReport $ArtifactDirectory 'Push message selection'
try {
    $setupLog = Join-Path $ArtifactDirectory 'setup.txt'
    $repository = Join-Path $ArtifactDirectory 'svnrepo'
    & svnadmin create $repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create SVN fixture.' }
    $url = ([Uri]($repository + '/')).AbsoluteUri.TrimEnd('/')
    $file = Join-Path $ArtifactDirectory 'base.txt'
    [IO.File]::WriteAllText($file, "Push message regression fixture`n")
    & svnmucc -m 'Create push fixture' mkdir "$url/trunk" put $file "$url/trunk/base.txt" | Out-File $setupLog
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed SVN fixture.' }
    $fixture = Join-Path $ArtifactDirectory 'root'
    & $cli init $fixture --no-fsmonitor | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture root.' }
    & $cli checkout add --url "$url/trunk" --root $fixture --name checkout | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not register fixture checkout.' }
    & $cli branch feature --root $fixture --from checkout | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture branch.' }
    $worktree = Join-Path $fixture 'feature'
    foreach ($i in 1..3) {
        [IO.File]::WriteAllText((Join-Path $worktree "change-$i.txt"), "Change $i`n")
        & git -C $worktree add "change-$i.txt"
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage fixture file.' }
        & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -m "Change $i" -m "Details for change $i" | Out-File $setupLog -Append
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit fixture file.' }
    }
    $process = Start-Process -FilePath $app -ArgumentList @('push', ('"' + $worktree + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    $null = Wait-For { $h = Find 'Header'; $h -and $h.Current.Name -like '3 commit*' }

    Start-UiScenario 'Selecting two commits generates only those subjects and bodies'
    Select-Range 2
    Assert-Message (Open-Message) 2
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'Narrowing the range again replaces the untouched generated message'
    Select-Range 1
    Assert-Message (Open-Message) 1
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'Send all restores the full generated message'
    Invoke-Control (Find 'AllCommitsButton')
    $null = Wait-For { (Find 'Header').Current.Name -like '3 commit*' }
    Assert-Message (Open-Message) 3
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'A hand-written message survives changing the selected range'
    $body = "Hand-written SVN summary`n`nKeep this explanation."
    $custom = "# Private drafting note, omitted from the commit.`n`n" + $body
    $box = Open-Message
    Enter-Value $box $custom
    $null = Wait-For { Find ($body.Length.ToString() + ' characters') -Name } 'Message validation did not clean the editor line endings correctly.'
    $stored = Read-Value $box
    Close-Message
    Select-Range 2
    $box = Open-Message
    if ((Read-Value $box) -ne $stored) { throw 'Changing the range overwrote the hand-written message.' }
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'Clearing a custom draft resumes generation for the selected range'
    Enter-Value (Open-Message) ''
    Close-Message
    Select-Range 1
    Assert-Message (Open-Message) 1
    Close-Message
    if ((& svnlook youngest $repository) -ne '1') { throw 'Preview testing unexpectedly wrote to SVN.' }
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
