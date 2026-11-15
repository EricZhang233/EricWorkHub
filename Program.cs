using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EricWorkHub;

internal static class Program
{
    internal static SingleInstance Instance { get; private set; } = null!;

    [STAThread]
    private static int Main()
    {
        try
        {
            using var instance = new SingleInstance();
            Instance = instance;
            if (!instance.IsPrimary) return instance.NotifyAsync(WindowActivator.AllowForeground).GetAwaiter().GetResult() ? 0 : 1;
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return 0;
        }
        catch (Exception ex) { LogService.Write("App", "Application startup failed", ex, level: LogService.LogLevel.Error); return 1; }
        finally { LogService.FlushAndStop(); }
    }
}
