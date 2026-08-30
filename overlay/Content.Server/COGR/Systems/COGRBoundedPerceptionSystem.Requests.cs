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
    private const int MaxPendingPerceptionRequests = 256;
    private const int MaxPendingPerceptionRequestsPerAgent = 8;
    private const int MaxPerceptionRequestsPerUpdate = 1;

    private readonly Dictionary<AgentId, Queue<PerceptionRequestMessage>> _pendingPerceptionRequests = new();
    private readonly Queue<AgentId> _pendingPerceptionAgents = new();
    private int _pendingPerceptionRequestCount;

    /// <summary>
    /// Admits canonical bounded requests into a fair main-thread projection queue.
    /// Projection itself is intentionally deferred so adapter message polling cannot synchronously
    /// execute an unbounded burst of expensive spatial queries in a single server update.
    /// </summary>
    public void HandleRequest(PerceptionRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Request);

        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true })
        {
            _sawmill.Warning(
                "Cannot answer bounded perception request {0}: runtime connection is unavailable",
                message.Request.RequestId);
            return;
        }

        var currentTick = new SimTick((ulong)_timing.CurTick.Value);
        if (_pendingPerceptionRequestCount >= MaxPendingPerceptionRequests)
        {
            EnqueueFailure(
                message,
                currentTick,
                OmissionCategory.AttentionInterrupted,
                "The Station bounded-perception admission queue is full.");
            return;
        }

        var agentId = message.Request.AgentId;
        if (!_pendingPerceptionRequests.TryGetValue(agentId, out var queue))
        {
            queue = new Queue<PerceptionRequestMessage>();
            _pendingPerceptionRequests.Add(agentId, queue);
            _pendingPerceptionAgents.Enqueue(agentId);
        }
        else if (queue.Count >= MaxPendingPerceptionRequestsPerAgent)
        {
            EnqueueFailure(
                message,
                currentTick,
                OmissionCategory.AttentionInterrupted,
                "This Coggent already has the maximum number of bounded-perception requests awaiting projection.");
            return;
        }

        queue.Enqueue(message);
        _pendingPerceptionRequestCount++;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var processed = 0;
        while (processed < MaxPerceptionRequestsPerUpdate &&
               _pendingPerceptionAgents.Count > 0)
        {
            var agentId = _pendingPerceptionAgents.Dequeue();
            if (!_pendingPerceptionRequests.TryGetValue(agentId, out var queue) || queue.Count == 0)
            {
                _pendingPerceptionRequests.Remove(agentId);
                continue;
            }

            var message = queue.Dequeue();
            _pendingPerceptionRequestCount--;

            if (queue.Count == 0)
                _pendingPerceptionRequests.Remove(agentId);
            else
                _pendingPerceptionAgents.Enqueue(agentId);

            ProcessRequest(message);
            processed++;
        }
    }

    private void ProcessRequest(PerceptionRequestMessage message)
    {
        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true })
        {
            _sawmill.Warning(
                "Cannot answer queued bounded perception request {0}: runtime connection is unavailable",
                message.Request.RequestId);
            return;
        }

        var currentTick = new SimTick((ulong)_timing.CurTick.Value);
        if (!TryValidateRequest(
                message,
                currentTick,
                out var observer,
                out var focusedEntity,
                out var failureCategory,
                out var failureDetail))
        {
            EnqueueFailure(message, currentTick, failureCategory, failureDetail);
            return;
        }

        var result = ProjectReplica(message.Request, observer, currentTick, focusedEntity);
        connection.EnqueueEnvironmentMessage(new PerceptionResultMessage
        {
            WorldId = message.WorldId,
            ConnectionId = message.ConnectionId,
            Tick = currentTick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            CorrelationId = message.CorrelationId,
            Result = result,
        });

        if (result.Observations.Count == 0 && focusedEntity.HasValue)
        {
            _sawmill.Debug(
                "Projected bounded perception request {0} with 0 observations ({1}); focused_miss={2}",
                message.Request.RequestId,
                result.CompletionState,
                DescribeFocusedProjectionMiss(observer, focusedEntity.Value, message.Request));
            return;
        }

        _sawmill.Debug(
            "Projected bounded perception request {0} with {1} observations ({2})",
            message.Request.RequestId,
            result.Observations.Count,
            result.CompletionState);
    }

    private string DescribeFocusedProjectionMiss(
        EntityUid observer,
        EntityUid focusedEntity,
        PerceptionRequest request)
    {
        if (Deleted(focusedEntity))
            return "source_deleted";
        if (focusedEntity == observer)
            return "source_is_observer";
        if (HasComp<MapGridComponent>(focusedEntity))
            return "source_is_map_grid";
        if (_containers.IsEntityOrParentInContainer(focusedEntity))
            return "source_is_contained";

        var requestedDistance = request.Budget.MaxDistance ?? COGRSpatialPolicy.DefaultVisualHorizon;
        var maxDistance = Math.Clamp(requestedDistance, 0, MaximumVisualRange);
        if (!Transform(observer).Coordinates.TryDistance(
                EntityManager,
                Transform(focusedEntity).Coordinates,
                out var distance))
        {
            return "source_distance_unavailable";
        }

        if (distance > maxDistance)
            return $"source_out_of_range(distance={distance:0.###},max={maxDistance:0.###})";

        if (!TryCreateCandidate(focusedEntity, distance, request.SearchConceptHints, out var candidate))
        {
            var prototype = MetaData(focusedEntity).EntityPrototype?.ID.ToString() ?? "<none>";
            var isPhysical = HasComp<PhysicsComponent>(focusedEntity);
            var anchored = Transform(focusedEntity).Anchored;
            return $"source_not_semantically_projectable(prototype={prototype},physics={isPhysical},anchored={anchored})";
        }

        if (!TryGetVisualFootprintQuality(observer, candidate, maxDistance, out var visibilityQuality))
            return $"source_no_longer_visible(quality={visibilityQuality:0.###})";

        return $"candidate_available_but_not_emitted(category={candidate.Category})";
    }

    private bool TryValidateRequest(
        PerceptionRequestMessage message,
        SimTick currentTick,
        out EntityUid observer,
        out EntityUid? focusedEntity,
        out OmissionCategory failureCategory,
        out string failureDetail)
    {
        var request = message.Request;
        observer = default;
        focusedEntity = null;
        failureCategory = OmissionCategory.AttentionInterrupted;
        failureDetail = "No current SS14 body authority matches this perception request.";

        if (!_authority.BoundWorld.HasValue ||
            !_authority.BoundConnection.HasValue ||
            _authority.BoundWorld.Value != message.WorldId ||
            _authority.BoundConnection.Value != message.ConnectionId ||
            request.ConnectionId != message.ConnectionId)
        {
            return false;
        }

        var resolved = _authority.ResolveBoundBody(
            request.AgentId,
            request.BodyId,
            request.ConnectionId,
            request.BodyGeneration);
        if (!resolved.HasValue)
            return false;

        observer = resolved.Value;
        if (_mobState.IsIncapacitated(observer))
        {
            failureCategory = OmissionCategory.SensoryCapabilityLacking;
            failureDetail = "The authoritative SS14 body is incapacitated and cannot perform visual inspection.";
            return false;
        }

        if (request.Modality != PerceptionModality.Visual)
        {
            failureCategory = OmissionCategory.AdapterCoverageLimited;
            failureDetail = "Station bounded perception currently supports only visual requests.";
            return false;
        }

        switch (request.Anchor.Type)
        {
            case AttentionAnchorType.Self:
                if (request.Anchor.BodyId != request.BodyId)
                {
                    failureCategory = OmissionCategory.AttentionInterrupted;
                    failureDetail = "The requested self-attention anchor does not match current body authority.";
                    return false;
                }
                break;

            case AttentionAnchorType.EntityReference:
                var optionalEnvironmentReference = request.Anchor.EnvironmentRef;
                if (!optionalEnvironmentReference.HasValue || !optionalEnvironmentReference.Value.IsAssigned)
                {
                    failureCategory = OmissionCategory.AttentionInterrupted;
                    failureDetail = "The requested entity-attention anchor is missing an environment reference.";
                    return false;
                }

                var registry = _adapter.ReferenceRegistry;
                if (registry == null)
                {
                    failureCategory = OmissionCategory.AdapterCoverageLimited;
                    failureDetail = "Station opaque environment-reference storage is unavailable.";
                    return false;
                }

                focusedEntity = registry.TryResolve(
                    optionalEnvironmentReference.Value,
                    new EnvironmentReferenceResolutionContext
                    {
                        ConnectionId = request.ConnectionId,
                        CurrentTick = currentTick,
                        BodyId = request.BodyId,
                        BodyGeneration = request.BodyGeneration,
                    });
                if (!focusedEntity.HasValue)
                {
                    failureCategory = OmissionCategory.AttentionInterrupted;
                    failureDetail = "The requested entity-attention reference is stale or not valid for current body authority.";
                    return false;
                }
                break;

            default:
                failureCategory = OmissionCategory.AdapterCoverageLimited;
                failureDetail = "Station bounded visual perception currently supports self and entity-reference anchors.";
                return false;
        }

        if (request.Budget.MaxDistance.HasValue &&
            (!double.IsFinite(request.Budget.MaxDistance.Value) || request.Budget.MaxDistance.Value < 0))
        {
            failureCategory = OmissionCategory.AdapterCoverageLimited;
            failureDetail = "The requested perception distance is invalid.";
            return false;
        }

        return true;
    }

    private void EnqueueFailure(
        PerceptionRequestMessage message,
        SimTick currentTick,
        OmissionCategory category,
        string detail)
    {
        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true })
            return;

        connection.EnqueueEnvironmentMessage(new PerceptionResultMessage
        {
            WorldId = message.WorldId,
            ConnectionId = message.ConnectionId,
            Tick = currentTick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            CorrelationId = message.CorrelationId,
            Result = CreateFailureResult(message.Request, currentTick, category, detail),
        });

        _sawmill.Info(
            "Answered bounded perception request {0} fail-closed: {1}",
            message.Request.RequestId,
            detail);
    }

    private static PerceptionResult CreateFailureResult(
        PerceptionRequest request,
        SimTick currentTick,
        OmissionCategory category,
        string detail)
    {
        return new PerceptionResult
        {
            EvidenceId = Guid.CreateVersion7(),
            ObservedAtTick = currentTick,
            AgentId = request.AgentId,
            BodyId = request.BodyId,
            CausalTraceId = request.CausalTraceId,
            SourceQueryId = request.RequestId,
            ObservationType = ObservationType.Sensory,
            UrgencyHint = 0,
            RequestId = request.RequestId,
            CompletionState = PerceptionCompletionState.Failed,
            Observations = Array.Empty<Observation>(),
            Omissions = new[]
            {
                new OmissionReason
                {
                    Category = category,
                    Description = detail,
                },
            },
        };
    }

}
