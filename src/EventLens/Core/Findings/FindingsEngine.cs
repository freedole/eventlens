using System.Globalization;
using System.Text.RegularExpressions;
using EventLens.Core.Knowledge;
using EventLens.Models;

namespace EventLens.Core.Findings;

public enum FindingSeverity { Info, Attention, Critical }

/// <summary>A problem on this computer, stated as one short thesis with supporting points.</summary>
public sealed class Finding
{
    public required string Key { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public List<string> Details { get; init; } = [];
    public List<string> Fix { get; init; } = [];
    public required IReadOnlyList<EventItem> Evidence { get; init; }
    public required EventQuerySpec Spec { get; init; }

    public int Count => Evidence.Count;
    public DateTime FirstSeen => Evidence.Min(e => e.TimeCreated);
    public DateTime LastSeen => Evidence.Max(e => e.TimeCreated);

    /// <summary>Occurrences per local day - fed into the history database.</summary>
    public Dictionary<DateTime, int> PerDay =>
        Evidence.GroupBy(e => e.TimeCreated.Date).ToDictionary(g => g.Key, g => g.Count());

    public string SeverityText => Severity.ToString();
    public string Icon => Category switch
    {
        "Power" => "Power24",
        "Crash" => "ErrorCircle24",
        "Service" => "Wrench24",
        "Storage" => "Storage24",
        "Hardware" => "Board24",
        "Network" => "Globe24",
        "Domain" => "Organization24",
        "Security" => "Shield24",
        "Antivirus" => "ShieldError24",
        "Update" => "ArrowSync24",
        "Backup" => "Archive24",
        "System" => "Settings24",
        _ => "Info24",
    };
}

/// <summary>
/// Rules that connect events into problems ("findings"). Pure logic: input = events, output = findings.
/// The app feeds it 30 days of System + Application (+ Security / Defender when readable).
/// </summary>
public static partial class FindingsEngine
{
    private static readonly string[] SysApp = ["System", "Application"];

    public static List<Finding> Run(IReadOnlyList<EventItem> events, DateTime now)
    {
        var list = new List<Finding>();
        var window = TimeSpan.FromDays(30);
        var ev = events.Where(e => now - e.TimeCreated <= window && !e.UserMuted).ToList();

        BlueScreens(ev, list);
        UnexpectedShutdowns(ev, list);
        ThirdPartyShutdowns(ev, list);
        HardwareErrors(ev, list);
        DiskHealth(ev, list);
        FileSystem(ev, list);
        GpuResets(ev, list);
        ServiceCrashes(ev, list);
        ServiceStartFailures(ev, list);
        AppCrashes(ev, list);
        AppHangs(ev, list);
        UpdateFailures(ev, list);
        LowMemory(ev, list);
        FailedLogons(ev, list);
        Lockouts(ev, list);
        LogCleared(ev, list);
        NewServices(ev, list);
        Malware(ev, list);
        DefenderOff(ev, list);
        BackupVss(ev, list);
        Domain(ev, list);
        TimeSync(ev, list);
        DnsTimeouts(ev, list);
        IpConflict(ev, list);
        CpuThrottling(ev, list);
        TempProfiles(ev, list);
        CrashDumpConfig(ev, list);

        return list
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.LastSeen)
            .ToList();
    }

    // ---------------- power / crashes ----------------

    private static void BlueScreens(List<EventItem> ev, List<Finding> list)
    {
        var bsod = ev.Where(e => e.Id == 1001 && Is(e, "WER-SystemErrorReporting")).ToList();
        if (bsod.Count == 0) return;

        var codes = bsod
            .Select(e => e.Explanation?.Codes.FirstOrDefault())
            .Where(c => c is not null)
            .GroupBy(c => c!.Code)
            .OrderByDescending(g => g.Count())
            .ToList();

        var details = new List<string> { $"Last one: {bsod.Max(e => e.TimeCreated):g}" };
        foreach (var g in codes.Take(3))
            details.Add($"{g.Count()}× {g.First()!.Meaning}");
        if (codes.Count == 1 && bsod.Count > 1)
            details.Add("Always the same stop code - points to one specific driver or component");

        list.Add(new Finding
        {
            Key = "bsod",
            Severity = FindingSeverity.Critical,
            Category = "Crash",
            Title = $"Windows crashed with a blue screen {Times(bsod.Count)}",
            Details = details,
            Fix = ["Open the minidump in C:\\Windows\\Minidump with WinDbg (!analyze -v) to find the driver", "Update or roll back that driver; update BIOS and chipset", "Test RAM if stop codes vary"],
            Evidence = bsod,
            Spec = Spec(["System"], [1001], "Blue screens", providers: ["Microsoft-Windows-WER-SystemErrorReporting"]),
        });
    }

    private static void UnexpectedShutdowns(List<EventItem> ev, List<Finding> list)
    {
        var kp = ev.Where(e => e.Id == 41 && Is(e, "Kernel-Power")).ToList();
        if (kp.Count == 0) kp = ev.Where(e => e.Id == 6008 && e.Provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase)).ToList();
        if (kp.Count == 0) return;

        // shutdowns with a stop code are already covered by the blue screen finding
        var noBsod = kp.Where(e => e.Id != 41 || e.Data.Count == 0 || e.Data[0] is "0" or "").ToList();
        if (noBsod.Count == 0) return;

        list.Add(new Finding
        {
            Key = "power-loss",
            Severity = noBsod.Count >= 2 ? FindingSeverity.Critical : FindingSeverity.Attention,
            Category = "Power",
            Title = $"PC lost power or was hard-reset {Times(noBsod.Count)}",
            Details =
            [
                $"Last time: {noBsod.Max(e => e.TimeCreated):g}",
                "No blue screen was recorded - the PC simply went off",
                "Typical causes: power outage, PSU/UPS, power button held, overheating, freeze",
            ],
            Fix = ["Check the power supply, UPS and cables", "Check CPU/GPU temperatures under load", "If the PC froze before going off, update chipset/graphics drivers and BIOS"],
            Evidence = noBsod,
            Spec = Spec(["System"], [41, 6008], "Unexpected shutdowns", group: false),
        });
    }

    private static readonly string[] SystemShutdownProcesses =
        ["winlogon.exe", "explorer.exe", "trustedinstaller.exe", "mousocoreworker.exe", "usoclient.exe", "wuauclt.exe",
         "musnotification.exe", "musnotificationux.exe", "sihclient.exe", "shutdown.exe", "svchost.exe", "startmenuexperiencehost.exe",
         "shellexperiencehost.exe", "lsass.exe", "wininit.exe", "services.exe", "runtimebroker.exe", "sihost.exe", "logonui.exe"];

    private static void ThirdPartyShutdowns(List<EventItem> ev, List<Finding> list)
    {
        var requests = ev.Where(e => e.Id == 1074 && e.Provider.Equals("User32", StringComparison.OrdinalIgnoreCase) && e.Data.Count > 0).ToList();
        foreach (var g in requests.GroupBy(e => ProcessName(e.Data[0])))
        {
            if (g.Key.Length == 0 || SystemShutdownProcesses.Contains(g.Key)) continue;
            var items = g.ToList();
            list.Add(new Finding
            {
                Key = "shutdown-by:" + g.Key,
                Severity = FindingSeverity.Attention,
                Category = "Power",
                Title = $"{g.Key} shut down or restarted this PC {Times(items.Count)}",
                Details =
                [
                    $"Last time: {items.Max(e => e.TimeCreated):g}",
                    "A third-party program asked Windows to shut down or restart",
                    "Typical: disk/UPS monitoring tools, backup software, remote management, scripts",
                ],
                Fix = [$"Check the settings and scheduled tasks of the program that owns {g.Key}", "If it is a monitoring tool, find out WHY it shut down (temperature, disk health, battery)"],
                Evidence = items,
                Spec = Spec(["System"], [1074], "Shutdown requests", providers: ["User32"], group: false),
            });
        }
    }

    private static void HardwareErrors(List<EventItem> ev, List<Finding> list)
    {
        var fatal = ev.Where(e => Is(e, "WHEA-Logger") && e.Id is 1 or 18).ToList();
        var corrected = ev.Where(e => Is(e, "WHEA-Logger") && e.Id is 17 or 19 or 47).ToList();
        if (fatal.Count > 0)
        {
            list.Add(new Finding
            {
                Key = "whea-fatal",
                Severity = FindingSeverity.Critical,
                Category = "Hardware",
                Title = $"Fatal hardware error crashed the PC {Times(fatal.Count)}",
                Details = [$"Last time: {fatal.Max(e => e.TimeCreated):g}", "Reported by the CPU/chipset (WHEA) - this is hardware, not software"],
                Fix = ["Reset BIOS to defaults (no overclock/XMP/undervolt)", "Check temperatures and the power supply", "Update BIOS; test RAM"],
                Evidence = fatal,
                Spec = Spec(["System"], [1, 18], "Fatal hardware errors", providers: ["Microsoft-Windows-WHEA-Logger"]),
            });
        }
        if (corrected.Count >= 5)
        {
            list.Add(new Finding
            {
                Key = "whea-corrected",
                Severity = FindingSeverity.Attention,
                Category = "Hardware",
                Title = $"{corrected.Count} corrected hardware errors",
                Details = ["The hardware recovered each time, but frequent errors point to an unstable PCIe link, CPU or RAM"],
                Fix = ["Update BIOS and chipset drivers", "Reseat the device named in the events", "Try disabling PCIe Link State Power Management"],
                Evidence = corrected,
                Spec = Spec(["System"], [17, 19, 47], "Corrected hardware errors", providers: ["Microsoft-Windows-WHEA-Logger"]),
            });
        }
    }

    [GeneratedRegex(@"Harddisk(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiskNumberRegex();

    private static void DiskHealth(List<EventItem> ev, List<Finding> list)
    {
        var bad = ev.Where(e => (e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id is 7 or 11 or 51 or 52 or 153)
                                || (e.Id == 129 && e.Provider is "storahci" or "stornvme" or "iaStorA" or "iaStorAC" or "iaStorAVC")
                                || (e.Id == 11 && e.Provider.Equals("stornvme", StringComparison.OrdinalIgnoreCase)))
                     .ToList();
        if (bad.Count == 0) return;

        foreach (var g in bad.GroupBy(e => DiskLabel(e)))
        {
            var items = g.ToList();
            var severe = items.Any(e => e.Id is 7 or 11 or 52);
            var retries = items.Count(e => e.Id is 153 or 129);
            if (!severe && retries < 3) continue;

            var details = new List<string> { $"Last error: {items.Max(e => e.TimeCreated):g}" };
            if (items.Any(e => e.Id == 52)) details.Add("The disk itself (SMART) predicts it will fail");
            if (items.Any(e => e.Id == 7)) details.Add($"{items.Count(e => e.Id == 7)} unreadable sector reports (bad blocks)");
            if (items.Any(e => e.Id == 11)) details.Add($"{items.Count(e => e.Id == 11)} controller errors");
            if (retries > 0) details.Add($"{retries} timeouts/resets (disk stopped responding)");

            list.Add(new Finding
            {
                Key = "disk:" + g.Key,
                Severity = severe ? FindingSeverity.Critical : FindingSeverity.Attention,
                Category = "Storage",
                Title = severe ? $"{g.Key} shows signs of failure" : $"{g.Key} has I/O timeouts",
                Details = details,
                Fix = ["Back up important data now", "Check SMART health (CrystalDiskInfo or Get-PhysicalDisk | Get-StorageReliabilityCounter)", "Replace the data cable / try another port", "Update SSD firmware and storage driver"],
                Evidence = items,
                Spec = Spec(["System"], [7, 11, 51, 52, 153, 129], "Disk errors"),
            });
        }
    }

    private static string DiskLabel(EventItem e)
    {
        var m = DiskNumberRegex().Match(e.Message + " " + string.Join(" ", e.Data));
        return m.Success ? $"Disk {m.Groups[1].Value}" : "A disk";
    }

    private static void FileSystem(List<EventItem> ev, List<Finding> list)
    {
        var fs = ev.Where(e => Is(e, "Ntfs") && e.Id is 55 or 140).ToList();
        if (fs.Count == 0) return;
        list.Add(new Finding
        {
            Key = "ntfs",
            Severity = FindingSeverity.Critical,
            Category = "Storage",
            Title = $"File system corruption detected {Times(fs.Count)}",
            Details = [$"Last time: {fs.Max(e => e.TimeCreated):g}", "Usually after power loss during writes, or a failing disk"],
            Fix = ["Run chkdsk /f on the affected volume", "Check disk health (SMART)"],
            Evidence = fs,
            Spec = Spec(["System"], [55, 140], "File system errors"),
        });
    }

    private static void GpuResets(List<EventItem> ev, List<Finding> list)
    {
        var tdr = ev.Where(e => (e.Id == 4101 && e.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase))
                                || e.Provider.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase) && e.Severity <= Severity.Error).ToList();
        if (tdr.Count < 2) return;
        list.Add(new Finding
        {
            Key = "gpu",
            Severity = tdr.Count >= 5 ? FindingSeverity.Critical : FindingSeverity.Attention,
            Category = "Hardware",
            Title = $"Graphics driver crashed/reset {Times(tdr.Count)}",
            Details = [$"Last time: {tdr.Max(e => e.TimeCreated):g}", "Screen may go black for a moment or games/apps freeze"],
            Fix = ["Clean-install the GPU driver (or roll back to the previous stable one)", "Check GPU temperature; remove overclock", "Check PSU and GPU power cables"],
            Evidence = tdr,
            Spec = Spec(["System"], [4101, 13, 14, 153], "Graphics driver errors", group: false),
        });
    }

    // ---------------- services / apps ----------------

    private static void ServiceCrashes(List<EventItem> ev, List<Finding> list)
    {
        var crashes = ev.Where(e => e.Id is 7031 or 7034 && Is(e, "Service Control Manager") && e.Data.Count > 0).ToList();
        foreach (var g in crashes.GroupBy(e => e.Data[0]))
        {
            var items = g.ToList();
            if (items.Count < 2) continue;
            list.Add(new Finding
            {
                Key = "svc-crash:" + g.Key,
                Severity = items.Count >= 5 ? FindingSeverity.Critical : FindingSeverity.Attention,
                Category = "Service",
                Title = $"Service \"{g.Key}\" keeps crashing ({items.Count}× in 30 days)",
                Details = [$"Last crash: {items.Max(e => e.TimeCreated):g}", "Windows restarts it each time, but something in it is broken"],
                Fix = ["Update or reinstall the program that owns this service", "Look for Application Error (1000) at the same times - it names the faulting module"],
                Evidence = items,
                Spec = Spec(["System"], [7031, 7034], $"Service crashes: {g.Key}", group: false),
            });
        }
    }

    private static void ServiceStartFailures(List<EventItem> ev, List<Finding> list)
    {
        var fails = ev.Where(e => e.Id is 7000 or 7023 or 7024 or 7038 && Is(e, "Service Control Manager") && e.Data.Count > 0).ToList();
        foreach (var g in fails.GroupBy(e => e.Data[0]))
        {
            var items = g.ToList();
            if (items.Count < 3) continue;
            var logon = items.Any(e => e.Id == 7038);
            list.Add(new Finding
            {
                Key = "svc-start:" + g.Key,
                Severity = FindingSeverity.Attention,
                Category = "Service",
                Title = logon ? $"Service \"{g.Key}\" cannot log on (wrong password?)" : $"Service \"{g.Key}\" fails to start ({items.Count}×)",
                Details = [$"Last time: {items.Max(e => e.TimeCreated):g}"],
                Fix = logon
                    ? ["Update the password on the service's Log On tab (services.msc)"]
                    : ["Check the service in services.msc (startup type, account, dependencies)", "If its program was uninstalled, disable the service"],
                Evidence = items,
                Spec = Spec(["System"], [7000, 7023, 7024, 7038], $"Service failures: {g.Key}", group: false),
            });
        }
    }

    private static void AppCrashes(List<EventItem> ev, List<Finding> list)
    {
        var crashes = ev.Where(e => e.Id == 1000 && e.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && e.Data.Count > 3).ToList();
        foreach (var g in crashes.GroupBy(e => e.Data[0], StringComparer.OrdinalIgnoreCase))
        {
            var items = g.ToList();
            if (items.Count < 3) continue;
            var modules = items.GroupBy(e => e.Data[3], StringComparer.OrdinalIgnoreCase).OrderByDescending(m => m.Count()).ToList();
            var top = modules[0].Key;
            var details = new List<string> { $"Last crash: {items.Max(e => e.TimeCreated):g}" };
            details.Add(modules.Count == 1
                ? $"Always crashes in {top}"
                : $"Crashes in {string.Join(", ", modules.Take(3).Select(m => $"{m.Key} ({m.Count()}×)"))}");
            var thirdParty = !IsSystemModule(top) && !top.Equals(g.Key, StringComparison.OrdinalIgnoreCase);
            if (thirdParty) details.Add($"{top} is not part of {g.Key} or Windows - likely an add-on, overlay or security product");

            list.Add(new Finding
            {
                Key = "app-crash:" + g.Key.ToLowerInvariant(),
                Severity = items.Count >= 10 ? FindingSeverity.Critical : FindingSeverity.Attention,
                Category = "Crash",
                Title = $"{g.Key} crashed {Times(items.Count)}",
                Details = details,
                Fix = thirdParty
                    ? [$"Update or remove the product that installs {top}", $"Update or reinstall {g.Key}"]
                    : [$"Update or reinstall {g.Key}", "Install the latest Visual C++ / .NET runtimes"],
                Evidence = items,
                Spec = Spec(["Application"], [1000], $"Crashes: {g.Key}", providers: ["Application Error"], group: false),
            });
        }

        var distinctApps = crashes.Select(e => e.Data[0].ToLowerInvariant()).Distinct().Count();
        if (distinctApps >= 6)
        {
            list.Add(new Finding
            {
                Key = "app-crash-many",
                Severity = FindingSeverity.Attention,
                Category = "Crash",
                Title = $"{distinctApps} different programs crashed",
                Details = ["When many unrelated programs crash, the cause is often system-wide"],
                Fix = ["Test RAM (Windows Memory Diagnostic / MemTest86)", "Run sfc /scannow and DISM /Online /Cleanup-Image /RestoreHealth", "Remove overclock/XMP"],
                Evidence = crashes,
                Spec = Spec(["Application"], [1000], "All application crashes", providers: ["Application Error"]),
            });
        }
    }

    private static readonly string[] SystemModules =
        ["ntdll.dll", "kernelbase.dll", "kernel32.dll", "ucrtbase.dll", "msvcrt.dll", "combase.dll", "user32.dll", "clr.dll", "coreclr.dll", "unknown",
         "vcruntime140.dll", "msvcp140.dll", "kernel.appcore.dll", "win32u.dll", "gdi32full.dll", "windows.ui.xaml.dll", "twinapi.appcore.dll"];

    private static bool IsSystemModule(string module) =>
        SystemModules.Contains(module.ToLowerInvariant()) || module.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase);

    private static void AppHangs(List<EventItem> ev, List<Finding> list)
    {
        var hangs = ev.Where(e => e.Id == 1002 && e.Provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) && e.Data.Count > 0).ToList();
        foreach (var g in hangs.GroupBy(e => e.Data[0], StringComparer.OrdinalIgnoreCase))
        {
            var items = g.ToList();
            if (items.Count < 3) continue;
            list.Add(new Finding
            {
                Key = "app-hang:" + g.Key.ToLowerInvariant(),
                Severity = FindingSeverity.Attention,
                Category = "Crash",
                Title = $"{g.Key} froze {Times(items.Count)}",
                Details = [$"Last time: {items.Max(e => e.TimeCreated):g}", "Often waiting for a network drive, printer, slow disk or antivirus scan"],
                Fix = [$"Update {g.Key}", "Check whether it uses network drives or printers that were offline"],
                Evidence = items,
                Spec = Spec(["Application"], [1002], $"Hangs: {g.Key}", providers: ["Application Hang"], group: false),
            });
        }
    }

    // ---------------- updates / resources ----------------

    [GeneratedRegex(@"0x[0-9A-Fa-f]{8}")]
    private static partial Regex HexRegex();

    private static void UpdateFailures(List<EventItem> ev, List<Finding> list)
    {
        var fails = ev.Where(e => e.Id == 20 && Is(e, "WindowsUpdateClient")).ToList();
        if (fails.Count == 0) return;
        var codes = fails.Select(e => HexRegex().Match(e.Message).Value).Where(c => c.Length > 0)
                         .GroupBy(c => c.ToLowerInvariant()).OrderByDescending(g => g.Count()).ToList();
        var details = new List<string> { $"Last failure: {fails.Max(e => e.TimeCreated):g}" };
        foreach (var c in codes.Take(2))
            if (ErrorCodes.TryParseHex(c.Key, out var code))
                details.Add($"{c.Key} ({c.Count()}×): {ErrorCodes.Describe(code)?.Meaning ?? "see event"}");

        var titles = fails.Select(e => UpdateTitle(e.Message)).Where(t => t.Length > 0).Distinct().ToList();
        if (titles.Count > 0) details.Add("Update: " + titles[0] + (titles.Count > 1 ? $" (+{titles.Count - 1} more)" : ""));

        list.Add(new Finding
        {
            Key = "wu-fail",
            Severity = fails.Count >= 3 ? FindingSeverity.Attention : FindingSeverity.Info,
            Category = "Update",
            Title = $"Windows Update failed {Times(fails.Count)}",
            Details = details,
            Fix = ["DISM /Online /Cleanup-Image /RestoreHealth, then sfc /scannow", "Free disk space on C:", "Reset Windows Update components", "Install the update manually from the Microsoft Update Catalog"],
            Evidence = fails,
            Spec = Spec(["System"], [20], "Windows Update failures", providers: ["Microsoft-Windows-WindowsUpdateClient"], group: false),
        });
    }

    private static string UpdateTitle(string message)
    {
        var i = message.LastIndexOf(": ", StringComparison.Ordinal);
        return i > 0 && i + 2 < message.Length ? message[(i + 2)..].Trim().TrimEnd('.') : "";
    }

    private static void LowMemory(List<EventItem> ev, List<Finding> list)
    {
        var low = ev.Where(e => e.Id == 2004 && Is(e, "Resource-Exhaustion-Detector")).ToList();
        if (low.Count == 0) return;
        list.Add(new Finding
        {
            Key = "low-memory",
            Severity = FindingSeverity.Critical,
            Category = "System",
            Title = $"Windows ran out of memory {Times(low.Count)}",
            Details = [$"Last time: {low.Max(e => e.TimeCreated):g}", "The event lists the programs that used the most memory"],
            Fix = ["Open the event to see the top memory consumer and update/restart it", "Set the page file to System managed", "Add RAM if it happens under normal workload"],
            Evidence = low,
            Spec = Spec(["System"], [2004], "Low memory", group: false),
        });
    }

    // ---------------- security ----------------

    private static void FailedLogons(List<EventItem> ev, List<Finding> list)
    {
        var failed = ev.Where(e => e.Id == 4625 && Is(e, "Security-Auditing")).ToList();
        if (failed.Count == 0) return;

        // brute force: by source IP
        foreach (var g in failed.Where(e => e.Data.Count > 19 && IsRealIp(e.Data[19])).GroupBy(e => e.Data[19]))
        {
            var items = g.ToList();
            if (items.Count < 10) continue;
            var users = items.Select(e => e.Data[5]).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rdp = items.Count(e => e.Data.Count > 10 && e.Data[10] == "10");
            list.Add(new Finding
            {
                Key = "bruteforce:" + g.Key,
                Severity = FindingSeverity.Critical,
                Category = "Security",
                Title = $"{items.Count} failed logons from {g.Key}",
                Details =
                [
                    $"Tried {users.Count} user name(s): {string.Join(", ", users.Take(5))}{(users.Count > 5 ? "…" : "")}",
                    $"Between {items.Min(e => e.TimeCreated):g} and {items.Max(e => e.TimeCreated):g}",
                    rdp > 0 ? $"{rdp} of them over Remote Desktop" : "Over the network (SMB/other)",
                ],
                Fix = ["Block the address in the firewall", "Never expose RDP (3389) directly to the internet - use VPN or RD Gateway", "Enable account lockout policy; use strong passwords/MFA"],
                Evidence = items,
                Spec = Spec(["Security"], [4625], $"Failed logons from {g.Key}", group: false),
            });
        }

        // repeated failures for one user from one workstation = stale stored password
        foreach (var g in failed.Where(e => e.Data.Count > 13).GroupBy(e => (User: e.Data[5], Ws: e.Data[13])))
        {
            var items = g.ToList();
            if (items.Count < 5 || g.Key.User.Length == 0 || g.Key.User == "-") continue;
            if (items.Any(e => e.Data.Count > 19 && IsRealIp(e.Data[19]) && failed.Count(f => f.Data.Count > 19 && f.Data[19] == e.Data[19]) >= 10)) continue;
            var reason = items[0].Data.Count > 9 && ErrorCodes.TryParseHex(items[0].Data[9], out var sub) ? ErrorCodes.Describe(sub)?.Meaning : null;
            var ws = g.Key.Ws is "" or "-" ? "an unknown computer" : g.Key.Ws;
            list.Add(new Finding
            {
                Key = $"logon-fail:{g.Key.User}@{g.Key.Ws}".ToLowerInvariant(),
                Severity = FindingSeverity.Attention,
                Category = "Security",
                Title = $"{g.Key.User} fails to log on from {ws} ({items.Count}×)",
                Details = [reason is null ? "Repeated failures" : $"Reason: {reason}", $"Last time: {items.Max(e => e.TimeCreated):g}", "Typical: an old password stored in a mapped drive, service, task or phone"],
                Fix = [$"On {ws}: update saved credentials (Credential Manager, mapped drives, services, scheduled tasks)"],
                Evidence = items,
                Spec = Spec(["Security"], [4625], $"Failed logons: {g.Key.User}", group: false),
            });
        }
    }

    private static bool IsRealIp(string ip) => ip.Length > 0 && ip is not "-" and not "::1" and not "127.0.0.1" and not "0.0.0.0";

    private static void Lockouts(List<EventItem> ev, List<Finding> list)
    {
        var locks = ev.Where(e => e.Id == 4740 && Is(e, "Security-Auditing") && e.Data.Count > 1).ToList();
        foreach (var g in locks.GroupBy(e => e.Data[0], StringComparer.OrdinalIgnoreCase))
        {
            var items = g.ToList();
            var callers = items.Select(e => e.Data[1]).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Add(new Finding
            {
                Key = "lockout:" + g.Key.ToLowerInvariant(),
                Severity = items.Count >= 3 ? FindingSeverity.Critical : FindingSeverity.Attention,
                Category = "Security",
                Title = $"Account {g.Key} was locked out {Times(items.Count)}",
                Details = [$"Bad attempts came from: {string.Join(", ", callers.Take(4))}", $"Last time: {items.Max(e => e.TimeCreated):g}"],
                Fix = ["On the caller computer, remove/update stored credentials for this account", "Check phones/mail clients using the old password"],
                Evidence = items,
                Spec = Spec(["Security"], [4740], $"Lockouts: {g.Key}", group: false),
            });
        }
    }

    private static void LogCleared(List<EventItem> ev, List<Finding> list)
    {
        var cleared = ev.Where(e => (e.Id == 1102 || e.Id == 104) && Is(e, "Eventlog")).ToList();
        if (cleared.Count == 0) return;
        list.Add(new Finding
        {
            Key = "log-cleared",
            Severity = FindingSeverity.Attention,
            Category = "Security",
            Title = $"Event logs were cleared {Times(cleared.Count)}",
            Details = [$"Last time: {cleared.Max(e => e.TimeCreated):g}", "Can be maintenance - or someone hiding what they did"],
            Fix = ["Open the events to see which account cleared the log and confirm it was expected"],
            Evidence = cleared,
            Spec = Spec(["System", "Security"], [104, 1102], "Cleared logs", group: false),
        });
    }

    private static void NewServices(List<EventItem> ev, List<Finding> list)
    {
        var svc = ev.Where(e => e.Id == 7045 && Is(e, "Service Control Manager") && e.Data.Count > 1).ToList();
        var suspicious = svc.Where(e =>
        {
            var path = e.Data[1].ToLowerInvariant();
            return path.Contains("\\temp\\") || path.Contains("\\appdata\\") || path.Contains("\\users\\public\\")
                   || path.Contains("powershell") || path.Contains("cmd.exe /c") || path.Contains("cmd /c") || path.Contains("%comspec%");
        }).ToList();
        if (suspicious.Count == 0) return;
        list.Add(new Finding
        {
            Key = "svc-suspicious",
            Severity = FindingSeverity.Critical,
            Category = "Security",
            Title = $"{suspicious.Count} service(s) installed from an unusual location",
            Details = suspicious.Take(4).Select(e => $"{e.Data[0]}: {e.Data[1]}").ToList(),
            Fix = ["Check whether you installed this; if not, investigate the file (signature, VirusTotal) and remove the service", "Services that run PowerShell/cmd or live in Temp/AppData are a classic malware pattern"],
            Evidence = suspicious,
            Spec = Spec(["System"], [7045], "New services", group: false),
        });
    }

    private static void Malware(List<EventItem> ev, List<Finding> list)
    {
        var det = ev.Where(e => e.Id is 1006 or 1116 && Is(e, "Windows Defender")).ToList();
        if (det.Count == 0) return;
        var failed = ev.Where(e => e.Id is 1008 or 1118 or 1119 && Is(e, "Windows Defender")).ToList();
        var threats = det.Where(e => e.Data.Count > 7).Select(e => e.Data[7]).Where(t => t.Length > 0).Distinct().ToList();
        list.Add(new Finding
        {
            Key = "malware",
            Severity = FindingSeverity.Critical,
            Category = "Antivirus",
            Title = $"Microsoft Defender detected threats {Times(det.Count)}",
            Details =
            [
                threats.Count > 0 ? "Threats: " + string.Join(", ", threats.Take(4)) : "See the events for threat names",
                failed.Count > 0 ? $"{failed.Count} time(s) Defender could NOT remove it" : "Defender acted against the detections",
                $"Last detection: {det.Max(e => e.TimeCreated):g}",
            ],
            Fix = ["Check Windows Security > Protection history", "Run a full scan, and an offline scan if removal failed", "Change passwords if a password stealer was found"],
            Evidence = det.Concat(failed).ToList(),
            Spec = Spec([DefenderLog], [1006, 1008, 1116, 1117, 1118, 1119], "Defender detections", group: false),
        });
    }

    public const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";

    private static void DefenderOff(List<EventItem> ev, List<Finding> list)
    {
        var off = ev.Where(e => e.Id is 5001 or 5010 or 5012 && Is(e, "Windows Defender")).ToList();
        if (off.Count == 0) return;
        list.Add(new Finding
        {
            Key = "defender-off",
            Severity = FindingSeverity.Attention,
            Category = "Antivirus",
            Title = "Microsoft Defender protection was turned off",
            Details = [$"Last time: {off.Max(e => e.TimeCreated):g}", "Normal if another antivirus is installed; otherwise suspicious"],
            Fix = ["If you have no other antivirus, turn real-time protection back on and find out what disabled it"],
            Evidence = off,
            Spec = Spec([DefenderLog], [5001, 5010, 5012], "Defender disabled", group: false),
        });
    }

    // ---------------- backup / domain / network ----------------

    private static void BackupVss(List<EventItem> ev, List<Finding> list)
    {
        var vss = ev.Where(e => (e.Provider.Equals("VSS", StringComparison.OrdinalIgnoreCase) && e.Id is 8193 or 12289 or 12293 or 13)
                                || (e.Provider.Equals("volsnap", StringComparison.OrdinalIgnoreCase) && e.Id is 25 or 36)).ToList();
        if (vss.Count < 2) return;
        list.Add(new Finding
        {
            Key = "vss",
            Severity = FindingSeverity.Attention,
            Category = "Backup",
            Title = $"Volume Shadow Copy errors ({vss.Count}×) - backups may be failing",
            Details = [$"Last error: {vss.Max(e => e.TimeCreated):g}", "Backups and restore points depend on VSS snapshots"],
            Fix = ["Check that your last backups actually succeeded", "vssadmin list writers (look for failed writers)", "Free disk space or enlarge shadow storage (vssadmin resize shadowstorage)"],
            Evidence = vss,
            Spec = Spec(SysApp, [8193, 12289, 12293, 13, 25, 36], "VSS / shadow copy errors"),
        });
    }

    private static void Domain(List<EventItem> ev, List<Finding> list)
    {
        var broken = ev.Where(e => e.Id == 3210 && Is(e, "NETLOGON")).ToList();
        if (broken.Count > 0)
        {
            list.Add(new Finding
            {
                Key = "domain-trust",
                Severity = FindingSeverity.Critical,
                Category = "Domain",
                Title = "Trust with the domain is broken",
                Details = ["The computer's machine password no longer matches the domain", "Domain logons may fail"],
                Fix = ["Test-ComputerSecureChannel -Repair -Credential (Get-Credential)", "Or rejoin the domain"],
                Evidence = broken,
                Spec = Spec(["System"], [3210], "Secure channel errors"),
            });
        }

        var noDc = ev.Where(e => (e.Id == 5719 && Is(e, "NETLOGON")) || (e.Id == 1129 && Is(e, "GroupPolicy"))).ToList();
        var days = noDc.Select(e => e.TimeCreated.Date).Distinct().Count();
        if (noDc.Count >= 5 && days >= 3)
        {
            list.Add(new Finding
            {
                Key = "domain-unreachable",
                Severity = FindingSeverity.Attention,
                Category = "Domain",
                Title = $"Domain controller often unreachable ({days} days)",
                Details = ["Group Policy and domain logon could not reach a DC", "Normal for laptops off the network; a problem for office PCs"],
                Fix = ["Check DNS points to the domain DNS servers (ipconfig /all)", "nltest /dsgetdc:<domain>"],
                Evidence = noDc,
                Spec = Spec(["System"], [5719, 1129], "Domain controller unreachable"),
            });
        }

        var gp = ev.Where(e => e.Id is 1085 or 1096 or 1058 && Is(e, "GroupPolicy")).ToList();
        if (gp.Count >= 3)
        {
            list.Add(new Finding
            {
                Key = "gpo-errors",
                Severity = FindingSeverity.Attention,
                Category = "Domain",
                Title = $"Group Policy errors ({gp.Count}×)",
                Details = [$"Last time: {gp.Max(e => e.TimeCreated):g}"],
                Fix = ["gpresult /h report.html to see which policy/extension fails", "Check SYSVOL access and replication"],
                Evidence = gp,
                Spec = Spec(["System"], [1085, 1096, 1058], "Group Policy errors"),
            });
        }
    }

    private static void TimeSync(List<EventItem> ev, List<Finding> list)
    {
        var t = ev.Where(e => Is(e, "Time-Service") && e.Id is 36 or 129 or 134 or 142).ToList();
        if (t.Count < 5) return;
        list.Add(new Finding
        {
            Key = "time-sync",
            Severity = FindingSeverity.Attention,
            Category = "Network",
            Title = $"Clock synchronization keeps failing ({t.Count}×)",
            Details = ["A drifting clock breaks domain logons (Kerberos) and HTTPS"],
            Fix = ["w32tm /query /status and w32tm /resync", "Allow UDP 123 to the time source"],
            Evidence = t,
            Spec = Spec(["System"], [36, 129, 134, 142], "Time sync errors"),
        });
    }

    private static void DnsTimeouts(List<EventItem> ev, List<Finding> list)
    {
        var dns = ev.Where(e => e.Id == 1014 && Is(e, "DNS-Client")).ToList();
        var days = dns.Select(e => e.TimeCreated.Date).Distinct().Count();
        if (dns.Count < 30 || days < 3) return;
        list.Add(new Finding
        {
            Key = "dns",
            Severity = FindingSeverity.Info,
            Category = "Network",
            Title = $"Frequent DNS timeouts ({dns.Count}× on {days} days)",
            Details = ["DNS servers often did not answer", "Websites may load slowly or fail at times"],
            Fix = ["Check configured DNS servers (ipconfig /all)", "Consider a reliable DNS (your domain DNS, or e.g. 1.1.1.1 / 8.8.8.8 at home)"],
            Evidence = dns,
            Spec = Spec(["System"], [1014], "DNS timeouts"),
        });
    }

    private static void IpConflict(List<EventItem> ev, List<Finding> list)
    {
        var c = ev.Where(e => e.Id == 4199 && e.Provider.Equals("Tcpip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (c.Count == 0) return;
        list.Add(new Finding
        {
            Key = "ip-conflict",
            Severity = FindingSeverity.Critical,
            Category = "Network",
            Title = $"IP address conflict {Times(c.Count)}",
            Details = ["Another device uses the same IP address", $"Last time: {c.Max(e => e.TimeCreated):g}"],
            Fix = ["Find the other device by the MAC address in the event", "Use DHCP reservations instead of static IPs inside the DHCP range"],
            Evidence = c,
            Spec = Spec(["System"], [4199], "IP conflicts"),
        });
    }

    private static void CpuThrottling(List<EventItem> ev, List<Finding> list)
    {
        var t = ev.Where(e => e.Id == 37 && Is(e, "Kernel-Processor-Power")).ToList();
        if (t.Count < 3) return;
        list.Add(new Finding
        {
            Key = "cpu-throttle",
            Severity = FindingSeverity.Attention,
            Category = "Hardware",
            Title = $"CPU was slowed down by firmware ({t.Count}×)",
            Details = ["Usually overheating or a power limit - the PC runs slower than it should"],
            Fix = ["Check CPU temperature; clean fans and heatsink", "On laptops use the original charger", "Update BIOS"],
            Evidence = t,
            Spec = Spec(["System"], [37], "CPU throttling"),
        });
    }

    private static void TempProfiles(List<EventItem> ev, List<Finding> list)
    {
        var p = ev.Where(e => e.Id is 1511 or 1515 or 1508 && Is(e, "User Profiles Service")).ToList();
        if (p.Count == 0) return;
        list.Add(new Finding
        {
            Key = "temp-profile",
            Severity = FindingSeverity.Critical,
            Category = "System",
            Title = "A user got a temporary profile",
            Details = ["The user's real profile could not be loaded", "Files and settings saved in the temporary profile are lost at logoff"],
            Fix = ["Check HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList for a .bak duplicate", "Back up data, then repair or recreate the profile"],
            Evidence = p,
            Spec = Spec(["Application"], [1511, 1515, 1508], "Profile errors"),
        });
    }

    private static void CrashDumpConfig(List<EventItem> ev, List<Finding> list)
    {
        var d = ev.Where(e => e.Provider.Equals("volmgr", StringComparison.OrdinalIgnoreCase) && e.Id is 46 or 49 or 161).ToList();
        if (d.Count == 0) return;
        list.Add(new Finding
        {
            Key = "dump-config",
            Severity = FindingSeverity.Info,
            Category = "System",
            Title = "Crash dumps cannot be saved",
            Details = ["If Windows crashes, no memory dump will be available to find the cause"],
            Fix = ["Set the page file on C: to System managed", "Check Startup and Recovery settings (Automatic memory dump)"],
            Evidence = d,
            Spec = Spec(["System"], [46, 49, 161], "Crash dump configuration"),
        });
    }

    // ---------------- helpers ----------------

    private static bool Is(EventItem e, string providerPart) =>
        e.Provider.Contains(providerPart, StringComparison.OrdinalIgnoreCase);

    private static string Times(int n) => n == 1 ? "once" : n == 2 ? "twice" : $"{n} times";

    private static string ProcessName(string raw)
    {
        var p = raw;
        var paren = p.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) p = p[..paren];
        p = p.Trim().Trim('"');
        var slash = p.LastIndexOfAny(['\\', '/']);
        return (slash >= 0 ? p[(slash + 1)..] : p).ToLowerInvariant();
    }

    private static EventQuerySpec Spec(string[] logs, int[] ids, string label, string[]? providers = null, bool? group = null) => new()
    {
        Logs = logs,
        Ids = ids,
        Providers = providers,
        Window = TimeSpan.FromDays(30),
        MaxLevel = null,
        Label = label + " · 30 days",
        Group = group,
    };

    /// <summary>Short text summary of all findings (for reports and AI).</summary>
    public static string Summarize(IEnumerable<Finding> findings) =>
        string.Join(Environment.NewLine, findings.Select(f => $"- [{f.Severity}] {f.Title}: {string.Join("; ", f.Details)}"));

    internal static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
