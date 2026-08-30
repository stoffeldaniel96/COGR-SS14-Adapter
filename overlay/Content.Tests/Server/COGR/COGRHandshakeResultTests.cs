using System;
using Content.Server.COGR.Transport;
using NUnit.Framework;

namespace Content.Tests.Server.COGR;

[TestFixture]
[TestOf(typeof(COGRHandshakeResult))]
public sealed class COGRHandshakeResultTests
{
    [Test]
    public void Success_SetsSucceededTrue()
    {
        var worldId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        var result = COGRHandshakeResult.Success(worldId, connectionId, 100, 50);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.IsTransient, Is.False);
        Assert.That(result.WorldId, Is.EqualTo(worldId));
        Assert.That(result.ConnectionId, Is.EqualTo(connectionId));
        Assert.That(result.CurrentTick, Is.EqualTo(100));
        Assert.That(result.LatestRuntimeSequence, Is.EqualTo(50));
        Assert.That(result.Error, Is.Null);
        Assert.That(result.StatusCode, Is.Null);
    }

    [Test]
    public void Failed_SetsSucceededFalse_NotTransient()
    {
        var result = COGRHandshakeResult.Failed("Protocol mismatch");

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.IsTransient, Is.False);
        Assert.That(result.Error, Is.EqualTo("Protocol mismatch"));
        Assert.That(result.WorldId, Is.Null);
        Assert.That(result.ConnectionId, Is.Null);
    }

    [Test]
    public void Transient_SetsSucceededFalse_IsTransientTrue()
    {
        var result = COGRHandshakeResult.Transient("Connection refused", "Unavailable");

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.IsTransient, Is.True);
        Assert.That(result.Error, Is.EqualTo("Connection refused"));
        Assert.That(result.StatusCode, Is.EqualTo("Unavailable"));
        Assert.That(result.WorldId, Is.Null);
        Assert.That(result.ConnectionId, Is.Null);
    }

    [Test]
    public void Transient_DistinguishesFromFailed()
    {
        var transient = COGRHandshakeResult.Transient("Runtime not ready", "Unavailable");
        var failed = COGRHandshakeResult.Failed("Protocol incompatible");

        Assert.That(transient.Succeeded, Is.False);
        Assert.That(failed.Succeeded, Is.False);
        Assert.That(transient.IsTransient, Is.True);
        Assert.That(failed.IsTransient, Is.False);
    }
}
