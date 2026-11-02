using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;
using Microsoft.Win32;

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
    /// <summary>Filter opened on the Events page when the card is clicked.</summary>
    public required EventQuerySpec Spec { get; init; }
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
    public string Tip => $"{Date:ddd, d MMM}: {Count:N0} errors - click to open";
    public string DayLabel => Date.Day.ToString();
}

public sealed class DashboardViewModel : ObservableObject
{
    private const int Days = 30;
    private static readonly TimeSpan Month = TimeSpan.FromDays(Days);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly string[] SysApp = ["System", "Application"];

    private bool _isLoading;
    private bool _loadedOnce;
    private string _status = "";
    private int _score;
    private string _verdict = "Analyzing…";
    private string _verdictDetail = "Reading event logs";
    private string _uptime = "";
    private string _lastBoot = "";
    private string _machineName = Environment.MachineName;

    public StatCard UnexpectedShutdowns { get; } = new()
    {
        Title = "Unexpected shutdowns", Icon = "Power24",
        Spec = new() { Logs = ["System"], Ids = [41, 6008], Window = Month, Label = "Unexpected shutdowns · 30 days", Group = false },
    };
    public StatCard BlueScreens { get; } = new()
    {
        Title = "Blue screens", Icon = "ErrorCircle24",
        Spec = new() { Logs = ["System"], Ids = [1001], Providers = ["Microsoft-Windows-WER-SystemErrorReporting"], Window = Month, Label = "Blue screens · 30 days", Group = false },
    };
    public StatCard AppCrashes { get; } = new()
    {
        Title = "App crashes", Icon = "Apps24",
        Spec = new() { Logs = ["Application"], Ids = [1000, 1002, 1026], Window = Month, Label = "App crashes · 30 days" },
    };
    public StatCard DiskErrors { get; } = new()
    {
        Title = "Disk errors", Icon = "Storage24",
        Spec = new() { Logs = ["System"], Ids = [7, 51, 153, 129, 55], Window = Month, Label = "Disk errors · 30 days" },
    };
    public StatCard Errors { get; } = new()
    {
        Title = "Errors today", Icon = "DismissCircle24",
        Spec = new() { Logs = SysApp, MaxLevel = Severity.Error, Window = Day, Label = "Errors · last 24 hours" },
    };
    public StatCard Warnings { get; } = new()
    {
        Title = "Warnings today", Icon = "Warning24",
        Spec = new() { Logs = SysApp, MaxLevel = Severity.Warning, Window = Day, Label = "Errors + warnings · last 24 hours" },
    };
    public StatCard FailedLogons { get; } = new()
    {
        Title = "Failed logons today", Icon = "LockClosed24",
        Spec = new() { Logs = ["Security"], Ids = [4625], Window = Day, Label = "Failed logons · last 24 hours" },
    };

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
    public string MachineName { get => _machineName; set => SetProperty(ref _machineName, value); }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<StatCard> OpenCardCommand { get; }
    public IRelayCommand<HeatDay> OpenDayCommand { get; }
    public IRelayCommand<EventItem> OpenEventCommand { get; }
    public IRelayCommand OpenMonthCommand { get; }
    public IRelayCommand OpenTodayCommand { get; }
    public IRelayCommand OpenIssuesCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IRelayCommand WhyRestartCommand { get; }
    public IRelayCommand OpenFindingsCommand { get; }
    public FindingsViewModel Findings => Navigator.Findings;

    public DashboardViewModel()
    {
        foreach (var c in new[] { UnexpectedShutdowns, BlueScreens, AppCrashes, DiskErrors, Errors, Warnings, FailedLogons })
            Cards.Add(c);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenCardCommand = new RelayCommand<StatCard>(c => { if (c is not null) Navigator.OpenEvents(c.Spec); });
        OpenDayCommand = new RelayCommand<HeatDay>(OpenDay);
        OpenEventCommand = new RelayCommand<EventItem>(OpenEvent);
        OpenMonthCommand = new RelayCommand(() => Navigator.OpenEvents(new EventQuerySpec
            { Logs = SysApp, MaxLevel = Severity.Error, Window = Month, Label = "Errors · last 30 days" }));
        OpenTodayCommand = new RelayCommand(() => OpenDay(Heat.LastOrDefault()));
        OpenIssuesCommand = new RelayCommand(() => Navigator.OpenEvents(new EventQuerySpec
        {
            Logs = SysApp, Ids = [41, 6008, 1001, 1000, 1002, 1026, 7, 51, 153, 129, 55], Window = Month,
            Label = "Problems behind the health score · 30 days",
        }));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        WhyRestartCommand = new RelayCommand(() => Navigator.Navigate(typeof(Views.RestartsPage)));
        OpenFindingsCommand = new RelayCommand(() => Navigator.Navigate(typeof(Views.FindingsPage)));

        AppState.MutedChanged += (_, _) => { if (_loadedOnce && !IsLoading) _ = RefreshAsync(); };

        AppState.SourceChanged += (_, _) =>
        {
            MachineName = AppState.Source.ShortName;
            if (_loadedOnce) _ = RefreshAsync();
        };
    }

    public void EnsureLoaded()
    {
        if (_loadedOnce) return;
        _ = RefreshAsync();
    }

    private void OpenDay(HeatDay? d)
    {
        if (d is null) return;
        Navigator.OpenEvents(new EventQuerySpec
        {
            Logs = SysApp, MaxLevel = Severity.Error, From = d.Date, To = d.Date.AddDays(1),
            Label = $"Errors on {d.Date:ddd, d MMM}",
        });
    }

    private void OpenEvent(EventItem? e)
    {
        if (e is null) return;
        Navigator.OpenEvents(new EventQuerySpec
        {
            Logs = [e.LogName], Ids = [e.Id], Providers = [e.Provider], Window = Month,
            SelectRecordId = e.RecordId, Group = false,
            Label = $"{e.Summary} · 30 days",
        });
    }

    // ---------- loading ----------

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        _loadedOnce = true;
        IsLoading = true;
        Status = "Reading event logs…";
        var sw = Stopwatch.StartNew();
        try
        {
            MachineName = AppState.Source.ShortName;
            if (AppState.Source.Kind == SourceKind.Local)
            {
                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                Uptime = FormatSpan(up);
                LastBoot = (DateTime.Now - up).ToString("g");
            }
            else
            {
                Uptime = "-";
                LastBoot = "-";
            }

            foreach (var c in Cards) { c.Value = "…"; c.Hint = ""; c.Spark = null; c.State = CardState.Neutral; }
            Verdict = "Analyzing…";
            VerdictDetail = "Reading event logs";

            const int cap = 20000;

            // each card fills in as soon as its own query is done (progressive UI)
            var tErr = SafeQuery(SysApp, EventLogService.BuildXPath(Severity.Error, Month), cap);
            var tDisk = SafeQuery(["System"], EventLogService.BuildXPath(null, Month, [7, 51, 153, 129, 55]), cap);
            var tWarn = SafeQuery(SysApp, LevelXPath(3, Month), cap);
            var tLogon = SafeQuery(["Security"], EventLogService.BuildXPath(null, Month, [4625]), cap);
            var tTimeline = LoadTimelineAsync();

            var allErrors = await tErr ?? new List<EventItem>();
            HistoryStore.RecordEvents(allErrors);
            var errors = allErrors.Where(e => !e.IsHarmless).ToList();
            var harmless = allErrors.Count - errors.Count;
            var shutdowns = errors.Where(e => e.Id is 41 or 6008).ToList();
            var bsod = errors.Where(e => e.Id == 1001 && e.Provider == "Microsoft-Windows-WER-SystemErrorReporting").ToList();
            var crashes = errors.Where(e => e.Id is 1000 or 1002 or 1026).ToList();
            SetCard(UnexpectedShutdowns, shutdowns, true, "last 30 days", warnAt: 1, badAt: 2);
            SetCard(BlueScreens, bsod, true, "last 30 days", warnAt: 1, badAt: 1);
            SetCard(AppCrashes, crashes, true, "last 30 days", warnAt: 3, badAt: 10);
            SetCard(Errors, errors, false, harmless > 0 ? $"{harmless:N0} harmless hidden (30 days)" : "System + Application", warnAt: 10, badAt: 50);
            BuildHeatmap(errors);

            var disk = (await tDisk ?? new List<EventItem>()).Where(e => IsDiskProvider(e.Provider)).ToList();
            SetCard(DiskErrors, disk, true, "last 30 days", warnAt: 1, badAt: 3);

            ComputeScore(shutdowns.Count, bsod.Count, crashes.Count, disk.Count, CountToday(errors));

            var warn = (await tWarn ?? new List<EventItem>()).Where(e => !e.IsHarmless).ToList();
            SetCard(Warnings, warn, false, "System + Application", warnAt: 50, badAt: 200, neutral: true);

            var logons = await tLogon;
            if (logons is null)
            {
                FailedLogons.Value = "-";
                FailedLogons.Hint = AppState.Source.Kind == SourceKind.Local ? "Run as administrator" : "No access to Security log";
                FailedLogons.State = CardState.Neutral;
                FailedLogons.Spark = null;
            }
            else SetCard(FailedLogons, logons, false, "Security log", warnAt: 5, badAt: 50);

            await tTimeline;
            Status = $"Updated {DateTime.Now:t} · {sw.Elapsed.TotalSeconds:0.0} s";
            _ = Navigator.Findings.RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            Verdict = "Can't read logs";
            VerdictDetail = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string FormatSpan(TimeSpan up) =>
        up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min";

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
        p is "disk" or "Disk" or "storahci" or "stornvme" or "Ntfs" or "Microsoft-Windows-Ntfs"
        || p.StartsWith("iaStor", StringComparison.OrdinalIgnoreCase);

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

    private async Task LoadTimelineAsync()
    {
        var sys = SafeQuery(["System"], EventLogService.BuildXPath(null, Month, [41, 6008, 1074, 1001, 7031, 7034, 7, 55]), 40, true);
        var app = SafeQuery(["Application"], EventLogService.BuildXPath(null, Month, [1000, 1002, 1026]), 40, true);
        var all = (await sys ?? new List<EventItem>()).Concat(await app ?? new List<EventItem>())
            .Where(e => !e.IsHarmless).OrderByDescending(e => e.TimeCreated).Take(25).ToList();

        Timeline.Clear();
        foreach (var e in all) Timeline.Add(e);
    }

    // ---------- export ----------

    private async Task ExportAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export health report",
            Filter = "HTML report (*.html)|*.html",
            FileName = $"EventLens-{MachineName}-{DateTime.Now:yyyy-MM-dd}.html",
        };
        if (dlg.ShowDialog() != true) return;

        List<RestartInfo>? restarts = null;
        try { restarts = await RestartAnalyzer.AnalyzeAsync(10); } catch { /* optional section */ }

        try
        {
            await File.WriteAllTextAsync(dlg.FileName, ReportService.Build(this, restarts));
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            Status = "Report saved: " + Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    // ---------- helpers ----------

    private static string LevelXPath(int level, TimeSpan window) =>
        $"*[System[Level={level} and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]";

    /// <summary>Returns null when access is denied (e.g. Security log without elevation).</summary>
    private static async Task<List<EventItem>?> SafeQuery(string[] logs, string xpath, int max, bool withMessages = false)
    {
        try { return await EventLogService.QueryLogsAsync(logs, xpath, max, withMessages); }
        catch (UnauthorizedAccessException) { return null; }
        catch { return new List<EventItem>(); }
    }
}
