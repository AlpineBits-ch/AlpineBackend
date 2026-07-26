namespace Isle.Domain.Enums;

public enum RewardType
{
    Xp,
    CosmeticUnlock,

    // --- Vital payouts -------------------------------------------------------------------------
    // "Diet" is hunger + food; the bridge exposes no per-nutrient (carb/protein/lipid) setter, so
    // those two channels are what a diet reward can actually move. Half payouts top a player *up*
    // to the halfway mark and never reduce a vital that is already above it.

    /// <summary>Hunger and food to full.</summary>
    FullDiet,

    /// <summary>Hunger and food topped up to at least half.</summary>
    HalfDiet,

    /// <summary>Thirst to full.</summary>
    FullWater,

    /// <summary>Thirst topped up to at least half.</summary>
    HalfWater,

    /// <summary>Health to full.</summary>
    FullHealth,

    /// <summary>Stamina to full.</summary>
    FullStamina,

    // --- Lasting payouts -----------------------------------------------------------------------
    // The two above are consumed the moment the player takes a hit or starts running. These are not:
    // growth carries until the dino dies, a storage slot is permanent. They are the reason to chase a
    // quest rather than eat a corpse, so authored amounts should stay small.

    /// <summary>
    /// Adds <see cref="ValueObjects.RewardConfig.Amount"/> percentage points of growth, clamped at
    /// fully grown. Never reduces growth.
    /// </summary>
    GrowthBoost,

    /// <summary>
    /// Permanently widens the player's dino storage by <see cref="ValueObjects.RewardConfig.Amount"/>
    /// slots (at least one). The only payout that lands whether or not the player is online.
    /// </summary>
    StorageSlot,
}