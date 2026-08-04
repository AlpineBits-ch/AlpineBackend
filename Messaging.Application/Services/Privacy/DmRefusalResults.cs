using Microsoft.AspNetCore.Mvc;

namespace Messaging.Application.Services.Privacy;

/// <summary>The body of a privacy refusal.</summary>
public sealed class DmRefusalDto
{
    /// <summary>One of the constants on <see cref="DmRefusal"/>, or
    /// <see cref="ExplicitContentGuard.RefusalCode"/>.</summary>
    public string Error { get; set; } = null!;

    /// <summary>The recipient the decision was about, when there is one.</summary>
    public string? UserId { get; set; }
}

/// <summary>Turns a <see cref="DmRefusal"/> into the HTTP answer T0-2 specifies.</summary>
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
