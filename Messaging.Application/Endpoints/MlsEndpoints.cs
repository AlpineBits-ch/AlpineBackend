using System.Security.Claims;
using Domain;
using Echo.Realtime;
using Echo.Realtime.Devices;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Contracts.Bus.Events;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

/// <summary>
/// Transport for the two MLS artifacts that are not application messages - commits (which advance
/// the group's epoch for everyone) and Welcomes (which admit one new device) - plus the switch that
/// turns encryption on and off for a context.
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

    // ══════════════════════════════════════════════════════════════════════════ Conversations
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
    /// client should advance its group state.
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

        return Results.Ok(await mls.GetStateAsync(conversationId, userId));
    }

    /// <summary>
    /// Which devices can read this conversation, asked after the fact rather than only in the
    /// response to the write that formed the group.
    /// </summary>
    [WolverineGet("/api/v1/conversations/{conversationId}/mls/coverage")]
    public static async Task<IResult> GetConversationMlsCoverage(
        string conversationId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return Results.Ok(await mls.GetCoverageAsync(conversationId, conversationId, userId));
    }

    /// <summary>
    /// Turns encryption on for a conversation, or re-keys it by minting the next generation.
    /// </summary>
    [WolverinePost("/api/v1/conversations/{conversationId}/mls/enable")]
    public static async Task<IResult> EnableConversationMls(
        string conversationId,
        EnableMlsDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return ToHttp(await mls.EnableAsync(
            conversationId, conversationId, null, userId, dto, DateTimeOffset.UtcNow,
            ActivatingDevice(http)));
    }

    /// <summary>The device that built the group being published, from <c>X-Device-Id</c>.</summary>
    private static string? ActivatingDevice(HttpContext http)
    {
        var header = http.Request.Headers[DeviceIdentity.HeaderName].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }

    /// <summary>Turns encryption off for a conversation.</summary>
    [WolverinePost("/api/v1/conversations/{conversationId}/mls/disable")]
    public static async Task<IResult> DisableConversationMls(
        string conversationId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return ToHttp(await mls.DisableAsync(conversationId, conversationId, null, userId, DateTimeOffset.UtcNow));
    }

    // ══════════════════════════════════════════════════════════════════════════ Guild channels
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Turns encryption on for a channel.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/enable")]
    public static async Task<IResult> EnableChannelMls(
        string channelId,
        EnableMlsDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await CanManageChannel(bus, userId, channelId)) return Results.Forbid();

        var result = await mls.EnableAsync(
            channelId, null, channelId, userId, dto, DateTimeOffset.UtcNow, ActivatingDevice(http));

        return ToHttp(result);
    }

    /// <summary>Turns encryption off for a channel.</summary>
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

    /// <summary>Encryption state of a channel.</summary>
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

        return Results.Ok(await mls.GetStateAsync(channelId, userId));
    }

    /// <summary>Which of the caller's devices can read this channel.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/mls/coverage")]
    public static async Task<IResult> GetChannelMlsCoverage(
        string channelId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        return Results.Ok(await mls.GetCoverageAsync(channelId, null, userId));
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

    // ══════════════════════════════════════════════════════════════════════════ Join requests

    /// <summary>Asks to be let into an encrypted channel.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/join-requests")]
    public static async Task<IResult> RequestChannelAccess(
        string channelId,
        SubmitJoinRequestDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] DeviceIdResolver devices,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        if (await OwnsDeviceAsync(devices, userId, dto.DeviceId) is { } deviceError)
            return Results.BadRequest(deviceError);

        var result = await joinRequests.SubmitAsync(
            channelId, null, channelId, userId, dto, DateTimeOffset.UtcNow);

        if (result.Status == MlsOperationStatus.Ok)
        {
            // Members need to see the badge without polling.
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

        return Results.Ok(await joinRequests.ListPendingAsync(
            channelId, DateTimeOffset.UtcNow, MlsContextKind.Channel, userId));
    }

    /// <summary>Vouches for a request.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/mls/join-requests/{requestId}/approve")]
    public static async Task<IResult> ApproveChannelJoinRequest(
        string channelId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] IMessageBus bus,
        [NotBody] DeviceIdResolver devices,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await HasChannelPermission(bus, userId, channelId, ExternalPermission.ViewChannel))
            return Results.Forbid();

        var approverDeviceId = await devices.ResolveVerifiedAsync(http.Request, userId);

        return ToHttp(await joinRequests.ApproveAsync(
            channelId, requestId, userId, DateTimeOffset.UtcNow, MlsContextKind.Channel, approverDeviceId));
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

    /// <summary>Withdraws your own request.</summary>
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

    // ══════════════════════════════════════════════════════════════════════════ Conversation join
    // requests

    /// <summary>Asks to be let into an encrypted conversation.</summary>
    [WolverinePost("/api/v1/conversations/{conversationId}/mls/join-requests")]
    public static async Task<IResult> RequestConversationAccess(
        string conversationId,
        SubmitJoinRequestDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] DeviceIdResolver devices,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        if (await OwnsDeviceAsync(devices, userId, dto.DeviceId) is { } deviceError)
            return Results.BadRequest(deviceError);

        var protectionLevel = await ResolveProtectionLevelAsync(bus, userId);

        var result = await joinRequests.SubmitAsync(
            conversationId, conversationId, null, userId, dto, DateTimeOffset.UtcNow,
            MlsContextKind.Conversation, protectionLevel);

        if (result.Status == MlsOperationStatus.Ok && result.Value is MlsJoinRequestDto request)
        {
            // Everyone in the conversation, the requester's own account included.
            var audience = await ctx.Members
                .Where(m => m.ConversationId == conversationId)
                .Select(m => m.UserId)
                .ToListAsync();

            if (audience.Count > 0)
            {
                // Deliberately no KeyPackage.
                await hub.Clients.Users(audience).SendAsync("conversation.MlsJoinRequest", new
                {
                    contextId = conversationId,
                    conversationId,
                    requestId = request.Id,
                    requesterUserId = userId,
                    requesterDeviceId = dto.DeviceId,
                    requesterDeviceName = dto.DeviceName,
                    generation = request.Generation,
                    // The fingerprint is the value a human actually compares out of band; the
                    // key-package hash changes every request and cannot be read over a phone call.
                    signatureKeyFingerprint = dto.SignatureKeyFingerprint,
                    requiresManualApproval = request.RequiresManualApproval,
                });
            }
        }

        return ToHttp(result);
    }

    /// <summary>
    /// Refuses a device id that is not one of the caller's own registered devices.
    /// </summary>
    private static async Task<string?> OwnsDeviceAsync(DeviceIdResolver devices, string userId, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "DeviceId is required";

        return await devices.IsRegisteredAsync(userId, deviceId, failOpen: false)
            ? null
            : $"'{deviceId}' is not one of your registered devices. Register it first, and note that "
              + "a device id is only ever yours - admission is per device of your own account.";
    }

    [WolverineGet("/api/v1/conversations/{conversationId}/mls/join-requests")]
    public static async Task<IResult> ListConversationJoinRequests(
        string conversationId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return Results.Ok(await joinRequests.ListPendingAsync(
            conversationId, DateTimeOffset.UtcNow, MlsContextKind.Conversation, userId));
    }

    /// <summary>Vouches for a request.</summary>
    [WolverinePost("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/approve")]
    public static async Task<IResult> ApproveConversationJoinRequest(
        string conversationId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] DeviceIdResolver devices,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        var approverDeviceId = await devices.ResolveVerifiedAsync(http.Request, userId);

        return ToHttp(await joinRequests.ApproveAsync(
            conversationId, requestId, userId, DateTimeOffset.UtcNow, MlsContextKind.Conversation,
            approverDeviceId));
    }

    [WolverinePost("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/deny")]
    public static async Task<IResult> DenyConversationJoinRequest(
        string conversationId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return ToHttp(await joinRequests.DenyAsync(conversationId, requestId, userId, DateTimeOffset.UtcNow));
    }

    /// <summary>Withdraws your own request.</summary>
    [WolverineDelete("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}")]
    public static async Task<IResult> CancelConversationJoinRequest(
        string conversationId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        return ToHttp(await joinRequests.CancelAsync(conversationId, requestId, userId, DateTimeOffset.UtcNow));
    }

    // ══════════════════════════════════════════════════════════════════════════ Admission proof
    // relay

    /// <summary>An existing device posts a 32-byte nonce for the joining device to sign.</summary>
    [WolverinePost("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/challenge")]
    public static async Task<IResult> IssueAdmissionChallenge(
        string conversationId,
        string requestId,
        IssueAdmissionChallengeDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] DeviceIdResolver devices,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        // Validated, not merely read: the issuer id decides whose challenge may be superseded, so an
        // unchecked header would hand back the displacement primitive that scoping removed.
        var issuerDeviceId = await devices.ResolveVerifiedAsync(http.Request, userId);

        return ToHttp(await joinRequests.IssueChallengeAsync(
            conversationId, requestId, userId, issuerDeviceId, dto, DateTimeOffset.UtcNow));
    }

    /// <summary>The joining device collects the outstanding nonce.</summary>
    [WolverineGet("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/challenge")]
    public static async Task<IResult> GetAdmissionChallenge(
        string conversationId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return ToHttp(await joinRequests.GetChallengeAsync(conversationId, requestId, userId, DateTimeOffset.UtcNow));
    }

    /// <summary>The joining device submits its signature. Stored verbatim; never checked here.</summary>
    [WolverinePost("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/proof")]
    public static async Task<IResult> SubmitAdmissionProof(
        string conversationId,
        string requestId,
        SubmitAdmissionProofDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        return ToHttp(await joinRequests.SubmitProofAsync(
            conversationId, requestId, userId, dto, DateTimeOffset.UtcNow));
    }

    /// <summary>The proof, for the device that issued the challenge to verify locally.</summary>
    [WolverineGet("/api/v1/conversations/{conversationId}/mls/join-requests/{requestId}/proof")]
    public static async Task<IResult> GetAdmissionProof(
        string conversationId,
        string requestId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] DeviceIdResolver devices,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] MlsJoinRequestService joinRequests)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        var callerDeviceId = await devices.ResolveVerifiedAsync(http.Request, userId);

        return ToHttp(await joinRequests.GetProofAsync(
            conversationId, requestId, userId, callerDeviceId, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The requester's protection level, or the strict one when Identity cannot answer.
    /// </summary>
    private static async Task<ProtectionLevel> ResolveProtectionLevelAsync(IMessageBus bus, string userId)
    {
        try
        {
            var response = await bus.InvokeAsync<GetUserProtectionLevelResponse>(
                new GetUserProtectionLevelRequest { UserIds = [userId] });

            return response.Levels.FirstOrDefault(l => l.UserId == userId)?.Level
                   ?? ProtectionLevel.VerifiedDevices;
        }
        catch (Exception)
        {
            return ProtectionLevel.VerifiedDevices;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Welcomes (context-agnostic - a device fetches everything waiting for it)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Welcomes waiting for one device, across every context.</summary>
    [WolverineGet("/api/v1/conversations/welcomes")]
    public static async Task<IResult> GetWelcomes(
        [FromQuery] string? deviceId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var query = ctx.PendingWelcomes.AsNoTracking()
            .Where(w => w.UserId == userId && w.ConsumedAt == null);

        if (!string.IsNullOrWhiteSpace(deviceId)) query = query.Where(w => w.DeviceId == deviceId);

        var welcomes = await query.OrderBy(w => w.CreatedAt).ToListAsync();

        return Results.Ok(welcomes.SelectFacets<PendingWelcome, PendingWelcomeDto>());
    }

    /// <summary>Marks Welcomes consumed once the device has actually joined their groups.</summary>
    [WolverinePost("/api/v1/conversations/welcomes/ack")]
    public static async Task<IResult> AckWelcomes(
        AckWelcomesDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] HttpContext http,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        if (dto.WelcomeIds.Count == 0) return Results.Ok(new AckWelcomesResultDto { Acknowledged = 0 });

        var deviceId = dto.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = http.Request.Headers[DeviceIdentity.HeaderName].ToString();
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Results.BadRequest(
                "deviceId is required, either in the body or as the X-Device-Id header. A Welcome is " +
                "sealed to one device's leaf, and acknowledging across all of a user's devices " +
                "destroys the others' only way into the group.");
        }

        // Scoped to the caller *and* the device, so an id guessed from another stream is a no-op
        // rather than a denial of service that strands somebody else's device outside the group.
        var welcomes = await ctx.PendingWelcomes
            .Where(w => dto.WelcomeIds.Contains(w.Id)
                        && w.UserId == userId
                        && w.DeviceId == deviceId
                        && w.ConsumedAt == null)
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
