using System.Security.Cryptography;
using Facet.Extensions;
using Messaging.Application.Dtos.Request;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Services;

/// <summary>
/// Admission to an encrypted context.
///
/// <para>The server holds no group keys, so it cannot let anyone in - only a current member can
/// produce an Add commit. Admission is therefore a request that members review, and the approval
/// that crosses the threshold is what prompts a member's client to mint the Welcome.</para>
/// </summary>
public class MlsJoinRequestService(MicroserviceContext ctx)
{
    /// <summary>
    /// How many approvals a request needs.
    ///
    /// <para>Two, except when the server has only ever seen a single person act on this group -
    /// the member who switched encryption on and nobody since. Demanding two there would make the
    /// group permanently unable to admit anyone, since the one member cannot approve twice.</para>
    ///
    /// <para>The count is derived from who activated the generation and who has published commits
    /// against it, both of which come from authenticated callers. A member cannot deflate it to let
    /// themselves solo-admit: forging a second actor is impossible, and if the set really does hold
    /// one person, that person could add anyone unilaterally anyway.</para>
    /// </summary>
    public async Task<int> RequiredApprovalsFor(string contextId, int generation)
    {
        var generationRow = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.Generation == generation);

        if (generationRow is null) return MlsJoinRequest.RequiredApprovals;

        var actors = await ctx.MlsCommits
            .AsNoTracking()
            .Where(c => c.ContextId == contextId && c.Generation == generation)
            .Select(c => c.SenderUserId)
            .Distinct()
            .ToListAsync();

        var known = actors.Append(generationRow.ActivatedByUserId).Distinct().Count();

        return known >= 2 ? MlsJoinRequest.RequiredApprovals : 1;
    }

    public static string HashKeyPackage(byte[] keyPackage) =>
        Convert.ToHexString(SHA256.HashData(keyPackage)).ToLowerInvariant();

    public async Task<MlsOperationResult> SubmitAsync(
        string contextId,
        string? conversationId,
        string? channelId,
        string requesterUserId,
        SubmitJoinRequestDto dto,
        DateTimeOffset now)
    {
        if (dto.KeyPackage is null || dto.KeyPackage.Length == 0)
            return MlsOperationResult.BadRequest("KeyPackage is required");
        if (string.IsNullOrWhiteSpace(dto.DeviceId))
            return MlsOperationResult.BadRequest("DeviceId is required");
        if (string.IsNullOrWhiteSpace(dto.SignatureKeyFingerprint))
            return MlsOperationResult.BadRequest("SignatureKeyFingerprint is required");

        var active = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

        if (active is null)
            return MlsOperationResult.BadRequest("This context is not encrypted; no request is needed.");

        var existing = await ctx.MlsJoinRequests
            .FirstOrDefaultAsync(r => r.ContextId == contextId
                                      && r.Generation == active.Generation
                                      && r.RequesterDeviceId == dto.DeviceId
                                      && r.State == MlsJoinRequestState.Pending);

        if (existing is not null)
        {
            // Idempotent for the same key. A different key from the same device replaces the old
            // request rather than stacking: reviewers should never be looking at two asks from one
            // device and have to work out which one they are vouching for.
            var incomingHash = HashKeyPackage(dto.KeyPackage);
            if (existing.KeyPackageHash == incomingHash)
                return MlsOperationResult.Ok(ToDto(existing, await RequiredApprovalsFor(contextId, active.Generation)));

            existing.State = MlsJoinRequestState.Cancelled;
            existing.UpdatedAt = now;
        }

        var request = MlsJoinRequest.Create(new CreateMlsJoinRequestParams
        {
            ContextId = contextId,
            ConversationId = conversationId,
            ChannelId = channelId,
            Generation = active.Generation,
            RequesterUserId = requesterUserId,
            RequesterDeviceId = dto.DeviceId,
            KeyPackage = dto.KeyPackage,
            KeyPackageHash = HashKeyPackage(dto.KeyPackage),
            SignatureKeyFingerprint = dto.SignatureKeyFingerprint,
            CreatedAt = now,
            ExpiresAt = now + MlsJoinRequest.Lifetime,
        });

        ctx.MlsJoinRequests.Add(request);
        await ctx.SaveChangesAsync();

        return MlsOperationResult.Ok(ToDto(request, await RequiredApprovalsFor(contextId, active.Generation)));
    }

    public async Task<List<MlsJoinRequestDto>> ListPendingAsync(string contextId, DateTimeOffset now)
    {
        var active = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

        if (active is null) return [];

        var required = await RequiredApprovalsFor(contextId, active.Generation);

        var requests = await ctx.MlsJoinRequests
            .AsNoTracking()
            .Include(r => r.Approvals)
            .Where(r => r.ContextId == contextId
                        && r.Generation == active.Generation
                        && r.State == MlsJoinRequestState.Pending
                        && r.ExpiresAt > now)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        return requests.Select(r => ToDto(r, required)).ToList();
    }

    /// <summary>
    /// Records one member's approval.
    ///
    /// <para>When this is the approval that meets the threshold, the response carries the key
    /// package so the approving client can mint the Add commit immediately - it is the one already
    /// holding group keys and already looking at the request.</para>
    /// </summary>
    public async Task<MlsOperationResult> ApproveAsync(
        string contextId,
        string requestId,
        string approverUserId,
        DateTimeOffset now)
    {
        var request = await ctx.MlsJoinRequests
            .Include(r => r.Approvals)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ContextId == contextId);

        if (request is null) return MlsOperationResult.NotFound("Join request not found");
        if (!request.IsActionableAt(now))
            return MlsOperationResult.Conflict(new MlsJoinRequestConflictDto
            {
                RequestId = requestId,
                State = request.State.ToString(),
                Reason = "This request is no longer open.",
            });

        // Vouching for yourself is not review. The two-person rule is meaningless if one of the two
        // can be the person being let in.
        if (request.RequesterUserId == approverUserId)
            return MlsOperationResult.BadRequest("You cannot approve your own join request.");

        var required = await RequiredApprovalsFor(contextId, request.Generation);

        if (request.Approvals.All(a => a.ApproverUserId != approverUserId))
        {
            request.Approvals.Add(MlsJoinRequestApproval.Create(request.Id, approverUserId, now));
            request.UpdatedAt = now;
            await ctx.SaveChangesAsync();
        }

        var approvals = request.Approvals.Select(a => a.ApproverUserId).Distinct().Count();
        var thresholdMet = approvals >= required;

        return MlsOperationResult.Ok(new MlsJoinRequestApprovalResultDto
        {
            RequestId = request.Id,
            Approvals = approvals,
            RequiredApprovals = required,
            ThresholdMet = thresholdMet,
            // Only handed over once the threshold is met - there is no reason for a lone approver to
            // be holding the bytes they are not yet entitled to add.
            KeyPackage = thresholdMet ? request.KeyPackage : null,
            KeyPackageHash = request.KeyPackageHash,
            Generation = request.Generation,
        });
    }

    public async Task<MlsOperationResult> DenyAsync(
        string contextId, string requestId, string approverUserId, DateTimeOffset now)
    {
        var request = await ctx.MlsJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ContextId == contextId);

        if (request is null) return MlsOperationResult.NotFound("Join request not found");
        if (!request.IsActionableAt(now))
            return MlsOperationResult.Conflict(new MlsJoinRequestConflictDto
            {
                RequestId = requestId,
                State = request.State.ToString(),
                Reason = "This request is no longer open.",
            });

        // One denial is enough. Refusing to vouch is not a decision that needs a second opinion -
        // the requester can always ask again.
        request.State = MlsJoinRequestState.Denied;
        request.DeniedByUserId = approverUserId;
        request.DeniedAt = now;
        request.UpdatedAt = now;

        await ctx.SaveChangesAsync();
        return MlsOperationResult.Ok();
    }

    public async Task<MlsOperationResult> CancelAsync(
        string contextId, string requestId, string requesterUserId, DateTimeOffset now)
    {
        var request = await ctx.MlsJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ContextId == contextId);

        if (request is null) return MlsOperationResult.NotFound("Join request not found");
        if (request.RequesterUserId != requesterUserId)
            return MlsOperationResult.NotFound("Join request not found");

        request.State = MlsJoinRequestState.Cancelled;
        request.UpdatedAt = now;

        await ctx.SaveChangesAsync();
        return MlsOperationResult.Ok();
    }

    /// <summary>
    /// Closes the requests a commit actually admitted.
    ///
    /// <para>Tied to the commit rather than to the approval, so a request is only ever marked
    /// fulfilled once the device is genuinely in the group. An approval that never resulted in a
    /// commit leaves the request open for someone else to act on.</para>
    /// </summary>
    public async Task FulfilAsync(string contextId, IReadOnlyCollection<string> requestIds, DateTimeOffset now)
    {
        if (requestIds.Count == 0) return;

        var requests = await ctx.MlsJoinRequests
            .Where(r => requestIds.Contains(r.Id)
                        && r.ContextId == contextId
                        && r.State == MlsJoinRequestState.Pending)
            .ToListAsync();

        foreach (var request in requests)
        {
            request.State = MlsJoinRequestState.Fulfilled;
            request.FulfilledAt = now;
            request.UpdatedAt = now;
        }
    }

    private static MlsJoinRequestDto ToDto(MlsJoinRequest request, int required)
    {
        var dto = request.ToFacet<MlsJoinRequest, MlsJoinRequestDto>();
        dto.RequiredApprovals = required;
        dto.ApproverUserIds = request.Approvals.Select(a => a.ApproverUserId).Distinct().ToList();
        return dto;
    }
}
