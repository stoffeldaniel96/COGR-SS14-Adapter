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
using Content.Server.Construction.Components;
using Content.Server.DeviceLinking.Components;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
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


public sealed partial class COGRBoundedPerceptionSystem
{
    private static List<ObservedFeature> CreateDoorFeatures(DoorComponent door)
    {
        var state = door.State switch
        {
            DoorState.Open => "open",
            DoorState.Opening or DoorState.Closing or DoorState.Denying or DoorState.Emagging => "transitioning",
            _ => "closed",
        };

        var apparentAffordance = door.State switch
        {
            DoorState.Open => ObservedFeature.Affordance("traverse_apparent", 0.95),
            DoorState.Welded => ObservedFeature.Affordance("blocked_apparent", 0.95),
            _ => ObservedFeature.Affordance("open_apparent", 0.7),
        };

        return new List<ObservedFeature>
        {
            ObservedFeature.Shape("portal_like", 0.95),
            ObservedFeature.State(state, 0.95),
            apparentAffordance,
        };
    }

    private List<ObservedFeature> CreateActorFeatures(EntityUid entity, bool isHumanoid)
    {
        var state = _mobState.IsDead(entity)
            ? "unresponsive"
            : _mobState.IsIncapacitated(entity)
                ? "incapacitated"
                : "responsive";

        var features = new List<ObservedFeature>
        {
            ObservedFeature.Shape(isHumanoid ? "humanoid" : "animate_form", 0.95),
            ObservedFeature.Appearance("animate", 0.95),
            ObservedFeature.State(state, 0.9),
        };

        // Public identity evidence follows the same ID/PDA path used by Station identity mechanics.
        // Do not fall back to MetaData.EntityName: a character name is not automatically actor-relative public evidence.
        if (_idCards.TryFindIdCard(entity, out var idCard))
        {
            var publicName = idCard.Comp.FullName;
            if (!string.IsNullOrWhiteSpace(publicName))
            {
                features.Add(new ObservedFeature
                {
                    Category = "identity",
                    FeatureType = "public_name",
                    Value = publicName.Trim(),
                    Confidence = 0.95,
                });
            }
        }

        return features;
    }

    private static List<ObservedFeature> CreateControlFeatures(SignalSwitchComponent control)
    {
        var isMomentary = string.Equals(control.OnPort, control.OffPort, StringComparison.Ordinal);
        var features = new List<ObservedFeature>
        {
            ObservedFeature.Shape("control_like", 0.9),
            ObservedFeature.Affordance(
                isMomentary ? "press_apparent" : "toggle_apparent",
                0.85),
        };

        if (!isMomentary)
            features.Add(ObservedFeature.State(control.State ? "active" : "inactive", 0.85));

        return features;
    }

    private static List<ObservedFeature> CreateContainerFeatures(EntityStorageComponent storage)
    {
        return new List<ObservedFeature>
        {
            ObservedFeature.Shape("container_like", 0.9),
            ObservedFeature.State(storage.Open ? "open" : "closed", 0.9),
            ObservedFeature.Affordance(
                storage.Open ? "close_apparent" : "open_apparent",
                0.75),
        };
    }

    private List<ObservedFeature> CreateMachineFeatures(EntityUid entity)
    {
        return new List<ObservedFeature>
        {
            ObservedFeature.Shape("machine_like", 0.9),
            ObservedFeature.State(Transform(entity).Anchored ? "anchored" : "mobile", 0.85),
            ObservedFeature.Affordance("interact_apparent", 0.65),
        };
    }

    private static List<ObservedFeature> CreateBarrierFeatures(bool isWindow)
    {
        var features = new List<ObservedFeature>
        {
            ObservedFeature.Shape(isWindow ? "window_like" : "wall_like", 0.95),
            ObservedFeature.State("solid", 0.9),
        };

        features.Add(ObservedFeature.Appearance(isWindow ? "transparent" : "opaque", 0.9));
        return features;
    }

    private List<ObservedFeature> CreateItemFeatures(NativeCandidate candidate)
    {
        var features = new List<ObservedFeature>
        {
            ObservedFeature.Shape(candidate.IsTool ? "tool_like" : "item_like", 0.9),
            ObservedFeature.Size("handheld", 0.9),
        };

        // A visible contained item may be externally carried in a hand, but it is not directly
        // available in the same way as a loose world item. The current hold relation supplies
        // that control context without inventing an ownership judgment.
        if (!_containers.IsEntityOrParentInContainer(candidate.Entity) &&
            !Transform(candidate.Entity).Anchored)
        {
            features.Add(ObservedFeature.Affordance("pickup_apparent", 0.85));
        }

        // Expose only a coarse apparent interaction affordance. Station's edible prototype,
        // food/drink classification, chemistry, and nutrition remain private implementation state.
        // COGR must learn whether an ingestible object is food, drink, nourishing, harmful, etc.
        // from corpus priors, episodic outcomes, and subsequent body evidence.
        if (HasComp<EdibleComponent>(candidate.Entity))
            features.Add(ObservedFeature.Affordance("ingest_apparent", 0.85));

        // Retain the raw appearance cue for provenance/debugging, but do not let it classify the
        // whole object as liquid. The relational projection creates a visible-contents subreferent.
        if (HasVisibleLiquidContents(candidate.Entity))
            features.Add(ObservedFeature.Appearance("liquid_content_visible", 0.9));

        return features;
    }

    private bool HasVisibleLiquidContents(EntityUid entity)
    {
        return TryComp<SolutionContainerVisualsComponent>(entity, out var solutionVisuals)
            && (solutionVisuals.FillBaseName is not null || solutionVisuals.Metamorphic)
            && _appearanceSystem.TryGetData(
                entity,
                SolutionContainerVisuals.FillFraction,
                out float fillFraction)
            && fillFraction > 0f;
    }

    private List<ObservedFeature> CreateStructureFeatures(EntityUid entity)
    {
        return new List<ObservedFeature>
        {
            ObservedFeature.Shape("structure_like", 0.8),
            ObservedFeature.State(Transform(entity).Anchored ? "anchored" : "mobile", 0.85),
        };
    }

    private List<ObservedFeature> CreateGenericObjectFeatures(EntityUid entity)
    {
        return new List<ObservedFeature>
        {
            ObservedFeature.Shape("object_like", 0.7),
            ObservedFeature.State(Transform(entity).Anchored ? "anchored" : "mobile", 0.8),
        };
    }
}
