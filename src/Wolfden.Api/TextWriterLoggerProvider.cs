using Microsoft.Extensions.Logging;

namespace Wolfden.Api;

/// <summary>Writes the server's log messages to a text writer: standard error in <c>wolfden serve</c>.</summary>
internal sealed class TextWriterLoggerProvider(TextWriter writer) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            try
            {
                writer.WriteLine($"wolfden serve: {level.ToString().ToLowerInvariant()}: {message}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
                writer.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Nowhere left to report it.
            }
        }
    }

    private sealed class Logger(TextWriterLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, formatter(state, exception), exception);
            }
        }
    }
}
