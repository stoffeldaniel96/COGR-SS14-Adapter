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


public sealed partial class COGRBoundedPerceptionSystem
{
    private static int GetSemanticPriority(string category) => category switch
    {
        "actor" => 100,
        "door" or "control" => 90,
        "handheld_tool" or "handheld_item" => 80,
        "container" or "machine" => 70,
        "barrier" => 40,
        "structure" => 30,
        _ => 20,
    };

    private static int HintScore(
        string category,
        IReadOnlyList<string>? hints)
    {
        if (hints == null)
            return 0;

        foreach (var rawHint in hints)
        {
            var hint = rawHint.Trim().Replace('-', '_').Replace(' ', '_');
            var score = category switch
            {
                "door" when hint.Contains("door", StringComparison.OrdinalIgnoreCase) => 2,
                "actor" when hint.Contains("actor", StringComparison.OrdinalIgnoreCase) ||
                                  hint.Contains("person", StringComparison.OrdinalIgnoreCase) ||
                                  hint.Contains("humanoid", StringComparison.OrdinalIgnoreCase) => 2,
                "control" when hint.Contains("button", StringComparison.OrdinalIgnoreCase) ||
                                    hint.Contains("switch", StringComparison.OrdinalIgnoreCase) ||
                                    hint.Contains("control", StringComparison.OrdinalIgnoreCase) => 2,
                "container" when hint.Contains("container", StringComparison.OrdinalIgnoreCase) ||
                                      hint.Contains("storage", StringComparison.OrdinalIgnoreCase) => 2,
                "machine" when hint.Contains("machine", StringComparison.OrdinalIgnoreCase) => 2,
                "barrier" when hint.Contains("wall", StringComparison.OrdinalIgnoreCase) ||
                                    hint.Contains("window", StringComparison.OrdinalIgnoreCase) ||
                                    hint.Contains("barrier", StringComparison.OrdinalIgnoreCase) => 2,
                "handheld_tool" when hint.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                                          hint.Contains("handheld", StringComparison.OrdinalIgnoreCase) => 2,
                "handheld_item" when hint.Contains("item", StringComparison.OrdinalIgnoreCase) => 1,
                "structure" when hint.Contains("structure", StringComparison.OrdinalIgnoreCase) => 1,
                "generic_object" when hint.Contains("object", StringComparison.OrdinalIgnoreCase) => 1,
                _ => 0,
            };

            if (score > 0)
                return score;
        }

        return 0;
    }

    private sealed record NativeCandidate(
        EntityUid Entity,
        double Distance,
        DoorComponent? Door,
        SignalSwitchComponent? Control,
        EntityStorageComponent? Storage,
        bool IsTool,
        bool IsHumanoid,
        bool IsWindow,
        string Category,
        int HintScore,
        int SemanticPriority);

    private readonly record struct ReferenceCacheKey(
        EntityUid Entity,
        ConnectionId ConnectionId,
        BodyId BodyId,
        uint BodyGeneration);
}
