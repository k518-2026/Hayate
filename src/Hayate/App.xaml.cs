using System;
using System.Windows;
using System.Windows.Threading;

namespace Hayate;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "予期しないエラーが発生しました。\n\n" + e.Exception.Message,
            "Hayate", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
