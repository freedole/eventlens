using System.Reflection;
using System.Security.Principal;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Services;
using Wpf.Ui.Appearance;

namespace EventLens.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private int _themeIndex;
    private int _providerIndex;
    private int _languageIndex;
    private string _endpoint;
    private string _model;
    private string _aiStatus = "";
    private bool _aiBusy;

    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<string> ProviderNames { get; } = AiService.Providers.Select(p => p.Name).ToList();
    public IReadOnlyList<string> LanguageNames { get; } = ["Automatic (Windows language)", .. AiService.Languages];

    public SettingsViewModel()
    {
        var s = AppSettings.Current;
        _themeIndex = Math.Clamp(s.Theme, 0, 2);
        _providerIndex = Math.Max(0, Array.FindIndex(AiService.Providers, p => p.Name == s.AiProvider));
        _languageIndex = string.IsNullOrWhiteSpace(s.AiLanguage) ? 0 : Math.Max(0, Array.IndexOf(AiService.Languages, s.AiLanguage) + 1);
        _endpoint = s.AiEndpoint;
        _model = s.AiModel;

        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsAdmin);
        TestAiCommand = new AsyncRelayCommand(TestAiAsync);
    }

    /// <summary>Applies the saved theme at startup.</summary>
    public void ApplySavedTheme() => ApplyTheme(_themeIndex);

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, value)) return;
            AppSettings.Current.Theme = value;
            AppSettings.Current.Save();
            ApplyTheme(value);
        }
    }

    // ---------- AI ----------

    public int ProviderIndex
    {
        get => _providerIndex;
        set
        {
            if (!SetProperty(ref _providerIndex, value) || value < 0) return;
            AppSettings.Current.AiProvider = AiService.Providers[value].Name;
            // switching provider resets custom endpoint/model to that provider's defaults
            Endpoint = "";
            Model = "";
            OnPropertyChanged(nameof(EndpointPlaceholder));
            OnPropertyChanged(nameof(ModelPlaceholder));
            OnPropertyChanged(nameof(KeyHint));
            AppSettings.Current.Save();
        }
    }

    public int LanguageIndex
    {
        get => _languageIndex;
        set
        {
            if (!SetProperty(ref _languageIndex, value)) return;
            AppSettings.Current.AiLanguage = value <= 0 ? "" : AiService.Languages[value - 1];
            AppSettings.Current.Save();
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (!SetProperty(ref _endpoint, value)) return;
            AppSettings.Current.AiEndpoint = value.Trim();
            AppSettings.Current.Save();
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (!SetProperty(ref _model, value)) return;
            AppSettings.Current.AiModel = value.Trim();
            AppSettings.Current.Save();
        }
    }

    public string EndpointPlaceholder => AiService.Providers[Math.Max(0, _providerIndex)].Endpoint is { Length: > 0 } e ? e : "https://…/v1/chat/completions";
    public string ModelPlaceholder => AiService.Providers[Math.Max(0, _providerIndex)].Model is { Length: > 0 } m ? m : "model name";

    public string KeyHint
    {
        get
        {
            var p = AiService.Providers[Math.Max(0, _providerIndex)];
            if (!p.NeedsKey) return "No key needed - Ollama must be running on this PC.";
            var saved = string.IsNullOrEmpty(AppSettings.Current.GetAiKey()) ? "No key saved." : "A key is saved (encrypted).";
            return p.Name.StartsWith("Groq") ? $"{saved} Free key: console.groq.com → API Keys." : saved;
        }
    }

    public string AiStatus { get => _aiStatus; set => SetProperty(ref _aiStatus, value); }
    public bool AiBusy { get => _aiBusy; set => SetProperty(ref _aiBusy, value); }

    public void SaveKey(string key)
    {
        AppSettings.Current.SetAiKey(key.Trim());
        AppSettings.Current.Save();
        OnPropertyChanged(nameof(KeyHint));
        AiStatus = string.IsNullOrWhiteSpace(key) ? "Key removed." : "Key saved.";
    }

    public IAsyncRelayCommand TestAiCommand { get; }

    private async Task TestAiAsync()
    {
        AiBusy = true;
        AiStatus = "Testing…";
        try
        {
            var answer = await AiService.ChatAsync([new ChatMessage("user", "Reply with exactly: EventLens OK")]);
            AiStatus = "✓ Works: " + (answer.Length > 80 ? answer[..80] + "…" : answer);
        }
        catch (Exception ex)
        {
            AiStatus = "✗ " + ex.Message;
        }
        finally
        {
            AiBusy = false;
        }
    }

    // ---------- about / permissions ----------

    public string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    public string KnowledgeInfo => $"{EventKnowledge.Count} known event types explained";

    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public string AdminText => IsAdmin ? "Running as administrator - all logs available" : "Not elevated - the Security log is not readable";

    public IRelayCommand RestartAsAdminCommand { get; }

    private static void ApplyTheme(int index)
    {
        var window = Application.Current.MainWindow;
        if (index == 0)
        {
            if (window is not null) SystemThemeWatcher.Watch(window);
            ApplicationThemeManager.ApplySystemTheme();
            return;
        }
        if (window is not null) SystemThemeWatcher.UnWatch(window);
        ApplicationThemeManager.Apply(index == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }

    private static void RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC prompt cancelled
        }
    }
}
