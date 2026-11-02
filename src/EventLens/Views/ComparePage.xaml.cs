using System.Windows.Controls;
using EventLens.Services;

namespace EventLens.Views;

public partial class ComparePage : Page
{
    public ComparePage()
    {
        InitializeComponent();
        DataContext = Navigator.Compare;
        Loaded += (_, _) => Navigator.Compare.EnsureLoaded();
    }
}
