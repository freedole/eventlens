using System.Diagnostics.Eventing.Reader;
using EventLens.Models;

namespace EventLens.Services;

public static class EventLogService
{
    public static readonly string[] StandardLogs = ["System", "Application", "Security", "Setup"];

    /// <summary>Builds an XPath filter for level and time window.</summary>
    public static string BuildXPath(Severity? maxSeverity, TimeSpan? window, IEnumerable<int>? ids = null)
    {
        var parts = new List<string>();

        if (maxSeverity is { } sev)
        {
            // Level 0 = LogAlways (used by audit events) counts as Information
            var levels = Enumerable.Range(1, (int)sev).Select(l => $"Level={l}").ToList();
            if (sev >= Severity.Information) levels.Add("Level=0");
            parts.Add("(" + string.Join(" or ", levels) + ")");
        }

        if (window is { } w)
            parts.Add($"TimeCreated[timediff(@SystemTime) <= {(long)w.TotalMilliseconds}]");

        if (ids is not null)
        {
            var idList = ids.ToList();
            if (idList.Count > 0)
                parts.Add("(" + string.Join(" or ", idList.Select(i => $"EventID={i}")) + ")");
        }

        return parts.Count == 0 ? "*" : $"*[System[{string.Join(" and ", parts)}]]";
    }

    public static Task<List<EventItem>> QueryAsync(
        string log, string xpath, int max, bool withMessages = true, string? computer = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var result = new List<EventItem>(Math.Min(max, 4096));
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            if (!string.IsNullOrWhiteSpace(computer))
                query.Session = new EventLogSession(computer);

            using var reader = new EventLogReader(query);
            EventRecord? rec;
            while (result.Count < max && (rec = reader.ReadEvent()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                using (rec)
                    result.Add(Map(rec, log, withMessages));
            }
            return result;
        }, ct);
    }

    public static Task<int> CountAsync(string log, string xpath, int cap = 10000, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, xpath);
                using var reader = new EventLogReader(query);
                int n = 0;
                EventRecord? rec;
                while (n < cap && (rec = reader.ReadEvent()) is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    rec.Dispose();
                    n++;
                }
                return n;
            }
            catch (EventLogNotFoundException) { return 0; }
            catch (UnauthorizedAccessException) { return -1; }
        }, ct);
    }

    private static EventItem Map(EventRecord rec, string log, bool withMessage)
    {
        var provider = rec.ProviderName ?? "";
        string message = "";
        if (withMessage)
        {
            try { message = rec.FormatDescription() ?? ""; }
            catch { /* provider metadata missing */ }
            if (string.IsNullOrEmpty(message))
                message = FallbackMessage(rec);
        }

        return new EventItem
        {
            TimeCreated = rec.TimeCreated ?? DateTime.MinValue,
            Id = rec.Id,
            Severity = ToSeverity(rec.Level),
            Provider = provider,
            LogName = rec.LogName ?? log,
            Message = message.Trim(),
            MachineName = rec.MachineName ?? "",
            UserId = rec.UserId?.Value,
            RecordId = rec.RecordId,
            Explanation = EventKnowledge.Lookup(provider, rec.Id),
        };
    }

    private static string FallbackMessage(EventRecord rec)
    {
        try
        {
            var props = rec.Properties?.Select(p => p.Value?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s));
            return props is null ? "" : string.Join(Environment.NewLine, props);
        }
        catch { return ""; }
    }

    private static Severity ToSeverity(byte? level) => level switch
    {
        1 => Severity.Critical,
        2 => Severity.Error,
        3 => Severity.Warning,
        5 => Severity.Verbose,
        _ => Severity.Information,
    };
}
