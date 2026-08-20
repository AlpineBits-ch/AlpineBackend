using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Wolverine.Http;

namespace Guild.Application.Endpoints.Household;

/// <summary>The whole household in one request.</summary>
[Authorize]
public class HouseholdDigestEndpoint
{
    /// <summary>
    /// Chores, lists, pantry, ledger, decisions, bills, meals, upkeep, who's home and who's away,
    /// for one guild.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/home")]
    public async Task<IResult> GetAsync(string guildId,
        [NotBody] HouseholdDigestService digest, [NotBody] MicroserviceContext ctx,
        [NotBody] HttpContext http, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // Membership rather than a feature gate: the digest spans six modules and omits the ones
        // that are off, so there is no single feature to check and a non-member must not learn
        // which of them a guild has.
        if (!await ctx.GuildMembers.AsNoTracking()
                .AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var dto = await digest.BuildAsync(guildId, userId);

        var etag = ComputeETag(dto);
        http.Response.Headers[HeaderNames.ETag] = etag;

        // Per-user content, so a shared cache must never serve one member's digest to another.
        http.Response.Headers[HeaderNames.CacheControl] = "private, no-cache";

        if (MatchesIfNoneMatch(http.Request.Headers[HeaderNames.IfNoneMatch], etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.Ok(dto);
    }

    /// <summary>A strong ETag over the digest's own JSON.</summary>
    internal static string ComputeETag(HouseholdDigestDto dto)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(dto);
        var hash = SHA256.HashData(json);

        var builder = new StringBuilder(2 + hash.Length * 2);
        builder.Append('"');
        foreach (var b in hash) builder.Append(b.ToString("x2"));
        builder.Append('"');

        return builder.ToString();
    }

    /// <summary>Whether the client already holds this exact digest.</summary>
    internal static bool MatchesIfNoneMatch(StringValues header, string etag)
    {
        foreach (var value in header)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            foreach (var candidate in value.Split(','))
            {
                var trimmed = candidate.Trim();
                if (trimmed == "*") return true;
                if (trimmed.StartsWith("W/", StringComparison.Ordinal)) trimmed = trimmed[2..];
                if (trimmed == etag) return true;
            }
        }

        return false;
    }
}
