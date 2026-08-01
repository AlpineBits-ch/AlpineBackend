using Domain;

namespace Identity.Contracts.Bus.Response;

public class GetUserProtectionLevelResponse
{
    public ICollection<UserProtectionLevelResponse> Levels { get; set; } = new List<UserProtectionLevelResponse>();
}

public class UserProtectionLevelResponse
{
    public string UserId { get; set; } = null!;

    /// <summary>The server's cached view.</summary>
    public ProtectionLevel Level { get; set; }

    /// <summary>The assertion signed by the user's identity key.</summary>
    public byte[]? SignedAssertion { get; set; }

    public int Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
