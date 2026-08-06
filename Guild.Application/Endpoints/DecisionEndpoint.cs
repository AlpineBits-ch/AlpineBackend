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

/// <summary>House decisions on a <see cref="ChannelType.Decisions"/> channel.</summary>
[Authorize]
public class DecisionEndpoint
{
    private const int MaxTitleLength = 150;
    private const int MaxOptions = 10;

    private static DecisionDto ToDto(Decision decision, string requestingUserId)
    {
        var myVote = decision.Votes.FirstOrDefault(v => v.UserId == requestingUserId);
        var blockedOptionIds = decision.Votes
            .Where(v => v.Kind == DecisionVoteKind.Block && v.OptionId is not null)
            .Select(v => v.OptionId!)
            .ToHashSet(StringComparer.Ordinal);

        return new DecisionDto
        {
            Id = decision.Id,
            ChannelId = decision.ChannelId,
            Title = decision.Title,
            Description = decision.Description,
            CreatedByUserId = decision.CreatedByUserId,
            ClosesAt = decision.ClosesAt,
            Quorum = decision.Quorum,
            Status = decision.Status,
            OutcomeOptionId = decision.OutcomeOptionId,
            Options = decision.Options
                .OrderBy(o => o.Position)
                .Select(o => new DecisionOptionDto
                {
                    Id = o.Id,
                    Title = o.Title,
                    Position = o.Position,
                    SupportCount = decision.Votes.Count(v => v.Kind == DecisionVoteKind.Support && v.OptionId == o.Id),
                    IsBlocked = blockedOptionIds.Contains(o.Id),
                }).ToList(),
            // Blocks are surfaced in full, with reasons: an objection is something the house has
            // to resolve, not a number to be outvoted.
            Blocks = decision.Votes
                .Where(v => v.Kind == DecisionVoteKind.Block)
                .Select(v => new DecisionBlockDto
                {
                    UserId = v.UserId,
                    OptionId = v.OptionId,
                    Reason = v.Reason ?? "",
                }).ToList(),
            MyVoteOptionId = myVote?.OptionId,
            MyVoteKind = myVote?.Kind,
        };
    }

    private static IQueryable<Decision> WithGraph(MicroserviceContext ctx) =>
        ctx.Decisions.Include(d => d.Options).Include(d => d.Votes);

    [WolverineGet("/api/v1/channels/{channelId}/decisions")]
    public async Task<IResult> ListAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Decisions, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var decisions = await WithGraph(ctx).AsNoTracking()
            .Where(d => d.ChannelId == channelId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(100)
            .ToListAsync();

        return Results.Ok(decisions.Select(d => ToDto(d, userId)));
    }

    [WolverinePost("/api/v1/channels/{channelId}/decisions")]
    public async Task<IResult> CreateAsync(string channelId, CreateDecisionDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] HouseholdAlertService alerts, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Decisions, userId, Permissions.CreateDecisions);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title is required");
        if (dto.Title.Length > MaxTitleLength) return Results.BadRequest($"Title must be {MaxTitleLength} characters or fewer");

        var options = dto.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
        if (options.Count < 2) return Results.BadRequest("A decision needs at least two options");
        if (options.Count > MaxOptions) return Results.BadRequest($"A decision holds at most {MaxOptions} options");
        if (dto.Quorum is < 1) return Results.BadRequest("Quorum must be at least 1");
        if (dto.ClosesAt is not null && dto.ClosesAt <= DateTimeOffset.UtcNow)
            return Results.BadRequest("ClosesAt must be in the future");

        var decision = Decision.Create(new CreateDecisionParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Title = dto.Title.Trim(),
            Description = dto.Description,
            CreatedByUserId = userId,
            ClosesAt = dto.ClosesAt,
            Quorum = dto.Quorum,
        });

        for (var i = 0; i < options.Count; i++)
        {
            decision.Options.Add(new DecisionOption
            {
                Id = DecisionOption.GenerateId(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                DecisionId = decision.Id,
                Title = options[i],
                Position = i,
            });
        }

        ctx.Decisions.Add(decision);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(decision.GuildId, channelId, "guild.DecisionCreated",
            new { GuildId = decision.GuildId, ChannelId = channelId, Decision = ToDto(decision, userId) });

        await alerts.DecisionOpenedAsync(decision, userId);

        return Results.Ok(ToDto(decision, userId));
    }

    [WolverinePut("/api/v1/decisions/{decisionId}/vote")]
    public async Task<IResult> VoteAsync(string decisionId, CastDecisionVoteDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] HouseholdAlertService alerts, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var decision = await WithGraph(ctx).FirstOrDefaultAsync(d => d.Id == decisionId);
        if (decision is null) return Results.NotFound();

        var access = await household.ResolveAsync(decision.ChannelId, ChannelType.Decisions, userId, Permissions.VoteDecisions);
        if (access.ToFailure() is { } failure) return failure;

        if (decision.Status != DecisionStatus.Open) return Results.BadRequest("That decision is already closed");

        // A veto nobody can see the reasoning for is how a house ends up in a silent standoff, so
        // the reason is mandatory rather than encouraged.
        if (dto.Kind == DecisionVoteKind.Block && string.IsNullOrWhiteSpace(dto.Reason))
            return Results.BadRequest("A block must carry a reason");

        if (dto.Kind == DecisionVoteKind.Support && dto.OptionId is null)
            return Results.BadRequest("Support must name an option");

        if (dto.OptionId is not null && decision.Options.All(o => o.Id != dto.OptionId))
            return Results.BadRequest("That option does not belong to this decision");

        // One vote per member: re-voting replaces rather than accumulates.
        var existing = decision.Votes.FirstOrDefault(v => v.UserId == userId);
        if (existing is null)
        {
            existing = new DecisionVote { DecisionId = decision.Id, UserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            decision.Votes.Add(existing);
        }

        // Only the transition into a block is news.
        var newlyBlocked = dto.Kind == DecisionVoteKind.Block && existing.Kind != DecisionVoteKind.Block;

        existing.Kind = dto.Kind;
        existing.OptionId = dto.Kind == DecisionVoteKind.Abstain ? null : dto.OptionId;
        existing.Reason = dto.Kind == DecisionVoteKind.Block ? dto.Reason!.Trim() : null;

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(decision.GuildId, decision.ChannelId, "guild.DecisionUpdated",
            new { GuildId = decision.GuildId, ChannelId = decision.ChannelId, Decision = ToDto(decision, userId) });

        if (newlyBlocked) await alerts.DecisionBlockedAsync(decision, userId, existing.Reason ?? "");

        return Results.Ok(ToDto(decision, userId));
    }

    /// <summary>Resolves the decision from the votes cast so far.</summary>
    [WolverinePost("/api/v1/decisions/{decisionId}/close")]
    public async Task<IResult> CloseAsync(string decisionId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var decision = await WithGraph(ctx).FirstOrDefaultAsync(d => d.Id == decisionId);
        if (decision is null) return Results.NotFound();

        var access = await household.ResolveAsync(decision.ChannelId, ChannelType.Decisions, userId, Permissions.CreateDecisions);
        if (access.ToFailure() is { } failure) return failure;

        if (decision.Status != DecisionStatus.Open) return Results.BadRequest("That decision is already closed");

        var (status, winningOptionId) = decision.Resolve();
        decision.Status = status;
        decision.OutcomeOptionId = winningOptionId;

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(decision.GuildId, decision.ChannelId, "guild.DecisionClosed",
            new { GuildId = decision.GuildId, ChannelId = decision.ChannelId, Decision = ToDto(decision, userId) });

        return Results.Ok(ToDto(decision, userId));
    }

    [WolverineDelete("/api/v1/decisions/{decisionId}")]
    public async Task<IResult> CancelAsync(string decisionId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var decision = await ctx.Decisions.FirstOrDefaultAsync(d => d.Id == decisionId);
        if (decision is null) return Results.NotFound();

        var access = await household.ResolveAsync(decision.ChannelId, ChannelType.Decisions, userId, Permissions.CreateDecisions);
        if (access.ToFailure() is { } failure) return failure;

        // Soft-cancel rather than delete, matching how ScheduledEventEndpoint handles this: people
        // who already voted should still see that it happened and was called off.
        decision.Status = DecisionStatus.Cancelled;
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(decision.GuildId, decision.ChannelId, "guild.DecisionCancelled",
            new { GuildId = decision.GuildId, ChannelId = decision.ChannelId, DecisionId = decisionId });

        return Results.NoContent();
    }
}
