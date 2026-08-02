using System.Security.Cryptography;
using Domain;
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
    /// <para><b>A conversation always needs exactly one.</b> A DM has two humans in it; requiring two
    /// approvals means the one person who is not the requester cannot admit them alone, and the
    /// conversation deadlocks with no route out. The threshold is passed in rather than inferred from
    /// the actor count, because inferring it made the answer depend on how much traffic the group
    /// happened to have seen.</para>
    ///
    /// <para>For a channel it is two, except when the server has only ever seen a single person act
    /// on this group - the member who switched encryption on and nobody since. Demanding two there
    /// would make the channel permanently unable to admit anyone, since the one member cannot
    /// approve twice.</para>
    ///
    /// <para>The count is derived from who activated the generation and who has published commits
    /// against it, both of which come from authenticated callers. A member cannot deflate it to let
    /// themselves solo-admit: forging a second actor is impossible, and if the set really does hold
    /// one person, that person could add anyone unilaterally anyway.</para>
    /// </summary>
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

        // Null and empty are "no actor", not an actor whose name happens to be blank. The
        // AddMlsGenerations backfill wrote activated_by_user_id = '' for every pre-existing
        // generation, and counting that as a second person made a one-member group demand two
        // approvals it could never collect.
        var known = actors
            .Append(generationRow.ActivatedByUserId)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .Count();

        return known >= 2 ? MlsJoinRequest.RequiredApprovals : 1;
    }

    /// <summary>How long one auto-admission spends the account's budget. See
    /// <see cref="HasAutoAdmissionBudgetAsync"/>.</summary>
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
        //
        // ClientDeviceId is chosen by the client and unique only per account, and victim device ids
        // are readable straight off MlsJoinRequestDto. Without the user in this predicate, any
        // co-member could submit a request naming the victim's device id, "supersede" the victim's
        // genuine pending request, and cancel it - repeatably, for as long as the victim kept
        // retrying.
        var existing = await ctx.MlsJoinRequests
            .FirstOrDefaultAsync(r => r.ContextId == contextId
                                      && r.Generation == active.Generation
                                      && r.RequesterUserId == requesterUserId
                                      && r.RequesterDeviceId == dto.DeviceId
                                      && r.State == MlsJoinRequestState.Pending);

        if (existing is not null)
        {
            // Idempotent for the same key. A different key from the same device replaces the old
            // request rather than stacking: reviewers should never be looking at two asks from one
            // device and have to work out which one they are vouching for.
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
    ///
    /// <para>One per 24 hours, counted by <i>device</i> rather than by request: the same handset
    /// joining five conversations is one admission, and charging it five would make a normal restore
    /// fall back to manual for no reason. The device currently asking is excluded for the same
    /// reason - re-requesting for a second conversation is not a second device.</para>
    ///
    /// <para>A burst of admissions is what a compromise looks like, so exceeding the budget makes
    /// the next one cost a tap. It does not make it fail: a legitimate user with two new devices
    /// must still be able to get both in.</para>
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

    /// <summary>
    /// The review queue.
    ///
    /// <para><paramref name="callerUserId"/> decides who gets the key-package bytes: the requester's
    /// own other devices do, because that is the flow in which they verify the §G admission proof
    /// against material they re-derive themselves; peers do not until they have crossed the approval
    /// threshold.</para>
    /// </summary>
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

    /// <summary>
    /// Records one approval.
    ///
    /// <para>When this is the approval that meets the threshold, the response carries the key
    /// package so the approving client can mint the Add commit immediately - it is the one already
    /// holding group keys and already looking at the request.</para>
    ///
    /// <para><b>Two flows, per §L.3.</b> §G.1 requires the admission proof to be verified by the
    /// requester's <i>own other device</i>, because that is the only party holding the account master
    /// key; §B and the original implementation forbade self-approval outright. Those two sets are
    /// disjoint, which made the entire admission ceremony structurally unreachable - the only party
    /// permitted to approve was a peer, who by construction cannot verify a master-key HMAC.</para>
    ///
    /// <list type="number">
    /// <item><b>Own-device admission.</b> A different device of the same account may approve. That
    /// is the flow the §G proof is meaningful in, and the one a user adding their second handset
    /// actually walks.</item>
    /// <item><b>Peer admission.</b> Unchanged, and never asked to verify a proof it cannot: it rests
    /// on the device certificate and an out-of-band fingerprint comparison.</item>
    /// </list>
    ///
    /// <para>What stays forbidden is the <i>same device</i> approving itself, which proves nothing at
    /// all. The approving device is resolved by the caller from its session, never from a body field
    /// - and when the caller cannot establish which device it is, self-approval is refused, because
    /// "I could not tell" must not read as "it was a different one".</para>
    /// </summary>
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
    /// <para><b>The ids are the caller's claim, and the Welcomes are the evidence.</b> Nothing tied
    /// them to the commit or to the caller, so a member could attach arbitrary pending ids from the
    /// same context and have three things happen at once: the requests left <c>Pending</c> and
    /// became unapprovable, each one spent its owner's 24-hour auto-admission budget, and the server
    /// pushed <c>identity.DeviceAdmitted</c> for devices that had never been added - naming a real
    /// device and a real signature-key fingerprint, which is precisely the notification a user is
    /// told to treat as a compromise.</para>
    ///
    /// <para>Two checks, and both are about what the commit <i>did</i> rather than what its publisher
    /// says it did:</para>
    ///
    /// <list type="number">
    /// <item><b>The commit must carry a Welcome for the requesting device.</b> An Add that admits a
    /// device is always accompanied by one - that is how the device gets in - so a request with no
    /// matching Welcome in this commit was not admitted by it. This is the only handle the server has
    /// on the commit's actual contents, and it is a real one.</item>
    /// <item><b>The request must have been admissible.</b> Either it met its approval threshold or it
    /// was eligible for auto-admission. Closing one that had neither turns "fulfilled" into a claim
    /// no review ever backed.</item>
    /// </list>
    ///
    /// <returns>The requests this commit actually closed, so the caller can announce the ones that
    /// were admitted without a human ever tapping approve.</returns>
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

    // ══════════════════════════════════════════════════════════════════════════
    // Admission proof relay
    //
    // The server carries bytes between two of the user's devices and does nothing else with them.
    // It cannot verify a proof - the key is derived from the account master key, which it holds only
    // wrapped - and it must not appear to, because a client that trusted a server-side verdict would
    // have handed back exactly the power this design removes. What it does enforce is the two things
    // it can: the nonce is answered once, and only inside its window.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An existing device posts a nonce for the joining device to sign.
    ///
    /// <para>The nonce comes from the issuing client, not from here. A server-chosen challenge could
    /// be one the server had precomputed a signature against, which would make the whole exchange
    /// theatre.</para>
    /// </summary>
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
        //
        // Superseding every outstanding nonce made this a displacement primitive: any co-member
        // could post a challenge of their own and delete the one the requester's real verifier was
        // waiting on, indefinitely. An issuer may replace its own - that is a retry - and nobody
        // else's.
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

    /// <summary>The outstanding challenge for a request, for the requester to sign. Only the
    /// requesting user may read it - handing the nonce to anyone else invites them to try to answer
    /// it.</summary>
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
    /// The joining device submits its signature over
    /// <c>challenge || requesterDeviceId || signatureKeyFingerprint</c>.
    ///
    /// <para>Stored verbatim and never checked here. Single-use is enforced by refusing a second
    /// proof rather than overwriting: two different signatures over one nonce means at least one of
    /// them did not come from the device that should have made it, and quietly keeping the later one
    /// would hide that.</para>
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

    /// <summary>
    /// The proof, for the device that issued the challenge to verify locally.
    ///
    /// <para>Everything needed for that verification travels together - the nonce, the signature, the
    /// device id and the fingerprint - because the verifier must not have to trust the server's
    /// account of what was signed over.</para>
    ///
    /// <para><b>Only the issuer.</b> This used to check nothing beyond membership, so every member of
    /// the context could collect an admission proof made under someone else's nonce. A proof is a
    /// signature over <c>challenge || deviceId || fingerprint</c>; handing it to parties who did not
    /// choose the challenge is handing out signed material for a nonce they may have influenced, and
    /// it is not something anyone but the verifier has a use for.</para>
    /// </summary>
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
