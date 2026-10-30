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
        new("Groq", "https://api.groq.com/openai/v1/chat/completions", "openai/gpt-oss-120b", true),
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
        "and concrete next steps (commands, settings, what to check). Be concise, use short sections and bullet points, " +
        "no filler. If the evidence is insufficient, say what additional data would confirm the cause. " +
        $"Answer in {AnswerLanguage}.";

    public static async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var p = CurrentProvider;
        var s = AppSettings.Current;
        var endpoint = string.IsNullOrWhiteSpace(s.AiEndpoint) ? p.Endpoint : s.AiEndpoint.Trim();
        var model = string.IsNullOrWhiteSpace(s.AiModel) ? p.Model : s.AiModel.Trim();
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
                ["max_tokens"] = 2048,
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

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI error {(int)resp.StatusCode}: {Trim(ExtractError(text) ?? text, 400)}");

        var json = JsonNode.Parse(text);
        string? answer = p.Anthropic
            ? json?["content"]?.AsArray().Select(c => c?["text"]?.GetValue<string>()).FirstOrDefault(t => t is not null)
            : json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(answer) ? "(empty answer)" : answer.Trim();
    }

    private static string? ExtractError(string json)
    {
        try { return JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------- context builders ----------

    public static string BuildEventContext(EventItem e, IEnumerable<EventItem>? nearby = null, int occurrences = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Computer: {AppState.Source.ShortName} ({AppState.Source.KindText})");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString}");
        sb.AppendLine();
        sb.AppendLine("EVENT");
        sb.AppendLine($"Time: {e.TimeCreated:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Log: {e.LogName}  Source: {e.Provider}  Event ID: {e.Id}  Level: {e.Severity}");
        if (occurrences > 1) sb.AppendLine($"Occurrences in the loaded period: {occurrences}");
        sb.AppendLine("Message:");
        sb.AppendLine(Trim(e.Message, 4000));
        if (e.Data.Count > 0)
            sb.AppendLine("Event data: " + Trim(string.Join(" | ", e.Data.Where(d => !string.IsNullOrWhiteSpace(d))), 1500));

        var near = nearby?.Where(n => n.RecordId != e.RecordId || n.LogName != e.LogName).Take(20).ToList();
        if (near is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("OTHER EVENTS AROUND THE SAME TIME (±10 min):");
            foreach (var n in near)
                sb.AppendLine($"- {n.TimeCreated:HH:mm:ss} {n.Severity} {n.LogName}/{n.Provider} ID {n.Id}: {Trim(OneLine(n.Message), 200)}");
        }
        return sb.ToString();
    }

    public static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
}
