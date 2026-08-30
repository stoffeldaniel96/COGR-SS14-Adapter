using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.COGR.Components;

/// <summary>
/// Marks an SS14 entity as actively controlled by the COGR cognitive runtime.
/// This component indicates that the entity is being controlled by an external
/// AI system rather than a player session or standard NPC logic.
/// </summary>
/// <remarks>
/// F1 Scope:
/// - Marks entities as COGR-controlled for examination and UI purposes
/// - Tracks whether the COGR runtime connection is active
/// - Replaces standard "SSD" or "no soul" messages with COGR status
///
/// This is distinct from COGRAgentComponent (server-only) which handles
/// the anchor/spawn marker. COGRControlledComponent is for the actual
/// visible humanoid entity that COGR controls.
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class COGRControlledComponent : Component
{
    /// <summary>
    /// The stable COGR AgentId assigned to this controlled entity.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables]
    public Guid AgentId { get; set; }

    /// <summary>
    /// The body identifier for F02 action authority tracking.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables]
    public Guid BodyId { get; set; }

    /// <summary>
    /// Whether this agent is currently active and connected to the COGR runtime.
    /// When true, the entity should show "Controlled by COGR" on examination.
    /// When false (runtime disconnected), may show "COGR disconnected" or similar.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The last simulation tick when an action was executed by this agent.
    /// Used for diagnostics and timeout detection.
    /// </summary>
    [DataField, ViewVariables]
    public uint LastActionTick { get; set; }

    /// <summary>
    /// Display name for diagnostic purposes.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables]
    public string? DisplayName { get; set; }
}

/// <summary>
/// Mind state values specific to COGR-controlled entities.
/// Extends the standard MindState concept for COGR integration.
/// </summary>
[Serializable, NetSerializable]
public enum COGRControlState : byte
{
    /// <summary>
    /// Entity is not COGR-controlled.
    /// </summary>
    None = 0,

    /// <summary>
    /// Entity is actively controlled by COGR runtime.
    /// </summary>
    Active = 1,

    /// <summary>
    /// COGR runtime is disconnected; entity is idle.
    /// </summary>
    Disconnected = 2,

    /// <summary>
    /// Entity is paused or suspended by COGR.
    /// </summary>
    Paused = 3,
}
