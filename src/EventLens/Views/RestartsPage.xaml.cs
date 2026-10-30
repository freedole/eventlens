using System.Windows.Controls;
using EventLens.Services;

namespace EventLens.Views;

public partial class RestartsPage : Page
{
    public RestartsPage()
    {
        InitializeComponent();
        DataContext = Navigator.Restarts;
        Loaded += (_, _) => Navigator.Restarts.EnsureLoaded();
    }
}
