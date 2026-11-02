using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;

namespace EventLens.ViewModels;

public sealed class CompareRow
{
    public required EventItem Sample { get; init; }
    public required int Now { get; init; }
    public required int Before { get; init; }
    public string Summary => Sample.Summary;
    public string Source => $"{Sample.Provider} · {Sample.Id}";
    public string Change => Before == 0 ? "new" : Now == 0 ? "gone" : $"{Before} → {Now}";
}

/// <summary>Compares errors/warnings between two periods: what is new, gone, more or less frequent.</summary>
public sealed class CompareViewModel : ObservableObject
{
    private int _periodIndex;
    private bool _includeWarnings = true;
    private bool _isLoading;
    private bool _loadedOnce;
    private string _status = "";
    private string _labelA = "";
    private string _labelB = "";
    private (DateTime From, DateTime To) _a, _b;

    public IReadOnlyList<string> Periods { get; } = ["Today vs yesterday", "Last 7 days vs the 7 before", "Last 30 days vs the 30 before"];

    public ObservableCollection<CompareRow> New { get; } = [];
    public ObservableCollection<CompareRow> Gone { get; } = [];
    public ObservableCollection<CompareRow> More { get; } = [];
    public ObservableCollection<CompareRow> Less { get; } = [];

    public int PeriodIndex
    {
        get => _periodIndex;
        set { if (SetProperty(ref _periodIndex, value)) _ = RefreshAsync(); }
    }

    public bool IncludeWarnings
    {
        get => _includeWarnings;
        set { if (SetProperty(ref _includeWarnings, value)) _ = RefreshAsync(); }
    }

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string LabelA { get => _labelA; set => SetProperty(ref _labelA, value); }
    public string LabelB { get => _labelB; set => SetProperty(ref _labelB, value); }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<CompareRow> OpenCommand { get; }

    public CompareViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenCommand = new RelayCommand<CompareRow>(Open);
        AppState.SourceChanged += (_, _) => { if (_loadedOnce) _ = RefreshAsync(); };
        AppState.MutedChanged += (_, _) => { if (_loadedOnce) _ = RefreshAsync(); };
    }

    public void EnsureLoaded()
    {
        if (!_loadedOnce) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        _loadedOnce = true;
        IsLoading = true;
        Status = "Comparing…";
        try
        {
            var now = DateTime.Now;
            (_a, _b, LabelA, LabelB) = _periodIndex switch
            {
                0 => ((DateTime.Today, now), (DateTime.Today.AddDays(-1), DateTime.Today), "Today", "Yesterday"),
                1 => ((now.AddDays(-7), now), (now.AddDays(-14), now.AddDays(-7)), "Last 7 days", "7 days before"),
                _ => ((now.AddDays(-30), now), (now.AddDays(-60), now.AddDays(-30)), "Last 30 days", "30 days before"),
            };

            var level = _includeWarnings ? Severity.Warning : Severity.Error;
            var logs = new[] { "System", "Application" };
            var tA = EventLogService.QueryLogsAsync(logs, EventLogService.BuildXPath(level, null, null, _a.From, _a.To), 20000, true);
            var tB = EventLogService.QueryLogsAsync(logs, EventLogService.BuildXPath(level, null, null, _b.From, _b.To), 20000, true);
            await Task.WhenAll(tA, tB);

            var a = tA.Result.Where(e => !e.IsHarmless).GroupBy(e => e.TypeKey).ToDictionary(g => g.Key, g => g.ToList());
            var b = tB.Result.Where(e => !e.IsHarmless).GroupBy(e => e.TypeKey).ToDictionary(g => g.Key, g => g.ToList());

            New.Clear(); Gone.Clear(); More.Clear(); Less.Clear();

            foreach (var (key, items) in a.OrderByDescending(x => x.Value.Count))
            {
                var before = b.TryGetValue(key, out var bi) ? bi.Count : 0;
                var row = new CompareRow { Sample = items[0], Now = items.Count, Before = before };
                if (before == 0) New.Add(row);
                else if (items.Count >= before * 1.5 && items.Count - before >= 3) More.Add(row);
                else if (before >= items.Count * 1.5 && before - items.Count >= 3) Less.Add(row);
            }
            foreach (var (key, items) in b.Where(x => !a.ContainsKey(x.Key)).OrderByDescending(x => x.Value.Count))
                Gone.Add(new CompareRow { Sample = items[0], Now = 0, Before = items.Count });

            Status = $"{LabelA}: {tA.Result.Count:N0} events · {LabelB}: {tB.Result.Count:N0} events (harmless excluded)";
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Access denied to the logs.";
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

    private void Open(CompareRow? row)
    {
        if (row is null) return;
        var e = row.Sample;
        var (from, to) = row.Now > 0 ? _a : _b;
        Navigator.OpenEvents(new EventQuerySpec
        {
            Logs = [e.LogName],
            Ids = [e.Id],
            Providers = [e.Provider],
            From = from,
            To = to,
            Group = false,
            Label = $"{e.Provider} · {e.Id} · {(row.Now > 0 ? LabelA : LabelB)}",
        });
    }
}
