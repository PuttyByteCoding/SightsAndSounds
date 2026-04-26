namespace VideoOrganizer.API.Services;

// ILoggerProvider that mirrors every log event into a LogBuffer so the /logs
// page can display a live feed without touching disk. The minimum level is
// read per-call from the configured Logging:LogLevel so this provider honors
// the same filters the console provider already does.
public sealed class BufferedLoggerProvider : ILoggerProvider
{
    private readonly LogBuffer _buffer;

    public BufferedLoggerProvider(LogBuffer buffer) { _buffer = buffer; }

    public ILogger CreateLogger(string categoryName) => new BufferedLogger(categoryName, _buffer);

    public void Dispose() { }

    private sealed class BufferedLogger : ILogger
    {
        private readonly string _category;
        private readonly LogBuffer _buffer;

        public BufferedLogger(string category, LogBuffer buffer)
        {
            _category = category;
            _buffer = buffer;
        }

        // Scopes aren't surfaced to the feed; callers who rely on BeginScope
        // still get their Console output, just without scope state in /logs.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Accept every level here; filter runs in the host's logging pipeline
        // via the built-in LoggerFilterRule configuration.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None) return;
            var message = formatter(state, exception);
            _buffer.Add(new LogEvent(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                _category,
                message,
                exception?.ToString()));
        }
    }
}
