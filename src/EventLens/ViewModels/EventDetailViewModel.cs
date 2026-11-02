using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Models;
using EventLens.Services;
using EventLens.Views;

namespace EventLens.ViewModels;

/// <summary>Everything shown in the event detail panel (Events and Timeline pages share it).</summary>
public sealed class EventDetailViewModel : ObservableObject
{
    private string _note;
    private string _noteStatus = "";
    private bool _isMuted;

    public EventItem Event { get; }
    public EventRow? Row { get; }

    public EventDetailViewModel(EventItem e, EventRow? row = null)
    {
        Event = e;
        Row = row;
        _note = HistoryStore.GetNote(NoteKey);
        _isMuted = AppState.IsMuted(e.TypeKey);
        HistoryText = HistoryStore.EventHistoryText(e.TypeKey);

        CopyCommand = new RelayCommand(() => SafeCopy(Event.ToClipboardText(), "Event copied"));
        CopyMessageCommand = new RelayCommand(() => SafeCopy(Event.Message, "Message copied"));
        SearchOnlineCommand = new RelayCommand(SearchOnline);
        AllLikeThisCommand = new RelayCommand(AllLikeThis);
        AroundThisCommand = new RelayCommand(() => Navigator.OpenTimeline(Event.TimeCreated, 10));
        AskAiCommand = new RelayCommand(AskAi);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        SaveNoteCommand = new RelayCommand(SaveNote);
        OpenLinkCommand = new RelayCommand(OpenLink, () => Event.Explanation?.HasLink == true);
    }

    private string NoteKey => "evt:" + Event.TypeKey;

    public string RelativeTime => Relative(Event.TimeCreated);
    public string OccurrenceText => Row?.OccurrenceText ?? "";
    public bool IsGroup => Row?.IsGroup == true;
    public string? HistoryText { get; }
    public bool HasHistory => HistoryText is not null;

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value)) OnPropertyChanged(nameof(MuteText));
        }
    }

    public string MuteText => _isMuted ? "Not harmless" : "Mark as harmless";

    public string Note
    {
        get => _note;
        set { if (SetProperty(ref _note, value)) NoteStatus = ""; }
    }

    public string NoteStatus { get => _noteStatus; private set => SetProperty(ref _noteStatus, value); }

    public IRelayCommand CopyCommand { get; }
    public IRelayCommand CopyMessageCommand { get; }
    public IRelayCommand SearchOnlineCommand { get; }
    public IRelayCommand AllLikeThisCommand { get; }
    public IRelayCommand AroundThisCommand { get; }
    public IRelayCommand AskAiCommand { get; }
    public IRelayCommand ToggleMuteCommand { get; }
    public IRelayCommand SaveNoteCommand { get; }
    public IRelayCommand OpenLinkCommand { get; }

    /// <summary>Raised after copy etc. so the page can show a short status.</summary>
    public static event EventHandler<string>? Status;

    private static void SafeCopy(string text, string done)
    {
        try { Clipboard.SetText(text); Status?.Invoke(null, done); }
        catch { Status?.Invoke(null, "Clipboard is busy - try again"); }
    }

    private void SearchOnline()
    {
        var q = Uri.EscapeDataString($"\"Event ID {Event.Id}\" {Event.Provider}");
        Open($"https://www.google.com/search?q={q}");
    }

    private void OpenLink()
    {
        if (Event.Explanation?.Link is { } link) Open(link);
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    private void AllLikeThis() => Navigator.OpenEvents(new EventQuerySpec
    {
        Logs = [Event.LogName],
        Ids = [Event.Id],
        Providers = [Event.Provider],
        Window = TimeSpan.FromDays(30),
        Label = $"{Event.Provider} · ID {Event.Id} · 30 days",
        Group = false,
    });

    private void AskAi()
    {
        var e = Event;
        var count = Row?.Count ?? 1;
        AiWindow.Open(
            $"{e.Summary}",
            AiService.BuildEventContext(e, count),
            "Explain this Windows event: what happened, the most likely cause, how serious it is, and what I should do.",
            "single",
            async () => AiService.BuildDeepContext(e, await EventsViewModel.NearbyAsync(e.TimeCreated, TimeSpan.FromMinutes(10)), count),
            "Using the event and the errors/warnings around it: what is the most likely root cause, how confident are you, and what should I do?");
    }

    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        AppState.SetMuted(Event.TypeKey, IsMuted);
        Status?.Invoke(null, IsMuted
            ? $"{Event.Provider} {Event.Id} is now treated as harmless (hidden from scores and counts)"
            : $"{Event.Provider} {Event.Id} is no longer treated as harmless");
    }

    private void SaveNote()
    {
        HistoryStore.SetNote(NoteKey, Note);
        NoteStatus = string.IsNullOrWhiteSpace(Note) ? "Note removed" : "Saved - shown next time this event appears";
    }

    public static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 0) return t.ToString("g");
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 31) return $"{(int)d.TotalDays} days ago";
        return t.ToString("d MMM yyyy");
    }
}
