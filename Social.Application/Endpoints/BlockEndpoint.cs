using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Api.Dtos.Response;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Http;

namespace Social.Api.Endpoints;

/// <summary>
/// Blocking (privacy spec T0-3). Social owns the block list; every other service consults it through
/// <c>GetBlockRelationshipsRequest</c>.
///
/// <para><b>One-directional and asymmetric.</b> A block is a single <c>Relationship</c> row owned by
/// the blocker, status <see cref="RelationshipStatus.Blocked"/>, <c>RelatedId</c> null. The blocked
/// party gets no row at all, so every list they can read - <c>GET /api/v1/relationships</c>,
/// <c>GET /api/v1/relationships/blocked</c>, their profile projections - shows them exactly what
/// "not friends" shows. That is the point: a block they can detect is a block that invites
/// retaliation.</para>
///
/// <para>The route parameter is an Identity <b>user</b> id, matching
/// <c>GET /api/v1/profiles/by-user/{id}</c>, not a <c>prfl_</c> profile id.</para>
/// </summary>
[Authorize]
public static class BlockEndpoints
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    /// <summary>
    /// Blocks <paramref name="userId"/>. Idempotent: blocking someone already blocked is a 204 that
    /// changes nothing and republishes nothing.
    ///
    /// <para>Tears down whatever relationship existed first - an accepted friendship, a pending
    /// request in either direction - because a block that left a live friendship in place would keep
    /// the DM and call paths (both gated on friendship) open to the person just blocked.</para>
    /// </summary>
    [WolverinePost("/api/v1/relationships/{userId}/block")]
    public static async Task<IResult> BlockAsync(
        string userId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (callerUserId is null) return Results.Unauthorized();

        var blocker = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == callerUserId);
        if (blocker is null) return Results.BadRequest("Caller profile not found.");

        if (userId == callerUserId) return Results.BadRequest("You cannot block yourself.");

        var target = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        // Blocking a user id with no profile is not something to correct the caller about at
        // length, but it is their own input and reveals nothing about anybody's settings.
        if (target is null) return Results.NotFound();

        var forward = await ctx.Relationships.FirstOrDefaultAsync(r => r.OwnerId == blocker.Id && r.TargetId == target.Id);
        var reverse = await ctx.Relationships.FirstOrDefaultAsync(r => r.OwnerId == target.Id && r.TargetId == blocker.Id);

        if (forward is { Status: RelationshipStatus.Blocked }) return Results.NoContent();

        // The blocked party's own row is cleared through the ordinary un-friending transition, so
        // their client is told the relationship ended and nothing more. Remove() raises
        // FriendRemoved only on a real transition, so a pair that was already at None stays quiet -
        // and it refuses outright on a Blocked row, so blocking someone who already blocked you
        // cannot clear their block (which is also how they would find out about it).
        if (reverse is not null)
        {
            reverse.Remove();
            if (reverse.Status != RelationshipStatus.Blocked) reverse.RelatedId = null;
        }

        if (forward is not null)
        {
            forward.Remove();
            forward.Block();
        }
        else
        {
            forward = new Relationship
            {
                Id = Relationship.GenerateId(),
                OwnerId = blocker.Id,
                TargetId = target.Id,
                Status = RelationshipStatus.Blocked,
                RelatedId = null,
            };
            ctx.Relationships.Add(forward);
        }

        // Both integration profiles carry the friend list Messaging gates DMs and calls on, and it
        // just changed for both people - same eviction the unfriend path does.
        await EvictProfilesAsync(cache, blocker.UserId, target.UserId);

        await bus.PublishAsync(new UserBlockedEvent { BlockerId = blocker.UserId, BlockedId = target.UserId });

        return Results.NoContent();
    }

    /// <summary>
    /// Lifts a block. Idempotent: unblocking someone who is not blocked is a 204.
    ///
    /// <para>The rows are deleted rather than parked at <see cref="RelationshipStatus.None"/>. A
    /// lingering row for the pair would trip the "relationship already exists" guard in
    /// <c>POST /api/v1/relationships</c> forever, so an unblocked pair could never become friends -
    /// the block would go on costing them something after it was lifted.</para>
    /// </summary>
    [WolverineDelete("/api/v1/relationships/{userId}/block")]
    public static async Task<IResult> UnblockAsync(
        string userId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (callerUserId is null) return Results.Unauthorized();

        var blocker = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == callerUserId);
        if (blocker is null) return Results.BadRequest("Caller profile not found.");

        var target = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        // Nothing to undo, and saying so tells the caller nothing they did not supply.
        if (target is null) return Results.NoContent();

        var forward = await ctx.Relationships.FirstOrDefaultAsync(r =>
            r.OwnerId == blocker.Id && r.TargetId == target.Id && r.Status == RelationshipStatus.Blocked);

        if (forward is null) return Results.NoContent();

        forward.Unblock();
        ctx.Relationships.Remove(forward);

        // Sweep the counterpart row too, but only if it is inert. A Blocked row there means the
        // other party blocked back, and that is theirs to lift.
        var reverse = await ctx.Relationships.FirstOrDefaultAsync(r => r.OwnerId == target.Id && r.TargetId == blocker.Id);
        if (reverse is { Status: RelationshipStatus.None })
        {
            reverse.RelatedId = null;
            ctx.Relationships.Remove(reverse);
        }

        await EvictProfilesAsync(cache, blocker.UserId, target.UserId);

        await bus.PublishAsync(new UserUnblockedEvent { BlockerId = blocker.UserId, BlockedId = target.UserId });

        return Results.NoContent();
    }

    /// <summary>
    /// The caller's own block list. Scoped to rows the caller owns, which is also why there is no
    /// "who blocked me" counterpart and never will be.
    /// </summary>
    [WolverineGet("/api/v1/relationships/blocked")]
    public static async Task<IResult> ListBlockedAsync(
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        int? limit = null,
        string? cursor = null)
    {
        var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (callerUserId is null) return Results.Unauthorized();

        var blocker = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == callerUserId);
        if (blocker is null) return Results.BadRequest("Caller profile not found.");

        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == blocker.Id && r.Status == RelationshipStatus.Blocked);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out var curUpdatedAt, out var curId))
                return Results.BadRequest("Malformed cursor.");

            // Keyset over (updated_at, id) descending. Ordering on UpdatedAt rather than Id
            // because a block placed on an existing friendship reuses that friendship's row, whose
            // id was minted when the friendship was - so id order is not block order.
            query = query.Where(r =>
                r.UpdatedAt < curUpdatedAt ||
                (r.UpdatedAt == curUpdatedAt && string.Compare(r.Id, curId) < 0));
        }

        var rows = await query
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new
            {
                r.Id,
                r.UpdatedAt,
                r.TargetId,
                r.Target.UserId,
                r.Target.UserName,
            })
            .ToListAsync();

        var hasMore = rows.Count > pageSize;
        var page = hasMore ? rows.Take(pageSize).ToList() : rows;
        var last = page.LastOrDefault();

        return Results.Ok(new BlockedUsersPageDto
        {
            Blocked = page.Select(r => new BlockedUserDto
            {
                RelationshipId = r.Id,
                ProfileId = r.TargetId,
                UserId = r.UserId,
                UserName = r.UserName,
                AvatarUrl = $"https://api.venta.gg/api/v1/social/profiles/{r.TargetId}/avatar",
                BlockedAt = r.UpdatedAt,
            }).ToList(),
            NextCursor = hasMore && last is not null ? EncodeCursor(last.UpdatedAt, last.Id) : null,
        });
    }

    private static Task EvictProfilesAsync(IDistributedCache cache, params string[] userIds)
        => Task.WhenAll(userIds.Select(id => cache.RemoveAsync($"integration_profile:user_id:{id}")));

    internal static string EncodeCursor(DateTimeOffset updatedAt, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updatedAt.UtcTicks}|{id}"));

    internal static bool TryDecodeCursor(string cursor, out DateTimeOffset updatedAt, out string id)
    {
        updatedAt = default;
        id = string.Empty;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks)) return false;

            updatedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            id = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
