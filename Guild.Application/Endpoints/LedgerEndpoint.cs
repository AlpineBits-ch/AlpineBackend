using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>Shared expenses on a <see cref="ChannelType.Ledger"/> channel.</summary>
[Authorize]
public class LedgerEndpoint
{
    private const int MaxDescriptionLength = 200;

    private static CategorizedExpenseDto ToDto(Expense expense, string currency) => new()
    {
        Id = expense.Id,
        Category = expense.Category,
        ChannelId = expense.ChannelId,
        PayerUserId = expense.PayerUserId,
        Description = expense.Description,
        AmountMinor = expense.AmountMinor,
        Currency = currency,
        OccurredAt = expense.OccurredAt,
        SplitKind = expense.SplitKind,
        CreatedByUserId = expense.CreatedByUserId,
        Shares = expense.Shares
            .OrderBy(s => s.UserId, StringComparer.Ordinal)
            .Select(s => new ExpenseShareEntryDto
            {
                UserId = s.UserId,
                ShareValue = s.ShareValue,
                AmountMinor = s.AmountMinor,
            }).ToList(),
    };

    /// <summary>The channel's expenses, newest first, in pages.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/expenses")]
    public async Task<IResult> ListAsync(string channelId, int? limit, string? cursor,
        ExpenseCategory? category,
        [NotBody] HouseholdChannelService household,
        [NotBody] LedgerService ledger, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        if (category is not null && !Enum.IsDefined(category.Value))
            return Results.BadRequest("Unknown category");

        var query = ctx.Expenses.AsNoTracking()
            .Include(e => e.Shares)
            .Where(e => e.ChannelId == channelId);

        if (category is not null) query = query.Where(e => e.Category == category.Value);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryParseCursor(cursor, out var beforeAt, out var beforeId))
                return Results.BadRequest("Malformed cursor");

            query = query.Where(e => e.OccurredAt < beforeAt
                                     || (e.OccurredAt == beforeAt && string.Compare(e.Id, beforeId) < 0));
        }

        // One extra row is the has-more probe, so the client never has to make a second request
        // just to learn the list ended.
        var page = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync();

        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var currency = await ledger.GetCurrencyAsync(channelId);

        return Results.Ok(new
        {
            Items = page.Select(e => ToDto(e, currency)).ToList(),
            NextCursor = hasMore && page.Count > 0
                ? $"{page[^1].OccurredAt:O}|{page[^1].Id}"
                : null,
        });
    }

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static bool TryParseCursor(string cursor, out DateTimeOffset occurredAt, out string id)
    {
        occurredAt = default;
        id = "";

        var separator = cursor.LastIndexOf('|');
        if (separator <= 0 || separator == cursor.Length - 1) return false;

        if (!DateTimeOffset.TryParse(cursor[..separator], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out occurredAt))
            return false;

        id = cursor[(separator + 1)..];
        return true;
    }

    [WolverinePost("/api/v1/channels/{channelId}/expenses")]
    public async Task<IResult> CreateAsync(string channelId, CreateCategorizedExpenseDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] HouseholdAlertService alerts, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.AddExpenses);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Description)) return Results.BadRequest("Description is required");
        if (dto.Description.Length > MaxDescriptionLength)
            return Results.BadRequest($"Description must be {MaxDescriptionLength} characters or fewer");
        if (dto.AmountMinor <= 0) return Results.BadRequest("AmountMinor must be greater than zero");

        // Checked rather than trusted because the enum arrives as a number for any client that is
        // not using the string names, and an out-of-range value would be stored, returned and then
        // silently dropped by the rollup's grouping - a category nobody can see and nobody can fix.
        if (dto.Category is not null && !Enum.IsDefined(dto.Category.Value))
            return Results.BadRequest("Unknown category");

        // Recording an expense someone else paid is normal (you enter the receipt they handed
        // you), but it moves money in their favour - so it needs the moderator permission.
        var payerUserId = dto.PayerUserId ?? userId;
        if (payerUserId != userId)
        {
            var onBehalf = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
            if (onBehalf.ToFailure() is { } onBehalfFailure) return onBehalfFailure;
        }

        // The participants are checked inside ApplySharesAsync; the payer never was, so a mistyped
        // or stale id became a creditor the house could never pay off.
        if (!await ledger.AreMembersAsync(access.Channel!.GuildId, payerUserId))
            return Results.BadRequest("The payer must be a member of this guild");

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = channelId,
            GuildId = access.Channel.GuildId,
            PayerUserId = payerUserId,
            Description = dto.Description.Trim(),
            AmountMinor = dto.AmountMinor,
            OccurredAt = dto.OccurredAt ?? DateTimeOffset.UtcNow,
            SplitKind = dto.SplitKind,
            CreatedByUserId = userId,
            Category = dto.Category ?? ExpenseCategory.Uncategorized,
        });

        var participants = dto.Shares
            .Select(s => new SplitParticipant(s.UserId, s.ShareValue))
            .ToList();

        if (await ledger.ApplySharesAsync(expense, dto.SplitKind, participants, access.Channel.GuildId) is { } error)
            return Results.BadRequest(error);

        ctx.Expenses.Add(expense);

        auditLog.Log(expense.GuildId, userId, AuditActionType.ExpenseCreated, expense.Id,
            new { expense.AmountMinor, expense.PayerUserId, expense.Description });

        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(channelId);

        await household.BroadcastAsync(expense.GuildId, channelId, "guild.ExpenseCreated",
            new { GuildId = expense.GuildId, ChannelId = channelId, Expense = ToDto(expense, currency) });

        // Not on update or delete: a correction to an amount is not worth a phone buzzing, and
        // editing a split repeatedly - which is normal while someone works out who was actually
        // there - would send one push per attempt.
        await alerts.ExpenseAddedAsync(expense, currency, userId);

        return Results.Ok(ToDto(expense, currency));
    }

    [WolverinePatch("/api/v1/expenses/{expenseId}")]
    public async Task<IResult> UpdateAsync(string expenseId, UpdateCategorizedExpenseDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var expense = await ctx.Expenses.Include(e => e.Shares).FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense is null) return Results.NotFound();

        // Fixing your own typo needs only AddExpenses; editing an expense someone else entered
        // changes what they're owed, so it's a moderator action.
        var required = expense.CreatedByUserId == userId ? Permissions.AddExpenses : Permissions.ManageLedger;
        var access = await household.ResolveAsync(expense.ChannelId, ChannelType.Ledger, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Description is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Description)) return Results.BadRequest("Description cannot be empty");
            if (dto.Description.Length > MaxDescriptionLength)
                return Results.BadRequest($"Description must be {MaxDescriptionLength} characters or fewer");
            expense.Description = dto.Description.Trim();
        }

        if (dto.AmountMinor is not null)
        {
            if (dto.AmountMinor <= 0) return Results.BadRequest("AmountMinor must be greater than zero");
            expense.AmountMinor = dto.AmountMinor.Value;
        }

        // Reassigning the payer is the same act as naming someone else as payer on create, and
        // needs the same permission.
        if (dto.PayerUserId is not null && dto.PayerUserId != expense.PayerUserId)
        {
            if (dto.PayerUserId != userId)
            {
                var reassign = await household.ResolveAsync(
                    expense.ChannelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
                if (reassign.ToFailure() is { } reassignFailure) return reassignFailure;
            }

            if (!await ledger.AreMembersAsync(expense.GuildId, dto.PayerUserId))
                return Results.BadRequest("The payer must be a member of this guild");

            expense.PayerUserId = dto.PayerUserId;
        }

        if (dto.OccurredAt is not null) expense.OccurredAt = dto.OccurredAt.Value;
        if (dto.SplitKind is not null) expense.SplitKind = dto.SplitKind.Value;

        if (dto.Category is not null)
        {
            if (!Enum.IsDefined(dto.Category.Value)) return Results.BadRequest("Unknown category");
            expense.Category = dto.Category.Value;
        }

        // Any change to the total or the split has to re-run the split, or the shares stop summing
        // to the expense and balances silently stop reconciling.
        var participants = dto.Shares is not null
            ? dto.Shares.Select(s => new SplitParticipant(s.UserId, s.ShareValue)).ToList()
            : expense.Shares.Select(s => new SplitParticipant(s.UserId, s.ShareValue)).ToList();

        if (await ledger.ApplySharesAsync(expense, expense.SplitKind, participants, expense.GuildId) is { } error)
            return Results.BadRequest(error);

        auditLog.Log(expense.GuildId, userId, AuditActionType.ExpenseUpdated, expense.Id,
            new { expense.AmountMinor, expense.PayerUserId, expense.Description });

        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(expense.ChannelId);

        await household.BroadcastAsync(expense.GuildId, expense.ChannelId, "guild.ExpenseUpdated",
            new { GuildId = expense.GuildId, ChannelId = expense.ChannelId, Expense = ToDto(expense, currency) });

        return Results.Ok(ToDto(expense, currency));
    }

    [WolverineDelete("/api/v1/expenses/{expenseId}")]
    public async Task<IResult> DeleteAsync(string expenseId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var expense = await ctx.Expenses.FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense is null) return Results.NotFound();

        var required = expense.CreatedByUserId == userId ? Permissions.AddExpenses : Permissions.ManageLedger;
        var access = await household.ResolveAsync(expense.ChannelId, ChannelType.Ledger, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        // Logged before the remove, so the entry still carries what was deleted rather than the
        // id of a row nobody can look up any more.
        auditLog.Log(expense.GuildId, userId, AuditActionType.ExpenseDeleted, expense.Id,
            new { expense.AmountMinor, expense.PayerUserId, expense.Description });

        ctx.Expenses.Remove(expense);   // shares cascade
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(expense.GuildId, expense.ChannelId, "guild.ExpenseDeleted",
            new { GuildId = expense.GuildId, ChannelId = expense.ChannelId, ExpenseId = expenseId });

        return Results.NoContent();
    }

    [WolverineGet("/api/v1/channels/{channelId}/ledger/balances")]
    public async Task<IResult> BalancesAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] LedgerService ledger, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var balances = await ledger.GetBalancesAsync(channelId);

        return Results.Ok(balances.Select(b => new LedgerBalanceDto { UserId = b.UserId, NetMinor = b.NetMinor }));
    }

    /// <summary>The minimal-ish set of payments that clears the house.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/ledger/settle-suggestion")]
    public async Task<IResult> SettleSuggestionAsync(string channelId,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var balances = await ledger.GetBalancesAsync(channelId);
        var transfers = DebtSimplifier.Simplify(balances);

        return Results.Ok(transfers.Select(t => new TransferSuggestionDto
        {
            FromUserId = t.FromUserId,
            ToUserId = t.ToUserId,
            AmountMinor = t.AmountMinor,
        }));
    }

    [WolverinePost("/api/v1/channels/{channelId}/ledger/settlements")]
    public async Task<IResult> RecordSettlementAsync(string channelId, CreateSettlementDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] HouseholdAlertService alerts, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // Recording a payment you made yourself is an ordinary action; recording one between two
        // other people rewrites their balances, so that needs ManageLedger.
        var required = dto.FromUserId == userId ? Permissions.AddExpenses : Permissions.ManageLedger;
        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.AmountMinor <= 0) return Results.BadRequest("AmountMinor must be greater than zero");
        if (dto.FromUserId == dto.ToUserId) return Results.BadRequest("A settlement needs two different people");

        // Both parties, for the same reason the payer is checked on create: a settlement naming
        // somebody who is not in the guild credits a person who cannot be paid and leaves the
        // counterparty permanently unsettleable.
        if (!await ledger.AreMembersAsync(access.Channel!.GuildId, dto.FromUserId, dto.ToUserId))
            return Results.BadRequest("Both parties to a settlement must be members of this guild");

        var settlement = new Settlement
        {
            Id = Settlement.GenerateId(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelId = channelId,
            GuildId = access.Channel.GuildId,
            FromUserId = dto.FromUserId,
            ToUserId = dto.ToUserId,
            AmountMinor = dto.AmountMinor,
            SettledAt = dto.SettledAt ?? DateTimeOffset.UtcNow,
            RecordedByUserId = userId,
        };

        ctx.Settlements.Add(settlement);

        auditLog.Log(settlement.GuildId, userId, AuditActionType.SettlementRecorded, settlement.Id,
            new { settlement.FromUserId, settlement.ToUserId, settlement.AmountMinor });

        await ctx.SaveChangesAsync();

        var settlementCurrency = await ledger.GetCurrencyAsync(channelId);
        await alerts.SettlementRecordedAsync(settlement, settlementCurrency, userId);

        await household.BroadcastAsync(settlement.GuildId, channelId, "guild.SettlementRecorded", new
        {
            GuildId = settlement.GuildId,
            ChannelId = channelId,
            Settlement = new SettlementDto
            {
                Id = settlement.Id,
                FromUserId = settlement.FromUserId,
                ToUserId = settlement.ToUserId,
                AmountMinor = settlement.AmountMinor,
                SettledAt = settlement.SettledAt,
                RecordedByUserId = settlement.RecordedByUserId,
            },
        });

        return Results.Ok(new SettlementDto
        {
            Id = settlement.Id,
            FromUserId = settlement.FromUserId,
            ToUserId = settlement.ToUserId,
            AmountMinor = settlement.AmountMinor,
            SettledAt = settlement.SettledAt,
            RecordedByUserId = settlement.RecordedByUserId,
        });
    }

    [WolverineGet("/api/v1/channels/{channelId}/ledger/config")]
    public async Task<IResult> GetConfigAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] LedgerService ledger, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        return Results.Ok(new { ChannelId = channelId, Currency = await ledger.GetCurrencyAsync(channelId) });
    }

    [WolverinePut("/api/v1/channels/{channelId}/ledger/config")]
    public async Task<IResult> UpdateConfigAsync(string channelId, UpdateLedgerConfigDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
        if (access.ToFailure() is { } failure) return failure;

        var currency = dto.Currency?.Trim().ToUpperInvariant();
        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            return Results.BadRequest("Currency must be a three-letter ISO-4217 code");

        var config = await ctx.LedgerConfigs.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (config is null)
        {
            config = new LedgerConfig { ChannelId = channelId, GuildId = access.Channel!.GuildId };
            ctx.LedgerConfigs.Add(config);
        }

        // Deliberately does not convert existing amounts: this changes the label, not the money.
        var previous = config.Currency;
        config.Currency = currency;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(access.Channel!.GuildId, userId, AuditActionType.LedgerConfigUpdated, channelId,
            new { From = previous, To = currency });

        await ctx.SaveChangesAsync();

        return Results.Ok(new { ChannelId = channelId, config.Currency });
    }
}
