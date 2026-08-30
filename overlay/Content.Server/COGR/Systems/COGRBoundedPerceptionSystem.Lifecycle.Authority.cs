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
    public void InvalidateBodyAuthority(BodyAuthorityLease lease, string reason)
    {
        if (!lease.IsValid)
            return;

        var batches = _adapter.ReferenceRegistry?.InvalidateForBody(
            lease.BodyId,
            lease.Generation) ?? Array.Empty<ReferenceInvalidationBatch>();

        RemoveCachedReferences(key =>
            key.ConnectionId == lease.ConnectionId &&
            key.BodyId == lease.BodyId &&
            key.BodyGeneration == lease.Generation);
        RemoveCachedSubreferents(key =>
            key.ConnectionId == lease.ConnectionId &&
            key.BodyId == lease.BodyId &&
            key.BodyGeneration == lease.Generation);
        EmitInvalidations(batches, reason);
    }

    /// <summary>
    /// Invalidates every local reference owned by a connection. A typed message is emitted
    /// only while that exact stream remains writable; stream loss itself is otherwise the
    /// authoritative invalidation signal.
    /// </summary>
    public void InvalidateConnection(ConnectionId connectionId, string reason)
    {
        var batches = _adapter.ReferenceRegistry?.InvalidateForConnection(connectionId)
            ?? Array.Empty<ReferenceInvalidationBatch>();

        RemoveCachedReferences(key => key.ConnectionId == connectionId);
        RemoveCachedSubreferents(key => key.ConnectionId == connectionId);
        EmitInvalidations(batches, reason);
    }
}
