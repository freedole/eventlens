using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using EventLens.ViewModels;

namespace EventLens.Views;

public partial class SettingsPage : Page
{
    private static readonly SettingsViewModel Vm = new();

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = Vm;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
