using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Core.Findings;
using EventLens.Models;
using EventLens.Services;
using EventLens.Views;

namespace EventLens.ViewModels;

/// <summary>One finding as shown on screen, with its history and the user's note.</summary>
public sealed class FindingItem : ObservableObject
{
    private string _note;
    private string _noteStatus = "";
    private bool _expanded;

    public Finding Finding { get; }
    public ProblemHistory? History { get; }

    public FindingItem(Finding f)
    {
        Finding = f;
        History = HistoryStore.GetProblem(f.Key);
        _note = HistoryStore.GetNote("finding:" + f.Key);
        SaveNoteCommand = new RelayCommand(() =>
        {
            HistoryStore.SetNote("finding:" + Finding.Key, Note);
            NoteStatus = string.IsNullOrWhiteSpace(Note) ? "Note removed" : "Saved";
            OnPropertyChanged(nameof(HasNote));
        });
    }

    public string Title => Finding.Title;
    public FindingSeverity Severity => Finding.Severity;
    public string SeverityText => Finding.SeverityText;
    public string Icon => Finding.Icon;
    public IReadOnlyList<string> Details => Finding.Details;
    public IReadOnlyList<string> Fix => Finding.Fix;
    public string HistoryText => History is null ? "" : $"{History.SinceText}  {History.TrendText}";
    public bool HasHistory => History is not null && History.Total > Finding.Count;
    public string TrendText => History?.TrendText ?? "";

    public bool Expanded { get => _expanded; set => SetProperty(ref _expanded, value); }

    public string Note
    {
        get => _note;
        set { if (SetProperty(ref _note, value)) NoteStatus = ""; }
    }
    public bool HasNote => !string.IsNullOrWhiteSpace(_note);
    public string NoteStatus { get => _noteStatus; private set => SetProperty(ref _noteStatus, value); }
    public IRelayCommand SaveNoteCommand { get; }
}

public sealed class FindingsViewModel : ObservableObject
{
    private bool _isLoading;
    private bool _loadedOnce;
    private string _status = "";
    private string _summary = "";

    public ObservableCollection<FindingItem> Items { get; } = [];
    public ObservableCollection<FindingItem> Top { get; } = [];
    public ObservableCollection<ProblemHistory> Resolved { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }
    public bool HasItems => Items.Count > 0;
    public bool IsClean => _loadedOnce && !IsLoading && Items.Count == 0;
    public bool HasResolved => Resolved.Count > 0;
    public bool HasMoreThanTop => Items.Count > Top.Count;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<FindingItem> OpenEventsCommand { get; }
    public IRelayCommand<FindingItem> AskAiCommand { get; }
    public IRelayCommand<FindingItem> ToggleCommand { get; }
    public IRelayCommand<FindingItem> TimelineCommand { get; }

    public FindingsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenEventsCommand = new RelayCommand<FindingItem>(f => { if (f is not null) Navigator.OpenEvents(f.Finding.Spec); });
        AskAiCommand = new RelayCommand<FindingItem>(AskAi);
        ToggleCommand = new RelayCommand<FindingItem>(f => { if (f is not null) f.Expanded = !f.Expanded; });
        TimelineCommand = new RelayCommand<FindingItem>(f => { if (f is not null) Navigator.OpenTimeline(f.Finding.LastSeen, 10); });
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
        Status = "Looking for problems in the last 30 days…";
        OnPropertyChanged(nameof(IsClean));
        try
        {
            var window = TimeSpan.FromDays(30);
            const int cap = 20000;

            var tMain = Load(["System", "Application"], EventLogService.BuildXPath(Severity.Warning, window), cap);
            var tInfo = Load(["System"], EventLogService.BuildXPath(null, window, [1074, 7045, 104]), 2000);
            var tSec = Load(["Security"], EventLogService.BuildXPath(null, window, [4625, 4740, 1102]), cap);
            var tDef = Load([FindingsEngine.DefenderLog], EventLogService.BuildXPath(null, window, [1006, 1008, 1116, 1117, 1118, 1119, 5001, 5010, 5012]), 2000);
            await Task.WhenAll(tMain, tInfo, tSec, tDef);

            var all = tMain.Result.Concat(tInfo.Result).Concat(tSec.Result).Concat(tDef.Result).ToList();
            var findings = await Task.Run(() => FindingsEngine.Run(all, DateTime.Now));

            if (AppState.Source.Kind == SourceKind.Local) HistoryStore.RecordFindings(findings);

            Items.Clear();
            Top.Clear();
            foreach (var f in findings) Items.Add(new FindingItem(f));
            foreach (var f in Items.Take(3)) Top.Add(f);

            Resolved.Clear();
            if (AppState.Source.Kind == SourceKind.Local)
                foreach (var r in HistoryStore.GetResolved(findings.Select(f => f.Key))) Resolved.Add(r);

            var crit = findings.Count(f => f.Severity == FindingSeverity.Critical);
            var att = findings.Count(f => f.Severity == FindingSeverity.Attention);
            Summary = findings.Count == 0
                ? "No problems found in the last 30 days."
                : $"{findings.Count} problem(s): {crit} critical, {att} need attention";
            Status = $"Checked {all.Count:N0} events · {DateTime.Now:t}"
                     + (tSec.Result.Count == 0 && !Navigator.Settings.IsAdmin && AppState.Source.Kind == SourceKind.Local
                        ? " · Security log not readable (run as administrator to check logons)" : "");
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsClean));
            OnPropertyChanged(nameof(HasResolved));
            OnPropertyChanged(nameof(HasMoreThanTop));
        }
    }

    private static async Task<List<EventItem>> Load(string[] logs, string xpath, int max)
    {
        try { return await EventLogService.QueryLogsAsync(logs, xpath, max, true); }
        catch { return []; }   // missing log or no permission: that rule simply has no data
    }

    private static void AskAi(FindingItem? item)
    {
        if (item is null) return;
        var f = item.Finding;
        var sb = new StringBuilder();
        sb.AppendLine($"Computer: {AppState.Source.ShortName} ({Environment.OSVersion.VersionString})");
        sb.AppendLine($"Problem found by EventLens: {f.Title}");
        foreach (var d in f.Details) sb.AppendLine("- " + d);
        sb.AppendLine($"Occurrences: {f.Count} between {f.FirstSeen:yyyy-MM-dd HH:mm} and {f.LastSeen:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Example events:");
        foreach (var e in f.Evidence.OrderByDescending(e => e.TimeCreated).Take(3))
            sb.AppendLine($"- {e.TimeCreated:yyyy-MM-dd HH:mm:ss} {e.Provider} {e.Id}: {AiService.Short(e.Message, 400)}");

        AiWindow.Open(f.Title, sb.ToString(),
            "What is the most likely root cause of this problem, how serious is it, and what exactly should I do to fix it?", "finding");
    }
}
