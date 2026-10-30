using EventLens.Services;
using EventLens.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EventLens;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Navigator.View = RootNavigation;

        if (AppSettings.Current.Theme == 0) SystemThemeWatcher.Watch(this);

        Loaded += (_, _) =>
        {
            Navigator.Settings.ApplySavedTheme();
            RootNavigation.Navigate(typeof(DashboardPage));
        };
    }
}
