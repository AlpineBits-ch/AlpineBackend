using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Exceptions;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.CommandController.Commands;

public class StoreDinoCommand(MicroserviceContext microserviceContext, IBridgeClient bridgeClient, ILogger<StoreDinoCommand> logger) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PlayerSpecies))
        {
            return "You need an active dino to store. Spawn in first, then try again.";
        }

        if (context.PlayerGrowth < Storage.GrowthThreshold)
        {
            return $"Your dino is too young to store. It must be at least {Storage.GrowthThreshold:P0} grown (currently {context.PlayerGrowth:P0}).";
        }

        var player = await microserviceContext.Players
            .Include(p => p.Storage)
            .ThenInclude(s => s.Slots)
            .FirstOrDefaultAsync(p => p.Id == context.PlayerId);

        if (player is null)
        {
            return "There was an issue accessing your storage (404), please report that on our discord!";
        }

        player.Storage ??= Storage.Create(player.Id);

        if (player.Storage.IsFull)
        {
            return $"Your storage is full ({player.Storage.Slots.Count}/{player.Storage.MaxSlotCount}). Buy a slot with !buyslot or load one out first.";
        }

        // Pull the current mutation loadout so it can be restored on load.
        MutationsData? mutations = null;
        try
        {
            mutations = await bridgeClient.GetMutationsAsync(context.PlayerSteam);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read mutations for {Steam}; storing without them", context.PlayerSteam);
        }

        StorageSlot slot;
        try
        {
            slot = player.Storage.StoreDino(new CreateStorageSlotParams
            {
                Species = context.PlayerSpecies,
                Growth = context.PlayerGrowth,
                HealthData = context.HealthData,
                Mutations = mutations
            });
        }
        catch (GrowthTooLowException)
        {
            return $"Your dino is too young to store. It must be at least {Storage.GrowthThreshold:P0} grown.";
        }
        catch (StorageFullException)
        {
            return $"Your storage is full ({player.Storage.MaxSlotCount} slots).";
        }

        await microserviceContext.SaveChangesAsync();

        // Remove the dino from the world now that it is safely persisted.
        try
        {
            await bridgeClient.KillAsync(context.PlayerSteam);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stored dino for {Steam} but failed to remove the live pawn", context.PlayerSteam);
        }

        return $"Stored your {slot.Species} ({slot.Growth:P0} grown). Storage: {player.Storage.Slots.Count}/{player.Storage.MaxSlotCount}.";
    }

    public override string Name { get; } = "store";
    public override string Description { get; } = "Stores your current dino into a storage slot";
    public override bool IsAdminOnly { get; set; } = false;
}
