using Billing.Domain.Aggregates;
using Billing.Domain.Promotions;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Billing.Application.Promotions;

/// <summary>
/// What is known about the card behind an account, supplied by the caller rather than read here.
/// </summary>
public sealed record PromotionCardEvidence(bool HasCard, string? Fingerprint);

/// <summary>The hashes one evaluation computed, carried forward so the redemption that follows writes
/// exactly what was matched against rather than recomputing and risking a different normalisation.
/// </summary>
public sealed record PromotionIdentityHashes(
    string? PhoneHash,
    IReadOnlyList<string> DeviceHashes,
    string? CardHash)
{
    public static PromotionIdentityHashes None => new(null, [], null);

    /// <summary>Every hash with its kind, in the order they are written as marks.</summary>
    public IEnumerable<(PromotionIdentityKind Kind, string Hash)> All()
    {
        foreach (var device in DeviceHashes) yield return (PromotionIdentityKind.Device, device);

        if (PhoneHash is not null) yield return (PromotionIdentityKind.Phone, PhoneHash);
        if (CardHash is not null) yield return (PromotionIdentityKind.Card, CardHash);
    }
}

/// <summary>The answer to "may this account take this campaign".</summary>
public sealed record TrialEligibility(
    bool Allowed,
    string? Code,
    string? Message,
    IReadOnlyList<string> FailedRules,
    PromotionIdentityHashes Hashes)
{
    public static TrialEligibility Yes(PromotionIdentityHashes hashes) =>
        new(true, null, null, [], hashes);

    public static TrialEligibility No(
        string code, string message, PromotionIdentityHashes hashes, IReadOnlyList<string>? rules = null) =>
        new(false, code, message, rules ?? [], hashes);

    /// <summary>Turns a refusal into the exception the endpoints answer with.</summary>
    public PromotionRefusedException ToRefusal() =>
        new(Code ?? PromotionErrorCodes.NotEligible, Message ?? "This offer is not available.")
        {
            FailedRules = FailedRules,
        };
}

/// <summary>
/// Decides whether one account may redeem one campaign, and it is the whole point of the wave.
/// </summary>
public sealed class PromotionEligibilityService(
    MicroserviceContext db,
    PromotionHasher hasher,
    IMessageBus bus,
    TimeProvider clock,
    IOptions<PromotionOptions> options,
    ILogger<PromotionEligibilityService>? logger = null)
{
    private readonly PromotionOptions _options = options?.Value ?? new PromotionOptions();

    /// <summary>The preflight a client uses to decide whether to offer the button at all.</summary>
    public async Task<bool> IsEligibleAsync(
        string campaignCode,
        string ownerUserId,
        string? guildId,
        CancellationToken cancellationToken)
    {
        var campaign = await db.PromotionCampaigns.FirstOrDefaultAsync(
            row => row.Code == campaignCode || row.Id == campaignCode, cancellationToken);

        if (campaign is null) return false;

        var decision = await EvaluateAsync(campaign, ownerUserId, guildId, card: null, cancellationToken);

        return decision.Allowed;
    }

    public async Task<TrialEligibility> EvaluateAsync(
        PromotionCampaign campaign,
        string ownerUserId,
        string? guildId,
        PromotionCardEvidence? card,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var now = clock.GetUtcNow();

        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return TrialEligibility.No(
                PromotionErrorCodes.NotPermitted,
                "A redemption needs an account to attach to.",
                PromotionIdentityHashes.None);
        }

        if (campaign.SubjectKind == SubjectKind.Guild && string.IsNullOrWhiteSpace(guildId))
        {
            return TrialEligibility.No(
                PromotionErrorCodes.TargetRequired,
                $"'{campaign.Code}' applies to a server, so it needs one naming.",
                PromotionIdentityHashes.None);
        }

        if (!campaign.IsOpenAt(now))
        {
            return TrialEligibility.No(
                PromotionErrorCodes.CampaignClosed,
                campaign.IsPaused
                    ? $"Campaign '{campaign.Code}' is paused."
                    : $"Campaign '{campaign.Code}' is outside its validity window.",
                PromotionIdentityHashes.None);
        }

        if (!campaign.HasRoomFor(1))
        {
            return TrialEligibility.No(
                PromotionErrorCodes.CampaignBudgetExhausted,
                $"Campaign '{campaign.Code}' has run out: all {campaign.TotalBudgetRedemptions} of its "
                + "redemptions have been taken.",
                PromotionIdentityHashes.None);
        }

        // The account.
        var ownerRedemptions = await CountRedemptionsAsync(
            campaign.Id, SubjectKind.User, ownerUserId, cancellationToken);

        if (ownerRedemptions >= campaign.MaxPerSubject)
        {
            return TrialEligibility.No(
                PromotionErrorCodes.AlreadyRedeemed,
                $"This account has already had '{campaign.Code}'. It is one per account, ever, and that "
                + "does not reset when the offer ends.",
                PromotionIdentityHashes.None);
        }

        // The guild, whoever owned it at the time.
        if (!string.IsNullOrWhiteSpace(guildId))
        {
            var guildRedemptions = await CountRedemptionsAsync(
                campaign.Id, SubjectKind.Guild, guildId.Trim(), cancellationToken);

            if (guildRedemptions >= campaign.MaxPerSubject)
            {
                return TrialEligibility.No(
                    PromotionErrorCodes.GuildAlreadyRedeemed,
                    $"This server has already had '{campaign.Code}', under this owner or a previous "
                    + "one. Moving it to a new owner does not bring the offer back.",
                    PromotionIdentityHashes.None);
            }
        }

        GetTrialEligibilitySignalsResponse identity;

        try
        {
            identity = await bus.InvokeAsync<GetTrialEligibilitySignalsResponse>(
                new GetTrialEligibilitySignalsRequest { UserId = ownerUserId },
                cancellationToken,
                _options.IdentityTimeout);
        }
        catch (Exception exception)
        {
            logger?.LogError(exception,
                "Identity did not answer the eligibility signals for {User}, so '{Campaign}' was "
                + "refused. This is an outage rather than a decision - failing open here would hand out "
                + "plans for as long as it lasted.", ownerUserId, campaign.Code);

            return TrialEligibility.No(
                PromotionErrorCodes.SignalsUnavailable,
                "This could not be checked just now. Try again in a moment.",
                PromotionIdentityHashes.None);
        }

        if (!identity.Found || identity.IsBot || !IsActive(identity.Status))
        {
            return TrialEligibility.No(
                PromotionErrorCodes.NotEligible,
                "This offer is not available on this account.",
                PromotionIdentityHashes.None);
        }

        var hashes = new PromotionIdentityHashes(
            hasher.Of(PromotionIdentityKind.Phone, identity.PhoneNumber),
            hasher.OfDevices(identity.DeviceIds),
            hasher.Of(PromotionIdentityKind.Card, card?.Fingerprint));

        var signals = new PromotionSignals(
            identity.EmailVerified,
            !string.IsNullOrWhiteSpace(identity.PhoneNumber),
            identity.CreatedAt,
            identity.DeviceIds.Count,
            await HasPriorSubscriptionAsync(ownerUserId, cancellationToken),
            card?.HasCard ?? false);

        var failed = PromotionEligibilityMap.Unsatisfied(
            campaign.RequiredSignals,
            signals,
            new PromotionEligibilityContext(now, campaign.MinimumAccountAgeDays));

        if (failed.Count > 0)
        {
            return TrialEligibility.No(
                PromotionErrorCodes.NotEligible,
                string.Join(" ", failed.Select(rule => rule.Refusal)),
                hashes,
                [.. failed.Select(rule => rule.Code)]);
        }

        // Last, because it is the most expensive and the one that needs the hashes.
        if (await MatchedIdentityAsync(campaign.Id, hashes, cancellationToken) is { } matched)
        {
            logger?.LogInformation(
                "Account {User} was refused '{Campaign}': a {Signal} already used on an existing "
                + "redemption of it.", ownerUserId, campaign.Code, matched);

            return TrialEligibility.No(
                PromotionErrorCodes.IdentityAlreadyRedeemed,
                "This offer has already been used by an account that shares a device, a phone number "
                + "or a card with this one. It is one per person rather than one per account.",
                hashes);
        }

        return TrialEligibility.Yes(hashes);
    }

    /// <summary>How many times this exact subject has redeemed this campaign.</summary>
    private Task<int> CountRedemptionsAsync(
        string campaignId, SubjectKind kind, string subjectId, CancellationToken cancellationToken) =>
        db.PromotionRedemptions.CountAsync(
            row => row.CampaignId == campaignId
                   && row.SubjectKind == kind
                   && row.SubjectId == subjectId,
            cancellationToken);

    /// <summary>The first signal that has been seen before under this campaign, or null when none has.
    /// Which one is returned only affects the log line - the refusal deliberately does not tell the
    /// person which of their identities matched, because that is a probe.</summary>
    private async Task<PromotionIdentityKind?> MatchedIdentityAsync(
        string campaignId, PromotionIdentityHashes hashes, CancellationToken cancellationToken)
    {
        foreach (var (kind, hash) in hashes.All())
        {
            var seen = await db.PromotionIdentityMarks.AnyAsync(
                mark => mark.CampaignId == campaignId && mark.Kind == kind && mark.Hash == hash,
                cancellationToken);

            if (seen) return kind;
        }

        return null;
    }

    /// <summary>Whether this account has ever paid for anything here.</summary>
    private Task<bool> HasPriorSubscriptionAsync(string userId, CancellationToken cancellationToken) =>
        db.Subscriptions.AnyAsync(
            subscription => subscription.PayerUserId == userId
                            && subscription.Status != SubscriptionStatus.Incomplete
                            && subscription.Status != SubscriptionStatus.IncompleteExpired,
            cancellationToken);

    /// <summary>Compared by name because <c>UserStatus</c> is Identity's enum and Billing has no
    /// business referencing it - the contract carries the name for exactly this reason. Anything that
    /// is not active is refused rather than reasoned about, so a status added later is refused by
    /// default.</summary>
    private static bool IsActive(string? status) =>
        string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase);
}
