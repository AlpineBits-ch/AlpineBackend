using Isle.Domain.Enums;

namespace Isle.Domain.ValueObjects;

public class RewardConfig
{
    public string RewardType { get; set; }         // e.g. "XpBoost", "Currency", "CosmeticUnlock" — maps via factory
    public int Amount { get; set; }
    public string? CosmeticId { get; set; }         // only used when RewardType == "CosmeticUnlock"
    public RankRequirement AppliesTo { get; set; }  // e.g. Winner, Top3, AllParticipants
}