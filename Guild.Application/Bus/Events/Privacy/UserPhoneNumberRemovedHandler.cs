using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Events;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Events.Privacy;

/// <summary>
/// Withdraws every per-guild phone-sharing opt-in when the account behind them removes its number.
/// </summary>
public class UserPhoneNumberRemovedHandler
{
    public static async Task Handle(
        UserPhoneNumberRemovedEvent removed,
        MicroserviceContext ctx,
        ILogger<UserPhoneNumberRemovedHandler> logger)
    {
        var sharing = await ctx.GuildMembers
            .Where(m => m.UserId == removed.UserId && m.SharePhoneForPayments)
            .ToListAsync();

        if (sharing.Count == 0) return;

        foreach (var member in sharing)
        {
            member.SharePhoneForPayments = false;
            member.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Counted, never listed.
        logger.LogInformation(
            "Cleared {Count} phone-sharing opt-in(s) for {UserId} after their number was removed",
            sharing.Count, removed.UserId);
    }
}
