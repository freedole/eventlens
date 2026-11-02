using System.Text.Json.Serialization;

namespace EventLens.Core.Knowledge;

/// <summary>How much an event matters, independent of the Windows "Level".</summary>
public enum KbClass
{
    Harmless,
    Info,
    Attention,
    Critical,
}

/// <summary>One knowledge-base entry as stored in knowledge/*.json.</summary>
public sealed class KbEntry
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("ids")] public int[]? Ids { get; set; }
    [JsonPropertyName("class")] public string Class { get; set; } = "info";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("what")] public string What { get; set; } = "";
    [JsonPropertyName("causes")] public string[] Causes { get; set; } = [];
    [JsonPropertyName("fix")] public string[] Fix { get; set; } = [];
    [JsonPropertyName("related")] public int[] Related { get; set; } = [];
    [JsonPropertyName("link")] public string? Link { get; set; }

    [JsonIgnore]
    public KbClass ClassValue => Class.ToLowerInvariant() switch
    {
        "harmless" => KbClass.Harmless,
        "attention" => KbClass.Attention,
        "critical" => KbClass.Critical,
        _ => KbClass.Info,
    };

    [JsonIgnore]
    public IEnumerable<int> AllIds => Ids is { Length: > 0 } ? Ids : Id is { } i ? [i] : [];
}

public sealed class KbManifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("files")] public string[] Files { get; set; } = [];
}

public sealed class KbFile
{
    [JsonPropertyName("entries")] public List<KbEntry> Entries { get; set; } = [];
}
