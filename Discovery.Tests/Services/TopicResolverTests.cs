using Discovery.Api.Dtos.Response;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Tests.Helpers;

namespace Discovery.Tests.Services;

[TestFixture]
public class TopicResolverTests
{
    [Test]
    public void Games_rank_above_tags_for_the_same_query()
    {
        // The tag is an exact match and the game only a partial one, so this pins that Kind beats
        // match quality: games always come first regardless of how well a tag fits the text.
        var tag = new TopicDto { Kind = "tag", Id = "isle", Name = "isle" };
        var game = new TopicDto { Kind = "game", Id = "gapp_isle", Name = "The Isle: Evrima" };

        var ranked = TopicResolver.RankOrder([tag, game], "isle");

        Assert.That(ranked.Select(t => t.Kind), Is.EqualTo(new[] { "game", "tag" }));
    }

    [Test]
    public async Task An_alias_finds_the_game_under_its_canonical_name()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic
        {
            Id = GameTopic.GenerateId(),
            GameApplicationId = "gapp_cs2",
            Name = "Counter-Strike 2",
            Aliases = ["CS2", "CSGO"],
        });
        await ctx.SaveChangesAsync();

        var resolver = new TopicResolver(ctx);
        var results = await resolver.SearchAsync("CS2", 10, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Kind, Is.EqualTo("game"));
            Assert.That(results[0].Name, Is.EqualTo("Counter-Strike 2"));
        });
    }

    [Test]
    public async Task A_disabled_game_is_never_offered()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic
        {
            Id = GameTopic.GenerateId(),
            GameApplicationId = "gapp_gone",
            Name = "Ghost Game",
            IsEnabled = false,
        });
        await ctx.SaveChangesAsync();

        var resolver = new TopicResolver(ctx);
        var results = await resolver.SearchAsync("Ghost", 10, CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task A_tag_merged_into_another_resolves_to_its_target()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.Tags.AddRange(
            new Tag { Id = Tag.GenerateId(), Slug = "tabletop", DisplayName = "Tabletop" },
            new Tag { Id = Tag.GenerateId(), Slug = "ttrpg", DisplayName = "TTRPG", AliasOf = "tabletop" });
        await ctx.SaveChangesAsync();

        var resolver = new TopicResolver(ctx);
        var results = await resolver.ResolveAsync([TopicRef.Parse("tag:ttrpg")], CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Kind, Is.EqualTo("tag"));
            Assert.That(results[0].Id, Is.EqualTo("tabletop"));
            Assert.That(results[0].Name, Is.EqualTo("Tabletop"));
        });
    }

    [Test]
    public async Task Ensuring_an_unknown_tag_mints_it_once_and_reuses_it_after()
    {
        await using var ctx = TestDiscoveryContext.New();
        var resolver = new TopicResolver(ctx);
        var topic = TopicRef.Parse("tag:west-marches");

        await resolver.EnsureTagsAsync([topic], CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.That(ctx.Tags.Count(), Is.EqualTo(1));
        var mintedId = ctx.Tags.Single().Id;

        await resolver.EnsureTagsAsync([topic], CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Tags.Count(), Is.EqualTo(1));
            Assert.That(ctx.Tags.Single().Id, Is.EqualTo(mintedId));
        });
    }
}
