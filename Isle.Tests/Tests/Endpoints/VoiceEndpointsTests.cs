using Isle.Api.Endpoints;
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
/// Covers VoiceMembershipEndpoints (Join/Leave/GetConnectionStatus) - a static,
/// Wolverine-attributed HTTP surface.
/// </summary>
[TestFixture]
public class VoiceEndpointsTests
{
    private TestIsleContext _context = null!;
    private VoicePlayerRegistry _registry = null!;
    private VoiceTrackRegistry _tracks = null!;
    private PlayerPresenceManager _presence = null!;
    private IMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _tracks = new VoiceTrackRegistry();
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _bus = Substitute.For<IMessageBus>();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Join ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Join_NoUserId_ReturnsUnauthorized()
    {
        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.CreateAnonymous(), _context, _registry, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Join_NoLinkedPlayer_ReturnsBadRequest()
    {
        var result = await VoiceMembershipEndpoints.Join(
            TestPrincipal.Create("user-1"), _context, _registry, CancellationToken.None);

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
            TestPrincipal.Create("user-1"), _context, _registry, CancellationToken.None);

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
            TestPrincipal.Create("user-1"), _context, _registry, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_registry.TryGetPlayerId("steam-1", out var playerId), Is.True);
        Assert.That(playerId, Is.EqualTo("user-1"));
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
