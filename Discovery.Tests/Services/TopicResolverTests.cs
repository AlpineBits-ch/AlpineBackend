using Discovery.Api.Dtos.Response;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

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
            // What GameCatalogSync.RunAsync would populate: Name + every Alias, lower-invariant.
            SearchText = "counter-strike 2 cs2 csgo",
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
    public async Task A_lowercase_alias_fragment_finds_the_game_under_its_canonical_name()
    {
        // The alias is uppercase, the query is a lowercase fragment of it - an exact-match
        // implementation (the alias regression this test pins) would find nothing here.
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic
        {
            Id = GameTopic.GenerateId(),
            GameApplicationId = "gapp_msfs",
            Name = "Microsoft Flight Simulator",
            Aliases = ["MSFS 2024", "MSFS2024"],
            SearchText = "microsoft flight simulator msfs 2024 msfs2024",
        });
        await ctx.SaveChangesAsync();

        var resolver = new TopicResolver(ctx);
        var results = await resolver.SearchAsync("msfs", 10, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Kind, Is.EqualTo("game"));
            Assert.That(results[0].Name, Is.EqualTo("Microsoft Flight Simulator"));
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
        var topic = new TopicInput(TopicRef.Parse("tag:west-marches"), "West Marches");

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

    [Test]
    public async Task Minting_a_tag_uses_the_typed_display_name_and_keeps_it_on_recasing()
    {
        await using var ctx = TestDiscoveryContext.New();
        var resolver = new TopicResolver(ctx);
        var topic = TopicRef.Parse("tag:play-by-post");

        // The slug the wire format carries is already lossy ("play-by-post"); the raw text is what
        // TopicInput exists to carry alongside it.
        await resolver.EnsureTagsAsync([new TopicInput(topic, "Play By Post")], CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.That(ctx.Tags.Single().DisplayName, Is.EqualTo("Play By Post"));

        // A second mint with different casing must not clobber what is already there - first writer
        // wins, a staff merge is the tool for fixing a bad display name.
        await resolver.EnsureTagsAsync([new TopicInput(topic, "PLAY BY POST")], CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Tags.Count(), Is.EqualTo(1));
            Assert.That(ctx.Tags.Single().DisplayName, Is.EqualTo("Play By Post"));
        });
    }

    /// <summary>
    /// The catalogue is tens of thousands of rows, so a common substring matches far more than the
    /// candidate cap. Without an order on the candidate query the database is free to return any
    /// slice of them, and the row the user actually typed simply is not in it.
    /// </summary>
    [Test]
    public async Task A_match_past_the_candidate_cap_still_reaches_the_results()
    {
        await using var ctx = TestDiscoveryContext.New();

        // The cap is max(limit * 10, 50), so 60 fillers overflow it at any small limit. Added
        // before the target, so insertion order alone would push the target out.
        for (var i = 0; i < 60; i++)
        {
            ctx.GameTopics.Add(new GameTopic
            {
                Id = GameTopic.GenerateId(),
                GameApplicationId = $"gapp_filler_{i}",
                Name = $"A Game That Mentions Microsoft In Passing {i}",
                SearchText = $"a game that mentions microsoft in passing {i}",
            });
        }

        ctx.GameTopics.Add(new GameTopic
        {
            Id = GameTopic.GenerateId(),
            GameApplicationId = "gapp_msfs",
            Name = "Microsoft Flight Simulator",
            SearchText = "microsoft flight simulator",
        });
        await ctx.SaveChangesAsync();

        var resolver = new TopicResolver(ctx);
        var results = await resolver.SearchAsync("microsoft", 5, CancellationToken.None);

        Assert.That(results.Select(t => t.Name), Does.Contain("Microsoft Flight Simulator"));
    }

    /// <summary>
    /// Guards against SearchAsync quietly reverting to fetching the whole table and filtering in
    /// memory: asserts against the real Npgsql provider (no live database - ToQueryString never
    /// executes) that the candidate queries carry a WHERE clause rather than being a bare scan.
    /// </summary>
    [Test]
    public async Task Search_filters_in_sql_rather_than_scanning_the_whole_catalogue()
    {
        await using var postgres = new PostgresDiscoveryContext();

        var gamesSql = TopicResolver.GameCandidatesQuery(postgres, "isle").ToQueryString();
        var tagsSql = TopicResolver.TagCandidatesQuery(postgres, "isle").ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(gamesSql, Does.Contain("WHERE"));
            Assert.That(tagsSql, Does.Contain("WHERE"));
        });
    }
}
