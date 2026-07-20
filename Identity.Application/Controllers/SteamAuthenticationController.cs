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

    private sealed record SteamAuthState(string Mode, string? UserId);

    /// <summary>Starts linking Steam to the currently authenticated user.</summary>
    [Authorize]
    [HttpGet("link/start")]
    public async Task<IActionResult> StartLink(CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest("claim not found");

        var redirectUrl = await CreateStateAndBuildRedirectAsync(new SteamAuthState(LinkMode, userId), ct);
        return Ok(new { redirectUrl });
    }

    /// <summary>Starts a Steam login for an anonymous caller.</summary>
    [AllowAnonymous]
    [HttpGet("login/start")]
    public async Task<IActionResult> StartLogin(CancellationToken ct)
    {
        var redirectUrl = await CreateStateAndBuildRedirectAsync(new SteamAuthState(LoginMode, null), ct);
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
        if (string.IsNullOrEmpty(stateId)) return ClientRedirect("error");

        // State is single-use: consume it before doing anything else.
        var stateJson = await cache.GetStringAsync(StateKey(stateId), ct);
        if (stateJson is null) return ClientRedirect("error");
        await cache.RemoveAsync(StateKey(stateId), ct);

        var state = JsonSerializer.Deserialize<SteamAuthState>(stateJson);
        if (state is null) return ClientRedirect("error");

        var steamId = await steam.VerifyAsync(Request.Query, ct);
        if (steamId is null) return ClientRedirect("error");

        return state.Mode switch
        {
            LinkMode => await HandleLinkAsync(state, steamId, ct),
            LoginMode => await HandleLoginAsync(steamId, ct),
            _ => ClientRedirect("error")
        };
    }

    private async Task<IActionResult> HandleLinkAsync(SteamAuthState state, string steamId, CancellationToken ct)
    {
        if (state.UserId is null) return ClientRedirect("error");

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == state.UserId, ct);
        if (user is null) return ClientRedirect("error");

        // Prevent hijacking a SteamID already linked to a different account.
        var alreadyLinked = await ctx.Users.AnyAsync(u => u.SteamId == steamId && u.Id != user.Id, ct);
        if (alreadyLinked) return ClientRedirect("already_linked");

        user.SteamId = steamId;
        await bus.PublishAsync(new SteamLinkedEvent()
        {
            SteamId = steamId,
            UserId = user.Id
        });
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Linked Steam {SteamId} to user {UserId}", steamId, user.Id);
        return ClientRedirect("linked");
    }

    private async Task<IActionResult> HandleLoginAsync(string steamId, CancellationToken ct)
    {
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.SteamId == steamId, ct);
        if (user is null) return ClientRedirect("no_account");
        if (!user.IsSigninAllowed()) return ClientRedirect("forbidden");

        // Mint a short-lived, single-use ticket the client exchanges at /connect/token.
        var ticket = Guid.NewGuid().ToString("N");
        await cache.SetStringAsync(SteamOpenIdService.LoginTicketCacheKey(ticket), user.Id, TicketExpiry, ct);

        logger.LogInformation("Issued Steam login ticket for user {UserId}", user.Id);
        return ClientRedirect("ok", ("ticket", ticket));
    }

    private async Task<string> CreateStateAndBuildRedirectAsync(SteamAuthState state, CancellationToken ct)
    {
        var stateId = Guid.NewGuid().ToString("N");
        await cache.SetStringAsync(StateKey(stateId), JsonSerializer.Serialize(state), StateExpiry, ct);
        return steam.BuildRedirectUrl(stateId);
    }

    private RedirectResult ClientRedirect(string status, params (string Key, string Value)[] extra)
    {
        var query = new Dictionary<string, string?> { ["status"] = status };
        foreach (var (key, value) in extra)
        {
            query[key] = value;
        }

        return Redirect(QueryHelpers.AddQueryString(Env.Steam.ClientReturnUrl, query));
    }
}
