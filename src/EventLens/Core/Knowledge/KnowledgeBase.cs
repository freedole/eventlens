using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EventLens.Core.Knowledge;

/// <summary>What EventLens knows about one concrete event (knowledge base + decoded codes + filled summary).</summary>
public sealed class Explanation
{
    public required string Title { get; init; }
    public required string What { get; init; }
    public required string Category { get; init; }
    public required KbClass Class { get; init; }
    public IReadOnlyList<string> Causes { get; init; } = [];
    public IReadOnlyList<string> Fix { get; init; } = [];
    public IReadOnlyList<int> Related { get; init; } = [];
    public string? Link { get; init; }
    /// <summary>Title with this event's own data filled in, e.g. "Service "Spooler" crashed (3rd time)".</summary>
    public string? Summary { get; init; }
    public IReadOnlyList<DecodedCode> Codes { get; set; } = [];

    public bool IsHarmless => Class == KbClass.Harmless;
    public string ClassText => Class.ToString();
    public bool HasCauses => Causes.Count > 0;
    public bool HasFix => Fix.Count > 0;
    public bool HasCodes => Codes.Count > 0;
    public bool HasLink => !string.IsNullOrEmpty(Link);
    public bool HasRelated => Related.Count > 0;
    public string RelatedText => string.Join(", ", Related);
    /// <summary>Fix steps as one block of text (reports, AI context, older bindings).</summary>
    public string Action => Fix.Count == 0 ? "" : string.Join(" ", Fix);
    public string DisplayTitle => Summary ?? Title;
}

/// <summary>
/// Loads the knowledge base (built-in JSON, optionally replaced by a newer copy synced from GitHub)
/// and explains events with it.
/// </summary>
public static partial class KnowledgeBase
{
    private static Dictionary<(string Provider, int Id), KbEntry> _exact = new();
    private static Dictionary<(string Provider, int Id), KbEntry> _normalized = new();
    private static readonly object Gate = new();
    private static bool _loaded;

    public static int Count { get; private set; }
    public static int Version { get; private set; }
    public static string Updated { get; private set; } = "";
    public static string Source { get; private set; } = "built-in";

    /// <summary>Folder used by the online updater; null = built-in only.</summary>
    public static string? CacheFolder { get; set; }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var (manifest, files) = LoadEmbedded();
            var source = "built-in";

            var cached = CacheFolder is null ? null : TryLoadFolder(CacheFolder);
            if (cached is { } c && c.Manifest.Version > manifest.Version)
            {
                manifest = c.Manifest;
                files = c.Files;
                source = "updated from GitHub";
            }

            Apply(manifest, files, source);
            _loaded = true;
        }
    }

    /// <summary>Swaps in a newly downloaded knowledge base.</summary>
    public static void Replace(KbManifest manifest, IReadOnlyList<KbFile> files, string source)
    {
        lock (Gate)
        {
            Apply(manifest, files, source);
            _loaded = true;
        }
    }

    public static IEnumerable<KbEntry> AllEntries
    {
        get
        {
            EnsureLoaded();
            return _exact.Values.Distinct();
        }
    }

    private static void Apply(KbManifest manifest, IReadOnlyList<KbFile> files, string source)
    {
        var exact = new Dictionary<(string, int), KbEntry>();
        var norm = new Dictionary<(string, int), KbEntry>();
        var entries = 0;
        foreach (var f in files)
        foreach (var e in f.Entries)
        {
            entries++;
            foreach (var id in e.AllIds)
            {
                exact[(e.Provider.ToLowerInvariant(), id)] = e;
                norm[(Normalize(e.Provider), id)] = e;
            }
        }
        _exact = exact;
        _normalized = norm;
        Count = entries;
        Version = manifest.Version;
        Updated = manifest.Updated;
        Source = source;
    }

    /// <summary>"Microsoft-Windows-Kernel-Power" and "Kernel-Power" are the same provider for lookup.</summary>
    public static string Normalize(string provider)
    {
        var p = provider.Trim().ToLowerInvariant();
        if (p.StartsWith("microsoft-windows-")) p = p["microsoft-windows-".Length..];
        return p.Replace(" ", "").Replace("-", "");
    }

    public static KbEntry? Find(string provider, int id)
    {
        EnsureLoaded();
        if (_exact.TryGetValue((provider.ToLowerInvariant(), id), out var e)) return e;
        if (_normalized.TryGetValue((Normalize(provider), id), out e)) return e;
        return null;
    }

    /// <summary>Builds the explanation for a concrete event; null when the event is unknown and has no decodable codes.</summary>
    public static Explanation? Explain(string provider, int id, IReadOnlyList<string> data, string message)
    {
        var e = Find(provider, id);
        var codes = DecodeCodes(provider, id, data, message);

        if (e is null)
        {
            return codes.Count == 0
                ? null
                : new Explanation
                {
                    Title = "",
                    What = "",
                    Category = "",
                    Class = KbClass.Info,
                    Codes = codes,
                };
        }

        return new Explanation
        {
            Title = e.Title,
            What = e.What,
            Category = e.Category,
            Class = e.ClassValue,
            Causes = e.Causes,
            Fix = e.Fix,
            Related = e.Related,
            Link = e.Link ?? DefaultLink(provider, id),
            Summary = string.IsNullOrEmpty(e.Summary) ? null : Fill(e.Summary, data),
            Codes = codes,
        };
    }

    private static string? DefaultLink(string provider, int id) =>
        Normalize(provider) == "securityauditing"
            ? $"https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-{id}"
            : null;

    // ---------- codes ----------

    private static List<DecodedCode> DecodeCodes(string provider, int id, IReadOnlyList<string> data, string message)
    {
        var list = new List<DecodedCode>();

        // Kernel-Power 41: BugcheckCode is a decimal number in data[0]
        if (id == 41 && Normalize(provider) == "kernelpower" && data.Count > 0
            && uint.TryParse(data[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bc) && bc != 0)
        {
            var d = ErrorCodes.Bugcheck(bc);
            list.Add(d ?? new DecodedCode($"0x{bc:X8}", "Blue screen stop code"));
        }

        // WER 1001 / text: "The computer has rebooted from a bugcheck. The bugcheck was: 0x0000009f (...)"
        var bcMatch = BugcheckTextRegex().Match(message);
        if (bcMatch.Success && ErrorCodes.TryParseHex(bcMatch.Groups[1].Value, out var bc2))
        {
            var d = ErrorCodes.Bugcheck(bc2);
            if (d is not null && list.All(x => x.Code != d.Code)) list.Add(d);
        }

        foreach (var d in ErrorCodes.DecodeAll(message + " " + string.Join(" ", data)))
            if (list.All(x => x.Code != d.Code)) list.Add(d);

        return list;
    }

    [GeneratedRegex(@"bug ?check (?:was|code)[: ]+(0x[0-9A-Fa-f]{8})", RegexOptions.IgnoreCase)]
    private static partial Regex BugcheckTextRegex();

    // ---------- templates ----------

    [GeneratedRegex(@"\{(\d+)(?:\|([a-z]+))?\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Fills "{0}", "{5|logontype}", "{0|bugcheck}", "{9|status}", "{1|ordinal}" from the event data.
    /// Returns null if any placeholder is missing or empty (the caller then shows the plain title).
    /// </summary>
    public static string? Fill(string template, IReadOnlyList<string> data)
    {
        var ok = true;
        var result = PlaceholderRegex().Replace(template, m =>
        {
            var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var raw = index < data.Count ? data[index]?.Trim() ?? "" : "";
            if (raw.Length == 0 || raw == "-") { ok = false; return ""; }
            var value = m.Groups[2].Success ? Format(raw, m.Groups[2].Value) : Shorten(raw);
            if (string.IsNullOrEmpty(value)) { ok = false; return ""; }
            return value;
        });
        return ok ? result : null;
    }

    private static string? Format(string raw, string filter)
    {
        switch (filter)
        {
            case "logontype":
                return ErrorCodes.LogonType(raw);
            case "bugcheck":
                if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                    return dec == 0 ? null : ErrorCodes.BugcheckName(dec) ?? $"0x{dec:X}";
                if (ErrorCodes.TryParseHex(raw, out var hex))
                    return hex == 0 ? null : ErrorCodes.BugcheckName(hex) ?? $"0x{hex:X}";
                return raw;
            case "status":
                return ErrorCodes.TryParseHex(raw, out var st) ? ErrorCodes.Describe(st)?.Meaning ?? raw : raw;
            case "ordinal":
                return int.TryParse(raw, out var n) ? Ordinal(n) : raw;
            case "file":
            {
                var path = raw;
                var paren = path.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) path = path[..paren];
                path = path.Trim().Trim('"').TrimEnd('\\', '/');
                var slash = path.LastIndexOfAny(['\\', '/']);
                return slash >= 0 ? path[(slash + 1)..] : path;
            }
            case "cap":
                return raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw[1..];
            default:
                return raw;
        }
    }

    private static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    // ---------- loading ----------

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static KbManifest ParseManifest(string json) =>
        JsonSerializer.Deserialize<KbManifest>(json, Json) ?? throw new InvalidDataException("Empty manifest");

    public static KbFile ParseFile(string json) =>
        JsonSerializer.Deserialize<KbFile>(json, Json) ?? throw new InvalidDataException("Empty knowledge file");

    private static (KbManifest, List<KbFile>) LoadEmbedded()
    {
        var asm = typeof(KnowledgeBase).Assembly;
        var manifest = ParseManifest(ReadResource(asm, "kb.manifest.json"));
        var files = manifest.Files.Select(f => ParseFile(ReadResource(asm, "kb." + f))).ToList();
        return (manifest, files);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new FileNotFoundException("Missing knowledge resource " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static (KbManifest Manifest, List<KbFile> Files)? TryLoadFolder(string folder)
    {
        try
        {
            var mPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(mPath)) return null;
            var manifest = ParseManifest(File.ReadAllText(mPath));
            var files = manifest.Files.Select(f => ParseFile(File.ReadAllText(Path.Combine(folder, f)))).ToList();
            return files.Sum(f => f.Entries.Count) > 0 ? (manifest, files) : null;
        }
        catch
        {
            return null; // corrupt cache: fall back to built-in
        }
    }
}
