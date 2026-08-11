using Identity.Application.Services.Sso;

namespace Identity.Application.Dtos.Response;

/// <summary>Who the browser is signed in as at <c>auth.venta.gg</c>.</summary>
public class SsoSessionDto
{
    public bool SignedIn { get; init; }
    public string? UserId { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }

    /// <summary>How this browser authenticated: <c>pwd</c>, <c>mfa</c>, <c>steam</c>, <c>qr</c>.</summary>
    public string[] AuthenticationMethods { get; init; } = [];

    public DateTimeOffset? AuthenticatedAt { get; init; }
}

/// <summary>One login on the "sites and devices" screen.</summary>
public class SsoSessionEntryDto
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? DeviceType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>The browser reading this list. Revoking it signs this page out.</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// The display projection of a parked authorization or end-session request - see docs/specs/sso.md
/// §3.1.
/// </summary>
public class SsoRequestDto
{
    /// <summary><c>authorize</c> or <c>logout</c>.</summary>
    public required string Kind { get; init; }

    public string? ClientId { get; init; }

    /// <summary>The name to put in front of a person.</summary>
    public string? ClientName { get; init; }

    public string? LogoUri { get; init; }

    public IReadOnlyList<SsoScopeDescription> Scopes { get; init; } = [];

    /// <summary>The address the client suggested, for pre-filling the username field.</summary>
    public string? LoginHint { get; init; }

    /// <summary>The client asked for <c>prompt=login</c>: an existing session is not enough and the
    /// page must collect a credential rather than offering "continue as".</summary>
    public bool ForceLogin { get; init; }

    /// <summary>Where to send the browser once the page has done its part.</summary>
    public required string ResumeUrl { get; init; }
}

/// <summary>The outcome of a consent or sign-out decision: one URL for the page to navigate to.</summary>
public class SsoDecisionDto
{
    public required string RedirectUrl { get; init; }
}
