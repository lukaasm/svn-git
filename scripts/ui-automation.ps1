# Match the app's actual XAML window, not a transient startup/WebView window returned by MainWindowHandle.
function Get-TestAppWindow($process) {
    $process.Refresh()
    if ($process.HasExited) { throw "Test app exited during startup (exit $($process.ExitCode))." }
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id))
    foreach ($window in $windows) {
        $title = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AppTitleBar'))
        if ($title) { return $window }
    }
}
