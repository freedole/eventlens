using EventLens.Core.Findings;
using EventLens.Core.Knowledge;
using EventLens.Core.Util;
using EventLens.Models;
using Xunit;

namespace EventLens.Tests;

public class KnowledgeBaseTests
{
    private static EventItem Ev(string provider, int id, Severity sev, string message, params string[] data) => new()
    {
        TimeCreated = DateTime.Now.AddHours(-1),
        Id = id,
        Severity = sev,
        Provider = provider,
        LogName = "System",
        Message = message,
        Data = data,
        Explanation = KnowledgeBase.Explain(provider, id, data, message),
    };

    [Fact]
    public void Loads_at_least_300_entries_without_duplicates()
    {
        KnowledgeBase.EnsureLoaded();
        Assert.True(KnowledgeBase.Count >= 300, $"only {KnowledgeBase.Count}");
        Assert.True(KnowledgeBase.Version > 0);

        var keys = new HashSet<(string, int)>();
        foreach (var e in KnowledgeBase.AllEntries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title), e.Provider);
            Assert.False(string.IsNullOrWhiteSpace(e.What), e.Title);
            Assert.Contains(e.Class.ToLowerInvariant(), new[] { "harmless", "info", "attention", "critical" });
            Assert.NotEmpty(e.AllIds);
            foreach (var id in e.AllIds) Assert.True(keys.Add((e.Provider.ToLowerInvariant(), id)), $"duplicate {e.Provider} {id}");
        }
    }

    [Fact]
    public void Provider_lookup_ignores_microsoft_windows_prefix()
    {
        Assert.NotNull(KnowledgeBase.Find("Kernel-Power", 41));
        Assert.NotNull(KnowledgeBase.Find("Microsoft-Windows-Kernel-Power", 41));
    }

    [Fact]
    public void Fills_service_crash_summary()
    {
        var e = Ev("Service Control Manager", 7031, Severity.Error, "", "Print Spooler", "3", "60000", "1");
        Assert.Equal("Service \"Print Spooler\" crashed (3rd time) - Windows restarted it", e.Summary);
    }

    [Fact]
    public void Decodes_bugcheck_from_kernel_power_41()
    {
        var e = Ev("Microsoft-Windows-Kernel-Power", 41, Severity.Critical, "", "159", "0");
        Assert.Contains("DRIVER_POWER_STATE_FAILURE", e.Summary);
        Assert.Contains(e.Explanation!.Codes, c => c.Code == "0x0000009F");
    }

    [Fact]
    public void Zero_bugcheck_falls_back_to_title()
    {
        var e = Ev("Microsoft-Windows-Kernel-Power", 41, Severity.Critical, "", "0");
        Assert.Equal(KnowledgeBase.Find("Kernel-Power", 41)!.Title, e.Summary);
    }

    [Fact]
    public void Shutdown_request_names_the_program()
    {
        var e = Ev("User32", 1074, Severity.Information, "", @"C:\Program Files\HD Sentinel\HDSentinel.exe (PC)", "PC", "Other", "0x0", "power off", "", @"PC\user");
        Assert.Equal(@"Power off requested by HDSentinel.exe (PC\user)", e.Summary);
    }

    [Fact]
    public void Known_noise_is_harmless_and_not_a_problem()
    {
        var e = Ev(".NET Runtime", 1022, Severity.Error, "profiler");
        Assert.True(e.IsHarmless);
        Assert.False(e.IsProblem);
        Assert.Equal("Harmless", e.SeverityText);
    }

    [Fact]
    public void User_muted_event_is_harmless()
    {
        var e = Ev("SomeVendor", 5, Severity.Error, "x");
        Assert.True(e.IsProblem);
        e.UserMuted = true;
        Assert.True(e.IsHarmless);
        Assert.False(e.IsProblem);
    }

    [Fact]
    public void Windows_update_error_code_is_decoded()
    {
        var e = Ev("Microsoft-Windows-WindowsUpdateClient", 20, Severity.Error,
            "Installation Failure: Windows failed to install the following update with error 0x800F0922: 2026-10 Cumulative Update.");
        Assert.Contains(e.Explanation!.Codes, c => c.Code == "0x800F0922");
    }

    [Fact]
    public void Failed_logon_reason_is_decoded()
    {
        var data = Enumerable.Range(0, 21).Select(i => i switch { 5 => "admin", 9 => "0xc000006a", _ => "x" }).ToArray();
        var e = Ev("Microsoft-Windows-Security-Auditing", 4625, Severity.Information, "fail", data);
        Assert.Equal("Failed logon for admin - Wrong password", e.Summary);
    }
}

public class FindingsTests
{
    private static EventItem Ev(string provider, int id, string message = "", int hoursAgo = 2, params string[] data) => new()
    {
        TimeCreated = DateTime.Now.AddHours(-hoursAgo),
        Id = id,
        Severity = Severity.Error,
        Provider = provider,
        LogName = "System",
        Message = message,
        Data = data,
        Explanation = KnowledgeBase.Explain(provider, id, data, message),
    };

    [Fact]
    public void Repeated_service_crashes_become_a_finding()
    {
        var events = Enumerable.Range(1, 3).Select(i => Ev("Service Control Manager", 7031, "", i, "Spooler", i.ToString())).ToList();
        var f = FindingsEngine.Run(events, DateTime.Now);
        Assert.Contains(f, x => x.Key == "svc-crash:Spooler");
    }

    [Fact]
    public void Brute_force_is_detected_by_source_ip()
    {
        var data = Enumerable.Range(0, 21).Select(i => i switch { 5 => "admin", 10 => "10", 19 => "185.1.2.3", _ => "x" }).ToArray();
        var events = Enumerable.Range(0, 12).Select(_ => Ev("Microsoft-Windows-Security-Auditing", 4625, "", 1, data)).ToList();
        var f = FindingsEngine.Run(events, DateTime.Now);
        var b = Assert.Single(f, x => x.Key.StartsWith("bruteforce:"));
        Assert.Equal(FindingSeverity.Critical, b.Severity);
    }

    [Fact]
    public void Third_party_shutdowns_are_reported_but_windows_ones_are_not()
    {
        var hd = Ev("User32", 1074, "", 3, @"C:\Tools\HDSentinel.exe (PC)", "PC", "", "", "power off", "", "PC\\u");
        var wl = Ev("User32", 1074, "", 4, @"C:\Windows\System32\winlogon.exe (PC)", "PC", "", "", "restart", "", "PC\\u");
        var f = FindingsEngine.Run([hd, wl], DateTime.Now);
        Assert.Contains(f, x => x.Key == "shutdown-by:hdsentinel.exe");
        Assert.DoesNotContain(f, x => x.Key == "shutdown-by:winlogon.exe");
    }

    [Fact]
    public void Old_events_are_ignored()
    {
        var events = Enumerable.Range(1, 3).Select(i => Ev("Service Control Manager", 7031, "", 24 * 40, "Spooler", "1")).ToList();
        Assert.Empty(FindingsEngine.Run(events, DateTime.Now));
    }

    [Fact]
    public void Muted_events_are_ignored()
    {
        var events = Enumerable.Range(1, 3).Select(i => Ev("Service Control Manager", 7031, "", i, "Spooler", "1")).ToList();
        foreach (var e in events) e.UserMuted = true;
        Assert.Empty(FindingsEngine.Run(events, DateTime.Now));
    }
}

public class UtilTests
{
    [Theory]
    [InlineData("0.2.0-alpha2", "0.2.0-alpha10", -1)]
    [InlineData("0.2.0", "0.2.0-alpha1", 1)]
    [InlineData("v0.2.0-alpha1", "0.1.0", 1)]
    [InlineData("0.2.0", "0.2.0", 0)]
    public void SemVer_compares(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    [Fact]
    public void Markdown_parses_headings_lists_and_inline()
    {
        var blocks = Markdown.Parse("### Title\nSome **bold** and `code`.\n- one\n1. first");
        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Contains(blocks[1].Inlines, i => i.Bold && i.Text == "bold");
        Assert.Contains(blocks[1].Inlines, i => i.Code && i.Text == "code");
        Assert.Equal(MdBlockKind.Bullet, blocks[2].Kind);
        Assert.Equal(MdBlockKind.Numbered, blocks[3].Kind);
    }
}
