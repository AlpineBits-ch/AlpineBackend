using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Domain;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Covers <see cref="GetGuildDirectMessagePreferenceHandler"/> - the contract Messaging resolves the
/// <c>FriendsAndServerMembers</c> DM branch against (privacy spec T0-2 / T2-14).
///
/// <para>The shape of the answer is what is being pinned here, because Messaging codes against it:
/// effective values rather than raw rows, non-members omitted rather than answered, and an empty
/// guild list meaning "all of them".</para>
/// </summary>
[TestFixture]
public class GetGuildDirectMessagePreferenceHandlerTests
{
    private const string GuildId = "gild-1";
    private const string OtherGuildId = "gild-2";
    private const string ForeignGuildId = "gild-foreign";
    private const string UserId = "user-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();

        foreach (var guildId in new[] { GuildId, OtherGuildId, ForeignGuildId })
        {
            _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
            {
                Id = guildId, Name = "g", OwnerId = "owner-1",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        foreach (var guildId in new[] { GuildId, OtherGuildId })
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"memb-{guildId}", GuildId = guildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
                SearchValue = UserId.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private GuildDirectMessagePreferenceService ServiceWith(DirectMessagePolicy policy)
    {
        var settings = PrivacyTestFactory.Permissive(UserId);
        settings.DirectMessagePolicy = policy;

        return new GuildDirectMessagePreferenceService(
            _context, PrivacyTestFactory.Privacy(_bus, _cache, settings));
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ReturnsTheStoredOverrideForTheNamedGuild()
    {
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(UserId, GuildId, false));
        await _context.SaveChangesAsync();

        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = UserId, GuildIds = [GuildId] },
            ServiceWith(DirectMessagePolicy.Everyone));

        var summary = response.Preferences.Single();
        Assert.Multiple(() =>
        {
            Assert.That(summary.GuildId, Is.EqualTo(GuildId));
            Assert.That(summary.AllowDirectMessages, Is.False);
        });
    }

    [Test]
    public async Task Handle_WithNoGuildIds_AnswersForEveryGuildTheUserIsIn()
    {
        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = UserId },
            ServiceWith(DirectMessagePolicy.Everyone));

        Assert.That(
            response.Preferences.Select(p => p.GuildId),
            Is.EquivalentTo(new[] { GuildId, OtherGuildId }));
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AGuildWithNoRow_StillAppearsCarryingTheInheritedValue()
    {
        // A caller must never have to know the override table exists, and absence must never read
        // as "allowed" by omission.
        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = UserId, GuildIds = [GuildId] },
            ServiceWith(DirectMessagePolicy.FriendsAndServerMembers));

        Assert.That(response.Preferences.Single().AllowDirectMessages, Is.True);
    }

    [Test]
    public async Task Handle_MixedOverriddenAndInherited_ReportsEachCorrectly()
    {
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(UserId, GuildId, false));
        await _context.SaveChangesAsync();

        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = UserId, GuildIds = [GuildId, OtherGuildId] },
            ServiceWith(DirectMessagePolicy.Everyone));

        var byGuild = response.Preferences.ToDictionary(p => p.GuildId, p => p.AllowDirectMessages);
        Assert.Multiple(() =>
        {
            Assert.That(byGuild[GuildId], Is.False);
            Assert.That(byGuild[OtherGuildId], Is.True);
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AGuildTheUserIsNotIn_IsOmitted()
    {
        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = UserId, GuildIds = [ForeignGuildId] },
            ServiceWith(DirectMessagePolicy.Everyone));

        Assert.That(response.Preferences, Is.Empty);
    }

    [Test]
    public async Task Handle_WithNoUserId_AnswersNothingRatherThanEverything()
    {
        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = "" },
            ServiceWith(DirectMessagePolicy.Everyone));

        Assert.That(response.Preferences, Is.Empty);
    }

    [Test]
    public async Task Handle_GlobalPolicyOfNobody_ReportsEveryUnoverriddenGuildAsClosed()
    {
        var response = await GetGuildDirectMessagePreferenceHandler.Handle(
            new GetGuildDirectMessagePreferenceRequest { UserId = UserId },
            ServiceWith(DirectMessagePolicy.Nobody));

        Assert.That(response.Preferences.Select(p => p.AllowDirectMessages), Is.All.False);
    }
}
