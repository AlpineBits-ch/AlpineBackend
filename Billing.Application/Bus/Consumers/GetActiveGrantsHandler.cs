using Billing.Application.Services;
using Billing.Contracts.Bus.Request;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;

namespace Billing.Application.Bus.Consumers;

/// <summary>
/// Answers "what live grants does this subject hold" for the services that resolve entitlements but
/// do not own the table.
/// </summary>
public class GetActiveGrantsHandler
{
    public static async Task<GetActiveGrantsResponse> Handle(
        GetActiveGrantsRequest request,
        GrantService grants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var live = await grants.ListAsync(
            new EntitlementSubject(request.SubjectKind, request.SubjectId),
            activeOnly: true,
            cancellationToken);

        return new GetActiveGrantsResponse { Grants = live.Select(Map).ToList() };
    }

    /// <summary>Four fields, and deliberately not the row.</summary>
    private static ActiveGrantDto Map(Grant grant)
    {
        var mapped = GrantService.ToEntitlementGrant(grant);

        return new ActiveGrantDto
        {
            GrantId = mapped.GrantId,
            Plan = mapped.Plan,
            Entitlements = mapped.Entitlements?.ToDictionary(
                entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            ExpiresAt = mapped.ExpiresAt,
        };
    }
}
