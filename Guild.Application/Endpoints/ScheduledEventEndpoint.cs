using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class ScheduledEventEndpoint
{
    private static GuildScheduledEventDto ToDto(GuildScheduledEvent evt, string requestingUserId)
    {
        var dto = evt.ToFacet<GuildScheduledEvent, GuildScheduledEventDto>();
        dto.InterestedCount = evt.Interested.Count;
        dto.IsInterested = evt.Interested.Any(i => i.UserId == requestingUserId);
        return dto;
    }

    [WolverineGet("/api/v1/guilds/{guildId}/events")]
    public async Task<IResult> ListEvents(string guildId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId)) return Results.Forbid();

        var events = await ctx.Set<GuildScheduledEvent>()
            .AsNoTracking()
            .Include(e => e.Interested)
            .Where(e => e.GuildId == guildId && e.Status != GuildScheduledEventStatus.Cancelled)
            .OrderBy(e => e.StartsAt)
            .ToListAsync();

        return Results.Ok(events.Select(e => ToDto(e, userId)));
    }

    [WolverinePost("/api/v1/guilds/{guildId}/events")]
    public async Task<IResult> CreateEvent(string guildId, CreateScheduledEventDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageEvents))
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title is required");
        if (dto.EndsAt is not null && dto.EndsAt <= dto.StartsAt) return Results.BadRequest("EndsAt must be after StartsAt");

        var evt = GuildScheduledEvent.Create(new CreateGuildScheduledEventParams
        {
            GuildId = guildId,
            CreatorUserId = userId,
            Title = dto.Title,
            Description = dto.Description,
            StartsAt = dto.StartsAt,
            EndsAt = dto.EndsAt,
            Location = dto.Location,
            VoiceChannelId = dto.VoiceChannelId,
        });

        ctx.Set<GuildScheduledEvent>().Add(evt);
        auditLog.Log(guildId, userId, AuditActionType.ScheduledEventCreated, evt.Id, new { evt.Title });

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.EventCreated",
            new { GuildId = guildId, EventId = evt.Id, evt.Title, evt.StartsAt });

        return Results.Ok(ToDto(evt, userId));
    }

    [WolverinePatch("/api/v1/events/{eventId}")]
    public async Task<IResult> UpdateEvent(string eventId, UpdateScheduledEventDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var evt = await ctx.Set<GuildScheduledEvent>().Include(e => e.Interested).FirstOrDefaultAsync(e => e.Id == eventId);
        if (evt is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, evt.GuildId, Permissions.ManageEvents))
            return Results.Forbid();

        if (dto.Title is not null) evt.Title = dto.Title;
        if (dto.Description is not null) evt.Description = dto.Description;
        if (dto.StartsAt is not null) evt.StartsAt = dto.StartsAt.Value;
        if (dto.EndsAt is not null) evt.EndsAt = dto.EndsAt;
        if (dto.Location is not null) evt.Location = dto.Location;
        if (dto.VoiceChannelId is not null) evt.VoiceChannelId = dto.VoiceChannelId;

        auditLog.Log(evt.GuildId, userId, AuditActionType.ScheduledEventUpdated, evt.Id);

        var presence = await guildHydrateService.GetGuildPresenceAsync(evt.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.EventUpdated",
            new { GuildId = evt.GuildId, EventId = evt.Id, evt.Title, evt.StartsAt });

        return Results.Ok(ToDto(evt, userId));
    }

    [WolverineDelete("/api/v1/events/{eventId}")]
    public async Task<IResult> CancelEvent(string eventId, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var evt = await ctx.Set<GuildScheduledEvent>().FirstOrDefaultAsync(e => e.Id == eventId);
        if (evt is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, evt.GuildId, Permissions.ManageEvents))
            return Results.Forbid();

        // Soft-cancel rather than delete - members who saw/RSVP'd should still be able to see it
        // happened and was called off, matching Discord's cancel-vs-delete distinction.
        evt.Status = GuildScheduledEventStatus.Cancelled;

        auditLog.Log(evt.GuildId, userId, AuditActionType.ScheduledEventCancelled, evt.Id, new { evt.Title });

        var presence = await guildHydrateService.GetGuildPresenceAsync(evt.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.EventCancelled",
            new { GuildId = evt.GuildId, EventId = evt.Id });

        return Results.NoContent();
    }

    [WolverinePost("/api/v1/events/{eventId}/interested")]
    public async Task<IResult> MarkInterested(string eventId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var evt = await ctx.Set<GuildScheduledEvent>().FirstOrDefaultAsync(e => e.Id == eventId);
        if (evt is null) return Results.NotFound();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == evt.GuildId && m.UserId == userId)) return Results.Forbid();

        var alreadyInterested = await ctx.Set<GuildScheduledEventInterest>().AnyAsync(i => i.EventId == eventId && i.UserId == userId);
        if (!alreadyInterested)
        {
            ctx.Set<GuildScheduledEventInterest>().Add(new GuildScheduledEventInterest
            {
                EventId = eventId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        return Results.Ok();
    }

    [WolverineDelete("/api/v1/events/{eventId}/interested")]
    public async Task<IResult> RemoveInterested(string eventId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var interest = await ctx.Set<GuildScheduledEventInterest>().FirstOrDefaultAsync(i => i.EventId == eventId && i.UserId == userId);
        if (interest is not null) ctx.Set<GuildScheduledEventInterest>().Remove(interest);

        return Results.Ok();
    }
}
