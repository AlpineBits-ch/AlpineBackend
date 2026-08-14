using System.Security.Claims;
using Echo.Dtos.Entitlements;
using Echo.RateLimiter;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Wolverine;

namespace Echo.Controllers;

/// <summary>What the caller, and the guilds the caller is in, are entitled to.</summary>
[Authorize]
[ApiController]
[Route("api/v1/entitlements")]
[EnableRateLimiting(GatewayRateLimiting.PolicyName)]
public class EntitlementsController(
    EntitlementSnapshotBuilder snapshots,
    EntitlementReadOptions options,
    IMessageBus bus) : ControllerBase
{
    /// <summary>The caller's own entitlements, plus what kind of instance this is.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> MineAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var snapshot = await snapshots.ForUserAsync(userId, ct);

        Cache();
        return Ok(snapshot);
    }

    /// <summary>One guild's entitlements, for the guild settings screens.</summary>
    [HttpGet("guilds/{guildId}")]
    public async Task<IActionResult> ForGuildAsync(string guildId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        var member = await bus.InvokeAsync<GetGuildMemberResponse>(
            new GetGuildMemberRequest { GuildId = guildId, UserId = userId }, ct);

        if (member.Member is null)
        {
            return NotFound(new { code = "not_found", message = "No such guild." });
        }

        // Resolved per request rather than inferred from a role the client already holds, and this is
        // the field the client draws its call to action from: a member who cannot manage the guild
        // gets the explanation without the button, instead of a button that 403s.
        var manage = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(
            new HasUserPermissionToGuildRequest
            {
                GuildId = guildId,
                UserId = userId,
                Permission = ExternalPermission.ManageGuild,
            }, ct);

        var snapshot = await snapshots.ForGuildAsync(guildId, manage.IsAllowed, ct);

        Cache();
        return Ok(snapshot);
    }

    /// <summary>The same number the payload carries, so a client that honours HTTP caching and one
    /// that honours <c>ttlSeconds</c> cannot disagree about how stale an answer is allowed to
    /// be.</summary>
    private void Cache() =>
        Response.Headers.CacheControl = $"private, max-age={options.TtlSeconds}";
}
