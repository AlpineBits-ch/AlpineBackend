using System.Security.Claims;
using Billing.Application.Credit;
using Billing.Application.Notifications;
using Billing.Application.Security;
using Identity.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>The staff credit surface of monetization.md section 8.8.</summary>
public class CreditAdminEndpoint
{
    /// <summary>A wallet: balance, cap, and every lot with something left in it.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/credit/wallets/{userId}")]
    public static async Task<IResult> WalletAsync(
        string userId,
        [NotBody] CreditLedgerService ledger,
        CancellationToken cancellationToken)
    {
        var lots = await ledger.OpenLotsAsync(userId, cancellationToken);

        return Results.Ok(new CreditWalletDto(
            userId,
            await ledger.BalanceAsync(userId, cancellationToken),
            ledger.Options.MaxWalletBalancePoints,
            lots.Select(CreditLotDto.From).ToList(),
            CreditDisclaimer.Text,
            CreditDisclaimer.LocalisationKey,
            null));
    }

    /// <summary>The full ledger with its provenance - which lot, which campaign, which grant, which
    /// member of staff. Section 8.8 puts this first among the admin surfaces, and it is the reason
    /// the ledger is append-only at all.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/credit/wallets/{userId}/ledger")]
    public static async Task<IResult> LedgerAsync(
        string userId,
        int limit,
        [NotBody] CreditLedgerService ledger,
        CancellationToken cancellationToken)
    {
        var entries = await ledger.LedgerAsync(userId, limit <= 0 ? 200 : limit, cancellationToken);

        return Results.Ok(new CreditLedgerDto(
            userId,
            await ledger.BalanceAsync(userId, cancellationToken),
            entries.Select(entry => CreditEntryDto.From(entry)).ToList(),
            CreditDisclaimer.Text,
            CreditDisclaimer.LocalisationKey));
    }

    /// <summary>Hands somebody credit, with a mandatory reason.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/wallets/{userId}/issue")]
    public static async Task<(IResult, CreditIssuedNotification?)> IssueAsync(
        string userId,
        IssueCreditRequest request,
        [NotBody] CreditLedgerService ledger,
        [NotBody] TimeProvider clock,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<CreditAdminEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), null);

        try
        {
            var result = await ledger.IssueAsync(
                new IssueCredit(
                    userId,
                    request.Amount,
                    request.Reason,
                    request.IdempotencyKey,
                    request.Campaign,
                    actor,
                    request.ExpiresAt,
                    request.LifetimeDays),
                cancellationToken);

            logger.LogInformation(
                "Staff {Actor} issued {Amount} credits to {UserId} under {Campaign}: {Reason}",
                actor, request.Amount, userId, request.Campaign ?? "no campaign", request.Reason);

            // Read back for the lot's expiry date, which is the one fact the mail needs that the write
            // result does not carry. Skipped entirely on a replay, where there is no mail to build.
            var lots = result.WasReplay
                ? []
                : await ledger.OpenLotsAsync(userId, cancellationToken);

            return (
                Results.Ok(Written(result)),
                BillingNotices.ForCreditIssue(
                    userId, result, lots, request.Campaign, clock.GetUtcNow()));
        }
        catch (CreditRefusedException refusal)
        {
            return (Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message)), null);
        }
    }

    /// <summary>A hand correction in either direction.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/wallets/{userId}/adjust")]
    public static async Task<IResult> AdjustAsync(
        string userId,
        AdjustCreditRequest request,
        [NotBody] CreditLedgerService ledger,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            var result = await ledger.AdjustAsync(
                userId, request.Amount, request.Reason, actor, request.IdempotencyKey, cancellationToken);

            return Results.Ok(Written(result));
        }
        catch (CreditRefusedException refusal)
        {
            return Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    /// <summary>Takes back an issuance.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/entries/{entryId}/reverse")]
    public static async Task<IResult> ReverseAsync(
        string entryId,
        ReverseCreditRequest request,
        [NotBody] CreditLedgerService ledger,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            return Results.Ok(Written(
                await ledger.ReverseEntryAsync(entryId, request.Reason, actor, cancellationToken)));
        }
        catch (CreditRefusedException refusal)
        {
            return refusal.Code == CreditErrorCodes.EntryNotFound
                ? Results.NotFound()
                : Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    /// <summary>Empties a wallet after a fraud ban (section 8.6).</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/wallets/{userId}/void")]
    public static async Task<IResult> VoidAsync(
        string userId,
        VoidCreditRequest request,
        [NotBody] CreditLedgerService ledger,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            return Results.Ok(Written(
                await ledger.VoidForFraudAsync(userId, request.Reason, actor, cancellationToken)));
        }
        catch (CreditRefusedException refusal)
        {
            return Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    /// <summary>Recomputes the cached balance from the entries.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/wallets/{userId}/rebuild")]
    public static async Task<IResult> RebuildAsync(
        string userId,
        [NotBody] CreditLedgerService ledger,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(new { balance = await ledger.RebuildAsync(userId, cancellationToken) });
        }
        catch (CreditRefusedException refusal)
        {
            return Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/credit/campaigns")]
    public static async Task<IResult> CampaignsAsync(
        [NotBody] CreditCampaignService campaigns,
        CancellationToken cancellationToken)
    {
        var rows = await campaigns.ListAsync(cancellationToken);
        return Results.Ok(rows.Select(CreditCampaignDto.From).ToList());
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/campaigns")]
    public static async Task<IResult> CreateCampaignAsync(
        CreateCreditCampaignRequest request,
        [NotBody] CreditCampaignService campaigns,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            var campaign = await campaigns.CreateAsync(
                new CreateCreditCampaign(
                    request.Code,
                    request.Description,
                    request.TotalBudgetPoints,
                    request.MaxPerUserPoints,
                    request.DefaultIssuePoints,
                    request.LotLifetimeDays,
                    request.AlertThresholdPercent,
                    request.Automated,
                    request.StartsAt,
                    request.EndsAt),
                actor,
                cancellationToken);

            return Results.Ok(CreditCampaignDto.From(campaign));
        }
        catch (CreditRefusedException refusal)
        {
            return Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/credit/campaigns/{codeOrId}/pause")]
    public static async Task<IResult> PauseCampaignAsync(
        string codeOrId,
        [NotBody] CreditCampaignService campaigns,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            return Results.Ok(CreditCampaignDto.From(
                await campaigns.PauseAsync(codeOrId, actor, cancellationToken)));
        }
        catch (CreditRefusedException refusal)
        {
            return Results.NotFound(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePatch("/api/v1/credit/campaigns/{codeOrId}/budget")]
    public static async Task<IResult> SetCampaignBudgetAsync(
        string codeOrId,
        SetCreditBudgetRequest request,
        [NotBody] CreditCampaignService campaigns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Results.Ok(CreditCampaignDto.From(
                await campaigns.SetBudgetAsync(codeOrId, request.TotalBudgetPoints, cancellationToken)));
        }
        catch (CreditRefusedException refusal)
        {
            return refusal.Code == CreditCampaignErrorCodes.NotFound
                ? Results.NotFound()
                : Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message));
        }
    }

    private static CreditWriteDto Written(CreditLedgerResult result) => new(
        result.Balance,
        result.Entries.Select(entry => CreditEntryDto.From(entry)).ToList(),
        result.WasReplay);

    private static string? ActorId(ClaimsPrincipal caller)
    {
        var actor = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(actor) ? null : actor;
    }
}
