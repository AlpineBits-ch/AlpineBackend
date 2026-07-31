using System.Security.Claims;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Contracts.Bus.Events;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

/// <summary>
/// Transport for the two MLS artifacts that are not application messages - commits (which advance
/// the group's epoch for everyone) and Welcomes (which admit one new device) - plus the switch that
/// turns encryption on and off for a context.
///
/// <para><b>The realtime push is a nudge, not the payload.</b> Fanout sends only
/// (contextId, generation, epoch); the client then GETs every commit above its own local epoch and
/// applies them in order. Pushing the commit bytes inline would be one round-trip faster and would
/// invite clients to apply commits in SignalR delivery order, which is not guaranteed across a
/// reconnect - and an MLS client that applies commits out of order is forked off the group
/// permanently. Making the ordered GET the only path that mutates group state means a dropped or
/// duplicated push costs a round-trip and nothing else.</para>
///
/// <para>Conversation and channel routes are separate because their authorization genuinely differs
/// - conversation membership versus channel permissions - but both are thin wrappers over
/// <see cref="MlsGroupService"/>, which is where the lifecycle actually lives.</para>
/// </summary>
[Authorize]
public class MlsEndpoints
{
    /// <inheritdoc cref="MlsGroupService.CommitRetention"/>
    public static TimeSpan CommitRetention => MlsGroupService.CommitRetention;

    /// <inheritdoc cref="MlsGroupService.MaxCommitPageSize"/>
    public const int MaxCommitPageSize = MlsGroupService.MaxCommitPageSize;

    private static IResult ToHttp(MlsOperationResult result) => result.Status switch
    {
        MlsOperationStatus.Ok => Results.Ok(result.Value),
        MlsOperationStatus.NotFound => Results.NotFound(result.Error),
        MlsOperationStatus.Conflict => Results.Conflict(result.Value),
        _ => Results.BadRequest(result.Error),
    };

    // ══════════════════════════════════════════════════════════════════════════
    // Conversations
    // ══════════════════════════════════════════════════════════════════════════

    [WolverinePost("/api/v1/conversations/{conversationId}/mls/commits")]
    public static async Task<IResult> PublishCommit(
        string conversationId,
        PublishMlsCommitDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        var conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation is null) return Results.NotFound();

        var memberIds = await ctx.Members
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.UserId)
            .ToListAsync();

        var result = await mls.PublishCommitAsync(
            conversationId, conversationId, null, userId, dto, memberIds, DateTimeOffset.UtcNow);

        return ToHttp(result);
    }

    /// <summary>
    /// Every commit above <paramref name="sinceEpoch"/>, in epoch order - the only path by which a
    /// client should advance its group state. A device that just joined passes the epoch its Welcome
    /// landed it on; a device coming back online passes its own last applied epoch.
    /// </summary>
    [WolverineGet("/api/v1/conversations/{conversationId}/mls/commits")]
    public static async Task<IResult> GetCommits(
        string conversationId,
        [FromQuery] long sinceEpoch,
        [FromQuery] int? generation,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return Results.Ok(await mls.GetCommitsAsync(conversationId, generation, sinceEpoch));
    }

    [WolverineGet("/api/v1/conversations/{conversationId}/mls/state")]
    public static async Task<IResult> GetConversationMlsState(
        string conversationId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return Results.Ok(await mls.GetStateAsync(conversationId));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Guild channels
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Turns encryption on for a channel. Requires ManageChannel - this changes what everyone in the
    /// channel can read, which is a moderation decision and not something any member should be able
    /// to do to the room.
    ///
    /// <para>Plaintext history already in the channel is untouched and stays readable. This marks a
    /// boundary in the channel's timeline; it does not convert anything.</para>
    /// </summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/enable")]
    public static async Task<IResult> EnableChannelMls(
        string channelId,
        EnableMlsDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await CanManageChannel(bus, userId, channelId)) return Results.Forbid();

        var result = await mls.EnableAsync(channelId, null, channelId, userId, dto, DateTimeOffset.UtcNow);
        return ToHttp(result);
    }

    /// <summary>
    /// Turns encryption off for a channel. Requires ManageChannel.
    ///
    /// <para>Messages sent while it was on stay ciphertext - they are not decrypted, rewritten or
    /// deleted. The response names the terminated generation so the caller can say exactly which
    /// stretch of history is now readable only by devices that still hold that group's keys.</para>
    /// </summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/disable")]
    public static async Task<IResult> DisableChannelMls(
        string channelId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await CanManageChannel(bus, userId, channelId)) return Results.Forbid();

        var result = await mls.DisableAsync(channelId, null, channelId, userId, DateTimeOffset.UtcNow);
        return ToHttp(result);
    }

    /// <summary>Encryption state of a channel. Only needs ViewChannel - anyone who can read the
    /// channel needs to know whether to encrypt, and which generation to encrypt under.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/mls/state")]
    public static async Task<IResult> GetChannelMlsState(
        string channelId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        return Results.Ok(await mls.GetStateAsync(channelId));
    }

    /// <summary>Publishing a commit needs only ViewChannel: adding and removing group members tracks
    /// who can see the channel, and every member has to be able to do it or the group cannot follow
    /// the roster. Who may <i>toggle</i> encryption is the privileged question, and that is gated
    /// separately on ManageChannel.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/commits")]
    public static async Task<IResult> PublishChannelCommit(
        string channelId,
        PublishMlsCommitDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        // Channel membership lives in Guild, so the nudge goes out via ChannelMlsStateChanged's
        // sibling path inside the service rather than to a user list resolved here.
        var result = await mls.PublishCommitAsync(
            channelId, null, channelId, userId, dto, [], DateTimeOffset.UtcNow);

        return ToHttp(result);
    }

    [WolverineGet("/api/v1/channels/{channelId}/mls/commits")]
    public static async Task<IResult> GetChannelCommits(
        string channelId,
        [FromQuery] long sinceEpoch,
        [FromQuery] int? generation,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        return Results.Ok(await mls.GetCommitsAsync(channelId, generation, sinceEpoch));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Join requests
    //
    // The server holds no group keys, so it cannot admit anyone - only a current member can produce
    // an Add commit. Admission is therefore a request that members review, and the approval that
    // meets the threshold prompts that member's client to mint the Welcome.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Asks to be let into an encrypted channel. Needs ViewChannel: you must be able to see
    /// the channel to ask for its contents.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/join-requests")]
    public static async Task<IResult> RequestChannelAccess(
        string channelId,
        SubmitJoinRequestDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        var result = await joinRequests.SubmitAsync(
            channelId, null, channelId, userId, dto, DateTimeOffset.UtcNow);

        if (result.Status == MlsOperationStatus.Ok)
        {
            // Members need to see the badge without polling. Guild owns channel membership, so the
            // announcement goes the same way channel messages already do.
            await bus.PublishAsync(new ChannelMlsJoinRequested
            {
                ChannelId = channelId,
                RequesterUserId = userId,
            });
        }

        return ToHttp(result);
    }

    /// <summary>The review queue for a channel. ViewChannel, because any member may vouch.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/mls/join-requests")]
    public static async Task<IResult> ListChannelJoinRequests(
        string channelId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        return Results.Ok(await joinRequests.ListPendingAsync(channelId, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Vouches for a request. Any current member may - only someone who can already read the
    /// channel is in a position to let someone else in, and gating this on moderators would leave a
    /// channel unable to admit anyone whenever its moderators are away.
    /// </summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/join-requests/{requestId}/approve")]
    public static async Task<IResult> ApproveChannelJoinRequest(
        string channelId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        return ToHttp(await joinRequests.ApproveAsync(channelId, requestId, userId, DateTimeOffset.UtcNow));
    }

    [WolverinePost("/api/v1/channels/{channelId}/mls/join-requests/{requestId}/deny")]
    public static async Task<IResult> DenyChannelJoinRequest(
        string channelId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        return ToHttp(await joinRequests.DenyAsync(channelId, requestId, userId, DateTimeOffset.UtcNow));
    }

    /// <summary>Withdraws your own request. No permission check beyond ownership - the service
    /// reports someone else's request as not-found rather than confirming it exists.</summary>
    [WolverineDelete("/api/v1/channels/{channelId}/mls/join-requests/{requestId}")]
    public static async Task<IResult> CancelChannelJoinRequest(
        string channelId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        return ToHttp(await joinRequests.CancelAsync(channelId, requestId, userId, DateTimeOffset.UtcNow));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Welcomes (context-agnostic - a device fetches everything waiting for it)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Welcomes waiting for one device, across every context.
    ///
    /// <para>Passing <paramref name="deviceId"/> selects the correct behaviour: only that device's
    /// Welcomes, and reading does not consume them - see <see cref="PendingWelcome"/> for why
    /// consuming on read loses a single-use init key whenever the join fails.</para>
    ///
    /// <para>Omitting it is the pre-existing contract and keeps the pre-existing semantics: all of
    /// the user's Welcomes, consumed as they are read. That is the lossy behaviour this endpoint
    /// exists to replace, but a client that has not been updated has no ack call to make, and
    /// switching it to non-destructive would leave it re-fetching and re-failing the same joins on
    /// every launch, forever. Old contract, old semantics; new contract, correct semantics.</para>
    /// </summary>
    [WolverineGet("/api/v1/conversations/welcomes")]
    public static async Task<IResult> GetWelcomes(
        [FromQuery] string? deviceId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var legacyCaller = string.IsNullOrWhiteSpace(deviceId);

        var query = ctx.PendingWelcomes.Where(w => w.UserId == userId && w.ConsumedAt == null);
        if (!legacyCaller) query = query.Where(w => w.DeviceId == deviceId);

        var welcomes = await query.OrderBy(w => w.CreatedAt).ToListAsync();

        if (legacyCaller && welcomes.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var welcome in welcomes) welcome.ConsumedAt = now;
            await ctx.SaveChangesAsync();
        }

        return Results.Ok(welcomes.SelectFacets<PendingWelcome, PendingWelcomeDto>());
    }

    /// <summary>Marks Welcomes consumed once the device has actually joined their groups.</summary>
    [WolverinePost("/api/v1/conversations/welcomes/ack")]
    public static async Task<IResult> AckWelcomes(
        AckWelcomesDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        if (dto.WelcomeIds.Count == 0) return Results.Ok(new AckWelcomesResultDto { Acknowledged = 0 });

        // Scoped to the caller so an id guessed from another user's stream is a no-op, not a denial
        // of service that strands someone else's device outside the group.
        var welcomes = await ctx.PendingWelcomes
            .Where(w => dto.WelcomeIds.Contains(w.Id) && w.UserId == userId && w.ConsumedAt == null)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var welcome in welcomes) welcome.ConsumedAt = now;

        await ctx.SaveChangesAsync();

        return Results.Ok(new AckWelcomesResultDto { Acknowledged = welcomes.Count });
    }

    // ══════════════════════════════════════════════════════════════════════════

    private static Task<bool> CanManageChannel(IMessageBus bus, string userId, string channelId) =>
        HasChannelPermission(bus, userId, channelId, ExternalPermission.ManageChannel);

    private static async Task<bool> HasChannelPermission(
        IMessageBus bus, string userId, string channelId, ExternalPermission permission)
    {
        var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest
            {
                ChannelId = channelId,
                UserId = userId,
                Permission = permission,
            });

        return response.IsAllowed;
    }
}
