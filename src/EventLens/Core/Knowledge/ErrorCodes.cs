using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EventLens.Core.Knowledge;

public sealed record DecodedCode(string Code, string Meaning);

/// <summary>Offline decoder for error codes found in events: bugchecks, NTSTATUS logon codes,
/// Windows Update codes, and anything Windows itself can describe (FormatMessage).</summary>
public static partial class ErrorCodes
{
    // ---------- bugchecks ----------

    private static readonly Dictionary<uint, (string Name, string Hint)> Bugchecks = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "Usually a faulty driver; sometimes RAM."),
        [0x19] = ("BAD_POOL_HEADER", "Pool corruption, usually a driver (often antivirus, VPN or storage filter)."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Often faulty RAM or unstable overclock/XMP; test memory."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "A kernel driver crashed; check the minidump for the module."),
        [0x24] = ("NTFS_FILE_SYSTEM", "File system problem; run chkdsk and check the disk's health."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "A driver or system service crashed; often graphics or antivirus drivers."),
        [0x4E] = ("PFN_LIST_CORRUPT", "Memory management corruption: RAM or a driver."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Bad memory access: driver, RAM, or antivirus."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Could not read kernel data from the page file: failing disk, cable or controller."),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "Windows lost access to the boot disk: storage driver, BIOS SATA/RAID mode, or disk failure."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A system thread crashed; the minidump names the driver."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "Hardware fault or driver bug; check RAM, CPU and overclocking."),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", "CPU reported a fatal hardware error."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver did not handle sleep/wake correctly; update chipset, network, USB and graphics drivers."),
        [0xA0] = ("INTERNAL_POWER_ERROR", "Power management failure, often during sleep/hibernate."),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "A driver wrote to read-only memory."),
        [0xC2] = ("BAD_POOL_CALLER", "A driver misused memory pool."),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver Verifier caught a bad driver (Verifier may be enabled)."),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "A driver corrupted memory pool."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Faulty driver, very often network, Wi-Fi or storage."),
        [0xD5] = ("DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "A driver used memory after freeing it."),
        [0xE2] = ("MANUALLY_INITIATED_CRASH", "The crash was triggered on purpose (keyboard or debugger)."),
        [0xEA] = ("THREAD_STUCK_IN_DEVICE_DRIVER", "Graphics driver hang; update the GPU driver, check the GPU."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A critical Windows process stopped: corrupted system files or failing disk. Run sfc /scannow and DISM."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "A critical process ended, often because the system disk stopped responding."),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "A driver tried to run code from non-executable memory."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "A CPU core stopped responding: CPU, overclock, BIOS or power."),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Kernel code or data was modified: bad driver, RAM, or malware."),
        [0x10D] = ("WDF_VIOLATION", "A driver built on the Windows Driver Framework misbehaved."),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", "DirectX graphics kernel failure; update the GPU driver."),
        [0x116] = ("VIDEO_TDR_FAILURE", "The graphics driver could not recover from a hang; update/reinstall the GPU driver, check GPU temperature."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "Graphics scheduler failure; GPU driver or GPU hardware."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "Fatal hardware error (CPU, RAM, PCIe, power); check temperatures, overclock, BIOS."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver took too long; often storage (SSD firmware) or network drivers."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Kernel data corruption detected; driver or RAM."),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "A driver corrupted the kernel heap."),
        [0x144] = ("BUGCODE_USB3_DRIVER", "USB 3 controller or device driver failure."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "Memory store failure, often a failing SSD or its firmware."),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "A system thread crashed; the minidump names the driver."),
        [0x1000007F] = ("UNEXPECTED_KERNEL_MODE_TRAP_M", "Hardware fault or driver bug."),
        [0x1000008E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", "A kernel driver crashed."),
        [0x100000EA] = ("THREAD_STUCK_IN_DEVICE_DRIVER_M", "Graphics driver hang."),
        [0xC000021A] = ("STATUS_SYSTEM_PROCESS_TERMINATED", "Winlogon or CSRSS died: corrupted system files or bad update."),
        [0xDEADDEAD] = ("MANUALLY_INITIATED_CRASH1", "The crash was triggered on purpose."),
    };

    public static int BugcheckCount => Bugchecks.Count;

    public static DecodedCode? Bugcheck(uint code) =>
        Bugchecks.TryGetValue(code, out var b) ? new DecodedCode($"0x{code:X8}", $"{b.Name} - {b.Hint}") : null;

    public static string? BugcheckName(uint code) => Bugchecks.TryGetValue(code, out var b) ? b.Name : null;

    // ---------- NTSTATUS (logon failures etc.) ----------

    private static readonly Dictionary<uint, string> NtStatus = new()
    {
        [0xC000005E] = "No logon servers available (domain controller unreachable)",
        [0xC0000064] = "User name does not exist",
        [0xC000006A] = "Wrong password",
        [0xC000006C] = "Password policy not met",
        [0xC000006D] = "Bad user name or authentication information",
        [0xC000006E] = "Account restriction",
        [0xC000006F] = "Logon outside allowed hours",
        [0xC0000070] = "Logon from an unauthorized workstation",
        [0xC0000071] = "Password expired",
        [0xC0000072] = "Account disabled",
        [0xC00000DC] = "Server in wrong state (e.g. domain controller not ready)",
        [0xC0000133] = "Clocks out of sync with the domain controller",
        [0xC000015B] = "Logon type not granted to this user",
        [0xC000018C] = "Trust relationship between the computer and the domain failed",
        [0xC0000192] = "Netlogon service not started",
        [0xC0000193] = "Account expired",
        [0xC0000224] = "User must change password at next logon",
        [0xC0000225] = "Windows bug (not a risk)",
        [0xC0000234] = "Account locked out",
        [0xC0000413] = "Authentication firewall: not allowed to authenticate to this machine",
        [0xC0000005] = "Access violation (program crash)",
        [0xC0000409] = "Stack buffer overrun / fail-fast (program terminated itself)",
        [0xC0000374] = "Heap corruption",
        [0xC00000FD] = "Stack overflow",
        [0xC0000417] = "Invalid parameter passed to a C runtime function",
        [0xC0000135] = "DLL not found",
        [0xC0000142] = "DLL initialization failed",
        [0xC000001D] = "Illegal instruction",
        [0x80000003] = "Breakpoint reached (debug break / app called fail-fast)",
        [0xE0434352] = ".NET unhandled exception",
        [0xE06D7363] = "C++ exception not handled",
        [0xC0000188] = "Log file full (ETW session; harmless)",
    };

    // ---------- Windows Update ----------

    private static readonly Dictionary<uint, string> UpdateCodes = new()
    {
        [0x80070002] = "File not found - update cache is damaged. Reset the Windows Update components.",
        [0x80070003] = "Path not found - update cache is damaged. Reset the Windows Update components.",
        [0x80070005] = "Access denied - security software or permissions blocked the install.",
        [0x8007000E] = "Out of memory.",
        [0x80070020] = "File in use by another process (often antivirus).",
        [0x80070422] = "The Windows Update service is disabled.",
        [0x8007045B] = "A system shutdown is in progress.",
        [0x80070643] = "Fatal error during installation (often the WinRE partition is too small or a .NET update failed).",
        [0x800705B4] = "Operation timed out.",
        [0x80073712] = "Component store is damaged. Run: DISM /Online /Cleanup-Image /RestoreHealth",
        [0x800F081F] = "Source files could not be found. Run DISM /RestoreHealth or install the update manually.",
        [0x800F0831] = "A required base package is missing; install the previous cumulative update first.",
        [0x800F0922] = "Could not finish: no connection to update servers or the System Reserved partition is full.",
        [0x80240017] = "Update is not applicable to this computer.",
        [0x80240034] = "Download failed.",
        [0x8024200D] = "Update must be downloaded again.",
        [0x80242016] = "Update failed after the restart.",
        [0x8024402C] = "Cannot reach Windows Update: proxy or DNS problem.",
        [0x80244022] = "Windows Update service temporarily unavailable (HTTP 503).",
        [0x8024A105] = "Windows Update agent error; try again or reset the update components.",
        [0x80072EE2] = "Connection timed out.",
        [0x80072EFD] = "Cannot connect to the server (firewall, proxy or no internet).",
        [0x80072F8F] = "TLS/certificate error - often a wrong system clock.",
    };

    // ---------- logon types ----------

    public static string LogonType(string raw) => raw.Trim() switch
    {
        "2" => "Interactive (at the keyboard)",
        "3" => "Network (file share, etc.)",
        "4" => "Batch (scheduled task)",
        "5" => "Service",
        "7" => "Unlock",
        "8" => "Network cleartext",
        "9" => "New credentials (RunAs /netonly)",
        "10" => "Remote Desktop",
        "11" => "Cached credentials",
        var s => s,
    };

    // ---------- parsing ----------

    [GeneratedRegex(@"0x[0-9A-Fa-f]{8}\b")]
    private static partial Regex HexCodeRegex();

    public static bool TryParseHex(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Describes one code. Known tables first, then Windows' own message table.</summary>
    public static DecodedCode? Describe(uint code)
    {
        if (UpdateCodes.TryGetValue(code, out var u)) return new($"0x{code:X8}", u);
        if (NtStatus.TryGetValue(code, out var n)) return new($"0x{code:X8}", n);
        var sys = FromSystem(code);
        return sys is null ? null : new($"0x{code:X8}", sys);
    }

    /// <summary>Finds and decodes every 0xXXXXXXXX code in a text (deduplicated, max 6).</summary>
    public static List<DecodedCode> DecodeAll(string? text)
    {
        var result = new List<DecodedCode>();
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<uint>();
        foreach (Match m in HexCodeRegex().Matches(text))
        {
            if (!TryParseHex(m.Value, out var code) || code == 0 || !seen.Add(code)) continue;
            var d = Describe(code);
            if (d is not null) result.Add(d);
            if (result.Count >= 6) break;
        }
        return result;
    }

    // ---------- Windows FormatMessage ----------

    private const int FormatMessageFromSystem = 0x00001000;
    private const int FormatMessageFromHmodule = 0x00000800;
    private const int FormatMessageIgnoreInserts = 0x00000200;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int FormatMessageW(int flags, IntPtr source, uint messageId, int languageId,
        StringBuilder buffer, int size, IntPtr arguments);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    private static string? FromSystem(uint code)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = FormatMessageW(FormatMessageFromSystem | FormatMessageIgnoreInserts, IntPtr.Zero, code, 0, sb, sb.Capacity, IntPtr.Zero);

            // HRESULT_FROM_WIN32: 0x8007xxxx -> Win32 error xxxx
            if (len == 0 && (code & 0xFFFF0000) == 0x80070000)
                len = FormatMessageW(FormatMessageFromSystem | FormatMessageIgnoreInserts, IntPtr.Zero, code & 0xFFFF, 0, sb, sb.Capacity, IntPtr.Zero);

            // NTSTATUS lives in ntdll's message table
            if (len == 0 && (code & 0xC0000000) != 0)
            {
                var ntdll = GetModuleHandleW("ntdll.dll");
                if (ntdll != IntPtr.Zero)
                    len = FormatMessageW(FormatMessageFromHmodule | FormatMessageIgnoreInserts, ntdll, code, 0, sb, sb.Capacity, IntPtr.Zero);
            }

            if (len == 0) return null;
            var text = sb.ToString(0, len).Trim().Replace("\r\n", " ").Trim('{', '}').Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
