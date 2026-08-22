using System.Security.Claims;
using Discovery.Api.Dtos.Request;
using Discovery.Api.Dtos.Response;
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

        // The cap bounds how many interests the user ends up with, not how verbose the request was
        // - dedup before counting, or a client that resent duplicates would get refused for it.
        var distinct = topics.GroupBy(t => t.Topic).Select(g => g.First()).ToList();
        if (distinct.Count > InterestService.MaxInterests)
            return Results.BadRequest($"At most {InterestService.MaxInterests} interests.");

        // An unknown game topic is refused inside ReplaceAsync, before anything is written - caught
        // here rather than pre-checked, since telling a real game id from a fake one needs the same
        // database round trip the service already makes.
        InterestsDto result;
        try
        {
            result = await interests.ReplaceAsync(userId, distinct, dto.Visible, ct);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        await realtime.InterestsChangedAsync(userId, ct);
        return Results.Ok(result);
    }
}
