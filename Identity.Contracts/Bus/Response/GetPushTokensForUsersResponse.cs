using Identity.Contracts.Enums;

namespace Identity.Contracts.Bus.Response;

public class PushTokenResponse
{
    public string UserId { get; set; } = null!;
    public string Token { get; set; } = null!;
    public PushTokenKind Kind { get; set; }

    /// <summary>The client device id this token was registered from, or null for tokens
    /// registered without one.</summary>
    public string? ClientDeviceId { get; set; }

    /// <summary>What the build behind this token declared it understands (see
    /// <c>Domain.PushCapabilities</c>). Empty both for a token with no device attached and for a
    /// device that never declared anything - which are the same answer for a sender's purposes:
    /// send the shape every client has always understood.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Reads as "this handset can definitely handle that", which is the only question a
    /// sender asks. Ordinal because these are protocol tokens, not words.</summary>
    public bool Supports(string capability) =>
        Capabilities.Contains(capability, StringComparer.Ordinal);
}

public class GetPushTokensForUsersResponse
{
    public ICollection<PushTokenResponse> Tokens { get; set; } = new List<PushTokenResponse>();

    public IEnumerable<string> Of(PushTokenKind kind) =>
        Tokens.Where(t => t.Kind == kind).Select(t => t.Token);
}
