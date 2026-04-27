using System.Windows;
using System.Windows.Threading;
using ModernWpf;

namespace WpfApp;

public partial class App : Application
{
    public App()
    {
        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Exception("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
        AppLogger.Exception("Unhandled AppDomain exception.", exception);
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Exception("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
