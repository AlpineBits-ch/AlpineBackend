using System.Security.Claims;
using Discovery.Api.Services;
using Discovery.Domain.Topics;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Discovery.Api.Endpoints;

/// <summary>The ranked, cursor-paged discovery feed. Any signed-in user may browse - spec section
/// 17: gating the demand side of a marketplace was rejected.</summary>
[Authorize]
public static class FeedEndpoint
{
    // No "discovery" segment - the gateway strips the service prefix, so declaring one here 404s
    // silently.
    [WolverineGet("/api/v1/discover")]
    public static async Task<IResult> DiscoverAsync(
        [NotBody] DiscoveryFeedQuery feed,
        [NotBody] ClaimsPrincipal user,
        string? q = null,
        string? topics = null,
        string? language = null,
        string? cursor = null,
        int limit = 24,
        CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        // An unparseable topic is dropped, not refused - the filter comes from a URL a user can
        // edit, and a 400 on a hand-mangled query string helps nobody.
        var filters = (topics ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(raw => TopicRef.TryParse(raw, out var topic) ? topic : (TopicRef?)null)
            .Where(topic => topic is not null)
            .Select(topic => topic!.Value)
            .ToList();

        return Results.Ok(await feed.RunAsync(new FeedRequest(userId, q, filters, language, cursor,
            Math.Clamp(limit, 1, 50)), ct));
    }
}
