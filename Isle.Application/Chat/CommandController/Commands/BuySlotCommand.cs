using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.CommandController.Commands;

public class BuySlotCommand(MicroserviceContext microserviceContext) : ChatCommand
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

        player.Storage ??= Storage.Create(player.Id);

        if (!player.TrySpendXp(Storage.SlotPurchaseCost))
        {
            return $"Not enough XP. A slot costs {Storage.SlotPurchaseCost} XP and you have {player.Xp}.";
        }

        player.Storage.PurchaseSlot();
        await microserviceContext.SaveChangesAsync();

        return $"Slot purchased! You now have {player.Storage.MaxSlotCount} slots and {player.Xp} XP left.";
    }

    public override string Name { get; } = "buyslot";
    public override string Description { get; } = "Buys an extra storage slot for XP";
    public override bool IsAdminOnly { get; set; } = false;
}
