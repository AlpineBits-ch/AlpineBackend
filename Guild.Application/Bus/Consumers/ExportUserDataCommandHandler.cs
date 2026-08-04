using System.Text.Json;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Guild's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
///
/// <para><b>The subject's membership of a guild is theirs; the guild is not.</b> What comes out here
/// is the rows keyed to this account - memberships with their nicknames and join dates, the roles
/// those memberships hold, per-guild notification settings, per-guild DM preferences, read positions,
/// and the bans placed against them. Guild names and ids are included because a list of opaque guild
/// ids is not an intelligible answer to "which servers am I in", and a guild's name is not personal
/// data about anybody. What is not included is any other member of those guilds: no member list, no
/// other nicknames, no moderator identities beyond the opaque id already on the row.</para>
///
/// <para><b>Bans are included deliberately.</b> A ban is a decision recorded about the subject and
/// they are entitled to see it, including the stated reason - a moderation record a subject cannot
/// obtain is exactly the kind of thing Art. 15 exists for. The moderator appears as a user id, never
/// resolved to a name.</para>
///
/// <para>Guild-owned content the subject merely touched - audit log entries naming them as an actor,
/// wiki revisions they authored - is deliberately out of scope for the same reason the purge leaves
/// it alone: it is the guild's record, jointly about everyone in it, and the subject's copy of it is
/// not separable from everyone else's.</para>
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
                // The moderator as an opaque id. Never resolved to a name here - that would be
                // somebody else's personal data riding along in the subject's archive.
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
