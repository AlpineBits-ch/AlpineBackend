using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;
using Guild.Contracts.Bus.Request;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests.Services;

[TestFixture]
public class GuildProfileMirrorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static GuildProfileMirror Mirror(TestDiscoveryContext ctx, FakeMessageBus bus, DateTimeOffset? now = null) =>
        new(ctx, bus, new TestClock(now ?? Now), NullLogger<GuildProfileMirror>.Instance);

    private static GuildProfileDto Dto(string guildId, string name) => new()
    {
        GuildId = guildId,
        Name = name,
        MemberCount = 10,
        ActiveMemberCount = 4,
        Features = "VoiceChannels",
    };

    [Test]
    public async Task A_missing_profile_is_fetched()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = new FakeMessageBus();
        bus.RespondWith<GetGuildProfilesRequest, GetGuildProfilesResponse>(_ =>
            new GetGuildProfilesResponse { Profiles = [Dto("gild_1", "The Isle")] });

        var result = await Mirror(ctx, bus).EnsureFreshAsync(["gild_1"], CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result["gild_1"].Name, Is.EqualTo("The Isle"));
            Assert.That(result["gild_1"].ProjectedAt, Is.EqualTo(Now));
            Assert.That(ctx.GuildProfiles.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_fresh_profile_is_not_refetched()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GuildProfiles.Add(new GuildProfile
        {
            Id = GuildProfile.GenerateId(),
            GuildId = "gild_2",
            Name = "Still Fresh",
            ProjectedAt = Now,
        });
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();

        var result = await Mirror(ctx, bus, Now + TimeSpan.FromHours(1))
            .EnsureFreshAsync(["gild_2"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result["gild_2"].Name, Is.EqualTo("Still Fresh"));
            Assert.That(bus.LastInvoked, Is.Null);
        });
    }

    [Test]
    public async Task A_stale_profile_is_refetched_and_overwritten()
    {
        await using var ctx = TestDiscoveryContext.New();
        var staleAt = Now - GuildProfileMirror.Ttl - TimeSpan.FromMinutes(1);
        ctx.GuildProfiles.Add(new GuildProfile
        {
            Id = GuildProfile.GenerateId(),
            GuildId = "gild_3",
            Name = "Old Name",
            ProjectedAt = staleAt,
        });
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        bus.RespondWith<GetGuildProfilesRequest, GetGuildProfilesResponse>(_ =>
            new GetGuildProfilesResponse { Profiles = [Dto("gild_3", "New Name")] });

        var result = await Mirror(ctx, bus).EnsureFreshAsync(["gild_3"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result["gild_3"].Name, Is.EqualTo("New Name"));
            Assert.That(result["gild_3"].ProjectedAt, Is.EqualTo(Now));
        });
    }

    [Test]
    public async Task A_guild_the_request_could_not_answer_keeps_its_stale_copy()
    {
        await using var ctx = TestDiscoveryContext.New();
        var staleAt = Now - GuildProfileMirror.Ttl - TimeSpan.FromMinutes(1);
        ctx.GuildProfiles.Add(new GuildProfile
        {
            Id = GuildProfile.GenerateId(),
            GuildId = "gild_4",
            Name = "Stale Name",
            MemberCount = 7,
            ProjectedAt = staleAt,
        });
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        // Guild being briefly unreachable: the request itself fails rather than answering short.
        bus.RespondWith<GetGuildProfilesRequest, GetGuildProfilesResponse>((GetGuildProfilesRequest _) =>
            throw new InvalidOperationException("guild service unreachable"));

        var result = await Mirror(ctx, bus).EnsureFreshAsync(["gild_4"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ContainsKey("gild_4"), Is.True, "must degrade to the stale card, never a blank one");
            Assert.That(result["gild_4"].Name, Is.EqualTo("Stale Name"));
            Assert.That(result["gild_4"].ProjectedAt, Is.EqualTo(staleAt), "an unanswered id is not stamped fresh");
        });
    }
}
