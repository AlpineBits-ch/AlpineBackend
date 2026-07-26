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
}