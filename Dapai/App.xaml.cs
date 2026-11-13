using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Dapai;

public partial class App : Application
{
    internal const string Title = "打π优化";
    internal const string IconUri = "pack://application:,,,/打π优化;component/Apex_logo.ico";

    private Mutex? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            activate = new EventWaitHandle(false, EventResetMode.AutoReset,
                "Dapai_Wpf_Activate");
            instance = new Mutex(false, "Dapai_Wpf_SingleInstance", out bool first);
            if (!first)
            {
                WindowsSession.AllowExistingWindowActivation(Title);
                activate.Set();
                Shutdown();
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            activationWait = ThreadPool.RegisterWaitForSingleObject(activate,
                (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, window.ShowFromTray),
                null, Timeout.Infinite, executeOnlyOnce: false);
            window.Show();
            AppLog.Write("应用已启动。");
        }
        catch (Exception ex)
        {
            AppLog.Write("应用启动失败。", ex);
            MessageBox.Show($"应用启动失败。\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null);
        activate?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
