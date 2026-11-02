using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Core.Findings;
using EventLens.Models;
using EventLens.Services;
using Microsoft.Win32;

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

/// <summary>Serializable snapshot of the Events filters (saved views).</summary>
public sealed class ViewState
{
    public string[]? Logs { get; set; }
    public int LevelIndex { get; set; }
    public int WindowIndex { get; set; }
    public int[]? Ids { get; set; }
    public string[]? Providers { get; set; }
    public string Search { get; set; } = "";
    public bool Group { get; set; } = true;
    public string? Label { get; set; }
}

public sealed class EventsViewModel : ObservableObject
{
    private const int DefaultLimit = 5000;
    private const int MaxLimit = 50000;

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
    private EventDetailViewModel? _detail;
    private bool _group;
    private bool _hideHarmless;
    private bool _suppress;
    private bool _loadedOnce;
    private bool _limitReached;
    private int _limit = DefaultLimit;
    private int _hiddenHarmless;
    private bool _isEmpty;
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
        new("Windows Defender", [FindingsEngine.DefenderLog]),
        new("All main logs", ["System", "Application", "Security", "Setup"]),
    ];

    public IReadOnlyList<string> Levels { get; } = ["Critical", "Errors", "Errors + warnings", "All except verbose", "All"];
    public IReadOnlyList<string> TimeWindows { get; } = ["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "All time", "Custom range"];

    public IReadOnlyList<QuickFilter> QuickFilters { get; } =
    [
        new() { Name = "Restarts", Icon = "ArrowReset24", Logs = ["System"], Ids = [41, 6008, 1074, 12, 13, 6005, 6006] },
        new() { Name = "Crashes", Icon = "ErrorCircle24", Logs = ["Application"], Ids = [1000, 1002, 1026] },
        new() { Name = "Blue screens", Icon = "Warning24", Logs = ["System"], Ids = [1001], Providers = ["Microsoft-Windows-WER-SystemErrorReporting"] },
        new() { Name = "Disk", Icon = "Storage24", Logs = ["System"], Ids = [7, 11, 51, 52, 153, 129, 55, 140] },
        new() { Name = "Services", Icon = "Wrench24", Logs = ["System"], Providers = ["Service Control Manager"], Level = Severity.Warning },
        new() { Name = "Logons", Icon = "Person24", Logs = ["Security"], Ids = [4624, 4625, 4740] },
        new() { Name = "Failed logons", Icon = "LockClosed24", Logs = ["Security"], Ids = [4625, 4740] },
        new() { Name = "Threats", Icon = "ShieldError24", Logs = [FindingsEngine.DefenderLog], Ids = [1006, 1008, 1116, 1117, 1118, 1119, 5001, 5010, 5012] },
        new() { Name = "Updates", Icon = "ArrowSync24", Logs = ["System"], Providers = ["Microsoft-Windows-WindowsUpdateClient"] },
        new() { Name = "Network", Icon = "Globe24", Logs = ["System"], Ids = [1014, 5719, 1129, 4199, 4227, 4231, 1001, 1003], Level = Severity.Warning },
    ];

    public ObservableCollection<EventRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<SavedView> SavedViews { get; } = [];

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
            if (value == 5 && _from is null) { OnPropertyChanged(); return; } // "Custom range" only via navigation
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

    public bool HideHarmless
    {
        get => _hideHarmless;
        set
        {
            if (!SetProperty(ref _hideHarmless, value)) return;
            AppSettings.Current.HideHarmless = value;
            AppSettings.Current.Save();
            RebuildRows();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value)) return;
            View.Refresh();
            UpdateEmpty();
        }
    }

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool LimitReached { get => _limitReached; set => SetProperty(ref _limitReached, value); }
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }

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
            Detail = value is null ? null : new EventDetailViewModel(value.First, value);
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public EventDetailViewModel? Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool HasSelection => _selected is not null;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IRelayCommand<QuickFilter> QuickFilterCommand { get; }
    public IRelayCommand ClearFilterCommand { get; }
    public IAsyncRelayCommand ExportCsvCommand { get; }
    public IAsyncRelayCommand ExportEvtxCommand { get; }
    public IRelayCommand<string> SaveViewCommand { get; }
    public IRelayCommand<SavedView> ApplyViewCommand { get; }
    public IRelayCommand<SavedView> DeleteViewCommand { get; }

    public EventsViewModel()
    {
        var s = AppSettings.Current;
        _logIndex = Math.Clamp(s.LogIndex, 0, LogChoices.Count - 1);
        _levelIndex = Math.Clamp(s.LevelIndex, 0, Levels.Count - 1);
        _windowIndex = Math.Clamp(s.WindowIndex, 0, 4);
        _group = s.GroupRepeats;
        _hideHarmless = s.HideHarmless;

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Matches;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CancelCommand = new RelayCommand(Cancel);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        QuickFilterCommand = new RelayCommand<QuickFilter>(ApplyQuickFilter);
        ClearFilterCommand = new RelayCommand(ClearFilter);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync);
        ExportEvtxCommand = new AsyncRelayCommand(ExportEvtxAsync);
        SaveViewCommand = new RelayCommand<string>(SaveView);
        ApplyViewCommand = new RelayCommand<SavedView>(ApplyView);
        DeleteViewCommand = new RelayCommand<SavedView>(DeleteView);

        foreach (var v in HistoryStore.GetViews()) SavedViews.Add(v);

        AppState.SourceChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SourceText));
            OnPropertyChanged(nameof(IsFileSource));
            OnPropertyChanged(nameof(IsNotFileSource));
            if (_loadedOnce) _ = RefreshAsync();
        };

        AppState.MutedChanged += (_, _) =>
        {
            foreach (var e in _loaded) e.UserMuted = AppState.IsMuted(e.TypeKey);
            RebuildRows();
        };

        EventDetailViewModel.Status += (_, msg) => Status = msg;
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

    private string[] CurrentLogs => _logsOverride ?? LogChoices[_logIndex].Logs;
    private string CurrentXPath => EventLogService.BuildXPath(LevelMap[_levelIndex], WindowSpans[_windowIndex], _ids, _from, _to, _providers);

    // ---------- filters ----------

    /// <summary>Opens the page with a filter coming from another page (Overview card, heatmap, timeline, finding...).</summary>
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

            _search = "";
            OnPropertyChanged(nameof(Search));
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

    public void ClearFilter()
    {
        var hadSearch = _search.Length > 0;
        _search = "";
        OnPropertyChanged(nameof(Search));
        if (!HasFilter && _windowIndex != 5)
        {
            if (hadSearch) { View.Refresh(); UpdateEmpty(); }
            return;
        }

        _ids = null;
        _providers = null;
        _logsOverride = null;
        _selectRecord = null;
        FilterLabel = null;
        if (_windowIndex == 5)
        {
            _suppress = true;
            WindowIndex = Math.Clamp(AppSettings.Current.WindowIndex, 0, 4);
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
            || e.Summary.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- loading ----------

    public Task RefreshAsync()
    {
        _limit = DefaultLimit;
        return LoadAsync();
    }

    private Task LoadMoreAsync()
    {
        _limit = MaxLimit;
        return LoadAsync();
    }

    private void Cancel()
    {
        _cts?.Cancel();
        IsLoading = false;
        Status = $"Cancelled · showing {_loaded.Count:N0} events loaded so far";
    }

    private async Task LoadAsync()
    {
        _loadedOnce = true;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var logs = CurrentLogs;
        var xpath = CurrentXPath;
        var dispatcher = Application.Current.Dispatcher;
        var sw = Stopwatch.StartNew();
        var limit = _limit;

        IsLoading = true;
        LimitReached = false;
        Status = $"Reading {string.Join(" + ", logs.Select(ShortLog))}…";
        Rows.Clear();
        Selected = null;
        IsEmpty = false;
        var streamed = new List<EventItem>();

        try
        {
            // stream the first rows in immediately; the final, sorted (and grouped) list replaces them at the end
            var list = await EventLogService.QueryLogsAsync(logs, xpath, limit, true, cts.Token, batch =>
            {
                dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    streamed.AddRange(batch);
                    if (Rows.Count < 400)
                        foreach (var e in batch.Where(e => !_hideHarmless || !e.IsHarmless).Take(400 - Rows.Count))
                            Rows.Add(new EventRow { First = e, All = [e] });
                    Status = $"Reading… {streamed.Count:N0}";
                });
            });
            if (cts.IsCancellationRequested) return;

            _loaded = list;
            RebuildRows();
            HistoryStore.RecordEvents(list);

            LimitReached = list.Count >= limit && limit < MaxLimit;
            var known = list.Count(e => e.HasExplanation);
            Status = (list.Count >= limit ? $"Newest {list.Count:N0} events" : $"{list.Count:N0} events")
                     + $" · {known:N0} explained"
                     + (_hiddenHarmless > 0 ? $" · {_hiddenHarmless:N0} harmless hidden" : "")
                     + $" · {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts) && streamed.Count > 0)
            {
                _loaded = streamed.OrderByDescending(e => e.TimeCreated).ToList();
                RebuildRows();
            }
        }
        catch (UnauthorizedAccessException)
        {
            Rows.Clear();
            Status = "Access denied - run EventLens as administrator (Settings) or use other credentials (Computers).";
            UpdateEmpty();
        }
        catch (EventLogNotFoundException)
        {
            Rows.Clear();
            Status = "This log does not exist on this computer.";
            UpdateEmpty();
        }
        catch (EventLogException ex)
        {
            Rows.Clear();
            Status = ex.Message;
            UpdateEmpty();
        }
        catch (Exception ex)
        {
            Rows.Clear();
            Status = "Error: " + ex.Message;
            UpdateEmpty();
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsLoading = false;
        }
    }

    private static string ShortLog(string log) => log.StartsWith("Microsoft-Windows-") ? log["Microsoft-Windows-".Length..].Split('/')[0] : log;

    private void RebuildRows()
    {
        var keepKey = _selected?.First.GroupKey;
        Rows.Clear();

        var source = _hideHarmless ? _loaded.Where(e => !e.IsHarmless).ToList() : _loaded;
        _hiddenHarmless = _loaded.Count - source.Count;

        IEnumerable<EventRow> rows = _group
            ? source.GroupBy(e => e.GroupKey)
                .Select(g => { var all = g.OrderByDescending(e => e.TimeCreated).ToList(); return new EventRow { First = all[0], All = all }; })
                .OrderByDescending(r => r.LastSeen)
            : source.Select(e => new EventRow { First = e, All = [e] });

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
        UpdateEmpty();
    }

    private void UpdateEmpty() => IsEmpty = !IsLoading && View.IsEmpty;

    /// <summary>Asks the view to scroll a row into view.</summary>
    public event EventHandler<EventRow>? SelectionRequested;

    // ---------- export ----------

    private async Task ExportCsvAsync()
    {
        var events = View.Cast<EventRow>().SelectMany(r => r.All).OrderByDescending(e => e.TimeCreated).ToList();
        if (events.Count == 0) { Status = "Nothing to export"; return; }

        var dlg = new SaveFileDialog
        {
            Title = "Export events to CSV",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"EventLens-{AppState.Source.ShortName}-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Level,Log,Source,EventID,Meaning,Harmless,Message");
            foreach (var e in events)
                sb.AppendLine(string.Join(",", Csv(e.TimeCreated.ToString("yyyy-MM-dd HH:mm:ss")), Csv(e.Severity.ToString()), Csv(e.LogName),
                    Csv(e.Provider), e.Id.ToString(), Csv(e.HasExplanation ? e.Summary : ""), e.IsHarmless ? "yes" : "no", Csv(e.Message)));
            await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Status = $"Exported {events.Count:N0} events to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    private async Task ExportEvtxAsync()
    {
        string[] logs = AppState.Source.Kind == SourceKind.File ? new[] { "file" } : CurrentLogs;
        var dlg = new SaveFileDialog
        {
            Title = "Export events to EVTX (opens in Event Viewer)",
            Filter = "Event log file (*.evtx)|*.evtx",
            FileName = $"EventLens-{AppState.Source.ShortName}-{DateTime.Now:yyyy-MM-dd-HHmm}.evtx",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            IsLoading = true;
            var dir = Path.GetDirectoryName(dlg.FileName)!;
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            var written = new List<string>();
            foreach (var log in logs)
            {
                var target = logs.Length == 1 ? dlg.FileName : Path.Combine(dir, $"{name}-{ShortLog(log).Replace('/', '-')}.evtx");
                if (File.Exists(target)) File.Delete(target);
                await EventLogService.ExportEvtxAsync(log, CurrentXPath, target);
                written.Add(Path.GetFileName(target));
            }
            Status = "Exported " + string.Join(", ", written) + (string.IsNullOrWhiteSpace(_search) ? "" : " (search text is not applied to EVTX export)");
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Export needs administrator rights for this log.";
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---------- saved views ----------

    private void SaveView(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Status = "Type a name for the view first"; return; }
        var state = new ViewState
        {
            Logs = CurrentLogs,
            LevelIndex = _levelIndex,
            WindowIndex = Math.Min(_windowIndex, 4),
            Ids = _ids,
            Providers = _providers,
            Search = _search,
            Group = _group,
            Label = name.Trim(),
        };
        var json = JsonSerializer.Serialize(state);
        HistoryStore.SaveView(name.Trim(), json);
        var existing = SavedViews.FirstOrDefault(v => v.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) SavedViews.Remove(existing);
        SavedViews.Add(new SavedView(name.Trim(), json));
        Status = $"View \"{name.Trim()}\" saved";
    }

    private void ApplyView(SavedView? v)
    {
        if (v is null) return;
        ViewState? s;
        try { s = JsonSerializer.Deserialize<ViewState>(v.Json); } catch { s = null; }
        if (s is null) { Status = "This view could not be read"; return; }

        _suppress = true;
        try
        {
            _ids = s.Ids;
            _providers = s.Providers;
            _from = _to = null;
            var logs = s.Logs ?? new[] { "System", "Application" };
            var idx = FindLogChoice(logs);
            if (idx >= 0) { LogIndex = idx; _logsOverride = null; }
            else _logsOverride = logs;
            LevelIndex = Math.Clamp(s.LevelIndex, 0, Levels.Count - 1);
            WindowIndex = Math.Clamp(s.WindowIndex, 0, 4);
            _group = s.Group;
            OnPropertyChanged(nameof(GroupRepeats));
            _search = s.Search;
            OnPropertyChanged(nameof(Search));
            FilterLabel = s.Label ?? v.Name;
        }
        finally { _suppress = false; }
        _ = RefreshAsync();
    }

    private void DeleteView(SavedView? v)
    {
        if (v is null) return;
        HistoryStore.DeleteView(v.Name);
        SavedViews.Remove(v);
        Status = $"View \"{v.Name}\" deleted";
    }

    // ---------- helpers ----------

    public static async Task<List<EventItem>> NearbyAsync(DateTime t, TimeSpan span)
    {
        try
        {
            var xpath = EventLogService.BuildXPath(Severity.Warning, null, null, t - span, t + span);
            var list = await EventLogService.QueryLogsAsync(["System", "Application"], xpath, 60, true);
            return list.Where(e => !e.IsHarmless).ToList();
        }
        catch { return []; }
    }
}
