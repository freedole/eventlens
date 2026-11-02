using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using EventLens.Services;
using EventLens.ViewModels;
using EventLens.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EventLens;

public partial class MainWindow : FluentWindow
{
    private UpdateInfo? _update;

    public MainWindow()
    {
        InitializeComponent();
        Navigator.View = RootNavigation;
        RestorePlacement();

        if (AppSettings.Current.Theme == 0) SystemThemeWatcher.Watch(this);

        SettingsViewModel.UpdateAvailable += (_, u) => ShowUpdate(u);

        Loaded += async (_, _) =>
        {
            Navigator.Settings.ApplySavedTheme();
            RootNavigation.Navigate(typeof(DashboardPage));

            // background: knowledge base + app update (both optional, once a day)
            var kb = await OnlineServices.SyncKnowledgeAsync();
            if (kb.StartsWith("Knowledge base updated")) Navigator.Settings.KbStatus = kb;
            var u = await OnlineServices.CheckForUpdateAsync();
            if (u is not null && u.Version != AppSettings.Current.SkippedVersion) ShowUpdate(u);
        };

        Closing += OnClosing;
    }

    // ---------- updates ----------

    private void ShowUpdate(UpdateInfo u)
    {
        _update = u;
        UpdateBar.Message = $"EventLens {u.Version}{(u.Prerelease ? " (pre-release)" : "")} is out - you have {OnlineServices.AppVersion}.";
        UpdateBar.IsOpen = true;
    }

    private void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        try { Process.Start(new ProcessStartInfo(_update.Url) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    private void OnSkipUpdate(object sender, RoutedEventArgs e)
    {
        if (_update is not null)
        {
            AppSettings.Current.SkippedVersion = _update.Version;
            AppSettings.Current.Save();
        }
        UpdateBar.IsOpen = false;
    }

    // ---------- window placement ----------

    private void RestorePlacement()
    {
        var s = AppSettings.Current;
        if (s.WindowWidth < 600 || s.WindowHeight < 400 || s.WindowLeft is not { } left || s.WindowTop is not { } top) return;

        // only restore if the saved rectangle is still on a visible screen
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(left, top, s.WindowWidth, s.WindowHeight);
        if (!vs.IntersectsWith(r) || r.Left < vs.Left - 50 || r.Top < vs.Top - 10) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = Math.Min(s.WindowWidth, vs.Width);
        Height = Math.Min(s.WindowHeight, vs.Height);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = AppSettings.Current;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty && b.Width > 0)
        {
            s.WindowLeft = b.Left;
            s.WindowTop = b.Top;
            s.WindowWidth = b.Width;
            s.WindowHeight = b.Height;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.Save();
    }
}
