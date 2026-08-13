using System.Numerics;
using System.Security.Claims;
using Isle.Api.Services.Progression;
using Isle.Api.Services.Quests;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class ProgressionSummaryDto
{
    /// <summary>Null until the account has a linked, seen player. See <see cref="CallerPlayer"/>.</summary>
    public bool HasPlayer { get; init; }

    public string FriendlyId { get; init; } = string.Empty;
    public string? InGameName { get; init; }

    /// <summary>When their player row was created - the first time this service saw them at all.</summary>
    public DateTimeOffset MemberSince { get; init; }

    /// <summary>Their first recorded session, which is later than <see cref="MemberSince"/> for anyone
    /// who was already playing before sessions were tracked. Null when they have none.</summary>
    public DateTimeOffset? FirstPlayedAt { get; init; }

    /// <summary>Total playtime in seconds.</summary>
    public long PlaytimeSeconds { get; init; }

    /// <summary>Most-played species, derived rather than chosen. Null when nothing has been attributed.</summary>
    public string? FavouriteSpecies { get; init; }

    public long Xp { get; init; }
    public bool IsAdmin { get; init; }
    public int CompletedQuests { get; init; }
    public int SkinCount { get; init; }
}

public sealed class PlayerQuestDto
{
    public string QuestInstanceId { get; init; } = string.Empty;
    public string FriendlyId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public QuestType Type { get; init; }
    public string? LocationName { get; init; }

    public int Progress { get; init; }
    public int Goal { get; init; }

    /// <summary><c>active</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>What they actually received, empty when nothing did. Never what they were owed.</summary>
    public string Reward { get; init; } = string.Empty;

    public DateTimeOffset? EndsAt { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }
}

/// <summary>What the player wants a skin called. Blank means "back to the species name".</summary>
public sealed class RenameSkinDto
{
    public string? Name { get; init; }
}

public sealed class LiveDinoDto
{
    public string Species { get; init; } = string.Empty;

    /// <summary>0..1.</summary>
    public double Growth { get; init; }

    /// <summary>"Male" or "Female", or null.</summary>
    public string? Gender { get; init; }

    /// <summary>Fractions of their own maxima, or null when the server reported no maximum.</summary>
    public double? Health { get; init; }

    public double? Hunger { get; init; }
    public double? Thirst { get; init; }
    public double? Stamina { get; init; }

    /// <summary>A place name, not coordinates. The raw position never leaves this service.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>Roughly when this life began, from <c>PlayerSpawnTracker</c>.</summary>
    public DateTimeOffset? AliveSince { get; init; }

    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
/// The signed-in player's own view of themselves: what they have played, what they have taken part
/// in, and what their dinosaur is doing right now.
/// </summary>
public static class PlayerProgressionEndpoints
{
    private const string StatusActive = "active";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";

    /// <summary>How much history a player is shown. The page is a summary, not an archive.</summary>
    private const int HistoryLimit = 50;

    [Authorize]
    [WolverineGet("/api/v1/progression/summary")]
    public static async Task<ProgressionSummaryDto> Summary(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] PlaySessionTracker sessions,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);
        if (caller is null)
            return new ProgressionSummaryDto { HasPlayer = false };

        var player = await db.Players
            .AsNoTracking()
            .Where(candidate => candidate.Id == caller.PlayerId)
            .Select(candidate => new
            {
                candidate.FriendlyIdSeq,
                candidate.InGameName,
                candidate.CreatedAt,
                candidate.Xp,
                candidate.IsAdmin,
                SkinCount = candidate.Skins.Count,
            })
            .FirstAsync(ct);

        var playtime = await sessions.SummariseAsync(caller.PlayerId, ct);

        // Paid, not merely present.
        var completed = await db.QuestParticipations
            .AsNoTracking()
            .CountAsync(row => row.PlayerId == caller.PlayerId && row.WasPaid, ct);

        return new ProgressionSummaryDto
        {
            HasPlayer = true,
            FriendlyId = Player.EncodeFriendlyId(player.FriendlyIdSeq),
            InGameName = player.InGameName,
            MemberSince = player.CreatedAt,
            FirstPlayedAt = playtime.FirstPlayedAt,
            PlaytimeSeconds = playtime.TotalSeconds,
            FavouriteSpecies = playtime.FavouriteSpecies,
            Xp = player.Xp,
            IsAdmin = player.IsAdmin,
            CompletedQuests = completed,
            SkinCount = player.SkinCount,
        };
    }

    /// <summary>
    /// The caller's quests: everything currently open that they have made progress on, then their
    /// settled history.
    /// </summary>
    [Authorize]
    [WolverineGet("/api/v1/progression/quests")]
    public static async Task<IReadOnlyList<PlayerQuestDto>> Quests(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] QuestProgressLedger ledger,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);
        if (caller is null) return [];

        var quests = new List<PlayerQuestDto>();

        var open = await db.QuestInstances
            .AsNoTracking()
            .Where(instance => instance.State == QuestInstanceState.Active && instance.Type == QuestType.Exploration)
            .OrderBy(instance => instance.ExpiresAt)
            .ToListAsync(ct);

        foreach (var instance in open)
        {
            // minTicks 0 - this is asking what the player has done so far, not who has qualified.
            var visitors = await ledger.GetQualifiedAsync(instance.Id, minTicks: 0);
            var mine = visitors.FirstOrDefault(visitor => visitor.SteamId == caller.SteamId);
            if (mine is null) continue;

            quests.Add(new PlayerQuestDto
            {
                QuestInstanceId = instance.Id,
                FriendlyId = instance.FriendlyId,
                Title = instance.Title,
                Type = instance.Type,
                LocationName = instance.LocationName,
                Progress = mine.Ticks,
                Goal = QuestCompletionService.RequiredPresenceTicks,
                Status = StatusActive,
                EndsAt = instance.ExpiresAt,
            });
        }

        var history = await db.QuestParticipations
            .AsNoTracking()
            .Include(row => row.QuestInstance)
            .Where(row => row.PlayerId == caller.PlayerId)
            .OrderByDescending(row => row.RecordedAt)
            .Take(HistoryLimit)
            .ToListAsync(ct);

        quests.AddRange(history.Select(row => new PlayerQuestDto
        {
            QuestInstanceId = row.QuestInstanceId,
            FriendlyId = row.QuestInstance?.FriendlyId ?? string.Empty,
            Title = row.QuestInstance?.Title ?? string.Empty,
            Type = row.QuestInstance?.Type ?? QuestType.Exploration,
            LocationName = row.QuestInstance?.LocationName,
            Progress = row.Progress,
            Goal = row.Goal,

            // Their outcome, not the instance's.
            Status = row.WasPaid ? StatusCompleted : StatusFailed,
            Reward = row.RewardSummary,
            EndsAt = row.QuestInstance?.EndedAt,
            RecordedAt = row.RecordedAt,
        }));

        return quests;
    }

    /// <summary>
    /// The caller's live dinosaur, or 204 when there is not one: offline, dead and not yet respawned, or
    /// the stats feed is down. All three are ordinary states and none of them is an error.
    /// </summary>
    [Authorize]
    [WolverineGet("/api/v1/progression/dino")]
    public static async Task<IResult> Dino(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] PlayerVitalsCache vitals,
        [NotBody] PlayerSpawnTracker spawns,
        [NotBody] RegionMap regions,
        [NotBody] WorldRosterCache roster,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);
        if (caller is null || string.IsNullOrWhiteSpace(caller.SteamId))
            return Results.NoContent();

        var snapshot = await vitals.GetAsync(caller.SteamId, ct);
        if (snapshot is null)
            return Results.NoContent();

        return Results.Ok(new LiveDinoDto
        {
            Species = IsleBridge.Sdk.Species.FriendlyName(snapshot.Species),
            Growth = snapshot.Growth,

            // The one field the stats feed cannot answer, so it comes off the roster's slower read.
            Gender = roster.FindBySteam(caller.SteamId)?.Gender,
            Health = snapshot.Health,
            Hunger = snapshot.Hunger,
            Thirst = snapshot.Thirst,
            Stamina = snapshot.Stamina,

            // A name, never the numbers.
            Location = regions.Describe(new Vector3((float)snapshot.X, (float)snapshot.Y, (float)snapshot.Z)),
            AliveSince = await spawns.GetLastSpawnAsync(caller.SteamId, ct),
            ObservedAt = snapshot.ObservedAt,
        });
    }

    /// <summary>Makes one of the caller's skins the one they wear.</summary>
    [Authorize]
    [WolverinePost("/api/v1/progression/skins/{skinId}/equip")]
    public static async Task<IResult> EquipSkin(
        string skinId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);
        if (caller is null)
            return Results.NotFound("This account has no linked player.");

        var player = await db.Players
            .Include(candidate => candidate.Skins)
            .FirstOrDefaultAsync(candidate => candidate.Id == caller.PlayerId, ct);

        if (player is null)
            return Results.NotFound("This account has no linked player.");

        // Not found rather than forbidden for a skin that belongs to somebody else: the two answers
        // would otherwise tell an enumerating caller which ids exist.
        if (!player.EquipSkin(skinId))
            return Results.NotFound("No such skin.");

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>Renames one of the caller's skins.</summary>
    [Authorize]
    [WolverinePost("/api/v1/progression/skins/{skinId}/name")]
    public static async Task<IResult> RenameSkin(
        string skinId,
        RenameSkinDto body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);
        if (caller is null)
            return Results.NotFound("This account has no linked player.");

        // Scoped to the caller's own player in the query itself, so there is no window in which a skin
        // is found and then checked - same reasoning as the equip route's not-found answer.
        var skin = await db.Skins.FirstOrDefaultAsync(
            candidate => candidate.Id == skinId && candidate.PlayerId == caller.PlayerId, ct);

        if (skin is null)
            return Results.NotFound("No such skin.");

        skin.Name = Skin.ResolveName(body.Name, skin.Species);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { skin.Id, skin.Name });
    }
}
