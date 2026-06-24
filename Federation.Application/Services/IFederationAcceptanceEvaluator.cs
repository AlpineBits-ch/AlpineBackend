namespace Federation.Application.Services;

public interface IFederationAcceptanceEvaluator
{
    Task<bool> ShouldAutoAcceptAsync(string host, CancellationToken ct = default);
}
