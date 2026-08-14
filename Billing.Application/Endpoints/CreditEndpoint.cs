using System.Security.Claims;
using AppEnvironment;
using Billing.Application.Credit;
using Billing.Contracts.Bus.Events;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>What the person who holds the credit sees (monetization.md section 8.8).</summary>
public class CreditEndpoint
{
    /// <summary>The caller's balance, their lots and the dates those lapse.</summary>
    [Authorize]
    [WolverineGet("/api/v1/credit/me")]
    public static async Task<IResult> MyWalletAsync(
        [NotBody] CreditLedgerService ledger,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        if (Hidden()) return Results.NotFound();

        var userId = CallerId(caller);
        if (userId is null) return Results.Unauthorized();

        var lots = await ledger.OpenLotsAsync(userId, cancellationToken);

        return Results.Ok(new CreditWalletDto(
            userId,
            await ledger.BalanceAsync(userId, cancellationToken),
            ledger.Options.MaxWalletBalancePoints,
            lots.Select(CreditLotDto.From).ToList(),
            CreditDisclaimer.Text,
            CreditDisclaimer.LocalisationKey));
    }

    /// <summary>The caller's ledger in plain language, newest first, with the expiry date on every
    /// line that has one.</summary>
    [Authorize]
    [WolverineGet("/api/v1/credit/me/ledger")]
    public static async Task<IResult> MyLedgerAsync(
        int limit,
        [NotBody] CreditLedgerService ledger,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        if (Hidden()) return Results.NotFound();

        var userId = CallerId(caller);
        if (userId is null) return Results.Unauthorized();

        var entries = await ledger.LedgerAsync(userId, limit <= 0 ? 100 : limit, cancellationToken);
        var lots = (await ledger.OpenLotsAsync(userId, cancellationToken))
            .ToDictionary(lot => lot.LotId, lot => lot.ExpiresAt);

        return Results.Ok(new CreditLedgerDto(
            userId,
            await ledger.BalanceAsync(userId, cancellationToken),
            entries
                .Select(entry => CreditEntryDto.From(
                    entry,
                    entry.LotId is not null && lots.TryGetValue(entry.LotId, out var expiry)
                        ? expiry
                        : null))
                .ToList(),
            CreditDisclaimer.Text,
            CreditDisclaimer.LocalisationKey));
    }

    /// <summary>What the balance can buy, in points and in cash side by side.</summary>
    [Authorize]
    [WolverineGet("/api/v1/credit/me/catalogue")]
    public static async Task<IResult> MyCatalogueAsync(
        [NotBody] CreditLedgerService ledger,
        [NotBody] CreditCatalogueService catalogue,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        if (Hidden()) return Results.NotFound();

        var userId = CallerId(caller);
        if (userId is null) return Results.Unauthorized();

        var skus = await catalogue.PurchasableAsync(cancellationToken);

        return Results.Ok(new CreditCatalogueDto(
            await ledger.BalanceAsync(userId, cancellationToken),
            skus.Select(CreditSkuDto.From).ToList(),
            CreditDisclaimer.Text,
            CreditDisclaimer.LocalisationKey));
    }

    /// <summary>Spends the caller's credit on one SKU.</summary>
    [Authorize]
    [WolverinePost("/api/v1/credit/me/purchases")]
    public static async Task<(IResult, EntitlementsChanged?)> PurchaseAsync(
        PurchaseCreditRequest request,
        [NotBody] CreditPurchaseService purchases,
        [NotBody] TimeProvider clock,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Hidden()) return (Results.NotFound(), null);

        var userId = CallerId(caller);
        if (userId is null) return (Results.Unauthorized(), null);

        try
        {
            var purchase = await purchases.PurchaseAsync(
                userId, request.Sku, request.TargetId, request.IdempotencyKey, cancellationToken);

            return (Results.Ok(CreditPurchaseDto.From(purchase, clock.GetUtcNow())), purchase.Announcement);
        }
        catch (CreditRefusedException refusal)
        {
            return (Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message)), null);
        }
        catch (Services.GrantRefusedException refusal)
        {
            // The grant half of the same transaction refused, so nothing was charged.
            return (Results.BadRequest(new CreditErrorDto(refusal.Code, refusal.Message)), null);
        }
    }

    /// <summary>Whether the credit surface exists on this instance at all.</summary>
    private static bool Hidden() => Env.License.IsSelfHost;

    private static string? CallerId(ClaimsPrincipal caller)
    {
        var userId = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(userId) ? null : userId;
    }
}
