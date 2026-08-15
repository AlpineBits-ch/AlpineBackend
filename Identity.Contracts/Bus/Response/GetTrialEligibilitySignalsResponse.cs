namespace Identity.Contracts.Bus.Response;

/// <summary>
/// Everything Identity can honestly say about an account for the purpose of deciding whether it may
/// take a trial.
/// </summary>
public class GetTrialEligibilitySignalsResponse
{
    public bool Found { get; set; }

    /// <summary>Whether <c>EmailVerifiedAt</c> is set.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>The number on the account, or null.</summary>
    public string? PhoneNumber { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Client device ids of the account's active devices, from the consolidated device set.
    /// Bounded by <see cref="MaxDeviceIds"/>, which is far above any real account.</summary>
    public List<string> DeviceIds { get; set; } = [];

    /// <summary>Name of the account's <c>UserStatus</c>.</summary>
    public string? Status { get; set; }

    /// <summary>True for a bot account.</summary>
    public bool IsBot { get; set; }

    /// <summary>Ceiling on one account's device list.</summary>
    public const int MaxDeviceIds = 200;
}
