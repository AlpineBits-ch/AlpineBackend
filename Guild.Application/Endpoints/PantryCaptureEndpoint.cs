using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The one-tap half of the pantry: scan something in, use something up, put something back.
/// </summary>
[Authorize]
public class PantryCaptureEndpoint
{
    /// <summary>How many completions the barcode table returns.</summary>
    private const int MaxBarcodeResults = 50;

    /// <summary>Stocks a scanned product, creating or topping up the item as needed.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/pantry-items/scan")]
    public async Task<IResult> ScanAsync(string channelId, ScanPantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryCaptureService capture,
        [NotBody] MicroserviceContext ctx, [NotBody] HttpContext http,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        var languages = ProductCatalogService.ParseLanguages(http.Request.Headers.AcceptLanguage);

        var (result, error) = await capture.ScanAsync(
            channelId, access.Channel!.GuildId, dto, userId, languages);

        if (error is not null)
        {
            // Committed even though the scan failed, and only because of what is staged: a rejected
            // scan stages nothing but the record that the catalog could not answer this code.
            await ctx.SaveChangesAsync();
            return Results.BadRequest(error);
        }

        await ctx.SaveChangesAsync();
        await capture.PublishAsync(result!, userId);

        return Results.Ok(new ScanPantryItemResultDto
        {
            Item = PantryCaptureService.ToDto(result!.Item),
            Created = result.Created,
            Learned = result.Learned,
            Catalog = result.Catalog is { } match ? ProductCatalogService.ToDto(match) : null,
        });
    }

    /// <summary>"Used it up." Runs the identical low-stock and restock loop a PATCH does, so the
    /// same alert fires exactly once and the same idempotency stamps are honoured.</summary>
    [WolverinePost("/api/v1/pantry-items/{itemId}/consume")]
    public async Task<IResult> ConsumeAsync(string itemId, ConsumePantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryCaptureService capture,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.PantryItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        var access = await household.ResolveAsync(item.ChannelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        // Rejected rather than treated as a no-op: a zero or negative "amount used" is a client
        // bug, and silently accepting a negative one would make this a restock route that skips
        // the stamp release.
        if (dto.Amount is <= 0) return Results.BadRequest("Amount must be greater than zero");

        var result = await capture.ConsumeAsync(item, dto.Amount ?? 1m, dto.All == true);

        await ctx.SaveChangesAsync();
        await capture.PublishAsync(result, userId);

        return Results.Ok(PantryCaptureService.ToDto(item));
    }

    /// <summary>"Put some back." Releases the low-stock stamps when the quantity clears the
    /// threshold, which re-arms the loop, and ticks off whatever the pantry had put on a list.</summary>
    [WolverinePost("/api/v1/pantry-items/{itemId}/restock")]
    public async Task<IResult> RestockAsync(string itemId, RestockPantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryCaptureService capture,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.PantryItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        var access = await household.ResolveAsync(item.ChannelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Amount is <= 0) return Results.BadRequest("Amount must be greater than zero");

        var result = await capture.RestockAsync(item, dto.Amount ?? 1m, userId);

        await ctx.SaveChangesAsync();
        await capture.PublishAsync(result, userId);

        return Results.Ok(PantryCaptureService.ToDto(item));
    }

    /// <summary>
    /// What this house has learned its barcodes mean, most-used first, for a client offering
    /// completions before the scanner is even open.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/pantry/barcodes")]
    public async Task<IResult> BarcodesAsync(string guildId, string? q,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Pantry))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var query = ctx.Set<PantryBarcode>().AsNoTracking().Where(b => b.GuildId == guildId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Upper-cased on both sides rather than ILike, so the same expression translates on
            // Npgsql and on the InMemory provider the tests run against.
            var term = q.Trim().ToUpperInvariant();
            query = query.Where(b => b.Barcode.StartsWith(term) || b.Name.ToUpper().Contains(term));
        }

        var barcodes = await query
            .OrderByDescending(b => b.TimesSeen)
            .ThenByDescending(b => b.LastUsedAt)
            .Take(MaxBarcodeResults)
            .ToListAsync();

        return Results.Ok(barcodes.Select(ToDto));
    }

    /// <summary>
    /// "This is what we call this." The house stating a name for a barcode, moving no stock.
    /// </summary>
    [WolverinePut("/api/v1/guilds/{guildId}/pantry/barcodes/{barcode}")]
    public async Task<IResult> TeachBarcodeAsync(string guildId, string barcode,
        TeachPantryBarcodeDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] PantryCaptureService capture, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // One call rather than a feature check and a permission check: this gates on the Pantry
        // feature before it resolves a single role, so nothing a member holds can outrank a module
        // the guild has switched off.
        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePantry))
            return Results.Forbid();

        // Membership on top of the permission, because @everyone in a guild this account is not in
        // must not be a route into another household's barcode table.
        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var (result, error) = await capture.TeachBarcodeAsync(guildId, barcode, dto);

        if (error is not null) return Results.BadRequest(error);

        await ctx.SaveChangesAsync();
        await capture.PublishTeachAsync(result!);

        return Results.Ok(new TeachPantryBarcodeResultDto
        {
            Barcode = ToDto(result!.Barcode),
            Learned = result.Learned,
            RenamedItems = result.RenamedItems.Select(PantryCaptureService.ToDto).ToList(),
        });
    }

    private static PantryBarcodeDto ToDto(PantryBarcode barcode) => new()
    {
        Barcode = barcode.Barcode,
        Name = barcode.Name,
        Unit = barcode.Unit,
        DefaultQuantity = barcode.DefaultQuantity,
        LowThreshold = barcode.LowThreshold,
        TimesSeen = barcode.TimesSeen,
        LastUsedAt = barcode.LastUsedAt,
    };
}
