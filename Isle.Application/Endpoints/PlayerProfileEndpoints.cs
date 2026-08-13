using System.Security.Claims;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

// The endpoint method below is also called Storage, which would make a bare `Storage.GrowthThreshold`
// resolve to the method group rather than the aggregate.
using StorageAggregate = Isle.Domain.Aggregates.Storage;

namespace Isle.Api.Endpoints;

/// <summary>The caller's own island profile.</summary>
public sealed class IsleProfileDto
{
    /// <summary>False when this Venta account has never linked a Steam account, and therefore has no
    /// player on the island at all. The site should render the "link your Steam account" prompt.</summary>
    public bool Linked { get; init; }

    /// <summary>The short id shown by the in-game <c>!id</c> command, so a page and the chat box name
    /// the same player.</summary>
    public string? FriendlyId { get; init; }

    /// <summary>The caller's own Steam ID.</summary>
    public string? SteamId { get; init; }

    /// <summary>Null until the player has run <c>!link</c> in game - a linked Steam account and a
    /// known in-game name are two separate steps.</summary>
    public string? InGameName { get; init; }

    /// <summary>When the player row was first created, i.e. their first join, not their Venta signup.</summary>
    public DateTimeOffset? MemberSince { get; init; }

    public long Xp { get; init; }

    public bool IsAdmin { get; init; }

    /// <summary>Whether the service currently believes this player is in the world, from the
    /// join/leave event stream. Entries carry a two-hour TTL, so a missed disconnect self-corrects
    /// rather than pinning someone online forever.</summary>
    public bool IsInGame { get; init; }

    public static readonly IsleProfileDto NotLinked = new() { Linked = false };
}

public sealed class StorageSlotDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Friendly species name, resolved from the stored engine class the same way the
    /// in-game <c>!storage</c> listing does.</summary>
    public string Species { get; init; } = string.Empty;

    /// <summary>Growth from 0 to 1 at the moment the dino was stored, restored on load.</summary>
    public double Growth { get; init; }

    /// <summary>True while this slot's dino is the one live in the world.</summary>
    public bool IsDeployed { get; init; }

    public DateTimeOffset StoredAt { get; init; }

    /// <summary>Raw engine values as captured at store time, not normalised to 0..100 or refreshed
    /// since. Null for rows stored before health capture existed.</summary>
    public DinoHealthDto? Health { get; init; }
}

public sealed class DinoHealthDto
{
    public long Health { get; init; }
    public long Hunger { get; init; }
    public long Thirst { get; init; }
    public long Stamina { get; init; }
}

public sealed class IsleStorageDto
{
    /// <summary>See <see cref="IsleProfileDto.Linked"/>.</summary>
    public bool Linked { get; init; }

    public IReadOnlyList<StorageSlotDto> Slots { get; init; } = [];

    public int UsedSlotCount { get; init; }

    /// <summary>The player's current capacity.</summary>
    public int MaxSlotCount { get; init; }

    /// <summary>Minimum growth (0 to 1) a dino must have reached before it can be stored or loaded,
    /// from <see cref="Storage.GrowthThreshold"/>.</summary>
    public double GrowthThreshold { get; init; }

    /// <summary>XP cost of one additional slot, from <see cref="Storage.SlotPurchaseCost"/>. Served
    /// rather than duplicated in the client so the two cannot drift apart.</summary>
    public long SlotPurchaseCost { get; init; }
}

/// <summary>One saved skin.</summary>
public sealed class SkinDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Friendly species name resolved from the stored engine class.</summary>
    public string Species { get; init; } = string.Empty;

    /// <summary>The per-part colours as they are sent to the game.</summary>
    public SkinCustomizer? Customizer { get; init; }

    /// <summary>
    /// Whether this is the skin the game would currently reapply for this player.
    /// </summary>
    public bool IsEffective { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class IsleSkinsDto
{
    /// <summary>See <see cref="IsleProfileDto.Linked"/>.</summary>
    public bool Linked { get; init; }

    public IReadOnlyList<SkinDto> Skins { get; init; } = [];
}

/// <summary>
/// The signed-in caller's own island data for the companion website: profile, dino storage and
/// skins.
/// </summary>
public static class PlayerProfileEndpoints
{
    [Authorize]
    [WolverineGet("/api/v1/me")]
    public static async Task<IResult> Me(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] PlayerPresenceManager presence,
        [NotBody] CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        // The whole entity rather than a projection: FriendlyId is computed from FriendlyIdSeq by the
        // aggregate's own encoder, so it cannot be selected in SQL.
        var player = await db.Players
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (player is null) return Results.Ok(IsleProfileDto.NotLinked);

        return Results.Ok(new IsleProfileDto
        {
            Linked = true,
            FriendlyId = player.FriendlyId,
            SteamId = player.SteamId,
            InGameName = player.InGameName,
            MemberSince = player.CreatedAt,
            Xp = player.Xp,
            IsAdmin = player.IsAdmin,
            IsInGame = presence.IsPlayerOnline(player.Id),
        });
    }

    [Authorize]
    [WolverineGet("/api/v1/me/storage")]
    public static async Task<IResult> Storage(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var storage = await db.Storages
            .AsNoTracking()
            .Include(s => s.Slots)
            .FirstOrDefaultAsync(s => s.Player.UserId == userId, ct);

        if (storage is null)
        {
            // Either the caller has never linked Steam, or - far rarer - a player row predates
            // Storage being created alongside it.
            return Results.Ok(new IsleStorageDto
            {
                Linked = await db.Players.AsNoTracking().AnyAsync(p => p.UserId == userId, ct),
                GrowthThreshold = StorageAggregate.GrowthThreshold,
                SlotPurchaseCost = StorageAggregate.SlotPurchaseCost,
            });
        }

        return Results.Ok(new IsleStorageDto
        {
            Linked = true,
            UsedSlotCount = storage.Slots.Count,
            MaxSlotCount = storage.MaxSlotCount,
            GrowthThreshold = StorageAggregate.GrowthThreshold,
            SlotPurchaseCost = StorageAggregate.SlotPurchaseCost,
            Slots = storage.Slots
                .OrderBy(slot => slot.CreatedAt)
                .Select(slot => new StorageSlotDto
                {
                    Id = slot.Id,
                    Species = slot.FriendlySpeciesName(),
                    Growth = slot.Growth,
                    IsDeployed = slot.IsDeployed,
                    StoredAt = slot.CreatedAt,
                    Health = slot.HealthData is { } health
                        ? new DinoHealthDto
                        {
                            Health = health.Health,
                            Hunger = health.Hunger,
                            Thirst = health.Thirst,
                            Stamina = health.Stamina,
                        }
                        : null,
                })
                .ToList(),
        });
    }

    [Authorize]
    [WolverineGet("/api/v1/me/skins")]
    public static async Task<IResult> Skins(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var player = await db.Players
            .AsNoTracking()
            .Include(p => p.Skins)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (player is null) return Results.Ok(new IsleSkinsDto { Linked = false });

        // Deliberately the same pick SkinStore makes, unordered LastOrDefault included - see
        // SkinDto.IsEffective.
        var effectiveSkinId = player.Skins.LastOrDefault()?.Id;

        return Results.Ok(new IsleSkinsDto
        {
            Linked = true,
            Skins = player.Skins
                .OrderBy(skin => skin.CreatedAt)
                .Select(skin => new SkinDto
                {
                    Id = skin.Id,
                    Species = IsleBridge.Sdk.Species.FriendlyName(skin.Species),
                    Customizer = skin.Customizer,
                    IsEffective = skin.Id == effectiveSkinId,
                    CreatedAt = skin.CreatedAt,
                })
                .ToList(),
        });
    }
}
