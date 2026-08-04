namespace Social.Api.Dtos.Response;

/// <summary>
/// Body of every privacy refusal Social returns. A machine-readable <see cref="Code"/> rather than a
/// prose message, so a client can tell "not allowed" from "malformed" without string matching -
/// the same shape the privacy spec asks for in T0-2 and T2-15.
/// </summary>
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
    /// The catch-all for a friend request that will not be delivered (privacy spec T2-15). Covers a
    /// target that does not exist, a target who is not discoverable, a target who has blocked the
    /// caller, and a target whose <c>FriendRequestPolicy</c> excludes them - deliberately one code
    /// for all four, because a caller able to tell them apart has an account-enumeration oracle and
    /// can detect that they were blocked.
    /// </summary>
    public const string FriendRequestPolicy = "friend_request_policy";

    /// <summary>
    /// Only ever returned to the party who *pressed* block, about someone they blocked. Never sent
    /// to a blocked user - to them a block is indistinguishable from "not friends".
    /// </summary>
    public const string Blocked = "blocked";
}
