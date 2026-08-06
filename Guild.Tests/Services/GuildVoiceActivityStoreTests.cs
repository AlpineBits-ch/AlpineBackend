using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// Covers <see cref="GuildVoiceActivityStore"/> - the per-guild index behind "which of my servers
/// has anyone in voice".
/// </summary>
[TestFixture]
public class GuildVoiceActivityStoreTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string OtherChannelId = "channel-2";

    private FakeDistributedCache _cache = null!;
    private GuildVoiceActivityStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _store = new GuildVoiceActivityStore(new FakeDistributedLockService(), _cache);
    }

    private async Task<ChannelVoiceActivity?> ChannelAsync(string channelId = ChannelId)
    {
        var activity = await _store.LoadAsync(GuildId);
        return activity is not null && activity.Channels.TryGetValue(channelId, out var channel) ? channel : null;
    }

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Load_BeforeAnythingHappened_IsNull()
    {
        Assert.That(await _store.LoadAsync(GuildId), Is.Null);
    }

    [Test]
    public async Task AddParticipant_RecordsThem()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");

        Assert.That((await ChannelAsync())!.UserIds, Is.EqualTo(new[] { "user-1" }));
    }

    [Test]
    public async Task AddParticipant_Twice_CountsOnce()
    {
        // A rejoin, a retried request, a reconnect that re-runs Join: all of these hit this path
        // twice for one person. A counter would drift; a set cannot.
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");

        Assert.That((await ChannelAsync())!.UserIds, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RemoveParticipant_ThatWasNeverThere_IsHarmless()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");
        await _store.RemoveParticipantAsync(GuildId, ChannelId, "user-2");

        Assert.That((await ChannelAsync())!.UserIds, Is.EqualTo(new[] { "user-1" }));
    }

    [Test]
    public async Task RemovingTheLastParticipant_DropsTheChannel()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");
        await _store.RemoveParticipantAsync(GuildId, ChannelId, "user-1");

        var activity = await _store.LoadAsync(GuildId);
        Assert.That(activity!.Channels, Is.Empty,
            "'has any voice activity' is Channels.Count > 0, so an emptied channel must not linger at zero");
    }

    [Test]
    public async Task RemoveParticipant_AlsoClearsTheirStreamingFlag()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-2");
        await _store.SetStreamingAsync(GuildId, ChannelId, "user-1", true);

        await _store.RemoveParticipantAsync(GuildId, ChannelId, "user-1");

        Assert.That((await ChannelAsync())!.StreamerIds, Is.Empty,
            "someone who left the channel is not still live in it");
    }

    [Test]
    public async Task SetStreaming_MarksAndUnmarks()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");

        await _store.SetStreamingAsync(GuildId, ChannelId, "user-1", true);
        Assert.That((await ChannelAsync())!.StreamerIds, Is.EqualTo(new[] { "user-1" }));

        await _store.SetStreamingAsync(GuildId, ChannelId, "user-1", false);
        Assert.That((await ChannelAsync())!.StreamerIds, Is.Empty);
    }

    [Test]
    public async Task SetStreaming_ForAnUnknownChannel_IsHarmless()
    {
        await _store.SetStreamingAsync(GuildId, "channel-missing", "user-1", true);

        var activity = await _store.LoadAsync(GuildId);
        Assert.That(activity!.Channels, Is.Empty,
            "a stream in a channel the index has no roster for must not conjure one");
    }

    [Test]
    public async Task MoveParticipant_LeavesThemInExactlyOneChannel()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");

        await _store.MoveParticipantAsync(GuildId, ChannelId, OtherChannelId, "user-1");

        var activity = await _store.LoadAsync(GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(activity!.Channels.ContainsKey(ChannelId), Is.False);
            Assert.That(activity.Channels[OtherChannelId].UserIds, Is.EqualTo(new[] { "user-1" }));
        });
    }

    [Test]
    public async Task MoveParticipant_DoesNotCarryTheirStreamingFlagAcross()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");
        await _store.SetStreamingAsync(GuildId, ChannelId, "user-1", true);

        await _store.MoveParticipantAsync(GuildId, ChannelId, OtherChannelId, "user-1");

        var activity = await _store.LoadAsync(GuildId);
        Assert.That(activity!.Channels[OtherChannelId].StreamerIds, Is.Empty,
            "a share does not survive being dragged into another channel - the tracks were closed");
    }

    [Test]
    public async Task Replace_RebuildsFromTheRosterItIsGiven()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "ghost");

        // What the heartbeat sweep does: recompute from the channel blobs, which are the truth,
        // so a write this index missed is corrected rather than believed forever.
        await _store.ReplaceAsync(GuildId, new Dictionary<string, ChannelVoiceActivity>
        {
            [ChannelId] = new() { UserIds = ["user-1"], StreamerIds = ["user-1"] },
        });

        var channel = (await ChannelAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(channel.UserIds, Is.EqualTo(new[] { "user-1" }));
            Assert.That(channel.StreamerIds, Is.EqualTo(new[] { "user-1" }));
        });
    }

    [Test]
    public async Task Replace_WithAnEmptyRoster_ClearsTheChannel()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "ghost");

        await _store.ReplaceAsync(GuildId, new Dictionary<string, ChannelVoiceActivity>
        {
            [ChannelId] = new(),
        });

        var activity = await _store.LoadAsync(GuildId);
        Assert.That(activity!.Channels, Is.Empty,
            "a participant the sweep found gone must disappear from the index too, or the server "
            + "list keeps a dot lit for a channel nobody is in");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // What the heartbeat sweep feeds into ReplaceAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Rebuild_GroupsChannelsUnderTheirGuild()
    {
        var rebuilt = new Dictionary<string, Dictionary<string, ChannelVoiceActivity>>();

        VoiceHeartbeatCleanupService.Record(rebuilt, State(GuildId, ChannelId, ("user-1", false)));
        VoiceHeartbeatCleanupService.Record(rebuilt, State(GuildId, OtherChannelId, ("user-2", true)));
        VoiceHeartbeatCleanupService.Record(rebuilt, State("guild-2", "channel-9", ("user-3", false)));

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt[GuildId].Keys, Is.EquivalentTo(new[] { ChannelId, OtherChannelId }));
            Assert.That(rebuilt[GuildId][OtherChannelId].StreamerIds, Is.EqualTo(new[] { "user-2" }));
            Assert.That(rebuilt["guild-2"].Keys, Is.EqualTo(new[] { "channel-9" }));
        });
    }

    [Test]
    public void Rebuild_RecordsAnEmptiedChannelRatherThanSkippingIt()
    {
        // The self-healing property depends on this.
        var rebuilt = new Dictionary<string, Dictionary<string, ChannelVoiceActivity>>();

        VoiceHeartbeatCleanupService.Record(rebuilt, State(GuildId, ChannelId));

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt[GuildId].ContainsKey(ChannelId), Is.True);
            Assert.That(rebuilt[GuildId][ChannelId].UserIds, Is.Empty);
        });
    }

    [Test]
    public void Rebuild_IgnoresAChannelBlobWithNoGuild()
    {
        var rebuilt = new Dictionary<string, Dictionary<string, ChannelVoiceActivity>>();

        VoiceHeartbeatCleanupService.Record(rebuilt, State(string.Empty, ChannelId, ("user-1", false)));

        Assert.That(rebuilt, Is.Empty, "there is no guild index to file it under");
    }

    private static ChannelVoiceState State(string guildId, string channelId, params (string UserId, bool Streaming)[] participants) =>
        new()
        {
            ChannelId = channelId,
            GuildId = guildId,
            Participants = participants
                .Select(p => new VoiceState
                {
                    UserId = p.UserId, ChannelId = channelId, GuildId = guildId, IsStreaming = p.Streaming,
                })
                .ToList(),
        };

    [Test]
    public async Task Guilds_AreIndexedSeparately()
    {
        await _store.AddParticipantAsync(GuildId, ChannelId, "user-1");
        await _store.AddParticipantAsync("guild-2", "channel-9", "user-1");

        var first = await _store.LoadAsync(GuildId);
        var second = await _store.LoadAsync("guild-2");

        Assert.Multiple(() =>
        {
            Assert.That(first!.Channels.Keys, Is.EqualTo(new[] { ChannelId }));
            Assert.That(second!.Channels.Keys, Is.EqualTo(new[] { "channel-9" }));
        });
    }
}
