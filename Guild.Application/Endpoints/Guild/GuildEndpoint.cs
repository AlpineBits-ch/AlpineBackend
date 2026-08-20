using System.Security.Claims;
using System.Text;
using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence.Durability;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Guild.Application.Endpoints;

[Authorize]
public class GuildEndpoint
{
    [WolverinePost("/api/v1/guilds")]
    public async Task<IResult> CreateGuild(CreateGuildDto dto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {

        var profileResponse = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = user.FindFirstValue(ClaimTypes.NameIdentifier)!
        });

        if(profileResponse.Profile is null) return Results.BadRequest("User not found");
        
        var searchValue = profileResponse.Profile.UserName! + "#" + profileResponse.Profile.Hash;
        
        var parameters = new CreateGuildParams()
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            OwnerSearchValue = searchValue.ToUpperInvariant(),
            OwnerNickname = profileResponse.Profile.UserName,
            Kind = dto.Kind,
        };

        var guild = Domain.Aggregates.Guild.Create(parameters);

        ctx.Guilds.Add(guild);

        // A household gets a starter house manual: wifi, bin day, the boiler, who to call.
        if (Domain.Aggregates.Guild.HouseManualFor(guild, parameters) is { } manual)
            ctx.AddRange(manual.Rows);
        
        
        // because of the sys channel we have to do some hacks 

        var sysChannelId = guild.SystemChannelId;
        guild.SystemChannelId = null;
        await ctx.SaveChangesAsync();
        guild.SystemChannelId = sysChannelId;

        if (!string.IsNullOrWhiteSpace(guild.SystemChannelId))
        {
            await bus.InvokeAsync(new CreateMessageCommand()
            {
                Content = Encoding.UTF8.GetBytes($"{profileResponse.Profile.UserName} joined the server"),
                ChannelId = guild.SystemChannelId,
                AuthorId = guild.OwnerId,
                AuthorIdType = AuthorIdType.User,
                Mentions = [],
                Type = MessagingMessageType.GuildMemberJoin,
            });
        }

        return Results.Ok(guild.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }
    
    
    
    [WolverineDelete("/api/v1/guilds/{id}")]
    public async Task<IResult> DeleteGuild(string id, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] PersonaService personas)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guild = await ctx.Guilds.FirstOrDefaultAsync(g => g.Id == id);
        if (guild is null) return Results.NotFound();

        // Only the owner may delete the guild - matches Discord (no delegated "ManageGuild"
        // holder can do this, since it's irreversible and destroys everyone's data).
        if (guild.OwnerId != userId) return Results.Forbid();

        var memberUserIds = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == id)
            .Select(m => m.UserId)
            .ToListAsync();

        // The profiles cascade with the guild but Persona.HomeProfileId carries no foreign key, so
        // without this sweep a character adopted here keeps pointing at a row that is about to go.
        await personas.RepointHomesForDeletedGuildAsync(id);

        ctx.Guilds.Remove(guild);
        await ctx.SaveChangesAsync();

        // Resolved from the DB (not presence), so any client currently connected -
        // even idle/backgrounded - gets the eviction event immediately.
        await hub.Clients.Users(memberUserIds).SendAsync("guild.GuildDeleted", new { GuildId = id });

        return Results.NoContent();
    }

    [WolverinePatch("/api/v1/guilds/{id}")]
    public async Task<IResult> UpdateGuild(string id, UpdateGuildDto dto, [NotBody] MicroserviceContext context,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guild = await context.Guilds.Include(g => g.Channels)
            .Include(g => g.Categories)
            .ThenInclude(c => c.Channels)
            .Where(g => g.Id == id).FirstOrDefaultAsync();
        if (guild is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, id, Permissions.ManageGuild);
        if (!canManage) return Results.Forbid();

        if (await mfa.RequireAsync(id, user) is { } mfaRejection) return mfaRejection;

        // Guarded like every other field on this PATCH: assigning unconditionally meant a request
        // that only switched a module on sent no name and nulled the column.
        if (!string.IsNullOrWhiteSpace(dto.Name)) guild.Name = dto.Name;
        if (dto.Description is not null) guild.Description = dto.Description;

        if (dto.SystemChannelId is not null)
        {
            var channel = guild.Channels.FirstOrDefault(c => c.Id == dto.SystemChannelId);
            if (channel is null || (channel.Type != ChannelType.Text && channel.Type != ChannelType.Announcement))
                return Results.BadRequest("System channel must be a text or announcement channel in this guild");

            guild.SystemChannelId = dto.SystemChannelId;
        }

        if (dto.VerificationLevel is not null)
        {
            guild.VerificationLevel = dto.VerificationLevel.Value;
        }

        if (dto.DefaultMessageNotifications is not null)
        {
            // Nothing is a valid *member* preference but not a valid guild default - it would
            // silence the server for everyone who never opened its settings.
            if (dto.DefaultMessageNotifications.Value == NotificationLevel.Nothing)
                return Results.BadRequest("A guild default of Nothing is not allowed; members can set that for themselves.");

            guild.DefaultMessageNotifications = dto.DefaultMessageNotifications.Value;
        }

        if (dto.Kind is not null) guild.Kind = dto.Kind.Value;

        // Explicit features win; a kind change on its own re-seeds from that kind's preset.
        if (dto.Features is not null) guild.Features = dto.Features.Value;
        else if (dto.Kind is not null) guild.Features = GuildFeaturePresets.For(dto.Kind.Value);

        if (dto.Features is not null || dto.Kind is not null)
            await permissionService.InvalidateGuildFeaturesCacheAsync(id);

        auditLog.Log(id, userId, AuditActionType.GuildUpdated, id);

        var presence = await guildHydrateService.GetGuildPresenceAsync(id);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.GuildUpdated", new { GuildId = id });

        return Results.Ok(guild.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }

    /// <summary>Turns the guild's two-factor requirement on or off.</summary>
    [WolverinePost("/api/v1/guilds/{id}/mfa")]
    public async Task<IResult> SetMfaRequirement(string id, SetMfaRequirementDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guild = await ctx.Guilds.FirstOrDefaultAsync(g => g.Id == id);
        if (guild is null) return Results.NotFound();

        if (guild.OwnerId != userId) return Results.Forbid();

        if (dto.Required && !MfaElevationService.HasMfa(user))
            return MfaElevationService.Rejection();

        if (guild.MfaRequired == dto.Required) return Results.NoContent();

        guild.MfaRequired = dto.Required;

        auditLog.Log(id, userId, AuditActionType.GuildUpdated, id, new { MfaRequired = dto.Required });

        var presence = await guildHydrateService.GetGuildPresenceAsync(id);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.GuildUpdated", new { GuildId = id });

        return Results.NoContent();
    }
}