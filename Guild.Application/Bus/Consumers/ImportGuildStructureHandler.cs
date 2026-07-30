using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using GuildAggregate = Guild.Domain.Aggregates.Guild;

namespace Guild.Application.Bus.Consumers;

public class ImportGuildStructureHandler
{
    public static async Task<ImportGuildStructureResponse> Handle(
        ImportGuildStructureCommand command,
        MicroserviceContext ctx,
        AuditLogService auditLog,
        IMessageBus bus,
        IHubContext<EchoRealtimeHub> hub,
        ILogger<ImportGuildStructureHandler> logger)
    {
        try
        {
            return await BuildGuildAsync(command, ctx, auditLog, bus, hub);
        }
        catch (Exception ex)
        {
            // A thrown exception here would otherwise dead-letter the whole Wolverine message (see
            // the whitespace-in-channel-name incident this was added for) - the caller
            // (Import.Application's StartDiscordStructureImportHandler) expects a clean response it
            // can turn into a normal "Failed" job status, not a bus-level retry/timeout cycle.
            logger.LogError(ex, "Failed to build imported guild structure for owner {OwnerId}", command.OwnerId);
            ctx.ChangeTracker.Clear();
            return new ImportGuildStructureResponse { ErrorMessage = ex.Message };
        }
    }

    private static async Task<ImportGuildStructureResponse> BuildGuildAsync(
        ImportGuildStructureCommand command,
        MicroserviceContext ctx,
        AuditLogService auditLog,
        IMessageBus bus,
        IHubContext<EchoRealtimeHub> hub)
    {
        var profileResponse = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest
        {
            UserId = command.OwnerId,
        });

        var searchValue = profileResponse.Profile is null
            ? command.OwnerId.ToUpperInvariant()
            : $"{profileResponse.Profile.UserName}#{profileResponse.Profile.Hash}".ToUpperInvariant();

        var guild = GuildAggregate.Create(new Domain.Aggregates.CreateGuildParams
        {
            Name = command.Name,
            Description = command.Description,
            OwnerId = command.OwnerId,
            OwnerSearchValue = searchValue,
            OwnerNickname = profileResponse.Profile?.UserName,
            SkipDefaultChannels = true,
            // Explicit rather than relying on the default: anything coming out of Discord is a
            // community server by definition, and its imported tree assumes every community
            // module (forums, announcements, automod, bots) is present.
            Kind = GuildKind.Community,
        });

        ctx.Guilds.Add(guild);

        var response = new ImportGuildStructureResponse { GuildId = guild.Id };

        // Roles first (except @everyone, already created by Guild.Create) - channel overwrites
        // below need the Discord-role-id -> Echo-role-id mapping to already exist.
        var roleIdMap = new Dictionary<string, string>();
        var everyoneRole = guild.Roles.First(r => r.Type == RoleType.Everyone);

        foreach (var roleDto in command.Roles.OrderBy(r => r.Position))
        {
            if (roleDto.IsEveryoneRole)
            {
                everyoneRole.Permissions = (Permissions)roleDto.Permissions;
                everyoneRole.Color = roleDto.Color;
                roleIdMap[roleDto.DiscordId] = everyoneRole.Id;
                response.DiscordToEchoRoleIds[roleDto.DiscordId] = everyoneRole.Id;
                continue;
            }

            var role = Domain.Aggregates.Role.Create(new Domain.Aggregates.CreateRoleParams
            {
                Name = roleDto.Name,
                Color = roleDto.Color,
                GuildId = guild.Id,
                Type = RoleType.None,
                Permissions = (Permissions)roleDto.Permissions,
            });
            role.Position = roleDto.Position;

            ctx.Roles.Add(role);
            roleIdMap[roleDto.DiscordId] = role.Id;
            response.DiscordToEchoRoleIds[roleDto.DiscordId] = role.Id;
        }

        var categoriesByPosition = command.Categories.OrderBy(c => c.Position).ToList();
        for (var categoryIndex = 0; categoryIndex < categoriesByPosition.Count; categoryIndex++)
        {
            var categoryDto = categoriesByPosition[categoryIndex];
            var category = Category.Create(new CreateCategoryParams
            {
                Name = categoryDto.Name,
                GuildId = guild.Id,
                Position = categoryIndex,
            });
            ctx.Categories.Add(category);
            response.DiscordToEchoCategoryIds[categoryDto.DiscordId] = category.Id;

            var channelsByPosition = categoryDto.Channels.OrderBy(c => c.Position).ToList();
            for (var channelIndex = 0; channelIndex < channelsByPosition.Count; channelIndex++)
            {
                var channelDto = channelsByPosition[channelIndex];
                if (!Enum.TryParse<ChannelType>(channelDto.Type, out var channelType))
                {
                    channelType = ChannelType.Text;
                }

                var channel = Domain.Aggregates.Channel.Create(new Domain.Aggregates.CreateChannelParams
                {
                    Name = channelDto.Name,
                    Description = "",
                    Type = channelType,
                    CategoryId = category.Id,
                    GuildId = guild.Id,
                    Position = channelIndex,
                });
                channel.IsAgeRestricted = channelDto.IsAgeRestricted;
                channel.SlowModeSeconds = channelDto.SlowModeSeconds;

                ctx.Channels.Add(channel);
                response.DiscordToEchoChannelIds[channelDto.DiscordId] = channel.Id;

                // Only role-targeted overwrites make it this far - Import.Application already
                // drops Discord's member-targeted (type 1) overwrites before building this DTO,
                // since no member exists yet to attach them to.
                foreach (var overwriteDto in channelDto.Overwrites)
                {
                    if (!roleIdMap.TryGetValue(overwriteDto.DiscordRoleId, out var echoRoleId)) continue;

                    ctx.Set<ChannelPermission>().Add(new ChannelPermission
                    {
                        Id = ChannelPermission.GenerateId(),
                        ChannelId = channel.Id,
                        RoleId = echoRoleId,
                        AllowPermissions = (Permissions)overwriteDto.AllowPermissions,
                        DenyPermissions = (Permissions)overwriteDto.DenyPermissions,
                    });
                }
            }
        }

        // SystemChannelId is deliberately left null here - Guild.Create already left it null when
        // SkipDefaultChannels is set (see Guild.cs), and setting it to a channel created in this
        // same unit of work would hit the same Guild<->Channel circular-FK issue
        // GuildEndpoint.CreateGuild has to work around with an explicit two-phase save; simplest to
        // just leave it unset here and let the owner pick one via the existing
        // UpdateGuild(SystemChannelId) endpoint.
        auditLog.Log(guild.Id, command.OwnerId, AuditActionType.GuildImportedFromDiscord, null,
            new { CategoryCount = command.Categories.Count, RoleCount = command.Roles.Count });

        // The import completes asynchronously, long after the HTTP request that kicked it off
        // already returned/redirected - unlike GuildEndpoint.CreateGuild's synchronous response,
        // there's no request left to hand the new guild back on.
        await hub.Clients.User(command.OwnerId).SendAsync("guild.GuildCreated", guild.ToFacet<GuildAggregate, GuildDto>());

        return response;
    }
}
