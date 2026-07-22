using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.CommandController.Commands;

/// <summary>
/// Resolves a player from a free-form identifier a user typed in chat: a Steam id, a friendly id,
/// or an in-game name.
/// </summary>
internal static class PlayerResolver
{
    internal enum ResolveOutcome
    {
        Found,
        NotFound,
        Ambiguous
    }

    internal readonly record struct ResolveResult(ResolveOutcome Outcome, Player? Player);

    public static async Task<ResolveResult> ResolveAsync(MicroserviceContext context, string identifier, CancellationToken ct = default)
    {
        identifier = identifier.Trim();
        if (identifier.Length == 0)
            return new ResolveResult(ResolveOutcome.NotFound, null);

        // 1. Steam id (exact, unique).
        var bySteam = await context.Players.FirstOrDefaultAsync(p => p.SteamId == identifier, ct);
        if (bySteam is not null)
            return new ResolveResult(ResolveOutcome.Found, bySteam);

        // 2. Friendly id (decode to the underlying sequence, unique).
        var seq = Player.DecodeFriendlyId(identifier);
        if (seq is not null)
        {
            var byFriendly = await context.Players.FirstOrDefaultAsync(p => p.FriendlyIdSeq == seq.Value, ct);
            if (byFriendly is not null)
                return new ResolveResult(ResolveOutcome.Found, byFriendly);
        }

        // 3. In-game name (case-insensitive, may collide).
        var lowered = identifier.ToLower();
        var byName = await context.Players
            .Where(p => p.InGameName != null && p.InGameName.ToLower() == lowered)
            .Take(2)
            .ToListAsync(ct);

        return byName.Count switch
        {
            1 => new ResolveResult(ResolveOutcome.Found, byName[0]),
            > 1 => new ResolveResult(ResolveOutcome.Ambiguous, null),
            _ => new ResolveResult(ResolveOutcome.NotFound, null)
        };
    }
}
