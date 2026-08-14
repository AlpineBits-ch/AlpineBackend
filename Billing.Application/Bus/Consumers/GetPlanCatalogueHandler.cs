using Billing.Application.Services;
using Billing.Contracts.Bus.Request;
using Echo.Entitlements.Model;

namespace Billing.Application.Bus.Consumers;

/// <summary>
/// Answers "what plans does this instance have" for the services that resolve entitlements but do
/// not own the table.
/// </summary>
public class GetPlanCatalogueHandler
{
    public static async Task<GetPlanCatalogueResponse> Handle(
        GetPlanCatalogueRequest request,
        PlanCatalogueService catalogue,
        EntitlementPlanOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = await catalogue.CurrentAsync(cancellationToken);

        return new GetPlanCatalogueResponse
        {
            Plans = current.Plans.Select(Map).ToList(),
            DefaultGuildPlan = options.DefaultGuildPlan,
            DefaultUserPlan = options.DefaultUserPlan,
        };
    }

    /// <summary>Values are formatted back into the string form they were stored and configured in,
    /// which is the one representation all three of configuration, grants and plan versions share.
    /// </summary>
    private static CataloguePlanDto Map(PlanDefinition plan) => new()
    {
        Name = plan.Name,
        DisplayName = plan.DisplayName,
        Values = plan.Values.ToDictionary(
            entry => entry.Key.Name, entry => entry.Key.Format(entry.Value), StringComparer.Ordinal),
    };
}
