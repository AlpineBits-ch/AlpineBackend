using Bots.Infrastructure.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Bots.Application.Commands;

/// <summary>
/// Bots' participant in the AccountDeletionSaga fan-out (T1-9 of docs/specs/privacy.md).
///
/// <para><b>The problem this solves: an orphaned bot application.</b> Every management route in
/// <c>BotApplicationEndpoint</c> gates on <c>app.OwnerUserId == caller</c>. When the owner's account
/// is purged, that id belongs to a tombstoned row nobody can ever sign in as - so the application
/// becomes permanently unadministrable while its bot account is still enabled, still holding a
/// client secret, still connected to the Gateway and still a member of every guild it was installed
/// in. Nobody can rotate its secret, nobody can disable it, and nobody can remove it. That is a live
/// credential with no owner.</para>
///
/// <para><b>Disable rather than transfer.</b> Transfer needs a transferee, and there is nobody to
/// choose: a bot application has exactly one owner column and no co-maintainers, and picking a guild
/// owner to hand it to would give a third party a credential the deleted user never agreed to share -
/// while installing a bot into a guild grants that guild nothing about the application itself. So the
/// application is disabled the same way its owner's own <c>DELETE /api/v1/applications/{id}</c>
/// disables it, through the same <c>DisableBotAccountCommand</c>, which takes the bot's Identity
/// account to Inactive and stops it signing in.</para>
///
/// <para><b>Installations are removed as well.</b> Disabling stops the bot acting, but leaving the
/// installation rows would leave every guild admin looking at a bot in their member list that can
/// never be reached, never respond and never be uninstalled through the owner. The row here is only
/// the Bots service's own bookkeeping - the actual <c>GuildMember</c> lives in Guild and is that
/// service's to remove.</para>
///
/// <para>Idempotent: a redelivered command finds the applications already disabled and the
/// installations already gone. No SaveChangesAsync - Wolverine's transactional middleware commits.</para>
/// </summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(
        PurgeUserDataCommand command,
        MicroserviceContext ctx,
        IMessageBus bus,
        ILogger<PurgeUserDataCommandHandler> logger)
    {
        var owned = await ctx.BotApplications
            .Where(a => a.OwnerUserId == command.UserId)
            .ToListAsync();

        if (owned.Count == 0)
        {
            return new PurgeUserDataCommandResponse { UserId = command.UserId, Service = "bots" };
        }

        var applicationIds = owned.Select(a => a.Id).ToList();

        var installations = await ctx.BotInstallations
            .Where(i => applicationIds.Contains(i.BotApplicationId))
            .ToListAsync();
        ctx.BotInstallations.RemoveRange(installations);

        foreach (var app in owned)
        {
            // Only for applications still enabled: DisableBotAccountCommand is idempotent, but not
            // re-sending it keeps a redelivered purge from putting another N messages on the bus.
            if (app.IsEnabled)
            {
                await bus.InvokeAsync(new DisableBotAccountCommand { BotUserId = app.BotUserId });
                app.IsEnabled = false;
            }

            // The owner id is deliberately left pointing at the tombstoned account rather than
            // nulled. It is the only remaining answer to "whose application was this", the column is
            // not nullable, and the account it names no longer identifies a person - which is exactly
            // what the tombstone is for.
        }

        logger.LogInformation(
            "Purge: disabled {Applications} bot application(s) and removed {Installations} installation(s) "
            + "owned by {UserId}", owned.Count, installations.Count, command.UserId);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "bots",
        };
    }
}
