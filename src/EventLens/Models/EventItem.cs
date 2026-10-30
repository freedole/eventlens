using System.Text;

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

    public string SeverityText => Severity.ToString();
    public string Summary => Explanation?.Title ?? FirstLine(Message);
    public bool HasExplanation => Explanation is not null;
    public string GroupKey => $"{LogName}|{Provider}|{Id}";

    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{TimeCreated:yyyy-MM-dd HH:mm:ss}  {Severity}  {LogName} / {Provider}  ID {Id}");
        if (!string.IsNullOrEmpty(MachineName)) sb.AppendLine($"Computer: {MachineName}");
        if (Explanation is not null)
        {
            sb.AppendLine($"Meaning: {Explanation.Title} - {Explanation.What}");
            sb.AppendLine($"What to do: {Explanation.Action}");
        }
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

public sealed record Explanation(string Title, string What, string Action, string Category);

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
    public string Provider => First.Provider;
    public string Summary => First.Summary;
}
