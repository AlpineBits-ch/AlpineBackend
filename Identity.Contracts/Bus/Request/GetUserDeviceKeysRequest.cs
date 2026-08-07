namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What is the long-term public key of each device these users own?" - the directory read a client
/// needs before it can seal anything to somebody else's devices.
/// </summary>
public class GetUserDeviceKeysRequest
{
    /// <summary>Ids past <see cref="MaxUserIds"/> are not answered, and come back in
    /// <c>GetUserDeviceKeysResponse.OmittedUserIds</c> rather than being dropped on the floor. See
    /// <see cref="MaxUserIds"/> for why the cap exists at all.</summary>
    public IReadOnlyList<string> UserIds { get; set; } = [];

    /// <summary>
    /// Well above any roster that legitimately seals in one pass, and not a paging mechanism.
    /// </summary>
    public const int MaxUserIds = 500;
}
