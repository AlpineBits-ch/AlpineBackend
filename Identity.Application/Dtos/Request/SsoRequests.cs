namespace Identity.Application.Dtos.Request;

/// <summary>A decision on a parked request: approve or refuse.</summary>
public class SsoDecisionRequest
{
    public required string Rq { get; init; }

    public bool Granted { get; init; }
}
