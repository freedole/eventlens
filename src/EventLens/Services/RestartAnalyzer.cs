using EventLens.Models;

namespace EventLens.Services;

public enum RestartKind { Planned, WindowsUpdate, Unexpected, BlueScreen, Unknown, Current }

public sealed class RestartInfo
{
    public required DateTime BootTime { get; init; }
    public DateTime? ShutdownTime { get; init; }
    public required RestartKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public TimeSpan? UptimeBefore { get; init; }
    public IReadOnlyList<EventItem> Evidence { get; init; } = [];

    public string KindText => Kind switch
    {
        RestartKind.Planned => "Planned",
        RestartKind.WindowsUpdate => "Windows Update",
        RestartKind.Unexpected => "Unexpected",
        RestartKind.BlueScreen => "Blue screen",
        _ => "Unknown",
    };

    public string Icon => Kind switch
    {
        RestartKind.Planned => "ArrowReset24",
        RestartKind.WindowsUpdate => "ArrowSync24",
        RestartKind.Unexpected => "Power24",
        RestartKind.BlueScreen => "ErrorCircle24",
        _ => "QuestionCircle24",
    };

    public string UptimeText => UptimeBefore is { } u
        ? (u.TotalDays >= 1 ? $"{(int)u.TotalDays} d {u.Hours} h" : $"{u.Hours} h {u.Minutes} min")
        : "-";
}

/// <summary>
/// Classifies past restarts by correlating boot (Kernel-General 12) with shutdown evidence:
/// User32 1074 (planned), Kernel-Power 41 / EventLog 6008 (unexpected), WER 1001 BugCheck (BSOD).
/// </summary>
public static class RestartAnalyzer
{
    private static readonly string[] UpdateProcesses =
        ["TrustedInstaller", "MoUsoCoreWorker", "UsoClient", "wuauclt", "MusNotification", "UpdateOrchestrator", "SIHClient"];

    public static async Task<List<RestartInfo>> AnalyzeAsync(int maxBoots = 15, CancellationToken ct = default)
    {
        var window = TimeSpan.FromDays(120);
        var xpath = EventLogService.BuildXPath(null, window, [12, 13, 41, 6008, 1074, 1001, 6005, 6006]);
        var events = await EventLogService.QueryLogsAsync(["System"], xpath, 3000, true, ct);

        var boots = events
            .Where(e => e.Id == 12 && e.Provider == "Microsoft-Windows-Kernel-General")
            .OrderByDescending(e => e.TimeCreated)
            .Take(maxBoots + 1)
            .ToList();

        // fallback for sources without Kernel-General: Event Log service start
        if (boots.Count == 0)
            boots = events.Where(e => e.Id == 6005 && e.Provider == "EventLog")
                .OrderByDescending(e => e.TimeCreated).Take(maxBoots + 1).ToList();

        var result = new List<RestartInfo>();
        for (int i = 0; i < boots.Count && result.Count < maxBoots; i++)
        {
            var boot = boots[i].TimeCreated;
            var prevBoot = i + 1 < boots.Count ? boots[i + 1].TimeCreated : (DateTime?)null;
            if (prevBoot is null && i > 0) break;

            // Shutdown-side evidence (1074, 13, 6006) is logged before this boot.
            // Crash-side evidence (41, 6008, 1001) is logged shortly AFTER the boot that follows the crash,
            // so it is matched in (prevBoot + 15 min, boot + 15 min] to avoid picking up the previous crash.
            var lower = prevBoot ?? boot.AddDays(-7);
            var crashLower = prevBoot is { } p ? p.AddMinutes(15) : lower;
            if (crashLower > boot) crashLower = lower;
            var crashUpper = boot.AddMinutes(15);

            var before = events.Where(e =>
                    e.Id is 1074 or 13 or 6006
                        ? e.TimeCreated > lower && e.TimeCreated <= boot
                        : e.Id is 41 or 6008 or 1001 && e.TimeCreated > crashLower && e.TimeCreated <= crashUpper)
                .ToList();

            result.Add(Classify(boot, prevBoot, before));
        }
        return result;
    }

    private static RestartInfo Classify(DateTime boot, DateTime? prevBoot, List<EventItem> ev)
    {
        var bsod = ev.FirstOrDefault(e => e.Id == 1001 && e.Provider == "Microsoft-Windows-WER-SystemErrorReporting");
        var kp41 = ev.FirstOrDefault(e => e.Id == 41 && e.Provider == "Microsoft-Windows-Kernel-Power");
        var e6008 = ev.FirstOrDefault(e => e.Id == 6008);
        var planned = ev.Where(e => e.Id == 1074).OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        var cleanShutdown = ev.Where(e => e.Id is 13 or 6006).OrderByDescending(e => e.TimeCreated).FirstOrDefault();

        var evidence = new[] { bsod, kp41, e6008, planned, cleanShutdown }.Where(e => e is not null).Cast<EventItem>()
            .OrderByDescending(e => e.TimeCreated).ToList();

        DateTime? shutdownTime = cleanShutdown?.TimeCreated ?? planned?.TimeCreated;
        TimeSpan? uptime = prevBoot is { } pb && shutdownTime is { } st && st > pb ? st - pb : null;

        if (bsod is not null)
        {
            var code = bsod.Data.FirstOrDefault() ?? "";
            return new RestartInfo
            {
                BootTime = boot, ShutdownTime = shutdownTime, Kind = RestartKind.BlueScreen, UptimeBefore = uptime,
                Title = "Crashed with a blue screen",
                Detail = string.IsNullOrWhiteSpace(code)
                    ? "Windows stopped with a bugcheck. Analyze the minidump to find the driver."
                    : $"Bugcheck {code}. Analyze the minidump (C:\\Windows\\Minidump) to find the driver.",
                Evidence = evidence,
            };
        }

        if (kp41 is not null || e6008 is not null)
        {
            return new RestartInfo
            {
                BootTime = boot, ShutdownTime = null, Kind = RestartKind.Unexpected, UptimeBefore = null,
                Title = "Lost power or was hard-reset",
                Detail = "No clean shutdown was recorded: power loss, the power button was held, or the system froze. " +
                         "Check the PSU/UPS, temperatures and drivers.",
                Evidence = evidence,
            };
        }

        if (planned is not null)
        {
            // 1074 data: [0] process, [1] computer, [2] reason, [3] code, [4] type, [5] comment, [6] user
            var d = planned.Data;
            string Get(int i) => i < d.Count ? d[i] : "";
            var process = Get(0);
            var user = Get(6);
            var reason = Get(2);
            var type = Get(4);
            var procName = System.IO.Path.GetFileName(process.Split(' ')[0]);
            var isUpdate = UpdateProcesses.Any(u => process.Contains(u, StringComparison.OrdinalIgnoreCase))
                           || reason.Contains("Upgrade", StringComparison.OrdinalIgnoreCase)
                           || reason.Contains("Service pack", StringComparison.OrdinalIgnoreCase);

            return new RestartInfo
            {
                BootTime = boot, ShutdownTime = shutdownTime, Kind = isUpdate ? RestartKind.WindowsUpdate : RestartKind.Planned,
                UptimeBefore = uptime,
                Title = isUpdate ? "Restarted by Windows Update" : $"{Cap(string.IsNullOrWhiteSpace(type) ? "restart" : type)} by {Who(user, procName)}",
                Detail = $"Process: {(string.IsNullOrWhiteSpace(procName) ? "?" : procName)}" +
                         (string.IsNullOrWhiteSpace(user) ? "" : $" · User: {user}") +
                         (string.IsNullOrWhiteSpace(reason) ? "" : $" · Reason: {reason}"),
                Evidence = evidence,
            };
        }

        if (cleanShutdown is not null)
        {
            return new RestartInfo
            {
                BootTime = boot, ShutdownTime = shutdownTime, Kind = RestartKind.Planned, UptimeBefore = uptime,
                Title = "Clean shutdown",
                Detail = "Windows shut down normally (no initiating process was logged).",
                Evidence = evidence,
            };
        }

        return new RestartInfo
        {
            BootTime = boot, ShutdownTime = null, Kind = RestartKind.Unknown, UptimeBefore = null,
            Title = "Cause not recorded",
            Detail = "No shutdown events found - the log may have been cleared or rolled over.",
            Evidence = evidence,
        };
    }

    private static string Who(string user, string proc)
    {
        if (!string.IsNullOrWhiteSpace(user) && !user.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)) return user;
        return string.IsNullOrWhiteSpace(proc) ? "the system" : proc;
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}
