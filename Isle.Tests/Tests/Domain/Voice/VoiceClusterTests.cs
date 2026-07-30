using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Tests.Tests.Domain.Voice;

[TestFixture]
public class VoiceClusterTests
{
    private VoiceCluster _cluster = null!;

    [SetUp]
    public void SetUp() =>
        _cluster = new VoiceCluster(new VoiceGridConfig { CellSize = 3000f, MovementEpsilon = 25f, YawEpsilon = 4f });

    // ── MovePlayer: first join ───────────────────────────────────────────────

    [Test]
    public void MovePlayer_FirstJoin_EmitsOnlyAMovedChange()
    {
        var changes = _cluster.MovePlayer("p1", 0, 0, 0);

        Assert.That(changes, Has.Count.EqualTo(1));
        Assert.That(changes[0], Is.InstanceOf<VoiceClusterChange.Moved>());
    }

    [Test]
    public void MovePlayer_TwoPlayersInSameCell_BothBecomeAudibleToEachOther()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        var changes = _cluster.MovePlayer("p2", 100, 100, 0);

        var joined = changes.OfType<VoiceClusterChange.PeerJoined>().ToList();
        Assert.That(joined, Has.Count.EqualTo(1));
        Assert.That(joined[0].PlayerId, Is.EqualTo("p2"));
        Assert.That(joined[0].OtherId, Is.EqualTo("p1"));
    }

    [Test]
    public void GetAudiblePeers_TwoPlayersInSameCell_AreMutuallyAudible()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 100, 100, 0);

        Assert.That(_cluster.GetAudiblePeers("p1"), Is.EquivalentTo(new[] { "p2" }));
        Assert.That(_cluster.GetAudiblePeers("p2"), Is.EquivalentTo(new[] { "p1" }));
    }

    [Test]
    public void GetAudiblePeers_PlayersFarApart_AreNotAudible()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 1_000_000, 1_000_000, 0); // many cells away

        Assert.That(_cluster.GetAudiblePeers("p1"), Is.Empty);
        Assert.That(_cluster.GetAudiblePeers("p2"), Is.Empty);
    }

    [Test]
    public void GetAudiblePeers_AdjacentCell_StillAudible()
    {
        // p1 at cell (0,0), p2 just across the boundary in cell (1,0) - still within the 3x3 block.
        _cluster.MovePlayer("p1", 100, 100, 0);
        _cluster.MovePlayer("p2", 3100, 100, 0);

        Assert.That(_cluster.GetAudiblePeers("p1"), Is.EquivalentTo(new[] { "p2" }));
    }

    [Test]
    public void GetAudiblePeers_UnknownPlayer_ReturnsEmpty()
    {
        Assert.That(_cluster.GetAudiblePeers("ghost"), Is.Empty);
    }

    // ── MovePlayer: moving between cells ─────────────────────────────────────

    [Test]
    public void MovePlayer_MovingOutOfRange_EmitsPeerLeft()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 100, 100, 0);

        var changes = _cluster.MovePlayer("p2", 1_000_000, 1_000_000, 0);

        var left = changes.OfType<VoiceClusterChange.PeerLeft>().ToList();
        Assert.That(left, Has.Count.EqualTo(1));
        Assert.That(left[0].PlayerId, Is.EqualTo("p2"));
        Assert.That(left[0].OtherId, Is.EqualTo("p1"));
    }

    [Test]
    public void MovePlayer_StayingInSameCell_DoesNotChangeAudibleSet()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 100, 100, 0);

        // Small move within the same cell - audible set (peer joined/left) should not change.
        var changes = _cluster.MovePlayer("p2", 200, 200, 0);

        Assert.That(changes.OfType<VoiceClusterChange.PeerJoined>(), Is.Empty);
        Assert.That(changes.OfType<VoiceClusterChange.PeerLeft>(), Is.Empty);
    }

    // ── RemovePlayer ──────────────────────────────────────────────────────────

    [Test]
    public void RemovePlayer_NotifiesRemainingAudiblePeers()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 100, 100, 0);

        var changes = _cluster.RemovePlayer("p1");

        Assert.That(changes, Has.Count.EqualTo(1));
        var left = (VoiceClusterChange.PeerLeft)changes[0];
        Assert.That(left.PlayerId, Is.EqualTo("p1"));
        Assert.That(left.OtherId, Is.EqualTo("p2"));
    }

    [Test]
    public void RemovePlayer_RemovedPlayerNoLongerInGetPlayers()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.RemovePlayer("p1");

        Assert.That(_cluster.GetPlayers(), Does.Not.Contain("p1"));
    }

    [Test]
    public void RemovePlayer_UnknownPlayer_ReturnsEmptyAndDoesNotThrow()
    {
        var changes = _cluster.RemovePlayer("ghost");

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void RemovePlayer_AfterAllPlayersLeaveCell_CellIsCleanedUp()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.RemovePlayer("p1");

        // Re-adding a player to the same cell must behave like a fresh cell (no leftover state).
        var changes = _cluster.MovePlayer("p2", 0, 0, 0);
        Assert.That(changes.OfType<VoiceClusterChange.PeerJoined>(), Is.Empty);
    }

    // ── GetPlayers ────────────────────────────────────────────────────────────

    [Test]
    public void GetPlayers_ReturnsAllTrackedPlayers()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 1_000_000, 1_000_000, 0);

        Assert.That(_cluster.GetPlayers(), Is.EquivalentTo(new[] { "p1", "p2" }));
    }

    // ── GetAudiblePairs ───────────────────────────────────────────────────────

    [Test]
    public void GetAudiblePairs_ThreeMutuallyAudiblePlayers_ReturnsEachPairOnce()
    {
        _cluster.MovePlayer("a", 0, 0, 0);
        _cluster.MovePlayer("b", 100, 100, 0);
        _cluster.MovePlayer("c", 200, 200, 0);

        var pairs = _cluster.GetAudiblePairs();

        Assert.That(pairs, Has.Count.EqualTo(3));
        // Each pair reported exactly once, owned by the lexicographically smaller id.
        Assert.That(pairs, Has.All.Matches<(string A, string B)>(p => string.CompareOrdinal(p.A, p.B) < 0));
    }

    [Test]
    public void GetAudiblePairs_NoOverlap_ReturnsEmpty()
    {
        _cluster.MovePlayer("a", 0, 0, 0);
        _cluster.MovePlayer("b", 1_000_000, 1_000_000, 0);

        Assert.That(_cluster.GetAudiblePairs(), Is.Empty);
    }

    // ── TryGetPosition ────────────────────────────────────────────────────────

    [Test]
    public void TryGetPosition_KnownPlayer_ReturnsLastPosition()
    {
        _cluster.MovePlayer("p1", 10, 20, 30, 45f);

        var found = _cluster.TryGetPosition("p1", out var pos);

        Assert.That(found, Is.True);
        Assert.That(pos.X, Is.EqualTo(10));
        Assert.That(pos.Y, Is.EqualTo(20));
        Assert.That(pos.Z, Is.EqualTo(30));
        Assert.That(pos.Yaw, Is.EqualTo(45f));
    }

    [Test]
    public void TryGetPosition_UnknownPlayer_ReturnsFalse()
    {
        var found = _cluster.TryGetPosition("ghost", out _);

        Assert.That(found, Is.False);
    }

    // ── Velocity derivation ──────────────────────────────────────────────────

    [Test]
    public void MovePlayer_FirstSample_VelocityStaysZero()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);

        _cluster.TryGetPosition("p1", out var pos);
        Assert.That(pos.Vx, Is.EqualTo(0));
        Assert.That(pos.Vy, Is.EqualTo(0));
        Assert.That(pos.Vz, Is.EqualTo(0));
    }

    // ── EmitPositionIfMoved epsilon gating ───────────────────────────────────

    [Test]
    public void MovePlayer_TinyMovementBelowEpsilon_DoesNotEmitSecondMovedChange()
    {
        _cluster.MovePlayer("p1", 0, 0, 0); // first sample always emits Moved

        // Move by less than MovementEpsilon (25 units) and without turning.
        var changes = _cluster.MovePlayer("p1", 1, 1, 0);

        Assert.That(changes.OfType<VoiceClusterChange.Moved>(), Is.Empty);
    }

    [Test]
    public void MovePlayer_MovementAboveEpsilon_EmitsMovedChange()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);

        var changes = _cluster.MovePlayer("p1", 100, 0, 0);

        Assert.That(changes.OfType<VoiceClusterChange.Moved>().ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public void MovePlayer_TurnAboveYawEpsilonWithoutMoving_EmitsMovedChange()
    {
        _cluster.MovePlayer("p1", 0, 0, 0, yaw: 0f);

        // Same position, turned more than YawEpsilon (4 degrees).
        var changes = _cluster.MovePlayer("p1", 0, 0, 0, yaw: 90f);

        Assert.That(changes.OfType<VoiceClusterChange.Moved>().ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public void MovePlayer_TinyTurnBelowYawEpsilon_DoesNotEmitMovedChange()
    {
        _cluster.MovePlayer("p1", 0, 0, 0, yaw: 0f);

        var changes = _cluster.MovePlayer("p1", 0, 0, 0, yaw: 1f);

        Assert.That(changes.OfType<VoiceClusterChange.Moved>(), Is.Empty);
    }
}
