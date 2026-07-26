using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

public class LoadDinoCommand(MicroserviceContext microserviceContext, IBridgeClient bridgeClient, ILogger<LoadDinoCommand> logger) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var slotArg = context.Arguments.FirstOrDefault();
        if (!int.TryParse(slotArg, out var slotNumber) || slotNumber < 1)
        {
            return "Usage: !load slot number - see your slots with !storage";
        }

        var player = await microserviceContext.Players
            .Include(p => p.Storage)
            .ThenInclude(s => s.Slots)
            .FirstOrDefaultAsync(p => p.Id == context.PlayerId);

        if (player?.Storage is null || player.Storage.Slots.Count == 0)
        {
            return "Your storage is empty.";
        }

        // Stable ordering so the numbers shown by !storage match what !load expects.
        var slots = player.Storage.Slots.OrderBy(s => s.CreatedAt).ToList();
        if (slotNumber > slots.Count)
        {
            return $"You only have {slots.Count} slot(s) in use. Use a number between 1 and {slots.Count}.";
        }

        var slot = slots[slotNumber - 1];

        if (slot.Growth < Storage.GrowthThreshold)
        {
            return $"That slot's dino is below the {Storage.GrowthThreshold:P0} growth threshold and cannot be loaded.";
        }

        // Swap the player's live pawn into the stored species/growth first; only consume the
        // slot once the swap actually succeeds so a failed load never loses the dino.
        var swap = await bridgeClient.SwapAsync(context.PlayerSteam, slot.Species, slot.Growth);
        if (!swap.Ok)
        {
            logger.LogWarning("Swap failed loading {Species} for {Steam}: {Code}", slot.FriendlySpeciesName(), context.PlayerSteam, swap.Code);
            return swap.Code switch
            {
                ResultCode.NoPawn => "You need a live dino to load onto. Spawn in first, then try again.",
                ResultCode.NotWhitelisted => $"{slot.FriendlySpeciesName()} is not allowed on this server right now.",
                _ => "Could not load your dino, please try again in a moment."
            };
        }

        if (slot.HealthData is not null)
        {
            try
            {
                await bridgeClient.SetStatsAsync(context.PlayerSteam, new SetStatsArgs
                {
                    Health = slot.HealthData.Health,
                    Stamina = slot.HealthData.Stamina,
                    Hunger = slot.HealthData.Hunger,
                    Thirst = slot.HealthData.Thirst
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Loaded {Species} for {Steam} but failed to restore vitals", slot.FriendlySpeciesName(), context.PlayerSteam);
            }
        }

        try
        {
            await bridgeClient.SetMutationsAsync(context.PlayerSteam, ToSetMutationsArgs(slot.Mutations));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Loaded {Species} for {Steam} but failed to restore mutations", slot.FriendlySpeciesName(), context.PlayerSteam);
        }

        // Keep the slot but mark it deployed: the dino is now live and tied to this slot. If it dies
        // the slot is wiped (see WipeDeployedSlotsCommand); until then it still occupies the slot.
        player.Storage.MarkDeployed(slot.Id);
        await microserviceContext.SaveChangesAsync();

        return $"Loaded your {slot.FriendlySpeciesName()} ({slot.Growth:P0} grown). It's live now - if it dies you lose this slot's dino.";
    }

    /// <summary>
    /// Maps a stored <see cref="MutationsData"/> read (slots keyed by engine slot name) back onto
    /// the named <see cref="SetMutationsArgs"/> fields. Matching is case-insensitive; unknown keys
    /// are ignored.
    /// </summary>
    private static SetMutationsArgs ToSetMutationsArgs(MutationsData? data)
    {
        var slots = data?.Slots is { } s
            ? new Dictionary<string, string>(s, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? Get(string key) => slots.TryGetValue(key, out var v) ? v : null;

        return new SetMutationsArgs
        {
            Slot1 = Get("Slot1"), Slot2 = Get("Slot2"), Slot3 = Get("Slot3"), Slot4 = Get("Slot4"),
            Parent1 = Get("Parent1"), Parent2 = Get("Parent2"), Parent3 = Get("Parent3"), Parent4 = Get("Parent4"),
            ElderA1 = Get("ElderA1"), ElderA2 = Get("ElderA2"), ElderA3 = Get("ElderA3"), ElderA4 = Get("ElderA4"),
            ElderB1 = Get("ElderB1"), ElderB2 = Get("ElderB2"), ElderB3 = Get("ElderB3"), ElderB4 = Get("ElderB4"),
            ElderStacks = data?.ElderStacks,
            Unlocks = data?.Unlocks
        };
    }

    public override string Name { get; } = "load";
    public override string Description { get; } = "Loads a stored dino back onto your current pawn";
    public override bool IsAdminOnly { get; set; } = false;
}
