using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetGuildSnapshotForBotHandler
{
    public static async Task<GetGuildSnapshotForBotResponse> Handle(
        GetGuildSnapshotForBotRequest request,
        MicroserviceContext ctx,
        GuildPermissionService permissionService)
    {
        var guild = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == request.GuildId)
            .Select(g => new { g.Id, g.Name, g.OwnerId, g.Kind, g.Features })
            .FirstOrDefaultAsync();

        if (guild is null) return new GetGuildSnapshotForBotResponse { Guild = null };

        var channels = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.GuildId == request.GuildId)
            .Select(c => new ChannelSnapshot
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type.ToString(),
                Position = c.Position,
                CategoryId = c.CategoryId,
            })
            .ToListAsync();

        // This snapshot is the bot's GUILD_CREATE hydration burst. It previously returned every
        // channel in the guild regardless of the bot's own permissions, so a bot confined to one
        // channel still learned the name and layout of every private channel - and knew which ones
        // to watch for. Narrowed to the channels this bot can actually view.
        channels = await FilterVisibleChannelsAsync(permissionService, request.BotUserId, channels);

        var roles = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == request.GuildId)
            .Select(r => new RoleSnapshot
            {
                Id = r.Id,
                Name = r.Name,
                Color = r.Color,
                Position = r.Position,
                Permissions = (ulong)r.Permissions,
            })
            .ToListAsync();

        var self = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == request.GuildId && m.UserId == request.BotUserId)
            .Select(m => new GuildMemberSummary
            {
                UserId = m.UserId,
                Nickname = m.Nickname,
                RoleIds = m.RoleMembers.Select(rm => rm.RoleId).ToList(),
                JoinedAt = m.JoinedAt,
                IsBot = m.Type == MemberType.Bot,
            })
            .FirstOrDefaultAsync();

        return new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot
            {
                Id = guild.Id,
                Name = guild.Name,
                OwnerId = guild.OwnerId,
                Kind = guild.Kind.ToString(),
                Features = (ulong)guild.Features,
                Channels = channels,
                Roles = roles,
                Self = self,
            },
        };
    }

    /// <summary>
    /// Keeps only the channels the bot holds ViewChannel on. Resolution runs off the bot's own
    /// Redis-cached permission set, so this is one cached read per channel rather than a query,
    /// and it only runs on gateway handshake - not on the dispatch hot path.
    /// </summary>
    private static async Task<List<ChannelSnapshot>> FilterVisibleChannelsAsync(
        GuildPermissionService permissionService,
        string botUserId,
        List<ChannelSnapshot> channels)
    {
        if (channels.Count == 0 || string.IsNullOrWhiteSpace(botUserId)) return channels;

        var visible = new List<ChannelSnapshot>(channels.Count);
        foreach (var channel in channels)
        {
            if (await permissionService.CanUserPerformActionAsync(botUserId, channel.Id, Permissions.ViewChannel))
                visible.Add(channel);
        }

        return visible;
    }
}
