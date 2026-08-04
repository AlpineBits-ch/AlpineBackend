using System.Text.Json;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Guild's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command, MicroserviceContext ctx)
    {
        var memberships = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == command.UserId)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync();

        var memberIds = memberships.Select(m => m.Id).ToList();
        var guildIds = memberships.Select(m => m.GuildId).Distinct().ToList();

        var guildNames = await ctx.Guilds
            .AsNoTracking()
            .Where(g => guildIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name })
            .ToListAsync();

        var nameByGuild = guildNames.ToDictionary(g => g.Id, g => g.Name);

        var roleMemberships = await ctx.RoleMembers
            .AsNoTracking()
            .Where(r => memberIds.Contains(r.MemberId))
            .ToListAsync();

        var notificationSettings = await ctx.GuildNotificationSettings
            .AsNoTracking()
            .Where(n => memberIds.Contains(n.MemberId))
            .ToListAsync();

        var readStates = await ctx.ReadStates
            .AsNoTracking()
            .Where(r => memberIds.Contains(r.MemberId))
            .ToListAsync();

        var dmPreferences = await ctx.GuildDirectMessagePreferences
            .AsNoTracking()
            .Where(p => p.UserId == command.UserId)
            .ToListAsync();

        var bans = await ctx.GuildBans
            .AsNoTracking()
            .Where(b => b.BannedUserId == command.UserId)
            .ToListAsync();

        var fragment = new
        {
            memberships = memberships.Select(m => new
            {
                m.Id,
                m.GuildId,
                guildName = nameByGuild.GetValueOrDefault(m.GuildId),
                m.JoinedAt,
                m.Nickname,
                m.Bio,
                type = m.Type.ToString(),
                m.InviteCode,
                m.FederatedServerId,
            }),
            roles = roleMemberships.Select(r => new
            {
                r.Id,
                r.RoleId,
                r.MemberId,
                r.ExpiresAt,
                r.CreatedAt,
            }),
            notificationSettings = notificationSettings.Select(n => new
            {
                n.Id,
                n.MemberId,
                level = n.Level.ToString(),
                n.MutedUntil,
                n.SuppressEveryone,
                n.SuppressRoleMentions,
                n.MobilePush,
            }),
            directMessagePreferences = dmPreferences.Select(p => new
            {
                p.Id,
                p.GuildId,
                p.AllowDirectMessages,
                p.UpdatedAt,
            }),
            readStates = readStates.Select(r => new
            {
                r.Id,
                r.ChannelId,
                r.LastReadMessageId,
                r.LastReadAt,
                r.MessageCountAtRead,
            }),
            bans = bans.Select(b => new
            {
                b.Id,
                b.GuildId,
                guildName = nameByGuild.GetValueOrDefault(b.GuildId),
                b.Reason,
                // The moderator as an opaque id.
                b.BannedByUserId,
                b.CreatedAt,
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "guild",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["memberships"] = memberships.Count,
                ["roles"] = roleMemberships.Count,
                ["notificationSettings"] = notificationSettings.Count,
                ["directMessagePreferences"] = dmPreferences.Count,
                ["readStates"] = readStates.Count,
                ["bans"] = bans.Count,
            },
        };
    }
}
