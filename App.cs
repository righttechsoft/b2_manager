using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace B2Manager;

public class App : Application
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Styles.xaml", UriKind.Relative) });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        app.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        var login = new LoginWindow();
        bool? loginResult = login.ShowDialog();

        if (loginResult == true && login.AuthorizedClient != null)
        {
            var main = new MainWindow(login.AuthorizedClient);
            app.MainWindow = main;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            app.Run();
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            MessageBox.Show(ex.Message, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }
}
