using System.Windows;
using System.Windows.Controls;
using EventLens.Services;

namespace EventLens.Views;

public partial class ComputersPage : Page
{
    public ComputersPage()
    {
        InitializeComponent();
        DataContext = Navigator.Computers;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        await Navigator.Computers.ConnectAsync(password);
        PasswordInput.Clear();
    }
}
