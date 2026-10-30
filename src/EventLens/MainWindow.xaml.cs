using EventLens.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EventLens;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(DashboardPage));
    }
}
