using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;
using EventLens.Views;

namespace EventLens.ViewModels;

public sealed record LogChoice(string Name, string[] Logs);

public sealed class QuickFilter
{
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public string[]? Logs { get; init; }
    public int[]? Ids { get; init; }
    public string[]? Providers { get; init; }
    public Severity? Level { get; init; }
}

public sealed class EventsViewModel : ObservableObject
{
    private const int MaxEvents = 5000;

    private static readonly TimeSpan?[] WindowSpans =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30), null, null];

    private static readonly Severity?[] LevelMap =
        [Severity.Critical, Severity.Error, Severity.Warning, Severity.Information, null];

    private int _logIndex;
    private int _levelIndex;
    private int _windowIndex;
    private string _search = "";
    private bool _isLoading;
    private string _status = "";
    private EventRow? _selected;
    private bool _group;
    private bool _suppress;
    private bool _loadedOnce;
    private CancellationTokenSource? _cts;
    private List<EventItem> _loaded = [];

    // active filter (on top of the combos)
    private int[]? _ids;
    private string[]? _providers;
    private string[]? _logsOverride;
    private DateTime? _from, _to;
    private string? _filterLabel;
    private long? _selectRecord;

    public IReadOnlyList<LogChoice> LogChoices { get; } =
    [
        new("System + Application", ["System", "Application"]),
        new("System", ["System"]),
        new("Application", ["Application"]),
        new("Security", ["Security"]),
        new("Setup", ["Setup"]),
        new("All logs", ["System", "Application", "Security", "Setup"]),
    ];

    public IReadOnlyList<string> Levels { get; } = ["Critical", "Errors", "Errors + warnings", "All except verbose", "All"];
    public IReadOnlyList<string> TimeWindows { get; } = ["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "All time", "Custom range"];

    public IReadOnlyList<QuickFilter> QuickFilters { get; } =
    [
        new() { Name = "Restarts", Icon = "ArrowReset24", Logs = ["System"], Ids = [41, 6008, 1074, 12, 13, 6005, 6006] },
        new() { Name = "Crashes", Icon = "ErrorCircle24", Logs = ["Application"], Ids = [1000, 1002, 1026] },
        new() { Name = "Blue screens", Icon = "Warning24", Logs = ["System"], Ids = [1001], Providers = ["Microsoft-Windows-WER-SystemErrorReporting"] },
        new() { Name = "Disk", Icon = "Storage24", Logs = ["System"], Ids = [7, 51, 153, 129, 55] },
        new() { Name = "Services", Icon = "Wrench24", Logs = ["System"], Providers = ["Service Control Manager"], Level = Severity.Warning },
        new() { Name = "Logons", Icon = "Person24", Logs = ["Security"], Ids = [4624, 4625, 4740] },
        new() { Name = "Failed logons", Icon = "LockClosed24", Logs = ["Security"], Ids = [4625, 4740] },
        new() { Name = "Updates", Icon = "ArrowSync24", Logs = ["System"], Providers = ["Microsoft-Windows-WindowsUpdateClient"] },
        new() { Name = "Network", Icon = "Globe24", Logs = ["System"], Ids = [1014, 5719, 1129, 129], Level = Severity.Warning },
    ];

    public ObservableCollection<EventRow> Rows { get; } = [];
    public ICollectionView View { get; }

    public int LogIndex
    {
        get => _logIndex;
        set { if (SetProperty(ref _logIndex, value) && !_suppress) { _logsOverride = null; SaveAndRefresh(); } }
    }

    public int LevelIndex
    {
        get => _levelIndex;
        set { if (SetProperty(ref _levelIndex, value) && !_suppress) SaveAndRefresh(); }
    }

    public int WindowIndex
    {
        get => _windowIndex;
        set
        {
            if (_suppress) { SetProperty(ref _windowIndex, value); return; }
            if (value == 5 && _from is null) { OnPropertyChanged(); return; } // "Custom" only via navigation
            if (SetProperty(ref _windowIndex, value) && value != 5)
            {
                _from = _to = null;
                SaveAndRefresh();
            }
        }
    }

    public bool GroupRepeats
    {
        get => _group;
        set
        {
            if (!SetProperty(ref _group, value)) return;
            AppSettings.Current.GroupRepeats = value;
            AppSettings.Current.Save();
            RebuildRows();
        }
    }

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) View.Refresh(); }
    }

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public string? FilterLabel
    {
        get => _filterLabel;
        private set { if (SetProperty(ref _filterLabel, value)) OnPropertyChanged(nameof(HasFilter)); }
    }
    public bool HasFilter => _filterLabel is not null;

    public string SourceText => AppState.Source.Kind == SourceKind.Local ? "" : $"{AppState.Source.KindText}: {AppState.Source.ShortName}";
    public bool IsFileSource => AppState.Source.Kind == SourceKind.File;
    public bool IsNotFileSource => !IsFileSource;

    public EventRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedEvent));
        }
    }
    public bool HasSelection => _selected is not null;
    public EventItem? SelectedEvent => _selected?.First;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<QuickFilter> QuickFilterCommand { get; }
    public IRelayCommand ClearFilterCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IRelayCommand SearchOnlineCommand { get; }
    public IRelayCommand FilterSimilarCommand { get; }
    public IAsyncRelayCommand AskAiCommand { get; }

    public EventsViewModel()
    {
        var s = AppSettings.Current;
        _logIndex = Math.Clamp(s.LogIndex, 0, LogChoices.Count - 1);
        _levelIndex = Math.Clamp(s.LevelIndex, 0, Levels.Count - 1);
        _windowIndex = Math.Clamp(s.WindowIndex, 0, 4);
        _group = s.GroupRepeats;

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Matches;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        QuickFilterCommand = new RelayCommand<QuickFilter>(ApplyQuickFilter);
        ClearFilterCommand = new RelayCommand(ClearFilter);
        CopyCommand = new RelayCommand(Copy);
        SearchOnlineCommand = new RelayCommand(SearchOnline);
        FilterSimilarCommand = new RelayCommand(FilterSimilar);
        AskAiCommand = new AsyncRelayCommand(AskAiAsync);

        AppState.SourceChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SourceText));
            OnPropertyChanged(nameof(IsFileSource));
            OnPropertyChanged(nameof(IsNotFileSource));
            if (_loadedOnce) _ = RefreshAsync();
        };
    }

    public void EnsureLoaded()
    {
        if (_loadedOnce) return;
        _ = RefreshAsync();
    }

    private void SaveAndRefresh()
    {
        var s = AppSettings.Current;
        s.LogIndex = _logIndex;
        s.LevelIndex = _levelIndex;
        if (_windowIndex <= 4) s.WindowIndex = _windowIndex;
        s.Save();
        _ = RefreshAsync();
    }

    // ---------- filters ----------

    /// <summary>Opens the page with a filter coming from another page (Overview card, heatmap, timeline, restart).</summary>
    public Task ApplyAsync(EventQuerySpec spec)
    {
        _suppress = true;
        try
        {
            _ids = spec.Ids;
            _providers = spec.Providers;
            _from = spec.From;
            _to = spec.To;
            _selectRecord = spec.SelectRecordId;

            var idx = FindLogChoice(spec.Logs);
            if (idx >= 0) { LogIndex = idx; _logsOverride = null; }
            else _logsOverride = spec.Logs;

            LevelIndex = spec.MaxLevel switch
            {
                Severity.Critical => 0,
                Severity.Error => 1,
                Severity.Warning => 2,
                Severity.Information => 3,
                _ => 4,
            };

            var hrs = spec.Window?.TotalHours;
            WindowIndex = spec.From is not null || spec.To is not null ? 5
                : hrs is null ? 4
                : hrs <= 1 ? 0
                : hrs <= 24 ? 1
                : hrs <= 24 * 7 ? 2
                : 3;

            if (spec.Group is { } g && g != _group)
            {
                _group = g;
                OnPropertyChanged(nameof(GroupRepeats));
            }

            Search = "";
            FilterLabel = spec.Label;
        }
        finally { _suppress = false; }

        return RefreshAsync();
    }

    private int FindLogChoice(string[] logs)
    {
        for (int i = 0; i < LogChoices.Count; i++)
            if (LogChoices[i].Logs.Length == logs.Length && !LogChoices[i].Logs.Except(logs).Any()) return i;
        return -1;
    }

    private void ApplyQuickFilter(QuickFilter? f)
    {
        if (f is null) return;
        _suppress = true;
        try
        {
            _ids = f.Ids;
            _providers = f.Providers;
            var idx = f.Logs is null ? -1 : FindLogChoice(f.Logs);
            if (idx >= 0) { LogIndex = idx; _logsOverride = null; }
            else _logsOverride = f.Logs;
            LevelIndex = f.Level switch { Severity.Warning => 2, Severity.Error => 1, _ => 4 };
            FilterLabel = f.Name;
        }
        finally { _suppress = false; }
        _ = RefreshAsync();
    }

    private void ClearFilter()
    {
        _ids = null;
        _providers = null;
        _logsOverride = null;
        _selectRecord = null;
        FilterLabel = null;
        if (_windowIndex == 5)
        {
            _suppress = true;
            WindowIndex = 1;
            _suppress = false;
            _from = _to = null;
        }
        _ = RefreshAsync();
    }

    private bool Matches(object o)
    {
        if (string.IsNullOrWhiteSpace(_search) || o is not EventRow r) return true;
        var s = _search.Trim();
        var e = r.First;
        return e.Id.ToString() == s
            || e.Provider.Contains(s, StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains(s, StringComparison.OrdinalIgnoreCase)
            || (e.Explanation?.Title.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ---------- loading ----------

    public async Task RefreshAsync()
    {
        _loadedOnce = true;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var logs = _logsOverride ?? LogChoices[_logIndex].Logs;
        var xpath = EventLogService.BuildXPath(LevelMap[_levelIndex], WindowSpans[_windowIndex], _ids, _from, _to, _providers);
        var dispatcher = Application.Current.Dispatcher;
        var sw = Stopwatch.StartNew();

        IsLoading = true;
        Status = $"Reading {string.Join(" + ", logs)}…";
        Rows.Clear();
        Selected = null;
        int streamed = 0;

        try
        {
            // stream the first rows in immediately; the final, sorted (and grouped) list replaces them at the end
            var list = await EventLogService.QueryLogsAsync(logs, xpath, MaxEvents, true, cts.Token, batch =>
            {
                if (streamed > 600) return;
                streamed += batch.Count;
                dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var e in batch) Rows.Add(new EventRow { First = e, All = [e] });
                    Status = $"Reading… {Rows.Count:N0}";
                });
            });
            if (cts.IsCancellationRequested) return;

            _loaded = list;
            RebuildRows();

            var known = list.Count(e => e.HasExplanation);
            Status = (list.Count >= MaxEvents ? $"{list.Count:N0} events (limit)" : $"{list.Count:N0} events")
                     + $" · {known:N0} explained · {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            Rows.Clear();
            Status = "Access denied - run EventLens as administrator (Settings) or use other credentials (Computers).";
        }
        catch (EventLogException ex)
        {
            Rows.Clear();
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Rows.Clear();
            Status = "Error: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsLoading = false;
        }
    }

    private void RebuildRows()
    {
        var keepKey = _selected?.First.GroupKey;
        Rows.Clear();

        IEnumerable<EventRow> rows = _group
            ? _loaded.GroupBy(e => e.GroupKey)
                .Select(g => { var all = g.OrderByDescending(e => e.TimeCreated).ToList(); return new EventRow { First = all[0], All = all }; })
                .OrderByDescending(r => r.LastSeen)
            : _loaded.Select(e => new EventRow { First = e, All = [e] });

        foreach (var r in rows) Rows.Add(r);

        EventRow? select = null;
        if (_selectRecord is { } rid)
        {
            select = Rows.FirstOrDefault(r => r.All.Any(e => e.RecordId == rid));
            _selectRecord = null;
        }
        select ??= keepKey is null ? null : Rows.FirstOrDefault(r => r.First.GroupKey == keepKey);
        Selected = select ?? Rows.FirstOrDefault();
        if (select is not null) SelectionRequested?.Invoke(this, select);
    }

    /// <summary>Asks the view to scroll a row into view.</summary>
    public event EventHandler<EventRow>? SelectionRequested;

    // ---------- detail actions ----------

    private void Copy()
    {
        if (SelectedEvent is not { } e) return;
        try { Clipboard.SetText(e.ToClipboardText()); Status = "Copied to clipboard"; }
        catch { Status = "Clipboard is busy - try again"; }
    }

    private void SearchOnline()
    {
        if (SelectedEvent is not { } e) return;
        var q = Uri.EscapeDataString($"Event ID {e.Id} {e.Provider}");
        Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={q}") { UseShellExecute = true });
    }

    private void FilterSimilar()
    {
        if (SelectedEvent is not { } e) return;
        _ = ApplyAsync(new EventQuerySpec
        {
            Logs = [e.LogName],
            Ids = [e.Id],
            Providers = [e.Provider],
            Window = TimeSpan.FromDays(30),
            Label = $"{e.Provider} · ID {e.Id}",
            Group = false,
        });
    }

    private async Task AskAiAsync()
    {
        if (_selected is not { } row) return;
        var e = row.First;
        var nearby = await NearbyAsync(e.TimeCreated, TimeSpan.FromMinutes(10));
        AiWindow.Open(
            $"Event {e.Id} · {e.Provider}",
            AiService.BuildEventContext(e, nearby, row.Count),
            "Explain this event: what happened, the most likely cause, how serious it is, and what I should do.");
    }

    public static async Task<List<EventItem>> NearbyAsync(DateTime t, TimeSpan span)
    {
        try
        {
            var xpath = EventLogService.BuildXPath(Severity.Warning, null, null, t - span, t + span);
            return await EventLogService.QueryLogsAsync(["System", "Application"], xpath, 40, true);
        }
        catch { return []; }
    }
}
