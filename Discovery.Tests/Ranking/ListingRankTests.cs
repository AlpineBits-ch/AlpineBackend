using Discovery.Domain.Ranking;

namespace Discovery.Tests.Ranking;

[TestFixture]
public class ListingRankTests
{
    private static readonly TimeSpan Now = TimeSpan.Zero;
    private static readonly TimeSpan AWeek = TimeSpan.FromDays(7);

    [Test]
    public void Matching_every_topic_beats_matching_none()
    {
        var all = ListingRank.Score(new RankInputs(4, 4, Now, 100));
        var none = ListingRank.Score(new RankInputs(0, 4, Now, 100));
        Assert.That(all, Is.GreaterThan(none));
    }

    [Test]
    public void At_equal_matches_the_broader_listing_ranks_lower()
    {
        var focused = ListingRank.Score(new RankInputs(2, 2, Now, 100));
        var broad = ListingRank.Score(new RankInputs(2, 8, Now, 100));
        Assert.That(broad, Is.LessThan(focused));
    }

    [Test]
    public void A_week_old_bump_is_worth_half_a_fresh_one()
    {
        var fresh = ListingRank.Score(new RankInputs(0, 4, Now, 0));
        var week = ListingRank.Score(new RankInputs(0, 4, AWeek, 0));
        Assert.That(week, Is.EqualTo(fresh / 2).Within(0.0001));
    }

    [Test]
    public void A_dead_guild_bumping_now_loses_to_a_healthy_one_from_last_week()
    {
        var dead = ListingRank.Score(new RankInputs(0, 4, Now, 0));
        var healthy = ListingRank.Score(new RankInputs(0, 4, AWeek, 5_000));
        Assert.That(healthy, Is.GreaterThan(dead));
    }

    [Test]
    public void A_full_interest_match_outranks_a_fresher_healthier_listing_with_none()
    {
        var matched = ListingRank.Score(new RankInputs(4, 4, AWeek, 0));
        var fresherAndHealthier = ListingRank.Score(new RankInputs(0, 4, Now, 10_000));
        Assert.That(matched, Is.GreaterThan(fresherAndHealthier));
    }

    [Test]
    public void With_no_interests_the_interest_term_is_equal_for_everyone()
    {
        var a = ListingRank.Score(new RankInputs(0, 2, Now, 100));
        var b = ListingRank.Score(new RankInputs(0, 8, Now, 100));
        Assert.That(a, Is.EqualTo(b).Within(0.0001));
    }

    [Test]
    public void Extreme_inputs_stay_in_range()
    {
        var scores = new[]
        {
            ListingRank.Score(new RankInputs(0, 0, TimeSpan.FromDays(-5), -10)),
            ListingRank.Score(new RankInputs(99, 1, TimeSpan.FromDays(9999), int.MaxValue)),
        };
        Assert.That(scores, Is.All.InRange(0d, 1d));
    }
}
