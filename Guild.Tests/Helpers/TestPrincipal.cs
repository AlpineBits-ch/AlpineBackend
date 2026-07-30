using System.Security.Claims;

namespace Guild.Tests.Helpers;

/// <summary>Builds a minimal authenticated <see cref="ClaimsPrincipal"/> carrying only the
/// NameIdentifier claim endpoints read via <c>user.FindFirstValue(ClaimTypes.NameIdentifier)</c>.</summary>
internal static class TestPrincipal
{
    public static ClaimsPrincipal Create(string userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>An unauthenticated principal with no NameIdentifier claim - endpoints should treat
    /// this the same as a missing/invalid token and return Unauthorized.</summary>
    public static ClaimsPrincipal CreateAnonymous() => new(new ClaimsIdentity());
}
