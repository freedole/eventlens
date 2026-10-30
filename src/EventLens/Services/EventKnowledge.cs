using EventLens.Models;

namespace EventLens.Services;

/// <summary>
/// Built-in knowledge base: translates well-known event IDs into human language.
/// Key = (provider, id); provider "*" matches any provider.
/// </summary>
public static class EventKnowledge
{
    private static readonly Dictionary<(string Provider, int Id), Explanation> Map = new()
    {
        // --- Power / restarts ---
        [("Microsoft-Windows-Kernel-Power", 41)] = new(
            "Unexpected shutdown (power loss or hard reset)",
            "The system rebooted without shutting down cleanly: power loss, hard reset, a hang that forced a power-off, or a blue screen.",
            "Check for a BugCheck event (1001) at the same time. If none: PSU, UPS, overheating, or a frozen system that was powered off.",
            "Power"),
        [("EventLog", 6008)] = new(
            "Previous shutdown was unexpected",
            "Windows noticed on boot that the last shutdown was not clean.",
            "Look at events just before this time to see what happened last.",
            "Power"),
        [("User32", 1074)] = new(
            "Restart or shutdown initiated",
            "A user or a process (e.g. Windows Update, installer) started a restart/shutdown. The message names the process and reason.",
            "Read the message: it shows which process and which user started it.",
            "Power"),
        [("EventLog", 6005)] = new("Event Log service started (system boot)", "Marks a system start.", "Informational.", "Power"),
        [("EventLog", 6006)] = new("Event Log service stopped (clean shutdown)", "Marks a clean shutdown.", "Informational.", "Power"),
        [("Microsoft-Windows-Kernel-General", 12)] = new("Operating system started", "The OS started at the time shown in the message.", "Informational.", "Power"),
        [("Microsoft-Windows-Kernel-General", 13)] = new("Operating system shut down", "The OS shut down cleanly.", "Informational.", "Power"),
        [("Microsoft-Windows-Kernel-Power", 42)] = new("Entering sleep", "The system is going to sleep.", "Informational.", "Power"),
        [("Microsoft-Windows-Power-Troubleshooter", 1)] = new("Resumed from sleep", "The system woke up. The message shows the wake source.", "If the PC wakes up by itself, check the wake source (powercfg /waketimers).", "Power"),

        // --- Crashes ---
        [("Microsoft-Windows-WER-SystemErrorReporting", 1001)] = new(
            "Blue screen (BugCheck)",
            "Windows crashed with a stop error. The message contains the bugcheck code and the dump file location.",
            "Analyze the minidump (C:\\Windows\\Minidump) with WinDbg; most often caused by drivers, RAM or storage.",
            "Crash"),
        [("Application Error", 1000)] = new(
            "Application crashed",
            "A program terminated unexpectedly. The message names the application and the faulting module.",
            "If the faulting module is a DLL from another product (antivirus, overlay, driver), that is the likely culprit. Update or reinstall the app.",
            "Crash"),
        [("Application Hang", 1002)] = new(
            "Application stopped responding",
            "A program hung and was closed.",
            "Repeated hangs: check disk, antivirus scanning, or network shares the app is waiting on.",
            "Crash"),
        [(".NET Runtime", 1026)] = new(
            ".NET application crashed",
            "A .NET program crashed with an unhandled exception. The message contains the exception and stack trace.",
            "Send the stack trace to the vendor or check the app logs.",
            "Crash"),

        // --- Services ---
        [("Service Control Manager", 7031)] = new(
            "Service crashed and was restarted",
            "A Windows service terminated unexpectedly; the recovery action was taken.",
            "Find the service in the message; check its own log and updates.",
            "Service"),
        [("Service Control Manager", 7034)] = new(
            "Service crashed",
            "A Windows service terminated unexpectedly.",
            "Find the service in the message; check its own log and updates.",
            "Service"),
        [("Service Control Manager", 7000)] = new(
            "Service failed to start",
            "A service could not start (bad path, missing dependency, wrong account/password).",
            "Check the service account and dependencies in services.msc.",
            "Service"),
        [("Service Control Manager", 7009)] = new(
            "Service start timed out",
            "A service did not respond within the start timeout (usually 30 s).",
            "Common on slow boots; harmless if the service starts later. Otherwise set Automatic (Delayed Start).",
            "Service"),
        [("Service Control Manager", 7023)] = new("Service terminated with an error", "A service stopped and reported an error code.", "Look up the error code in the message.", "Service"),

        // --- Storage ---
        [("disk", 7)] = new("Bad block on disk", "The disk reported an unreadable sector.", "Back up immediately and check SMART status; the disk may be failing.", "Storage"),
        [("disk", 51)] = new("Disk paging error", "An error was detected on a disk during a paging operation.", "Check cables, SMART and controller drivers.", "Storage"),
        [("disk", 153)] = new("Disk I/O retried", "An I/O operation had to be retried.", "Occasional is OK; frequent means a failing disk, cable or controller.", "Storage"),
        [("storahci", 129)] = new("Storage controller reset", "The storage controller was reset after a timeout.", "Update the storage driver / firmware; check the disk.", "Storage"),
        [("stornvme", 129)] = new("NVMe controller reset", "The NVMe controller was reset after a timeout.", "Update SSD firmware and NVMe driver; check power settings (APST).", "Storage"),
        [("Ntfs", 55)] = new("File system corruption", "NTFS detected corruption.", "Run chkdsk /f on the affected volume.", "Storage"),
        [("Microsoft-Windows-Ntfs", 55)] = new("File system corruption", "NTFS detected corruption.", "Run chkdsk /f on the affected volume.", "Storage"),

        // --- Network ---
        [("Microsoft-Windows-DNS-Client", 1014)] = new("DNS name resolution timed out", "No DNS server responded for a name.", "Check DNS servers and connectivity; occasional is harmless.", "Network"),
        [("Microsoft-Windows-Time-Service", 129)] = new("Time sync failed", "Windows could not reach a time source.", "Check NTP/domain time source and firewall (UDP 123).", "Network"),
        [("NETLOGON", 5719)] = new("No domain controller available", "The computer could not reach a domain controller.", "Check network/VPN, DNS pointing to DCs, and DC availability.", "Network"),
        [("Microsoft-Windows-GroupPolicy", 1129)] = new("Group Policy failed: no DC connectivity", "Group Policy processing failed because a DC could not be reached.", "Usually network not ready at boot; check DNS and DC reachability.", "Network"),

        // --- Security ---
        [("Microsoft-Windows-Security-Auditing", 4624)] = new("Successful logon", "An account logged on. Logon type: 2 = local, 3 = network, 10 = RDP.", "Informational; check the logon type and source address.", "Security"),
        [("Microsoft-Windows-Security-Auditing", 4625)] = new("Failed logon", "A logon attempt failed. The message shows the account, failure reason and source address.", "Many failures from one address = brute force; block it and check RDP exposure.", "Security"),
        [("Microsoft-Windows-Security-Auditing", 4740)] = new("Account locked out", "An account was locked out after too many failed attempts.", "The message shows the caller computer - usually a stale password on a device or service.", "Security"),
        [("Microsoft-Windows-Security-Auditing", 4720)] = new("User account created", "A new user account was created.", "Verify it was expected.", "Security"),
        [("Microsoft-Windows-Security-Auditing", 4732)] = new("Member added to a security group", "A user was added to a local group (e.g. Administrators).", "Verify the change was expected.", "Security"),
        [("Microsoft-Windows-Eventlog", 1102)] = new("Security log was cleared", "Someone cleared the Security log.", "Suspicious unless planned - check who did it.", "Security"),

        // --- Noise ---
        [("Microsoft-Windows-DistributedCOM", 10016)] = new(
            "DCOM permission warning (usually harmless)",
            "A component tried to activate a COM server without permission. Microsoft documents this as expected noise.",
            "Safe to ignore in most cases.",
            "Noise"),
        [("Microsoft-Windows-DistributedCOM", 10010)] = new("DCOM server did not register in time", "A COM server did not register within the timeout.", "Usually harmless.", "Noise"),

        // --- Updates ---
        [("Microsoft-Windows-WindowsUpdateClient", 19)] = new("Update installed", "An update was installed successfully.", "Informational.", "Update"),
        [("Microsoft-Windows-WindowsUpdateClient", 20)] = new("Update failed", "An update failed to install. The message contains the error code.", "Look up the error code; run the Windows Update troubleshooter or DISM /RestoreHealth.", "Update"),
        [("Microsoft-Windows-WindowsUpdateClient", 43)] = new("Update installation started", "Installation of an update started.", "Informational.", "Update"),
    };

    public static Explanation? Lookup(string provider, int id)
    {
        if (Map.TryGetValue((provider, id), out var e)) return e;
        if (Map.TryGetValue(("*", id), out e)) return e;
        return null;
    }

    public static int Count => Map.Count;
}
