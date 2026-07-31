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
}

public class GetPushTokensForUsersResponse
{
    public ICollection<PushTokenResponse> Tokens { get; set; } = new List<PushTokenResponse>();

    public IEnumerable<string> Of(PushTokenKind kind) =>
        Tokens.Where(t => t.Kind == kind).Select(t => t.Token);
}
