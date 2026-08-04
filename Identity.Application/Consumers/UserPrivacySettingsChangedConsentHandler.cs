using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Applies a privacy-settings change to the in-process telemetry consent snapshot the moment it
/// happens, rather than up to a refresh interval later.
///
/// <para>Withdrawal of consent has to be immediate (T1-10 says so about optional consents, and it is
/// the only reading of a consent control that means anything). The periodic refresh in
/// <see cref="DataCollectionConsentRefreshService"/> would leave a window in which a user who has
/// just turned data collection off still has their account id attached to outgoing error reports.
/// This closes it.</para>
///
/// <para>Re-reads the flag from the database rather than carrying it on the event: the event
/// deliberately carries only the user id and a version, so that adding a field to the settings record
/// does not mean changing a contract every consuming service deserializes.</para>
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
