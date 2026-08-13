using Isle.Api.Services.Progression;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class PublicProfileDto
{
    public string FriendlyId { get; init; } = string.Empty;
    public string Player { get; init; } = string.Empty;
    public string? FavouriteSpecies { get; init; }
    public long PlaytimeSeconds { get; init; }
    public int CompletedQuests { get; init; }

    /// <summary>When this service first saw them. Not a live signal - nothing on this surface is.</summary>
    public DateTimeOffset MemberSince { get; init; }
}

/// <summary>One player, by their friendly id, for anyone who has the link.</summary>
public static class PublicProfileEndpoints
{
    [WolverineGet("/api/v1/profiles/{friendlyId}")]
    public static async Task<IResult> Get(
        string friendlyId,
        [NotBody] MicroserviceContext db,
        [NotBody] PlayerPreferencesService preferences,
        [NotBody] PlaySessionTracker sessions,
        [NotBody] CancellationToken ct)
    {
        if (Player.DecodeFriendlyId(friendlyId) is not { } sequence)
            return Results.NotFound();

        var player = await db.Players
            .AsNoTracking()
            .Where(candidate => candidate.FriendlyIdSeq == sequence)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.FriendlyIdSeq,
                candidate.InGameName,
                candidate.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (player is null)
            return Results.NotFound();

        var settings = await preferences.GetAsync(player.Id, ct);
        if (!settings.PublicProfile)
            return Results.NotFound();

        var playtime = await sessions.SummariseAsync(player.Id, ct);

        var completed = await db.QuestParticipations
            .AsNoTracking()
            .CountAsync(row => row.PlayerId == player.Id && row.WasPaid, ct);

        return Results.Ok(new PublicProfileDto
        {
            FriendlyId = Player.EncodeFriendlyId(player.FriendlyIdSeq),
            Player = string.IsNullOrWhiteSpace(player.InGameName)
                ? $"Player {Player.EncodeFriendlyId(player.FriendlyIdSeq)}"
                : player.InGameName!,
            FavouriteSpecies = playtime.FavouriteSpecies,
            PlaytimeSeconds = playtime.TotalSeconds,
            CompletedQuests = completed,
            MemberSince = player.CreatedAt,
        });
    }
}
