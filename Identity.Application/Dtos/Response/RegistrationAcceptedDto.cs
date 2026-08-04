namespace Identity.Application.Dtos.Response;

/// <summary>
/// The one body <c>POST api/v1/authentication/register</c> returns when it accepts a registration
/// request, whether or not the address it was given already has an account.
/// </summary>
public sealed class RegistrationAcceptedDto
{
    public static readonly RegistrationAcceptedDto Instance = new();

    /// <summary>Stable machine-readable discriminator. The client branches on this, not on prose.</summary>
    public string Status { get; init; } = "verification_pending";

    /// <summary>Wording the client may show verbatim.</summary>
    public string Message { get; init; } =
        "If that address can be registered, we have sent it an email. Check your inbox to continue.";
}
