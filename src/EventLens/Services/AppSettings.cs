using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EventLens.Services;

/// <summary>Persisted user settings: %AppData%\EventLens\settings.json. API key is DPAPI-encrypted.</summary>
public sealed class AppSettings
{
    public int Theme { get; set; }                  // 0 system, 1 light, 2 dark
    public bool GroupRepeats { get; set; } = true;
    public int LogIndex { get; set; }
    public int LevelIndex { get; set; } = 2;
    public int WindowIndex { get; set; } = 1;
    public List<string> RecentComputers { get; set; } = [];

    public string AiProvider { get; set; } = "Groq";
    public string AiEndpoint { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string AiKeyProtected { get; set; } = "";
    public string AiLanguage { get; set; } = "";

    // ---------- storage ----------

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EventLens");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file - start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    // ---------- API key (DPAPI, current user) ----------

    public string GetAiKey()
    {
        if (string.IsNullOrEmpty(AiKeyProtected)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(AiKeyProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    public void SetAiKey(string key)
    {
        AiKeyProtected = string.IsNullOrEmpty(key)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    public void AddRecentComputer(string name)
    {
        RecentComputers.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        RecentComputers.Insert(0, name);
        if (RecentComputers.Count > 10) RecentComputers.RemoveRange(10, RecentComputers.Count - 10);
        Save();
    }
}
