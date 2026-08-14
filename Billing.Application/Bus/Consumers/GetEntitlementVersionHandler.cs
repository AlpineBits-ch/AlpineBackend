using Billing.Application.Services;
using Billing.Contracts.Bus.Request;
using Echo.Entitlements.Model;

namespace Billing.Application.Bus.Consumers;

/// <summary>Answers "what version are this subject's entitlements at".</summary>
public class GetEntitlementVersionHandler
{
    public static async Task<GetEntitlementVersionResponse> Handle(
        GetEntitlementVersionRequest request,
        EntitlementVersionService versions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var version = await versions.VersionAsync(
            new EntitlementSubject(request.SubjectKind, request.SubjectId), cancellationToken);

        return new GetEntitlementVersionResponse { Version = version };
    }
}
