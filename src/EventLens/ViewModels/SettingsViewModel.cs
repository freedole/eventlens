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

    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    public int ThemeIndex
    {
        get => _themeIndex;
        set { if (SetProperty(ref _themeIndex, value)) ApplyTheme(value); }
    }

    public string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    public string KnowledgeInfo => $"{EventKnowledge.Count} known event types explained";

    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public string AdminText => IsAdmin ? "Running as administrator - all logs available" : "Not elevated - the Security log is not readable";

    public IRelayCommand RestartAsAdminCommand { get; }

    public SettingsViewModel()
    {
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsAdmin);
    }

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
