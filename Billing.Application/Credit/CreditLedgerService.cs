using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billing.Application.Credit;

/// <summary>Machine-readable refusals from the credit ledger.</summary>
public static class CreditErrorCodes
{
    public const string UserRequired = "user_required";
    public const string ReasonRequired = "reason_required";
    public const string ActorRequired = "actor_required";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string AmountNotPositive = "amount_not_positive";
    public const string AmountZero = "amount_zero";
    public const string InsufficientBalance = "insufficient_balance";
    public const string WalletCapExceeded = "wallet_cap_exceeded";
    public const string UnknownCampaign = "unknown_campaign";
    public const string CampaignClosed = "campaign_closed";
    public const string CampaignBudgetExhausted = "campaign_budget_exhausted";
    public const string CampaignPerUserCapReached = "campaign_per_user_cap_reached";
    public const string EntryNotFound = "entry_not_found";

    /// <summary>An attempt to reverse something that is not an issuance.</summary>
    public const string NotReversible = "not_reversible";

    public const string NothingToReverse = "nothing_to_reverse";
}

/// <summary>A credit operation was refused.</summary>
public sealed class CreditRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What is left in one lot, and when it lapses.</summary>
public sealed record CreditLotRemainder(
    string LotId,
    DateTimeOffset ExpiresAt,
    long OriginalAmount,
    long Remaining,
    string? CampaignId);

/// <summary>An issuance request.</summary>
public sealed record IssueCredit(
    string UserId,
    long Amount,
    string Reason,
    string IdempotencyKey,
    string? Campaign = null,
    string? CreatedBy = null,
    DateTimeOffset? ExpiresAt = null,
    int? LifetimeDays = null);

/// <summary>The result of a write against the ledger: the rows it produced and the balance
/// afterwards, so a caller never has to re-read to render an answer.</summary>
public sealed record CreditLedgerResult(
    IReadOnlyList<CreditEntry> Entries,
    long Balance,
    bool WasReplay);

/// <summary>The credit wallet and its lot ledger (monetization.md section 8.5).</summary>
public sealed class CreditLedgerService(
    MicroserviceContext db,
    IOptions<CreditOptions> options,
    TimeProvider clock,
    ILogger<CreditLedgerService>? logger = null)
{
    private readonly CreditOptions _options = options?.Value ?? new CreditOptions();

    /// <summary>
    /// Creates the wallet if it is missing and locks it either way, in one statement.
    /// </summary>
    private const string LockWalletSql =
        """
        INSERT INTO credit_wallets (id, user_id, cached_balance, cached_at, created_at, updated_at)
        VALUES ({0}, {1}, 0, {2}, {2}, {2})
        ON CONFLICT (user_id)
        DO UPDATE SET updated_at = {2}
        RETURNING cached_balance AS "Value"
        """;

    public CreditOptions Options => _options;

    /// <summary>
    /// The balance, from the entries, which is the definition rather than an implementation choice.
    /// </summary>
    public async Task<long> BalanceAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return await db.CreditEntries
            .Where(entry => entry.UserId == userId)
            .SumAsync(entry => (long?)entry.Amount, cancellationToken) ?? 0;
    }

    /// <summary>
    /// The read path's balance: the cache, or zero for an account that has never held any.
    /// </summary>
    public async Task<long> CachedBalanceAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var wallet = await db.CreditWallets
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.UserId == userId, cancellationToken);

        return wallet?.CachedBalance ?? 0;
    }

    /// <summary>Lots with something left in them, earliest-expiring first.</summary>
    public async Task<IReadOnlyList<CreditLotRemainder>> OpenLotsAsync(
        string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Projected into an anonymous type rather than straight into the record, because Npgsql
        // cannot see through a positional record's constructor to filter on one of its members and
        // refuses to translate the query at all.
        var rows = await db.CreditLots
            .Where(lot => lot.UserId == userId)
            .Select(lot => new
            {
                lot.Id,
                lot.ExpiresAt,
                lot.OriginalAmount,
                lot.CampaignId,
                Remaining = db.CreditEntries
                    .Where(entry => entry.LotId == lot.Id)
                    .Sum(entry => (long?)entry.Amount) ?? 0,
            })
            .Where(row => row.Remaining > 0)
            .OrderBy(row => row.ExpiresAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CreditLotRemainder(
                row.Id, row.ExpiresAt, row.OriginalAmount, row.Remaining, row.CampaignId))
            .ToList();
    }

    /// <summary>The whole ledger for one account, newest first.</summary>
    public async Task<IReadOnlyList<CreditEntry>> LedgerAsync(
        string userId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return await db.CreditEntries
            .Where(entry => entry.UserId == userId)
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Hands out credit.</summary>
    public async Task<CreditLedgerResult> IssueAsync(
        IssueCredit request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Require(request.UserId, CreditErrorCodes.UserRequired, "An issuance needs an account to credit.");
        Require(request.IdempotencyKey, CreditErrorCodes.IdempotencyKeyRequired,
            "An issuance needs an idempotency key. It is what stops a retry from paying twice.");
        Require(request.Reason, CreditErrorCodes.ReasonRequired,
            "An issuance needs a reason. Credit that appeared for no recorded purpose cannot be "
            + "defended to the person who receives it or to the person who audits it.");

        if (request.Amount <= 0)
        {
            throw new CreditRefusedException(CreditErrorCodes.AmountNotPositive,
                $"An issuance of {request.Amount} points is not an issuance. Use an adjustment with a "
                + "reason to take credit away.");
        }

        return await InTransactionAsync(async ct =>
        {
            if (await ReplayAsync(request.UserId, request.IdempotencyKey, ct) is { } replay) return replay;

            var now = clock.GetUtcNow();
            await LockWalletAsync(request.UserId, now, ct);

            var campaign = await LockCampaignAsync(request.Campaign, ct);
            var balance = await BalanceAsync(request.UserId, ct);

            // Before the campaign is charged, so a refused issuance does not consume budget.
            EnsureWithinWalletCap(balance, request.Amount, request.UserId);

            if (campaign is not null) await ChargeCampaignAsync(campaign, request, now, ct);

            var lifetime = request.LifetimeDays
                           ?? campaign?.LotLifetimeDays
                           ?? _options.DefaultLotLifetimeDays;

            var lot = new CreditLot
            {
                Id = CreditLot.GenerateId(),
                UserId = request.UserId,
                OriginalAmount = request.Amount,
                ExpiresAt = request.ExpiresAt ?? now.AddDays(lifetime),
                CampaignId = campaign?.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.CreditLots.Add(lot);

            var entry = NewEntry(
                request.UserId, request.Amount, CreditEntryKind.Issue,
                DerivedKey(request.IdempotencyKey, 0), now,
                lotId: lot.Id, campaignId: campaign?.Id, reason: request.Reason.Trim(),
                createdBy: request.CreatedBy);

            db.CreditEntries.Add(entry);

            return await FinishAsync(request.UserId, balance + request.Amount, [entry], now, ct);
        }, cancellationToken);
    }

    /// <summary>Takes credit out, earliest-expiring lot first.</summary>
    public async Task<CreditLedgerResult> DeductAsync(
        string userId,
        long amount,
        string idempotencyKey,
        string? grantId,
        string? reason,
        CancellationToken cancellationToken)
    {
        Require(userId, CreditErrorCodes.UserRequired, "A spend needs an account to draw from.");
        Require(idempotencyKey, CreditErrorCodes.IdempotencyKeyRequired,
            "A spend needs an idempotency key. It is what stops a retry from deducting twice.");

        if (amount <= 0)
        {
            throw new CreditRefusedException(CreditErrorCodes.AmountNotPositive,
                $"A spend of {amount} points is not a spend.");
        }

        return await InTransactionAsync(async ct =>
        {
            if (await ReplayAsync(userId, idempotencyKey, ct) is { } replay) return replay;

            var now = clock.GetUtcNow();
            await LockWalletAsync(userId, now, ct);

            var balance = await BalanceAsync(userId, ct);

            if (balance < amount)
            {
                throw new CreditRefusedException(CreditErrorCodes.InsufficientBalance,
                    $"This costs {amount} credits and the balance is {balance}. Credit has no cash "
                    + "value and cannot be topped up with money, so the only way to more is to be "
                    + "given more.");
            }

            var entries = ConsumeFifo(
                userId, amount, idempotencyKey, CreditEntryKind.Spend, now,
                await OpenLotsAsync(userId, ct), grantId, reason, createdBy: null);

            return await FinishAsync(userId, balance - amount, entries, now, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// A hand correction, in either direction, with a reason that is not optional.
    /// </summary>
    public async Task<CreditLedgerResult> AdjustAsync(
        string userId,
        long amount,
        string reason,
        string createdBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Require(userId, CreditErrorCodes.UserRequired, "An adjustment needs an account.");
        Require(reason, CreditErrorCodes.ReasonRequired,
            "An adjustment needs a reason. It is a number somebody typed, and next year it has to be "
            + "explainable.");
        Require(createdBy, CreditErrorCodes.ActorRequired,
            "An adjustment records the staff account that made it, and this request carried none.");
        Require(idempotencyKey, CreditErrorCodes.IdempotencyKeyRequired,
            "An adjustment needs an idempotency key.");

        if (amount == 0)
        {
            throw new CreditRefusedException(CreditErrorCodes.AmountZero,
                "An adjustment of zero changes nothing and explains nothing.");
        }

        return await InTransactionAsync(async ct =>
        {
            if (await ReplayAsync(userId, idempotencyKey, ct) is { } replay) return replay;

            var now = clock.GetUtcNow();
            await LockWalletAsync(userId, now, ct);

            var balance = await BalanceAsync(userId, ct);

            if (amount > 0)
            {
                EnsureWithinWalletCap(balance, amount, userId);

                var lot = new CreditLot
                {
                    Id = CreditLot.GenerateId(),
                    UserId = userId,
                    OriginalAmount = amount,
                    ExpiresAt = now.AddDays(_options.DefaultLotLifetimeDays),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                db.CreditLots.Add(lot);

                var credited = NewEntry(
                    userId, amount, CreditEntryKind.Adjustment, DerivedKey(idempotencyKey, 0), now,
                    lotId: lot.Id, campaignId: null, reason: reason.Trim(), createdBy: createdBy);

                db.CreditEntries.Add(credited);

                return await FinishAsync(userId, balance + amount, [credited], now, ct);
            }

            var wanted = -amount;

            if (balance < wanted)
            {
                throw new CreditRefusedException(CreditErrorCodes.InsufficientBalance,
                    $"This would take {wanted} credits off a balance of {balance}, which would leave it "
                    + "negative. The ledger has no such state.");
            }

            var debited = ConsumeFifo(
                userId, wanted, idempotencyKey, CreditEntryKind.Adjustment, now,
                await OpenLotsAsync(userId, ct), grantId: null, reason: reason.Trim(), createdBy);

            return await FinishAsync(userId, balance - wanted, debited, now, ct);
        }, cancellationToken);
    }

    /// <summary>Takes back an issuance that should not have happened.</summary>
    public async Task<CreditLedgerResult> ReverseEntryAsync(
        string entryId, string reason, string actor, CancellationToken cancellationToken)
    {
        Require(reason, CreditErrorCodes.ReasonRequired, "A reversal needs a reason.");
        Require(actor, CreditErrorCodes.ActorRequired,
            "A reversal records the staff account that made it, and this request carried none.");

        return await InTransactionAsync(async ct =>
        {
            var original = await db.CreditEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct)
                           ?? throw new CreditRefusedException(CreditErrorCodes.EntryNotFound,
                               $"No credit entry {entryId}.");

            if (original.Amount <= 0)
            {
                throw new CreditRefusedException(CreditErrorCodes.NotReversible,
                    "Only an issuance can be reversed. Reversing a spend would hand the credit back "
                    + "for a grant that has already been given, which is a refund - and credit is "
                    + "never refundable or convertible. Revoke the grant instead.");
            }

            var key = $"reverse:{original.Id}";
            if (await ReplayAsync(original.UserId, key, ct) is { } replay) return replay;

            var now = clock.GetUtcNow();
            await LockWalletAsync(original.UserId, now, ct);

            var balance = await BalanceAsync(original.UserId, ct);
            var lots = await OpenLotsAsync(original.UserId, ct);
            var lot = lots.FirstOrDefault(candidate => candidate.LotId == original.LotId);

            if (lot is null || lot.Remaining <= 0)
            {
                throw new CreditRefusedException(CreditErrorCodes.NothingToReverse,
                    "That issuance has already been spent, expired or reversed, so there is nothing "
                    + "left in its lot to take back. The entries stay either way.");
            }

            var amount = Math.Min(original.Amount, lot.Remaining);

            var reversal = NewEntry(
                original.UserId, -amount, CreditEntryKind.Reversal, DerivedKey(key, 0), now,
                lotId: lot.LotId, campaignId: original.CampaignId, reason: reason.Trim(), createdBy: actor);

            db.CreditEntries.Add(reversal);

            return await FinishAsync(original.UserId, balance - amount, [reversal], now, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Empties a wallet because the account behind it was banned for fraud (section 8.6).
    /// </summary>
    public async Task<CreditLedgerResult> VoidForFraudAsync(
        string userId, string reason, string actor, CancellationToken cancellationToken)
    {
        Require(userId, CreditErrorCodes.UserRequired, "A void needs an account.");
        Require(reason, CreditErrorCodes.ReasonRequired, "A void needs a reason.");
        Require(actor, CreditErrorCodes.ActorRequired,
            "A void records the staff account that made it, and this request carried none.");

        return await InTransactionAsync(async ct =>
        {
            var now = clock.GetUtcNow();
            await LockWalletAsync(userId, now, ct);

            var balance = await BalanceAsync(userId, ct);
            var lots = await OpenLotsAsync(userId, ct);

            var entries = new List<CreditEntry>(lots.Count);

            foreach (var lot in lots)
            {
                var reversal = NewEntry(
                    userId, -lot.Remaining, CreditEntryKind.Reversal,
                    DerivedKey($"fraud-void:{lot.LotId}", 0), now,
                    lotId: lot.LotId, campaignId: lot.CampaignId, reason: reason.Trim(), createdBy: actor);

                db.CreditEntries.Add(reversal);
                entries.Add(reversal);
            }

            logger?.LogWarning(
                "Staff {Actor} voided {Points} credit(s) across {Lots} lot(s) for {UserId}: {Reason}",
                actor, balance, lots.Count, userId, reason);

            return await FinishAsync(userId, 0, entries, now, ct);
        }, cancellationToken);
    }

    /// <summary>Recomputes the cached balance from the entries.</summary>
    public async Task<long> RebuildAsync(string userId, CancellationToken cancellationToken)
    {
        Require(userId, CreditErrorCodes.UserRequired, "A rebuild needs an account.");

        return await InTransactionAsync(async ct =>
        {
            var now = clock.GetUtcNow();
            await LockWalletAsync(userId, now, ct);

            var balance = await BalanceAsync(userId, ct);
            await WriteCacheAsync(userId, balance, now, ct);

            return balance;
        }, cancellationToken);
    }

    /// <summary>
    /// Writes the <see cref="CreditEntryKind.Expiry"/> lines for lots that have lapsed.
    /// </summary>
    public async Task<IReadOnlyList<CreditEntry>> ExpireLotsAsync(
        int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var lapsed = await db.CreditLots
            .Where(lot => lot.ExpiresAt <= now)
            .Select(lot => new
            {
                lot.Id,
                lot.UserId,
                Remaining = db.CreditEntries
                    .Where(entry => entry.LotId == lot.Id)
                    .Sum(entry => (long?)entry.Amount) ?? 0,
            })
            .Where(lot => lot.Remaining > 0)
            .OrderBy(lot => lot.Id)
            .Take(Math.Clamp(batchSize, 1, 1000))
            .ToListAsync(cancellationToken);

        var written = new List<CreditEntry>(lapsed.Count);

        // One transaction per account rather than one for the whole batch.
        foreach (var group in lapsed.GroupBy(lot => lot.UserId))
        {
            var userId = group.Key;

            var entries = await InTransactionAsync(async ct =>
            {
                await LockWalletAsync(userId, now, ct);

                var balance = await BalanceAsync(userId, ct);
                var open = (await OpenLotsAsync(userId, ct)).ToDictionary(lot => lot.LotId);
                var produced = new List<CreditEntry>();
                var expired = 0L;

                foreach (var lot in group)
                {
                    // Re-read under the lock.
                    if (!open.TryGetValue(lot.Id, out var remainder) || remainder.Remaining <= 0) continue;

                    var entry = NewEntry(
                        userId, -remainder.Remaining, CreditEntryKind.Expiry,
                        DerivedKey($"expiry:{lot.Id}", 0), now,
                        lotId: lot.Id, campaignId: remainder.CampaignId, reason: null, createdBy: null);

                    db.CreditEntries.Add(entry);
                    produced.Add(entry);
                    expired += remainder.Remaining;
                }

                var result = await FinishAsync(userId, balance - expired, produced, now, ct);
                return result.Entries;
            }, cancellationToken);

            written.AddRange(entries);
        }

        return written;
    }

    /// <summary>
    /// Claims the lots that are close enough to expiry to be worth telling somebody about, and
    /// stamps them so nobody else claims them again.
    /// </summary>
    public async Task<IReadOnlyList<CreditLot>> ClaimExpiryWarningsAsync(
        int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var horizon = now.AddDays(_options.ExpiryWarningDays);

        var due = await db.CreditLots
            .Where(lot => lot.ExpiryWarningSentAt == null
                          && lot.ExpiresAt > now
                          && lot.ExpiresAt <= horizon)
            .Select(lot => new
            {
                Lot = lot,
                Remaining = db.CreditEntries
                    .Where(entry => entry.LotId == lot.Id)
                    .Sum(entry => (long?)entry.Amount) ?? 0,
            })
            .Where(row => row.Remaining > 0)
            .OrderBy(row => row.Lot.ExpiresAt)
            .Take(Math.Clamp(batchSize, 1, 1000))
            .ToListAsync(cancellationToken);

        var claimed = new List<CreditLot>(due.Count);

        foreach (var row in due)
        {
            var won = await db.CreditLots
                .Where(lot => lot.Id == row.Lot.Id && lot.ExpiryWarningSentAt == null)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(lot => lot.ExpiryWarningSentAt, now)
                        .SetProperty(lot => lot.UpdatedAt, now),
                    cancellationToken);

            if (won == 1) claimed.Add(row.Lot);
        }

        return claimed;
    }

    // ── internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the body inside a transaction, joining one if the caller already opened it.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            var joined = await body(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return joined;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var result = await body(cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    private async Task LockWalletAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Database
            .SqlQueryRaw<long>(LockWalletSql, CreditWallet.GenerateId(), userId, now)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Locked only after the wallet, never before.</summary>
    private async Task<CreditCampaign?> LockCampaignAsync(
        string? campaign, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(campaign)) return null;

        var key = campaign.Trim();

        // Either identifier, because a campaign is addressed by its code by the humans who run it
        // and by its id by the rows that reference it, and making the caller convert would be a
        // second round trip for no gain.
        var found = await db.CreditCampaigns
            .FromSql($"SELECT * FROM credit_campaigns WHERE id = {key} OR code = {key} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return found.FirstOrDefault() ?? throw new CreditRefusedException(
            CreditErrorCodes.UnknownCampaign, $"'{key}' is not a credit campaign on this instance.");
    }

    /// <summary>The budget check, and the only place it happens.</summary>
    private async Task ChargeCampaignAsync(
        CreditCampaign campaign, IssueCredit request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!campaign.IsOpenAt(now))
        {
            throw new CreditRefusedException(CreditErrorCodes.CampaignClosed,
                campaign.IsPaused
                    ? $"Campaign '{campaign.Code}' is paused."
                    : $"Campaign '{campaign.Code}' is outside its validity window.");
        }

        if (campaign.IssuedPoints + request.Amount > campaign.TotalBudgetPoints)
        {
            logger?.LogError(
                "Campaign {Code} refused an issuance of {Amount} points: {Issued} of {Budget} already "
                + "spent. Raise the budget deliberately or leave it stopped - an automated campaign "
                + "that quietly kept issuing is the five-figure failure this cap exists for.",
                campaign.Code, request.Amount, campaign.IssuedPoints, campaign.TotalBudgetPoints);

            throw new CreditRefusedException(CreditErrorCodes.CampaignBudgetExhausted,
                $"Campaign '{campaign.Code}' has {campaign.RemainingPoints} of its "
                + $"{campaign.TotalBudgetPoints} point budget left, which does not cover "
                + $"{request.Amount}.");
        }

        if (campaign.MaxPerUserPoints is { } perUser)
        {
            var campaignId = campaign.Id;
            var userId = request.UserId;

            var already = await db.CreditEntries
                .Where(entry => entry.CampaignId == campaignId
                                && entry.UserId == userId
                                && entry.Kind == CreditEntryKind.Issue)
                .SumAsync(entry => (long?)entry.Amount, cancellationToken) ?? 0;

            if (already + request.Amount > perUser)
            {
                throw new CreditRefusedException(CreditErrorCodes.CampaignPerUserCapReached,
                    $"Campaign '{campaign.Code}' allows {perUser} points per account and this one has "
                    + $"already had {already}.");
            }
        }

        campaign.IssuedPoints += request.Amount;

        if (campaign.ShouldAlert())
        {
            campaign.AlertedAt = now;

            logger?.LogWarning(
                "Campaign {Code} has issued {Issued} of its {Budget} point budget, past its "
                + "{Threshold}% alert. {Remaining} left.",
                campaign.Code, campaign.IssuedPoints, campaign.TotalBudgetPoints,
                campaign.AlertThresholdPercent, campaign.RemainingPoints);
        }
    }

    private void EnsureWithinWalletCap(long balance, long amount, string userId)
    {
        var cap = _options.MaxWalletBalancePoints;
        if (cap <= 0 || balance + amount <= cap) return;

        throw new CreditRefusedException(CreditErrorCodes.WalletCapExceeded,
            $"Account {userId} holds {balance} credits and this would take it to {balance + amount}, "
            + $"past the {cap} cap. There is no legitimate reason to hold a balance that size, so the "
            + "issuance is refused rather than trimmed - a trim would spend the campaign's budget on "
            + "points nobody received.");
    }

    /// <summary>Walks the open lots earliest-expiring first, producing one entry per lot it draws
    /// from. The caller has already checked that the balance covers the amount, under the lock.
    /// </summary>
    private List<CreditEntry> ConsumeFifo(
        string userId,
        long amount,
        string idempotencyKey,
        CreditEntryKind kind,
        DateTimeOffset now,
        IReadOnlyList<CreditLotRemainder> lots,
        string? grantId,
        string? reason,
        string? createdBy)
    {
        var entries = new List<CreditEntry>();
        var outstanding = amount;

        foreach (var lot in lots)
        {
            if (outstanding <= 0) break;

            var taken = Math.Min(outstanding, lot.Remaining);
            outstanding -= taken;

            entries.Add(NewEntry(
                userId, -taken, kind, DerivedKey(idempotencyKey, entries.Count), now,
                lotId: lot.LotId, campaignId: lot.CampaignId, reason: reason, createdBy: createdBy,
                grantId: grantId));
        }

        if (outstanding > 0)
        {
            // Unreachable while the lots and the balance are read under the same lock, and thrown
            // rather than ignored because reaching it means the two disagree - which is the one state
            // a ledger cannot be quietly in.
            throw new InvalidOperationException(
                $"The lots for {userId} came up {outstanding} points short of a {amount} point "
                + "deduction that the balance said was affordable. The wallet lock did not hold.");
        }

        foreach (var entry in entries) db.CreditEntries.Add(entry);

        return entries;
    }

    /// <summary>
    /// One operation can produce several rows and the unique index is on a single column, so the
    /// keys are derived from the caller's key by ordinal.
    /// </summary>
    private static string DerivedKey(string idempotencyKey, int ordinal) => $"{idempotencyKey}:{ordinal}";

    /// <summary>
    /// Whether this exact request has already been applied, and what it produced if so.
    /// </summary>
    private async Task<CreditLedgerResult?> ReplayAsync(
        string userId, string idempotencyKey, CancellationToken cancellationToken)
    {
        // Detected on an exact match rather than on the prefix.
        var first = DerivedKey(idempotencyKey, 0);

        var seen = await db.CreditEntries
            .AnyAsync(entry => entry.UserId == userId && entry.IdempotencyKey == first, cancellationToken);

        if (!seen) return null;

        var prefix = $"{idempotencyKey}:";

        var existing = await db.CreditEntries
            .Where(entry => entry.UserId == userId && entry.IdempotencyKey.StartsWith(prefix))
            .OrderBy(entry => entry.IdempotencyKey)
            .ToListAsync(cancellationToken);

        return new CreditLedgerResult(existing, await BalanceAsync(userId, cancellationToken), WasReplay: true);
    }

    private CreditEntry NewEntry(
        string userId,
        long amount,
        CreditEntryKind kind,
        string idempotencyKey,
        DateTimeOffset now,
        string? lotId,
        string? campaignId,
        string? reason,
        string? createdBy,
        string? grantId = null) => new()
    {
        Id = CreditEntry.GenerateId(),
        UserId = userId,
        Amount = amount,
        Kind = kind,
        LotId = lotId,
        CampaignId = campaignId,
        GrantId = grantId,
        // Single-row operations pass their caller's key straight through DerivedKey too, so every
        // key in the table has the same shape and none of them can collide with a caller's raw
        // string.
        IdempotencyKey = idempotencyKey,
        Reason = reason,
        CreatedBy = createdBy,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Updates the cache and answers with what was written.</summary>
    private async Task<CreditLedgerResult> FinishAsync(
        string userId,
        long balance,
        IReadOnlyList<CreditEntry> entries,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await WriteCacheAsync(userId, balance, now, cancellationToken);
        return new CreditLedgerResult(entries, balance, WasReplay: false);
    }

    private async Task WriteCacheAsync(
        string userId, long balance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.CreditWallets
            .Where(wallet => wallet.UserId == userId)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(wallet => wallet.CachedBalance, balance)
                    .SetProperty(wallet => wallet.CachedAt, now)
                    .SetProperty(wallet => wallet.UpdatedAt, now),
                cancellationToken);
    }

    private static void Require(string? value, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new CreditRefusedException(code, message);
    }
}
