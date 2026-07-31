using Identity.Contracts.Enums;

namespace Identity.Contracts.Bus.Request;

/// <summary>
/// Replaces GetDeviceTokenForUserIdRequest + GetVoipTokenForUserIdRequest, which every caller had
/// to invoke as a pair anyway. One round trip, and the response says which device each token
/// belongs to so a send can leave out the device that is already dealing with the event in-app.
/// </summary>
public class GetPushTokensForUsersRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();

    /// <summary>Empty means every transport.</summary>
    public ICollection<PushTokenKind> Kinds { get; set; } = new List<PushTokenKind>();

    /// <summary>Client device ids (the id clients send as <c>X-Device-Id</c>) whose tokens should
    /// be left out - e.g. the device that just accepted the call does not need a push about it.
    /// Tokens with no device attached are never excluded: there is no way to tell whether they
    /// belong to the excluded installation.</summary>
    public ICollection<string> ExcludeClientDeviceIds { get; set; } = new List<string>();
}
