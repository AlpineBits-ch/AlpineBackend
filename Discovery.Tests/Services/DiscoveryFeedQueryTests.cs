using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests.Services;

[TestFixture]
public class DiscoveryFeedQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DiscoveryFeedQuery Query(TestDiscoveryContext ctx, FakeMessageBus? bus = null) => new(
        ctx,
        new GuildProfileMirror(ctx, bus ?? new FakeMessageBus(), new TestClock(Now), NullLogger<GuildProfileMirror>.Instance),
        new TopicResolver(ctx),
        new TestClock(Now));

    private static FeedRequest Request(
        string userId = "user_1",
        string? query = null,
        IReadOnlyList<TopicRef>? topics = null,
        string? language = null,
        string? cursor = null,
        int limit = 24) =>
        new(userId, query, topics ?? [], language, cursor, limit);

    private static Listing BuildListing(
        string guildId, string headline, ListingState state, DateTimeOffset? bumpedAt, params TopicRef[] topics)
    {
        var listing = Listing.Create(guildId);
        listing.Headline = headline;
        listing.Pitch = $"{headline} - a pitch.";
        listing.Language = "en";
        listing.State = state;
        listing.PublishedAt = bumpedAt;
        listing.LastBumpedAt = bumpedAt;
        foreach (var topic in topics)
            listing.Topics.Add(ListingTopic.For(listing.Id, topic));
        return listing;
    }

    [Test]
    public async Task Only_published_listings_appear()
    {
        // One assertion covers Draft, Unlisted and Suspended together - the honest scope of the
        // rule is one WHERE clause, and three separate tests for it would be padding.
        await using var ctx = TestDiscoveryContext.New();
        ctx.Listings.AddRange(
            BuildListing("gild_pub", "Published community", ListingState.Published, Now, TopicRef.Parse("tag:chess")),
            BuildListing("gild_draft", "Draft community", ListingState.Draft, null),
            BuildListing("gild_unlisted", "Unlisted community", ListingState.Unlisted, Now),
            BuildListing("gild_suspended", "Suspended community", ListingState.Suspended, Now));
        await ctx.SaveChangesAsync();

        var page = await Query(ctx).RunAsync(Request(), CancellationToken.None);

        Assert.That(page.Cards.Select(c => c.GuildId), Is.EquivalentTo(new[] { "gild_pub" }));
    }

    [Test]
    public async Task Every_card_names_the_topics_it_matched()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.Tags.Add(new Tag { Id = Tag.GenerateId(), Slug = "chess", DisplayName = "Chess" });
        ctx.UserInterests.Add(new UserInterest
        {
            Id = UserInterest.GenerateId(), UserId = "user_1", Kind = TopicKind.Tag, TopicId = "chess",
        });
        ctx.Listings.Add(BuildListing("gild_1", "Chess club", ListingState.Published, Now,
            TopicRef.Parse("tag:chess"), TopicRef.Parse("tag:board-games")));
        await ctx.SaveChangesAsync();

        var page = await Query(ctx).RunAsync(Request(), CancellationToken.None);

        var card = page.Cards.Single();
        Assert.Multiple(() =>
        {
            // board-games is on the listing but not in the user's interests, so it must not appear.
            Assert.That(card.MatchedTopics, Has.Count.EqualTo(1));
            Assert.That(card.MatchedTopics[0].Id, Is.EqualTo("chess"));
            Assert.That(card.MatchedTopics[0].Name, Is.EqualTo("Chess"));
        });
    }

    [Test]
    public async Task With_no_interests_the_feed_is_still_ordered_and_not_empty()
    {
        // The first thing a new user sees, and the easiest path to leave returning nothing.
        await using var ctx = TestDiscoveryContext.New();
        ctx.Listings.AddRange(
            BuildListing("gild_fresh", "Fresh community", ListingState.Published, Now,
                TopicRef.Parse("tag:chess")),
            BuildListing("gild_stale", "Stale community", ListingState.Published, Now - TimeSpan.FromDays(30),
                TopicRef.Parse("tag:golf")));
        await ctx.SaveChangesAsync();

        // No UserInterests rows exist for user_1 at all.
        var page = await Query(ctx).RunAsync(Request(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(page.Cards, Has.Count.EqualTo(2));
            Assert.That(page.Cards[0].GuildId, Is.EqualTo("gild_fresh"),
                "freshness alone must still order the feed when interest overlap is zero for everyone");
        });
    }

    [Test]
    public async Task A_topic_filter_excludes_listings_without_it()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.Listings.AddRange(
            BuildListing("gild_has_it", "Has the topic", ListingState.Published, Now, TopicRef.Parse("tag:chess")),
            BuildListing("gild_without", "Without the topic", ListingState.Published, Now, TopicRef.Parse("tag:golf")));
        await ctx.SaveChangesAsync();

        var page = await Query(ctx).RunAsync(
            Request(topics: [TopicRef.Parse("tag:chess")]), CancellationToken.None);

        Assert.That(page.Cards.Select(c => c.GuildId), Is.EquivalentTo(new[] { "gild_has_it" }));
    }

    [Test]
    public async Task A_card_carries_guild_identity_from_the_mirror()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GuildProfiles.Add(new GuildProfile
        {
            Id = GuildProfile.GenerateId(),
            GuildId = "gild_1",
            Name = "The Isle",
            IconUrl = "/api/v1/guild/guilds/gild_1/icon",
            MemberCount = 42,
            ActiveMemberCount = 10,
            ProjectedAt = Now,
        });
        ctx.Listings.Add(BuildListing("gild_1", "The Isle community", ListingState.Published, Now,
            TopicRef.Parse("tag:chess")));
        await ctx.SaveChangesAsync();

        var page = await Query(ctx).RunAsync(Request(), CancellationToken.None);

        var card = page.Cards.Single();
        Assert.Multiple(() =>
        {
            Assert.That(card.GuildName, Is.EqualTo("The Isle"));
            Assert.That(card.GuildIconUrl, Is.EqualTo("/api/v1/guild/guilds/gild_1/icon"));
            Assert.That(card.MemberCount, Is.EqualTo(42));
        });
    }
}
