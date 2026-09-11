using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Sg.App;

/// <summary>One sg-ui process at a time. A second launch hands its command line to the running one and exits.</summary>
public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (!TryBecomeMain()) return 0;
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    static bool TryBecomeMain()
    {
        try
        {
            var main = AppInstance.FindOrRegisterForKey("sg-ui-main");
            if (main.IsCurrent) return true;
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
