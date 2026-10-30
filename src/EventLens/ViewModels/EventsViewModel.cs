using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;

namespace EventLens.ViewModels;

public sealed class EventsViewModel : ObservableObject
{
    private static readonly TimeSpan[] WindowSpans =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.Zero];

    private int _logIndex;
    private int _levelIndex = 2;   // Errors + warnings
    private int _windowIndex = 1;  // 24 h
    private string _search = "";
    private bool _isLoading;
    private string _status = "";
    private EventItem? _selected;
    private CancellationTokenSource? _cts;

    public IReadOnlyList<string> Logs => EventLogService.StandardLogs;
    public IReadOnlyList<string> Levels { get; } = ["Critical", "Errors", "Errors + warnings", "All except verbose", "All"];
    public IReadOnlyList<string> TimeWindows { get; } = ["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "All time"];

    public ObservableCollection<EventItem> Items { get; } = [];
    public ICollectionView View { get; }

    public int LogIndex { get => _logIndex; set { if (SetProperty(ref _logIndex, value)) _ = RefreshAsync(); } }
    public int LevelIndex { get => _levelIndex; set { if (SetProperty(ref _levelIndex, value)) _ = RefreshAsync(); } }
    public int WindowIndex { get => _windowIndex; set { if (SetProperty(ref _windowIndex, value)) _ = RefreshAsync(); } }

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) View.Refresh(); }
    }

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public EventItem? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected is not null;

    public IAsyncRelayCommand RefreshCommand { get; }

    public EventsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    private bool Matches(object o)
    {
        if (string.IsNullOrWhiteSpace(_search) || o is not EventItem e) return true;
        var s = _search.Trim();
        return e.Id.ToString() == s
            || e.Provider.Contains(s, StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains(s, StringComparison.OrdinalIgnoreCase)
            || (e.Explanation?.Title.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task RefreshAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        Severity? level = _levelIndex switch
        {
            0 => Severity.Critical,
            1 => Severity.Error,
            2 => Severity.Warning,
            3 => Severity.Information,
            _ => null,
        };
        var window = WindowSpans[_windowIndex];
        var log = Logs[_logIndex];
        var xpath = EventLogService.BuildXPath(level, window == TimeSpan.Zero ? null : window);

        IsLoading = true;
        Status = $"Reading {log}…";
        try
        {
            var list = await EventLogService.QueryAsync(log, xpath, 5000, true, null, cts.Token);
            if (cts.IsCancellationRequested) return;

            Items.Clear();
            foreach (var e in list) Items.Add(e);
            Selected = Items.FirstOrDefault();
            var known = list.Count(e => e.HasExplanation);
            Status = list.Count >= 5000
                ? $"{list.Count:N0} events (limit reached) · {known:N0} explained"
                : $"{list.Count:N0} events · {known:N0} explained";
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            Items.Clear();
            Status = $"Access denied to {log} - run EventLens as administrator.";
        }
        catch (EventLogException ex)
        {
            Items.Clear();
            Status = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsLoading = false;
        }
    }
}
