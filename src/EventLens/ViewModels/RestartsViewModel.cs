using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;
using EventLens.Views;

namespace EventLens.ViewModels;

public sealed class RestartsViewModel : ObservableObject
{
    private bool _isLoading;
    private bool _loadedOnce;
    private string _status = "";
    private RestartInfo? _latest;

    public ObservableCollection<RestartInfo> Items { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public RestartInfo? Latest
    {
        get => _latest;
        set { if (SetProperty(ref _latest, value)) OnPropertyChanged(nameof(HasLatest)); }
    }
    public bool HasLatest => _latest is not null;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<RestartInfo> OpenEventsCommand { get; }
    public IAsyncRelayCommand<RestartInfo> AskAiCommand { get; }

    public RestartsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenEventsCommand = new RelayCommand<RestartInfo>(OpenEvents);
        AskAiCommand = new AsyncRelayCommand<RestartInfo>(AskAiAsync);
        AppState.SourceChanged += (_, _) => { if (_loadedOnce) _ = RefreshAsync(); };
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
        Status = "Analyzing restarts…";
        try
        {
            var list = await RestartAnalyzer.AnalyzeAsync(15);
            Items.Clear();
            foreach (var r in list) Items.Add(r);
            Latest = list.FirstOrDefault();

            var unexpected = list.Count(r => r.Kind is RestartKind.Unexpected or RestartKind.BlueScreen);
            Status = list.Count == 0
                ? "No restarts found in the last 120 days."
                : $"{list.Count} restarts analyzed · {unexpected} unexpected";
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Access denied to the System log.";
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

    /// <summary>Everything in System + Application from 30 min before the shutdown until 10 min after boot.</summary>
    private static EventQuerySpec SpecFor(RestartInfo r)
    {
        var start = (r.ShutdownTime ?? r.Evidence.LastOrDefault()?.TimeCreated ?? r.BootTime).AddMinutes(-30);
        if (start > r.BootTime) start = r.BootTime.AddMinutes(-30);
        return new EventQuerySpec
        {
            Logs = ["System", "Application"],
            MaxLevel = Severity.Information,
            From = start,
            To = r.BootTime.AddMinutes(10),
            Group = false,
            Label = $"Around the restart on {r.BootTime:d MMM HH:mm}",
        };
    }

    private void OpenEvents(RestartInfo? r)
    {
        if (r is null) return;
        // centre the timeline on the shutdown (or the boot if no shutdown was logged)
        var center = r.ShutdownTime ?? r.Evidence.LastOrDefault()?.TimeCreated ?? r.BootTime;
        var minutes = (int)Math.Clamp((r.BootTime - center).TotalMinutes + 10, 15, 180);
        Navigator.OpenTimeline(center, minutes);
    }

    private async Task AskAiAsync(RestartInfo? r)
    {
        if (r is null) return;
        var spec = SpecFor(r);
        List<EventItem> around;
        try
        {
            around = await EventLogService.QueryLogsAsync(spec.Logs, EventLogService.BuildXPath(Severity.Warning, null, null, spec.From, spec.To), 60);
        }
        catch { around = []; }

        var sb = new StringBuilder();
        sb.AppendLine($"Computer: {AppState.Source.ShortName}");
        sb.AppendLine($"Boot time: {r.BootTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"EventLens classification: {r.KindText} - {r.Title}");
        sb.AppendLine($"Details: {r.Detail}");
        sb.AppendLine($"Uptime before restart: {r.UptimeText}");
        sb.AppendLine();
        sb.AppendLine("SHUTDOWN/BOOT EVIDENCE:");
        foreach (var e in r.Evidence)
            sb.AppendLine($"- {e.TimeCreated:yyyy-MM-dd HH:mm:ss} {e.Provider} ID {e.Id}: {AiService.Short(e.Message, 300)}");
        sb.AppendLine();
        sb.AppendLine("ERRORS AND WARNINGS AROUND THE RESTART:");
        foreach (var e in around.Where(e => !e.IsHarmless).OrderBy(e => e.TimeCreated).TakeLast(20))
            sb.AppendLine($"- {e.TimeCreated:HH:mm:ss} {e.Severity} {e.Provider} {e.Id}: {AiService.Short(e.Message, 160)}");

        AiWindow.Open($"Restart on {r.BootTime:d MMM HH:mm}", sb.ToString(),
            "Why did this computer restart? Give the most likely root cause, how confident you are, and what I should check or change to prevent it.",
            "restart");
    }
}
