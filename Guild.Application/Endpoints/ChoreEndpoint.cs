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

/// <summary>The chore rota on a <see cref="ChannelType.Chores"/> channel.</summary>
[Authorize]
public class ChoreEndpoint
{
    private const int MaxTitleLength = 100;

    private static ChoreDto ToDto(Chore chore) => new()
    {
        Id = chore.Id,
        ChannelId = chore.ChannelId,
        Title = chore.Title,
        Description = chore.Description,
        IntervalDays = chore.IntervalDays,
        AnchorAt = chore.AnchorAt,
        EffortMinutes = chore.EffortMinutes,
        RotationRoleId = chore.RotationRoleId,
        FixedAssigneeUserId = chore.FixedAssigneeUserId,
        GraceHours = chore.GraceHours,
        IsPaused = chore.IsPaused,
        NextDueAt = chore.NextDueAt,
    };

    private static ChoreOccurrenceDto ToDto(ChoreOccurrence occurrence, string title, int graceHours) => new()
    {
        Id = occurrence.Id,
        ChoreId = occurrence.ChoreId,
        ChannelId = occurrence.ChannelId,
        Title = title,
        DueAt = occurrence.DueAt,
        AssignedUserId = occurrence.AssignedUserId,
        EffortMinutes = occurrence.EffortMinutes,
        CompletedAt = occurrence.CompletedAt,
        CompletedByUserId = occurrence.CompletedByUserId,
        SkippedAt = occurrence.SkippedAt,
        IsOverdue = occurrence.CompletedAt is null
                    && occurrence.SkippedAt is null
                    && occurrence.DueAt.AddHours(graceHours) < DateTimeOffset.UtcNow,
        NudgedAt = occurrence.NudgedAt,
    };

    [WolverineGet("/api/v1/channels/{channelId}/chores")]
    public async Task<IResult> ListAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Chores, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var chores = await ctx.Chores.AsNoTracking()
            .Where(c => c.ChannelId == channelId)
            .OrderBy(c => c.NextDueAt)
            .ToListAsync();

        return Results.Ok(chores.Select(ToDto));
    }

    [WolverinePost("/api/v1/channels/{channelId}/chores")]
    public async Task<IResult> CreateAsync(string channelId, CreateChoreDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] ChoreRotationService rotation,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Chores, userId, ModulePermissions.ManageChores);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title is required");
        if (dto.Title.Length > MaxTitleLength) return Results.BadRequest($"Title must be {MaxTitleLength} characters or fewer");
        if (dto.IntervalDays is < 1 or > 365) return Results.BadRequest("IntervalDays must be between 1 and 365");
        if (dto.EffortMinutes is < 1 or > 600) return Results.BadRequest("EffortMinutes must be between 1 and 600");
        if (dto.GraceHours is < 0 or > 336) return Results.BadRequest("GraceHours must be between 0 and 336");

        if (dto.RotationRoleId is null && dto.FixedAssigneeUserId is null)
            return Results.BadRequest("A chore needs either a RotationRoleId or a FixedAssigneeUserId");

        if (dto.RotationRoleId is not null &&
            !await ctx.Roles.AnyAsync(r => r.Id == dto.RotationRoleId && r.GuildId == access.Channel!.GuildId))
            return Results.BadRequest("RotationRoleId must be a role in this guild");

        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Title = dto.Title.Trim(),
            Description = dto.Description,
            IntervalDays = dto.IntervalDays,
            AnchorAt = dto.AnchorAt ?? DateTimeOffset.UtcNow,
            EffortMinutes = dto.EffortMinutes,
            RotationRoleId = dto.RotationRoleId,
            FixedAssigneeUserId = dto.FixedAssigneeUserId,
            GraceHours = dto.GraceHours,
        });

        ctx.Chores.Add(chore);

        // Generate the first occurrence immediately so a newly created chore shows up on the board
        // rather than appearing only after the first sweep.
        var occurrence = await rotation.StageNextOccurrenceAsync(chore);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(chore.GuildId, channelId, "guild.ChoreCreated",
            new { GuildId = chore.GuildId, ChannelId = channelId, Chore = ToDto(chore) });

        if (occurrence is not null)
            await household.BroadcastAsync(chore.GuildId, channelId, "guild.ChoreOccurrenceCreated",
                new { GuildId = chore.GuildId, ChannelId = channelId, Occurrence = ToDto(occurrence, chore.Title, chore.GraceHours) });

        return Results.Ok(ToDto(chore));
    }

    [WolverinePatch("/api/v1/chores/{choreId}")]
    public async Task<IResult> UpdateAsync(string choreId, UpdateChoreDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var chore = await ctx.Chores.FirstOrDefaultAsync(c => c.Id == choreId);
        if (chore is null) return Results.NotFound();

        var access = await household.ResolveAsync(chore.ChannelId, ChannelType.Chores, userId, ModulePermissions.ManageChores);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title cannot be empty");
            if (dto.Title.Length > MaxTitleLength) return Results.BadRequest($"Title must be {MaxTitleLength} characters or fewer");
            chore.Title = dto.Title.Trim();
        }

        if (dto.Description is not null) chore.Description = dto.Description;

        if (dto.IntervalDays is not null)
        {
            if (dto.IntervalDays is < 1 or > 365) return Results.BadRequest("IntervalDays must be between 1 and 365");
            chore.IntervalDays = dto.IntervalDays.Value;
        }

        if (dto.EffortMinutes is not null)
        {
            if (dto.EffortMinutes is < 1 or > 600) return Results.BadRequest("EffortMinutes must be between 1 and 600");
            // Only affects occurrences generated from here on - existing ones keep their snapshot,
            // so re-weighting a chore never rewrites anyone's historical balance.
            chore.EffortMinutes = dto.EffortMinutes.Value;
        }

        if (dto.GraceHours is not null)
        {
            if (dto.GraceHours is < 0 or > 336) return Results.BadRequest("GraceHours must be between 0 and 336");
            chore.GraceHours = dto.GraceHours.Value;
        }

        if (dto.RotationRoleId is not null)
        {
            if (!await ctx.Roles.AnyAsync(r => r.Id == dto.RotationRoleId && r.GuildId == chore.GuildId))
                return Results.BadRequest("RotationRoleId must be a role in this guild");
            chore.RotationRoleId = dto.RotationRoleId;
        }

        if (dto.FixedAssigneeUserId is not null) chore.FixedAssigneeUserId = dto.FixedAssigneeUserId;
        if (dto.IsPaused is not null) chore.IsPaused = dto.IsPaused.Value;

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(chore.GuildId, chore.ChannelId, "guild.ChoreUpdated",
            new { GuildId = chore.GuildId, ChannelId = chore.ChannelId, Chore = ToDto(chore) });

        return Results.Ok(ToDto(chore));
    }

    [WolverineDelete("/api/v1/chores/{choreId}")]
    public async Task<IResult> DeleteAsync(string choreId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var chore = await ctx.Chores.FirstOrDefaultAsync(c => c.Id == choreId);
        if (chore is null) return Results.NotFound();

        var access = await household.ResolveAsync(chore.ChannelId, ChannelType.Chores, userId, ModulePermissions.ManageChores);
        if (access.ToFailure() is { } failure) return failure;

        ctx.Chores.Remove(chore);   // occurrences cascade
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(chore.GuildId, chore.ChannelId, "guild.ChoreDeleted",
            new { GuildId = chore.GuildId, ChannelId = chore.ChannelId, ChoreId = choreId });

        return Results.NoContent();
    }

    [WolverineGet("/api/v1/channels/{channelId}/chores/occurrences")]
    public async Task<IResult> ListOccurrencesAsync(string channelId, DateTimeOffset? from, DateTimeOffset? to,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Chores, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var start = from ?? DateTimeOffset.UtcNow.AddDays(-14);
        var end = to ?? DateTimeOffset.UtcNow.AddDays(28);

        var occurrences = await ctx.ChoreOccurrences.AsNoTracking()
            .Where(o => o.ChannelId == channelId && o.DueAt >= start && o.DueAt <= end)
            .OrderBy(o => o.DueAt)
            .ToListAsync();

        var choreIds = occurrences.Select(o => o.ChoreId).Distinct().ToList();
        var chores = await ctx.Chores.AsNoTracking()
            .Where(c => choreIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.GraceHours })
            .ToDictionaryAsync(c => c.Id);

        return Results.Ok(occurrences.Select(o =>
            ToDto(o,
                chores.TryGetValue(o.ChoreId, out var c) ? c.Title : "",
                chores.TryGetValue(o.ChoreId, out var g) ? g.GraceHours : 24)));
    }

    [WolverinePost("/api/v1/chore-occurrences/{occurrenceId}/complete")]
    public Task<IResult> CompleteAsync(string occurrenceId, [NotBody] HouseholdChannelService household,
        [NotBody] ChoreRotationService rotation, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user) =>
        SetCompletedAsync(occurrenceId, true, household, rotation, ctx, user);

    [WolverineDelete("/api/v1/chore-occurrences/{occurrenceId}/complete")]
    public Task<IResult> UncompleteAsync(string occurrenceId, [NotBody] HouseholdChannelService household,
        [NotBody] ChoreRotationService rotation, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user) =>
        SetCompletedAsync(occurrenceId, false, household, rotation, ctx, user);

    private static async Task<IResult> SetCompletedAsync(string occurrenceId, bool completed,
        HouseholdChannelService household, ChoreRotationService rotation, MicroserviceContext ctx,
        ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var occurrence = await ctx.ChoreOccurrences.FirstOrDefaultAsync(o => o.Id == occurrenceId);
        if (occurrence is null) return Results.NotFound();

        var access = await household.ResolveAsync(occurrence.ChannelId, ChannelType.Chores, userId, ModulePermissions.CompleteChores);
        if (access.ToFailure() is { } failure) return failure;

        if ((occurrence.CompletedAt is not null) == completed)
            return Results.Ok();   // idempotent - a double tap is not an error

        if (completed)
        {
            occurrence.CompletedAt = DateTimeOffset.UtcNow;
            // Whoever actually did it, which is frequently not who it was assigned to.
            occurrence.CompletedByUserId = userId;
            occurrence.SkippedAt = null;
        }
        else
        {
            occurrence.CompletedAt = null;
            occurrence.CompletedByUserId = null;
        }

        await ctx.SaveChangesAsync();

        var chore = await ctx.Chores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == occurrence.ChoreId);

        await household.BroadcastAsync(occurrence.GuildId, occurrence.ChannelId, "guild.ChoreOccurrenceUpdated", new
        {
            GuildId = occurrence.GuildId,
            ChannelId = occurrence.ChannelId,
            Occurrence = ToDto(occurrence, chore?.Title ?? "", chore?.GraceHours ?? 24),
        });

        return Results.Ok();
    }

    [WolverinePost("/api/v1/chore-occurrences/{occurrenceId}/skip")]
    public async Task<IResult> SkipAsync(string occurrenceId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var occurrence = await ctx.ChoreOccurrences.FirstOrDefaultAsync(o => o.Id == occurrenceId);
        if (occurrence is null) return Results.NotFound();

        var access = await household.ResolveAsync(occurrence.ChannelId, ChannelType.Chores, userId, ModulePermissions.CompleteChores);
        if (access.ToFailure() is { } failure) return failure;

        occurrence.SkippedAt = DateTimeOffset.UtcNow;
        occurrence.CompletedAt = null;
        occurrence.CompletedByUserId = null;

        // Deliberately not credited to the balance: a skipped chore stays unpaid work, which is
        // exactly what makes the next rotation land back on the same person.
        await ctx.SaveChangesAsync();

        var chore = await ctx.Chores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == occurrence.ChoreId);

        // Same envelope as complete/uncomplete/swap.
        await household.BroadcastAsync(occurrence.GuildId, occurrence.ChannelId, "guild.ChoreOccurrenceUpdated", new
        {
            GuildId = occurrence.GuildId,
            ChannelId = occurrence.ChannelId,
            Occurrence = ToDto(occurrence, chore?.Title ?? "", chore?.GraceHours ?? 24),
        });

        return Results.Ok(ToDto(occurrence, chore?.Title ?? "", chore?.GraceHours ?? 24));
    }

    /// <summary>Hands an occurrence to whoever currently has the lightest load.</summary>
    [WolverinePost("/api/v1/chore-occurrences/{occurrenceId}/swap")]
    public async Task<IResult> SwapAsync(string occurrenceId, [NotBody] HouseholdChannelService household,
        [NotBody] ChoreRotationService rotation, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var occurrence = await ctx.ChoreOccurrences.FirstOrDefaultAsync(o => o.Id == occurrenceId);
        if (occurrence is null) return Results.NotFound();

        var access = await household.ResolveAsync(occurrence.ChannelId, ChannelType.Chores, userId, ModulePermissions.CompleteChores);
        if (access.ToFailure() is { } failure) return failure;

        if (occurrence.CompletedAt is not null) return Results.BadRequest("That chore is already done");

        var chore = await ctx.Chores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == occurrence.ChoreId);
        if (chore is null) return Results.NotFound();

        // Picked for the date the work is actually due, so "I can't do the bins tonight" does not
        // resolve itself onto somebody who is away that night.
        var replacement = await rotation.PickNextAssigneeAsync(
            chore, excludeUserId: occurrence.AssignedUserId, dueAt: occurrence.DueAt);

        if (replacement is null || replacement == occurrence.AssignedUserId)
            return Results.BadRequest("Nobody else is in the rotation for this chore");

        occurrence.AssignedUserId = replacement;
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(occurrence.GuildId, occurrence.ChannelId, "guild.ChoreOccurrenceUpdated", new
        {
            GuildId = occurrence.GuildId,
            ChannelId = occurrence.ChannelId,
            Occurrence = ToDto(occurrence, chore.Title, chore.GraceHours),
        });

        return Results.Ok(ToDto(occurrence, chore.Title, chore.GraceHours));
    }

    /// <summary>Asks the assignee, without saying who is asking.</summary>
    [WolverinePost("/api/v1/chore-occurrences/{occurrenceId}/nudge")]
    public async Task<IResult> NudgeAsync(string occurrenceId, [NotBody] HouseholdChannelService household,
        [NotBody] ChoreAlertService choreAlerts, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var occurrence = await ctx.ChoreOccurrences.FirstOrDefaultAsync(o => o.Id == occurrenceId);
        if (occurrence is null) return Results.NotFound();

        var access = await household.ResolveAsync(occurrence.ChannelId, ChannelType.Chores, userId, ModulePermissions.CompleteChores);
        if (access.ToFailure() is { } failure) return failure;

        // Nudging yourself is not a thing.
        if (occurrence.AssignedUserId == userId)
            return Results.BadRequest("You can't nudge yourself about your own chore");

        if (occurrence.CompletedAt is not null) return Results.BadRequest("That chore is already done");
        if (occurrence.SkippedAt is not null) return Results.BadRequest("That chore was skipped");

        var chore = await ctx.Chores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == occurrence.ChoreId);
        if (chore is null) return Results.NotFound();

        var now = DateTimeOffset.UtcNow;

        // Only once it is genuinely late.
        if (occurrence.DueAt.AddHours(chore.GraceHours) >= now)
            return Results.BadRequest("That chore isn't overdue yet");

        if (occurrence.NudgedAt is { } lastNudge && now - lastNudge < ChoreAlertService.NudgeCooldown)
        {
            return Results.Conflict(new
            {
                Error = "Somebody already nudged about this recently",
                NextNudgeAt = lastNudge + ChoreAlertService.NudgeCooldown,
            });
        }

        // Rejected inside quiet hours rather than deferred to the end of them, which is the
        // opposite of what chore.due does with the same config.
        var quietHours = await ctx.GuildQuietHoursConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.GuildId == occurrence.GuildId);

        if (quietHours?.DeferPast(now) is { } quietUntil && quietUntil > now)
        {
            return Results.Conflict(new
            {
                Error = "The house is in quiet hours",
                QuietUntil = quietUntil,
            });
        }

        occurrence.NudgedAt = now;
        await ctx.SaveChangesAsync();

        // Carries no sender.
        await household.BroadcastAsync(occurrence.GuildId, occurrence.ChannelId, "guild.ChoreOccurrenceNudged", new
        {
            GuildId = occurrence.GuildId,
            ChannelId = occurrence.ChannelId,
            OccurrenceId = occurrence.Id,
            occurrence.NudgedAt,
        });

        await choreAlerts.NudgeAsync(occurrence, chore.Title);

        return Results.Ok(new { OccurrenceId = occurrence.Id, occurrence.NudgedAt });
    }

    /// <summary>Who is actually carrying the house.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/chores/balance")]
    public async Task<IResult> BalanceAsync(string channelId, int? days,
        [NotBody] HouseholdChannelService household, [NotBody] ChoreRotationService rotation,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Chores, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var window = Math.Clamp(days ?? ChoreRotationService.DefaultBalanceWindowDays, 1, 365);

        // Everyone who appears in any rotation on this board, so the balance covers the people the
        // rota can actually assign to rather than every member of the guild.
        var chores = await ctx.Chores.AsNoTracking().Where(c => c.ChannelId == channelId).ToListAsync();
        var pool = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chore in chores)
            foreach (var candidate in await rotation.GetRotationPoolAsync(chore))
                pool.Add(candidate);

        if (pool.Count == 0) return Results.Ok(Array.Empty<ChoreBalanceEntryDto>());

        var balances = await rotation.GetWeightedBalancesAsync(access.Channel!.GuildId, pool, window);

        return Results.Ok(balances
            .OrderBy(b => b.CompletedMinutes)
            .Select(b => new ChoreBalanceEntryDto
            {
                UserId = b.UserId,
                CompletedMinutes = b.CompletedMinutes,
                CompletedCount = b.CompletedCount,
                // Relative to their own expected share, so the number reads as "behind/ahead of
                // your share" rather than as a raw total nobody can calibrate.
                BalanceMinutes = b.BalanceMinutes,
                PresentDays = b.PresentDays,
            }));
    }
}
