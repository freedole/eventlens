using EventLens.Models;
using EventLens.ViewModels;
using EventLens.Views;
using Wpf.Ui.Controls;

namespace EventLens.Services;

/// <summary>Shared view models + cross-page navigation (e.g. Overview card -> filtered Events).</summary>
public static class Navigator
{
    public static NavigationView? View { get; set; }

    public static DashboardViewModel Dashboard { get; } = new();
    public static FindingsViewModel Findings { get; } = new();
    public static EventsViewModel Events { get; } = new();
    public static TimelineViewModel Timeline { get; } = new();
    public static CompareViewModel Compare { get; } = new();
    public static RestartsViewModel Restarts { get; } = new();
    public static ComputersViewModel Computers { get; } = new();
    public static SettingsViewModel Settings { get; } = new();

    public static void Navigate(Type page) => View?.Navigate(page);

    public static void OpenEvents(EventQuerySpec spec)
    {
        _ = Events.ApplyAsync(spec);
        Navigate(typeof(EventsPage));
    }

    public static void OpenTimeline(DateTime at, int minutes)
    {
        _ = Timeline.ShowAsync(at, minutes);
        Navigate(typeof(TimelinePage));
    }
}
