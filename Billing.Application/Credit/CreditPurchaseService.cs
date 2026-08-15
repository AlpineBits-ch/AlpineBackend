using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Credit;

/// <summary>Refusals that belong to the purchase rather than to the ledger underneath it.</summary>
public static class CreditPurchaseErrorCodes
{
    public const string UnknownSku = "unknown_sku";

    /// <summary>The SKU exists and is not on sale, which on this surface always means the same thing:
    /// its plan has no cash price. Section 8.1 forbids credit being the only route to anything, so a
    /// SKU that money cannot buy is a SKU credit must not buy either.</summary>
    public const string NoCashPrice = "no_cash_price";

    public const string TargetRequired = "target_required";

    /// <summary>The subject already holds this plan permanently, so time bought now would queue
    /// behind something with no end and never start. Refused rather than sold: "I spent my credit and
    /// got nothing" is the worst support ticket in the document, and this is the only shape of it the
    /// queueing rule cannot fix on its own.</summary>
    public const string AlreadyPermanent = "already_permanent";
}

/// <summary>
/// What a purchase produced: the grant, the ledger lines that paid for it, and the announcement
/// that goes with a changed entitlement.
/// </summary>
public sealed record CreditPurchase(
    CreditSku Sku,
    Grant Grant,
    IReadOnlyList<CreditEntry> Entries,
    long BalanceAfter,
    EntitlementsChanged? Announcement,
    bool WasQueued,
    bool WasReplay = false);

/// <summary>Spending credit (monetization.md section 8.3).</summary>
public sealed class CreditPurchaseService(
    MicroserviceContext db,
    CreditLedgerService ledger,
    CreditCatalogueService catalogue,
    GrantService grants,
    TimeProvider clock,
    ILogger<CreditPurchaseService>? logger = null)
{
    /// <summary>Buys one SKU with the buyer's own credit.</summary>
    /// <param name="buyerUserId">Whose wallet pays.</param>
    /// <param name="targetId">
    /// What the grant attaches to - a guild the buyer belongs to, or the buyer themselves.
    /// </param>
    /// <param name="skuCode">Which catalogue entry, by its stable code.</param>
    /// <param name="idempotencyKey">
    /// What makes a retry the same purchase rather than a second one.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="startNotBefore">
    /// The earliest the bought time may begin, on top of whatever the subject already holds.
    /// </param>
    public async Task<CreditPurchase> PurchaseAsync(
        string buyerUserId,
        string skuCode,
        string? targetId,
        string idempotencyKey,
        CancellationToken cancellationToken,
        DateTimeOffset? startNotBefore = null)
    {
        if (string.IsNullOrWhiteSpace(buyerUserId))
        {
            throw new CreditRefusedException(CreditErrorCodes.UserRequired,
                "A purchase needs a buyer whose wallet it comes out of.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new CreditRefusedException(CreditErrorCodes.IdempotencyKeyRequired,
                "A purchase needs an idempotency key. Without one a retried request buys the thing "
                + "twice and charges for it twice.");
        }

        var sku = await catalogue.FindAsync(skuCode, cancellationToken)
                  ?? throw new CreditRefusedException(CreditPurchaseErrorCodes.UnknownSku,
                      $"'{skuCode}' is not something credit buys on this instance.");

        if (!CreditCatalogueService.IsPurchasable(sku))
        {
            throw new CreditRefusedException(CreditPurchaseErrorCodes.NoCashPrice,
                $"'{sku.Code}' is not on sale. Every SKU credit buys must also have a plain cash "
                + "price, and this one's plan has none - credit being the only route to something is "
                + "the pattern monetization.md section 8.1 rules out.");
        }

        var subject = ResolveSubject(sku, buyerUserId, targetId);

        // The transaction is owned here rather than by the ledger, because the deduction and the
        // grant have to be one write. Everything below joins it.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var now = clock.GetUtcNow();
        var startsAt = await QueueBehindAsync(subject, sku, now, startNotBefore, cancellationToken);

        var deduction = await ledger.DeductAsync(
            buyerUserId, sku.PricePoints, idempotencyKey, grantId: null,
            reason: null, cancellationToken);

        if (deduction.WasReplay)
        {
            // The same request arrived twice.
            var existing = await ExistingPurchaseAsync(sku, deduction, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var (grant, announcement) = await grants.IssueAsync(
            new IssueGrant(
                subject.Kind,
                subject.Id,
                GrantKind.Plan,
                sku.Plan,
                null,
                startsAt.AddDays(sku.DurationDays),
                $"{sku.PricePoints} credits spent on '{sku.Code}' by {buyerUserId}.",
                GrantSource.Credit,
                startsAt == now ? null : startsAt),
            buyerUserId,
            cancellationToken);

        // Set after the fact and inside the same transaction, so no reader ever sees a spend
        // without the grant it paid for.
        foreach (var entry in deduction.Entries) entry.GrantId = grant.Id;

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        logger?.LogInformation(
            "{Buyer} spent {Points} credits on {Sku} for {SubjectKind} {SubjectId}; grant {GrantId} "
            + "runs {Start} to {End}",
            buyerUserId, sku.PricePoints, sku.Code, subject.Kind, subject.Id, grant.Id,
            startsAt.ToString("O"), grant.ExpiresAt?.ToString("O") ?? "forever");

        return new CreditPurchase(
            sku, grant, deduction.Entries, deduction.Balance, announcement, WasQueued: startsAt > now);
    }

    /// <summary>When the bought time should begin.</summary>
    private async Task<DateTimeOffset> QueueBehindAsync(
        EntitlementSubject subject,
        CreditSku sku,
        DateTimeOffset now,
        DateTimeOffset? startNotBefore,
        CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        // Filtered in memory on the plan name because plan names are matched case-insensitively
        // everywhere else in this service - PlanCatalogue.Find, the seeder, the grant validator - and
        // a case-sensitive comparison here would silently fail to queue behind "Pro" when the SKU
        // said "pro". A subject holds a handful of grants, never more.
        var held = await db.Grants
            .Where(grant => grant.SubjectKind == kind
                            && grant.SubjectId == id
                            && grant.RevokedAt == null
                            && grant.GrantKind == GrantKind.Plan
                            && (grant.ExpiresAt == null || grant.ExpiresAt > now))
            .ToListAsync(cancellationToken);

        var samePlan = held
            .Where(grant => string.Equals(grant.Plan, sku.Plan, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (samePlan.Any(grant => grant.ExpiresAt is null))
        {
            throw new CreditRefusedException(CreditPurchaseErrorCodes.AlreadyPermanent,
                $"{subject.Kind} {subject.Id} already holds '{sku.Plan}' with no end date, so thirty "
                + "more days would queue behind something that never finishes and would never take "
                + "effect. Nothing has been charged.");
        }

        var latest = samePlan.Select(grant => grant.ExpiresAt).Max();

        var behindHeldTime = latest is { } end && end > now ? end : now;

        return startNotBefore is { } floor && floor > behindHeldTime ? floor : behindHeldTime;
    }

    /// <summary>A user SKU is always applied to the buyer; a guild SKU needs somewhere to go. The
    /// membership check is Guild's, and asking for it here would put a cross-service call inside the
    /// transaction that holds the wallet lock.</summary>
    private static EntitlementSubject ResolveSubject(CreditSku sku, string buyerUserId, string? targetId)
    {
        if (sku.Subject == SubjectKind.User) return EntitlementSubject.ForUser(buyerUserId);

        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new CreditRefusedException(CreditPurchaseErrorCodes.TargetRequired,
                $"'{sku.Code}' applies to a guild, so it needs one naming.");
        }

        return EntitlementSubject.ForGuild(targetId.Trim());
    }

    /// <summary>The answer for a retry: the grant the original purchase produced, read back off the
    /// spend entries that recorded it.</summary>
    private async Task<CreditPurchase> ExistingPurchaseAsync(
        CreditSku sku, CreditLedgerResult deduction, CancellationToken cancellationToken)
    {
        var grantId = deduction.Entries.Select(entry => entry.GrantId).FirstOrDefault(id => id is not null);

        var grant = grantId is null
            ? null
            : await db.Grants.FirstOrDefaultAsync(row => row.Id == grantId, cancellationToken);

        if (grant is null)
        {
            // Unreachable while the two are written in one transaction, and loud rather than silent
            // because reaching it means they were not: a spend with no grant behind it is exactly the
            // "credit gone, nothing bought" state the single transaction exists to make impossible.
            throw new InvalidOperationException(
                $"A replayed purchase of '{sku.Code}' found spend entries with no grant behind them. "
                + "The deduction and the grant are supposed to be one transaction.");
        }

        return new CreditPurchase(
            sku,
            grant,
            deduction.Entries,
            deduction.Balance,
            Announcement: null,
            WasQueued: grant.StartsAt is not null,
            WasReplay: true);
    }
}
