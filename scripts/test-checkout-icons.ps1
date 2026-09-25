# Checkout identity and the real file picker, exercised with UI Automation on a private desktop.
param([switch]$Worker, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/checkout-icons-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    $desktop = [UiTestDesktop]::new((Join-Path $PSHOME 'pwsh.exe'), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)), $PSScriptRoot, ('sg-icons-' + [Guid]::NewGuid().ToString('N')))
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(300)
        while (!$desktop.Wait(200)) { if ([DateTime]::UtcNow -ge $deadline) { throw 'Checkout icon test timed out.' } }
        if ($desktop.ExitCode -ne 0) { throw "Checkout icon UI failed. See $ArtifactDirectory/probe-error.txt" }
        Write-Output "PASS: Checkout icons. Artifacts: $ArtifactDirectory"
    } finally { $desktop.Dispose() }
    return
}
. "$PSScriptRoot/ui-test-report.ps1"
. "$PSScriptRoot/ui-automation.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
try {
Add-Type -AssemblyName Accessibility
# The common dialog's Invoke provider attempts foreground activation, which a private desktop
# cannot do. Its accessibility default action invokes the same native control without input.
Add-Type -CompilerOptions '/nowarn:1701' -ReferencedAssemblies @([Accessibility.IAccessible].Assembly.Location, 'System.Runtime.InteropServices.dll') -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Accessibility;
public static class DialogAccessibility {
    public static void Invoke(IntPtr window) {
        var iid = typeof(IAccessible).GUID;
        IAccessible accessible;
        Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(window, 0xfffffffc, ref iid, out accessible));
        accessible.accDoDefaultAction(0);
    }
    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
}
'@
} catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt'); exit 1 }
$env:SG_UI_TEST_DIRECTORY = Join-Path $ArtifactDirectory 'settings'
$env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $ArtifactDirectory 'webview'
$null = New-Item -ItemType Directory -Path $env:SG_UI_TEST_DIRECTORY
@{ BackupMinutes = 0; UpdateCheckMinutes = 0; RemoteCheckMinutes = 0; Notify = $false; Tray = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:SG_UI_TEST_DIRECTORY 'app.json')
function Find([string]$id, [switch]$Name, $within = $window) {
    $property = if ($Name) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $within.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new($property, $id))
}
function Wait-For([scriptblock]$read) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        if ($process) { $process.Refresh(); if ($process.HasExited) { throw "Test app exited: $($process.ExitCode)" } }
        $value = & $read; if ($value) { return $value }; Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Checkout icon assertion timed out.'
}
function Invoke-Control($control) { $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Enter-Value($control, [string]$value) { $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Read-Config { Get-Content -LiteralPath (Join-Path $root '.sg/sg.json') -Raw | ConvertFrom-Json }
function Select-Checkout([string]$name) {
    $nav = Wait-For { Find ('CheckoutNav_' + $name) }
    if ($nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected -and !(Find 'CoName')) {
        $other = if ($name -eq 'Fort') { 'Dashboard' } else { 'Fort' }
        (Find ('CheckoutNav_' + $other)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { (Find 'CoName').Current.Name -eq $other }
    }
    (Wait-For { Find ('CheckoutNav_' + $name) }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { (Find 'CoName').Current.Name -eq $name }
}
function Edit-Checkout {
    $edit = Find 'Edit checkout' -Name
    if (!$edit -or $edit.Current.IsOffscreen) { Invoke-Control (Find 'MoreButton' -within (Find 'Actions')) }
    Invoke-Control (Wait-For { Find 'Edit checkout' -Name })
    $null = Wait-For { Find 'ChooseIconButton' }
}
function Pick-Image([string]$path, [switch]$Cancel) {
    Invoke-Control (Find 'ChooseIconButton')
    $dialog = Wait-For {
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')))
    }
    function Dialog-Control([string]$id, $type) {
        $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
    }
    if ($Cancel) {
        $cancelButton = Wait-For { Dialog-Control '2' ([System.Windows.Automation.ControlType]::Button) }
        [DialogAccessibility]::Invoke([IntPtr]$cancelButton.Current.NativeWindowHandle)
        $null = Wait-For { (Find 'ChooseIconButton').Current.IsEnabled }
        return
    }
    $filename = Wait-For { Dialog-Control '1148' ([System.Windows.Automation.ControlType]::Edit) }
    Enter-Value $filename $path
    $openButton = Wait-For { Dialog-Control '1' ([System.Windows.Automation.ControlType]::Button) }
    [DialogAccessibility]::Invoke([IntPtr]$openButton.Current.NativeWindowHandle)
    # Closing the native modal can replace the XAML accessibility provider. Do not retain its old root.
    $script:window = Wait-For { Get-TestAppWindow $process }
}
$process = $null; $window = $null
$cli = (Resolve-Path "$PSScriptRoot/../src/sg/bin/Debug/net10.0/sg.exe").Path
$app = (Resolve-Path "$PSScriptRoot/../src/Sg.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/sg-ui.exe").Path
Initialize-UiReport $ArtifactDirectory 'Checkout icons'
try {
    $setup = Join-Path $ArtifactDirectory 'setup.txt'
    $repository = Join-Path $ArtifactDirectory 'svnrepo'
    & svnadmin create $repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create SVN fixture.' }
    $url = ([Uri]($repository + '/')).AbsoluteUri.TrimEnd('/')
    & svnmucc -m 'Create checkout icon fixture' mkdir "$url/trunk" 2>&1 | Out-File $setup
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed SVN fixture.' }
    $root = Join-Path $ArtifactDirectory 'root'
    & $cli init $root --no-fsmonitor 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture.' }
    foreach ($name in @('Fort', 'Dashboard')) {
        & $cli checkout add --url "$url/trunk" --root $root --name $name 2>&1 | Out-File $setup -Append
        if ($LASTEXITCODE -ne 0) { throw 'Could not register checkout.' }
    }
    # Transparent, rectangular source verifies decoding, scaling and aspect preservation.
    $source = Join-Path $ArtifactDirectory 'custom.png'
    $bitmap = [Drawing.Bitmap]::new(400, 200)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.FillEllipse([Drawing.Brushes]::DeepSkyBlue, 0, 0, 200, 200)
    $graphics.FillRectangle([Drawing.Brushes]::Gold, 240, 30, 140, 140)
    $bitmap.Save($source, [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }

    Start-UiScenario 'Folder initials stay visible in expanded and collapsed navigation'
    Select-Checkout 'Fort'
    $icon = Wait-For { Find 'CheckoutIcon_Fort' }
    if ($icon.Current.HelpText -ne 'Folder with FO initials') { throw 'Initials no longer accompany the folder.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'initials-expanded.png')
    Invoke-Control (Find 'TogglePaneButton')
    foreach ($name in @('Fort', 'Dashboard')) {
        $icon = Find ('CheckoutIcon_' + $name)
        if ($icon.Current.IsOffscreen -or $icon.Current.BoundingRectangle.Width -lt 24) { throw 'Collapsed icon is clipped.' }
    }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'initials-collapsed.png')
    Invoke-Control (Find 'TogglePaneButton')
    Complete-UiScenario

    Start-UiScenario 'Picker cancellation leaves initials unchanged'
    Edit-Checkout
    Pick-Image '' -Cancel
    if ((Read-Config).checkouts[0].icon) { throw 'Cancel changed the checkout icon.' }
    Complete-UiScenario

    Start-UiScenario 'Picking an image stores a small copy and updates navigation immediately'
    Pick-Image $source
    $saved = Wait-For { $c = Read-Config; if ($c.checkouts[0].icon) { $c.checkouts[0].icon } }
    $asset = Join-Path $root ('.sg/icons/' + $saved)
    if (!(Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath $asset)) { throw 'The source or the stored image is missing.' }
    $stored = [Drawing.Image]::FromFile($asset)
    try { if ($stored.Width -ne 128 -or $stored.Height -ne 64) { throw 'The normalized image lost its aspect ratio.' } } finally { $stored.Dispose() }
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Folder with a custom checkout image' }
    if ((Read-Config).checkouts[1].icon) { throw 'The image changed another checkout.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'custom-settings.png')
    Select-Checkout 'Fort'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'custom-expanded.png')
    Invoke-Control (Find 'TogglePaneButton')
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'custom-collapsed.png')
    Complete-UiScenario

    Start-UiScenario 'Custom images survive restarting the app and deleting the source'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    # Prepare a real remote backup from the icon chosen through the native picker.
    $backup = Join-Path $ArtifactDirectory 'backup.git'
    & git init --bare --quiet $backup 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create backup fixture.' }
    & $cli branch icons --from Fort --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create worktree for backup.' }
    & $cli backup set $backup --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure backup fixture.' }
    & $cli backup --worktree icons --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not back up checkout appearance.' }
    Remove-Item -LiteralPath $source
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Select-Checkout 'Fort'
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Folder with a custom checkout image' }
    Complete-UiScenario

    Start-UiScenario 'A missing managed image falls back to initials'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    Remove-Item -LiteralPath $asset
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Select-Checkout 'Fort'
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Folder with FO initials' }
    Complete-UiScenario

    Start-UiScenario 'Reset restores initials without applying an unsaved checkout rename'
    Edit-Checkout
    Enter-Value (Find 'NameBox') 'Dashboard'
    Invoke-Control (Find 'ResetIconButton')
    $null = Wait-For { !(Read-Config).checkouts[0].icon }
    if ((Read-Config).checkouts[0].name -ne 'Fort') { throw 'Reset applied an unsaved name.' }
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Folder with FO initials' }
    $null = Wait-For { !(Find 'ResetIconButton').Current.IsEnabled }
    if ((Find 'ResetIconButton').Current.HelpText -notlike '*already uses initials*') { throw 'Disabled reset is unexplained.' }
    if (!(Find 'DisabledHint_ResetIconButton').Current.IsKeyboardFocusable) { throw 'The disabled reason is not keyboard accessible.' }
    Complete-UiScenario

    Start-UiScenario 'Restoring a backup explains and hydrates checkout appearance without restarting'
    Select-Checkout 'Dashboard'
    Invoke-Control (Wait-For { Find 'BackupButton' })
    Invoke-Control (Wait-For { Find 'BackupWorktreeOpen_icons' })
    $null = Wait-For { Find 'IntoBox' }
    (Find 'IntoBox').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $destination = Wait-For { Find 'Dashboard' -Name -within (Find 'IntoBox') }
    $destination.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Enter-Value (Wait-For { Find 'NameBox' }) 'restored-icons'
    $null = Wait-For { (Find 'RestoreButton').Current.IsEnabled }
    Invoke-Control (Find 'RestoreButton')
    $null = Wait-For {
        @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)) |
            Where-Object { $_.Current.Name -like '*The saved checkout appearance will be restored.*' }).Count -gt 0
    }
    Invoke-Control (Wait-For { Find 'PrimaryButton' })
    $null = Wait-For { (Read-Config).checkouts[1].icon -eq $saved }
    $null = Wait-For { (Find 'CheckoutIcon_Dashboard').Current.HelpText -eq 'Folder with a custom checkout image' }
    if ((Read-Config).checkouts[0].icon -ne '') { throw 'Restoring changed the source checkout initials choice.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'restored-appearance.png')
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
