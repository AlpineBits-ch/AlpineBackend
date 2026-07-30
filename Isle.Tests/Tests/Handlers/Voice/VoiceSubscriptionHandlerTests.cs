using Isle.Api.Handlers.Voice;
using Isle.Contracts.Events.Voice;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using NSubstitute;

namespace Isle.Tests.Tests.Handlers.Voice;

[TestFixture]
public class VoiceSubscriptionHandlerTests
{
    private VoiceCluster _cluster = null!;
    private ISfuClient _sfu = null!;

    [SetUp]
    public void SetUp()
    {
        _cluster = new VoiceCluster(new VoiceGridConfig());
        _sfu = Substitute.For<ISfuClient>();
    }

    // --- Handle(PeerBecameAudibleEvent) -------------------------------------------------------------

    [Test]
    public async Task HandleAudible_AlwaysSubscribesMutually()
    {
        var @event = new PeerBecameAudibleEvent("p1", "p2");

        await VoiceSubscriptionHandler.Handle(@event, _cluster, _sfu);

        await _sfu.Received(1).SubscribeMutual("p1", "p2");
    }

    [Test]
    public async Task HandleAudible_NeitherPositionKnown_SendsNoPeerPositions()
    {
        var @event = new PeerBecameAudibleEvent("p1", "p2");

        await VoiceSubscriptionHandler.Handle(@event, _cluster, _sfu);

        await _sfu.DidNotReceive().SendPeerPosition(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task HandleAudible_OnlyOtherPositionKnown_SeedsPlayerWithOthersPosition()
    {
        _cluster.MovePlayer("p2", 10, 20, 30, 45f);
        var @event = new PeerBecameAudibleEvent("p1", "p2");

        await VoiceSubscriptionHandler.Handle(@event, _cluster, _sfu);

        await _sfu.Received(1).SendPeerPosition("p1", "p2", 10, 20, 30, 45f, 0, 0, 0, Arg.Any<long>());
        await _sfu.DidNotReceive().SendPeerPosition("p2", "p1", Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task HandleAudible_OnlyPlayerPositionKnown_SeedsOtherWithPlayersPosition()
    {
        _cluster.MovePlayer("p1", 5, 6, 7, 90f);
        var @event = new PeerBecameAudibleEvent("p1", "p2");

        await VoiceSubscriptionHandler.Handle(@event, _cluster, _sfu);

        await _sfu.Received(1).SendPeerPosition("p2", "p1", 5, 6, 7, 90f, 0, 0, 0, Arg.Any<long>());
        await _sfu.DidNotReceive().SendPeerPosition("p1", "p2", Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task HandleAudible_BothPositionsKnown_SeedsBothSides()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 1, 1, 1);
        var @event = new PeerBecameAudibleEvent("p1", "p2");

        await VoiceSubscriptionHandler.Handle(@event, _cluster, _sfu);

        // Literal values are mixed with Arg.Any of the same type (float), so every float position
        // must be an explicit matcher — NSubstitute cannot disambiguate literals from matchers of the
        // same type within a single call otherwise.
        await _sfu.Received(1).SendPeerPosition("p1", "p2", Arg.Is<float>(1), Arg.Is<float>(1), Arg.Is<float>(1),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
        await _sfu.Received(1).SendPeerPosition("p2", "p1", Arg.Is<float>(0), Arg.Is<float>(0), Arg.Is<float>(0),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    // --- Handle(PeerBecameInaudibleEvent) -----------------------------------------------------------

    [Test]
    public async Task HandleInaudible_UnsubscribesThePair()
    {
        var @event = new PeerBecameInaudibleEvent("p1", "p2");

        await VoiceSubscriptionHandler.Handle(@event, _sfu);

        await _sfu.Received(1).UnsubscribePair("p1", "p2");
    }
}
