using Domain;

namespace Identity.Contracts.Bus.Response;

public class GetUserProtectionLevelResponse
{
    public ICollection<UserProtectionLevelResponse> Levels { get; set; } = new List<UserProtectionLevelResponse>();
}

public class UserProtectionLevelResponse
{
    public string UserId { get; set; } = null!;

    /// <summary>The server's cached view. Authoritative only for the server's own "may this be
    /// auto-admitted" decision - clients verify <see cref="SignedAssertion"/> instead, and fail
    /// closed to <see cref="ProtectionLevel.VerifiedDevices"/> when they cannot.</summary>
    public ProtectionLevel Level { get; set; }

    /// <summary>The assertion signed by the user's identity key. Null when the account has never
    /// published one, which a client must treat as unverifiable rather than as consent.</summary>
    public byte[]? SignedAssertion { get; set; }

    public int Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
