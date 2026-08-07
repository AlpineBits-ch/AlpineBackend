using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>"I'm away between these dates", and everything the rota does about it.</summary>
[Authorize]
public class AbsenceEndpoint
{
    private static AbsenceDto ToDto(MemberAbsence absence) => new()
    {
        Id = absence.Id,
        GuildId = absence.GuildId,
        UserId = absence.UserId,
        StartAt = absence.StartAt,
        EndAt = absence.EndAt,
        Note = absence.Note,
        CreatedByUserId = absence.CreatedByUserId,
        CreatedAt = absence.CreatedAt,
    };

    /// <summary>Everybody's absences overlapping the window, so the board can show who is around
    /// when. Any member of the house - the rota assigns on this, so it cannot be a private field.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/absences")]
    public async Task<IResult> ListAsync(string guildId, DateTimeOffset? from, DateTimeOffset? to,
        [NotBody] GuildPermissionService permissionService, [NotBody] AbsenceService absences,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        // Defaults span the fairness window backwards and a season forwards: the two questions a
        // client asks are "why does the balance say that" and "who is here next month".
        var start = from ?? DateTimeOffset.UtcNow.AddDays(-ChoreRotationService.DefaultBalanceWindowDays);
        var end = to ?? DateTimeOffset.UtcNow.AddDays(90);

        if (end <= start) return Results.BadRequest("'to' must be after 'from'");

        var rows = await absences.ListAsync(guildId, start, end);

        return Results.Ok(rows.Select(ToDto));
    }

    /// <summary>Declares your own absence, and hands over the chores you were holding for it.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/absences")]
    public async Task<IResult> CreateAsync(string guildId, CreateAbsenceDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] AbsenceService absences,
        [NotBody] ChoreAlertService choreAlerts, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        if (await absences.ValidateAsync(guildId, userId, dto.StartAt, dto.EndAt, dto.Note,
                ignoreAbsenceId: null) is { } error)
            return Results.BadRequest(error);

        var absence = MemberAbsence.Create(new CreateMemberAbsenceParams
        {
            GuildId = guildId,
            UserId = userId,
            StartAt = dto.StartAt,
            EndAt = dto.EndAt,
            Note = dto.Note?.Trim(),
            CreatedByUserId = userId,
        });

        ctx.Set<MemberAbsence>().Add(absence);

        var handovers = await absences.StageChoreHandoverAsync(absence);
        await ctx.SaveChangesAsync();

        await BroadcastAndAlertAsync(household, choreAlerts, absence, handovers, "guild.AbsenceCreated");

        return Results.Ok(new AbsenceSavedDto { Absence = ToDto(absence), ChoresReassigned = handovers.Count });
    }

    /// <summary>Amends an absence.</summary>
    [WolverinePatch("/api/v1/absences/{absenceId}")]
    public async Task<IResult> UpdateAsync(string absenceId, UpdateAbsenceDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] AbsenceService absences,
        [NotBody] ChoreAlertService choreAlerts, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var absence = await absences.FindAsync(absenceId);
        if (absence is null) return Results.NotFound();

        if (!await permissionService.IsFeatureEnabledAsync(absence.GuildId, GuildFeatures.Presence))
            return Results.Forbid();

        if (!await CanWriteAsync(permissionService, absence, userId)) return Results.Forbid();

        var startAt = dto.StartAt ?? absence.StartAt;
        var endAt = dto.EndAt ?? absence.EndAt;
        var note = dto.ClearNote == true ? null : dto.Note ?? absence.Note;

        if (await absences.ValidateAsync(absence.GuildId, absence.UserId, startAt, endAt, note,
                ignoreAbsenceId: absence.Id) is { } error)
            return Results.BadRequest(error);

        absence.StartAt = startAt;
        absence.EndAt = endAt;
        absence.Note = note?.Trim();
        absence.UpdatedAt = DateTimeOffset.UtcNow;

        var handovers = await absences.StageChoreHandoverAsync(absence);
        await ctx.SaveChangesAsync();

        await BroadcastAndAlertAsync(household, choreAlerts, absence, handovers, "guild.AbsenceUpdated");

        return Results.Ok(new AbsenceSavedDto { Absence = ToDto(absence), ChoresReassigned = handovers.Count });
    }

    /// <summary>Withdraws an absence.</summary>
    [WolverineDelete("/api/v1/absences/{absenceId}")]
    public async Task<IResult> DeleteAsync(string absenceId,
        [NotBody] GuildPermissionService permissionService, [NotBody] AbsenceService absences,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var absence = await absences.FindAsync(absenceId);
        if (absence is null) return Results.NotFound();

        if (!await permissionService.IsFeatureEnabledAsync(absence.GuildId, GuildFeatures.Presence))
            return Results.Forbid();

        if (!await CanWriteAsync(permissionService, absence, userId)) return Results.Forbid();

        ctx.Set<MemberAbsence>().Remove(absence);
        await ctx.SaveChangesAsync();

        await household.BroadcastGuildAsync(absence.GuildId, "guild.AbsenceDeleted",
            new { GuildId = absence.GuildId, AbsenceId = absenceId, absence.UserId });

        return Results.NoContent();
    }

    private static async Task<bool> CanWriteAsync(
        GuildPermissionService permissionService, MemberAbsence absence, string userId) =>
        absence.UserId == userId ||
        await permissionService.CanUserPerformActionOnGuildAsync(userId, absence.GuildId, Permissions.ManageGuild);

    /// <summary>Guild-scoped realtime, unlike the channel-scoped household broadcasts.</summary>
    private static async Task BroadcastAndAlertAsync(
        HouseholdChannelService household, ChoreAlertService choreAlerts,
        MemberAbsence absence, IReadOnlyList<ChoreHandover> handovers, string eventName)
    {
        await household.BroadcastGuildAsync(absence.GuildId, eventName, new
        {
            GuildId = absence.GuildId,
            Absence = ToDto(absence),
            ChoresReassigned = handovers.Count,
        });

        await choreAlerts.ReassignedAsync(handovers);
    }
}
