using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using EventLens.Core.Knowledge;
using EventLens.Models;

namespace EventLens.Services;

public static class EventLogService
{
    public static readonly string[] StandardLogs = ["System", "Application", "Security", "Setup"];

    /// <summary>Builds an XPath filter. Window = relative to now; From/To = absolute (local time).</summary>
    public static string BuildXPath(Severity? maxSeverity, TimeSpan? window, IEnumerable<int>? ids = null,
        DateTime? from = null, DateTime? to = null, IEnumerable<string>? providers = null)
    {
        var parts = new List<string>();

        if (maxSeverity is { } sev)
        {
            // Level 0 = LogAlways (used by audit events) counts as Information
            var levels = Enumerable.Range(1, (int)sev).Select(l => $"Level={l}").ToList();
            if (sev >= Severity.Information) levels.Add("Level=0");
            parts.Add("(" + string.Join(" or ", levels) + ")");
        }

        if (from is not null || to is not null)
        {
            var t = new List<string>();
            if (from is { } f) t.Add($"@SystemTime>='{Utc(f)}'");
            if (to is { } u) t.Add($"@SystemTime<'{Utc(u)}'");
            parts.Add($"TimeCreated[{string.Join(" and ", t)}]");
        }
        else if (window is { } w)
        {
            parts.Add($"TimeCreated[timediff(@SystemTime) <= {(long)w.TotalMilliseconds}]");
        }

        var idList = ids?.ToList();
        if (idList is { Count: > 0 })
            parts.Add("(" + string.Join(" or ", idList.Select(i => $"EventID={i}")) + ")");

        var provList = providers?.ToList();
        if (provList is { Count: > 0 })
            parts.Add("Provider[" + string.Join(" or ", provList.Select(p => $"@Name='{p.Replace("'", "")}'")) + "]");

        return parts.Count == 0 ? "*" : $"*[System[{string.Join(" and ", parts)}]]";
    }

    public static string BuildXPath(EventQuerySpec spec) =>
        BuildXPath(spec.MaxLevel, spec.Window, spec.Ids, spec.From, spec.To, spec.Providers);

    private static string Utc(DateTime local) =>
        local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>Reads one log (newest first). onBatch is called with partial results for progressive UI.</summary>
    public static Task<List<EventItem>> QueryAsync(
        string log, string xpath, int max, bool withMessages = true,
        CancellationToken ct = default, Action<IReadOnlyList<EventItem>>? onBatch = null, EventSource? source = null)
    {
        source ??= AppState.Source;
        return Task.Run(() =>
        {
            var result = new List<EventItem>(Math.Min(max, 4096));
            var batch = new List<EventItem>(256);
            var query = source.CreateQuery(log, xpath);

            using var reader = new EventLogReader(query);
            EventRecord? rec;
            while (result.Count < max && (rec = reader.ReadEvent()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                EventItem item;
                using (rec)
                    item = Map(rec, log, withMessages);
                result.Add(item);

                if (onBatch is not null)
                {
                    batch.Add(item);
                    if (batch.Count >= 200)
                    {
                        onBatch(batch.ToArray());
                        batch.Clear();
                    }
                }
            }
            if (onBatch is not null && batch.Count > 0) onBatch(batch.ToArray());
            return result;
        }, ct);
    }

    /// <summary>Reads several logs and merges them newest-first. In file mode the file is read once.</summary>
    public static async Task<List<EventItem>> QueryLogsAsync(
        IEnumerable<string> logs, string xpath, int max, bool withMessages = true,
        CancellationToken ct = default, Action<IReadOnlyList<EventItem>>? onBatch = null)
    {
        var source = AppState.Source;
        List<string> list = source.IgnoresLogName ? new List<string> { "(file)" } : logs.Distinct().ToList();
        var all = new List<EventItem>();
        Exception? firstError = null;

        foreach (var log in list)
        {
            try
            {
                all.AddRange(await QueryAsync(log, xpath, max, withMessages, ct, onBatch, source));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (list.Count > 1 && ex is UnauthorizedAccessException or EventLogNotFoundException)
            {
                firstError ??= ex; // e.g. Security without elevation - keep the other logs
            }
        }

        if (all.Count == 0 && firstError is not null) throw firstError;
        return all.OrderByDescending(e => e.TimeCreated).Take(max).ToList();
    }

    private static EventItem Map(EventRecord rec, string log, bool full)
    {
        var provider = rec.ProviderName ?? "";
        string message = "";
        var data = ReadData(rec);   // cheap; needed for templates, rules and harmless classification

        if (full)
        {
            try { message = rec.FormatDescription() ?? ""; }
            catch { /* provider metadata missing */ }
            if (string.IsNullOrEmpty(message))
                message = string.Join(Environment.NewLine, data);
        }

        var item = new EventItem
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
            Data = data,
            Explanation = KnowledgeBase.Explain(provider, rec.Id, data, message),
        };
        item.UserMuted = AppState.IsMuted(item.TypeKey);
        return item;
    }

    private static IReadOnlyList<string> ReadData(EventRecord rec)
    {
        try
        {
            return rec.Properties?.Select(p => p.Value?.ToString() ?? "").ToArray() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private static Severity ToSeverity(byte? level) => level switch
    {
        1 => Severity.Critical,
        2 => Severity.Error,
        3 => Severity.Warning,
        5 => Severity.Verbose,
        _ => Severity.Information,
    };

    /// <summary>Quick connectivity/permission check for a source, with a timeout (remote RPC can hang).</summary>
    public static async Task TestAsync(EventSource source, int timeoutSeconds = 20)
    {
        var work = Task.Run(() =>
        {
            var q = source.CreateQuery("System", "*");
            using var reader = new EventLogReader(q);
            using var rec = reader.ReadEvent();
        });
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != work)
            throw new TimeoutException($"No answer from {source.Computer} within {timeoutSeconds} seconds.");
        await work;
    }

    /// <summary>Exports matching events of one log (or the opened file) to an .evtx file, with messages.</summary>
    public static Task ExportEvtxAsync(string log, string xpath, string targetPath) => Task.Run(() =>
    {
        var src = AppState.Source;
        using var session = src.CreateSession();
        if (src.Kind == SourceKind.File)
            session.ExportLogAndMessages(src.FilePath!, PathType.FilePath, xpath, targetPath);
        else
            session.ExportLogAndMessages(log, PathType.LogName, xpath, targetPath);
    });

    /// <summary>Lists event log channels that exist and contain events (for the Timeline).</summary>
    public static IReadOnlyList<string> TimelineChannels { get; } =
    [
        "System", "Application", "Security", "Setup",
        "Microsoft-Windows-Windows Defender/Operational",
        "Microsoft-Windows-WindowsUpdateClient/Operational",
        "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational",
        "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational",
        "Microsoft-Windows-Diagnostics-Performance/Operational",
        "Microsoft-Windows-WLAN-AutoConfig/Operational",
        "Microsoft-Windows-Kernel-PnP/Configuration",
        "Microsoft-Windows-TaskScheduler/Operational",
        "Microsoft-Windows-Time-Service/Operational",
        "Microsoft-Windows-GroupPolicy/Operational",
    ];
}
