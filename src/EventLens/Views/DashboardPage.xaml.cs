using System.Windows.Controls;
using EventLens.ViewModels;

namespace EventLens.Views;

public partial class DashboardPage : Page
{
    private static readonly DashboardViewModel Vm = new();
    private static bool _loaded;

    public DashboardPage()
    {
        InitializeComponent();
        DataContext = Vm;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await Vm.RefreshAsync();
        };
    }
}
