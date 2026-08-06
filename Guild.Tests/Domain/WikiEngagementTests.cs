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

    // ── Watchers ─────────────────────────────────────────────────────────────

    [Test]
    public void Watcher_Create_SetsAllPropertiesFromParams()
    {
        var watcher = WikiPageWatcher.Create(new CreateWikiPageWatcherParams
        {
            PageId = "wkpg_abc", GuildId = "gild_abc", UserId = "user_1",
        });

        Assert.Multiple(() =>
        {
            Assert.That(watcher.PageId, Is.EqualTo("wkpg_abc"));
            Assert.That(watcher.GuildId, Is.EqualTo("gild_abc"));
            Assert.That(watcher.UserId, Is.EqualTo("user_1"));
            Assert.That(watcher.CreatedAt, Is.Not.EqualTo(default(DateTime)));
        });
    }

    // ── Comments ─────────────────────────────────────────────────────────────

    private static WikiComment MakeComment(string content = "looks good") =>
        WikiComment.Create(new CreateWikiCommentParams
        {
            PageId = "wkpg_abc", GuildId = "gild_abc", AuthorId = "user_1", Content = content,
        });

    [Test]
    public void Comment_Create_GeneratesIdWithCorrectPrefixAndTrimsContent()
    {
        var comment = MakeComment("  looks good  ");

        Assert.Multiple(() =>
        {
            Assert.That(comment.Id, Does.StartWith("wkcm"));
            Assert.That(comment.Content, Is.EqualTo("looks good"));
            Assert.That(comment.EditedAt, Is.Null);
        });
    }

    [Test]
    public void Comment_Create_RaisesCreated()
    {
        var comment = MakeComment();

        var evt = comment.GetDomainEvents().OfType<WikiCommentCreated>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(evt.CommentId, Is.EqualTo(comment.Id));
            Assert.That(evt.PageId, Is.EqualTo("wkpg_abc"));
            Assert.That(evt.AuthorId, Is.EqualTo("user_1"));
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Comment_Create_RejectsBlankContent(string content) =>
        Assert.Throws<ArgumentException>(() => MakeComment(content));

    // EditedAt is what the client's "(edited)" marker reads.
    [Test]
    public void Comment_Edit_SetsEditedAt()
    {
        var comment = MakeComment();

        comment.Edit("  fixed  ");

        Assert.Multiple(() =>
        {
            Assert.That(comment.Content, Is.EqualTo("fixed"));
            Assert.That(comment.EditedAt, Is.Not.Null);
        });
    }

    [Test]
    public void Comment_Edit_RejectsBlankContent()
    {
        var comment = MakeComment();
        Assert.Throws<ArgumentException>(() => comment.Edit("   "));
    }

    [Test]
    public void Comment_RaiseUpdatedAndDeleted_CarryPageAndGuild()
    {
        var comment = MakeComment();
        comment.RaiseUpdated();
        comment.RaiseDeleted();

        var updated = comment.GetDomainEvents().OfType<WikiCommentUpdated>().Single();
        var deleted = comment.GetDomainEvents().OfType<WikiCommentDeleted>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(updated.PageId, Is.EqualTo("wkpg_abc"));
            Assert.That(updated.GuildId, Is.EqualTo("gild_abc"));
            Assert.That(deleted.CommentId, Is.EqualTo(comment.Id));
        });
    }
}
