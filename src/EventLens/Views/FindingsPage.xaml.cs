using System.Windows.Controls;
using EventLens.Services;

namespace EventLens.Views;

public partial class FindingsPage : Page
{
    public FindingsPage()
    {
        InitializeComponent();
        DataContext = Navigator.Findings;
        Loaded += (_, _) => Navigator.Findings.EnsureLoaded();
    }
}
