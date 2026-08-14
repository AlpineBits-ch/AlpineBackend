using Echo.Realtime;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Guild.Application.Bus.Events.Realtime;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// Speech reported for a guild voice channel, which until now had nowhere to arrive.
/// </summary>
[TestFixture]
public class GuildVoiceSpeakingTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";

    /// <summary>Three, so a five-person room is a large one and the fixture stays readable. The
    /// production default is ten.</summary>
    private static readonly VoiceSubscriptionOptions Options = new()
    {
        ActiveSpeakerThreshold = 3,
        ActiveSpeakerCount = 2,
        MaxActiveSpeakers = 3,
    };

    private static VoiceRoomKey Room => VoiceRoomKey.Channel(ChannelId);

    private FakeDistributedCache _cache = null!;
    private FakeDistributedLockService _locks = null!;
    private FakeHubContext _hub = null!;
    private VoiceRoomService _voice = null!;
    private GuildVoiceStateHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _locks = new FakeDistributedLockService();
        _hub = new FakeHubContext();

        var subscriptions = new VoiceSubscriptions(
            new VoiceAttentionStore(_locks, _cache, Options), Options, TimeProvider.System,
            NullLogger<VoiceSubscriptions>.Instance);

        _voice = new VoiceRoomService(
            VoiceTestHarness.StoreFor(_cache, _locks), new VoiceAnnouncer(_hub), subscriptions);
        _handler = new GuildVoiceStateHandler();
    }

    private async Task PopulateAsync(int size)
    {
        for (var i = 0; i < size; i++)
        {
            var userId = $"u{i:D2}";
            await _voice.JoinAsync(Room, userId, $"device-{i}", GuildId);
            await _voice.RecordPublishAsync(Room, userId, $"cf-{userId}");
        }
    }

    private FakeHubClients HubClients => (FakeHubClients)_hub.Clients;

    [Test]
    public async Task Speech_in_a_large_channel_ranks_the_room()
    {
        await PopulateAsync(5);

        await _handler.Handle(new GuildVoiceSpeakingCommand("u01", ChannelId, true), _voice);

        var plan = await _voice.GetSubscriptionsAsync(Room);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.ActiveSpeaker));
            Assert.That(plan.ActiveSpeakers, Is.EqualTo(new[] { "u01" }));
            Assert.That(plan.For("u04").Tracks.Select(t => t.UserId), Is.EqualTo(new[] { "u01" }),
                "four subscriptions instead of sixteen, which is the whole point of the change");
        });
    }

    [Test]
    public async Task Speech_in_a_small_channel_leaves_it_all_to_all()
    {
        await PopulateAsync(3);

        await _handler.Handle(new GuildVoiceSpeakingCommand("u01", ChannelId, true), _voice);

        var plan = await _voice.GetSubscriptionsAsync(Room);
        Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.All),
            "below the threshold the quadratic is not worth the renegotiation cost of managing it");
    }

    [Test]
    public async Task Speech_is_relayed_to_the_rest_of_the_channel_and_not_back_to_its_author()
    {
        await PopulateAsync(3);
        HubClients.SentToUsers.Clear();

        await _handler.Handle(new GuildVoiceSpeakingCommand("u01", ChannelId, true), _voice);

        Assert.That(HubClients.RecipientsOf("guild.voice.SpeakingChanged"),
            Is.EquivalentTo(new[] { "u00", "u02" }));
    }

    /// <summary>
    /// The command names its author, but the hub overwrites that with the authenticated connection
    /// before dispatching, so what reaches here is server-authoritative.
    /// </summary>
    [Test]
    public async Task Speech_from_somebody_who_is_not_in_the_channel_is_ignored()
    {
        await PopulateAsync(5);
        HubClients.SentToUsers.Clear();

        await _handler.Handle(new GuildVoiceSpeakingCommand("stranger", ChannelId, true), _voice);

        var plan = await _voice.GetSubscriptionsAsync(Room);
        Assert.Multiple(() =>
        {
            Assert.That(HubClients.RecipientsOf("guild.voice.SpeakingChanged"), Is.Empty);
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.All),
                "a room whose only reported speech came from outside it has nothing to rank");
        });
    }
}
