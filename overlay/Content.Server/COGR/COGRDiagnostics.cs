namespace Content.Server.COGR;

/// <summary>
/// Diagnostic information about the COGR adapter state.
/// </summary>
public sealed class COGRDiagnostics
{
    /// <summary>
    /// Gets or sets whether the adapter is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the adapter is connected to COGR runtime.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Gets or sets the COGR runtime endpoint.
    /// </summary>
    public string RuntimeEndpoint { get; set; } = "";

    /// <summary>
    /// Gets or sets the number of registered agents.
    /// </summary>
    public int RegisteredAgentCount { get; set; }

    /// <summary>
    /// Gets or sets the current simulation tick.
    /// </summary>
    public uint CurrentTick { get; set; }

    /// <summary>
    /// Gets or sets the last heartbeat tick.
    /// </summary>
    public uint LastHeartbeatTick { get; set; }

    /// <summary>
    /// Gets or sets the connection state.
    /// </summary>
    public string ConnectionState { get; set; } = "Unknown";

    /// <summary>
    /// Gets or sets the protocol version.
    /// </summary>
    public string ProtocolVersion { get; set; } = "F0.5";

    /// <summary>
    /// Gets or sets the world ID.
    /// </summary>
    public Guid? WorldId { get; set; }

    /// <summary>
    /// Gets or sets the connection ID.
    /// </summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>
    /// Gets or sets the last error message, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the messages sent count.
    /// </summary>
    public uint MessagesSent { get; set; }

    /// <summary>
    /// Gets or sets the messages received count.
    /// </summary>
    public uint MessagesReceived { get; set; }

    /// <summary>
    /// Returns a formatted diagnostic string.
    /// </summary>
    public override string ToString()
    {
        return $"""
            COGR Adapter Diagnostics:
              Enabled:        {IsEnabled}
              Connected:      {IsConnected}
              State:          {ConnectionState}
              Endpoint:       {RuntimeEndpoint}
              Protocol:       {ProtocolVersion}
              World ID:       {WorldId?.ToString() ?? "N/A"}
              Connection ID:  {ConnectionId?.ToString() ?? "N/A"}
              Agents:         {RegisteredAgentCount}
              Current Tick:   {CurrentTick}
              Last Heartbeat: {LastHeartbeatTick}
              Messages TX:    {MessagesSent}
              Messages RX:    {MessagesReceived}
              Last Error:     {LastError ?? "None"}
            """;
    }
}
