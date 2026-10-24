using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Relay.Hosting;

/// <summary>One line of the relay's activity: a start, a join, a refusal, a departure.</summary>
public sealed record RelayActivity(DateTimeOffset Timestamp, LogLevel Level, string Message);

/// <summary>
/// The relay's recent activity, kept in memory for the settings page. Older entries fall off once
/// <see cref="Capacity"/> is reached; the server's own log keeps everything.
/// </summary>
public sealed class RelayActivityLog(TimeProvider time)
{
    public const int Capacity = 500;

    private readonly Lock _lock = new();
    private readonly Queue<RelayActivity> _entries = new();

    /// <summary>Raised after an entry is added or the log is cleared.</summary>
    public event Action? Changed;

    /// <summary>The entries, newest first.</summary>
    public IReadOnlyList<RelayActivity> Entries
    {
        get
        {
            lock (_lock)
                return _entries.Reverse().ToArray();
        }
    }

    public void Add(LogLevel level, string message)
    {
        lock (_lock)
        {
            _entries.Enqueue(new RelayActivity(time.GetUtcNow(), level, message));

            while (_entries.Count > Capacity)
                _entries.Dequeue();
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();

        Changed?.Invoke();
    }

    /// <summary>
    /// A logger that writes to <paramref name="inner"/> and records what is worth showing an
    /// administrator. The relay library reports joins, departures and refusals only through its
    /// logger, so this is how they reach the page.
    /// </summary>
    public ILogger<T> Tee<T>(ILogger<T> inner) => new TeeLogger<T>(inner, this);

    private sealed class TeeLogger<T>(ILogger<T> inner, RelayActivityLog log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        // Information and above is recorded whatever the host's own log level, so the page shows
        // the relay's activity even on a server that logs only warnings.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information || inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (inner.IsEnabled(logLevel))
                inner.Log(logLevel, eventId, state, exception, formatter);

            if (logLevel < LogLevel.Information)
                return;

            var message = formatter(state, exception);

            if (exception is not null && logLevel >= LogLevel.Warning)
                message = $"{message}: {exception.Message}";

            log.Add(logLevel, message);
        }
    }
}
