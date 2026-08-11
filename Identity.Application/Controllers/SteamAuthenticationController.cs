using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Identity.Application.Services.Steam;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Identity.Application.Controllers;

[ApiController]
[Route("api/v1/authentication/steam")]
public class SteamAuthenticationController(
    MicroserviceContext ctx,
    IDistributedCache cache,
    SteamOpenIdService steam,
    IMessageBus bus,
    ILogger<SteamAuthenticationController> logger) : ControllerBase
{
    private const string LinkMode = "link";
    private const string LoginMode = "login";

    private static string StateKey(string stateId) => $"steam_state:{stateId}";

    private static readonly DistributedCacheEntryOptions StateExpiry = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
    };

    private static readonly DistributedCacheEntryOptions TicketExpiry = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
    };

    /// <summary>
    /// A verified but unlinked Steam identity, held while the person decides what to do with it.
    /// </summary>
    private static readonly DistributedCacheEntryOptions PendingLinkExpiry = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
    };

    private static string PendingLinkKey(string ticket) => $"steam_pending_link:{ticket}";

    /// <param name="ReturnUrl">Where to put the browser when this flow finishes.</param>
    private sealed record SteamAuthState(string Mode, string? UserId, string ReturnUrl);

    /// <summary>Starts linking Steam to the currently authenticated user.</summary>
    [Authorize]
    [HttpGet("link/start")]
    public async Task<IActionResult> StartLink([FromQuery] string? returnUrl, CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest("claim not found");

        var redirectUrl = await CreateStateAndBuildRedirectAsync(
            new SteamAuthState(LinkMode, userId, SteamReturnTargets.Resolve(returnUrl)), ct);

        return Ok(new { redirectUrl });
    }

    /// <summary>Starts a Steam login for an anonymous caller.</summary>
    [AllowAnonymous]
    [HttpGet("login/start")]
    public async Task<IActionResult> StartLogin([FromQuery] string? returnUrl, CancellationToken ct)
    {
        var redirectUrl = await CreateStateAndBuildRedirectAsync(
            new SteamAuthState(LoginMode, null, SteamReturnTargets.Resolve(returnUrl)), ct);

        return Ok(new { redirectUrl });
    }

    /// <summary>Removes the Steam link from the currently authenticated user.</summary>
    [Authorize]
    [HttpDelete("link")]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest("claim not found");

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound();

        var steamId = user.SteamId;
        user.SteamId = null;
        await bus.PublishAsync(new SteamUnlinkedEvent()
        {
            SteamId = steamId ?? string.Empty,
            UserId = user.Id
        });
        await ctx.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>Shared return endpoint Steam redirects the browser to.</summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(CancellationToken ct)
    {
        var stateId = Request.Query["state"].ToString();
        if (string.IsNullOrEmpty(stateId)) return ClientRedirect(SteamReturnTargets.Default, "error");

        // State is single-use: consume it before doing anything else.
        var stateJson = await cache.GetStringAsync(StateKey(stateId), ct);
        if (stateJson is null) return ClientRedirect(SteamReturnTargets.Default, "error");
        await cache.RemoveAsync(StateKey(stateId), ct);

        var state = JsonSerializer.Deserialize<SteamAuthState>(stateJson);
        if (state is null) return ClientRedirect(SteamReturnTargets.Default, "error");

        // Re-resolved rather than trusted straight out of the blob: the allowlist can change under
        // a state that was minted ten minutes ago, and a stale target should fall back rather than
        // be honoured because it was legal once.
        var returnUrl = SteamReturnTargets.Resolve(state.ReturnUrl);

        var steamId = await steam.VerifyAsync(Request.Query, ct);
        if (steamId is null) return ClientRedirect(returnUrl, "error");

        return state.Mode switch
        {
            LinkMode => await HandleLinkAsync(state, returnUrl, steamId, ct),
            LoginMode => await HandleLoginAsync(returnUrl, steamId, ct),
            _ => ClientRedirect(returnUrl, "error")
        };
    }

    /// <summary>
    /// What the auth site needs to render the two-door screen somebody meets when they sign in with
    /// a Steam account nothing is linked to.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("pending/{ticket}")]
    public async Task<IActionResult> GetPendingLink(string ticket, CancellationToken ct)
    {
        var steamId = await cache.GetStringAsync(PendingLinkKey(ticket), ct);
        if (steamId is null) return NotFound();

        var profile = await steam.GetProfileAsync(steamId, ct);

        return Ok(new
        {
            steamId,
            personaName = profile?.PersonaName,
            avatarUrl = profile?.AvatarUrl,
        });
    }

    /// <summary>
    /// Redeems a pending Steam identity onto the account that is signed in now - the second half of
    /// both doors on that screen.
    /// </summary>
    [Authorize]
    [HttpPost("pending/{ticket}/link")]
    public async Task<IActionResult> RedeemPendingLink(string ticket, CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();

        var steamId = await cache.GetStringAsync(PendingLinkKey(ticket), ct);
        if (steamId is null) return NotFound();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound();

        // Checked again here and not only when the ticket was minted: half an hour is long enough
        // for somebody else to have linked this SteamID in the meantime, and the loser of that race
        // must not silently take it off them.
        var alreadyLinked = await ctx.Users.AnyAsync(u => u.SteamId == steamId && u.Id != user.Id, ct);
        if (alreadyLinked) return Conflict(new { status = "already_linked" });

        // Single-use: consume before acting, so a redelivered request cannot re-link a SteamID that
        // has since been unlinked on purpose.
        await cache.RemoveAsync(PendingLinkKey(ticket), ct);

        user.SteamId = steamId;
        await bus.PublishAsync(new SteamLinkedEvent { SteamId = steamId, UserId = user.Id });
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Linked pending Steam {SteamId} to user {UserId}", steamId, user.Id);
        return Ok(new { status = "linked" });
    }

    private async Task<IActionResult> HandleLinkAsync(
        SteamAuthState state, string returnUrl, string steamId, CancellationToken ct)
    {
        if (state.UserId is null) return ClientRedirect(returnUrl, "error");

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == state.UserId, ct);
        if (user is null) return ClientRedirect(returnUrl, "error");

        // Prevent hijacking a SteamID already linked to a different account.
        var alreadyLinked = await ctx.Users.AnyAsync(u => u.SteamId == steamId && u.Id != user.Id, ct);
        if (alreadyLinked) return ClientRedirect(returnUrl, "already_linked");

        user.SteamId = steamId;
        await bus.PublishAsync(new SteamLinkedEvent()
        {
            SteamId = steamId,
            UserId = user.Id
        });
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Linked Steam {SteamId} to user {UserId}", steamId, user.Id);
        return ClientRedirect(returnUrl, "linked");
    }

    private async Task<IActionResult> HandleLoginAsync(string returnUrl, string steamId, CancellationToken ct)
    {
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.SteamId == steamId, ct);

        if (user is null)
        {
            // Used to be the end of the road: `no_account` with nothing to do about it, after
            // somebody had already proven they own a Steam account.
            var pending = Guid.NewGuid().ToString("N");
            await cache.SetStringAsync(PendingLinkKey(pending), steamId, PendingLinkExpiry, ct);

            logger.LogInformation("Steam {SteamId} is not linked to any account; issued a pending link ticket", steamId);
            return ClientRedirect(returnUrl, "no_account", ("pending", pending));
        }

        if (!user.IsSigninAllowed()) return ClientRedirect(returnUrl, "forbidden");

        // Mint a short-lived, single-use ticket the client exchanges at /connect/token.
        var ticket = Guid.NewGuid().ToString("N");
        await cache.SetStringAsync(SteamOpenIdService.LoginTicketCacheKey(ticket), user.Id, TicketExpiry, ct);

        logger.LogInformation("Issued Steam login ticket for user {UserId}", user.Id);
        return ClientRedirect(returnUrl, "ok", ("ticket", ticket));
    }

    private async Task<string> CreateStateAndBuildRedirectAsync(SteamAuthState state, CancellationToken ct)
    {
        var stateId = Guid.NewGuid().ToString("N");
        await cache.SetStringAsync(StateKey(stateId), JsonSerializer.Serialize(state), StateExpiry, ct);
        return steam.BuildRedirectUrl(stateId);
    }

    private RedirectResult ClientRedirect(string returnUrl, string status, params (string Key, string Value)[] extra)
    {
        var query = new Dictionary<string, string?> { ["status"] = status };
        foreach (var (key, value) in extra)
        {
            query[key] = value;
        }

        return Redirect(QueryHelpers.AddQueryString(returnUrl, query));
    }
}
