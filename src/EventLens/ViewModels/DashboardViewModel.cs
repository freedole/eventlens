using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;

namespace EventLens.ViewModels;

public sealed class StatCard : ObservableObject
{
    private string _value = "…";
    private string _hint = "";

    public required string Title { get; init; }
    public required string Icon { get; init; }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public string Hint { get => _hint; set => SetProperty(ref _hint, value); }
}

public sealed class DashboardViewModel : ObservableObject
{
    private bool _isLoading;
    private string _status = "";

    public StatCard Uptime { get; } = new() { Title = "Uptime", Icon = "Clock24" };
    public StatCard UnexpectedShutdowns { get; } = new() { Title = "Unexpected shutdowns (30 days)", Icon = "Power24" };
    public StatCard BlueScreens { get; } = new() { Title = "Blue screens (30 days)", Icon = "ErrorCircle24" };
    public StatCard AppCrashes { get; } = new() { Title = "App crashes (7 days)", Icon = "Apps24" };
    public StatCard Errors { get; } = new() { Title = "Errors (24 h)", Icon = "DismissCircle24" };
    public StatCard Warnings { get; } = new() { Title = "Warnings (24 h)", Icon = "Warning24" };
    public StatCard FailedLogons { get; } = new() { Title = "Failed logons (24 h)", Icon = "LockClosed24" };

    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<EventItem> Timeline { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public IAsyncRelayCommand RefreshCommand { get; }

    public DashboardViewModel()
    {
        foreach (var c in new[] { Uptime, UnexpectedShutdowns, BlueScreens, AppCrashes, Errors, Warnings, FailedLogons })
            Cards.Add(c);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Status = "Reading event logs…";
        try
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            Uptime.Value = up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min";
            Uptime.Hint = "Booted " + (DateTime.Now - up).ToString("g");

            var d30 = TimeSpan.FromDays(30);
            var d7 = TimeSpan.FromDays(7);
            var h24 = TimeSpan.FromHours(24);

            var tShut = EventLogService.CountAsync("System", EventLogService.BuildXPath(null, d30, [41, 6008]));
            var tBsod = EventLogService.CountAsync("System",
                "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and " +
                $"TimeCreated[timediff(@SystemTime) <= {(long)d30.TotalMilliseconds}]]]");
            var tCrash = EventLogService.CountAsync("Application", EventLogService.BuildXPath(null, d7, [1000, 1002, 1026]));
            var tErrSys = EventLogService.CountAsync("System", EventLogService.BuildXPath(Severity.Error, h24));
            var tErrApp = EventLogService.CountAsync("Application", EventLogService.BuildXPath(Severity.Error, h24));
            var tWarnSys = EventLogService.CountAsync("System", "*[System[Level=3 and " + $"TimeCreated[timediff(@SystemTime) <= {(long)h24.TotalMilliseconds}]]]");
            var tWarnApp = EventLogService.CountAsync("Application", "*[System[Level=3 and " + $"TimeCreated[timediff(@SystemTime) <= {(long)h24.TotalMilliseconds}]]]");
            var tLogon = EventLogService.CountAsync("Security", EventLogService.BuildXPath(null, h24, [4625]));

            await Task.WhenAll(tShut, tBsod, tCrash, tErrSys, tErrApp, tWarnSys, tWarnApp, tLogon);

            Set(UnexpectedShutdowns, tShut.Result, "Kernel-Power 41 / EventLog 6008");
            Set(BlueScreens, tBsod.Result, "BugCheck 1001");
            Set(AppCrashes, tCrash.Result, "Application Error / Hang / .NET");
            Set(Errors, tErrSys.Result + tErrApp.Result, $"System {tErrSys.Result} · Application {tErrApp.Result}");
            Set(Warnings, tWarnSys.Result + tWarnApp.Result, $"System {tWarnSys.Result} · Application {tWarnApp.Result}");
            Set(FailedLogons, tLogon.Result, "Security 4625");

            await LoadTimelineAsync(d30);
            Status = $"Updated {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void Set(StatCard card, int count, string hint)
    {
        if (count < 0)
        {
            card.Value = "-";
            card.Hint = "Run as administrator to read this log";
        }
        else
        {
            card.Value = count.ToString("N0");
            card.Hint = hint;
        }
    }

    private async Task LoadTimelineAsync(TimeSpan window)
    {
        var sys = SafeQuery("System", EventLogService.BuildXPath(null, window, [41, 6008, 1074, 1001, 7031, 7034, 7]), 40);
        var app = SafeQuery("Application", EventLogService.BuildXPath(null, window, [1000, 1002, 1026]), 40);
        await Task.WhenAll(sys, app);

        Timeline.Clear();
        foreach (var e in sys.Result.Concat(app.Result).OrderByDescending(e => e.TimeCreated).Take(25))
            Timeline.Add(e);
    }

    private static async Task<List<EventItem>> SafeQuery(string log, string xpath, int max)
    {
        try { return await EventLogService.QueryAsync(log, xpath, max); }
        catch { return []; }
    }
}
