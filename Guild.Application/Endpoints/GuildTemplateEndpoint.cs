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

        foreach (var roleTemplate in template.Snapshot.Roles.Where(r => r != everyoneTemplate))
        {
            ctx.Roles.Add(Role.Create(new CreateRoleParams
            {
                Name = roleTemplate.Name,
                Color = roleTemplate.Color,
                GuildId = guild.Id,
                Permissions = roleTemplate.Permissions,
            }));
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
            firstTextChannelId ??= channelTemplate.Type == ChannelType.Text ? channel.Id : null;
        }

        guild.SystemChannelId = firstTextChannelId;
        template.UsageCount++;

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
}
