using System.Windows.Controls;
using EventLens.Models;
using EventLens.Services;

namespace EventLens.Views;

public partial class EventsPage : Page
{
    public EventsPage()
    {
        InitializeComponent();
        var vm = Navigator.Events;
        DataContext = vm;

        void OnSelect(object? s, EventRow row) => Dispatcher.InvokeAsync(() => EventGrid.ScrollIntoView(row));
        Loaded += (_, _) =>
        {
            vm.SelectionRequested += OnSelect;
            vm.EnsureLoaded();
        };
        Unloaded += (_, _) => vm.SelectionRequested -= OnSelect;
    }
}
