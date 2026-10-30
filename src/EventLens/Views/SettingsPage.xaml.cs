using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using EventLens.Services;

namespace EventLens.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = Navigator.Settings;
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        Navigator.Settings.SaveKey(KeyInput.Password);
        KeyInput.Clear();
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
