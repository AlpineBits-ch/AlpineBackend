using Guild.Domain;
using Guild.Domain.Entity;
using Guild.Domain.Events.Wiki;

namespace Guild.Tests.Domain;

/// <summary>
/// The domain rules behind wiki reactions, watchers and comments - the parts the endpoint tests
/// exercise through HTTP results but do not pin directly.
/// </summary>
[TestFixture]
public class WikiEngagementTests
{
    // ── Reactions ────────────────────────────────────────────────────────────

    [Test]
    public void Reaction_Create_SetsAllPropertiesFromParams()
    {
        var reaction = WikiPageReaction.Create(new CreateWikiPageReactionParams
        {
            PageId = "wkpg_abc", GuildId = "gild_abc", UserId = "user_1", Emoji = "👍",
        });

        Assert.Multiple(() =>
        {
            Assert.That(reaction.PageId, Is.EqualTo("wkpg_abc"));
            Assert.That(reaction.GuildId, Is.EqualTo("gild_abc"));
            Assert.That(reaction.UserId, Is.EqualTo("user_1"));
            Assert.That(reaction.Emoji, Is.EqualTo("👍"));
        });
    }

    [TestCase("lgtm")]
    [TestCase("👍👎")]
    [TestCase("")]
    [TestCase("a")]
    public void Reaction_Create_RejectsAnythingThatIsNotOneEmoji(string emoji)
    {
        Assert.Throws<ArgumentException>(() => WikiPageReaction.Create(new CreateWikiPageReactionParams
        {
            PageId = "wkpg_abc", GuildId = "gild_abc", UserId = "user_1", Emoji = emoji,
        }));
    }

    // The whole point of duplicating the rule into Guild.Domain rather than referencing
    // Messaging.Domain is that both sides keep answering the same way, so a client can reuse one
    // emoji picker for messages and wiki pages.
    [TestCase("👍", true)]
    [TestCase("🇨🇭", true)]
    [TestCase("❤️", true)]
    [TestCase("x", false)]
    [TestCase("  ", false)]
    public void EmojiText_MatchesTheMessageReactionRule(string value, bool expected) =>
        Assert.That(EmojiText.IsSingleEmoji(value), Is.EqualTo(expected));
}
