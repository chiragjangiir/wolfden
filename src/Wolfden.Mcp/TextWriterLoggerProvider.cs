using Microsoft.Extensions.Logging;

namespace Wolfden.Mcp;

/// <summary>
/// Writes the MCP SDK's log messages to a text writer, standard error in
/// <c>wolfden mcp</c>, since standard output carries the protocol.
/// </summary>
internal sealed class TextWriterLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public TextWriterLoggerProvider(TextWriter writer)
    {
        _writer = writer;
    }

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
                _writer.WriteLine($"wolfden mcp: {level.ToString().ToLowerInvariant()}: {message}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
                _writer.Flush();
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
