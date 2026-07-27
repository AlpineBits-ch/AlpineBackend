using Bots.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bots.Application.Gateway;

/// <summary>Shared "which bots are installed in this guild" query used by every dispatch
/// handler in Bots.Application/Gateway/Handlers - one indexed local query, no bus round-trip.</summary>
public static class InstalledBotsLookup
{
    public static Task<List<string>> GetBotUserIdsInGuildAsync(MicroserviceContext ctx, string guildId) =>
        ctx.BotInstallations.AsNoTracking()
            .Where(i => i.GuildId == guildId)
            .Join(ctx.BotApplications.AsNoTracking(), i => i.BotApplicationId, a => a.Id, (_, a) => a.BotUserId)
            .ToListAsync();
}
