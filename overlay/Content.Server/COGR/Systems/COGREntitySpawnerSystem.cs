using Content.Server.COGR.Components;
using Content.Shared.Body;
using Content.Shared.CCVar;
using Content.Shared.COGR.Components;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Content.Shared.Tag;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Handles spawning visible COGR-controlled humanoid entities at anchor positions.
/// </summary>
/// <remarks>
/// F1 Scope:
/// - Detects COGRAgentAnchor entities on map initialization
/// - Spawns a visible humanoid (MobHuman or similar) at the anchor position
/// - Attaches COGRControlledComponent to the spawned humanoid
/// - Registers the humanoid (not the anchor) with the COGR entity mapper
/// - Optionally removes or hides the anchor after spawning
///
/// The spawned humanoid uses all normal SS14 embodiment systems:
/// - Networking, sprite, physics, collision
/// - Body, hands, inventory
/// - Damage and interaction rules
/// </remarks>
public sealed partial class COGREntitySpawnerSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private TagSystem _tagSystem = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedVisualBodySystem _visualBody = default!;
    [Dependency] private HumanoidProfileSystem _humanoidProfile = default!;
    [Dependency] private MetaDataSystem _metaData = default!;

    private COGRAdapterSystem? _adapter;
    private ISawmill _sawmill = default!;

    /// <summary>
    /// The entity prototype to spawn for COGR-controlled humanoids.
    /// This should be a normal humanoid prototype with full embodiment systems.
    /// </summary>
    private const string COGRHumanoidPrototype = "MobHuman";

    /// <summary>
    /// The species to use for spawned COGR humanoids.
    /// </summary>
    private const string DefaultSpecies = "Human";

    /// <summary>
    /// Tag that marks anchor entities for COGR agent spawning.
    /// </summary>
    private const string AgentAnchorTag = "COGRAgentAnchor";

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.spawner");

        // Get the adapter system reference
        _adapter = EntityManager.System<COGRAdapterSystem>();

        // Subscribe to anchor entity lifecycle events
        SubscribeLocalEvent<TagComponent, ComponentStartup>(OnTagComponentStartup);

        _sawmill.Info("COGR Entity Spawner System initialized");
    }

    private void OnTagComponentStartup(EntityUid uid, TagComponent component, ComponentStartup args)
    {
        if (!_configuration.GetCVar(CCVars.COGRLegacyAnchorSpawningEnabled))
            return;

        // Check if this entity has the COGRAgentAnchor tag
        if (!_tagSystem.HasTag(uid, AgentAnchorTag))
            return;

        // Don't process the same anchor twice
        if (HasComp<COGRAnchorProcessedComponent>(uid))
        {
            _sawmill.Debug("Anchor {0} already processed, skipping", uid);
            return;
        }

        // This anchor should trigger humanoid spawning
        SpawnHumanoidAtAnchor(uid);
    }

    /// <summary>
    /// Spawns a visible humanoid entity at the anchor position and registers it with COGR.
    /// </summary>
    private void SpawnHumanoidAtAnchor(EntityUid anchorUid)
    {
        if (_adapter == null || !_adapter.IsEnabled)
        {
            _sawmill.Warning("Cannot spawn COGR humanoid: adapter not available or disabled");
            return;
        }

        // Get anchor position
        var anchorTransform = Transform(anchorUid);
        var coordinates = anchorTransform.Coordinates;

        _sawmill.Info("Spawning COGR humanoid at anchor {0} position {1}", anchorUid, coordinates);

        // Spawn the humanoid entity
        var humanoidUid = Spawn(COGRHumanoidPrototype, coordinates);

        if (!humanoidUid.Valid)
        {
            _sawmill.Error("Failed to spawn COGR humanoid at anchor {0}", anchorUid);
            return;
        }

        // Randomize appearance for variety
        SetupHumanoidAppearance(humanoidUid);

        // Add COGR controlled component to the humanoid
        var controlledComp = EnsureComp<COGRControlledComponent>(humanoidUid);

        // Register the humanoid (not the anchor) with the adapter
        var agentId = _adapter.RegisterAgent(humanoidUid);
        if (agentId == null)
        {
            _sawmill.Error("Failed to register spawned humanoid {0} with COGR", humanoidUid);
            Del(humanoidUid);
            return;
        }

        controlledComp.AgentId = agentId.Value;
        controlledComp.IsActive = true;
        controlledComp.DisplayName = Name(humanoidUid);

        _sawmill.Info("Spawned COGR humanoid: Entity {0} ({1}) -> Agent {2}",
            humanoidUid, controlledComp.DisplayName, agentId);

        // Mark the anchor as processed (optionally hide or delete it)
        // For now, we keep the anchor but could remove it:
        // Del(anchorUid);

        // Add a component to the anchor to track that it has been processed
        var processedComp = EnsureComp<COGRAnchorProcessedComponent>(anchorUid);
        processedComp.SpawnedHumanoid = humanoidUid;
    }

    /// <summary>
    /// Sets up randomized appearance for the spawned humanoid.
    /// </summary>
    private void SetupHumanoidAppearance(EntityUid uid)
    {
        if (!TryComp<HumanoidProfileComponent>(uid, out var profileComp))
            return;

        // Generate random appearance using the humanoid system
        // This gives the COGR character a unique look
        var profile = HumanoidCharacterProfile.RandomWithSpecies(profileComp.Species);

        // Apply visual body and profile using Entity<> tuple syntax
        if (TryComp<VisualBodyComponent>(uid, out var visualBody))
            _visualBody.ApplyProfileTo((uid, visualBody), profile);

        _humanoidProfile.ApplyProfileTo((uid, profileComp), profile);

        // Set a name for the COGR character
        var name = GenerateCOGRName();
        _metaData.SetEntityName(uid, name);

        _sawmill.Debug("Set COGR humanoid {0} appearance with name: {1}", uid, name);
    }

    /// <summary>
    /// Generates a name for a COGR-controlled character.
    /// </summary>
    private string GenerateCOGRName()
    {
        // Simple name generation - can be expanded later
        var prefixes = new[] { "COGR", "Unit", "Agent", "Bot" };
        var prefix = _random.Pick(prefixes);
        var number = _random.Next(100, 999);
        return $"{prefix}-{number}";
    }
}

/// <summary>
/// Marker component to indicate that a COGRAgentAnchor has already been processed
/// and a humanoid has been spawned from it.
/// </summary>
[RegisterComponent]
public sealed partial class COGRAnchorProcessedComponent : Component
{
    /// <summary>
    /// The EntityUid of the humanoid that was spawned from this anchor.
    /// </summary>
    [ViewVariables]
    public EntityUid SpawnedHumanoid { get; set; }
}
