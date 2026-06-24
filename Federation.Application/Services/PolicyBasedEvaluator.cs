using Federation.Domain.Aggregates;
using Federation.Infrastructure.Persistence;

namespace Federation.Application.Services;

public class PolicyBasedEvaluator(MicroserviceContext db) : IFederationAcceptanceEvaluator
{
    public async Task<bool> ShouldAutoAcceptAsync(string host, CancellationToken ct = default)
    {
        var settings = await db.FederationSettings.FindAsync([FederationSettings.SingletonId], ct)
                       ?? new FederationSettings();
        return settings.AcceptancePolicy == AcceptancePolicy.AutoAccept;
    }
}
