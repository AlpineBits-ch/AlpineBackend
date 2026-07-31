using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

/// <summary>Returned on 409 so a client that lost the race to commit knows exactly where the group
/// actually is and can catch up from there instead of guessing.</summary>
public class MlsEpochConflictDto
{
    public long CurrentEpoch { get; set; }
    public long RejectedEpoch { get; set; }
    public string Reason { get; set; } = null!;
}

/// <summary>
/// Transport for the two MLS artifacts that are not application messages: commits (which advance
/// the group's epoch for everyone) and Welcomes (which admit one new device).
/// </summary>
[Authorize]
public class MlsEndpoints
{
    /// <summary>How long a commit stays fetchable.</summary>
    public static readonly TimeSpan CommitRetention = TimeSpan.FromDays(30);

    /// <summary>Cap on one catch-up page.</summary>
    public const int MaxCommitPageSize = 200;

    [WolverinePost("/api/v1/conversations/{conversationId}/mls/commits")]
    public static async Task<IResult> PublishCommit(
        string conversationId,
        PublishMlsCommitDto dto,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ConversationPermissionService permissions,
        [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (dto.Commit is null || dto.Commit.Length == 0)
            return Results.BadRequest("Commit is required");
        if (string.IsNullOrWhiteSpace(dto.SenderDeviceId))
            return Results.BadRequest("SenderDeviceId is required");

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        var conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation is null) return Results.NotFound();
        if (conversation.EncryptionState != ChannelEncryptionState.Encrypted)
            return Results.BadRequest("Conversation is not end-to-end encrypted");

        var currentEpoch = conversation.MlsEpoch ?? 0;
        if (dto.Epoch != currentEpoch + 1)
        {
            return Results.Conflict(new MlsEpochConflictDto
            {
                CurrentEpoch = currentEpoch,
                RejectedEpoch = dto.Epoch,
                Reason = $"Expected epoch {currentEpoch + 1}; catch up and re-issue the change.",
            });
        }

        // Belt to the unique index's braces.
        var epochTaken = await ctx.MlsCommits
            .AnyAsync(c => c.ContextId == conversationId && c.Epoch == dto.Epoch);
        if (epochTaken)
        {
            return Results.Conflict(new MlsEpochConflictDto
            {
                CurrentEpoch = currentEpoch,
                RejectedEpoch = dto.Epoch,
                Reason = "Another member already committed this epoch.",
            });
        }

        ctx.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = conversationId,
            ConversationId = conversationId,
            Epoch = dto.Epoch,
            Commit = dto.Commit,
            SenderUserId = userId,
            SenderDeviceId = dto.SenderDeviceId,
        }));

        conversation.MlsEpoch = dto.Epoch;
        if (dto.GroupInfo is { Length: > 0 }) conversation.MlsGroupInfo = dto.GroupInfo;

        foreach (var welcome in dto.Welcomes)
        {
            if (welcome.Welcome is null || welcome.Welcome.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(welcome.DeviceId) || string.IsNullOrWhiteSpace(welcome.UserId)) continue;

            ctx.PendingWelcomes.Add(PendingWelcome.Create(new CreatePendingWelcomeParams
            {
                ContextId = conversationId,
                ConversationId = conversationId,
                UserId = welcome.UserId,
                DeviceId = welcome.DeviceId,
                Welcome = welcome.Welcome,
                Epoch = dto.Epoch,
            }));
        }

        var cutoff = DateTimeOffset.UtcNow - CommitRetention;
        var expired = await ctx.MlsCommits
            .Where(c => c.ContextId == conversationId && c.CreatedAt < cutoff)
            .ToListAsync();
        if (expired.Count > 0) ctx.MlsCommits.RemoveRange(expired);

        // Save before the fanout, not after.
        await ctx.SaveChangesAsync();

        var memberIds = await ctx.Members
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.UserId)
            .ToListAsync();

        await hub.Clients.Users(memberIds).SendAsync("conversation.MlsCommit", new
        {
            conversationId,
            epoch = dto.Epoch,
            senderDeviceId = dto.SenderDeviceId,
        });

        // Welcomes are addressed to one leaf, so they are pushed to one device - not to every
        // session the user has open.
        foreach (var welcome in dto.Welcomes)
        {
            if (string.IsNullOrWhiteSpace(welcome.UserId) || string.IsNullOrWhiteSpace(welcome.DeviceId)) continue;
            await hub.Clients
                .Group(EchoRealtimeHub.DeviceGroup(welcome.UserId, welcome.DeviceId))
                .SendAsync("conversation.Welcome", conversationId);
        }

        return Results.Ok(new MlsCommitPublishedDto { ConversationId = conversationId, Epoch = dto.Epoch });
    }

    /// <summary>
    /// Every commit above <paramref name="sinceEpoch"/>, in epoch order - the only path by which a
    /// client should advance its group state.
    /// </summary>
    [WolverineGet("/api/v1/conversations/{conversationId}/mls/commits")]
    public static async Task<IResult> GetCommits(
        string conversationId,
        [FromQuery] long sinceEpoch,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ConversationPermissionService permissions)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissions.HasPermission(userId, conversationId)) return Results.Forbid();

        var commits = await ctx.MlsCommits
            .AsNoTracking()
            .Where(c => c.ContextId == conversationId && c.Epoch > sinceEpoch)
            .OrderBy(c => c.Epoch)
            .Take(MaxCommitPageSize)
            .ToListAsync();

        return Results.Ok(commits.SelectFacets<MlsCommit, MlsCommitResponseDto>());
    }

    /// <summary>Welcomes waiting for one device.</summary>
    [WolverineGet("/api/v1/conversations/welcomes")]
    public static async Task<IResult> GetWelcomes(
        [FromQuery] string? deviceId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(deviceId)) return Results.BadRequest("deviceId is required");

        var welcomes = await ctx.PendingWelcomes
            .AsNoTracking()
            .Where(w => w.UserId == userId && w.DeviceId == deviceId && w.ConsumedAt == null)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();

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

        // Scoped to the caller so an id guessed from another user's stream is a no-op, not a
        // denial of service that strands someone else's device outside the group.
        var welcomes = await ctx.PendingWelcomes
            .Where(w => dto.WelcomeIds.Contains(w.Id) && w.UserId == userId && w.ConsumedAt == null)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var welcome in welcomes) welcome.ConsumedAt = now;

        await ctx.SaveChangesAsync();

        return Results.Ok(new AckWelcomesResultDto { Acknowledged = welcomes.Count });
    }
}
