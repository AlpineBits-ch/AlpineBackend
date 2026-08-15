namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What does this account actually carry?" - asked by Billing before it hands out a trial.
/// </summary>
public class GetTrialEligibilitySignalsRequest
{
    public string UserId { get; set; } = string.Empty;
}
