using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventLens.Services;
using EventLens.Views;
using Microsoft.Win32;

namespace EventLens.ViewModels;

public sealed class ComputersViewModel : ObservableObject
{
    private string _computer = "";
    private string _user = "";
    private bool _isBusy;
    private string _status = "";
    private bool _statusIsError;

    public ObservableCollection<string> Recent { get; } = new(AppSettings.Current.RecentComputers);

    public string Computer { get => _computer; set => SetProperty(ref _computer, value); }
    public string User { get => _user; set => SetProperty(ref _user, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; set => SetProperty(ref _statusIsError, value); }

    public string CurrentName => AppState.Source.DisplayName;
    public string CurrentKind => AppState.Source.KindText;
    public bool IsNotLocal => AppState.Source.Kind != SourceKind.Local;
    public string CurrentIcon => AppState.Source.Kind switch
    {
        SourceKind.Remote => "DesktopArrowRight24",
        SourceKind.File => "DocumentText24",
        _ => "Desktop24",
    };

    public IRelayCommand UseLocalCommand { get; }
    public IRelayCommand OpenFileCommand { get; }
    public IRelayCommand<string> PickRecentCommand { get; }

    public ComputersViewModel()
    {
        UseLocalCommand = new RelayCommand(() => Switch(EventSource.Local, "Switched to this computer."));
        OpenFileCommand = new RelayCommand(OpenFile);
        PickRecentCommand = new RelayCommand<string>(n => { if (!string.IsNullOrWhiteSpace(n)) Computer = n; });

        AppState.SourceChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentName));
            OnPropertyChanged(nameof(CurrentKind));
            OnPropertyChanged(nameof(IsNotLocal));
            OnPropertyChanged(nameof(CurrentIcon));
        };
    }

    public async Task ConnectAsync(string password)
    {
        var name = Computer.Trim();
        if (name.Length == 0)
        {
            SetStatus("Enter a computer name or IP address.", true);
            return;
        }

        SecureString? secure = null;
        if (!string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(password))
        {
            secure = new SecureString();
            foreach (var ch in password) secure.AppendChar(ch);
            secure.MakeReadOnly();
        }

        var source = EventSource.Remote(name, string.IsNullOrWhiteSpace(User) ? null : User.Trim(), secure);
        IsBusy = true;
        SetStatus($"Connecting to {name}…", false);
        try
        {
            await EventLogService.TestAsync(source);
            AppSettings.Current.AddRecentComputer(name);
            if (!Recent.Contains(name, StringComparer.OrdinalIgnoreCase)) Recent.Insert(0, name);
            Switch(source, $"Connected to {name}.");
            Navigator.Navigate(typeof(DashboardPage));
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus("Access denied. Use an account that is a local administrator (or member of Event Log Readers) on the target.", true);
        }
        catch (EventLogException ex)
        {
            SetStatus($"Could not connect: {ex.Message.Trim()} - check that the computer is online and the firewall allows " +
                      "\"Remote Event Log Management\" (RPC).", true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not connect: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open event log file",
            Filter = "Event log files (*.evtx)|*.evtx|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        var source = EventSource.File(dlg.FileName);
        try
        {
            using var reader = new EventLogReader(source.CreateQuery("", "*"));
            using var _ = reader.ReadEvent();
            Switch(source, $"Opened {System.IO.Path.GetFileName(dlg.FileName)}.");
            Navigator.Navigate(typeof(EventsPage));
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the file: " + ex.Message, true);
        }
    }

    private void Switch(EventSource source, string message)
    {
        AppState.Source = source;
        SetStatus(message, false);
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }
}
