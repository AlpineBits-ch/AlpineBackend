namespace Social.Api.Dtos.Response;

/// <summary>Body of every privacy refusal Social returns.</summary>
public class RefusalDto
{
    public RefusalDto() { }
    public RefusalDto(string code) => Code = code;

    public string Code { get; set; } = null!;
}

/// <summary>The refusal codes Social emits, and the rule about which one is safe to send.</summary>
public static class RefusalCodes
{
    /// <summary>
    /// The catch-all for a friend request that will not be delivered (privacy spec T2-15).
    /// </summary>
    public const string FriendRequestPolicy = "friend_request_policy";

    /// <summary>
    /// Only ever returned to the party who pressed block, about someone they blocked.
    /// </summary>
    public const string Blocked = "blocked";
}
