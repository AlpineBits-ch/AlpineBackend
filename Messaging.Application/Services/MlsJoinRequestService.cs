using System.Security.Cryptography;
using Domain;
using Facet.Extensions;
using Messaging.Application.Dtos.Request;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Services;

/// <summary>Admission to an encrypted context.</summary>
public class MlsJoinRequestService(MicroserviceContext ctx)
{
    /// <summary>How many approvals a request needs.</summary>
    public async Task<int> RequiredApprovalsFor(string contextId, int generation, MlsContextKind kind)
    {
        if (kind == MlsContextKind.Conversation) return 1;

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

        // Null and empty are "no actor", not an actor whose name happens to be blank.
        var known = actors
            .Append(generationRow.ActivatedByUserId)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .Count();

        return known >= 2 ? MlsJoinRequest.RequiredApprovals : 1;
    }

    /// <summary>How long one auto-admission spends the account's budget.</summary>
    public static readonly TimeSpan AutoAdmissionWindow = TimeSpan.FromHours(24);

    public static string HashKeyPackage(byte[] keyPackage) =>
        Convert.ToHexString(SHA256.HashData(keyPackage)).ToLowerInvariant();

    public async Task<MlsOperationResult> SubmitAsync(
        string contextId,
        string? conversationId,
        string? channelId,
        string requesterUserId,
        SubmitJoinRequestDto dto,
        DateTimeOffset now,
        MlsContextKind kind = MlsContextKind.Channel,
        ProtectionLevel protectionLevel = ProtectionLevel.VerifiedDevices)
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

        // Scoped to the requester as well as the device.
        var existing = await ctx.MlsJoinRequests
            .FirstOrDefaultAsync(r => r.ContextId == contextId
                                      && r.Generation == active.Generation
                                      && r.RequesterUserId == requesterUserId
                                      && r.RequesterDeviceId == dto.DeviceId
                                      && r.State == MlsJoinRequestState.Pending);

        if (existing is not null)
        {
            // Idempotent for the same key.
            var incomingHash = HashKeyPackage(dto.KeyPackage);
            if (existing.KeyPackageHash == incomingHash)
                return MlsOperationResult.Ok(
                    ToDto(existing, await RequiredApprovalsFor(contextId, active.Generation, kind)));

            existing.State = MlsJoinRequestState.Cancelled;
            existing.UpdatedAt = now;
        }

        var requiresManualApproval =
            protectionLevel == ProtectionLevel.VerifiedDevices
            || !await HasAutoAdmissionBudgetAsync(requesterUserId, dto.DeviceId, now);

        var request = MlsJoinRequest.Create(new CreateMlsJoinRequestParams
        {
            RequiresManualApproval = requiresManualApproval,
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

        return MlsOperationResult.Ok(
            ToDto(request, await RequiredApprovalsFor(contextId, active.Generation, kind)));
    }

    /// <summary>
    /// Whether this account may still admit a device today without a human tapping approve.
    /// </summary>
    private async Task<bool> HasAutoAdmissionBudgetAsync(string userId, string deviceId, DateTimeOffset now)
    {
        var since = now - AutoAdmissionWindow;

        var recentlyAdmitted = await ctx.MlsJoinRequests
            .AsNoTracking()
            .Where(r => r.RequesterUserId == userId
                        && r.State == MlsJoinRequestState.Fulfilled
                        && !r.RequiresManualApproval
                        && r.FulfilledAt > since
                        && r.RequesterDeviceId != deviceId)
            .Select(r => r.RequesterDeviceId)
            .Distinct()
            .CountAsync();

        return recentlyAdmitted == 0;
    }

    /// <summary>The review queue.</summary>
    public async Task<List<MlsJoinRequestDto>> ListPendingAsync(
        string contextId, DateTimeOffset now, MlsContextKind kind = MlsContextKind.Channel,
        string? callerUserId = null)
    {
        var active = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

        if (active is null) return [];

        var required = await RequiredApprovalsFor(contextId, active.Generation, kind);

        var requests = await ctx.MlsJoinRequests
            .AsNoTracking()
            .Include(r => r.Approvals)
            .Where(r => r.ContextId == contextId
                        && r.Generation == active.Generation
                        && r.State == MlsJoinRequestState.Pending
                        && r.ExpiresAt > now)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        return requests
            .Select(r => ToDto(r, required, includeKeyPackage: r.RequesterUserId == callerUserId))
            .ToList();
    }

    /// <summary>Records one approval.</summary>
    public async Task<MlsOperationResult> ApproveAsync(
        string contextId,
        string requestId,
        string approverUserId,
        DateTimeOffset now,
        MlsContextKind kind = MlsContextKind.Channel,
        string? approverDeviceId = null)
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

        if (request.RequesterUserId == approverUserId)
        {
            if (string.IsNullOrWhiteSpace(approverDeviceId))
            {
                return MlsOperationResult.BadRequest(
                    "Approving your own account's join request requires a validated X-Device-Id, so "
                    + "the server can tell one of your devices from the one asking to be let in.");
            }

            // Vouching for yourself is not review.
            if (string.Equals(approverDeviceId, request.RequesterDeviceId, StringComparison.Ordinal))
            {
                return MlsOperationResult.BadRequest(
                    "A device cannot approve its own join request.");
            }
        }

        var required = await RequiredApprovalsFor(contextId, request.Generation, kind);

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

        // One denial is enough.
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

    /// <summary>Closes the requests a commit actually admitted.</summary>
    /// <returns>
    /// The requests this commit actually closed, so the caller can announce the ones that were
    /// admitted without a human ever tapping approve.
    /// </returns>
    public async Task<List<MlsJoinRequest>> FulfilAsync(
        string contextId,
        IReadOnlyCollection<string> requestIds,
        IReadOnlyCollection<(string UserId, string DeviceId)> welcomedDevices,
        int required,
        DateTimeOffset now)
    {
        if (requestIds.Count == 0) return [];

        var candidates = await ctx.MlsJoinRequests
            .Include(r => r.Approvals)
            .Where(r => requestIds.Contains(r.Id)
                        && r.ContextId == contextId
                        && r.State == MlsJoinRequestState.Pending)
            .ToListAsync();

        var welcomed = welcomedDevices.ToHashSet();

        var requests = candidates
            .Where(r => welcomed.Contains((r.RequesterUserId, r.RequesterDeviceId)))
            .Where(r => r.Approvals.Select(a => a.ApproverUserId).Distinct().Count() >= required
                        || !r.RequiresManualApproval)
            .ToList();

        foreach (var request in requests)
        {
            request.State = MlsJoinRequestState.Fulfilled;
            request.FulfilledAt = now;
            request.UpdatedAt = now;
        }

        return requests;
    }

    // ══════════════════════════════════════════════════════════════════════════ Admission proof
    // relay

    /// <summary>An existing device posts a nonce for the joining device to sign.</summary>
    public async Task<MlsOperationResult> IssueChallengeAsync(
        string contextId,
        string requestId,
        string issuerUserId,
        string? issuerDeviceId,
        IssueAdmissionChallengeDto dto,
        DateTimeOffset now)
    {
        if (dto.Challenge is null || dto.Challenge.Length != MlsAdmissionChallenge.ChallengeLength)
            return MlsOperationResult.BadRequest(
                $"Challenge must be exactly {MlsAdmissionChallenge.ChallengeLength} bytes.");

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

        // Challenging your own request proves nothing - the point is that a *different* device,
        // one already trusted with the master key, chose the nonce.
        if (request.RequesterUserId == issuerUserId && request.RequesterDeviceId == issuerDeviceId)
            return MlsOperationResult.BadRequest("A device cannot challenge its own join request.");

        // One live challenge per issuer, not per request.
        var superseded = await ctx.MlsAdmissionChallenges
            .Where(c => c.JoinRequestId == requestId
                        && c.Proof == null
                        && c.IssuedByUserId == issuerUserId
                        && c.IssuedByDeviceId == issuerDeviceId)
            .ToListAsync();
        if (superseded.Count > 0) ctx.MlsAdmissionChallenges.RemoveRange(superseded);

        var challenge = MlsAdmissionChallenge.Create(new CreateMlsAdmissionChallengeParams
        {
            JoinRequestId = requestId,
            ContextId = contextId,
            IssuedByUserId = issuerUserId,
            IssuedByDeviceId = issuerDeviceId,
            Challenge = dto.Challenge,
            CreatedAt = now,
            ExpiresAt = now + MlsAdmissionChallenge.Lifetime,
        });

        ctx.MlsAdmissionChallenges.Add(challenge);
        await ctx.SaveChangesAsync();

        return MlsOperationResult.Ok(ToDto(challenge, includeChallenge: true));
    }

    /// <summary>The outstanding challenge for a request, for the requester to sign.</summary>
    public async Task<MlsOperationResult> GetChallengeAsync(
        string contextId, string requestId, string callerUserId, DateTimeOffset now)
    {
        var request = await ctx.MlsJoinRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ContextId == contextId);

        if (request is null) return MlsOperationResult.NotFound("Join request not found");
        if (request.RequesterUserId != callerUserId)
            return MlsOperationResult.NotFound("Join request not found");

        var challenge = await ctx.MlsAdmissionChallenges.AsNoTracking()
            .Where(c => c.JoinRequestId == requestId && c.Proof == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        return challenge is null
            ? MlsOperationResult.NotFound("No challenge is outstanding for this request.")
            : MlsOperationResult.Ok(ToDto(challenge, includeChallenge: true));
    }

    /// <summary>
    /// The joining device submits its signature over <c>challenge || requesterDeviceId ||
    /// signatureKeyFingerprint</c>.
    /// </summary>
    public async Task<MlsOperationResult> SubmitProofAsync(
        string contextId, string requestId, string callerUserId, SubmitAdmissionProofDto dto, DateTimeOffset now)
    {
        if (dto.Proof is null || dto.Proof.Length == 0)
            return MlsOperationResult.BadRequest("Proof is required");

        var request = await ctx.MlsJoinRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ContextId == contextId);

        if (request is null) return MlsOperationResult.NotFound("Join request not found");
        if (request.RequesterUserId != callerUserId)
            return MlsOperationResult.NotFound("Join request not found");

        var challenge = await ctx.MlsAdmissionChallenges
            .FirstOrDefaultAsync(c => c.Id == dto.ChallengeId && c.JoinRequestId == requestId);

        if (challenge is null) return MlsOperationResult.NotFound("Challenge not found");

        if (!challenge.IsAnswerableAt(now))
        {
            return MlsOperationResult.Conflict(new MlsJoinRequestConflictDto
            {
                RequestId = requestId,
                State = challenge.Proof is null ? "Expired" : "AlreadyAnswered",
                Reason = challenge.Proof is null
                    ? "This challenge has expired; ask for a new one."
                    : "This challenge has already been answered.",
            });
        }

        challenge.Proof = dto.Proof;
        challenge.ProofSubmittedAt = now;
        challenge.UpdatedAt = now;

        await ctx.SaveChangesAsync();

        return MlsOperationResult.Ok(ToDto(challenge, includeChallenge: false));
    }

    /// <summary>The proof, for the device that issued the challenge to verify locally.</summary>
    public async Task<MlsOperationResult> GetProofAsync(
        string contextId, string requestId, string callerUserId, string? callerDeviceId, DateTimeOffset now)
    {
        var request = await ctx.MlsJoinRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ContextId == contextId);

        if (request is null) return MlsOperationResult.NotFound("Join request not found");

        var challenge = await ctx.MlsAdmissionChallenges.AsNoTracking()
            .Where(c => c.JoinRequestId == requestId
                        && c.Proof != null
                        && c.IssuedByUserId == callerUserId
                        && (c.IssuedByDeviceId == null || callerDeviceId == null
                                                       || c.IssuedByDeviceId == callerDeviceId))
            .OrderByDescending(c => c.ProofSubmittedAt)
            .FirstOrDefaultAsync();

        if (challenge is null || !challenge.IsUsableProofAt(now))
            return MlsOperationResult.NotFound("No usable admission proof for this request.");

        return MlsOperationResult.Ok(new MlsAdmissionProofDto
        {
            RequestId = requestId,
            ChallengeId = challenge.Id,
            Challenge = challenge.Challenge,
            Proof = challenge.Proof,
            RequesterDeviceId = request.RequesterDeviceId,
            SignatureKeyFingerprint = request.SignatureKeyFingerprint,
            ExpiresAt = challenge.ExpiresAt,
        });
    }

    private static MlsAdmissionChallengeDto ToDto(MlsAdmissionChallenge challenge, bool includeChallenge) => new()
    {
        ChallengeId = challenge.Id,
        RequestId = challenge.JoinRequestId,
        Challenge = includeChallenge ? challenge.Challenge : null,
        IssuedByDeviceId = challenge.IssuedByDeviceId,
        ExpiresAt = challenge.ExpiresAt,
        Answered = challenge.Proof is { Length: > 0 },
    };

    private static MlsJoinRequestDto ToDto(MlsJoinRequest request, int required, bool includeKeyPackage = false)
    {
        var dto = request.ToFacet<MlsJoinRequest, MlsJoinRequestDto>();
        dto.RequiredApprovals = required;
        dto.ApproverUserIds = request.Approvals.Select(a => a.ApproverUserId).Distinct().ToList();
        if (includeKeyPackage) dto.KeyPackage = request.KeyPackage;
        return dto;
    }
}
