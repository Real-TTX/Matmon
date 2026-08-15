namespace Matmon.Host.Services;

/// <summary>
/// An <see cref="ILoggerProvider"/> that tees every log line into the process-local <see cref="InMemoryLogStore"/>
/// so the admin "Logs" page can show them in the UI (no need to shell into <c>docker logs</c>). Registered in
/// Program.cs via <c>builder.Logging.AddProvider(...)</c>, it inherits the same category/level filters as the
/// other providers (appsettings <c>Logging:LogLevel</c>), so it captures exactly what the console does.
/// </summary>
[ProviderAlias("Ring")]
public sealed class RingBufferLoggerProvider(InMemoryLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RingLogger(store, categoryName);

    public void Dispose() { }

    private sealed class RingLogger(InMemoryLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }
            store.Add(logLevel, category, message, exception?.ToString());
        }
    }
}
