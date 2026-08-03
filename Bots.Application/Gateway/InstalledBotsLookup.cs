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

    /// <summary>
    /// Installed bots, narrowed to those that may actually see <paramref name="channelId"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetBotUserIdsForChannelAsync(
        MicroserviceContext ctx,
        IBotChannelVisibility visibility,
        string guildId,
        string? channelId)
    {
        var installed = await GetBotUserIdsInGuildAsync(ctx, guildId);
        if (installed.Count == 0) return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(channelId)) return installed;

        return await visibility.FilterToVisibleAsync(channelId, installed);
    }
}
