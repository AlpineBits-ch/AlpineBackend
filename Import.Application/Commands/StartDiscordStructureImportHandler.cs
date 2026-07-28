using Guild.Contracts.Bus.Commands;
using Import.Application.Discord;
using Import.Application.Mapping;
using Import.Domain.Entity;
using Import.Domain.Enums;
using Import.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Import.Application.Commands;

public class StartDiscordStructureImportHandler
{
    public static async Task Handle(
        StartDiscordStructureImportCommand command,
        MicroserviceContext ctx,
        DiscordApiClient discordApi,
        IMessageBus bus,
        ILogger<StartDiscordStructureImportHandler> logger,
        CancellationToken ct)
    {
        var job = await ctx.ImportJobs.FirstAsync(j => j.Id == command.ImportJobId, ct);

        try
        {
            job.Status = ImportJobStatus.FetchingFromDiscord;

            var discordGuild = await discordApi.GetGuildAsync(command.DiscordGuildId, ct);
            if (discordGuild is null)
            {
                job.Status = ImportJobStatus.Failed;
                job.ErrorMessage = "Bot is not a member of the target Discord guild (or it was removed).";
                return;
            }

            var discordRoles = await discordApi.GetGuildRolesAsync(command.DiscordGuildId, ct);
            var discordChannels = await discordApi.GetGuildChannelsAsync(command.DiscordGuildId, ct);

            job.Status = ImportJobStatus.CreatingGuild;

            var importCommand = BuildImportCommand(command.RequestedByUserId, discordGuild, discordRoles, discordChannels);
            var response = await bus.InvokeAsync<ImportGuildStructureResponse>(importCommand, ct);

            job.EchoGuildId = response.GuildId;
            job.Status = ImportJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;

            var link = new GuildLink
            {
                Id = GuildLink.GenerateId(),
                EchoGuildId = response.GuildId,
                DiscordGuildId = command.DiscordGuildId,
                SyncDirection = SyncDirection.DiscordToVenta,
                Status = GuildLinkStatus.Active,
                LastSyncedAt = DateTimeOffset.UtcNow,
            };
            ctx.GuildLinks.Add(link);

            foreach (var (discordId, echoId) in response.DiscordToEchoCategoryIds)
            {
                ctx.ImportEntityMappings.Add(NewMapping(link.Id, discordId, ImportEntityType.Category, echoId));
            }
            foreach (var (discordId, echoId) in response.DiscordToEchoChannelIds)
            {
                ctx.ImportEntityMappings.Add(NewMapping(link.Id, discordId, ImportEntityType.Channel, echoId));
            }
            foreach (var (discordId, echoId) in response.DiscordToEchoRoleIds)
            {
                ctx.ImportEntityMappings.Add(NewMapping(link.Id, discordId, ImportEntityType.Role, echoId));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord structure import failed for job {ImportJobId} (guild {DiscordGuildId})",
                job.Id, command.DiscordGuildId);
            job.Status = ImportJobStatus.Failed;
            job.ErrorMessage = ex.Message;
        }
    }

    private static ImportEntityMapping NewMapping(string guildLinkId, string discordId, ImportEntityType type, string echoId) =>
        new()
        {
            Id = ImportEntityMapping.GenerateId(),
            GuildLinkId = guildLinkId,
            DiscordId = discordId,
            EntityType = type,
            EchoId = echoId,
        };

    private static ImportGuildStructureCommand BuildImportCommand(
        string ownerId,
        DiscordGuildPayload guild,
        List<DiscordRolePayload> roles,
        List<DiscordChannelPayload> channels)
    {
        var roleDtos = roles.Select(r => new ImportedRoleDto
        {
            DiscordId = r.Id,
            Name = r.Name,
            Color = $"#{r.Color:X6}",
            Position = r.Position,
            Permissions = DiscordPermissionMapper.ToEchoPermissions(r.Permissions),
            IsEveryoneRole = r.Id == guild.Id,
        }).ToList();

        var categoryChannels = channels
            .Where(c => DiscordChannelTypeMapper.IsCategory(c.Type))
            .OrderBy(c => c.Position)
            .ToList();

        var nonThreadChannels = channels
            .Where(c => !DiscordChannelTypeMapper.IsCategory(c.Type) && !DiscordChannelTypeMapper.IsThread(c.Type))
            .ToList();

        var categoryDtos = categoryChannels.Select(cat => new ImportedCategoryDto
        {
            DiscordId = cat.Id,
            Name = cat.Name ?? "Category",
            Position = cat.Position,
            Channels = nonThreadChannels
                .Where(c => c.ParentId == cat.Id)
                .OrderBy(c => c.Position)
                .Select(ToChannelDto)
                .ToList(),
        }).ToList();

        var uncategorized = nonThreadChannels
            .Where(c => c.ParentId is null || categoryChannels.All(cat => cat.Id != c.ParentId))
            .OrderBy(c => c.Position)
            .ToList();

        if (uncategorized.Count > 0)
        {
            categoryDtos.Add(new ImportedCategoryDto
            {
                DiscordId = $"uncategorized:{guild.Id}",
                Name = "Channels",
                Position = categoryDtos.Count,
                Channels = uncategorized.Select(ToChannelDto).ToList(),
            });
        }

        return new ImportGuildStructureCommand
        {
            OwnerId = ownerId,
            Name = guild.Name,
            Description = guild.Description,
            Categories = categoryDtos,
            Roles = roleDtos,
        };
    }

    private static ImportedChannelDto ToChannelDto(DiscordChannelPayload channel) => new()
    {
        DiscordId = channel.Id,
        Name = channel.Name ?? "channel",
        Type = DiscordChannelTypeMapper.ToEchoChannelType(channel.Type),
        Position = channel.Position,
        IsAgeRestricted = channel.Nsfw,
        SlowModeSeconds = channel.RateLimitPerUser ?? 0,
        // Member-targeted (type 1) overwrites are dropped here - no Echo member exists yet to
        // attach them to; only role-targeted (type 0) overwrites make it into the bulk command.
        Overwrites = channel.PermissionOverwrites
            .Where(o => o.Type == 0)
            .Select(o => new ImportedOverwriteDto
            {
                DiscordRoleId = o.Id,
                AllowPermissions = DiscordPermissionMapper.ToEchoPermissions(o.Allow),
                DenyPermissions = DiscordPermissionMapper.ToEchoPermissions(o.Deny),
            })
            .ToList(),
    };
}
