# Native UI Automation on a private desktop. Build Debug successfully before running.
# Creates a local SVN fixture; no push or server write is performed by the UI.
param([switch]$Worker, [string]$ArtifactDirectory, [switch]$CompactOnly, [switch]$PreviewOnly)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/push-message-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    if ($CompactOnly) { $command += ' -CompactOnly' }
    if ($PreviewOnly) { $command += ' -PreviewOnly' }
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-push-message-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(600)
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
$script:fixtureCommits = 3
function Choose-Range([int]$count) {
    $rows = (Find 'Commits').FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $rows[$script:fixtureCommits - $count].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Select-Range([int]$count) {
    Choose-Range $count
    $null = Wait-For { (Find 'Header').Current.Name -like "Sending $count of $script:fixtureCommits commit*" }
}
function Open-Readiness {
    Invoke-Control (Find 'AdvancedButton')
    Invoke-Control (Wait-For { Find 'ReadinessButton' })
    $null = Wait-For { !(Find 'ReadinessButton') }
}
function Return-ToPush {
    Invoke-Control (Wait-For { Find 'PART_BackButton' })
    $null = Wait-For { $h = Find 'Header'; $h -and $h.Current.Name -ne 'Updating push preview…' }
}
function Set-CommandGate([string]$mode = 'hold') {
    $id = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $env:SG_UI_COMMAND_GATE 'request.txt'), ($mode + ':' + $id))
    Join-Path $env:SG_UI_COMMAND_GATE $id
}
function Wait-CommandGate([string]$marker) {
    $null = Wait-For { Test-Path -LiteralPath ($marker + '.entered') } 'The preview did not reach the delayed Git read.'
}
function Release-CommandGate([string]$marker) {
    Remove-Item -LiteralPath (Join-Path $env:SG_UI_COMMAND_GATE 'request.txt') -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText(($marker + '.release'), '')
}
function Assert-Cancelled([string]$marker) {
    $childId = [int](Get-Content -LiteralPath ($marker + '.entered'))
    $null = Wait-For { !(Get-Process -Id $childId -ErrorAction SilentlyContinue) } 'The obsolete preview kept its Git process running.'
}
function Assert-Pending {
    if ((Find 'PushButton').Current.IsEnabled) { throw 'Push still accepts the previous preview while the new commit range is loading.' }
    $null = Wait-For { Find 'Updating push preview…' -Name }
    if ((Find 'PushButton').Current.HelpText -notlike '*preview*') { throw 'Disabled Push does not explain that its preview is loading.' }
    $hint = Find 'DisabledHint_PushButton'
    if (!$hint -or !$hint.Current.IsKeyboardFocusable -or $hint.Current.HelpText -notlike '*preview*') {
        throw 'The loading explanation is not available to keyboard users.'
    }
}
function Open-Message([switch]$Basic) {
    $button = Wait-For { $b = Find 'PushButton'; if ($b -and $b.Current.IsEnabled) { $b } }
    Invoke-Control $button
    $header = Wait-For { Find 'Commit message for SVN' -Name }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $panel = $walker.GetParent($walker.GetParent($header))
    $box = Wait-For {
        $panel.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
    }
    if (!$Basic) { (Wait-For { Find 'RepoMessageOptions' }).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
    $box
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
    & svnmucc -m 'Create push fixture' mkdir "$url/trunk" put $file "$url/trunk/base.txt" mkdir "$url/library" put $file "$url/library/base.txt" propset svn:externals '^/library library' "$url/trunk" | Out-File $setupLog
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed SVN fixture.' }
    $fixture = Join-Path $ArtifactDirectory 'root'
    & $cli init $fixture --no-fsmonitor 2>&1 | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture root.' }
    & $cli checkout add --url "$url/trunk" --root $fixture --name checkout 2>&1 | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not register fixture checkout.' }
    & $cli branch feature --root $fixture --from checkout 2>&1 | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture branch.' }
    $worktree = Join-Path $fixture 'feature'
    foreach ($i in 1..3) {
        [IO.File]::WriteAllText((Join-Path $worktree "change-$i.txt"), "Change $i`n")
        & git -C $worktree add "change-$i.txt"
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage fixture file.' }
        if ($i -gt 1) {
            [IO.File]::WriteAllText((Join-Path $worktree 'library/base.txt'), "Library change $i`n")
            & git -C $worktree add 'library/base.txt'
            if ($LASTEXITCODE -ne 0) { throw 'Could not stage external fixture file.' }
        }
        & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -m "Change $i" -m "Details for change $i" | Out-File $setupLog -Append
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit fixture file.' }
    }
    $env:SG_UI_COMMAND_GATE = (New-Item -ItemType Directory -Path (Join-Path $ArtifactDirectory 'command-gate')).FullName
    [IO.File]::WriteAllText((Join-Path $env:SG_UI_COMMAND_GATE 'executable.txt'), (Get-Command git).Source)
    $source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'UiCommandGate.cs'))
    $project = Join-Path $env:SG_UI_COMMAND_GATE 'UiCommandGate.csproj'
    [IO.File]::WriteAllText($project, "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include=`"$source`" /></ItemGroup></Project>")
    & dotnet build $project --nologo -v quiet -m:1 -nr:false | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the test command gate.' }
    $configFile = Join-Path $fixture '.sg/sg.json'
    $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    $config.gitExe = Join-Path $env:SG_UI_COMMAND_GATE 'bin/Debug/net10.0/UiCommandGate.exe'
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configFile

    if (!$PreviewOnly) {
        Start-UiScenario 'Narrow Push keeps common actions and both review panes accessible'
        $env:SG_UI_TEST_WINDOW_SIZE = '600x900'
        $process = Start-Process -FilePath $app -ArgumentList @('push', ('"' + $worktree + '"')) -WindowStyle Hidden -PassThru
        $window = Wait-For { Get-TestAppWindow $process }
        $null = Wait-For { (Find 'Header').Current.Name -like '3 commit*' }
        function Assert-Fits([string]$id) {
            $element = Wait-For { Find $id } "Missing narrow Push control: $id"
            $r = $element.Current.BoundingRectangle; $bounds = $window.Current.BoundingRectangle
            if ($element.Current.IsOffscreen -or $r.Width -le 0 -or $r.Left -lt $bounds.Left -or $r.Right -gt $bounds.Right -or $r.Bottom -gt $bounds.Bottom) { throw "Clipped narrow Push control: $id ($r) in $bounds" }
        }
        foreach ($id in @('PushButton', 'AdvancedButton', 'CommitFilterBox', 'AllCommitsButton', 'Filter', 'ChangesTab', 'DiffTab')) { Assert-Fits $id }
        if (Find 'ReadinessButton') { throw 'Advanced review readiness is exposed before opening Advanced.' }
        Select-Range 2
        (Find 'DiffTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { (Find 'TitleText') -and !(Find 'TitleText').Current.IsOffscreen }
        Assert-Fits 'TitleText'
        Save-UiWindow $window (Join-Path $ArtifactDirectory 'push-diff-narrow.png')
        (Find 'ChangesTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { (Find 'Commits') -and !(Find 'Commits').Current.IsOffscreen }
        if ((Find 'Header').Current.Name -notlike 'Sending 2 of 3 commit*') { throw 'Changing review panes lost the selected range.' }
        (Find 'A  change-1.txt' -Name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { $title = Find 'TitleText'; $title -and !$title.Current.IsOffscreen -and $title.Current.Name -like '*change-1.txt*' }
        if ((Find 'DiffTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected -ne $true) { throw 'Selecting a file did not switch to its diff.' }
        (Find 'ChangesTab').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Save-UiWindow $window (Join-Path $ArtifactDirectory 'push-narrow.png')
        $null = Open-Message -Basic
        if ((Find 'RepoMessageOptions').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne 'Collapsed') { throw 'Per-repository messages should start collapsed.' }
        foreach ($id in @('RepoSummary', 'RepoMessageOptions', 'PrimaryButton', 'CloseButton')) { Assert-Fits $id }
        if ((Find 'RepoSummary').Current.Name -notlike '2 SVN commit*root*library*') { throw 'The collapsed message form hides its destinations.' }
        Save-UiWindow $window (Join-Path $ArtifactDirectory 'push-message-narrow.png')
        (Find 'RepoMessageOptions').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        foreach ($wc in @('root', 'library')) {
            $toggle = Wait-For { Find "Own message for $wc" -Name }
            $r = $toggle.Current.BoundingRectangle; $bounds = $window.Current.BoundingRectangle
            if ($toggle.Current.IsOffscreen -or $r.Left -lt $bounds.Left -or $r.Right -gt $bounds.Right) { throw "Clipped narrow repository message switch: $wc" }
        }
        Save-UiWindow $window (Join-Path $ArtifactDirectory 'push-message-advanced-narrow.png')
        Close-Message
        Open-Readiness
        Return-ToPush
        if ((Find 'Header').Current.Name -notlike 'Sending 2 of 3 commit*') { throw 'Advanced navigation lost the narrow view range.' }
        Stop-Process -Id $process.Id; $process.WaitForExit()
        Complete-UiScenario

        if ($CompactOnly) {
            Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
            return
        }
    }

    $env:SG_UI_TEST_WINDOW_SIZE = '1280x900'
    $process = Start-Process -FilePath $app -ArgumentList @('push', ('"' + $worktree + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For {
        $candidate = Get-TestAppWindow $process
        $pattern = $null
        if ($candidate -and $candidate.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { $candidate }
    }
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    $null = Wait-For { $h = Find 'Header'; $h -and $h.Current.Name -like '3 commit*' }

    if (!$PreviewOnly) {
        Start-UiScenario 'A shared draft survives returning from Readiness'
        $draft = "# Drafting note`n`nCustom SVN summary`n`nKeep this explanation."
        Enter-Value (Open-Message) $draft
        Close-Message
        Open-Readiness
        Return-ToPush
        $box = Open-Message
        if ((Read-Value $box).Replace("`r`n", "`n").Replace("`r", "`n") -ne $draft) { throw 'Returning from Readiness discarded the custom shared message.' }
        Enter-Value $box ''
        Close-Message
        Complete-UiScenario

        Start-UiScenario 'Own drafts stay with each working copy across navigation and range changes'
        $null = Open-Message
        foreach ($wc in @('root', 'library')) {
            (Find "Own message for $wc" -Name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Enter-Value (Wait-For { Find "Commit message for $wc" -Name }) "Custom $wc summary`n`nDetails for $wc."
        }
        Close-Message
        Select-Range 1
        Open-Readiness
        Return-ToPush
        $null = Open-Message
        if ((Read-Value (Find 'Commit message for root' -Name)) -notlike 'Custom root summary*') { throw 'Root own message was not restored.' }
        if (Find 'Own message for library' -Name) { throw 'An absent working copy remained in the selected range.' }
        Close-Message
        Invoke-Control (Find 'AllCommitsButton')
        $null = Wait-For { (Find 'Header').Current.Name -like '3 commit*' }
        $null = Open-Message
        $library = Wait-For { Find 'Commit message for library' -Name }
        if ((Read-Value $library) -notlike 'Custom library summary*') { throw 'The temporarily absent external lost its own draft.' }
        Enter-Value $library ''
        Close-Message
        Open-Readiness
        Return-ToPush
        $null = Open-Message
        if ((Read-Value (Find 'Commit message for library' -Name)) -ne '') { throw 'Returning silently replaced an empty own draft.' }
        if ((Find 'PrimaryButton').Current.IsEnabled) { throw 'An empty own draft passed validation.' }
        Enter-Value (Find 'Commit message for library' -Name) 'Saved external draft'
        foreach ($wc in @('root', 'library')) {
            (Find "Own message for $wc" -Name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        }
        Close-Message
        Open-Readiness
        Return-ToPush
        $null = Open-Message
        $toggle = (Find 'Own message for library' -Name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Returning turned an own-message override on.' }
        $toggle.Toggle()
        if ((Read-Value (Find 'Commit message for library' -Name)) -ne 'Saved external draft') { throw 'Turning the override off discarded its draft.' }
        $toggle.Toggle()
        Close-Message
        Complete-UiScenario

        Start-UiScenario 'Returning from Readiness restores the selected range and both filters'
        Select-Range 2
        Enter-Value (Find 'CommitFilterBox') 'Change 3'
        Enter-Value (Find 'Filter') 'change-2'
        $null = Wait-For { (Find 'CommitsHeader').Current.Name -like '*showing 1 of 3*' }
        Open-Readiness
        Return-ToPush
        if ((Find 'Header').Current.Name -notlike 'Sending 2 of 3 commit*') { throw 'Returning from Readiness lost the selected commit range.' }
        if ((Read-Value (Find 'CommitFilterBox')) -ne 'Change 3' -or (Read-Value (Find 'Filter')) -ne 'change-2') { throw 'Returning from Readiness lost the commit or file filter.' }
        $null = Wait-For { (Find 'CommitsHeader').Current.Name -like '*showing 1 of 3*' -and (Find 'FilesHeader').Current.Name -like '*showing 1 of 3*' }
        Assert-Message (Open-Message) 2
        Close-Message
        Enter-Value (Find 'CommitFilterBox') ''
        Enter-Value (Find 'Filter') ''
        Invoke-Control (Find 'AllCommitsButton')
        $null = Wait-For { (Find 'Header').Current.Name -like '3 commit*' }
        Complete-UiScenario

    }

    Start-UiScenario 'A pending selection blocks Push until its preview is ready'
    $before = (Find 'Commits').Current.BoundingRectangle
    $gate = Set-CommandGate
    Choose-Range 2
    Wait-CommandGate $gate
    Assert-Pending
    $pending = (Find 'Commits').Current.BoundingRectangle
    if ($before.Top -ne $pending.Top -or $before.Left -ne $pending.Left -or $before.Width -ne $pending.Width) { throw 'Loading feedback moved the commit list.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'pending-preview.png')
    Release-CommandGate $gate
    $null = Wait-For { (Find 'Header').Current.Name -like 'Sending 2 of 3 commit*' }
    Assert-Message (Open-Message) 2
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'A newer selection cancels the obsolete preview and wins'
    $obsolete = Set-CommandGate
    Choose-Range 1
    Wait-CommandGate $obsolete
    $latest = Set-CommandGate
    Choose-Range 2
    Wait-CommandGate $latest
    Assert-Cancelled $obsolete
    Assert-Pending
    Release-CommandGate $latest
    $null = Wait-For { (Find 'Header').Current.Name -like 'Sending 2 of 3 commit*' }
    Assert-Message (Open-Message) 2
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'Refreshing blocks Push while rechecking the current range'
    $gate = Set-CommandGate
    Invoke-Control (Find 'RefreshPreviewButton')
    Wait-CommandGate $gate
    Assert-Pending
    Release-CommandGate $gate
    $null = Wait-For { (Find 'Header').Current.Name -like 'Sending 2 of 3 commit*' }
    Assert-Message (Open-Message) 2
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'A failed preview keeps Push blocked and offers an inline retry'
    $gate = Set-CommandGate 'fail'
    Choose-Range 1
    Wait-CommandGate $gate
    $null = Wait-For { (Find 'Header').Current.Name -eq 'Push preview unavailable' }
    if ((Find 'PushButton').Current.IsEnabled) { throw 'A failed read enabled Push with a stale preview.' }
    if ((Find 'PushButton').Current.HelpText -notlike '*Retry*') { throw 'The disabled explanation does not offer recovery.' }
    Release-CommandGate $gate
    Invoke-Control (Find 'RetryPreviewButton')
    $null = Wait-For { (Find 'Header').Current.Name -like 'Sending 1 of 3 commit*' }
    Assert-Message (Open-Message) 1
    Close-Message
    if (Find 'RetryPreviewButton') { throw 'The stale error is still visible after a successful retry.' }
    Complete-UiScenario

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
    $gate = Set-CommandGate
    Invoke-Control (Find 'AllCommitsButton')
    Wait-CommandGate $gate
    Assert-Pending
    Release-CommandGate $gate
    $null = Wait-For { (Find 'Header').Current.Name -like '3 commit*' }
    if ((Find 'AllCommitsButton').Current.IsEnabled) { throw 'Send all was re-enabled despite already selecting every commit.' }
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
    Complete-UiScenario

    Start-UiScenario 'Leaving the page cancels its pending preview'
    $gate = Set-CommandGate
    Choose-Range 2
    Wait-CommandGate $gate
    Remove-Item -LiteralPath (Join-Path $env:SG_UI_COMMAND_GATE 'request.txt')
    Open-Readiness
    Assert-Cancelled $gate
    Return-ToPush
    if ((Find 'Header').Current.Name -notlike 'Sending 2 of 3 commit*') { throw 'Navigation lost a selection whose preview had not finished.' }
    Assert-Message (Open-Message) 2
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'Commits added while away do not widen the restored range'
    Open-Readiness
    [IO.File]::WriteAllText((Join-Path $worktree 'change-4.txt'), "Change 4`n")
    & git -C $worktree add change-4.txt
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the new fixture commit.' }
    & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit -m 'Change 4' -m 'Details for change 4' | Out-File $setupLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not append a fixture commit.' }
    $script:fixtureCommits = 4
    Return-ToPush
    if ((Find 'Header').Current.Name -notlike 'Sending 2 of 4 commit*') { throw 'New commits widened the restored range.' }
    Assert-Message (Open-Message) 2
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'A moved snapshot restores the same boundary with a new commit count'
    Open-Readiness
    $snapshotRef = 'refs/remotes/svn/checkout'
    $snapshot = & git -C $worktree rev-parse $snapshotRef
    $firstCommit = & git -C $worktree rev-parse 'HEAD~3'
    # Model a changed base only in this disposable fixture; the SVN repository stays untouched.
    & git -C $worktree update-ref $snapshotRef $firstCommit $snapshot
    if ($LASTEXITCODE -ne 0) { throw 'Could not move the fixture snapshot.' }
    try {
        Return-ToPush
        if ((Find 'Header').Current.Name -notlike 'Sending 1 of 3 commit*') { throw 'The restored count targeted a different commit after the snapshot moved.' }
        $box = Open-Message
        if ((Read-Value $box).Replace("`r`n", "`n").Replace("`r", "`n").Trim() -ne "Change 2`n`nDetails for change 2") { throw 'The restored message includes commits outside the new range.' }
        Close-Message
        Open-Readiness
    } finally {
        & git -C $worktree update-ref $snapshotRef $snapshot $firstCommit
        if ($LASTEXITCODE -ne 0) { throw 'Could not restore the fixture snapshot.' }
    }
    Return-ToPush
    if ((Find 'Header').Current.Name -notlike 'Sending 2 of 4 commit*') { throw 'The boundary did not follow the restored snapshot.' }
    Complete-UiScenario

    Start-UiScenario 'A replaced boundary requires explicit selection after returning'
    Open-Readiness
    $oldTip = & git -C $worktree rev-parse HEAD
    $commits = @(& git -C $worktree rev-list --reverse "$snapshot..HEAD")
    $parent = $snapshot
    $index = 0
    foreach ($commit in $commits) {
        $index++
        $tree = & git -C $worktree rev-parse ($commit + '^{tree}')
        $parent = & git -C $worktree -c user.name=Fixture -c user.email=fixture@example.invalid commit-tree $tree -p $parent -m "Replayed change $index"
        if ($LASTEXITCODE -ne 0) { throw 'Could not construct the replacement fixture history.' }
    }
    # Preserve the complete working tree and index; only this fixture branch's commit identities change.
    & git -C $worktree update-ref refs/heads/feature $parent $oldTip
    if ($LASTEXITCODE -ne 0) { throw 'Could not replace the fixture branch history.' }
    Return-ToPush
    if ((Find 'Header').Current.Name -ne 'Select the commits to push') { throw 'A missing boundary silently selected a different range.' }
    if ((Find 'PushButton').Current.IsEnabled -or (Find 'PushButton').Current.HelpText -notlike '*no longer*') { throw 'A missing boundary does not block and explain Push.' }
    if (!(Find 'AllCommitsButton').Current.IsEnabled) { throw 'Send all is not available to recover from a missing boundary.' }
    Invoke-Control (Find 'RefreshPreviewButton')
    $null = Wait-For { (Find 'Header').Current.Name -eq 'Select the commits to push' }
    Open-Readiness
    Return-ToPush
    if ((Find 'PushButton').Current.IsEnabled) { throw 'Refresh or repeated navigation silently accepted a missing boundary.' }
    Select-Range 2
    $box = Open-Message
    if ((Read-Value $box).Replace("`r`n", "`n").Replace("`r", "`n").Trim() -ne "Replayed change 1`n`nReplayed change 2") { throw 'Explicit reselection did not regenerate the correct message.' }
    Close-Message
    Complete-UiScenario

    Start-UiScenario 'Send all survives returning from Readiness'
    Invoke-Control (Find 'AllCommitsButton')
    $null = Wait-For { (Find 'Header').Current.Name -like '4 commit*' }
    Open-Readiness
    Return-ToPush
    if ((Find 'Header').Current.Name -notlike '4 commit*' -or (Find 'AllCommitsButton').Current.IsEnabled) { throw 'Returning did not preserve Send all.' }
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
