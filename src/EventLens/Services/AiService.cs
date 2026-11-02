using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EventLens.Models;

namespace EventLens.Services;

public sealed record AiProviderInfo(string Name, string Endpoint, string Model, bool NeedsKey, bool Anthropic = false);

public sealed record ChatMessage(string Role, string Content);

/// <summary>Bring-your-own-key AI: OpenAI-compatible chat endpoints + Anthropic Messages API.</summary>
public static class AiService
{
    public static readonly AiProviderInfo[] Providers =
    [
        new("Groq", "https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile", true),
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.5-flash", true),
        new("OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-5-mini", true),
        new("Anthropic Claude", "https://api.anthropic.com/v1/messages", "claude-haiku-5-5", true, Anthropic: true),
        new("OpenRouter", "https://openrouter.ai/api/v1/chat/completions", "openrouter/auto", true),
        new("Ollama (local)", "http://localhost:11434/v1/chat/completions", "llama3.2", false),
        new("Other (OpenAI-compatible)", "", "", true),
    ];

    public static readonly string[] Languages =
        ["English", "Serbian", "Bosnian", "Croatian", "Slovenian", "Macedonian", "German", "French", "Spanish", "Italian"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static AiProviderInfo CurrentProvider =>
        Providers.FirstOrDefault(p => p.Name == AppSettings.Current.AiProvider) ?? Providers[0];

    public static bool IsConfigured
    {
        get
        {
            var p = CurrentProvider;
            var s = AppSettings.Current;
            var endpoint = string.IsNullOrWhiteSpace(s.AiEndpoint) ? p.Endpoint : s.AiEndpoint;
            return !string.IsNullOrWhiteSpace(endpoint) && (!p.NeedsKey || !string.IsNullOrEmpty(s.GetAiKey()));
        }
    }

    public static string AnswerLanguage
    {
        get
        {
            var l = AppSettings.Current.AiLanguage;
            if (!string.IsNullOrWhiteSpace(l)) return l;
            var name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
            {
                "sr" => "Serbian",
                "bs" => "Bosnian",
                "hr" => "Croatian",
                "sl" => "Slovenian",
                "mk" => "Macedonian",
                "de" => "German",
                "fr" => "French",
                "es" => "Spanish",
                "it" => "Italian",
                _ => "English",
            };
            return name;
        }
    }

    public static string SystemPrompt =>
        "You are a senior Windows systems administrator inside EventLens, a Windows Event Log analysis tool. " +
        "Explain events precisely and practically: what happened, the most likely root cause, how serious it is, " +
        "and concrete next steps (commands, settings, what to check). Be concise: at most ~200 words unless asked for more, " +
        "use short headings and bullet points, no filler. " +
        "If the evidence is insufficient, say what additional data would confirm the cause. " +
        $"Answer in {AnswerLanguage}.";

    public static string CurrentModel
    {
        get
        {
            var m = AppSettings.Current.AiModel;
            return string.IsNullOrWhiteSpace(m) ? CurrentProvider.Model : m.Trim();
        }
    }

    /// <summary>Sends a conversation. Retries automatically (up to 2×) when the provider says "rate limited, try again in N s".</summary>
    public static async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default, Action<string>? progress = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(messages, ct);
            }
            catch (RateLimitException rl) when (attempt < 2 && rl.RetryAfter <= TimeSpan.FromSeconds(40))
            {
                var wait = rl.RetryAfter + TimeSpan.FromSeconds(1);
                progress?.Invoke($"The AI service is busy (free-tier limit). Retrying in {wait.TotalSeconds:0} s…");
                await Task.Delay(wait, ct);
            }
        }
    }

    private sealed class RateLimitException(string message, TimeSpan retryAfter) : Exception(message)
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    private static async Task<string> SendOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var p = CurrentProvider;
        var s = AppSettings.Current;
        var endpoint = string.IsNullOrWhiteSpace(s.AiEndpoint) ? p.Endpoint : s.AiEndpoint.Trim();
        var model = CurrentModel;
        var key = s.GetAiKey();

        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("AI endpoint is not set (Settings → AI).");
        if (p.NeedsKey && string.IsNullOrEmpty(key)) throw new InvalidOperationException("AI API key is not set (Settings → AI).");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        JsonObject body;

        if (p.Anthropic)
        {
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            var arr = new JsonArray();
            foreach (var m in messages)
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 1500,
                ["system"] = SystemPrompt,
                ["messages"] = arr,
            };
        }
        else
        {
            if (!string.IsNullOrEmpty(key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var arr = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt } };
            foreach (var m in messages)
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            body = new JsonObject { ["model"] = model, ["messages"] = arr };
        }

        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if ((int)resp.StatusCode == 429)
        {
            var msg = ExtractError(text) ?? "Rate limit reached.";
            var retry = resp.Headers.RetryAfter?.Delta ?? ParseRetry(msg) ?? TimeSpan.FromSeconds(10);
            throw new RateLimitException(
                $"The AI service rate limit was reached ({p.Name} free tier allows a limited number of tokens per minute). Wait a moment and press Retry, or pick another service in Settings.",
                retry);
        }

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI error {(int)resp.StatusCode}: {Trim(ExtractError(text) ?? text, 400)}");

        var json = JsonNode.Parse(text);
        string? answer = p.Anthropic
            ? json?["content"]?.AsArray().Select(c => c?["text"]?.GetValue<string>()).FirstOrDefault(t => t is not null)
            : json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(answer) ? "(empty answer)" : answer.Trim();
    }

    private static TimeSpan? ParseRetry(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"try again in (?:(\d+)m)?([\d.]+)s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var min = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
            ? TimeSpan.FromSeconds(min * 60 + sec)
            : null;
    }

    /// <summary>Stable key for caching an answer to the same question about the same evidence.</summary>
    public static string CacheKey(string mode, string context, string question)
    {
        var raw = $"{CurrentProvider.Name}|{CurrentModel}|{AnswerLanguage}|{mode}|{question}|{context}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string? ExtractError(string json)
    {
        try { return JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------- context builders (kept small: free tiers allow ~6-8k tokens per minute) ----------

    /// <summary>Only the one event (≈300-800 tokens).</summary>
    public static string BuildEventContext(EventItem e, int occurrences = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Windows event on {AppState.Source.ShortName} ({Environment.OSVersion.VersionString})");
        sb.AppendLine($"Time: {e.TimeCreated:yyyy-MM-dd HH:mm:ss} | Log: {e.LogName} | Source: {e.Provider} | ID: {e.Id} | Level: {e.Severity}");
        if (occurrences > 1) sb.AppendLine($"Occurred {occurrences} times in the loaded period.");
        sb.AppendLine("Message:");
        sb.AppendLine(Trim(e.Message, 1500));
        var data = e.Data.Where(d => !string.IsNullOrWhiteSpace(d) && d != "-").ToList();
        if (data.Count > 0 && string.IsNullOrWhiteSpace(e.Message)) sb.AppendLine("Data: " + Trim(string.Join(" | ", data), 400));
        if (e.HasExplanation) sb.AppendLine($"EventLens knowledge base says: {e.Explanation!.DisplayTitle} - {e.Explanation.What}");
        foreach (var c in e.Explanation?.Codes ?? [])
            sb.AppendLine($"Code {c.Code}: {c.Meaning}");
        return sb.ToString();
    }

    /// <summary>The event plus up to 12 errors/warnings around it, one line each (≈1500-2500 tokens).</summary>
    public static string BuildDeepContext(EventItem e, IEnumerable<EventItem> nearby, int occurrences = 1)
    {
        var sb = new StringBuilder(BuildEventContext(e, occurrences));
        var near = nearby.Where(n => n.RecordId != e.RecordId || n.LogName != e.LogName).Take(12).ToList();
        if (near.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Other errors/warnings within ±10 minutes:");
            foreach (var n in near)
                sb.AppendLine($"- {n.TimeCreated:HH:mm:ss} {n.Severity} {n.Provider} {n.Id}: {Trim(OneLine(n.Message), 160)}");
        }
        return sb.ToString();
    }

    /// <summary>Follow-up questions do not resend the evidence - only a short reminder and the previous answer.</summary>
    public static ChatMessage FollowUp(string subject, string previousAnswer, string question) =>
        new("user", $"We are discussing: {subject}\n\nYour previous answer:\n{Trim(previousAnswer, 1200)}\n\nFollow-up question: {question}");

    public static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    public static string Short(string s, int max) => Trim(OneLine(s), max);
}
