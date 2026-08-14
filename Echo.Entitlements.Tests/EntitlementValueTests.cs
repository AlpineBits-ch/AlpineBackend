using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Tests;

/// <summary>The three merge rules from spec section 4.1, and their opposites.</summary>
[TestFixture]
public class EntitlementValueTests
{
    [Test]
    public void Merging_two_flags_is_a_logical_or()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Merge(false, false).AsFlag, Is.False);
            Assert.That(Merge(true, false).AsFlag, Is.True);
            Assert.That(Merge(false, true).AsFlag, Is.True, "OR has to be symmetric");
            Assert.That(Merge(true, true).AsFlag, Is.True);
        });
    }

    [Test]
    public void Merging_two_numeric_limits_takes_the_maximum()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntitlementValue.Merge(Number(10), Number(50)).AsNumber, Is.EqualTo(50));
            Assert.That(EntitlementValue.Merge(Number(50), Number(10)).AsNumber, Is.EqualTo(50),
                "max has to be symmetric, or the order sources are registered in would change the answer");
        });
    }

    [Test]
    public void Merging_two_ladder_values_takes_the_highest_rank()
    {
        var ladder = EntitlementLadders.VideoQuality;

        var merged = EntitlementValue.Merge(
            EntitlementValue.OfRank(ladder.RankOf("720p30")),
            EntitlementValue.OfRank(ladder.RankOf("1080p60")));

        Assert.That(ladder.Describe(merged), Is.EqualTo("1080p60"));
    }

    [Test]
    public void Unlimited_wins_every_merge_and_loses_every_restriction()
    {
        var unlimited = Number(EntitlementValue.Unlimited);

        Assert.Multiple(() =>
        {
            Assert.That(EntitlementValue.Merge(unlimited, Number(10)).IsUnlimited, Is.True);
            Assert.That(EntitlementValue.Restrict(unlimited, Number(10)).AsNumber, Is.EqualTo(10),
                "an absent ceiling must never be the binding one, or a member with no plan would "
                + "cap the guild they are in");
        });
    }

    [Test]
    public void Restricting_is_the_opposite_of_merging_for_every_kind()
    {
        var ladder = EntitlementLadders.VideoQuality;

        Assert.Multiple(() =>
        {
            Assert.That(EntitlementValue.Restrict(Flag(true), Flag(false)).AsFlag, Is.False,
                "a paired flag needs both sides to allow it");
            Assert.That(EntitlementValue.Restrict(Number(50), Number(10)).AsNumber, Is.EqualTo(10));
            Assert.That(
                ladder.Describe(EntitlementValue.Restrict(
                    EntitlementValue.OfRank(ladder.RankOf("1080p60")),
                    EntitlementValue.OfRank(ladder.RankOf("480p30")))),
                Is.EqualTo("480p30"));
        });
    }

    [Test]
    public void Merging_values_of_different_kinds_is_refused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => EntitlementValue.Merge(Flag(true), Number(1)),
                Throws.InstanceOf<InvalidOperationException>());
            Assert.That(() => EntitlementValue.Restrict(Number(1), EntitlementValue.OfRank(0)),
                Throws.InstanceOf<InvalidOperationException>());
        });
    }

    [Test]
    public void Reading_a_value_as_the_wrong_kind_is_refused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => Flag(true).AsNumber, Throws.InstanceOf<InvalidOperationException>());
            Assert.That(() => Number(1).AsFlag, Throws.InstanceOf<InvalidOperationException>());
            Assert.That(() => Number(1).AsRank, Throws.InstanceOf<InvalidOperationException>());
        });
    }

    [Test]
    public void A_negative_limit_or_rank_cannot_be_constructed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => EntitlementValue.OfNumber(-1),
                Throws.InstanceOf<ArgumentOutOfRangeException>(),
                "zero already means 'none'; a negative limit would only ever be a sentinel nobody "
                + "handles consistently");
            Assert.That(() => EntitlementValue.OfRank(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void Zero_is_a_real_limit_and_not_an_absent_one()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntitlementValue.OfNumber(0).AsNumber, Is.Zero);
            Assert.That(EntitlementValue.Merge(Number(0), Number(0)).AsNumber, Is.Zero);
            Assert.That(EntitlementValue.OfNumber(0).IsUnlimited, Is.False);
        });
    }

    private static EntitlementValue Flag(bool granted) => EntitlementValue.OfFlag(granted);

    private static EntitlementValue Number(long limit) => EntitlementValue.OfNumber(limit);

    private static EntitlementValue Merge(bool left, bool right) =>
        EntitlementValue.Merge(Flag(left), Flag(right));
}
