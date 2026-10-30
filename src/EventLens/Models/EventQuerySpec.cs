namespace EventLens.Models;

/// <summary>Everything needed to open the Events page with a specific filter.</summary>
public sealed record EventQuerySpec
{
    public string[] Logs { get; init; } = ["System", "Application"];
    public Severity? MaxLevel { get; init; }
    public TimeSpan? Window { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int[]? Ids { get; init; }
    public string[]? Providers { get; init; }
    public string? Label { get; init; }
    public long? SelectRecordId { get; init; }
    public bool? Group { get; init; }
}
