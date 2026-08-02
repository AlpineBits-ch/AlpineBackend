using System.Security.Claims;

namespace Identity.Tests.Helpers;

/// <summary>Builds a minimal ClaimsPrincipal carrying just the NameIdentifier - the endpoints under
/// test read nothing else off it. Mirrors Messaging.Tests/Helpers/TestPrincipal.</summary>
internal static class TestPrincipal
{
    public static ClaimsPrincipal ForUser(string userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>A principal carrying the <c>session_id</c> claim, which is how every per-device rule
    /// resolves which device the caller actually is - see <c>SessionDeviceResolver</c>.</summary>
    public static ClaimsPrincipal ForSession(string userId, string sessionId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim("session_id", sessionId)], "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
}
