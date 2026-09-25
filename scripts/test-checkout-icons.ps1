# Checkout identity and the real file picker, exercised with UI Automation on a private desktop.
param([switch]$Worker, [string]$ArtifactDirectory, [switch]$LayoutOnly)
$ErrorActionPreference = 'Stop'
if (!$Worker) {
    if (!('UiTestDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/UiTestDesktop.cs" }
    $ArtifactDirectory = (New-Item -ItemType Directory -Path "$PSScriptRoot/../TestResults/UI/checkout-icons-$([Guid]::NewGuid().ToString('N'))").FullName
    $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -Worker -ArtifactDirectory '" + $ArtifactDirectory.Replace("'", "''") + "'"
    if ($LayoutOnly) { $command += ' -LayoutOnly' }
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
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
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
function Assert-CheckoutIcon([string]$name, $within = $null) {
    $item = if ($within) { $within } else { Find ('CheckoutNav_' + $name) }
    $icon = Find ('CheckoutIcon_' + $name) -within $item
    $dpi = [DialogAccessibility]::GetDpiForWindow($process.MainWindowHandle)
    if ($dpi -eq 0) { throw 'Could not read the test window display scale.' }
    $minimum = 24 * $dpi / 96 - 1
    $maximum = 24 * $dpi / 96 + 1
    $bounds = $icon.Current.BoundingRectangle; $row = $item.Current.BoundingRectangle
    if ($icon.Current.IsOffscreen -or $bounds.Width -lt $minimum -or $bounds.Height -lt $minimum) {
        throw "Checkout identity was scaled down: $name ($bounds); expected at least $minimum px."
    }
    if ($bounds.Width -gt $maximum -or $bounds.Height -gt $maximum) { throw "Checkout identity exceeds the compact size: $name ($bounds)." }
    if ($bounds.Left -lt $row.Left -or $bounds.Right -gt $row.Right -or $bounds.Top -lt $row.Top -or $bounds.Bottom -gt $row.Bottom) {
        throw "Checkout identity is clipped by its row: $name ($bounds) in $row."
    }
}
function Select-Destination([string]$name) {
    (Find 'IntoBox').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item = Wait-For { Find $name -Name -within (Find 'IntoBox') }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { Find 'NameBox' }
}
function Assert-AppearancePreview([string]$currentHelp, [string]$note, [string]$savedHelp = 'Custom checkout image') {
    $null = Wait-For { (Find 'CurrentAppearanceIcon').Current.HelpText -eq $currentHelp }
    $null = Wait-For { (Find 'SavedAppearanceIcon').Current.HelpText -eq $savedHelp }
    $null = Wait-For { (Find 'AppearanceNote').Current.Name -eq $note }
    foreach ($id in @('CurrentAppearanceIcon', 'SavedAppearanceIcon')) {
        $icon = Find $id
        $bounds = $icon.Current.BoundingRectangle
        if ($icon.Current.IsOffscreen -or $bounds.Width -lt 40 -or $bounds.Height -lt 40) { throw "Preview icon is clipped: $id" }
        if ($bounds.Right -gt $window.Current.BoundingRectangle.Right -or $bounds.Bottom -gt $window.Current.BoundingRectangle.Bottom) { throw "Preview icon is outside the window: $id" }
    }
}
function Select-Checkout([string]$name) {
    $nav = Wait-For { Find ('CheckoutNav_' + $name) }
    if ($nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected -and !(Find 'CoName')) {
        $other = if ($name -eq 'Fort') { 'Dashboard' } else { 'Fort' }
        (Wait-For { Find ('CheckoutNav_' + $other) }).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
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
function Pick-File([string]$path, [switch]$Cancel, $Button = (Find 'ChooseIconButton')) {
    Invoke-Control $Button
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

function New-MultiSizeIcon([string]$path) {
    # Distinct colors prove the chosen frame, rather than only testing that some image decoded.
    $frames = @(@{ size = 16; color = [Drawing.Brushes]::Red },
                @{ size = 64; color = [Drawing.Brushes]::Lime },
                @{ size = 256; color = [Drawing.Brushes]::Blue })
    foreach ($frame in $frames) {
        $bitmap = [Drawing.Bitmap]::new($frame.size, $frame.size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.FillEllipse($frame.color, 0, 0, $frame.size, $frame.size)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frame.bytes = $stream.ToArray()
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($path))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $size = if ($frame.size -eq 256) { 0 } else { $frame.size }
            $writer.Write([byte]$size); $writer.Write([byte]$size)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.bytes) }
    } finally { $writer.Dispose() }
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
    foreach ($name in @('Fort', 'Dashboard', 'Tools')) {
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
    foreach ($name in @('Fort', 'Dashboard')) { Assert-CheckoutIcon $name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'initials-expanded.png')
    Invoke-Control (Find 'TogglePaneButton')
    foreach ($name in @('Fort', 'Dashboard')) { Assert-CheckoutIcon $name }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'initials-collapsed.png')
    Invoke-Control (Find 'TogglePaneButton')
    Complete-UiScenario

    Start-UiScenario 'Picker cancellation leaves initials unchanged'
    Edit-Checkout
    Pick-File '' -Cancel
    if ((Read-Config).checkouts[0].icon) { throw 'Cancel changed the checkout icon.' }
    Complete-UiScenario

    Start-UiScenario 'Picking an image stores a small copy and updates navigation immediately'
    Pick-File $source
    $saved = Wait-For { $c = Read-Config; if ($c.checkouts[0].icon) { $c.checkouts[0].icon } }
    $asset = Join-Path $root ('.sg/icons/' + $saved)
    if (!(Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath $asset)) { throw 'The source or the stored image is missing.' }
    $stored = [Drawing.Image]::FromFile($asset)
    try { if ($stored.Width -ne 128 -or $stored.Height -ne 64) { throw 'The normalized image lost its aspect ratio.' } } finally { $stored.Dispose() }
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Custom checkout image' }
    if ((Read-Config).checkouts[1].icon) { throw 'The image changed another checkout.' }
    Assert-CheckoutIcon 'Fort' -within (Find 'IconCard')
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'custom-settings.png')
    Select-Checkout 'Fort'
    Assert-CheckoutIcon 'Fort'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'custom-expanded.png')
    Invoke-Control (Find 'TogglePaneButton')
    Assert-CheckoutIcon 'Fort'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'custom-collapsed.png')
    Complete-UiScenario

    if ($LayoutOnly) {
        Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
        return
    }

    Start-UiScenario 'Legacy ICO images decode through the native picker'
    Select-Checkout 'Fort'
    Edit-Checkout
    $legacyIcon = Join-Path $ArtifactDirectory 'legacy.ico'
    $legacyStream = [IO.File]::Create($legacyIcon)
    try { [Drawing.SystemIcons]::Warning.Save($legacyStream) } finally { $legacyStream.Dispose() }
    Pick-File $legacyIcon
    $previous = $saved
    $saved = Wait-For { $name = (Read-Config).checkouts[0].icon; if ($name -and $name -ne $previous) { $name } }
    $asset = Join-Path $root ('.sg/icons/' + $saved)
    $stored = [Drawing.Image]::FromFile($asset)
    try { if ($stored.Width -gt 128 -or $stored.Height -gt 128) { throw 'The legacy icon was not bounded.' } } finally { $stored.Dispose() }
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Custom checkout image' }
    Complete-UiScenario

    Start-UiScenario 'ICO uses the largest image and preserves transparency in its portable copy'
    $source = Join-Path $ArtifactDirectory 'multiple-sizes.ico'
    New-MultiSizeIcon $source
    Pick-File $source
    $previous = $saved
    $saved = Wait-For { $name = (Read-Config).checkouts[0].icon; if ($name -and $name -ne $previous) { $name } }
    $asset = Join-Path $root ('.sg/icons/' + $saved)
    $stored = [Drawing.Bitmap]::new($asset)
    try {
        if ($stored.Width -ne 128 -or $stored.Height -ne 128) { throw 'The largest ICO image was not resized to 128 pixels.' }
        $center = $stored.GetPixel(64, 64)
        if ($center.B -lt 240 -or $center.R -gt 10 -or $center.G -gt 10) { throw 'The ICO decoder chose a smaller image.' }
        if ($stored.GetPixel(0, 0).A -ne 0) { throw 'ICO transparency was lost.' }
    } finally { $stored.Dispose() }
    if (!(Test-Path -LiteralPath $source)) { throw 'The original ICO was removed.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'ico-settings.png')
    Complete-UiScenario

    Start-UiScenario 'A malformed ICO leaves the existing checkout image intact'
    $invalid = Join-Path $ArtifactDirectory 'invalid.ico'
    [IO.File]::WriteAllBytes($invalid, [byte[]]@(0, 0, 1, 0, 1, 0))
    Pick-File $invalid
    $null = Wait-For { Find 'Could not use this image' -Name }
    Invoke-Control (Wait-For { Find 'CloseButton' })
    if ((Read-Config).checkouts[0].icon -ne $saved -or !(Test-Path -LiteralPath $asset)) { throw 'An invalid ICO changed the saved image.' }
    Select-Checkout 'Fort'
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Custom checkout image' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'ico-navigation.png')
    Complete-UiScenario

    Start-UiScenario 'Custom images survive restarting the app and deleting the source'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    # Prepare a real remote backup from the icon chosen through the native picker.
    $backup = Join-Path $ArtifactDirectory 'backup.git'
    & git init --bare --quiet $backup 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create backup fixture.' }
    & $cli branch icons --from Fort --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not create worktree for backup.' }
    $worktree = Join-Path $root 'icons'
    Set-Content -LiteralPath (Join-Path $worktree 'exported.txt') -Value 'A commit carried with the checkout icon.'
    & git -C $worktree add exported.txt 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage export fixture.' }
    & git -C $worktree -c user.name=UiTest -c user.email=ui@example.com commit -m 'Export an icon' 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit export fixture.' }
    $export = Join-Path $ArtifactDirectory 'icons.sgexport'
    & $cli export $worktree --out $export --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not export checkout appearance.' }
    & $cli backup set $backup --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure backup fixture.' }
    & $cli backup --worktree icons --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not back up checkout appearance.' }
    Remove-Item -LiteralPath $source
    $process = Start-Process -FilePath $app -ArgumentList @('overview', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
    $window = Wait-For { Get-TestAppWindow $process }
    Select-Checkout 'Fort'
    $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Custom checkout image' }
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
    Select-Destination 'Dashboard'
    Enter-Value (Wait-For { Find 'NameBox' }) 'restored-icons'
    $null = Wait-For { (Find 'RestoreButton').Current.IsEnabled }
    Assert-AppearancePreview 'Folder with DA initials' 'The saved checkout appearance will be restored.'
    if ((Read-Config).checkouts[1].icon) { throw 'Opening a backup preview changed checkout appearance.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'backup-appearance-preview.png')
    Invoke-Control (Find 'RestoreButton')
    $null = Wait-For {
        @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)) |
            Where-Object { $_.Current.Name -like '*The saved checkout appearance will be restored.*' }).Count -gt 0
    }
    Invoke-Control (Wait-For { Find 'PrimaryButton' })
    $null = Wait-For { (Read-Config).checkouts[1].icon -eq $saved }
    $null = Wait-For { (Find 'CheckoutIcon_Dashboard').Current.HelpText -eq 'Custom checkout image' }
    Assert-AppearancePreview 'Custom checkout image' 'Checkout appearance restored.'
    if ((Read-Config).checkouts[0].icon -ne '') { throw 'Restoring changed the source checkout initials choice.' }
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'restored-appearance.png')
    Complete-UiScenario

    foreach ($target in @('Tools', 'Fort')) {
        $apply = $target -eq 'Tools'
        $expected = if ($apply) { 'The saved checkout appearance will be restored.' } else { 'Your existing checkout appearance will be kept.' }
        Start-UiScenario ("Export import previews and " + $(if ($apply) { 'restores an icon immediately' } else { 'preserves local initials' }))
        Select-Checkout $target
        foreach ($label in @('Import', 'Merge', 'Server branch', 'Edit checkout')) {
            $advanced = Find $label -Name
            if ($advanced -and !$advanced.Current.IsOffscreen) { throw "Advanced checkout action is visible by default: $label" }
        }
        $importButton = Find 'Import' -Name
        if (!$importButton -or $importButton.Current.IsOffscreen) { Invoke-Control (Find 'MoreButton' -within (Find 'Actions')) }
        Pick-File $export -Button (Wait-For { Find 'Import' -Name })
        $null = Wait-For { Find 'IntoBox' }
        Select-Destination 'Dashboard'
        Assert-AppearancePreview 'Custom checkout image' 'Your existing checkout appearance will be kept.'
        Select-Destination $target
        $branch = 'imported-' + $target.ToLowerInvariant()
        Enter-Value (Wait-For { Find 'NameBox' }) $branch
        $null = Wait-For { (Find 'AppearanceNote').Current.Name -eq $expected }
        $initials = if ($apply) { 'TO' } else { 'FO' }
        Assert-AppearancePreview "Folder with $initials initials" $expected
        $null = Wait-For { (Find 'ImportButton').Current.IsEnabled }
        (Find 'ActivityItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $null = Wait-For { !(Find 'NameBox') }
        Invoke-Control (Find 'NavigationViewBackButton')
        $null = Wait-For { (Find 'ImportButton').Current.IsEnabled }
        if ((Find 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $branch) { throw 'Import branch draft was lost after navigating back.' }
        $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like "$target*" }
        Assert-AppearancePreview "Folder with $initials initials" $expected
        Save-UiWindow $window (Join-Path $ArtifactDirectory ("import-preview-$target.png"))
        Invoke-Control (Find 'ImportButton')
        $null = Wait-For {
            @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)) |
                Where-Object { $_.Current.Name.Contains($expected) }).Count -gt 0
        }
        Invoke-Control (Wait-For { Find 'PrimaryButton' })
        $null = Wait-For { (Find 'Summary').Current.Name -like 'Done.*' }
        if (!(Test-Path -LiteralPath (Join-Path $root "$branch/exported.txt"))) { throw 'Import did not replay its commit.' }
        $choice = ((Read-Config).checkouts | Where-Object name -eq $target).icon
        if ($apply) {
            if ($choice -ne $saved) { throw 'Import did not restore the saved image.' }
            $null = Wait-For { (Find ('CheckoutIcon_' + $target)).Current.HelpText -eq 'Custom checkout image' }
            Assert-AppearancePreview 'Custom checkout image' 'Checkout appearance restored.'
        } else {
            if ($choice -ne '') { throw 'Import replaced the local initials choice.' }
            $null = Wait-For { (Find 'CheckoutIcon_Fort').Current.HelpText -eq 'Folder with FO initials' }
            Assert-AppearancePreview 'Folder with FO initials' 'Your existing checkout appearance was kept.'
        }
        Save-UiWindow $window (Join-Path $ArtifactDirectory ("import-result-$target.png"))
        Complete-UiScenario
    }
    Start-UiScenario 'Saved initials follow the destination name without replacing its image'
    $initialsExport = Join-Path $ArtifactDirectory 'initials.sgexport'
    & $cli export $worktree --out $initialsExport --root $root 2>&1 | Out-File $setup -Append
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the explicit initials choice.' }
    Pick-File $initialsExport -Button (Find 'PickButton')
    $null = Wait-For { Find 'IntoBox' }
    Select-Destination 'Tools'
    Assert-AppearancePreview 'Custom checkout image' 'Your existing checkout appearance will be kept.' 'Folder with TO initials'
    Select-Destination 'Dashboard'
    Assert-AppearancePreview 'Custom checkout image' 'Your existing checkout appearance will be kept.' 'Folder with DA initials'
    if ((Read-Config).checkouts[1].icon -ne $saved) { throw 'Previewing initials changed the destination image.' }
    Enter-Value (Find 'NameBox') ''
    (Find 'ActivityItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null = Wait-For { !(Find 'NameBox') }
    Invoke-Control (Find 'NavigationViewBackButton')
    $null = Wait-For { (Find 'ComparisonSummary').Current.Name -like 'Dashboard*' }
    if ((Find 'FilePath').Current.Name -ne $initialsExport) { throw 'Navigation restored the original export instead of the newly chosen file.' }
    if ((Find 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '') { throw 'An empty import draft was overwritten.' }
    if ((Find 'ImportButton').Current.IsEnabled) { throw 'An empty import branch permits submission.' }
    Assert-AppearancePreview 'Custom checkout image' 'Your existing checkout appearance will be kept.' 'Folder with DA initials'
    Save-UiWindow $window (Join-Path $ArtifactDirectory 'initials-preview.png')
    Complete-UiScenario
    Write-UiResult $ArtifactDirectory @{ status = 'passed'; scenarios = @(Read-UiScenarios $ArtifactDirectory) }
} catch {
    Fail-UiScenario $_ $window
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'probe-error.txt')
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
