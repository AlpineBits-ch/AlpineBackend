namespace Identity.Application.Dtos.Response;

/// <summary>
/// The one body <c>POST api/v1/authentication/register</c> returns when it accepts a registration
/// request, whether or not the address it was given already has an account.
///
/// <para><b>Every field is a constant.</b> The point of this type is that two responses to two
/// different addresses are byte-identical, so anything that varies per request - a user id, a
/// "created" flag, a "we've sent a code" that is only true sometimes - must not be added to it. The
/// endpoint used to answer <c>200 {"userId": ...}</c> for a free address and <c>400 "Email already
/// exists"</c> for a taken one, which let an anonymous caller read an arbitrary address list against
/// the user table one POST at a time.</para>
///
/// <para><see cref="Instance"/> exists so the controller cannot accidentally build a per-request
/// one.</para>
/// </summary>
public sealed class RegistrationAcceptedDto
{
    public static readonly RegistrationAcceptedDto Instance = new();

    /// <summary>Stable machine-readable discriminator. The client branches on this, not on prose.</summary>
    public string Status { get; init; } = "verification_pending";

    /// <summary>
    /// Wording the client may show verbatim. Deliberately conditional ("if that address can be
    /// registered"): it is the only phrasing that is true in both branches, and a client that
    /// promises "we created your account" is lying to half the people who read it.
    /// </summary>
    public string Message { get; init; } =
        "If that address can be registered, we have sent it an email. Check your inbox to continue.";
}
