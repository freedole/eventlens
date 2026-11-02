using System.Text;
using EventLens.Core.Knowledge;

namespace EventLens.Models;

public enum Severity
{
    Critical = 1,
    Error = 2,
    Warning = 3,
    Information = 4,
    Verbose = 5
}

public sealed class EventItem
{
    public required DateTime TimeCreated { get; init; }
    public required int Id { get; init; }
    public required Severity Severity { get; init; }
    public required string Provider { get; init; }
    public required string LogName { get; init; }
    public string Message { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string? UserId { get; init; }
    public long? RecordId { get; init; }
    public IReadOnlyList<string> Data { get; init; } = [];
    public Explanation? Explanation { get; init; }

    /// <summary>Set by the app when the user marked this event type as harmless.</summary>
    public bool UserMuted { get; set; }

    public string SeverityText => IsHarmless ? "Harmless" : Severity.ToString();
    public bool HasExplanation => !string.IsNullOrEmpty(Explanation?.Title);
    public bool IsHarmless => UserMuted || Explanation?.IsHarmless == true;
    /// <summary>Errors/criticals/warnings that actually matter (used by counters and the health score).</summary>
    public bool IsProblem => !IsHarmless && Severity <= Severity.Warning;

    public string Summary =>
        HasExplanation ? Explanation!.DisplayTitle : FirstLine(Message);

    /// <summary>Same type of event regardless of log: provider + ID.</summary>
    public string TypeKey => $"{Provider}|{Id}";
    public string GroupKey => $"{LogName}|{Provider}|{Id}";

    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{TimeCreated:yyyy-MM-dd HH:mm:ss}  {Severity}  {LogName} / {Provider}  ID {Id}");
        if (!string.IsNullOrEmpty(MachineName)) sb.AppendLine($"Computer: {MachineName}");
        if (HasExplanation)
        {
            sb.AppendLine($"Meaning: {Explanation!.DisplayTitle} - {Explanation.What}");
            if (Explanation.HasFix) sb.AppendLine("What to do: " + string.Join(" ", Explanation.Fix));
        }
        foreach (var c in Explanation?.Codes ?? [])
            sb.AppendLine($"{c.Code}: {c.Meaning}");
        sb.AppendLine();
        sb.AppendLine(Message);
        return sb.ToString();
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(no description)";
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

/// <summary>One row in the Events list: a single event or a group of identical repeats.</summary>
public sealed class EventRow
{
    public required EventItem First { get; init; }   // most recent occurrence
    public required IReadOnlyList<EventItem> All { get; init; }

    public int Count => All.Count;
    public bool IsGroup => All.Count > 1;
    public DateTime LastSeen => First.TimeCreated;
    public DateTime FirstSeen => All[^1].TimeCreated;
    public string CountText => IsGroup ? $"×{Count}" : "";
    public string OccurrenceText => IsGroup
        ? $"Occurred {Count} times · first {FirstSeen:g} · last {LastSeen:g}"
        : "";

    // passthroughs for DataGrid sorting/binding
    public DateTime TimeCreated => First.TimeCreated;
    public int Id => First.Id;
    public Severity Severity => First.Severity;
    public string SeverityText => First.SeverityText;
    public bool IsHarmless => First.IsHarmless;
    public string Provider => First.Provider;
    public string Summary => First.Summary;
}
