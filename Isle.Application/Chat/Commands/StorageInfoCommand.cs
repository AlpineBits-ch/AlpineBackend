using System.Text;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

public class StorageInfoCommand(MicroserviceContext microserviceContext) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var player = await microserviceContext.Players
            .Include(p => p.Storage)
            .ThenInclude(s => s.Slots)
            .FirstOrDefaultAsync(p => p.Id == context.PlayerId);

        if (player is null)
        {
            return "There was an issue accessing your storage (404), please report that on our discord!";
        }

        var storage = player.Storage;
        var slotCount = storage?.Slots.Count ?? 0;
        var maxSlots = storage?.MaxSlotCount ?? 0;

        var sb = new StringBuilder();
        sb.Append($"XP: {player.Xp} | Slots: {slotCount}/{maxSlots} | Next slot: {Storage.SlotPurchaseCost} XP");

        if (storage is null || slotCount == 0)
        {
            sb.Append(" | Storage empty.");
            return sb.ToString();
        }

        var slots = storage.Slots.OrderBy(s => s.CreatedAt).ToList();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var deployed = slot.IsDeployed ? " [out]" : string.Empty;
            sb.Append($" | {i + 1}. {slot.FriendlySpeciesName()} ({slot.Growth:P0}){deployed}");
        }

        return sb.ToString();
    }

    public override string Name { get; } = "storage";
    public override string Description { get; } = "Shows your XP and stored dinos with their species";
    public override bool IsAdminOnly { get; set; } = false;
}
