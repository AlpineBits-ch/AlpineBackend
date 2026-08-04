using System.Security.Claims;
using Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;
using Wolverine.Http;

namespace Social.Api.Endpoints;


[Authorize]
public static class FriendshipEndpoints
{
    /// <summary>
    /// The single refusal a would-be requester sees, whatever actually stopped them (privacy spec
    /// T2-15 and cross-cutting rule 5). No such username, username lookup switched off, the target
    /// blocked them, or the target's <c>FriendRequestPolicy</c> excludes them all land here with the
    /// same status and the same body - anything finer is an account-enumeration oracle and a
    /// block detector.
    /// </summary>
    private static IResult PolicyRefusal() =>
        Results.Json(new RefusalDto(RefusalCodes.FriendRequestPolicy), statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Creates a friend request, subject to blocking (T0-3), the target's discoverability (T2-16) and
    /// the target's friend-request policy (T2-15).
    ///
    /// <para>Note the direction: every gate is read off the <i>target's</i> record, because the
    /// question is whether the target is willing to be reached, not whether the caller is willing to
    /// reach out.</para>
    /// </summary>
    [WolverinePost("/api/v1/relationships")]
    public static async Task<IResult> CreateAsync(
        CreateFriendshipDto dto,
        [NotBody] MicroserviceContext ctx,
        [NotBody] UserDirectory directory,
        [NotBody] ISharedGuildResolver sharedGuilds,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var initiator = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        // About the caller's own account, so it reveals nothing about anybody else - stays a 400.
        if (initiator is null) return Results.BadRequest("Initiator profile not found.");

        // Answered before the directory is consulted, and by string rather than by id: the caller
        // supplied their own username, so this reveals nothing, and a user who switched their own
        // discoverability off must not become invisible to themselves and get a policy refusal where
        // the truthful answer is "that is you".
        if (string.Equals(dto.UserName, initiator.UserName, StringComparison.Ordinal))
            return Results.BadRequest("You cannot friend yourself.");

        // T2-16. The directory resolves and gates in one step, so a username nobody holds and a
        // username whose holder has switched off username discoverability are the same null here and
        // land on the same refusal - a caller able to tell them apart has an enumeration oracle.
        // Callers relying on the old 400 body should switch to the code.
        var match = await directory.FindAsync(DirectoryKey.Username, dto.UserName);
        if (match is null) return PolicyRefusal();

        var targetProfile = match.Profile;
        var targetSettings = match.Settings;

        var (initiatorBlockedTarget, targetBlockedInitiator) =
            await ctx.BlockStateAsync(initiator.Id, targetProfile.Id);

        // The one place a block may be named: the caller is the blocker, so they already know.
        // Telling them keeps the client able to say "unblock them first" instead of showing a
        // policy refusal they cannot act on.
        if (initiatorBlockedTarget)
            return Results.Json(new RefusalDto(RefusalCodes.Blocked), statusCode: StatusCodes.Status403Forbidden);

        // The other direction is never named - see PolicyRefusal.
        if (targetBlockedInitiator) return PolicyRefusal();

        // Existence of a relationship is something the caller already knows about (they are on one
        // side of it), so a 409 here leaks nothing the caller could not read off GET /relationships.
        var existing = await ctx.Relationships.AnyAsync(r =>
            r.OwnerId == initiator.Id && r.TargetId == targetProfile.Id);

        if (existing) return Results.Conflict("Relationship already exists.");

        if (!await IsAdmittedByPolicyAsync(ctx, sharedGuilds, initiator, targetProfile, targetSettings))
            return PolicyRefusal();

        var relationships = Relationship.Create(new CreateRelationshipParams
        {
            Subject = targetProfile.Id,
            Initiator = initiator.Id 
        });



        var first = relationships.First();
        var second = relationships.Last();

        // Relationship.Create() cross-links RelatedId (first -> second, second -> first) - a
        // circular FK within the same table. Nulling first.RelatedId lets it insert on its own
        // (breaking the cycle) before second (which validly points at first's now-persisted id)
        // is added; first.RelatedId is then restored and requires its own save.
        first.RelatedId = null;

        ctx.Relationships.Add(first);
        await ctx.SaveChangesAsync();

        ctx.Relationships.Add(second);
        first.RelatedId = second.Id;

        // No second SaveChangesAsync here - WolverineHttp endpoints auto-commit the ambient
        // DbContext once the endpoint returns (opts.Policies.AutoApplyTransactions(), same as bus
        // handlers), so `second`'s insert and `first.RelatedId`'s update land in that commit.
        return Results.Ok();
    }

    /// <summary>
    /// T2-15's table, evaluated against the target's policy.
    ///
    /// <para><c>Everyone</c> is the shipped default and matches Discord - the block list, not this
    /// setting, is the escape hatch for the common case. An unrecognised value (a newer Identity
    /// than this build knows about) refuses.</para>
    /// </summary>
    private static async Task<bool> IsAdmittedByPolicyAsync(
        MicroserviceContext ctx,
        ISharedGuildResolver sharedGuilds,
        Profile initiator,
        Profile target,
        Identity.Contracts.Bus.Response.UserPrivacySettingsSummary targetSettings)
        => targetSettings.FriendRequestPolicy switch
        {
            FriendRequestPolicy.Everyone => true,
            FriendRequestPolicy.FriendsOfFriends => await ctx.HasMutualFriendAsync(initiator.Id, target.Id),
            // Resolves to false today: Guild exposes no "guilds user X is in" contract yet, and
            // NoSharedGuildResolver answers restrictively rather than guessing. See
            // ISharedGuildResolver.
            FriendRequestPolicy.ServerMembers => await sharedGuilds.ShareAnyGuildAsync(initiator.UserId, target.UserId),
            FriendRequestPolicy.Nobody => false,
            _ => false,
        };


    [WolverinePost("/api/v1/relationships/{id}/accept")]
    public static async Task<IResult> AcceptAsync(
        string id, 
        [NotBody]MicroserviceContext ctx,
        [NotBody] IDistributedCache cache,
        ClaimsPrincipal user)
    {
        var friendship = await ctx.Relationships
            .Include(r => r.Related)
            .Include(r => r.Owner)
            .Include(r => r.Target)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (friendship == null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var currentProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (friendship.OwnerId != currentProfile?.Id)
            return Results.Forbid();

        // Accept() is a no-op once the pair is already Friends, so a double accept (double tap,
        // client retry, two devices) can't raise a second FriendRequestAccepted and therefore
        // can't fan out a duplicate social.FriendRequestAccepted push. Returning early keeps the
        // response idempotent too, and stops a rejected/removed (None) request being resurrected
        // into a friendship without a fresh request.
        if (!friendship.Accept())
            return friendship.Status == RelationshipStatus.Friends
                ? Results.Accepted()
                : Results.BadRequest("Relationship is not a pending friend request.");

        // Null for a federation-materialized relationship - only the local half of those is
        // persisted, so there is no mirrored row to flip. AcceptCounterpart rather than Accept:
        // the mirrored row is PendingOutgoing, and moving it to Friends is only legitimate as a
        // consequence of the accept above (see Relationship.Accept).
        friendship.Related?.AcceptCounterpart();

        var firstUserCache = $"integration_profile:user_id:{friendship.Target.UserId}";
        var secondUserCache = $"integration_profile:user_id:{friendship.Owner.UserId}";

        await cache.RemoveAsync(firstUserCache);
        await cache.RemoveAsync(secondUserCache);

        return Results.Accepted();
    }

    [WolverinePost("/api/v1/relationships/{id}/reject")]
    public static async Task<IResult> RejectAsync(
        string id,
        [NotBody]MicroserviceContext ctx,
        ClaimsPrincipal user)
    {
        var friendship = await ctx.Relationships
            .Include(r => r.Related)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (friendship == null) return Results.NotFound();

        // Same ownership gate as AcceptAsync. Without it, knowing a relationship id was enough to
        // reject anybody's pending request - the row was only checked for existence, never for
        // whose it was.
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var currentProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (friendship.OwnerId != currentProfile?.Id)
            return Results.Forbid();

        // Already cleared - stay idempotent rather than raising a second FriendRequestRejected
        // and pushing social.FriendRequestRejected twice.
        if (!friendship.Reject()) return Results.Accepted();

        // Mirrors AcceptAsync/RevokeAsync, which both update the Related side too - without this
        // the initiator's own (PendingOutgoing) row was left stuck forever, so a rejected request
        // kept showing as "pending" to the initiator even though the recipient had rejected it.
        friendship.Related?.Reject();

        return Results.Accepted();
    }

    [WolverinePost("/api/v1/relationships/{id}/revoke")]
    public static async Task<IResult> RevokeAsync(
        string id,
        [NotBody]MicroserviceContext ctx,
        [NotBody] IDistributedCache cache,
        ClaimsPrincipal user)
    {
        var friendship = await ctx.Relationships
            .Include(r => r.Related)
            .Include(r => r.Owner)
            .Include(r => r.Target)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (friendship == null) return Results.NotFound();

        // Same ownership gate as AcceptAsync. This endpoint covers both "revoke my pending
        // request" and "unfriend", so without the check a relationship id was enough to tear down
        // any two users' friendship - which also silently revoked their ability to DM and call
        // each other, since both are gated on friendship.
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var currentProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (friendship.OwnerId != currentProfile?.Id)
            return Results.Forbid();

        // Same idempotency guard as Accept/Reject - a repeated revoke must not re-raise
        // FriendRemoved and push social.FriendRemoved a second time.
        if (!friendship.Remove()) return Results.Ok();

        friendship.Related?.Remove();

        // Mirrors AcceptAsync: the integration profile (which carries the friend list Messaging
        // reads to gate DMs and calls) is cached for 10 minutes, so without busting it here an
        // unfriend stayed ineffective for up to 10 minutes.
        await cache.RemoveAsync($"integration_profile:user_id:{friendship.Target.UserId}");
        await cache.RemoveAsync($"integration_profile:user_id:{friendship.Owner.UserId}");

        return Results.Ok();
    }
}