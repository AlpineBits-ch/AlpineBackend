using Identity.Contracts.Bus.Events;
using Social.Api.Services;

namespace Social.Api.Integration.Privacy;

/// <summary>
/// Drops Social's cached copy of one account's privacy record when Identity writes it (privacy spec
/// §1.4).
/// </summary>
public class UserPrivacySettingsChangedHandler
{
    public static Task Handle(UserPrivacySettingsChangedEvent @event, PrivacySettingsCache cache)
        => cache.EvictAsync(@event.UserId);
}
