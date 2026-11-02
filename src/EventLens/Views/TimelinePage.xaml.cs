using System.Windows.Controls;
using System.Windows.Input;
using EventLens.Services;

namespace EventLens.Views;

public partial class TimelinePage : Page
{
    public TimelinePage()
    {
        InitializeComponent();
        DataContext = Navigator.Timeline;
    }

    private void OnTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Navigator.Timeline.ShowCommand.Execute(null);
        e.Handled = true;
    }
}
