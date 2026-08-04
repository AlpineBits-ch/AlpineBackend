using Guild.Contracts.Bus.Request;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>
/// The bus-backed shared-guild lookup behind <c>FriendRequestPolicy.ServerMembers</c> (T2-15) and
/// <c>mutualServers</c> (T2-17). The properties that matter are Guild's response conventions -
/// a pair with nothing in common is omitted rather than returned empty - and the fail-closed
/// behaviour when Guild cannot be reached at all.
/// </summary>
[TestFixture]
public class BusSharedGuildResolverTests
{
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp() => _bus = new FakeMessageBus();

    // ── normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task SharedGuildsAsync_ReturnsTheGuildIdsForTheRequestedPair()
    {
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus,
            ("user-b", ["guld_1", "guld_2"]),
            ("user-c", ["guld_9"]));

        var result = await sut.SharedGuildsAsync("user-a", "user-b");

        Assert.That(result.Select(g => g.GuildId), Is.EqualTo(new[] { "guld_1", "guld_2" }));
    }

    [Test]
    public async Task SharedGuildsAsync_LeavesNameNull()
    {
        // Guild does not project names on this contract, and filling them in would cost a second
        // round trip on every profile read to decorate a field the client resolves by id anyway.
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-b", ["guld_1"]));

        var result = await sut.SharedGuildsAsync("user-a", "user-b");

        Assert.That(result.Single().Name, Is.Null);
    }

    [Test]
    public async Task SharedGuildsAsync_SendsTheViewerAsUserIdAndTheSubjectAsTheOnlyOther()
    {
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-b", ["guld_1"]));

        await sut.SharedGuildsAsync("user-a", "user-b");

        var request = (GetSharedGuildsRequest)_bus.LastInvoked!;
        Assert.Multiple(() =>
        {
            Assert.That(request.UserId, Is.EqualTo("user-a"));
            Assert.That(request.OtherUserIds, Is.EqualTo(new[] { "user-b" }));
        });
    }

    [Test]
    public async Task ShareAnyGuildAsync_TrueWhenThePairIsPresent()
    {
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-b", ["guld_1"]));

        Assert.That(await sut.ShareAnyGuildAsync("user-a", "user-b"), Is.True);
    }

    // ── edge: Guild's response conventions ───────────────────────────────────

    [Test]
    public async Task PairOmittedFromTheResponse_MeansNoSharedGuilds()
    {
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-somebody-else", ["guld_1"]));

        var guilds = await sut.SharedGuildsAsync("user-a", "user-b");
        var shareAny = await sut.ShareAnyGuildAsync("user-a", "user-b");

        Assert.Multiple(() =>
        {
            Assert.That(guilds, Is.Empty);
            Assert.That(shareAny, Is.False);
        });
    }

    [Test]
    public async Task EmptyResponse_MeansNoSharedGuilds()
    {
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus);

        Assert.That(await sut.ShareAnyGuildAsync("user-a", "user-b"), Is.False);
    }

    [Test]
    public async Task SelfPair_ShortCircuitsWithoutCallingGuild()
    {
        // Guild drops an id equal to UserId rather than answering it - the intersection of a user
        // with themselves is their whole guild list. Asking would cost a round trip to learn nothing.
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-a", ["guld_1"]));

        var result = await sut.SharedGuildsAsync("user-a", "user-a");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Empty);
            Assert.That(_bus.LastInvoked, Is.Null);
        });
    }

    [TestCase("", "user-b")]
    [TestCase("user-a", "")]
    public async Task BlankIds_ShortCircuit(string userA, string userB)
    {
        var sut = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-b", ["guld_1"]));

        var shareAny = await sut.ShareAnyGuildAsync(userA, userB);

        Assert.Multiple(() =>
        {
            Assert.That(shareAny, Is.False);
            Assert.That(_bus.LastInvoked, Is.Null);
        });
    }

    // ── negative: fail closed ────────────────────────────────────────────────

    [Test]
    public async Task BusFailure_YieldsNoSharedGuildsRatherThanThrowing()
    {
        // No registered answer: FakeMessageBus throws, standing in for Guild being down. The
        // resolver must absorb it - a ServerMembers policy has to refuse, and an exception here
        // would surface as a 500 on an ordinary profile read.
        var sut = PrivacyTestHelpers.SharedGuilds(_bus);

        var guilds = await sut.SharedGuildsAsync("user-a", "user-b");
        var shareAny = await sut.ShareAnyGuildAsync("user-a", "user-b");

        Assert.Multiple(() =>
        {
            Assert.That(guilds, Is.Empty);
            Assert.That(shareAny, Is.False);
        });
    }

    [Test]
    public async Task NoSharedGuildResolver_StillAnswersNothing()
    {
        // Kept as the explicit restrictive stand-in for a deployment with no Guild to ask.
        var sut = new Social.Api.Services.NoSharedGuildResolver();

        var guilds = await sut.SharedGuildsAsync("user-a", "user-b");
        var shareAny = await sut.ShareAnyGuildAsync("user-a", "user-b");

        Assert.Multiple(() =>
        {
            Assert.That(guilds, Is.Empty);
            Assert.That(shareAny, Is.False);
        });
    }
}
