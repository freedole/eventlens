using System.Globalization;
using System.Text.RegularExpressions;

namespace EventLens.Core.Util;

/// <summary>Minimal semantic-version comparison: 0.2.0-alpha2 &lt; 0.2.0-alpha10 &lt; 0.2.0 &lt; 0.2.1.</summary>
public static partial class SemVer
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.\-]+))?")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex ChunkRegex();

    public static bool TryParse(string? s, out (int Major, int Minor, int Patch, string Pre) v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = VersionRegex().Match(s.Trim());
        if (!m.Success) return false;
        v = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
             int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
             int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
             m.Groups[4].Success ? m.Groups[4].Value : "");
        return true;
    }

    /// <summary>Returns &lt;0 if a &lt; b, 0 if equal, &gt;0 if a &gt; b. Unparseable versions sort lowest.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var va);
        var okB = TryParse(b, out var vb);
        if (!okA || !okB) return okA.CompareTo(okB);

        var c = va.Major.CompareTo(vb.Major);
        if (c == 0) c = va.Minor.CompareTo(vb.Minor);
        if (c == 0) c = va.Patch.CompareTo(vb.Patch);
        if (c != 0) return c;

        // a release is newer than any pre-release of the same version
        if (va.Pre.Length == 0 && vb.Pre.Length == 0) return 0;
        if (va.Pre.Length == 0) return 1;
        if (vb.Pre.Length == 0) return -1;
        return Natural(va.Pre, vb.Pre);
    }

    private static int Natural(string a, string b)
    {
        var ca = ChunkRegex().Matches(a.ToLowerInvariant()).Select(m => m.Value).ToList();
        var cb = ChunkRegex().Matches(b.ToLowerInvariant()).Select(m => m.Value).ToList();
        for (int i = 0; i < Math.Min(ca.Count, cb.Count); i++)
        {
            int c;
            if (long.TryParse(ca[i], out var na) && long.TryParse(cb[i], out var nb)) c = na.CompareTo(nb);
            else c = string.CompareOrdinal(ca[i], cb[i]);
            if (c != 0) return c;
        }
        return ca.Count.CompareTo(cb.Count);
    }
}
