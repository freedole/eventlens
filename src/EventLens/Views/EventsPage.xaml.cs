using System.Windows.Controls;
using EventLens.ViewModels;

namespace EventLens.Views;

public partial class EventsPage : Page
{
    private static readonly EventsViewModel Vm = new();
    private static bool _loaded;

    public EventsPage()
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
