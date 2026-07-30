using System.Security.Claims;

namespace Messaging.Tests.Helpers;

/// <summary>Builds a minimal ClaimsPrincipal carrying just the NameIdentifier (and optionally a
/// user_type claim, mirroring how MessagingEndpoints distinguishes bot authors) - endpoints under
/// test only ever read these two claims off ClaimsPrincipal.</summary>
internal static class TestPrincipal
{
    public static ClaimsPrincipal ForUser(string userId, string? userType = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (userType is not null) claims.Add(new Claim("user_type", userType));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
}
