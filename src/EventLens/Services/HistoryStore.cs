using System.IO;
using EventLens.Core.Findings;
using EventLens.Models;
using Microsoft.Data.Sqlite;

namespace EventLens.Services;

public sealed record ProblemHistory(string Key, string Title, string Severity, string Category, DateTime FirstSeen, DateTime LastSeen, int Total, int Last7, int Prev7)
{
    public string TrendText =>
        Last7 > Prev7 * 1.5 && Last7 - Prev7 >= 2 ? "↑ getting worse"
        : Prev7 > Last7 * 1.5 && Prev7 - Last7 >= 2 ? "↓ getting better"
        : Last7 == 0 && Prev7 == 0 ? "" : "→ steady";

    public string SinceText => $"Seen {Total}× on this PC since {FirstSeen:d MMM yyyy}";
}

public sealed record SavedView(string Name, string Json);

/// <summary>
/// Local SQLite database (%AppData%\EventLens\history.db): problem history, user notes,
/// "mark as harmless" list, cached AI answers and saved views. Nothing leaves the PC.
/// </summary>
public static class HistoryStore
{
    private static string? _cs;
    private static readonly object Gate = new();

    public static string DbPath => Path.Combine(AppSettings.Folder, "history.db");
    public static bool Available => _cs is not null;

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            _cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Cache = SqliteCacheMode.Shared }.ToString();
            Exec("""
                CREATE TABLE IF NOT EXISTS problems(key TEXT PRIMARY KEY, title TEXT, severity TEXT, category TEXT, first_seen TEXT, last_seen TEXT);
                CREATE TABLE IF NOT EXISTS problem_days(key TEXT, day TEXT, count INTEGER, PRIMARY KEY(key, day));
                CREATE TABLE IF NOT EXISTS event_days(key TEXT, day TEXT, count INTEGER, PRIMARY KEY(key, day));
                CREATE TABLE IF NOT EXISTS notes(key TEXT PRIMARY KEY, text TEXT, updated TEXT);
                CREATE TABLE IF NOT EXISTS muted(key TEXT PRIMARY KEY, created TEXT);
                CREATE TABLE IF NOT EXISTS ai_cache(hash TEXT PRIMARY KEY, answer TEXT, provider TEXT, model TEXT, created TEXT);
                CREATE TABLE IF NOT EXISTS views(name TEXT PRIMARY KEY, json TEXT, created TEXT);
                """);
        }
        catch
        {
            _cs = null; // history is optional - the app works without it
        }
    }

    // ---------- plumbing ----------

    private static void Exec(string sql, params (string, object?)[] args)
    {
        if (_cs is null) return;
        lock (Gate)
        {
            using var c = new SqliteConnection(_cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        var list = new List<T>();
        if (_cs is null) return list;
        lock (Gate)
        {
            using var c = new SqliteConnection(_cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(map(r));
        }
        return list;
    }

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd");
    private static string Now => DateTime.Now.ToString("s");
    private static DateTime ParseDate(string s) => DateTime.TryParse(s, out var d) ? d : DateTime.MinValue;

    private static void Safe(Action a)
    {
        try { a(); } catch { /* history is best effort */ }
    }

    // ---------- findings / problem history ----------

    public static void RecordFindings(IEnumerable<Finding> findings)
    {
        Safe(() =>
        {
            if (_cs is null) return;
            lock (Gate)
            {
                using var c = new SqliteConnection(_cs);
                c.Open();
                using var tx = c.BeginTransaction();
                foreach (var f in findings)
                {
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            INSERT INTO problems(key,title,severity,category,first_seen,last_seen) VALUES($k,$t,$s,$c,$f,$l)
                            ON CONFLICT(key) DO UPDATE SET title=$t, severity=$s, category=$c,
                              first_seen=MIN(first_seen,$f), last_seen=MAX(last_seen,$l);
                            """;
                        cmd.Parameters.AddWithValue("$k", f.Key);
                        cmd.Parameters.AddWithValue("$t", f.Title);
                        cmd.Parameters.AddWithValue("$s", f.Severity.ToString());
                        cmd.Parameters.AddWithValue("$c", f.Category);
                        cmd.Parameters.AddWithValue("$f", f.FirstSeen.ToString("s"));
                        cmd.Parameters.AddWithValue("$l", f.LastSeen.ToString("s"));
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var (day, count) in f.PerDay)
                    {
                        using var cmd = c.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO problem_days(key,day,count) VALUES($k,$d,$n) ON CONFLICT(key,day) DO UPDATE SET count=MAX(count,$n);";
                        cmd.Parameters.AddWithValue("$k", f.Key);
                        cmd.Parameters.AddWithValue("$d", Day(day));
                        cmd.Parameters.AddWithValue("$n", count);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        });
    }

    public static ProblemHistory? GetProblem(string key)
    {
        try
        {
            var today = DateTime.Today;
            return Query<ProblemHistory>("""
                SELECT p.key, p.title, p.severity, p.category, p.first_seen, p.last_seen,
                  IFNULL((SELECT SUM(count) FROM problem_days d WHERE d.key=p.key),0),
                  IFNULL((SELECT SUM(count) FROM problem_days d WHERE d.key=p.key AND d.day > $w1),0),
                  IFNULL((SELECT SUM(count) FROM problem_days d WHERE d.key=p.key AND d.day > $w2 AND d.day <= $w1),0)
                FROM problems p WHERE p.key=$k
                """,
                MapProblem, ("$k", key), ("$w1", Day(today.AddDays(-7))), ("$w2", Day(today.AddDays(-14)))).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Problems seen in the last 90 days that have not appeared in the last 7 days.</summary>
    public static List<ProblemHistory> GetResolved(IEnumerable<string> activeKeys)
    {
        try
        {
            var active = activeKeys.ToHashSet();
            var today = DateTime.Today;
            return Query<ProblemHistory>("""
                SELECT p.key, p.title, p.severity, p.category, p.first_seen, p.last_seen,
                  IFNULL((SELECT SUM(count) FROM problem_days d WHERE d.key=p.key),0), 0, 0
                FROM problems p WHERE p.last_seen < $cut AND p.last_seen > $old ORDER BY p.last_seen DESC LIMIT 30
                """,
                MapProblem, ("$cut", today.AddDays(-7).ToString("s")), ("$old", today.AddDays(-90).ToString("s")))
                .Where(p => !active.Contains(p.Key)).ToList();
        }
        catch { return []; }
    }

    private static ProblemHistory MapProblem(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        ParseDate(r.GetString(4)), ParseDate(r.GetString(5)), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8));

    // ---------- per event type history ----------

    public static void RecordEvents(IEnumerable<EventItem> events)
    {
        Safe(() =>
        {
            if (_cs is null) return;
            var groups = events.Where(e => e.IsProblem).GroupBy(e => (e.TypeKey, Day(e.TimeCreated))).ToList();
            if (groups.Count == 0) return;
            lock (Gate)
            {
                using var c = new SqliteConnection(_cs);
                c.Open();
                using var tx = c.BeginTransaction();
                foreach (var g in groups)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO event_days(key,day,count) VALUES($k,$d,$n) ON CONFLICT(key,day) DO UPDATE SET count=MAX(count,$n);";
                    cmd.Parameters.AddWithValue("$k", g.Key.TypeKey);
                    cmd.Parameters.AddWithValue("$d", g.Key.Item2);
                    cmd.Parameters.AddWithValue("$n", g.Count());
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        });
    }

    /// <summary>"Seen 14× on this PC since 3 Oct 2026" for an event type, or null.</summary>
    public static string? EventHistoryText(string typeKey)
    {
        try
        {
            var r = Query("SELECT MIN(day), IFNULL(SUM(count),0), COUNT(*) FROM event_days WHERE key=$k",
                x => (First: x.IsDBNull(0) ? null : x.GetString(0), Total: x.GetInt32(1), Days: x.GetInt32(2)), ("$k", typeKey)).FirstOrDefault();
            if (r.First is null || r.Total == 0) return null;
            return $"Seen {r.Total}× on {r.Days} day(s) on this PC since {ParseDate(r.First):d MMM yyyy}";
        }
        catch { return null; }
    }

    // ---------- notes ----------

    public static string GetNote(string key)
    {
        try { return Query("SELECT text FROM notes WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault() ?? ""; }
        catch { return ""; }
    }

    public static void SetNote(string key, string text) => Safe(() =>
    {
        if (string.IsNullOrWhiteSpace(text)) Exec("DELETE FROM notes WHERE key=$k", ("$k", key));
        else Exec("INSERT INTO notes(key,text,updated) VALUES($k,$t,$u) ON CONFLICT(key) DO UPDATE SET text=$t, updated=$u", ("$k", key), ("$t", text.Trim()), ("$u", Now));
    });

    // ---------- muted ("mark as harmless") ----------

    public static HashSet<string> GetMuted()
    {
        try { return Query("SELECT key FROM muted", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public static void SetMuted(string key, bool muted) => Safe(() =>
    {
        if (muted) Exec("INSERT OR IGNORE INTO muted(key,created) VALUES($k,$c)", ("$k", key), ("$c", Now));
        else Exec("DELETE FROM muted WHERE key=$k", ("$k", key));
    });

    // ---------- AI cache ----------

    public static (string Answer, DateTime Created)? GetAi(string hash)
    {
        try
        {
            var r = Query("SELECT answer, created FROM ai_cache WHERE hash=$h", x => (x.GetString(0), ParseDate(x.GetString(1))), ("$h", hash));
            return r.Count == 0 ? null : r[0];
        }
        catch { return null; }
    }

    public static void PutAi(string hash, string answer, string provider, string model) => Safe(() =>
        Exec("INSERT INTO ai_cache(hash,answer,provider,model,created) VALUES($h,$a,$p,$m,$c) ON CONFLICT(hash) DO UPDATE SET answer=$a, created=$c",
            ("$h", hash), ("$a", answer), ("$p", provider), ("$m", model), ("$c", Now)));

    public static int ClearAiCache()
    {
        try
        {
            var n = Query("SELECT COUNT(*) FROM ai_cache", r => r.GetInt32(0)).FirstOrDefault();
            Exec("DELETE FROM ai_cache");
            return n;
        }
        catch { return 0; }
    }

    // ---------- saved views ----------

    public static List<SavedView> GetViews()
    {
        try { return Query("SELECT name, json FROM views ORDER BY name", r => new SavedView(r.GetString(0), r.GetString(1))); }
        catch { return []; }
    }

    public static void SaveView(string name, string json) => Safe(() =>
        Exec("INSERT INTO views(name,json,created) VALUES($n,$j,$c) ON CONFLICT(name) DO UPDATE SET json=$j", ("$n", name.Trim()), ("$j", json), ("$c", Now)));

    public static void DeleteView(string name) => Safe(() => Exec("DELETE FROM views WHERE name=$n", ("$n", name)));
}
