using Bots.Contracts.Gateway.Payloads;
using Bots.Domain.Entity;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Bots.Application.Gateway;

/// <summary>
/// Builds the READY + GUILD_CREATE burst a bot receives right after a successful IDENTIFY.
/// </summary>
public class GatewayHandshakeService(MicroserviceContext ctx, IMessageBus bus, ILogger<GatewayHandshakeService> logger)
{
    public async Task<bool> SendReadyAndGuildsAsync(GatewayConnection connection, GatewaySession session, CancellationToken token)
    {
        var botApp = await ctx.BotApplications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BotUserId == session.BotUserId, token);
        if (botApp is null) return false;

        var guildIds = await ctx.BotInstallations.AsNoTracking()
            .Where(i => i.BotApplicationId == botApp.Id)
            .Select(i => i.GuildId)
            .ToListAsync(token);

        var ready = new ReadyPayload
        {
            V = 10,
            User = new DiscordUserPayload
            {
                Id = botApp.BotUserId,
                Username = botApp.Name,
                Discriminator = "0000",
                Avatar = botApp.IconUrl,
                Bot = true,
            },
            Guilds = guildIds.Select(id => new UnavailableGuildPayload { Id = id, Unavailable = true }).ToList(),
            SessionId = session.SessionId,
            ResumeGatewayUrl = "",
            Application = new ApplicationPayload { Id = botApp.BotUserId, Flags = 0 },
        };

        await connection.SendDispatchAsync("READY", ready, token);

        var snapshots = await Task.WhenAll(guildIds.Select(id => FetchGuildCreatePayloadAsync(id, session.BotUserId)));
        foreach (var payload in snapshots)
        {
            if (payload is not null) await connection.SendDispatchAsync("GUILD_CREATE", payload, token);
        }

        return true;
    }

    private async Task<GuildCreatePayload?> FetchGuildCreatePayloadAsync(string guildId, string botUserId)
    {
        try
        {
            var response = await bus.InvokeAsync<GetGuildSnapshotForBotResponse>(
                new GetGuildSnapshotForBotRequest { GuildId = guildId, BotUserId = botUserId });

            var guild = response.Guild;
            if (guild is null) return null;

            return new GuildCreatePayload
            {
                Id = guild.Id,
                Name = guild.Name,
                Icon = null, // Guild.Domain.Aggregates.Guild has no icon field today
                OwnerId = guild.OwnerId,
                Unavailable = false,
                MemberCount = guild.Self is null ? 0 : 1,
                Roles = guild.Roles.Select(GatewayRoleMapper.ToPayload).ToList(),
                Channels = guild.Channels.Select(c => new GatewayChannelPayload
                {
                    Id = c.Id,
                    Type = DiscordChannelType.FromEnumName(c.Type),
                    GuildId = guild.Id,
                    Name = c.Name,
                    Position = c.Position,
                    ParentId = c.CategoryId,
                }).ToList(),
                Members = guild.Self is null
                    ? new List<GatewayMemberPayload>()
                    :
                    [
                        new GatewayMemberPayload
                        {
                            User = new DiscordUserPayload { Id = guild.Self.UserId, Username = botUserId, Discriminator = "0000", Bot = true },
                            Nick = guild.Self.Nickname,
                            Roles = guild.Self.RoleIds,
                            JoinedAt = guild.Self.JoinedAt,
                        },
                    ],
            };
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to fetch guild snapshot for bot {BotUserId} in guild {GuildId}", botUserId, guildId);
            return null;
        }
    }
}
