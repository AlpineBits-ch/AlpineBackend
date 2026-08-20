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

        var overwrites = await CaptureOverwritesAsync(ctx, guild);

        var snapshot = new TemplateSnapshot
        {
            Kind = guild.Kind,
            Features = guild.Features,
            Roles = guild.Roles
                .Where(r => r.Type != RoleType.Everyone)
                .Select(ToTemplateRole)
                .ToList(),
            Categories = guild.Categories
                .OrderBy(c => c.Position)
                .Select(c => new TemplateCategory
                {
                    Name = c.Name,
                    Position = c.Position,
                    Overwrites = overwrites.ForCategory(c.Id),
                    Channels = c.Channels
                        .Where(ch => !ch.Type.IsThreadShaped() && ch.Type != ChannelType.Ticket)
                        .OrderBy(ch => ch.Position)
                        .Select(ch => new TemplateChannel
                        {
                            Name = ch.Name, Type = ch.Type, Description = ch.Description, Position = ch.Position,
                            Overwrites = overwrites.ForChannel(ch.Id),
                        })
                        .ToList(),
                })
                .ToList(),
            // Excludes the thread-shaped types (they need a parent, and a scene would carry turn
            // state a template has nowhere to put) and Ticket (no behaviour behind it yet).
            UncategorizedChannels = guild.Channels
                .Where(ch => ch.CategoryId is null && !ch.Type.IsThreadShaped() && ch.Type != ChannelType.Ticket)
                .OrderBy(ch => ch.Position)
                .Select(ch => new TemplateChannel
                {
                    Name = ch.Name, Type = ch.Type, Description = ch.Description, Position = ch.Position,
                    Overwrites = overwrites.ForChannel(ch.Id),
                })
                .ToList(),
        };

        var everyoneRole = guild.Roles.FirstOrDefault(r => r.Type == RoleType.Everyone);
        if (everyoneRole is not null)
        {
            var everyoneEntry = ToTemplateRole(everyoneRole);
            everyoneEntry.Position = 0;
            everyoneEntry.IsEveryone = true;
            snapshot.Roles.Insert(0, everyoneEntry);
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
            Kind = template.Snapshot.Kind,
            Features = template.Snapshot.Features,
        });

        ctx.Guilds.Add(guild);

        var everyoneRole = guild.Roles.First(r => r.Type == RoleType.Everyone);
        var everyoneTemplate = template.Snapshot.Roles.FirstOrDefault(r => r.IsEveryone)
                               // Snapshots captured before TemplateRole.IsEveryone existed identify
                               // the role only by the name and position the capture side writes.
                               ?? template.Snapshot.Roles.FirstOrDefault(r => r.Position == 0 && r.Name == "Everyone");
        if (everyoneTemplate is not null)
            everyoneRole.ApplyExternalEveryonePermissions(everyoneTemplate.Permissions, everyoneTemplate.ModulePermissions);

        // Onboarding and permission overwrites in a template reference roles and channels by name
        // (ids don't survive into a new guild), so the replay needs a name -> freshly-generated-id
        // map. @everyone goes in first and wins the slot: a template that also carries a custom role
        // literally named "Everyone" would otherwise decide by insertion order whether the overwrite
        // that makes a channel private lands on the role every member holds.
        var roleIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var channelIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        roleIdsByName[everyoneRole.Name] = everyoneRole.Id;

        // Guild.Create seeds more than @everyone for a Household (see its Roles assignment), and
        // the snapshot carries its own copy of the same role.
        var seededByName = guild.Roles
            .Where(r => r.Type != RoleType.Everyone)
            .ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase);
        foreach (var seeded in seededByName) roleIdsByName.TryAdd(seeded.Key, seeded.Value.Id);

        // Sorted and renumbered rather than replayed verbatim - see TemplateRole.Position for why
        // only the relative order can be trusted. @everyone keeps 0, which is what the rest of the
        // permission system assumes of it, and the seeded roles keep the ranks they were given.
        var rolePosition = guild.Roles.Max(r => r.Position) + 1;
        foreach (var roleTemplate in template.Snapshot.Roles
                     .Where(r => r != everyoneTemplate)
                     .OrderBy(r => r.Position)
                     .ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            if (seededByName.TryGetValue(roleTemplate.Name, out var existing))
            {
                ApplyTemplateToSeededRole(existing, roleTemplate);
                continue;
            }

            var role = Role.Create(new CreateRoleParams
            {
                Name = roleTemplate.Name,
                Description = roleTemplate.Description,
                Color = roleTemplate.Color,
                GuildId = guild.Id,
                Permissions = roleTemplate.Permissions,
                ModulePermissions = roleTemplate.ModulePermissions,
                Hoist = roleTemplate.Hoist,
                Mentionable = roleTemplate.Mentionable ?? true,
                UnicodeEmoji = roleTemplate.UnicodeEmoji,
            });
            role.Position = rolePosition++;
            ctx.Roles.Add(role);
            roleIdsByName.TryAdd(role.Name, role.Id);
        }

        string? firstTextChannelId = null;
        var position = 0;
        foreach (var categoryTemplate in template.Snapshot.Categories)
        {
            var category = Category.Create(new CreateCategoryParams { Name = categoryTemplate.Name, GuildId = guild.Id, Position = position++ });
            ctx.Categories.Add(category);
            ReplayOverwrites(ctx, categoryTemplate.Overwrites, roleIdsByName, categoryId: category.Id, channelId: null);

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
                ReplayOverwrites(ctx, channelTemplate.Overwrites, roleIdsByName, categoryId: null, channelId: channel.Id);
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
            ReplayOverwrites(ctx, channelTemplate.Overwrites, roleIdsByName, categoryId: null, channelId: channel.Id);
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

    /// <summary>
    /// Overlays a captured role onto the identically named role Guild.Create already seeded.
    /// </summary>
    private static void ApplyTemplateToSeededRole(Role role, TemplateRole roleTemplate)
    {
        role.Description = roleTemplate.Description ?? role.Description;
        role.Color = roleTemplate.Color;
        role.Permissions = roleTemplate.Permissions;
        role.ModulePermissions = roleTemplate.ModulePermissions;
        role.Hoist = roleTemplate.Hoist;
        role.Mentionable = roleTemplate.Mentionable ?? true;
        role.SetBadge(null, roleTemplate.UnicodeEmoji);
    }

    /// <summary>
    /// Recreates a captured channel's or category's overwrites against the new guild's roles.
    /// </summary>
    private static void ReplayOverwrites(MicroserviceContext ctx, List<TemplateOverwrite> overwrites,
        Dictionary<string, string> roleIdsByName, string? categoryId, string? channelId)
    {
        foreach (var overwrite in overwrites)
        {
            if (overwrite.RoleName is null || !roleIdsByName.TryGetValue(overwrite.RoleName, out var roleId)) continue;

            ctx.Set<ChannelPermission>().Add(new ChannelPermission
            {
                Id = ChannelPermission.GenerateId(),
                CategoryId = categoryId,
                ChannelId = channelId,
                RoleId = roleId,
                AllowPermissions = overwrite.Allow,
                DenyPermissions = overwrite.Deny,
                AllowModulePermissions = overwrite.AllowModule,
                DenyModulePermissions = overwrite.DenyModule,
            });
        }
    }

    /// <summary>The role fields a template carries.</summary>
    private static TemplateRole ToTemplateRole(Role role) => new()
    {
        Name = role.Name,
        Description = role.Description,
        Color = role.Color,
        Position = role.Position,
        Permissions = role.Permissions,
        ModulePermissions = role.ModulePermissions,
        Hoist = role.Hoist,
        Mentionable = role.Mentionable,
        UnicodeEmoji = role.UnicodeEmoji,
    };

    /// <summary>
    /// Every role-targeted overwrite on the guild's categories and channels, indexed by the entity
    /// it hangs off and already resolved to role names.
    /// </summary>
    private static async Task<CapturedOverwrites> CaptureOverwritesAsync(MicroserviceContext ctx,
        Domain.Aggregates.Guild guild)
    {
        var roleNamesById = guild.Roles.ToDictionary(r => r.Id, r => r.Name);
        var categoryIds = guild.Categories.Select(c => c.Id).ToList();
        var channelIds = guild.Channels
            .Concat(guild.Categories.SelectMany(c => c.Channels))
            .Select(ch => ch.Id)
            .Distinct()
            .ToList();

        var rows = await ctx.Set<ChannelPermission>().AsNoTracking()
            .Where(p => p.RoleId != null && p.MemberId == null &&
                        ((p.ChannelId != null && channelIds.Contains(p.ChannelId)) ||
                         (p.CategoryId != null && categoryIds.Contains(p.CategoryId))))
            .ToListAsync();

        var byCategory = new Dictionary<string, List<TemplateOverwrite>>();
        var byChannel = new Dictionary<string, List<TemplateOverwrite>>();

        foreach (var row in rows)
        {
            if (!roleNamesById.TryGetValue(row.RoleId!, out var roleName)) continue;

            var captured = new TemplateOverwrite
            {
                RoleName = roleName,
                Allow = row.AllowPermissions,
                Deny = row.DenyPermissions,
                AllowModule = row.AllowModulePermissions,
                DenyModule = row.DenyModulePermissions,
            };

            var target = row.ChannelId is not null ? byChannel : byCategory;
            var key = row.ChannelId ?? row.CategoryId;
            if (key is null) continue;

            if (!target.TryGetValue(key, out var list)) target[key] = list = [];
            list.Add(captured);
        }

        return new CapturedOverwrites(byCategory, byChannel);
    }

    private sealed record CapturedOverwrites(
        Dictionary<string, List<TemplateOverwrite>> ByCategory,
        Dictionary<string, List<TemplateOverwrite>> ByChannel)
    {
        public List<TemplateOverwrite> ForCategory(string categoryId) =>
            ByCategory.TryGetValue(categoryId, out var list) ? list : [];

        public List<TemplateOverwrite> ForChannel(string channelId) =>
            ByChannel.TryGetValue(channelId, out var list) ? list : [];
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
