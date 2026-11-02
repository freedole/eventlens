using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            RestoreColumnWidths();
            vm.EnsureLoaded();
        };
        Unloaded += (_, _) =>
        {
            vm.SelectionRequested -= OnSelect;
            SaveColumnWidths();
        };

        PreviewKeyDown += OnKeyDown;
        EventGrid.PreviewMouseRightButtonDown += (_, e) =>
        {
            // select the row under the mouse so the context menu acts on it
            var dep = e.OriginalSource as DependencyObject;
            while (dep is not null and not DataGridRow) dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            if (dep is DataGridRow row) row.IsSelected = true;
        };
    }

    // ---------- keyboard ----------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Navigator.Events;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.ClearFilter();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C && Keyboard.FocusedElement is not TextBox && vm.Detail is { } d)
        {
            d.CopyCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---------- column widths ----------

    private void RestoreColumnWidths()
    {
        var widths = AppSettings.Current.EventColumnWidths;
        if (widths.Count != EventGrid.Columns.Count) return;
        for (int i = 0; i < widths.Count; i++)
        {
            var col = EventGrid.Columns[i];
            if (col.Width.IsStar || !col.CanUserResize || widths[i] < 20) continue;
            col.Width = new DataGridLength(widths[i]);
        }
    }

    private void SaveColumnWidths()
    {
        AppSettings.Current.EventColumnWidths = EventGrid.Columns.Select(c => c.ActualWidth).ToList();
        AppSettings.Current.Save();
    }

    // ---------- views / export ----------

    private void OnViewSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewsCombo.SelectedItem is SavedView v)
        {
            Navigator.Events.ApplyViewCommand.Execute(v);
            ViewsCombo.SelectedItem = null;
        }
    }

    private void OnSaveViewClick(object sender, RoutedEventArgs e)
    {
        ViewNameBox.Text = Navigator.Events.FilterLabel ?? "";
        SaveViewPopup.IsOpen = true;
        Dispatcher.InvokeAsync(() => ViewNameBox.Focus());
    }

    private void OnSaveViewConfirm(object sender, RoutedEventArgs e)
    {
        Navigator.Events.SaveViewCommand.Execute(ViewNameBox.Text);
        SaveViewPopup.IsOpen = false;
    }

    private void OnViewNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnSaveViewConfirm(sender, e);
        e.Handled = true;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is not { } menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = ExportButton;
        menu.IsOpen = true;
    }
}
