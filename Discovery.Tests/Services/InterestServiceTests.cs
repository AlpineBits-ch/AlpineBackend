using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Tests.Helpers;

namespace Discovery.Tests.Services;

[TestFixture]
public class InterestServiceTests
{
    private static InterestService Service(TestDiscoveryContext ctx) => new(ctx, new TopicResolver(ctx));

    [Test]
    public async Task Replacing_removes_what_is_no_longer_listed()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.Tags.AddRange(
            new Tag { Id = Tag.GenerateId(), Slug = "golf", DisplayName = "Golf" },
            new Tag { Id = Tag.GenerateId(), Slug = "chess", DisplayName = "Chess" });
        ctx.UserInterests.AddRange(
            new UserInterest { Id = UserInterest.GenerateId(), UserId = "user_1", Kind = TopicKind.Tag, TopicId = "golf" },
            new UserInterest { Id = UserInterest.GenerateId(), UserId = "user_1", Kind = TopicKind.Tag, TopicId = "chess" });
        await ctx.SaveChangesAsync();

        var topics = new[] { new TopicInput(TopicRef.Parse("tag:chess"), "Chess") };
        await Service(ctx).ReplaceAsync("user_1", topics, true, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var remaining = ctx.UserInterests.Where(i => i.UserId == "user_1").ToList();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].TopicId, Is.EqualTo("chess"));
    }

    [Test]
    public async Task More_than_the_cap_is_refused_and_nothing_is_written()
    {
        await using var ctx = TestDiscoveryContext.New();
        var topics = Enumerable.Range(0, InterestService.MaxInterests + 1)
            .Select(i => new TopicInput(TopicRef.Parse($"tag:topic-{i}"), $"Topic {i}"))
            .ToList();

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await Service(ctx).ReplaceAsync("user_2", topics, true, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ctx.UserInterests.Any(i => i.UserId == "user_2"), Is.False);
            // Refused before EnsureTagsAsync even runs, so nothing was minted either.
            Assert.That(ctx.Tags.Any(), Is.False);
        });
    }

    [Test]
    public async Task An_unknown_tag_is_minted_on_the_way_in()
    {
        await using var ctx = TestDiscoveryContext.New();
        var topics = new[] { new TopicInput(TopicRef.Parse("tag:west-marches"), "West Marches") };

        var result = await Service(ctx).ReplaceAsync("user_3", topics, true, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Tags.Single().DisplayName, Is.EqualTo("West Marches"));
            Assert.That(ctx.UserInterests.Single(i => i.UserId == "user_3").TopicId, Is.EqualTo("west-marches"));
            // The mint happens in the same call and is never saved before the response is built, so
            // this also pins that the response does not silently drop a brand new tag.
            Assert.That(result.Topics.Single().Name, Is.EqualTo("West Marches"));
        });
    }

    [Test]
    public async Task Duplicates_in_one_request_collapse_to_one_row()
    {
        await using var ctx = TestDiscoveryContext.New();
        var topic = TopicRef.Parse("tag:duplicated");
        var topics = new[]
        {
            new TopicInput(topic, "Duplicated"),
            new TopicInput(topic, "Duplicated"),
        };

        await Service(ctx).ReplaceAsync("user_4", topics, true, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.That(ctx.UserInterests.Count(i => i.UserId == "user_4"), Is.EqualTo(1));
    }

    [Test]
    public async Task The_cap_is_checked_after_dedup_not_against_the_raw_request()
    {
        // 30 entries, only 10 distinct topics - the cap must bound what the user ends up with, not
        // how many times their client repeated itself.
        await using var ctx = TestDiscoveryContext.New();
        var distinctTopics = Enumerable.Range(0, 10).Select(i => TopicRef.Parse($"tag:topic-{i}")).ToList();
        var topics = distinctTopics
            .SelectMany(t => Enumerable.Repeat(new TopicInput(t, t.Id), 3))
            .ToList();

        Assert.That(topics, Has.Count.EqualTo(30));

        await Service(ctx).ReplaceAsync("user_6", topics, true, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.That(ctx.UserInterests.Count(i => i.UserId == "user_6"), Is.EqualTo(10));
    }

    [Test]
    public async Task Hiding_interests_does_not_remove_them()
    {
        await using var ctx = TestDiscoveryContext.New();
        var service = Service(ctx);
        var topics = new[] { new TopicInput(TopicRef.Parse("tag:visible-topic"), "Visible Topic") };

        await service.ReplaceAsync("user_5", topics, true, CancellationToken.None);
        await ctx.SaveChangesAsync();

        // Same topics, only visibility flips - the interest rows must survive untouched.
        var result = await service.ReplaceAsync("user_5", topics, false, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Visible, Is.False);
            Assert.That(result.Topics, Has.Count.EqualTo(1));
            Assert.That(ctx.UserInterests.Count(i => i.UserId == "user_5"), Is.EqualTo(1));
        });
    }
}
