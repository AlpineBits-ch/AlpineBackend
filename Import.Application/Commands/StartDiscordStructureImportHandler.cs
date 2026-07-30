using System.Text.RegularExpressions;
using Guild.Contracts.Bus.Commands;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
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
            var existingLink = await ctx.GuildLinks
                .FirstOrDefaultAsync(l => l.DiscordGuildId == command.DiscordGuildId, ct);
            if (existingLink is not null)
            {
                job.Status = ImportJobStatus.Failed;
                job.ErrorMessage = "This Discord server is already linked to an Echo guild - unlink it first if you want to re-import.";
                job.EchoGuildId = existingLink.EchoGuildId;
                return;
            }

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

            if (response.ErrorMessage is not null)
            {
                job.Status = ImportJobStatus.Failed;
                job.ErrorMessage = response.ErrorMessage;
                return;
            }

            job.EchoGuildId = response.GuildId;
            job.Status = ImportJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;

            var link = new GuildLink
            {
                Id = GuildLink.GenerateId(),
                // Non-null guaranteed here - we already returned above when ErrorMessage was set.
                EchoGuildId = response.GuildId!,
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

            // Deliberately swallowed: the guild, its channels, roles and link are already imported
            // and the job is Completed above. Failing the whole import because the source guild's
            // onboarding couldn't be read or applied would be a far worse outcome than a guild that
            // simply arrives without it.
            try
            {
                await ImportOnboardingAsync(command, response, discordApi, bus, logger, ct);
            }
            catch (Exception onboardingEx)
            {
                logger.LogWarning(onboardingEx,
                    "Imported guild {EchoGuildId} kept its structure but its onboarding could not be imported",
                    response.GuildId);
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

    /// <summary>
    /// Pulls the Discord guild's onboarding and welcome screen and applies them to the new Echo
    /// guild, remapping every role/channel reference onto the ids the structure import just
    /// created. Best-effort by design: a guild without onboarding, a bot without MANAGE_GUILD, or a
    /// config our validation rejects all leave the imported guild intact and un-onboarded rather
    /// than failing an import that otherwise succeeded.
    /// </summary>
    private static async Task ImportOnboardingAsync(
        StartDiscordStructureImportCommand command,
        ImportGuildStructureResponse response,
        DiscordApiClient discordApi,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        var onboarding = await discordApi.GetGuildOnboardingAsync(command.DiscordGuildId, ct);
        var welcomeScreen = await discordApi.GetGuildWelcomeScreenAsync(command.DiscordGuildId, ct);

        if (onboarding is null && welcomeScreen is null) return;

        var channels = response.DiscordToEchoChannelIds;
        var roles = response.DiscordToEchoRoleIds;

        List<string> MapChannels(IEnumerable<string> discordIds) =>
            discordIds.Where(channels.ContainsKey).Select(id => channels[id]).ToList();

        var request = new ImportGuildOnboardingRequest
        {
            GuildId = response.GuildId!,
            ActorUserId = command.RequestedByUserId,
            Onboarding = onboarding is null ? null : new GuildOnboardingContract
            {
                Enabled = onboarding.Enabled,
                Mode = onboarding.Mode,
                DefaultChannelIds = MapChannels(onboarding.DefaultChannelIds),
                Prompts = onboarding.Prompts.Select((p, index) => new GuildOnboardingPromptContract
                {
                    Title = p.Title,
                    Type = p.Type,
                    SingleSelect = p.SingleSelect,
                    Required = p.Required,
                    InOnboarding = p.InOnboarding,
                    Position = index,
                    Options = p.Options.Select((o, optionIndex) => new GuildOnboardingOptionContract
                    {
                        Title = o.Title,
                        Description = o.Description,
                        Emoji = o.Emoji?.Name,
                        RoleIds = o.RoleIds.Where(roles.ContainsKey).Select(id => roles[id]).ToList(),
                        ChannelIds = MapChannels(o.ChannelIds),
                        Position = optionIndex,
                    }).ToList(),
                }).ToList(),
            },
            WelcomeScreen = welcomeScreen is null ? null : new ImportedWelcomeScreen
            {
                Description = welcomeScreen.Description,
                Channels = welcomeScreen.WelcomeChannels
                    .Where(c => channels.ContainsKey(c.ChannelId))
                    .Select(c => new ImportedWelcomeChannel
                    {
                        ChannelId = channels[c.ChannelId],
                        Description = c.Description,
                        Emoji = c.EmojiName,
                    })
                    .ToList(),
            },
        };

        var result = await bus.InvokeAsync<ImportGuildOnboardingResponse>(request, ct);

        if (result.ErrorMessage is not null)
        {
            logger.LogWarning(
                "Imported guild {EchoGuildId} kept its structure but not its onboarding: {Error}",
                response.GuildId, result.ErrorMessage);
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
        Name = SanitizeChannelName(channel.Name),
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

    /// <summary>Guild.Domain's ChannelValidator rejects any whitespace in a channel name (Echo's
    /// own convention, matching how Discord's client auto-kebab-cases text channel names) - but
    /// Discord doesn't enforce that server-side for every channel type (voice/forum/category
    /// names can freely contain spaces), so a real import can hand us a name that would otherwise
    /// fail Guild-side validation. Collapse any whitespace run to a single hyphen.</summary>
    private static string SanitizeChannelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "channel";
        var sanitized = Regex.Replace(name.Trim(), @"\s+", "-");
        return sanitized.Length == 0 ? "channel" : sanitized;
    }
}
