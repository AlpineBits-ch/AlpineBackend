using Isle.Domain.Enums;

namespace Isle.Domain.ValueObjects;

public class RewardConfig
{
    public RewardType RewardType { get; set; }

    /// <summary>
    /// Meaning depends on <see cref="RewardType"/>: XP for <see cref="Enums.RewardType.Xp"/>,
    /// percentage points for <see cref="Enums.RewardType.GrowthBoost"/>, slot count for
    /// <see cref="Enums.RewardType.StorageSlot"/>. The vital payouts are all-or-nothing and ignore it.
    /// </summary>
    public int Amount { get; set; }
    public string? CosmeticId { get; set; }         // only used when RewardType == "CosmeticUnlock"
    public RankRequirement AppliesTo { get; set; }  // e.g. Winner, Top3, AllParticipants
}