using System.Diagnostics.Eventing.Reader;
using System.Security;

namespace EventLens.Services;

public enum SourceKind { Local, Remote, File }

/// <summary>Where events are read from: this PC, a remote PC, or an exported .evtx file.</summary>
public sealed class EventSource
{
    public SourceKind Kind { get; private init; }
    public string Computer { get; private init; } = Environment.MachineName;
    public string? Domain { get; private init; }
    public string? User { get; private init; }
    public SecureString? Password { get; private init; }
    public string? FilePath { get; private init; }

    public static EventSource Local { get; } = new() { Kind = SourceKind.Local };

    public static EventSource Remote(string computer, string? user, SecureString? password)
    {
        string? domain = null;
        if (!string.IsNullOrWhiteSpace(user) && user.Contains('\\'))
        {
            var parts = user.Split('\\', 2);
            domain = parts[0];
            user = parts[1];
        }
        return new EventSource
        {
            Kind = SourceKind.Remote,
            Computer = computer.Trim(),
            Domain = domain,
            User = string.IsNullOrWhiteSpace(user) ? null : user,
            Password = password,
        };
    }

    public static EventSource File(string path) => new()
    {
        Kind = SourceKind.File,
        FilePath = path,
        Computer = System.IO.Path.GetFileName(path),
    };

    public string DisplayName => Kind switch
    {
        SourceKind.Local => $"{Environment.MachineName} (this computer)",
        SourceKind.Remote => Computer,
        _ => Computer,
    };

    public string ShortName => Kind == SourceKind.Local ? Environment.MachineName : Computer;

    public string KindText => Kind switch
    {
        SourceKind.Local => "This computer",
        SourceKind.Remote => "Remote computer",
        _ => "Log file",
    };

    /// <summary>In file mode the requested log name is ignored - the file is the log.</summary>
    public bool IgnoresLogName => Kind == SourceKind.File;

    public EventLogQuery CreateQuery(string log, string xpath)
    {
        var q = Kind == SourceKind.File
            ? new EventLogQuery(FilePath!, PathType.FilePath, xpath)
            : new EventLogQuery(log, PathType.LogName, xpath);
        q.ReverseDirection = true;

        if (Kind == SourceKind.Remote)
        {
            q.Session = User is null
                ? new EventLogSession(Computer)
                : new EventLogSession(Computer, Domain, User, Password, SessionAuthentication.Default);
        }
        return q;
    }
}

/// <summary>App-wide current source; pages reload when it changes.</summary>
public static class AppState
{
    private static EventSource _source = EventSource.Local;

    public static EventSource Source
    {
        get => _source;
        set
        {
            _source = value;
            SourceChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? SourceChanged;
}
