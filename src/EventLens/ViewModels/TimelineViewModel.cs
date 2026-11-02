using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;

namespace EventLens.ViewModels;

public sealed class TimelineEntry
{
    public required EventItem Event { get; init; }
    public required string LogShort { get; init; }
    public bool IsCenter { get; init; }
    public string Offset { get; init; } = "";
}

/// <summary>"What happened at …": every log on one time line around a moment.</summary>
public sealed class TimelineViewModel : ObservableObject
{
    private DateTime? _date = DateTime.Today;
    private string _time = DateTime.Now.ToString("HH:mm");
    private int _spanIndex = 1;
    private bool _includeInfo = true;
    private bool _hideHarmless = true;
    private bool _isLoading;
    private string _status = "Pick a moment and press Show.";
    private TimelineEntry? _selected;
    private EventDetailViewModel? _detail;
    private DateTime _center;
    private List<TimelineEntry> _all = [];
    private CancellationTokenSource? _cts;

    public IReadOnlyList<string> Spans { get; } = ["± 5 minutes", "± 15 minutes", "± 30 minutes", "± 1 hour", "± 3 hours"];
    private static readonly int[] SpanMinutes = [5, 15, 30, 60, 180];

    public ObservableCollection<TimelineEntry> Entries { get; } = [];

    public DateTime? Date { get => _date; set => SetProperty(ref _date, value); }
    public string Time { get => _time; set => SetProperty(ref _time, value); }
    public int SpanIndex { get => _spanIndex; set => SetProperty(ref _spanIndex, value); }

    public bool IncludeInfo
    {
        get => _includeInfo;
        set { if (SetProperty(ref _includeInfo, value)) ApplyFilter(); }
    }

    public bool HideHarmless
    {
        get => _hideHarmless;
        set { if (SetProperty(ref _hideHarmless, value)) ApplyFilter(); }
    }

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public TimelineEntry? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
                Detail = value is null ? null : new EventDetailViewModel(value.Event);
        }
    }

    public EventDetailViewModel? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public IAsyncRelayCommand ShowCommand { get; }
    public IRelayCommand NowCommand { get; }

    public TimelineViewModel()
    {
        ShowCommand = new AsyncRelayCommand(ShowFromInputsAsync);
        NowCommand = new RelayCommand(() =>
        {
            Date = DateTime.Today;
            Time = DateTime.Now.ToString("HH:mm");
            _ = ShowFromInputsAsync();
        });
        AppState.MutedChanged += (_, _) =>
        {
            foreach (var e in _all) e.Event.UserMuted = AppState.IsMuted(e.Event.TypeKey);
            ApplyFilter();
        };
    }

    public Task ShowAsync(DateTime at, int minutes)
    {
        Date = at.Date;
        Time = at.ToString("HH:mm");
        var idx = Array.FindIndex(SpanMinutes, m => m >= minutes);
        SpanIndex = idx < 0 ? SpanMinutes.Length - 1 : idx;
        return LoadAsync(at, SpanMinutes[SpanIndex]);
    }

    private Task ShowFromInputsAsync()
    {
        if (Date is not { } d) { Status = "Pick a date."; return Task.CompletedTask; }
        if (!TimeSpan.TryParseExact(Time.Trim(), ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out var t))
        {
            Status = "Time must look like 14:30.";
            return Task.CompletedTask;
        }
        return LoadAsync(d.Date + t, SpanMinutes[Math.Clamp(SpanIndex, 0, SpanMinutes.Length - 1)]);
    }

    private async Task LoadAsync(DateTime at, int minutes)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _center = at;
        IsLoading = true;
        Status = $"Reading all logs from {at.AddMinutes(-minutes):g} to {at.AddMinutes(minutes):t}…";
        Entries.Clear();
        Selected = null;

        var from = at.AddMinutes(-minutes);
        var to = at.AddMinutes(minutes);
        var xpath = EventLogService.BuildXPath(Severity.Information, null, null, from, to);
        string[] channels = AppState.Source.Kind == SourceKind.File ? new[] { "(file)" } : EventLogService.TimelineChannels.ToArray();

        var results = new List<EventItem>();
        var skipped = new List<string>();
        try
        {
            var tasks = channels.Select(async ch =>
            {
                try { return await EventLogService.QueryLogsAsync([ch], xpath, 3000, true, cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (UnauthorizedAccessException) { lock (skipped) skipped.Add(Short(ch)); return new List<EventItem>(); }
                catch (EventLogNotFoundException) { return new List<EventItem>(); }
                catch (EventLogException) { return new List<EventItem>(); }
                catch (Exception) { return new List<EventItem>(); }
            }).ToList();

            foreach (var list in await Task.WhenAll(tasks)) results.AddRange(list);
            if (cts.IsCancellationRequested) return;

            _all = results
                .OrderBy(e => e.TimeCreated)
                .Select(e => new TimelineEntry
                {
                    Event = e,
                    LogShort = Short(e.LogName),
                    Offset = Offset(e.TimeCreated - at),
                })
                .ToList();
            ApplyFilter();

            Status = $"{_all.Count:N0} events from {channels.Length} logs around {at:g}"
                     + (skipped.Count > 0 ? $" · no access to: {string.Join(", ", skipped)}" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var e in _all)
        {
            if (!_includeInfo && e.Event.Severity > Severity.Warning) continue;
            if (_hideHarmless && e.Event.IsHarmless) continue;
            Entries.Add(e);
        }
        // select the event closest to the chosen moment
        Selected = Entries.OrderBy(e => Math.Abs((e.Event.TimeCreated - _center).Ticks)).FirstOrDefault();
    }

    private static string Offset(TimeSpan d)
    {
        var sign = d < TimeSpan.Zero ? "−" : "+";
        d = d.Duration();
        return d.TotalMinutes < 1 ? $"{sign}{d.Seconds}s" : d.TotalHours < 1 ? $"{sign}{(int)d.TotalMinutes}m {d.Seconds:00}s" : $"{sign}{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    private static string Short(string log)
    {
        if (!log.StartsWith("Microsoft-Windows-")) return log;
        var s = log["Microsoft-Windows-".Length..];
        return s.Split('/')[0].Replace("TerminalServices-", "RDP ").Replace("Windows Defender", "Defender");
    }
}
