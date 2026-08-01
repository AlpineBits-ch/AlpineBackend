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

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
}
