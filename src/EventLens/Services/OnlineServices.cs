using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using EventLens.Core.Knowledge;
using EventLens.Core.Util;

namespace EventLens.Services;

public sealed record UpdateInfo(string Version, string Url, bool Prerelease);

/// <summary>Talks to GitHub: knowledge-base updates and new app versions. Both are optional and fail silently.</summary>
public static class OnlineServices
{
    public const string Repo = "freedole/eventlens";
    private const string RawBase = "https://raw.githubusercontent.com/" + Repo + "/main/knowledge/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("EventLens/" + AppVersion);
        return c;
    }

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    public static string KnowledgeFolder => Path.Combine(AppSettings.Folder, "knowledge");

    // ---------- knowledge base ----------

    /// <summary>Downloads a newer knowledge base from GitHub if one exists. Returns a status text.</summary>
    public static async Task<string> SyncKnowledgeAsync(bool force = false)
    {
        var s = AppSettings.Current;
        if (!force && (!s.KbAutoUpdate || DateTime.Now - s.KbLastCheck < TimeSpan.FromHours(20)))
            return "";

        try
        {
            var manifest = KnowledgeBase.ParseManifest(await Http.GetStringAsync(RawBase + "manifest.json"));
            s.KbLastCheck = DateTime.Now;
            s.Save();

            if (manifest.Version <= KnowledgeBase.Version)
                return $"Knowledge base is up to date (version {KnowledgeBase.Version}).";

            var files = new List<KbFile>();
            var raw = new Dictionary<string, string>();
            foreach (var f in manifest.Files)
            {
                if (f.Contains('/') || f.Contains('\\') || !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var json = await Http.GetStringAsync(RawBase + f);
                files.Add(KnowledgeBase.ParseFile(json));   // throws if invalid - nothing is replaced
                raw[f] = json;
            }
            if (files.Sum(f => f.Entries.Count) < 50) return "Downloaded knowledge base looked incomplete - kept the current one.";

            var tmp = KnowledgeFolder + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            foreach (var (name, json) in raw) await File.WriteAllTextAsync(Path.Combine(tmp, name), json);
            await File.WriteAllTextAsync(Path.Combine(tmp, "manifest.json"),
                new JsonObject { ["version"] = manifest.Version, ["updated"] = manifest.Updated, ["files"] = new JsonArray(raw.Keys.Select(k => (JsonNode)k).ToArray()) }.ToJsonString());
            if (Directory.Exists(KnowledgeFolder)) Directory.Delete(KnowledgeFolder, true);
            Directory.Move(tmp, KnowledgeFolder);

            KnowledgeBase.Replace(manifest, files, "updated from GitHub");
            return $"Knowledge base updated to version {manifest.Version} ({KnowledgeBase.Count} entries).";
        }
        catch (Exception ex)
        {
            return "Could not check for knowledge updates: " + ex.Message;
        }
    }

    // ---------- app updates ----------

    public static async Task<UpdateInfo?> CheckForUpdateAsync(bool force = false)
    {
        var s = AppSettings.Current;
        if (!force && (!s.CheckForUpdates || DateTime.Now - s.UpdateLastCheck < TimeSpan.FromHours(20)))
            return null;

        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=15"));
            s.UpdateLastCheck = DateTime.Now;
            s.Save();

            var current = AppVersion;
            var currentIsPre = current.Contains('-');
            UpdateInfo? best = null;
            foreach (var r in (IEnumerable<JsonNode?>?)json?.AsArray() ?? Enumerable.Empty<JsonNode?>())
            {
                if (r?["draft"]?.GetValue<bool>() == true) continue;
                var pre = r?["prerelease"]?.GetValue<bool>() == true;
                if (pre && !currentIsPre && !s.IncludePrereleases) continue;
                var tag = r?["tag_name"]?.GetValue<string>();
                var url = r?["html_url"]?.GetValue<string>();
                if (tag is null || url is null) continue;
                if (SemVer.Compare(tag, current) > 0 && (best is null || SemVer.Compare(tag, best.Version) > 0))
                    best = new UpdateInfo(tag.TrimStart('v'), url, pre);
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    public static string IssueUrl()
    {
        var body = $"**What happened**\n\n\n**What I expected**\n\n\n---\nEventLens {AppVersion}\n{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";
        return $"https://github.com/{Repo}/issues/new?body={Uri.EscapeDataString(body)}";
    }
}
