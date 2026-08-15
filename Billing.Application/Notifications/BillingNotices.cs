using AppEnvironment;
using Billing.Application.Credit;
using Billing.Application.Dtos;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;
using Identity.Contracts.Bus.Commands;

namespace Billing.Application.Notifications;

/// <summary>Builds the three billing notifications, and returns null for everything else.</summary>
public static class BillingNotices
{
    /// <summary>Whether billing mail exists on this instance.</summary>
    public static bool Suppressed => Env.License.IsSelfHost;

    /// <summary>Staff or a campaign handed somebody credit.</summary>
    public static CreditIssuedNotification? ForCreditIssue(
        string userId,
        CreditLedgerResult result,
        IReadOnlyList<CreditLotRemainder> lots,
        string? campaign,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (Suppressed || result.WasReplay) return null;

        var issue = result.Entries.FirstOrDefault(entry => entry.Kind == CreditEntryKind.Issue);
        if (issue is null || issue.Amount <= 0) return null;

        // The lot this issue created carries the date; the wallet read is already in hand at the call
        // site, so this costs nothing extra.
        var expiry = lots?.FirstOrDefault(lot => lot.LotId == issue.LotId)?.ExpiresAt;

        return new CreditIssuedNotification
        {
            UserId = userId,
            DedupeKey = $"credit.issued:{issue.Id}",
            Points = issue.Amount,
            BalancePoints = result.Balance,
            ExpiresAt = expiry,
            IssuedBy = string.IsNullOrWhiteSpace(campaign) ? CreditIssuedBy.Staff : CreditIssuedBy.Campaign,
            Disclaimer = CreditDisclaimer.Text,
            OccurredAt = now,
        };
    }

    /// <summary>A human issued, amended or revoked a grant.</summary>
    public static EntitlementGrantNotification? ForGrant(
        EntitlementGrantChange change,
        Grant grant,
        EntitlementsChanged announcement,
        string? planDisplayName,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(announcement);

        if (Suppressed || grant.SubjectKind != SubjectKind.User) return null;

        return new EntitlementGrantNotification
        {
            UserId = grant.SubjectId,
            DedupeKey = $"grant.{change.ToString().ToLowerInvariant()}:{grant.Id}:{announcement.Version}",
            Change = change,
            PlanDisplayName = grant.GrantKind == GrantKind.Plan ? planDisplayName ?? grant.Plan : null,

            // Reused from the event rather than re-derived.
            Entitlements = [.. announcement.ChangedKeys],

            // A revocation ended the grant now, so its old expiry would read as a promise still
            // standing.
            ExpiresAt = change == EntitlementGrantChange.Revoked ? null : grant.ExpiresAt,
            OccurredAt = now,
        };
    }

    /// <summary>A subscriber moved themselves up.</summary>
    public static PlanUpgradedNotification? ForPlanChange(
        string userId, SubscriptionDto before, SubscriptionDto after, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (Suppressed) return null;
        if (before.PriceMinorUnits is not { } was || after.PriceMinorUnits is not { } isNow) return null;
        if (!string.Equals(before.Currency, after.Currency, StringComparison.OrdinalIgnoreCase)) return null;
        if (isNow <= was) return null;

        return new PlanUpgradedNotification
        {
            UserId = userId,
            DedupeKey = $"plan.upgraded:{after.Id}:{after.PlanName}@{after.VersionNumber}:{now:O}",
            PlanDisplayName = after.PlanDisplayName,
            PreviousPlanDisplayName = before.PlanDisplayName,
            CurrentPeriodEnd = after.CurrentPeriodEnd,
            OccurredAt = now,
        };
    }
}
