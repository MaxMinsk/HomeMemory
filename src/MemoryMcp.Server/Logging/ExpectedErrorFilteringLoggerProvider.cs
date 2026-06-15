using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace MemoryMcp.Server.Logging;

/// <summary>
/// Wraps a logger provider and drops log records whose exception is an <see cref="McpException"/>.
/// Those are expected, model-visible tool errors (bad input, out-of-scope domain, invalid filter) that
/// the MCP SDK turns into <c>isError</c> results — not server faults. Without this they are logged at
/// error level with full stack traces, making routine agent mistakes look like crashes. Genuine
/// (non-<see cref="McpException"/>) exceptions still log normally.
/// </summary>
internal sealed class ExpectedErrorFilteringLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;

    public ExpectedErrorFilteringLoggerProvider(ILoggerProvider inner) => _inner = inner;

    public ILogger CreateLogger(string categoryName) => new FilteringLogger(_inner.CreateLogger(categoryName));

    public void Dispose() => _inner.Dispose();

    internal sealed class FilteringLogger : ILogger
    {
        private readonly ILogger _inner;

        public FilteringLogger(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is McpException)
            {
                return; // expected, already surfaced to the model — not a server fault
            }

            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
