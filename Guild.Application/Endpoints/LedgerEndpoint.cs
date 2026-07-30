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

    private static ExpenseDto ToDto(Expense expense, string currency) => new()
    {
        Id = expense.Id,
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

    [WolverineGet("/api/v1/channels/{channelId}/expenses")]
    public async Task<IResult> ListAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] LedgerService ledger, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var expenses = await ctx.Expenses.AsNoTracking()
            .Include(e => e.Shares)
            .Where(e => e.ChannelId == channelId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync();

        var currency = await ledger.GetCurrencyAsync(channelId, access.Channel!.GuildId);
        return Results.Ok(expenses.Select(e => ToDto(e, currency)));
    }

    [WolverinePost("/api/v1/channels/{channelId}/expenses")]
    public async Task<IResult> CreateAsync(string channelId, CreateExpenseDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.AddExpenses);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Description)) return Results.BadRequest("Description is required");
        if (dto.Description.Length > MaxDescriptionLength)
            return Results.BadRequest($"Description must be {MaxDescriptionLength} characters or fewer");
        if (dto.AmountMinor <= 0) return Results.BadRequest("AmountMinor must be greater than zero");

        // Recording an expense someone else paid is normal (you enter the receipt they handed
        // you), but it moves money in their favour - so it needs the moderator permission.
        var payerUserId = dto.PayerUserId ?? userId;
        if (payerUserId != userId)
        {
            var onBehalf = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
            if (onBehalf.ToFailure() is { } onBehalfFailure) return onBehalfFailure;
        }

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            PayerUserId = payerUserId,
            Description = dto.Description.Trim(),
            AmountMinor = dto.AmountMinor,
            OccurredAt = dto.OccurredAt ?? DateTimeOffset.UtcNow,
            SplitKind = dto.SplitKind,
            CreatedByUserId = userId,
        });

        var participants = dto.Shares
            .Select(s => new SplitParticipant(s.UserId, s.ShareValue))
            .ToList();

        if (await ledger.ApplySharesAsync(expense, dto.SplitKind, participants, access.Channel.GuildId) is { } error)
            return Results.BadRequest(error);

        ctx.Expenses.Add(expense);
        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(channelId, access.Channel.GuildId);

        await household.BroadcastAsync(expense.GuildId, "guild.ExpenseCreated",
            new { GuildId = expense.GuildId, ChannelId = channelId, Expense = ToDto(expense, currency) });

        return Results.Ok(ToDto(expense, currency));
    }

    [WolverinePatch("/api/v1/expenses/{expenseId}")]
    public async Task<IResult> UpdateAsync(string expenseId, UpdateExpenseDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
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

        if (dto.PayerUserId is not null) expense.PayerUserId = dto.PayerUserId;
        if (dto.OccurredAt is not null) expense.OccurredAt = dto.OccurredAt.Value;
        if (dto.SplitKind is not null) expense.SplitKind = dto.SplitKind.Value;

        // Any change to the total or the split has to re-run the split, or the shares stop summing
        // to the expense and balances silently stop reconciling.
        var participants = dto.Shares is not null
            ? dto.Shares.Select(s => new SplitParticipant(s.UserId, s.ShareValue)).ToList()
            : expense.Shares.Select(s => new SplitParticipant(s.UserId, s.ShareValue)).ToList();

        if (await ledger.ApplySharesAsync(expense, expense.SplitKind, participants, expense.GuildId) is { } error)
            return Results.BadRequest(error);

        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(expense.ChannelId, expense.GuildId);

        await household.BroadcastAsync(expense.GuildId, "guild.ExpenseUpdated",
            new { GuildId = expense.GuildId, ChannelId = expense.ChannelId, Expense = ToDto(expense, currency) });

        return Results.Ok(ToDto(expense, currency));
    }

    [WolverineDelete("/api/v1/expenses/{expenseId}")]
    public async Task<IResult> DeleteAsync(string expenseId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var expense = await ctx.Expenses.FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense is null) return Results.NotFound();

        var required = expense.CreatedByUserId == userId ? Permissions.AddExpenses : Permissions.ManageLedger;
        var access = await household.ResolveAsync(expense.ChannelId, ChannelType.Ledger, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        ctx.Expenses.Remove(expense);   // shares cascade
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(expense.GuildId, "guild.ExpenseDeleted",
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
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
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

        var settlement = new Settlement
        {
            Id = Settlement.GenerateId(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            FromUserId = dto.FromUserId,
            ToUserId = dto.ToUserId,
            AmountMinor = dto.AmountMinor,
            SettledAt = dto.SettledAt ?? DateTimeOffset.UtcNow,
            RecordedByUserId = userId,
        };

        ctx.Settlements.Add(settlement);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(settlement.GuildId, "guild.SettlementRecorded", new
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

        return Results.Ok(new { ChannelId = channelId, Currency = await ledger.GetCurrencyAsync(channelId, access.Channel!.GuildId) });
    }

    [WolverinePut("/api/v1/channels/{channelId}/ledger/config")]
    public async Task<IResult> UpdateConfigAsync(string channelId, UpdateLedgerConfigDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
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
        config.Currency = currency;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();

        return Results.Ok(new { ChannelId = channelId, config.Currency });
    }
}
