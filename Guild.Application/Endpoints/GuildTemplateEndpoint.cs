using System.Security.Claims;
using System.Text;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Http;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Guild.Application.Endpoints;

[Authorize]
public class GuildTemplateEndpoint
{
    /// <summary>Snapshots a guild's current category/channel/role structure into a reusable
    /// template. Not a Wolverine "Handle" method dispatched over the bus, so - like GuildEndpoint's
    /// CreateGuild - this commits manually.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/templates")]
    public async Task<IResult> CreateFromGuild(string guildId, CreateGuildTemplateFromGuildDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var guild = await ctx.Guilds
            .Include(g => g.Categories).ThenInclude(c => c.Channels)
            .Include(g => g.Channels)
            .Include(g => g.Roles)
            .FirstOrDefaultAsync(g => g.Id == guildId);
        if (guild is null) return Results.NotFound();

        var snapshot = new TemplateSnapshot
        {
            Roles = guild.Roles
                .Where(r => r.Type != RoleType.Everyone)
                .Select(r => new TemplateRole { Name = r.Name, Color = r.Color, Position = r.Position, Permissions = r.Permissions })
                .ToList(),
            Categories = guild.Categories
                .OrderBy(c => c.Position)
                .Select(c => new TemplateCategory
                {
                    Name = c.Name,
                    Position = c.Position,
                    Channels = c.Channels
                        .Where(ch => ch.Type is ChannelType.Text or ChannelType.Voice or ChannelType.Forum or ChannelType.Announcement)
                        .OrderBy(ch => ch.Position)
                        .Select(ch => new TemplateChannel { Name = ch.Name, Type = ch.Type, Description = ch.Description, Position = ch.Position })
                        .ToList(),
                })
                .ToList(),
            UncategorizedChannels = guild.Channels
                .Where(ch => ch.CategoryId is null && ch.Type is ChannelType.Text or ChannelType.Voice or ChannelType.Forum or ChannelType.Announcement)
                .OrderBy(ch => ch.Position)
                .Select(ch => new TemplateChannel { Name = ch.Name, Type = ch.Type, Description = ch.Description, Position = ch.Position })
                .ToList(),
        };

        var everyoneRole = guild.Roles.FirstOrDefault(r => r.Type == RoleType.Everyone);
        if (everyoneRole is not null)
        {
            snapshot.Roles.Insert(0, new TemplateRole { Name = "Everyone", Position = 0, Permissions = everyoneRole.Permissions });
        }

        snapshot.Onboarding = await CaptureOnboardingAsync(ctx, guild);

        var template = GuildTemplate.Create(new CreateGuildTemplateParams
        {
            Name = dto.Name,
            Description = dto.Description,
            CreatorUserId = userId,
            SourceGuildId = guildId,
            Snapshot = snapshot,
        });

        ctx.GuildTemplates.Add(template);
        auditLog.Log(guildId, userId, AuditActionType.TemplateCreated, template.Id, new { template.Name });
        await ctx.SaveChangesAsync();

        return Results.Ok(new { template.Id, template.Name, template.Description, template.CreatedAt });
    }

    [WolverineGet("/api/v1/templates/{templateId}")]
    public async Task<IResult> GetTemplate(string templateId, [NotBody] MicroserviceContext ctx)
    {
        var template = await ctx.GuildTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId);
        if (template is null) return Results.NotFound();

        return Results.Ok(new
        {
            template.Id,
            template.Name,
            template.Description,
            template.CreatorUserId,
            template.CreatedAt,
            template.UsageCount,
            template.Snapshot,
        });
    }

    /// <summary>Creates a brand new guild from a template - mirrors GuildEndpoint.CreateGuild's
    /// owner/system-channel setup, then replays the snapshot on top instead of the usual
    /// two-category default.</summary>
    [WolverinePost("/api/v1/templates/{templateId}/use")]
    public async Task<IResult> CreateGuildFromTemplate(string templateId, CreateGuildFromTemplateDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus,
        [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var template = await ctx.GuildTemplates.FirstOrDefaultAsync(t => t.Id == templateId);
        if (template is null) return Results.NotFound();

        var profileResponse = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = userId });
        if (profileResponse.Profile is null) return Results.BadRequest("User not found");

        var searchValue = (profileResponse.Profile.UserName! + "#" + profileResponse.Profile.Hash).ToUpperInvariant();

        var guild = Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = userId,
            OwnerSearchValue = searchValue,
            OwnerNickname = profileResponse.Profile.UserName,
            SkipDefaultChannels = true,
        });

        ctx.Guilds.Add(guild);

        var everyoneRole = guild.Roles.First(r => r.Type == RoleType.Everyone);
        var everyoneTemplate = template.Snapshot.Roles.FirstOrDefault(r => r.Position == 0 && r.Name == "Everyone");
        if (everyoneTemplate is not null) everyoneRole.Permissions = everyoneTemplate.Permissions;

        // Onboarding in a template references roles and channels by name (ids don't survive into a
        // new guild), so the replay needs a name -> freshly-generated-id map.
        var roleIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var channelIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var roleTemplate in template.Snapshot.Roles.Where(r => r != everyoneTemplate))
        {
            var role = Role.Create(new CreateRoleParams
            {
                Name = roleTemplate.Name,
                Color = roleTemplate.Color,
                GuildId = guild.Id,
                Permissions = roleTemplate.Permissions,
            });
            ctx.Roles.Add(role);
            roleIdsByName.TryAdd(role.Name, role.Id);
        }

        string? firstTextChannelId = null;
        var position = 0;
        foreach (var categoryTemplate in template.Snapshot.Categories)
        {
            var category = Category.Create(new CreateCategoryParams { Name = categoryTemplate.Name, GuildId = guild.Id, Position = position++ });
            ctx.Categories.Add(category);

            foreach (var channelTemplate in categoryTemplate.Channels)
            {
                var channel = Channel.Create(new CreateChannelParams
                {
                    Name = channelTemplate.Name,
                    Description = channelTemplate.Description ?? "",
                    Type = channelTemplate.Type,
                    CategoryId = category.Id,
                    GuildId = guild.Id,
                    Position = channelTemplate.Position,
                });
                ctx.Channels.Add(channel);
                channelIdsByName.TryAdd(channel.Name, channel.Id);
                firstTextChannelId ??= channelTemplate.Type == ChannelType.Text ? channel.Id : null;
            }
        }

        foreach (var channelTemplate in template.Snapshot.UncategorizedChannels)
        {
            var channel = Channel.Create(new CreateChannelParams
            {
                Name = channelTemplate.Name,
                Description = channelTemplate.Description ?? "",
                Type = channelTemplate.Type,
                GuildId = guild.Id,
                Position = channelTemplate.Position,
            });
            ctx.Channels.Add(channel);
            channelIdsByName.TryAdd(channel.Name, channel.Id);
            firstTextChannelId ??= channelTemplate.Type == ChannelType.Text ? channel.Id : null;
        }

        guild.SystemChannelId = firstTextChannelId;
        template.UsageCount++;

        ReplayOnboarding(ctx, guild.Id, template.Snapshot.Onboarding, roleIdsByName, channelIdsByName);

        // Same FK ordering hack as GuildEndpoint.CreateGuild - SystemChannelId points at a row
        // being inserted in the same batch, so it has to go in after the initial save.
        var sysChannelId = guild.SystemChannelId;
        guild.SystemChannelId = null;
        await ctx.SaveChangesAsync();
        guild.SystemChannelId = sysChannelId;
        auditLog.Log(guild.Id, userId, AuditActionType.GuildCreatedFromTemplate, template.Id);
        await ctx.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(guild.SystemChannelId))
        {
            await bus.InvokeAsync(new CreateMessageCommand
            {
                Content = Encoding.UTF8.GetBytes($"{profileResponse.Profile.UserName} joined the server"),
                ChannelId = guild.SystemChannelId,
                AuthorId = guild.OwnerId,
                AuthorIdType = AuthorIdType.User,
                Mentions = [],
                Type = MessagingMessageType.GuildMemberJoin,
            });
        }

        return Results.Ok(new { guild.Id, guild.Name });
    }

    /// <summary>Captures the guild's onboarding into the snapshot, referencing roles and channels by
    /// name so it can be replayed into a guild whose ids don't exist yet.</summary>
    private static async Task<TemplateOnboarding?> CaptureOnboardingAsync(MicroserviceContext ctx,
        Domain.Aggregates.Guild guild)
    {
        var config = await ctx.Set<GuildOnboardingConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.GuildId == guild.Id);

        var prompts = await ctx.Set<GuildOnboardingPrompt>().AsNoTracking()
            .Include(p => p.Options)
            .Where(p => p.GuildId == guild.Id)
            .OrderBy(p => p.Position)
            .ToListAsync();

        if (config is null && prompts.Count == 0) return null;

        var roleNamesById = guild.Roles.ToDictionary(r => r.Id, r => r.Name);
        var channelNamesById = guild.Channels
            .Concat(guild.Categories.SelectMany(c => c.Channels))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);

        List<string> ResolveChannelNames(IEnumerable<string> ids) =>
            ids.Where(channelNamesById.ContainsKey).Select(id => channelNamesById[id]).ToList();

        return new TemplateOnboarding
        {
            Enabled = config?.Enabled ?? false,
            RulesText = config?.RulesText,
            Mode = config?.Mode ?? OnboardingMode.Default,
            DefaultChannelNames = ResolveChannelNames(config?.DefaultChannelIds ?? []),
            Prompts = prompts.Select(p => new TemplateOnboardingPrompt
            {
                Title = p.Title,
                Type = p.Type,
                SingleSelect = p.SingleSelect,
                Required = p.Required,
                InOnboarding = p.InOnboarding,
                Position = p.Position,
                Options = p.Options.OrderBy(o => o.Position).Select(o => new TemplateOnboardingOption
                {
                    Title = o.Title,
                    Description = o.Description,
                    Emoji = o.Emoji,
                    RoleNames = o.RoleIds.Where(roleNamesById.ContainsKey).Select(id => roleNamesById[id]).ToList(),
                    ChannelNames = ResolveChannelNames(o.ChannelIds),
                    Position = o.Position,
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Replays a captured onboarding config into the new guild.</summary>
    private static void ReplayOnboarding(MicroserviceContext ctx, string guildId, TemplateOnboarding? onboarding,
        Dictionary<string, string> roleIdsByName, Dictionary<string, string> channelIdsByName)
    {
        if (onboarding is null) return;

        List<string> ResolveChannels(IEnumerable<string> names) =>
            names.Where(channelIdsByName.ContainsKey).Select(n => channelIdsByName[n]).ToList();

        var now = DateTimeOffset.UtcNow;

        ctx.Set<GuildOnboardingConfig>().Add(new GuildOnboardingConfig
        {
            GuildId = guildId,
            Enabled = onboarding.Enabled,
            RulesText = onboarding.RulesText,
            Mode = onboarding.Mode,
            DefaultChannelIds = ResolveChannels(onboarding.DefaultChannelNames),
            UpdatedAt = now,
        });

        var position = 0;
        foreach (var promptTemplate in onboarding.Prompts.OrderBy(p => p.Position))
        {
            var options = promptTemplate.Options.OrderBy(o => o.Position).Select(o => new
            {
                Template = o,
                RoleIds = o.RoleNames.Where(roleIdsByName.ContainsKey).Select(n => roleIdsByName[n]).ToList(),
                ChannelIds = ResolveChannels(o.ChannelNames),
            })
            .Where(o => o.RoleIds.Count + o.ChannelIds.Count > 0)
            .ToList();

            if (options.Count == 0) continue;

            var prompt = new GuildOnboardingPrompt
            {
                Id = GuildOnboardingPrompt.GenerateId(),
                CreatedAt = now,
                UpdatedAt = now,
                GuildId = guildId,
                Title = promptTemplate.Title,
                Type = promptTemplate.Type,
                SingleSelect = promptTemplate.SingleSelect,
                Required = promptTemplate.Required,
                InOnboarding = promptTemplate.InOnboarding,
                Position = position++,
            };
            ctx.Set<GuildOnboardingPrompt>().Add(prompt);

            var optionPosition = 0;
            foreach (var option in options)
            {
                ctx.Set<GuildOnboardingPromptOption>().Add(new GuildOnboardingPromptOption
                {
                    Id = GuildOnboardingPromptOption.GenerateId(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    PromptId = prompt.Id,
                    Title = option.Template.Title,
                    Description = option.Template.Description,
                    Emoji = option.Template.Emoji,
                    RoleIds = option.RoleIds,
                    ChannelIds = option.ChannelIds,
                    Position = optionPosition++,
                });
            }
        }
    }
}
