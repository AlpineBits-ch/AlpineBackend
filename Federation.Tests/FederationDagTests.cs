using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Services;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Federation.Tests;

[TestFixture]
public class FederationDagTests
{
    private MicroserviceContext _db = null!;
    private FederationDagService _dag = null!;

    [SetUp]
    public void SetUp()
    {
        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"dag-{Guid.NewGuid()}")
            .Options;
        _db = new MicroserviceContext(opts);
        _dag = new FederationDagService(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MessageCreated MakeEvent(
        string eventId,
        string channelId = "scope-test",
        string[]? parents = null,
        long depth = 0)
        => new()
        {
            EventId = eventId,
            MessageId = $"msg-{eventId}",
            ChannelId = channelId,
            Host = "https://origin.example.com",
            ProtocolVersion = "venta/v0.1",
            PreviousEventIds = parents ?? [],
            Depth = depth,
            OriginServerTime = DateTime.UtcNow
        };

    private static string Id() => Guid.NewGuid().ToString();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task InOrder_ThreeEvents_AllAppliedInOrder()
    {
        var aId = Id(); var bId = Id(); var cId = Id();
        var a = MakeEvent(aId, depth: 0);
        var b = MakeEvent(bId, parents: [aId], depth: 1);
        var c = MakeEvent(cId, parents: [bId], depth: 2);

        var rA = await _dag.RecordAndResolveAsync(a);
        var rB = await _dag.RecordAndResolveAsync(b);
        var rC = await _dag.RecordAndResolveAsync(c);

        Assert.That(rA, Has.Count.EqualTo(1));
        Assert.That(rB, Has.Count.EqualTo(1));
        Assert.That(rC, Has.Count.EqualTo(1));
        Assert.That(rA[0].EventId, Is.EqualTo(aId));
        Assert.That(rB[0].EventId, Is.EqualTo(bId));
        Assert.That(rC[0].EventId, Is.EqualTo(cId));
    }

    [Test]
    public async Task OutOfOrder_CArrivesBeforeB_BufferedThenFlushed()
    {
        var aId = Id(); var bId = Id(); var cId = Id();
        var a = MakeEvent(aId, depth: 0);
        var b = MakeEvent(bId, parents: [aId], depth: 1);
        var c = MakeEvent(cId, parents: [bId], depth: 2);

        await _dag.RecordAndResolveAsync(a);

        var rC = await _dag.RecordAndResolveAsync(c);  // C arrives before B — buffered
        Assert.That(rC, Is.Empty, "C must be buffered while B is missing");

        var rB = await _dag.RecordAndResolveAsync(b);  // B arrives, unblocks C
        Assert.That(rB, Has.Count.EqualTo(2));
        Assert.That(rB[0].EventId, Is.EqualTo(bId));
        Assert.That(rB[1].EventId, Is.EqualTo(cId));
    }

    [Test]
    public async Task SplitBrain_TwoConcurrentBranches_BothAccepted()
    {
        var aId = Id(); var bId = Id(); var cId = Id();
        var a = MakeEvent(aId, depth: 0);
        var b = MakeEvent(bId, parents: [aId], depth: 1);
        var c = MakeEvent(cId, parents: [aId], depth: 1);  // concurrent with B

        await _dag.RecordAndResolveAsync(a);

        var rB = await _dag.RecordAndResolveAsync(b);
        var rC = await _dag.RecordAndResolveAsync(c);

        Assert.That(rB, Has.Count.EqualTo(1), "B should apply immediately");
        Assert.That(rC, Has.Count.EqualTo(1), "C should apply independently");

        var tips = await _dag.GetTipsAsync("scope-test");
        Assert.That(tips, Has.Length.EqualTo(2));
        Assert.That(tips, Contains.Item(bId));
        Assert.That(tips, Contains.Item(cId));
    }

    [Test]
    public async Task MergeCommit_MultiParent_FiresAfterBothParentsApplied()
    {
        var aId = Id(); var bId = Id(); var cId = Id(); var dId = Id();
        var a = MakeEvent(aId, depth: 0);
        var b = MakeEvent(bId, parents: [aId], depth: 1);
        var c = MakeEvent(cId, parents: [aId], depth: 1);
        var d = MakeEvent(dId, parents: [bId, cId], depth: 2);  // merge commit

        await _dag.RecordAndResolveAsync(a);
        await _dag.RecordAndResolveAsync(b);

        var rDBeforeC = await _dag.RecordAndResolveAsync(d);
        Assert.That(rDBeforeC, Is.Empty, "D must wait for C");

        var rC = await _dag.RecordAndResolveAsync(c);  // C arrives, unblocks D
        Assert.That(rC, Has.Count.EqualTo(2));
        Assert.That(rC[0].EventId, Is.EqualTo(cId));
        Assert.That(rC[1].EventId, Is.EqualTo(dId));
    }

    [Test]
    public async Task Deduplication_SameEventIdTwice_FiresOnce()
    {
        var aId = Id();
        var a = MakeEvent(aId);

        var r1 = await _dag.RecordAndResolveAsync(a);
        var r2 = await _dag.RecordAndResolveAsync(a);  // duplicate

        Assert.That(r1, Has.Count.EqualTo(1));
        Assert.That(r2, Is.Empty, "Duplicate event must be ignored");
    }

    [Test]
    public async Task Depth_IsComputedCorrectly()
    {
        var scope = $"scope-depth-{Id()}";
        var e1 = new MessageCreated
        {
            EventId = Id(), MessageId = "m1", ChannelId = scope,
            Host = "https://origin.example.com", ProtocolVersion = "venta/v0.1",
            OriginServerTime = DateTime.UtcNow
        };
        var e2 = new MessageCreated
        {
            EventId = Id(), MessageId = "m2", ChannelId = scope,
            Host = "https://origin.example.com", ProtocolVersion = "venta/v0.1",
            OriginServerTime = DateTime.UtcNow
        };
        var e3 = new MessageCreated
        {
            EventId = Id(), MessageId = "m3", ChannelId = scope,
            Host = "https://origin.example.com", ProtocolVersion = "venta/v0.1",
            OriginServerTime = DateTime.UtcNow
        };

        await _dag.StampAndRecordAsync(e1, scope);
        await _dag.StampAndRecordAsync(e2, scope);
        await _dag.StampAndRecordAsync(e3, scope);

        Assert.That(e1.Depth, Is.EqualTo(0));
        Assert.That(e2.Depth, Is.EqualTo(1));
        Assert.That(e3.Depth, Is.EqualTo(2));
        Assert.That(e1.PreviousEventIds, Is.Empty);
        Assert.That(e2.PreviousEventIds, Is.EqualTo(new[] { e1.EventId }));
        Assert.That(e3.PreviousEventIds, Is.EqualTo(new[] { e2.EventId }));
    }

    [Test]
    public async Task Tips_UpdateCorrectly_AfterLinearChain()
    {
        var scope = $"scope-tips-linear-{Id()}";
        var e1 = new MessageCreated { EventId = Id(), MessageId = "m1", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };
        var e2 = new MessageCreated { EventId = Id(), MessageId = "m2", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };
        var e3 = new MessageCreated { EventId = Id(), MessageId = "m3", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };

        await _dag.StampAndRecordAsync(e1, scope);
        await _dag.StampAndRecordAsync(e2, scope);
        await _dag.StampAndRecordAsync(e3, scope);

        var tips = await _dag.GetTipsAsync(scope);
        Assert.That(tips, Has.Length.EqualTo(1));
        Assert.That(tips[0], Is.EqualTo(e3.EventId));
    }

    [Test]
    public async Task Tips_MergeReducesTips()
    {
        var scope = $"scope-tips-merge-{Id()}";
        var eA = new MessageCreated { EventId = Id(), MessageId = "a", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };
        var eB = new MessageCreated { EventId = Id(), MessageId = "b", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };
        var eC = new MessageCreated { EventId = Id(), MessageId = "c", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };
        var eD = new MessageCreated { EventId = Id(), MessageId = "d", ChannelId = scope, Host = "h", ProtocolVersion = "venta/v0.1", OriginServerTime = DateTime.UtcNow };

        await _dag.StampAndRecordAsync(eA, scope);  // root

        // Split: B and C both point to A
        eB.PreviousEventIds = [eA.EventId]; eB.Depth = 1;
        _db.FederatedEvents.Add(new Domain.Aggregates.FederatedEventRecord
        {
            EventId = eB.EventId, Host = eB.Host, ScopeKey = scope, Depth = 1,
            PreviousEventIds = [eA.EventId], PayloadJson = System.Text.Json.JsonSerializer.Serialize(eB),
            Applied = true, ReceivedAt = DateTimeOffset.UtcNow
        });
        eC.PreviousEventIds = [eA.EventId]; eC.Depth = 1;
        _db.FederatedEvents.Add(new Domain.Aggregates.FederatedEventRecord
        {
            EventId = eC.EventId, Host = eC.Host, ScopeKey = scope, Depth = 1,
            PreviousEventIds = [eA.EventId], PayloadJson = System.Text.Json.JsonSerializer.Serialize(eC),
            Applied = true, ReceivedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var tipsBefore = await _dag.GetTipsAsync(scope);
        Assert.That(tipsBefore, Has.Length.EqualTo(2), "B and C are both tips before merge");

        // Merge: D points to both B and C
        await _dag.StampAndRecordAsync(eD, scope);

        var tipsAfter = await _dag.GetTipsAsync(scope);
        Assert.That(tipsAfter, Has.Length.EqualTo(1));
        Assert.That(tipsAfter[0], Is.EqualTo(eD.EventId));
    }
}
