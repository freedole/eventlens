using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;

namespace EventLens.ViewModels;

public enum CardState { Neutral, Good, Warn, Bad }

public sealed class StatCard : ObservableObject
{
    private string _value = "…";
    private string _hint = "";
    private CardState _state = CardState.Neutral;
    private IReadOnlyList<int>? _spark;

    public required string Title { get; init; }
    public required string Icon { get; init; }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public string Hint { get => _hint; set => SetProperty(ref _hint, value); }
    public CardState State { get => _state; set => SetProperty(ref _state, value); }
    public IReadOnlyList<int>? Spark { get => _spark; set => SetProperty(ref _spark, value); }
}

public sealed class HeatDay
{
    public required DateTime Date { get; init; }
    public required int Count { get; init; }
    public required int Level { get; init; }
    public string Tip => $"{Date:ddd, d MMM}: {Count:N0} errors";
    public string DayLabel => Date.Day.ToString();
}

public sealed class DashboardViewModel : ObservableObject
{
    private const int Days = 30;

    private bool _isLoading;
    private string _status = "";
    private int _score;
    private string _verdict = "Analyzing…";
    private string _verdictDetail = "Reading event logs";
    private string _uptime = "";
    private string _lastBoot = "";

    public StatCard UnexpectedShutdowns { get; } = new() { Title = "Unexpected shutdowns", Icon = "Power24" };
    public StatCard BlueScreens { get; } = new() { Title = "Blue screens", Icon = "ErrorCircle24" };
    public StatCard AppCrashes { get; } = new() { Title = "App crashes", Icon = "Apps24" };
    public StatCard DiskErrors { get; } = new() { Title = "Disk errors", Icon = "Storage24" };
    public StatCard Errors { get; } = new() { Title = "Errors today", Icon = "DismissCircle24" };
    public StatCard Warnings { get; } = new() { Title = "Warnings today", Icon = "Warning24" };
    public StatCard FailedLogons { get; } = new() { Title = "Failed logons today", Icon = "LockClosed24" };

    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<HeatDay> Heat { get; } = [];
    public ObservableCollection<EventItem> Timeline { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public int Score { get => _score; set => SetProperty(ref _score, value); }
    public string Verdict { get => _verdict; set => SetProperty(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; set => SetProperty(ref _verdictDetail, value); }
    public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }
    public string LastBoot { get => _lastBoot; set => SetProperty(ref _lastBoot, value); }
    public string MachineName { get; } = Environment.MachineName;

    public IAsyncRelayCommand RefreshCommand { get; }

    public DashboardViewModel()
    {
        foreach (var c in new[] { UnexpectedShutdowns, BlueScreens, AppCrashes, DiskErrors, Errors, Warnings, FailedLogons })
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
            Uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min";
            LastBoot = (DateTime.Now - up).ToString("g");

            var window = TimeSpan.FromDays(Days);
            const int cap = 20000;

            var tSysErr = SafeQuery("System", EventLogService.BuildXPath(Severity.Error, window), cap);
            var tAppErr = SafeQuery("Application", EventLogService.BuildXPath(Severity.Error, window), cap);
            var tSysWarn = SafeQuery("System", LevelXPath(3, window), cap);
            var tAppWarn = SafeQuery("Application", LevelXPath(3, window), cap);
            var tDisk = SafeQuery("System", EventLogService.BuildXPath(null, window, [7, 51, 153, 129, 55]), cap);
            var tLogon = SafeQuery("Security", EventLogService.BuildXPath(null, window, [4625]), cap);

            await Task.WhenAll(tSysErr, tAppErr, tSysWarn, tAppWarn, tDisk, tLogon);

            var sysErr = tSysErr.Result ?? new List<EventItem>();
            var appErr = tAppErr.Result ?? new List<EventItem>();
            var warn = (tSysWarn.Result ?? new List<EventItem>()).Concat(tAppWarn.Result ?? new List<EventItem>()).ToList();
            var disk = (tDisk.Result ?? new List<EventItem>()).Where(e => IsDiskProvider(e.Provider)).ToList();

            var shutdowns = sysErr.Where(e => e.Id is 41 or 6008).ToList();
            var bsod = sysErr.Where(e => e.Id == 1001 && e.Provider == "Microsoft-Windows-WER-SystemErrorReporting").ToList();
            var crashes = appErr.Where(e => e.Id is 1000 or 1002 or 1026).ToList();
            var errors = sysErr.Concat(appErr).ToList();

            SetCard(UnexpectedShutdowns, shutdowns, true, "last 30 days", warnAt: 1, badAt: 2);
            SetCard(BlueScreens, bsod, true, "last 30 days", warnAt: 1, badAt: 1);
            SetCard(AppCrashes, crashes, true, "last 30 days", warnAt: 3, badAt: 10);
            SetCard(DiskErrors, disk, true, "last 30 days", warnAt: 1, badAt: 3);
            SetCard(Errors, errors, false, "System + Application", warnAt: 10, badAt: 50);
            SetCard(Warnings, warn, false, "System + Application", warnAt: 50, badAt: 200, neutral: true);

            if (tLogon.Result is null)
            {
                FailedLogons.Value = "-";
                FailedLogons.Hint = "Run as administrator";
                FailedLogons.State = CardState.Neutral;
                FailedLogons.Spark = null;
            }
            else SetCard(FailedLogons, tLogon.Result, false, "Security log", warnAt: 5, badAt: 50);

            BuildHeatmap(errors);
            ComputeScore(shutdowns.Count, bsod.Count, crashes.Count, disk.Count, CountToday(errors));

            await LoadTimelineAsync(window);
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

    // ---------- cards ----------

    private static void SetCard(StatCard card, List<EventItem> items, bool whole30, string hint,
        int warnAt, int badAt, bool neutral = false)
    {
        var count = whole30 ? items.Count : CountToday(items);
        card.Value = count.ToString("N0");
        card.Hint = hint;
        card.Spark = DailyBuckets(items);
        card.State = count >= badAt ? CardState.Bad
                   : count >= warnAt ? CardState.Warn
                   : neutral ? CardState.Neutral : CardState.Good;
    }

    private static int CountToday(IEnumerable<EventItem> items)
    {
        var from = DateTime.Now.AddHours(-24);
        return items.Count(e => e.TimeCreated >= from);
    }

    private static int[] DailyBuckets(IEnumerable<EventItem> items)
    {
        var buckets = new int[Days];
        var today = DateTime.Today;
        foreach (var e in items)
        {
            var d = (today - e.TimeCreated.Date).Days;
            if (d is >= 0 and < Days) buckets[Days - 1 - d]++;
        }
        return buckets;
    }

    private static bool IsDiskProvider(string p) =>
        p is "disk" or "Disk" or "storahci" or "stornvme" or "Ntfs" or "Microsoft-Windows-Ntfs" || p.StartsWith("iaStor", StringComparison.OrdinalIgnoreCase);

    // ---------- heatmap ----------

    private void BuildHeatmap(List<EventItem> errors)
    {
        var buckets = DailyBuckets(errors);
        var max = Math.Max(1, buckets.Max());
        Heat.Clear();
        for (int i = 0; i < Days; i++)
        {
            var c = buckets[i];
            var level = c == 0 ? 0 : Math.Clamp((int)Math.Ceiling(c * 4.0 / max), 1, 4);
            Heat.Add(new HeatDay { Date = DateTime.Today.AddDays(i - (Days - 1)), Count = c, Level = level });
        }
    }

    // ---------- health score ----------

    private void ComputeScore(int shutdowns, int bsod, int crashes, int disk, int errorsToday)
    {
        int penalty = 0;
        penalty += Math.Min(bsod * 15, 45);
        penalty += Math.Min(shutdowns * 8, 32);
        penalty += Math.Min(disk * 6, 30);
        penalty += Math.Min(crashes * 2, 20);
        penalty += errorsToday > 50 ? 10 : errorsToday > 10 ? 5 : 0;
        Score = Math.Clamp(100 - penalty, 0, 100);

        Verdict = Score >= 85 ? "Healthy" : Score >= 60 ? "Needs attention" : "Critical";

        var issues = new List<string>();
        if (bsod > 0) issues.Add(Plural(bsod, "blue screen"));
        if (shutdowns > 0) issues.Add(Plural(shutdowns, "unexpected shutdown"));
        if (disk > 0) issues.Add(Plural(disk, "disk error"));
        if (crashes > 0) issues.Add(Plural(crashes, "app crash", "app crashes"));

        VerdictDetail = issues.Count == 0
            ? "No serious problems in the last 30 days."
            : string.Join(" · ", issues) + " in the last 30 days";
    }

    private static string Plural(int n, string one, string? many = null) =>
        $"{n} {(n == 1 ? one : many ?? one + "s")}";

    // ---------- timeline ----------

    private async Task LoadTimelineAsync(TimeSpan window)
    {
        var sys = SafeQuery("System", EventLogService.BuildXPath(null, window, [41, 6008, 1074, 1001, 7031, 7034, 7, 55]), 40, true);
        var app = SafeQuery("Application", EventLogService.BuildXPath(null, window, [1000, 1002, 1026]), 40, true);
        await Task.WhenAll(sys, app);

        Timeline.Clear();
        foreach (var e in (sys.Result ?? new List<EventItem>()).Concat(app.Result ?? new List<EventItem>()).OrderByDescending(e => e.TimeCreated).Take(25))
            Timeline.Add(e);
    }

    // ---------- helpers ----------

    private static string LevelXPath(int level, TimeSpan window) =>
        $"*[System[Level={level} and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]";

    /// <summary>Returns null when access is denied (e.g. Security log without elevation).</summary>
    private static async Task<List<EventItem>?> SafeQuery(string log, string xpath, int max, bool withMessages = false)
    {
        try { return await EventLogService.QueryAsync(log, xpath, max, withMessages); }
        catch (UnauthorizedAccessException) { return null; }
        catch { return new List<EventItem>(); }
    }
}
