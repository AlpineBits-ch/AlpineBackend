namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What phone number has each of these accounts recorded?" - Identity owns the number, and Guild
/// needs it to show a household who to send money to.
/// </summary>
public class GetUserPhoneNumbersRequest
{
    /// <summary>Ids past <see cref="MaxUserIds"/> are not answered and come back in
    /// <c>GetUserPhoneNumbersResponse.OmittedUserIds</c> rather than being dropped silently.</summary>
    public IReadOnlyList<string> UserIds { get; set; } = [];

    /// <summary>Well above any household roster and not a paging mechanism - it guards against a
    /// caller handing over an unbounded list and turning this into a scan of the user table. The
    /// same cap <c>GetUserDeviceKeysRequest</c> uses, so a roster that fits one fits the
    /// other.</summary>
    public const int MaxUserIds = 500;
}
