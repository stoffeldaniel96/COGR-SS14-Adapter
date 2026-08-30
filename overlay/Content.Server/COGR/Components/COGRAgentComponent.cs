using Robust.Shared.GameObjects;

namespace Content.Server.COGR.Components;

/// <summary>
/// Marks an SS14 entity as a COGR-controlled agent.
/// The adapter maintains a stable AgentId mapping for this entity.
/// </summary>
[RegisterComponent]
public sealed partial class COGRAgentComponent : Component
{
    /// <summary>
    /// The stable COGR AgentId assigned to this entity.
    /// </summary>
    [ViewVariables]
    public Guid AgentId { get; set; }

    /// <summary>
    /// Whether this agent is currently active (spawned and ready).
    /// </summary>
    [ViewVariables]
    public bool IsActive { get; set; }

    /// <summary>
    /// Display name for diagnostic purposes.
    /// </summary>
    [ViewVariables]
    public string? DisplayName { get; set; }
}
