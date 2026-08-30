using Microsoft.Extensions.Logging;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Adapts the shared bridge's Microsoft logging surface to the SS14 sawmill.
/// </summary>
internal sealed class COGRSawmillLogger : ILogger
{
    private readonly ISawmill _sawmill;

    public COGRSawmillLogger(ISawmill sawmill)
    {
        _sawmill = sawmill ?? throw new ArgumentNullException(nameof(sawmill));
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
        logLevel != Microsoft.Extensions.Logging.LogLevel.None;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (exception != null)
            message = $"{message}: {exception}";

        switch (logLevel)
        {
            case Microsoft.Extensions.Logging.LogLevel.Trace:
                _sawmill.Verbose("{0}", message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Debug:
                _sawmill.Debug("{0}", message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Information:
                _sawmill.Info("{0}", message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Warning:
                _sawmill.Warning("{0}", message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Error:
            case Microsoft.Extensions.Logging.LogLevel.Critical:
                _sawmill.Error("{0}", message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.None:
                break;
            default:
                _sawmill.Debug("{0}", message);
                break;
        }
    }
}
