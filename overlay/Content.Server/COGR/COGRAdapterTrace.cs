namespace Content.Server.COGR;

/// <summary>
/// Runtime switch for high-volume COGR adapter origin tracing.
/// Disabled by default on every server start and enabled explicitly by an operator when needed.
/// </summary>
public static class COGRAdapterTrace
{
    public static bool Enabled { get; internal set; }
}
