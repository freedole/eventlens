using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using EventLens.Core.Knowledge;
using EventLens.Services;
using Wpf.Ui.Appearance;

namespace EventLens;

public partial class App : Application
{
    public static string LogFolder => Path.Combine(AppSettings.Folder, "logs");
    private static bool _showingCrash;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        ApplicationThemeManager.ApplySystemTheme();

        HistoryStore.Init();
        AppState.LoadMuted();
        KnowledgeBase.CacheFolder = OnlineServices.KnowledgeFolder;
        KnowledgeBase.EnsureLoaded();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    // ---------- crash handling ----------

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        var path = WriteCrashLog(args.Exception);
        if (_showingCrash) return;
        _showingCrash = true;
        _ = ShowCrashAsync(args.Exception, path).ContinueWith(_ => _showingCrash = false, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static async Task ShowCrashAsync(Exception ex, string? path)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Something went wrong",
            Content = $"{ex.Message}\n\nEventLens kept running. Details were saved to a log file{(path is null ? "" : ":\n" + path)}",
            PrimaryButtonText = "Copy details",
            SecondaryButtonText = "Open log folder",
            CloseButtonText = "Close",
        };
        var result = await box.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            try { Clipboard.SetText(Details(ex)); } catch { /* clipboard busy */ }
        }
        else if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
        {
            OpenLogFolder();
        }
    }

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            Process.Start(new ProcessStartInfo(LogFolder) { UseShellExecute = true });
        }
        catch { /* explorer not available */ }
    }

    private static string Details(Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"EventLens {OnlineServices.AppVersion}");
        sb.AppendLine($"{Environment.OSVersion.VersionString} · .NET {Environment.Version}");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(ex?.ToString() ?? "(no exception object)");
        return sb.ToString();
    }

    private static string? WriteCrashLog(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Details(ex));
            return path;
        }
        catch
        {
            return null;
        }
    }
}
