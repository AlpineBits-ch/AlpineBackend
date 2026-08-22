using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Guild.Contracts.Bus.Request;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Discovery.Api.Services;

/// <summary>
/// Guild identity mirrored locally on a TTL so the ranked feed query never fans out to Guild per
/// card. Pull-with-TTL, not event-projected: Guild publishes no guild-lifecycle events today, only
/// the ...ForBots family, and inventing five to feed a card is a larger change to Guild than this
/// feature earns.
/// </summary>
public class GuildProfileMirror(
    MicroserviceContext ctx,
    IMessageBus bus,
    TimeProvider clock,
    ILogger<GuildProfileMirror> logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    // Mirrors the cap Guild enforces on its side of the same request.
    private const int MaxGuildIdsPerRequest = 200;

    /// <summary>
    /// Mutates the tracked context and returns without saving - called from inside a Wolverine
    /// endpoint whose AutoApplyTransactions middleware commits on a successful return. Never throws
    /// on a Guild request failure: every id in the batch just keeps whatever it already had.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GuildProfile>> EnsureFreshAsync(
        IReadOnlyCollection<string> guildIds, CancellationToken ct)
    {
        var ids = guildIds.Distinct().ToList();
        var rows = await ctx.GuildProfiles
            .Where(p => ids.Contains(p.GuildId))
            .ToDictionaryAsync(p => p.GuildId, ct);

        var now = clock.GetUtcNow();
        var staleOrMissing = ids
            .Where(id => !rows.TryGetValue(id, out var row) || now - row.ProjectedAt >= Ttl)
            .Take(MaxGuildIdsPerRequest)
            .ToList();

        if (staleOrMissing.Count == 0) return rows;

        GetGuildProfilesResponse? response;
        try
        {
            response = await bus.InvokeAsync<GetGuildProfilesResponse>(
                new GetGuildProfilesRequest { GuildIds = staleOrMissing }, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A card showing a stale guild name is fine; a feed request failing because Guild
            // hiccuped for a moment is not.
            logger.LogWarning(exception,
                "Could not refresh {Count} guild profile(s), keeping whatever is stored locally",
                staleOrMissing.Count);
            return rows;
        }

        var answered = response.Profiles.ToDictionary(p => p.GuildId);
        foreach (var id in staleOrMissing)
        {
            if (!answered.TryGetValue(id, out var dto))
            {
                // Guild answered the request but had nothing for this particular id - the existing
                // row, if any, is left exactly as it was rather than cleared or timestamped fresh.
                continue;
            }

            if (!rows.TryGetValue(id, out var row))
            {
                row = new GuildProfile { Id = GuildProfile.GenerateId(), GuildId = id };
                ctx.GuildProfiles.Add(row);
                rows[id] = row;
            }

            row.Name = dto.Name;
            row.IconUrl = dto.IconUrl;
            row.BannerUrl = dto.BannerUrl;
            row.MemberCount = dto.MemberCount;
            row.ActiveMemberCount = dto.ActiveMemberCount;
            row.Features = dto.Features;
            row.ProjectedAt = now;
        }

        return rows;
    }
}
