using Billing.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>What is for sale, for somebody deciding whether to buy it.</summary>
public class CatalogueEndpoint
{
    [Authorize]
    [WolverineGet("/api/v1/catalogue")]
    public static async Task<IResult> GetAsync(
        [NotBody] CheckoutCatalogueService catalogue,
        CancellationToken cancellationToken) =>
        Results.Ok(await catalogue.ReadAsync(cancellationToken));
}
