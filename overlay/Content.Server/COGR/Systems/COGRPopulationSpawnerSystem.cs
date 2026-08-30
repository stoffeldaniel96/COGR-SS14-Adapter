using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.COGR.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Preferences;
using Content.Shared.Spawning;
using Robust.Shared.Configuration;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Creates the configured round-start COGR population from ordinary Station spawn points.
/// Explicit COGR anchors remain a separate test/specific-entity spawning mechanism and are not
/// charged against this system's population budget.
/// </summary>
public sealed partial class COGRPopulationSpawnerSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private StationSystem _stationSystem = default!;
    [Dependency] private StationSpawningSystem _stationSpawning = default!;

    private COGRAdapterSystem _adapter = default!;
    private COGRAgentRegistrationSystem _registration = default!;
    private ISawmill _sawmill = default!;

    private const string COGRHumanoidPrototype = "MobHuman";
    private const string DefaultSpecies = "Human";
    private const float OccupiedSpawnRadius = 0.35f;
    private const int LateJoinRetryLimit = 120;
    private static readonly TimeSpan LateJoinRetryInterval = TimeSpan.FromSeconds(5);

    private int _populationRunGeneration;
    private int _requestedPassengers;
    private int _spawnedPassengers;
    private int _lateJoinRetriesRemaining;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.population");
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _registration = EntityManager.System<COGRAgentRegistrationSystem>();

        // RoundStartingEvent fires before the round map is initialized. Population spawning
        // requires initialized Station spawn points and therefore waits for the InRound transition.
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
    }

    public override void Shutdown()
    {
        InvalidatePopulationRun();
        base.Shutdown();
    }

    private void OnGameRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        InvalidatePopulationRun();

        if (ev.New != GameRunLevel.InRound || !_adapter.IsEnabled)
            return;

        var totalBudget = Math.Max(0, _configuration.GetCVar(CCVars.COGRPopulationSpawnBudget));
        var passengerAllotment = Math.Max(0, _configuration.GetCVar(CCVars.COGRPopulationPassengerAllotment));
        _requestedPassengers = Math.Min(totalBudget, passengerAllotment);

        if (_requestedPassengers == 0)
        {
            _sawmill.Debug(
                "Automated COGR population disabled or no Passenger allotment configured (budget={0}, passenger={1})",
                totalBudget,
                passengerAllotment);
            return;
        }

        var candidates = GetPassengerSpawnCandidates(lateJoinOnly: false);
        _spawnedPassengers = SpawnFromCandidates(candidates, _requestedPassengers);

        if (_spawnedPassengers >= _requestedPassengers)
        {
            _sawmill.Info(
                "Spawned configured COGR Passenger population {0}/{1} (global budget {2})",
                _spawnedPassengers,
                passengerAllotment,
                totalBudget);
            return;
        }

        _lateJoinRetriesRemaining = LateJoinRetryLimit;
        var generation = _populationRunGeneration;
        _sawmill.Warning(
            "Initial COGR population pass spawned {0}/{1} Passengers. Vacated LateJoin spawn points will be retried every {2} seconds for up to {3} attempts.",
            _spawnedPassengers,
            _requestedPassengers,
            LateJoinRetryInterval.TotalSeconds,
            LateJoinRetryLimit);
        ScheduleLateJoinRetry(generation);
    }

    private void ScheduleLateJoinRetry(int generation)
    {
        Timer.Spawn(LateJoinRetryInterval, () => RetryLateJoinPopulation(generation));
    }

    private void RetryLateJoinPopulation(int generation)
    {
        if (generation != _populationRunGeneration ||
            _requestedPassengers == 0 ||
            _spawnedPassengers >= _requestedPassengers)
        {
            return;
        }

        if (_lateJoinRetriesRemaining <= 0)
        {
            _sawmill.Warning(
                "COGR population LateJoin retries exhausted at {0}/{1} spawned Passengers",
                _spawnedPassengers,
                _requestedPassengers);
            return;
        }

        _lateJoinRetriesRemaining--;
        var remaining = _requestedPassengers - _spawnedPassengers;
        var candidates = GetPassengerSpawnCandidates(lateJoinOnly: true);
        var spawnedThisAttempt = SpawnFromCandidates(candidates, remaining);
        _spawnedPassengers += spawnedThisAttempt;

        if (spawnedThisAttempt > 0)
        {
            _sawmill.Info(
                "COGR LateJoin retry spawned {0} additional Passengers; population is now {1}/{2}",
                spawnedThisAttempt,
                _spawnedPassengers,
                _requestedPassengers);
        }

        if (_spawnedPassengers >= _requestedPassengers)
        {
            _sawmill.Info(
                "Completed configured COGR Passenger population {0}/{1} after LateJoin retries",
                _spawnedPassengers,
                _requestedPassengers);
            return;
        }

        ScheduleLateJoinRetry(generation);
    }

    private void InvalidatePopulationRun()
    {
        _populationRunGeneration++;
        _requestedPassengers = 0;
        _spawnedPassengers = 0;
        _lateJoinRetriesRemaining = 0;
    }

    private int SpawnFromCandidates(List<EntityUid> candidates, int maximumToSpawn)
    {
        var spawned = 0;
        while (spawned < maximumToSpawn && candidates.Count > 0)
        {
            var index = _random.Next(candidates.Count);
            var spawnPoint = candidates[index];
            candidates.RemoveAt(index);

            if (!TrySpawnPassenger(spawnPoint))
                continue;

            spawned++;
        }

        return spawned;
    }

    private List<EntityUid> GetPassengerSpawnCandidates(bool lateJoinOnly)
    {
        var candidates = new List<EntityUid>();
        var query = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var spawnPoint, out _))
        {
            // Native SpawnPointComponent currently has no individual-player claim/reservation
            // identity. LateJoin markers and exact Passenger job markers are shared pool entries.
            // If Station later exposes reservation metadata, that reservation must veto
            // population eligibility here before the point enters this candidate pool.
            if (spawnPoint.SpawnType == SpawnPointType.LateJoin)
            {
                candidates.Add(uid);
                continue;
            }

            if (!lateJoinOnly &&
                spawnPoint.SpawnType == SpawnPointType.Job &&
                spawnPoint.Job == SharedGameTicker.FallbackOverflowJob)
            {
                candidates.Add(uid);
            }
        }

        return candidates;
    }

    private bool TrySpawnPassenger(EntityUid spawnPointUid)
    {
        if (!TryComp(spawnPointUid, out TransformComponent? spawnTransform))
            return false;

        var coordinates = spawnTransform.Coordinates;

        // Players/mobs are deliberately treated as occupying a population spawn point even
        // when their normal SS14 collision layers permit mobs to pass through one another.
        foreach (var nearby in _lookup.GetEntitiesInRange(coordinates, OccupiedSpawnRadius))
        {
            if (nearby == spawnPointUid)
                continue;

            if (HasComp<MobStateComponent>(nearby))
            {
                _sawmill.Debug("Skipping occupied COGR population spawn point {0}: mob {1} is present", spawnPointUid, nearby);
                return false;
            }
        }

        // Use Station's native hard-collision-aware spawn check for non-mob obstructions.
        // We spawn the ordinary humanoid shell first, then let StationSpawningSystem apply the
        // normal Passenger role/profile/loadout onto that same entity.
        var humanoidUid = EntityManager.SpawnIfUnobstructed(
            COGRHumanoidPrototype,
            coordinates,
            CollisionGroup.MobLayer);

        if (humanoidUid == null)
        {
            _sawmill.Debug("Skipping obstructed COGR population spawn point {0}", spawnPointUid);
            return false;
        }

        var station = _stationSystem.GetOwningStation(spawnPointUid, spawnTransform);
        var profile = HumanoidCharacterProfile.RandomWithSpecies(DefaultSpecies);

        try
        {
            humanoidUid = _stationSpawning.SpawnPlayerMob(
                coordinates,
                SharedGameTicker.FallbackOverflowJob,
                profile,
                station,
                humanoidUid.Value);
        }
        catch (Exception ex)
        {
            _sawmill.Error(
                "Failed to configure Passenger role for COGR population body {0} at spawn point {1}: {2}",
                humanoidUid,
                spawnPointUid,
                ex.Message);
            Del(humanoidUid.Value);
            return false;
        }

        var controlled = EnsureComp<COGRControlledComponent>(humanoidUid.Value);
        controlled.IsActive = true;
        controlled.DisplayName = Name(humanoidUid.Value);
        Dirty(humanoidUid.Value, controlled);

        var agentId = _registration.RegisterEntity(humanoidUid.Value, controlled.DisplayName);
        if (agentId == null)
        {
            _sawmill.Error("Failed to register COGR population body {0}; deleting spawned Passenger", humanoidUid);
            Del(humanoidUid.Value);
            return false;
        }

        _sawmill.Info(
            "Spawned COGR Passenger {0} ({1}) -> Agent {2} from spawn point {3} at {4}",
            humanoidUid,
            controlled.DisplayName,
            agentId,
            spawnPointUid,
            coordinates);

        return true;
    }
}
