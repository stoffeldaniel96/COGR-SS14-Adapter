namespace Content.Server.COGR;

/// <summary>
/// Configuration for the COGR adapter.
/// </summary>
public sealed class COGRAdapterConfiguration
{
    /// <summary>
    /// Gets or sets the COGR runtime endpoint.
    /// </summary>
    /// <example>http://localhost:5050</example>
    public string RuntimeEndpoint { get; set; } = "http://localhost:5050";

    /// <summary>
    /// Gets or sets the launch token for authentication.
    /// </summary>
    public string LaunchToken { get; set; } = "";

    /// <summary>
    /// Gets or sets whether to automatically connect on initialization.
    /// </summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// Gets or sets the heartbeat interval in simulation ticks.
    /// </summary>
    public uint HeartbeatIntervalTicks { get; set; } = 30; // ~1 second at 30 TPS

    /// <summary>
    /// Gets or sets the reconnection delay in milliseconds.
    /// </summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the connection timeout in milliseconds.
    /// </summary>
    public int ConnectionTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Gets or sets the maximum reconnection attempts before giving up.
    /// Set to 0 for infinite retries.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether to log verbose message traffic.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;
}
