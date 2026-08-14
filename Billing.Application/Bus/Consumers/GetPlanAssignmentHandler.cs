using Billing.Application.Services;
using Billing.Contracts.Bus.Request;
using Echo.Entitlements.Model;

namespace Billing.Application.Bus.Consumers;

/// <summary>Answers which plan version a subject is pinned to.</summary>
public class GetPlanAssignmentHandler
{
    public static async Task<GetPlanAssignmentResponse> Handle(
        GetPlanAssignmentRequest request,
        PlanCatalogueService catalogue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = await catalogue.PlanReferenceForAsync(
            new EntitlementSubject(request.SubjectKind, request.SubjectId), cancellationToken);

        return new GetPlanAssignmentResponse { PlanReference = reference };
    }
}
