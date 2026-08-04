using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Integration;

/// <summary>
/// Isle's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
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
