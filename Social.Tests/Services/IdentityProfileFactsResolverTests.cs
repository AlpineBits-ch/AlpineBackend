using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Social.Api.Services;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>
/// The Identity-owned half of the profile supplements: birthday and linked external accounts
/// (privacy spec T2-17).
/// </summary>
[TestFixture]
public class IdentityProfileFactsResolverTests
{
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp() => _bus = new FakeMessageBus();

    private BusIdentityProfileFactsResolver Resolver() => PrivacyTestHelpers.IdentityFacts(_bus);

    // ── birthday ─────────────────────────────────────────────────────────────

    [Test]
    public async Task BirthdayAsync_ReturnsTheDateIdentityReported()
    {
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-1", new DateOnly(1990, 3, 4)));

        Assert.That(await Resolver().BirthdayAsync("user-1"), Is.EqualTo(new DateOnly(1990, 3, 4)));
    }

    [Test]
    public async Task BirthdayAsync_SendsTheBatchedRequest()
    {
        // Batched in the contract even though this call passes one id: a future list projection must
        // be one round trip, not N.
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-1", new DateOnly(1990, 3, 4)));

        await Resolver().BirthdayAsync("user-1");

        Assert.That(_bus.LastInvoked, Is.InstanceOf<GetUserBirthdaysRequest>());
        Assert.That(((GetUserBirthdaysRequest)_bus.LastInvoked!).UserIds, Is.EqualTo(new[] { "user-1" }));
    }

    [Test]
    public async Task BirthdayAsync_IdentityAnsweredNull_StaysNull()
    {
        // "Hidden", "never recorded", "purged" and "unknown id" all arrive as a null date, and the
        // resolver is not allowed to invent a difference between them.
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-1", null));

        Assert.That(await Resolver().BirthdayAsync("user-1"), Is.Null);
    }

    [Test]
    public async Task BirthdayAsync_AnswerForSomebodyElse_IsNotUsed()
    {
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-2", new DateOnly(1990, 3, 4)));

        Assert.That(await Resolver().BirthdayAsync("user-1"), Is.Null);
    }

    [Test]
    public async Task BirthdayAsync_LookupFailure_OmitsTheFieldRatherThanThrowing()
    {
        // No registered answer => FakeMessageBus throws, standing in for Identity being down. A 500
        // on every profile read would be a worse outage than a missing optional field.
        Assert.That(await Resolver().BirthdayAsync("user-1"), Is.Null);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task BirthdayAsync_BlankUserId_SkipsTheRoundTrip(string userId)
    {
        Assert.That(await Resolver().BirthdayAsync(userId), Is.Null);
        Assert.That(_bus.LastInvoked, Is.Null);
    }

    // ── connections ──────────────────────────────────────────────────────────

    [Test]
    public async Task ConnectionsAsync_MapsSteamOntoTheTypedDto()
    {
        PrivacyTestHelpers.RegisterConnections(_bus, "user-1", PrivacyTestHelpers.Steam("76561198000000000"));

        var connections = await Resolver().ConnectionsAsync("user-1");

        Assert.That(connections, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(connections[0].Type, Is.EqualTo(ExternalConnectionTypes.Steam));
            Assert.That(connections[0].ExternalId, Is.EqualTo("76561198000000000"));
            Assert.That(connections[0].DisplayName, Is.Null);
            Assert.That(connections[0].Verified, Is.True);
        });
    }

    [Test]
    public async Task ConnectionsAsync_SupportsMoreThanOneTypeWithoutAContractChange()
    {
        // The whole reason the field is a list of { type, externalId, displayName? } rather than a
        // bare steamId: a second provider must be additive.
        PrivacyTestHelpers.RegisterConnections(_bus, "user-1",
            PrivacyTestHelpers.Steam("76561198000000000"),
            new ExternalConnectionSummary { Type = "example", ExternalId = "ext-1", DisplayName = "Someone" });

        var connections = await Resolver().ConnectionsAsync("user-1");

        Assert.That(connections.Select(c => c.Type), Is.EqualTo(new[] { ExternalConnectionTypes.Steam, "example" }));
        Assert.That(connections[1].DisplayName, Is.EqualTo("Someone"));
    }

    [Test]
    public async Task ConnectionsAsync_NothingLinked_IsAnEmptyList()
    {
        PrivacyTestHelpers.RegisterConnections(_bus, "user-1");

        Assert.That(await Resolver().ConnectionsAsync("user-1"), Is.Empty);
    }

    [Test]
    public async Task ConnectionsAsync_EntryWithNoExternalId_IsDropped()
    {
        // A half-populated entry would render as a connection with nothing behind it.
        PrivacyTestHelpers.RegisterConnections(_bus, "user-1",
            new ExternalConnectionSummary { Type = ExternalConnectionTypes.Steam, ExternalId = "" });

        Assert.That(await Resolver().ConnectionsAsync("user-1"), Is.Empty);
    }

    [Test]
    public async Task ConnectionsAsync_AnswerForSomebodyElse_IsNotUsed()
    {
        PrivacyTestHelpers.RegisterConnections(_bus, "user-2", PrivacyTestHelpers.Steam("76561198000000000"));

        Assert.That(await Resolver().ConnectionsAsync("user-1"), Is.Empty);
    }

    [Test]
    public async Task ConnectionsAsync_LookupFailure_OmitsTheFieldRatherThanThrowing()
    {
        Assert.That(await Resolver().ConnectionsAsync("user-1"), Is.Empty);
    }

    // ── the restrictive stand-in ─────────────────────────────────────────────

    [Test]
    public async Task NoIdentityProfileFactsResolver_ReportsNeitherField()
    {
        var resolver = new NoIdentityProfileFactsResolver();

        Assert.That(await resolver.BirthdayAsync("user-1"), Is.Null);
        Assert.That(await resolver.ConnectionsAsync("user-1"), Is.Empty);
    }
}
