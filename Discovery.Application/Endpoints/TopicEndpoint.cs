using Discovery.Api.Dtos.Response;
using Discovery.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Discovery.Api.Endpoints;

/// <summary>The single autocomplete over games and tags (spec section 3.3).</summary>
[Authorize]
public static class TopicEndpoint
{
    // No "discovery" segment - the gateway strips the service prefix, so declaring one here 404s
    // silently.
    [WolverineGet("/api/v1/topics/search")]
    public static async Task<IResult> SearchAsync(
        [NotBody] TopicResolver topics,
        string? q = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { topics = Array.Empty<TopicDto>() });
        return Results.Ok(new { topics = await topics.SearchAsync(q, Math.Clamp(limit, 1, 50), ct) });
    }
}
