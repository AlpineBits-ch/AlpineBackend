using Identity.Contracts.Bus.Events;
using Social.Api.Services;

namespace Social.Api.Integration.Privacy;

/// <summary>
/// Drops Social's cached copy of one account's privacy record when Identity writes it
/// (privacy spec §1.4).
///
/// <para>Eviction, not overwrite: the event deliberately carries no values, so the only correct
/// reaction is to forget and re-ask. That also makes two events delivered out of order harmless -
/// neither can resurrect a setting the user just turned off.</para>
/// </summary>
public class UserPrivacySettingsChangedHandler
{
    public static Task Handle(UserPrivacySettingsChangedEvent @event, PrivacySettingsCache cache)
        => cache.EvictAsync(@event.UserId);
}
