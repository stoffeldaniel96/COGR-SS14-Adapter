using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Shared.Atmos.Components;
using Robust.Shared.Map.Components;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Rebuilds round-start atmosphere for the dedicated COGR test map after external map edits.
/// This is intentionally scoped to the COGR game-map prototype and does not alter normal
/// station-map atmosphere behavior.
/// </summary>
public sealed partial class COGRTestMapAtmosSystem : EntitySystem
{
    [Dependency] private ILogManager _logManager = default!;

    private const string COGRMapId = "COGR";

    private readonly HashSet<EntityUid> _pendingGrids = new();
    private AtmosphereSystem _atmosphere = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _atmosphere = EntityManager.System<AtmosphereSystem>();
        _sawmill = _logManager.GetSawmill("cogr.atmos");

        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
    }

    private void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        if (ev.GameMap.ID != COGRMapId)
            return;

        foreach (var grid in ev.Grids)
            _pendingGrids.Add(grid);
    }

    private void OnGameRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound || _pendingGrids.Count == 0)
            return;

        foreach (var grid in _pendingGrids)
        {
            if (!TryComp<GridAtmosphereComponent>(grid, out var gridAtmosphere) ||
                !TryComp<MapGridComponent>(grid, out var mapGrid))
            {
                _sawmill.Warning(
                    "COGR test-map atmosphere rebuild skipped for {0}: grid or atmosphere component is missing",
                    grid);
                continue;
            }

            _atmosphere.RebuildGridAtmosphere((grid, gridAtmosphere, mapGrid));
            _sawmill.Info("Rebuilt round-start atmosphere for COGR test-map grid {0}", grid);
        }

        _pendingGrids.Clear();
    }
}
