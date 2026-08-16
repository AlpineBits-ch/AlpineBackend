using Identity.Contracts.Bus.Events;
using Isle.Api.Handlers.Privacy;
using Isle.Api.Services.Privacy;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Domain.Entity.Voice;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Handlers.Privacy;

/// <summary>
/// Covers the eviction handler and, more importantly, the revocation it performs: someone turning
/// positional voice capture off is not expressing a preference for next session, they are in the
/// world with a live microphone right now.
/// </summary>
[TestFixture]
public class PrivacyCacheInvalidationHandlerTests
{
    private const string UserId = "user-1";

    private VoicePlayerRegistry _registry = null!;
    private VoiceTrackRegistry _tracks = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _tracks = new VoiceTrackRegistry();
    }

    private Task<object?> HandleAsync(PrivacyTestFactory.Bundle bundle) =>
        PrivacyCacheInvalidationHandler.Handle(
            new UserPrivacySettingsChangedEvent { UserId = UserId, Version = 2 },
            bundle.Settings, bundle.Consent, _registry, _tracks,
            NullLogger<PrivacyCacheInvalidationHandler>.Instance);

    // ── normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_EvictsTheCachedRecord()
    {
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)]);
        await bundle.Settings.GetAsync(UserId);

        await HandleAsync(bundle);

        Assert.That(bundle.Cache.HasEntry(PrivacySettingsCache.KeyFor(UserId)), Is.False);
    }

    [Test]
    public async Task Handle_PlayerStillConsents_LeavesThemInTheVoiceGrid()
    {
        await _registry.RegisterAsync(UserId, "steam-1");
        _tracks.Publish(UserId, UserId, "TR_sid", "audio");
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)]);

        var cascaded = await HandleAsync(bundle);

        Assert.Multiple(() =>
        {
            Assert.That(cascaded, Is.Null);
            Assert.That(_registry.TryGetSteamId(UserId, out _), Is.True);
            Assert.That(_tracks.TryGet(UserId, out _), Is.True);
        });
    }

    // ── edge ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_PlayerNotInVoice_DoesNothingBeyondEvicting()
    {
        // The overwhelmingly common case: a settings change by someone who is not in the game.
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, false)]);

        var cascaded = await HandleAsync(bundle);

        Assert.That(cascaded, Is.Null);
        await bundle.Bus.DidNotReceiveWithAnyArgs()
            .InvokeAsync<Identity.Contracts.Bus.Response.GetUserPrivacySettingsResponse>(default!, default, default);
    }

    // ── negative (the point) ──────────────────────────────────────────────

    [Test]
    public async Task Handle_ConsentRevokedMidSession_TearsDownPositionalCaptureImmediately()
    {
        await _registry.RegisterAsync(UserId, "steam-1");
        _tracks.Publish(UserId, UserId, "TR_sid", "audio");
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, false)]);

        var cascaded = await HandleAsync(bundle);

        Assert.Multiple(() =>
        {
            // The registry entry is what feeds the whole positional pipeline - dropping it is what
            // actually stops capture, rather than merely hiding the player.
            Assert.That(_registry.TryGetSteamId(UserId, out _), Is.False);
            Assert.That(_tracks.TryGet(UserId, out _), Is.False);
            Assert.That(cascaded, Is.InstanceOf<RemovePlayerCommand>());
            Assert.That(((RemovePlayerCommand)cascaded!).PlayerId, Is.EqualTo(UserId));
        });
    }

    [Test]
    public async Task Handle_ConsentUnresolvableAfterEviction_TearsDownRatherThanAssumingConsent()
    {
        await _registry.RegisterAsync(UserId, "steam-1");
        var bundle = PrivacyTestFactory.Build(lookupFails: true);

        var cascaded = await HandleAsync(bundle);

        Assert.Multiple(() =>
        {
            Assert.That(_registry.TryGetSteamId(UserId, out _), Is.False);
            Assert.That(cascaded, Is.InstanceOf<RemovePlayerCommand>());
        });
    }
}
