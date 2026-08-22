using System.Security.Claims;
using Discovery.Api.Dtos.Request;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Discovery.Api.Endpoints;

/// <summary>A guild's own listing: read it, save the draft, publish it, unlist it, bump it. Spec
/// section 5. Permission (ManageGuild) is checked on every route here; the entitlement is checked
/// on publish only - see <see cref="ListingWriteService"/>.</summary>
[Authorize]
public static class ListingEndpoint
{
    [WolverineGet("/api/v1/guilds/{guildId}/listing")]
    public static async Task<IResult> GetAsync(
        string guildId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ListingWriteService writes,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        CancellationToken ct = default)
    {
        if (!await HasManageGuildAsync(bus, user, guildId)) return Results.Forbid();

        var listing = await ctx.Listings.Include(l => l.Topics).AsNoTracking()
            .FirstOrDefaultAsync(l => l.GuildId == guildId, ct);
        if (listing is null) return Results.NotFound();

        return Results.Ok(await writes.DescribeAsync(listing, ct));
    }

    /// <summary>Creates or overwrites the draft. Never checks the entitlement - see
    /// <see cref="ListingWriteService.UpsertDraftAsync"/>.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/listing")]
    public static async Task<IResult> SaveDraftAsync(
        string guildId,
        UpsertListingDraftDto dto,
        [NotBody] ListingWriteService writes,
        [NotBody] ListingRealtime realtime,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        CancellationToken ct = default)
    {
        if (!await HasManageGuildAsync(bus, user, guildId)) return Results.Forbid();

        var result = await writes.UpsertDraftAsync(guildId, dto, ct);
        if (result.Refusal == ListingWriteRefusal.Invalid) return Results.BadRequest(result.Message);

        // A draft is invisible to everyone but its owner. Only editing content that is already
        // public needs to reach other members - a fresh draft has nobody to tell.
        if (result.Listing!.State == ListingState.Published)
            await realtime.ListingChangedAsync("discovery.ListingUpdated", result.Listing, ct);

        return Results.Ok(result.Dto);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/listing/publish")]
    public static async Task<IResult> PublishAsync(
        string guildId,
        [NotBody] ListingWriteService writes,
        [NotBody] ListingRealtime realtime,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        CancellationToken ct = default)
    {
        if (!await HasManageGuildAsync(bus, user, guildId)) return Results.Forbid();

        var result = await writes.PublishAsync(guildId, ct);
        if (result.Refusal == ListingWriteRefusal.NotFound) return Results.NotFound();
        if (result.Refusal == ListingWriteRefusal.NotEntitled) return NotEntitledResult();

        await realtime.ListingChangedAsync("discovery.ListingPublished", result.Listing!, ct);
        return Results.Ok(result.Dto);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/listing/unlist")]
    public static async Task<IResult> UnlistAsync(
        string guildId,
        [NotBody] ListingWriteService writes,
        [NotBody] ListingRealtime realtime,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        CancellationToken ct = default)
    {
        if (!await HasManageGuildAsync(bus, user, guildId)) return Results.Forbid();

        var result = await writes.UnlistAsync(guildId, ct);
        if (result.Refusal == ListingWriteRefusal.NotFound) return Results.NotFound();

        await realtime.ListingChangedAsync("discovery.ListingUnlisted", result.Listing!, ct);
        return Results.Ok(result.Dto);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/listing/bump")]
    public static async Task<IResult> BumpAsync(
        string guildId,
        [NotBody] ListingWriteService writes,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IMessageBus bus,
        CancellationToken ct = default)
    {
        if (!await HasManageGuildAsync(bus, user, guildId)) return Results.Forbid();

        var result = await writes.BumpAsync(guildId, ct);
        if (result.Refusal == ListingWriteRefusal.NotFound) return Results.NotFound();

        if (result.Refusal == ListingWriteRefusal.CooldownActive)
        {
            return Results.Json(
                new { error = "bump_cooldown", bumpAvailableAt = result.Listing!.BumpAvailableAt },
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(result.Dto);
    }

    private static IResult NotEntitledResult() => Results.Json(
        new { error = "public_listing_not_entitled", message = "This guild's plan does not include a public listing." },
        statusCode: StatusCodes.Status403Forbidden);

    private static async Task<bool> HasManageGuildAsync(IMessageBus bus, ClaimsPrincipal user, string guildId)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;

        var response = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(
            new HasUserPermissionToGuildRequest { UserId = userId, GuildId = guildId, Permission = ExternalPermission.ManageGuild });

        return response.IsAllowed;
    }
}
