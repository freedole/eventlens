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
    public Explanation? Explanation { get; init; }

    public string SeverityText => Severity.ToString();
    public string Summary => Explanation?.Title ?? FirstLine(Message);
    public bool HasExplanation => Explanation is not null;

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(no description)";
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

public sealed record Explanation(string Title, string What, string Action, string Category);
