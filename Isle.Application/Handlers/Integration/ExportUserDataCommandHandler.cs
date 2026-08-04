using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Integration;

/// <summary>
/// Isle's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
///
/// <para><b>Scoped through the account link, exactly as the purge is.</b> A player is identified by
/// Steam id and the Echo account is a nullable side-reference, so this handler resolves
/// <c>Player.UserId == command.UserId</c> and exports what hangs off those player rows: the player
/// itself, its dinosaur storage, its skins, and the kill log entries it appears in. An account with no
/// linked player produces an empty fragment, which is the honest answer rather than an error.</para>
///
/// <para><b>Kill logs name the other player by id and nothing else.</b> Every entry is a fact about
/// two people, and the subject is entitled to their half - that they killed, or were killed. They are
/// not entitled to the other player's Steam id or in-game name, and neither is resolved here. Quest
/// instances that merely <i>target</i> the subject are excluded for the same reason: a quest somebody
/// else was given is that person's row, even though the subject is named in it.</para>
///
/// <para><b>Live positional and voice state is not exported.</b> It is in-memory and Redis-backed with
/// a two-hour TTL - a snapshot of where a player's dinosaur was standing while the export assembled is
/// not a record the system keeps, and presenting one as though it were would misrepresent what is
/// held. What is held is what the purge handler drops on erasure, and it ages out on its own.</para>
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command, MicroserviceContext ctx)
    {
        var players = await ctx.Players
            .AsNoTracking()
            .Where(p => p.UserId == command.UserId)
            .ToListAsync();

        var playerIds = players.Select(p => p.Id).ToList();

        var storages = await ctx.Storages
            .AsNoTracking()
            .Where(s => playerIds.Contains(s.PlayerId))
            .ToListAsync();

        var storageIds = storages.Select(s => s.Id).ToList();

        var slots = await ctx.StorageSlots
            .AsNoTracking()
            .Where(s => storageIds.Contains(s.StorageId))
            .ToListAsync();

        var skins = await ctx.Skins
            .AsNoTracking()
            .Where(s => playerIds.Contains(s.PlayerId))
            .ToListAsync();

        var kills = await ctx.KillLogs
            .AsNoTracking()
            .Where(k => (k.KillerId != null && playerIds.Contains(k.KillerId))
                        || (k.VictimId != null && playerIds.Contains(k.VictimId)))
            .OrderBy(k => k.CreatedAt)
            .ToListAsync();

        var quests = await ctx.QuestInstances
            .AsNoTracking()
            .Where(q => q.CompletedByPlayerId != null && playerIds.Contains(q.CompletedByPlayerId))
            .OrderBy(q => q.StartedAt)
            .ToListAsync();

        var fragment = new
        {
            notice =
                "Isle game data is keyed to a Steam identity, linked to your Echo account. Live "
                + "positional and voice state is held only in memory with a two-hour expiry and is "
                + "not part of this export.",
            players = players.Select(p => new
            {
                p.Id,
                p.SteamId,
                p.InGameName,
                p.Xp,
                p.IsAdmin,
                p.CreatedAt,
            }),
            storage = storages.Select(s => new
            {
                s.Id,
                s.PlayerId,
                s.MaxSlotCount,
                slots = slots.Where(slot => slot.StorageId == s.Id).Select(slot => new
                {
                    slot.Id,
                    slot.Species,
                    slot.Growth,
                    slot.IsDeployed,
                    slot.CreatedAt,
                }),
            }),
            skins = skins.Select(s => new
            {
                s.Id,
                s.PlayerId,
                s.Species,
            }),
            killLog = kills.Select(k => new
            {
                k.Id,
                // Player ids only. The other party's Steam id and in-game name are theirs.
                k.KillerId,
                k.VictimId,
                k.VictimWeightKg,
                k.CreatedAt,
            }),
            quests = quests.Select(q => new
            {
                q.Id,
                q.QuestId,
                q.Title,
                type = q.Type.ToString(),
                q.StartedAt,
                q.EndedAt,
                q.BonusXp,
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "isle",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["players"] = players.Count,
                ["storage"] = storages.Count,
                ["storageSlots"] = slots.Count,
                ["skins"] = skins.Count,
                ["killLog"] = kills.Count,
                ["quests"] = quests.Count,
            },
        };
    }
}
