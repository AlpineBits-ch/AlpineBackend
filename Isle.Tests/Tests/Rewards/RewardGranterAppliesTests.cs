using Isle.Api.Services.Rewards;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;

namespace Isle.Tests.Tests.Rewards;

/// <summary>
/// <see cref="RewardGranter.Applies"/> is the tiering rule every quest/KOTH payout leans on: tiers nest
/// downward so a winner also collects Top3 and AllParticipants rows without a template having to
/// restate the participation payout inside the winner's.
/// </summary>
[TestFixture]
public class RewardGranterAppliesTests
{
    private static RewardConfig RewardFor(RankRequirement appliesTo) => new() { RewardType = RewardType.Xp, Amount = 100, AppliesTo = appliesTo };

    [TestCase(RankRequirement.Winner, true)]
    [TestCase(RankRequirement.Top3, false)]
    [TestCase(RankRequirement.AllParticipants, false)]
    public void WinnerOnlyReward_AppliesOnlyToWinnerRank(RankRequirement actualRank, bool expected)
    {
        var reward = RewardFor(RankRequirement.Winner);

        Assert.That(RewardGranter.Applies(reward, actualRank), Is.EqualTo(expected));
    }

    [TestCase(RankRequirement.Winner, true)]
    [TestCase(RankRequirement.Top3, true)]
    [TestCase(RankRequirement.AllParticipants, false)]
    public void Top3Reward_AppliesToWinnerAndTop3ButNotParticipants(RankRequirement actualRank, bool expected)
    {
        var reward = RewardFor(RankRequirement.Top3);

        Assert.That(RewardGranter.Applies(reward, actualRank), Is.EqualTo(expected));
    }

    [TestCase(RankRequirement.Winner, true)]
    [TestCase(RankRequirement.Top3, true)]
    [TestCase(RankRequirement.AllParticipants, true)]
    public void AllParticipantsReward_AppliesToEveryRank(RankRequirement actualRank, bool expected)
    {
        var reward = RewardFor(RankRequirement.AllParticipants);

        Assert.That(RewardGranter.Applies(reward, actualRank), Is.EqualTo(expected));
    }
}
