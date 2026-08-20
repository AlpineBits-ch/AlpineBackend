using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Domain;
using Guild.Application.Endpoints.Guild;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers <see cref="GuildPrivacyEndpoint"/> and the resolution rules behind it (privacy spec
/// T2-14).
/// </summary>
[TestFixture]
public class GuildPrivacyEndpointTests
{
    private const string GuildId = "gild-1";
    private const string OtherGuildId = "gild-2";
    private const string UserId = "user-1";
    private const string StrangerId = "user-stranger";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();

        AddGuild(GuildId);
        AddGuild(OtherGuildId);
        AddMember(GuildId, UserId);
        AddMember(OtherGuildId, UserId);

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private void AddGuild(string guildId) =>
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = guildId, Name = "g", OwnerId = "owner-1", CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddMember(string guildId, string userId) =>
        _context.GuildMembers.Add(new GuildMember
        {
            Id = $"memb-{guildId}-{userId}", GuildId = guildId, UserId = userId,
            JoinedAt = DateTime.UtcNow, SearchValue = userId.ToUpperInvariant(),
            CreatedAt = Now, UpdatedAt = Now,
        });

    private GuildDirectMessagePreferenceService ServiceWith(DirectMessagePolicy policy)
    {
        var settings = PrivacyTestFactory.Permissive(UserId);
        settings.DirectMessagePolicy = policy;

        return new GuildDirectMessagePreferenceService(
            _context, PrivacyTestFactory.Privacy(_bus, _cache, settings));
    }

    private GuildDirectMessagePreferenceService UnreachableService() =>
        new(_context, PrivacyTestFactory.UnreachablePrivacy(_bus, _cache));

    // ══════════════════════════════════════════════════════════════════════
    // PUT /api/v1/guilds/{guildId}/privacy
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Put_FirstWrite_CreatesTheOverride()
    {
        var service = ServiceWith(DirectMessagePolicy.Everyone);

        var result = await GuildPrivacyEndpoint.UpdateAsync(
            GuildId, new UpdateGuildPrivacyDto { AllowDirectMessages = false },
            service, _context, TestPrincipal.Create(UserId));

        await _context.SaveChangesAsync();

        var ok = result as Ok<GuildDirectMessagePreferenceDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.AllowDirectMessages, Is.False);
            Assert.That(ok.Value.IsOverride, Is.True);
            Assert.That(ok.Value.GuildId, Is.EqualTo(GuildId));
        });

        var stored = await _context.GuildDirectMessagePreferences.SingleAsync();
        Assert.That(stored.AllowDirectMessages, Is.False);
    }

    [Test]
    public async Task Put_Twice_UpdatesInPlaceRatherThanAccumulatingRows()
    {
        var service = ServiceWith(DirectMessagePolicy.Everyone);
        var principal = TestPrincipal.Create(UserId);

        await GuildPrivacyEndpoint.UpdateAsync(
            GuildId, new UpdateGuildPrivacyDto { AllowDirectMessages = false }, service, _context, principal);
        await _context.SaveChangesAsync();

        await GuildPrivacyEndpoint.UpdateAsync(
            GuildId, new UpdateGuildPrivacyDto { AllowDirectMessages = true }, service, _context, principal);
        await _context.SaveChangesAsync();

        var rows = await _context.GuildDirectMessagePreferences.ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].AllowDirectMessages, Is.True);
        });
    }

    [Test]
    public async Task Put_ForAGuildTheCallerIsNotIn_Is404AndStoresNothing()
    {
        var service = ServiceWith(DirectMessagePolicy.Everyone);

        var result = await GuildPrivacyEndpoint.UpdateAsync(
            GuildId, new UpdateGuildPrivacyDto { AllowDirectMessages = false },
            service, _context, TestPrincipal.Create(StrangerId));

        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(_context.GuildDirectMessagePreferences.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task Put_WithNoSubjectClaim_IsUnauthorized()
    {
        var result = await GuildPrivacyEndpoint.UpdateAsync(
            GuildId, new UpdateGuildPrivacyDto { AllowDirectMessages = false },
            ServiceWith(DirectMessagePolicy.Everyone), _context, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    // ══════════════════════════════════════════════════════════════════════
    // GET /api/v1/users/me/guild-privacy
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Get_ReturnsOnlyTheCallersOverrides()
    {
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(UserId, GuildId, false));
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(StrangerId, GuildId, false));
        await _context.SaveChangesAsync();

        var result = await GuildPrivacyEndpoint.GetAllForUserAsync(
            ServiceWith(DirectMessagePolicy.Everyone), TestPrincipal.Create(UserId));

        var ok = result as Ok<List<GuildDirectMessagePreferenceDto>>;
        Assert.That(ok, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!, Has.Count.EqualTo(1));
            Assert.That(ok.Value[0].GuildId, Is.EqualTo(GuildId));
            Assert.That(ok.Value[0].AllowDirectMessages, Is.False);
        });
    }

    [Test]
    public async Task Get_WithNoOverridesStored_IsAnEmptyListNotAnInventedOnePerGuild()
    {
        var result = await GuildPrivacyEndpoint.GetAllForUserAsync(
            ServiceWith(DirectMessagePolicy.Everyone), TestPrincipal.Create(UserId));

        var ok = result as Ok<List<GuildDirectMessagePreferenceDto>>;
        Assert.That(ok!.Value!, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Resolution - what a guild with no row means
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Resolve_NoOverride_InheritsFromTheGlobalPolicy()
    {
        var resolved = await ServiceWith(DirectMessagePolicy.FriendsAndServerMembers)
            .ResolveAsync(UserId, [GuildId]);

        Assert.That(resolved[GuildId], Is.True);
    }

    [Test]
    public async Task Resolve_NoOverrideAndAGlobalPolicyOfNobody_IsFalse()
    {
        // Defaulting a per-server toggle on while the account-level answer is "no one" would be the
        // setting contradicting itself.
        var resolved = await ServiceWith(DirectMessagePolicy.Nobody).ResolveAsync(UserId, [GuildId]);

        Assert.That(resolved[GuildId], Is.False);
    }

    [Test]
    public async Task Resolve_AnOverride_WinsOverTheGlobalPolicy()
    {
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(UserId, GuildId, false));
        await _context.SaveChangesAsync();

        var resolved = await ServiceWith(DirectMessagePolicy.Everyone).ResolveAsync(UserId, [GuildId]);

        Assert.That(resolved[GuildId], Is.False);
    }

    [Test]
    public async Task Resolve_WithNoGuildIds_CoversEveryGuildTheUserIsIn()
    {
        var resolved = await ServiceWith(DirectMessagePolicy.Everyone).ResolveAsync(UserId, []);

        Assert.That(resolved.Keys, Is.EquivalentTo(new[] { GuildId, OtherGuildId }));
    }

    [Test]
    public async Task Resolve_AGuildTheUserIsNotIn_IsOmittedRatherThanAnswered()
    {
        var resolved = await ServiceWith(DirectMessagePolicy.Everyone)
            .ResolveAsync(StrangerId, [GuildId]);

        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public async Task Resolve_EveryGuildOverridden_NeverAsksIdentityAtAll()
    {
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(UserId, GuildId, true));
        _context.GuildDirectMessagePreferences.Add(
            GuildDirectMessagePreference.Create(UserId, OtherGuildId, false));
        await _context.SaveChangesAsync();

        // Unreachable Identity on purpose: if the resolution needed it, this would come back
        // restrictive and the assertion below would fail.
        var resolved = await UnreachableService().ResolveAsync(UserId, []);

        Assert.Multiple(() =>
        {
            Assert.That(resolved[GuildId], Is.True);
            Assert.That(resolved[OtherGuildId], Is.False);
        });
    }

    [Test]
    public async Task Resolve_NoOverrideAndIdentityUnreachable_FallsBackToTheRestrictivePolicy()
    {
        // The restrictive DM default is Friends, which admits nothing on the server-member branch -
        // so the refusal lands at the policy, and this toggle cannot turn an outage into permission.
        var resolved = await UnreachableService().ResolveAsync(UserId, [GuildId]);

        Assert.That(resolved[GuildId], Is.True);
        Assert.That(
            GuildDirectMessagePreferenceService.DefaultFor(
                PrivacySettingsCache.Restrictive(UserId).DirectMessagePolicy),
            Is.True);
        Assert.That(PrivacySettingsCache.Restrictive(UserId).DirectMessagePolicy,
            Is.EqualTo(DirectMessagePolicy.Friends));
    }
}
