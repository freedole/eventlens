using System.Windows;
using Wpf.Ui.Appearance;

namespace EventLens;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "EventLens", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
