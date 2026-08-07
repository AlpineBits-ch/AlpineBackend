using System.Security.Claims;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>The spending rollup for a ledger channel.</summary>
[Authorize]
public class LedgerSummaryEndpoint
{
    /// <summary>Totals over a window, bucketed by month and by category.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/ledger/summary")]
    public async Task<IResult> SummaryAsync(string channelId, DateTimeOffset? from, DateTimeOffset? to,
        string? groupBy,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] LedgerSummaryService summaries, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        if (from is not null && to is not null && from > to)
            return Results.BadRequest("'from' must not be after 'to'");

        var includeCategories = true;
        var includePeriods = true;

        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            switch (groupBy.Trim().ToLowerInvariant())
            {
                case "month":
                    includeCategories = false;
                    break;
                case "category":
                    includePeriods = false;
                    break;
                default:
                    return Results.BadRequest("groupBy must be 'month' or 'category'");
            }
        }

        var window = LedgerSummaryService.ResolveWindow(from, to, DateTimeOffset.UtcNow);
        var currency = await ledger.GetCurrencyAsync(channelId);

        return Results.Ok(await summaries.SummarizeAsync(
            channelId, userId, currency, window, includeCategories, includePeriods));
    }
}
