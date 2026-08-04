using Isle.Api.Endpoints;
using Isle.Api.Services.Privacy;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Domain.Entity.Voice;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Endpoints;

/// <summary>
/// Covers VoiceMembershipEndpoints (Join/Leave/GetConnectionStatus) - a static, Wolverine-attributed
/// HTTP surface. Called directly as plain C# methods with a TestIsleContext and Redis-backed
/// registries (RedisTestFactory, same as VoicePlayerRegistry's own production dependency), plus an
/// NSubstitute IMessageBus for the RemovePlayerCommand invoke. No SaveChangesAsync is needed on the
/// endpoint's own behalf - it never writes to the DbContext, only reads.
/// </summary>
[TestFixture]
public class VoiceEndpointsTests
{
    private TestIsleContext _context = null!;
    private VoicePlayerRegistry _registry = null!;
    private VoiceTrackRegistry _tracks = null!;
    private PlayerPresenceManager _presence = null!;
    private IMessageBus _bus = null!;
    private PositionalVoiceConsent _consent = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _tracks = new VoiceTrackRegistry();
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _bus = Substitute.For<IMessageBus>();

        // T2-19: Join now refuses anyone who has not consented to positional capture, so every
        // pre-existing Join test needs a consenting account rather than the fail-closed default.
        _consent = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice("user-1", true)]).Consent;
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Join ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Join_NoUserId_ReturnsUnauthorized()
    {
        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.CreateAnonymous(), _context, _registry, _consent, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Join_NoLinkedPlayer_ReturnsBadRequest()
    {
        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, _consent, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Join_PlayerMissingSteamId_ReturnsBadRequest()
    {
        var player = TestData.Player("");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, _consent, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Join_LinkedPlayer_RegistersVoiceMappingAndReturnsNoContent()
    {
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, _consent, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_registry.TryGetPlayerId("steam-1", out var playerId), Is.True);
        Assert.That(playerId, Is.EqualTo("user-1"));
    }

    // ── Join and positional voice consent (T2-19) ─────────────────────────

    [Test]
    public async Task Join_ConsentWithheld_RefusesAndRegistersNothing()
    {
        // The registry entry is the entire trigger for positional capture: without it
        // StatsStreamIngestionService ignores the player's position snapshots, so there is no
        // cluster membership, no audibility and nothing for a peer to pull.
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var consent = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice("user-1", false)]).Consent;

        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, consent, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<JsonHttpResult<VoiceRefusalDto>>());
            Assert.That(((JsonHttpResult<VoiceRefusalDto>)result).StatusCode,
                Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(((JsonHttpResult<VoiceRefusalDto>)result).Value!.Code,
                Is.EqualTo(VoiceMembershipEndpoints.PositionalVoiceRefusalCode));
            Assert.That(_registry.TryGetPlayerId("steam-1", out _), Is.False);
        });
    }

    [Test]
    public async Task Join_ConsentCannotBeResolved_RefusesRatherThanRegistering()
    {
        // Fail closed. A stored consent flag that stops applying the moment Identity hiccups is not
        // a consent control.
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var consent = PrivacyTestFactory.Build(lookupFails: true).Consent;

        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, consent, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<JsonHttpResult<VoiceRefusalDto>>());
            Assert.That(_registry.TryGetPlayerId("steam-1", out _), Is.False);
        });
    }

    [Test]
    public async Task Join_UnlinkedPlayer_IsRefusedBeforeConsentIsEvenConsulted()
    {
        // Ordering edge: the linkage failure is the more specific answer, and consulting Identity
        // for someone who cannot join anyway is a round trip for nothing.
        var consent = PrivacyTestFactory.Build(lookupFails: true).Consent;

        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, consent, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ── Leave ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Leave_NoUserId_ReturnsUnauthorized()
    {
        var result = await VoiceMembershipEndpoints.Leave(
            _registry, _tracks, _bus, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Leave_RegisteredPlayer_UnregistersRemovesTrackAndInvokesRemovePlayerCommand()
    {
        await _registry.RegisterAsync("user-1", "steam-1");
        _tracks.Publish("user-1", "cf-session", "audio");

        var result = await VoiceMembershipEndpoints.Leave(
            _registry, _tracks, _bus, TestPrincipal.Create("user-1"));

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_registry.TryGetSteamId("user-1", out _), Is.False);
        Assert.That(_tracks.TryGet("user-1", out _), Is.False);
        await _bus.Received(1).InvokeAsync(Arg.Is<RemovePlayerCommand>(c => c.PlayerId == "user-1"));
    }

    // ── GetConnectionStatus ───────────────────────────────────────────────

    [Test]
    public async Task GetConnectionStatus_NoLinkedPlayer_ReturnsNotFound()
    {
        var result = await VoiceMembershipEndpoints.GetConnectionStatus(
            TestPrincipal.Create("user-1"), _registry, _context, _presence);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task GetConnectionStatus_RegisteredInVoiceRegistry_ReportsBothConnected()
    {
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        await _registry.RegisterAsync("user-1", "steam-1");

        var result = await VoiceMembershipEndpoints.GetConnectionStatus(
            TestPrincipal.Create("user-1"), _registry, _context, _presence);

        var dto = (VoiceConnectionStatusDto)((IValueHttpResult)result).Value!;
        Assert.That(dto.IsVoiceConnected, Is.True);
        Assert.That(dto.IsGameConnected, Is.True);
    }

    [Test]
    public async Task GetConnectionStatus_NotInVoiceButPresentInGame_ReportsGameConnectedOnly()
    {
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(player.Id);

        var result = await VoiceMembershipEndpoints.GetConnectionStatus(
            TestPrincipal.Create("user-1"), _registry, _context, _presence);

        var dto = (VoiceConnectionStatusDto)((IValueHttpResult)result).Value!;
        Assert.That(dto.IsVoiceConnected, Is.False);
        Assert.That(dto.IsGameConnected, Is.True);
    }

    [Test]
    public async Task GetConnectionStatus_LinkedButFullyOffline_ReportsBothDisconnected()
    {
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await VoiceMembershipEndpoints.GetConnectionStatus(
            TestPrincipal.Create("user-1"), _registry, _context, _presence);

        var dto = (VoiceConnectionStatusDto)((IValueHttpResult)result).Value!;
        Assert.That(dto.IsVoiceConnected, Is.False);
        Assert.That(dto.IsGameConnected, Is.False);
    }
}
