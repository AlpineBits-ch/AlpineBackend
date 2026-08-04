using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Applies a privacy-settings change to the in-process telemetry consent snapshot the moment it
/// happens, rather than up to a refresh interval later.
/// </summary>
public class UserPrivacySettingsChangedConsentHandler
{
    public static async Task Handle(
        UserPrivacySettingsChangedEvent changed,
        MicroserviceContext ctx,
        DataCollectionConsentSnapshot snapshot)
    {
        var allows = await ctx.UserPrivacySettings.AsNoTracking()
            .Where(p => p.UserId == changed.UserId)
            .Select(p => p.AllowDataCollection)
            .FirstOrDefaultAsync();

        snapshot.Set(changed.UserId, allows);
    }
}
