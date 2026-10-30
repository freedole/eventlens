using System.Windows.Controls;
using EventLens.Services;

namespace EventLens.Views;

public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        DataContext = Navigator.Dashboard;
        Loaded += (_, _) => Navigator.Dashboard.EnsureLoaded();
    }
}
