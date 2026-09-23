# Native UI Automation only, isolated on a private desktop and a disposable worktree.
param([Parameter(Mandatory)][string]$FixtureRoot, [switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/code-review-$([Guid]::NewGuid().ToString('N'))").FullName
    $FixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -FixtureRoot '" + $FixtureRoot.Replace("'", "''") + "' -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-code-review-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Code review UI test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Code review UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Code review UI. Artifacts: $ArtifactDirectory"
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
$process = $null; $window = $null; $made = $false
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
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Start-UiScenario 'Leave code feedback without modifying source files'
    $comment = Wait-For { $c = Find 'CodeReviewComment'; if ($c -and $c.Current.IsEnabled) { $c } }
    Invoke-Control $comment
    Enter-Value (Wait-For { Find 'ReviewCommentBody' }) 'Please explain the initial count.'
    Invoke-Control (Wait-For { Find 'Save comment' -Name })
    $null = Wait-For { Find 'Please explain the initial count.' -Name }
    if ([IO.File]::ReadAllText((Join-Path $path 'review-example.cs')) -ne "int count = 1;`n") { throw 'Commenting changed the source.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'open-comment.png')
    Complete-UiScenario
    Start-UiScenario 'Resolve, reveal history, and reopen feedback'
    Invoke-Control (Wait-For { Find 'Resolve' -Name })
    Enter-Value (Wait-For { Find 'ReviewAddressBody' }) 'One is the documented initial value; reviewed the current code.'
    Invoke-Control (Wait-For { Find 'PrimaryButton' })
    $null = Wait-For { Find 'No open comments. Choose All comments to see resolved feedback.' -Name }
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
    Start-UiScenario 'Comments survive closing and reopening the app'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    $process = Start-Process -FilePath $app -ArgumentList @('code-review', ('"' + $path + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    $null = Wait-For { Find 'Please add this rationale in code.' -Name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'after-restart.png')
    Complete-UiScenario
    & $cli review export --root $FixtureRoot --worktree $path --out (Join-Path $ArtifactDirectory 'review.json') | Out-Null
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($made) { & $cli rm $branch --root $FixtureRoot --force | Out-Null }
}
