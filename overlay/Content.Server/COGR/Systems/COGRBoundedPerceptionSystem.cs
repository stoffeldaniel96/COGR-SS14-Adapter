using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.Access.Systems;
using Content.Server.Construction.Components;
using Content.Server.DeviceLinking.Components;
using Content.Server.Hands.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Storage.Components;
using Content.Shared.Tag;
using Content.Shared.Tools.Components;
using Robust.Shared.Containers;
using Robust.Shared.Log;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Identifies the specific limit that caused perception projection to terminate early.
/// </summary>
public enum BudgetExhaustionReason
{
    /// <summary>No budget was exhausted; projection completed normally.</summary>
    None,

    /// <summary>The server clamped one or more requested budget parameters.</summary>
    ServerLimitClamped,

    /// <summary>The candidate evaluation limit was reached before all candidates could be evaluated.</summary>
    CandidateLimitReached,

    /// <summary>The observation output limit was reached.</summary>
    ObservationLimitReached,

    /// <summary>The processing time budget was exhausted.</summary>
    TimeBudgetExhausted,
}

/// <summary>
/// Tracks privileged candidate counts by coarse semantic category for diagnostics only.
/// These values are never copied into cognitive omission metadata.
/// </summary>
public readonly record struct CategoryCounts(
    int Doors,
    int Items,
    int Tools,
    int Actors,
    int Controls,
    int Containers,
    int Machines,
    int Barriers,
    int Structures,
    int GenericObjects)
{
    public int Total => Doors + Items + Tools + Actors + Controls + Containers +
        Machines + Barriers + Structures + GenericObjects;

    public CategoryCounts Increment(string category) => category switch
    {
        "door" => this with { Doors = Doors + 1 },
        "handheld_item" => this with { Items = Items + 1 },
        "handheld_tool" => this with { Tools = Tools + 1 },
        "actor" => this with { Actors = Actors + 1 },
        "control" => this with { Controls = Controls + 1 },
        "container" => this with { Containers = Containers + 1 },
        "machine" => this with { Machines = Machines + 1 },
        "barrier" => this with { Barriers = Barriers + 1 },
        "structure" => this with { Structures = Structures + 1 },
        _ => this with { GenericObjects = GenericObjects + 1 },
    };

    public override string ToString() =>
        $"doors={Doors}, items={Items}, tools={Tools}, actors={Actors}, " +
        $"controls={Controls}, containers={Containers}, machines={Machines}, " +
        $"barriers={Barriers}, structures={Structures}, generic={GenericObjects}";
}

/// <summary>
/// Diagnostic summary of a bounded perception projection operation.
/// </summary>
public sealed class PerceptionDiagnostics
{
    public int CandidatesDiscovered { get; init; }
    public int CandidatesEvaluated { get; init; }
    public int ObservationsEmitted { get; init; }
    public double ElapsedProjectionMs { get; init; }
    public BudgetExhaustionReason ExhaustionReason { get; init; }
    public CategoryCounts DiscoveredByCategory { get; init; }
    public CategoryCounts EvaluatedByCategory { get; init; }
    public CategoryCounts EmittedByCategory { get; init; }

    /// <summary>
    /// Returns a bounded diagnostic summary string suitable for operator logging.
    /// Does not expose entity identifiers and is never sent as cognitive evidence.
    /// </summary>
    public string ToSummary()
    {
        var sb = new StringBuilder();
        sb.Append($"discovered={CandidatesDiscovered} ({DiscoveredByCategory}), ");
        sb.Append($"evaluated={CandidatesEvaluated} ({EvaluatedByCategory}), ");
        sb.Append($"emitted={ObservationsEmitted} ({EmittedByCategory}), ");
        sb.Append($"elapsed={ElapsedProjectionMs:F2}ms, ");
        sb.Append($"exhaustion={ExhaustionReason}");
        return sb.ToString();
    }
}

/// <summary>
/// Projects a bounded, observer-relative subset of native SS14 visual state into
/// environment-neutral semantic observations.
/// </summary>
public sealed partial class COGRBoundedPerceptionSystem : EntitySystem
{
    private const int DefaultCandidateBudget = 64;
    private const int DefaultObservationBudget = 16;
    private const int DefaultProcessingBudgetMs = 20;
    private const double DefaultVisualRange = 6.0;
    private const int MaximumCandidateBudget = 256;
    private const int MaximumObservationBudget = 64;
    private const int MaximumProcessingBudgetMs = 50;
    private const double MaximumVisualRange = 12.0;
    private const float MovingVelocitySquaredThreshold = 0.01f;

    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private IdCardSystem _idCards = default!;
    [Dependency] private HandsSystem _hands = default!;

    private readonly Dictionary<ReferenceCacheKey, EnvironmentRef> _referenceCache = new();
    private readonly Dictionary<AgentId, HashSet<EnvironmentRef>> _agentReferenceCache = new();
    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _sawmill = _logManager.GetSawmill("cogr.perception");
    }
}
