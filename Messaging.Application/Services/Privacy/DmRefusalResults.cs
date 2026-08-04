using Microsoft.AspNetCore.Mvc;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// The body of a privacy refusal. A named type rather than an anonymous object so it appears in the
/// generated OpenAPI document and so clients have something to deserialize into - the point of T0-2
/// is that the client can tell <c>blocked</c> from <c>recipient_dm_policy</c> and offer the right
/// next step, which it cannot do against an untyped blob.
/// </summary>
public sealed class DmRefusalDto
{
    /// <summary>One of the constants on <see cref="DmRefusal"/>, or
    /// <see cref="ExplicitContentGuard.RefusalCode"/>.</summary>
    public string Error { get; set; } = null!;

    /// <summary>The recipient the decision was about, when there is one. Always somebody the caller
    /// already named, so it is not an enumeration oracle.</summary>
    public string? UserId { get; set; }
}

/// <summary>
/// Turns a <see cref="DmRefusal"/> into the HTTP answer T0-2 specifies.
///
/// <para><b>403, not 400.</b> The previous refusal was
/// <c>400 "User cannot be added to conversation if not friends"</c> - a prose string on the status
/// code that means "your request was malformed". A client cannot tell that apart from a genuine
/// validation error, so it cannot offer "send a friend request" or "unblock them" instead of
/// "something went wrong". This is a deliberate breaking change to the response shape of the four
/// endpoints that carried it.</para>
///
/// <para>A lookup that could not be performed is <b>503</b>, not 403: the request is still refused,
/// but calling a transient outage a permission decision teaches clients to show the user a
/// permanent-sounding error for something a retry fixes.</para>
/// </summary>
public static class DmRefusalResults
{
    /// <summary>Minimal-API flavour, for the Wolverine endpoints.</summary>
    public static IResult ToResult(DmRefusal refusal) =>
        Results.Json(Body(refusal), statusCode: StatusFor(refusal));

    /// <summary>MVC flavour, for the controllers.</summary>
    public static IActionResult ToActionResult(DmRefusal refusal) => new ObjectResult(Body(refusal))
    {
        StatusCode = StatusFor(refusal),
    };

    /// <summary>The T2-20 refusal, which is a content decision rather than a contactability one but
    /// travels in the same envelope so a client has one shape to parse. Deliberately names no
    /// recipient - whose filter rejected the attachment is their business.</summary>
    public static IResult ExplicitContent() => Results.Json(
        new DmRefusalDto { Error = ExplicitContentGuard.RefusalCode },
        statusCode: StatusCodes.Status403Forbidden);

    private static int StatusFor(DmRefusal refusal) => refusal.IsTransient
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status403Forbidden;

    private static DmRefusalDto Body(DmRefusal refusal) =>
        new() { Error = refusal.Code, UserId = refusal.UserId };
}
