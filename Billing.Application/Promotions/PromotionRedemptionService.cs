using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Promotions;

/// <summary>What one redemption produced: the owner's row, the guild's row if there was one, and the
/// marks that will refuse the next account presenting the same identity.</summary>
public sealed record PromotionRedemptionResult(
    PromotionCampaign Campaign,
    PromotionRedemption Owner,
    PromotionRedemption? Guild,
    IReadOnlyList<PromotionIdentityMark> Marks);

/// <summary>Writing and reading the permanent record of who has had what.</summary>
public sealed class PromotionRedemptionService(
    MicroserviceContext db,
    PromotionCampaignService campaigns,
    TimeProvider clock,
    ILogger<PromotionRedemptionService>? logger = null)
{
    /// <summary>
    /// Records a redemption against the owner account, against the guild it was applied to, and
    /// against every identity behind it.
    /// </summary>
    /// <param name="campaign">
    /// The campaign, already charged by the caller. Charging belongs to whoever knows when the
    /// slot is safe to spend: charging here left the budget checked only after a real Stripe
    /// subscription existed that a refusal could not undo.
    /// </param>
    /// <param name="ownerUserId">The account taking the offer.</param>
    /// <param name="guildId">The guild it applies to, or null for a user-scoped campaign.</param>
    /// <param name="hashes">
    /// What <see cref="PromotionEligibilityService"/> already computed.
    /// </param>
    /// <param name="stripeSubscriptionId">The subscription carrying it, when there is one.</param>
    /// <param name="cancellationToken">The request's token.</param>
    public async Task<PromotionRedemptionResult> RecordAsync(
        PromotionCampaign campaign,
        string ownerUserId,
        string? guildId,
        PromotionIdentityHashes hashes,
        string? stripeSubscriptionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var now = clock.GetUtcNow();
        var endsAt = now.AddDays(campaign.TrialDays);

        var owner = NewRow(campaign, SubjectKind.User, ownerUserId, ownerUserId, now, endsAt);
        owner.StripeSubscriptionId = stripeSubscriptionId;
        db.PromotionRedemptions.Add(owner);

        PromotionRedemption? guild = null;

        if (!string.IsNullOrWhiteSpace(guildId))
        {
            guild = NewRow(campaign, SubjectKind.Guild, guildId.Trim(), ownerUserId, now, endsAt);
            db.PromotionRedemptions.Add(guild);
        }

        // Against the owner row rather than the guild one.
        var marks = new List<PromotionIdentityMark>();

        foreach (var (kind, hash) in hashes.All())
        {
            var mark = new PromotionIdentityMark
            {
                Id = PromotionIdentityMark.GenerateId(),
                CampaignId = campaign.Id,
                RedemptionId = owner.Id,
                Kind = kind,
                Hash = hash,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.PromotionIdentityMarks.Add(mark);
            marks.Add(mark);
        }

        logger?.LogInformation(
            "Account {Owner} redeemed '{Campaign}' for {Subject} until {EndsAt}. {Marks} identity "
            + "mark(s) written; {Issued} of {Budget} redemptions taken.",
            ownerUserId, campaign.Code, guildId ?? ownerUserId, endsAt.ToString("O"), marks.Count,
            campaign.IssuedRedemptions, campaign.TotalBudgetRedemptions);

        return new PromotionRedemptionResult(campaign, owner, guild, marks);
    }

    /// <summary>The owner's row for a campaign, or null. The one row that carries the clock.</summary>
    public Task<PromotionRedemption?> FindOwnerRedemptionAsync(
        string campaignId, string ownerUserId, CancellationToken cancellationToken) =>
        db.PromotionRedemptions.FirstOrDefaultAsync(
            row => row.CampaignId == campaignId
                   && row.SubjectKind == SubjectKind.User
                   && row.SubjectId == ownerUserId,
            cancellationToken);

    /// <summary>Every redemption a subject has ever had, newest first.</summary>
    public Task<List<PromotionRedemption>> ListForSubjectAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        return db.PromotionRedemptions
            .Where(row => row.SubjectKind == kind && row.SubjectId == id)
            .OrderByDescending(row => row.RedeemedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<List<PromotionRedemption>> ListForCampaignAsync(
        string campaignId, int limit, CancellationToken cancellationToken) =>
        db.PromotionRedemptions
            .Where(row => row.CampaignId == campaignId)
            .OrderByDescending(row => row.RedeemedAt)
            .ThenBy(row => row.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

    /// <summary>Moves the guild half of a redemption, keeping the clock.</summary>
    public async Task<PromotionRedemption> MoveGuildAsync(
        PromotionCampaign campaign,
        string ownerUserId,
        string toGuildId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentException.ThrowIfNullOrWhiteSpace(toGuildId);

        var now = clock.GetUtcNow();
        var target = toGuildId.Trim();

        var owner = await FindOwnerRedemptionAsync(campaign.Id, ownerUserId, cancellationToken)
                    ?? throw new PromotionRefusedException(PromotionErrorCodes.NoTrialToMove,
                        $"This account has not redeemed '{campaign.Code}', so there is nothing to "
                        + "move.");

        if (owner.IsExpiredAt(now))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.NoTrialToMove,
                $"'{campaign.Code}' ended for this account on {owner.EndsAt:d}. Moving it would be a "
                + "second trial, which is the thing the record of the first one exists to prevent.");
        }

        var existing = await db.PromotionRedemptions
            .Where(row => row.CampaignId == campaign.Id
                          && row.SubjectKind == SubjectKind.Guild
                          && row.SubjectId == target)
            .ToListAsync(cancellationToken);

        // Somebody else's redemption of this guild, live or released.
        if (existing.Any(row => row.OwnerUserId != ownerUserId))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.GuildAlreadyRedeemed,
                $"This server has already had '{campaign.Code}' under another account.");
        }

        // Already here and still the live one: a no-op, because a double-clicked button is not an
        // error and a second row is what the unique index would refuse anyway.
        if (existing.FirstOrDefault(row => !row.IsReleased) is { } current) return current;

        foreach (var row in await LiveGuildRowsAsync(campaign.Id, ownerUserId, cancellationToken))
        {
            row.ReleasedAt = now;
            row.UpdatedAt = now;
        }

        // Moving back to a server this same trial was on before.
        if (existing.FirstOrDefault(row => row.IsReleased) is { } returning)
        {
            returning.ReleasedAt = null;
            returning.UpdatedAt = now;

            return returning;
        }

        var moved = NewRow(campaign, SubjectKind.Guild, target, ownerUserId, owner.RedeemedAt, owner.EndsAt);
        moved.CreatedAt = now;
        moved.UpdatedAt = now;

        db.PromotionRedemptions.Add(moved);

        logger?.LogInformation(
            "Account {Owner} moved their '{Campaign}' trial to {Guild}. It still ends {EndsAt}, which "
            + "is the date it was always going to end.",
            ownerUserId, campaign.Code, target, owner.EndsAt?.ToString("O") ?? "never");

        return moved;
    }

    /// <summary>Stamps <c>ExpiredAt</c> on redemptions whose clock has run out.</summary>
    public async Task<int> StampExpiredAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        return await db.PromotionRedemptions
            .Where(row => row.ExpiredAt == null && row.EndsAt != null && row.EndsAt <= now)
            .Take(Math.Clamp(batchSize, 1, 1000))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.ExpiredAt, now)
                    .SetProperty(row => row.UpdatedAt, now),
                cancellationToken);
    }

    private Task<List<PromotionRedemption>> LiveGuildRowsAsync(
        string campaignId, string ownerUserId, CancellationToken cancellationToken) =>
        db.PromotionRedemptions
            .Where(row => row.CampaignId == campaignId
                          && row.SubjectKind == SubjectKind.Guild
                          && row.OwnerUserId == ownerUserId
                          && row.ReleasedAt == null)
            .ToListAsync(cancellationToken);

    private static PromotionRedemption NewRow(
        PromotionCampaign campaign,
        SubjectKind kind,
        string subjectId,
        string ownerUserId,
        DateTimeOffset redeemedAt,
        DateTimeOffset? endsAt) => new()
    {
        Id = PromotionRedemption.GenerateId(),
        CampaignId = campaign.Id,
        SubjectKind = kind,
        SubjectId = subjectId,
        OwnerUserId = ownerUserId,
        RedeemedAt = redeemedAt,
        EndsAt = endsAt,
        CreatedAt = redeemedAt,
        UpdatedAt = redeemedAt,
    };
}
