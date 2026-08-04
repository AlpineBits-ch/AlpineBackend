using Identity.Contracts.Bus.Events;
using Isle.Api.Services.Privacy;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Domain.Entity.Voice;
using Wolverine.Attributes;

namespace Isle.Api.Handlers.Privacy;

/// <summary>
/// Keeps Isle's copy of one account's privacy record honest, and - the part that matters - acts on
/// the change immediately when it revokes positional voice capture (T2-19).
/// </summary>
[NonTransactional]
public class PrivacyCacheInvalidationHandler
{
    public static async Task<object?> Handle(
        UserPrivacySettingsChangedEvent message,
        PrivacySettingsCache cache,
        PositionalVoiceConsent consent,
        VoicePlayerRegistry registry,
        VoiceTrackRegistry tracks,
        ILogger<PrivacyCacheInvalidationHandler> logger,
        CancellationToken ct = default)
    {
        // The event carries no values by design, so forget first and re-ask below - that also makes
        // two events delivered out of order harmless, since neither can resurrect a setting the
        // user just turned off.
        await cache.InvalidateAsync(message.UserId, ct);

        // Not in the voice grid: nothing is being captured, so there is nothing to revoke and no
        // reason to spend an Identity round trip on the overwhelmingly common case.
        if (!registry.TryGetSteamId(message.UserId, out _))
            return null;

        if (await consent.MayCaptureAsync(message.UserId, ct))
            return null;

        logger.LogInformation(
            "Positional voice capture revoked for {UserId}; removing them from the voice grid", message.UserId);

        await registry.UnregisterAsync(message.UserId);
        tracks.Remove(message.UserId);

        // Cascaded rather than invoked: VoiceClusterHandler turns this into the PeerBecameInaudible
        // events that unsubscribe every peer who could hear them.
        return new RemovePlayerCommand(message.UserId);
    }
}
