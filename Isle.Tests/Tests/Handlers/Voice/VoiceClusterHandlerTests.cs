using Isle.Api.Handlers.Voice;
using Isle.Contracts.Commands;
using Isle.Contracts.Events.Voice;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Tests.Tests.Handlers.Voice;

[TestFixture]
public class VoiceClusterHandlerTests
{
    private VoiceCluster _cluster = null!;

    [SetUp]
    public void SetUp() => _cluster = new VoiceCluster(new VoiceGridConfig());

    [Test]
    public void Handle_UpdatePlayerPositionCommand_FirstJoin_YieldsPlayerPositionUpdatedEvent()
    {
        var result = VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p1", 0, 0, 0), _cluster).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.InstanceOf<PlayerPositionUpdatedEvent>());
    }

    [Test]
    public void Handle_UpdatePlayerPositionCommand_TwoPlayersInRange_YieldsAudibleEventForTheSecond()
    {
        VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p1", 0, 0, 0), _cluster).ToList();

        var result = VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p2", 100, 100, 0), _cluster).ToList();

        var joined = result.OfType<PeerBecameAudibleEvent>().ToList();
        Assert.That(joined, Has.Count.EqualTo(1));
        Assert.That(joined[0].PlayerId, Is.EqualTo("p2"));
        Assert.That(joined[0].OtherId, Is.EqualTo("p1"));
    }

    [Test]
    public void Handle_RemovePlayerCommand_NotifiesRemainingAudiblePeer()
    {
        VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p1", 0, 0, 0), _cluster).ToList();
        VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p2", 100, 100, 0), _cluster).ToList();

        var result = VoiceClusterHandler.Handle(new RemovePlayerCommand("p1"), _cluster).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        var inaudible = (PeerBecameInaudibleEvent)result[0];
        Assert.That(inaudible.PlayerId, Is.EqualTo("p1"));
        Assert.That(inaudible.OtherId, Is.EqualTo("p2"));
    }

    [Test]
    public void Handle_RemovePlayerCommand_UnknownPlayer_YieldsNothing()
    {
        var result = VoiceClusterHandler.Handle(new RemovePlayerCommand("ghost"), _cluster).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Handle_GetRoommatesCommand_ReturnsAudiblePeers()
    {
        VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p1", 0, 0, 0), _cluster).ToList();
        VoiceClusterHandler.Handle(new UpdatePlayerPositionCommand("p2", 100, 100, 0), _cluster).ToList();

        var response = VoiceClusterHandler.Handle(new GetRoommatesCommand("p1"), _cluster);

        Assert.That(response.PlayerId, Is.EqualTo("p1"));
        Assert.That(response.Roommates, Is.EquivalentTo(new[] { "p2" }));
    }

    [Test]
    public void Handle_GetRoommatesCommand_UnknownPlayer_ReturnsEmptyRoommates()
    {
        var response = VoiceClusterHandler.Handle(new GetRoommatesCommand("ghost"), _cluster);

        Assert.That(response.Roommates, Is.Empty);
    }
}
