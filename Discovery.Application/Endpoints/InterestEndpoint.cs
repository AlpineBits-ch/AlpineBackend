using System.Security.Claims;
using Discovery.Api.Dtos.Request;
using Discovery.Api.Services;
using Discovery.Domain.Topics;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Discovery.Api.Endpoints;

/// <summary>Reading and replacing the caller's own interest set (spec section 3.4).</summary>
[Authorize]
public static class InterestEndpoint
{
    // No "discovery" segment - the gateway strips the service prefix, so declaring one here 404s
    // silently.
    [WolverineGet("/api/v1/me/interests")]
    public static async Task<IResult> GetAsync(
        [NotBody] InterestService interests,
        [NotBody] ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await interests.GetAsync(userId, ct));
    }

    [WolverinePut("/api/v1/me/interests")]
    public static async Task<IResult> PutAsync(
        UpdateInterestsDto dto,
        [NotBody] InterestService interests,
        [NotBody] ListingRealtime realtime,
        [NotBody] ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var topics = new List<TopicInput>();
        foreach (var raw in dto.Topics)
        {
            if (!TopicRef.TryParse(raw, out var topic)) return Results.BadRequest($"Not a topic: {raw}");

            // TopicRef.TryParse slugs the id and does not hand the pre-slug text back - recompute
            // the same substring here so a minted tag gets a readable display name, not its slug.
            var separator = raw.IndexOf(':');
            var rawText = separator >= 0 ? raw[(separator + 1)..] : raw;
            topics.Add(new TopicInput(topic, rawText));
        }

        if (topics.Count > InterestService.MaxInterests)
            return Results.BadRequest($"At most {InterestService.MaxInterests} interests.");

        var result = await interests.ReplaceAsync(userId, topics, dto.Visible, ct);
        await realtime.InterestsChangedAsync(userId, ct);
        return Results.Ok(result);
    }
}
