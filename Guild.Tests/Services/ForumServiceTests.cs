using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Covers ForumService: the tag-set replace rules (cap, forum membership, require-tag, moderated
/// gating against the added/removed difference rather than the whole set), the per-page tag and
/// post-count lookups, and keyset cursor round-tripping.
/// </summary>
[TestFixture]
public class ForumServiceTests
{
    private const string GuildId = "guild-1";
    private const string ForumId = "chan-forum";
    private const string PostId = "chan-post";

    private TestGuildContext _context = null!;
    private ForumService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = new ForumService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private ForumTag AddTag(string id, string name, int position = 0, bool moderated = false, string channelId = ForumId)
    {
        var tag = new ForumTag
        {
            Id = id, ChannelId = channelId, GuildId = GuildId, Name = name,
            Color = ForumTag.DefaultColor, Position = position, Moderated = moderated,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.ForumTags.Add(tag);
        return tag;
    }

    private Channel AddPost(string id, bool archived = false)
    {
        var post = Channel.Create(new CreateChannelParams
        {
            Name = "post", Type = ChannelType.Thread, GuildId = GuildId, ParentChannelId = ForumId, Description = "",
        });
        post.Id = id;
        post.IsArchived = archived;
        _context.Channels.Add(post);
        return post;
    }

    // ══════════════════════════════════════════════════════════════════════ SetPostTagsAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetPostTags_AppliesRequestedTags()
    {
        AddTag("ftag-a", "alpha");
        AddTag("ftag-b", "beta");
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a", "ftag-b"], callerIsModerator: false, requireTag: false);
        await _context.SaveChangesAsync();

        Assert.That(result.Succeeded, Is.True);
        var applied = await _context.ForumPostTags.Where(pt => pt.ThreadChannelId == PostId).ToListAsync();
        Assert.That(applied, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SetPostTags_IsReplaceNotDelta()
    {
        AddTag("ftag-a", "alpha");
        AddTag("ftag-b", "beta");
        await _context.SaveChangesAsync();

        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a", "ftag-b"], false, false);
        await _context.SaveChangesAsync();

        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-b"], false, false);
        await _context.SaveChangesAsync();

        var applied = await _context.ForumPostTags.Where(pt => pt.ThreadChannelId == PostId).Select(pt => pt.TagId).ToListAsync();
        Assert.That(applied, Is.EquivalentTo(new[] { "ftag-b" }));
    }

    [Test]
    public async Task SetPostTags_RepeatedCallIsIdempotent()
    {
        AddTag("ftag-a", "alpha");
        await _context.SaveChangesAsync();

        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a"], false, false);
        await _context.SaveChangesAsync();
        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a"], false, false);
        await _context.SaveChangesAsync();

        var applied = await _context.ForumPostTags.Where(pt => pt.ThreadChannelId == PostId).ToListAsync();
        Assert.That(applied, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SetPostTags_DuplicateIdsInRequest_AppliedOnce()
    {
        AddTag("ftag-a", "alpha");
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a", "ftag-a"], false, false);
        await _context.SaveChangesAsync();

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.TagIds, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SetPostTags_ReturnsIdsOrderedByTagPosition()
    {
        AddTag("ftag-c", "charlie", position: 2);
        AddTag("ftag-a", "alpha", position: 0);
        AddTag("ftag-b", "beta", position: 1);
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-c", "ftag-a", "ftag-b"], false, false);

        Assert.That(result.TagIds, Is.EqualTo(new[] { "ftag-a", "ftag-b", "ftag-c" }));
    }

    [Test]
    public async Task SetPostTags_ExceedingCap_ReturnsInvalid()
    {
        for (var i = 0; i <= ForumPostTag.MaxTagsPerPost; i++) AddTag($"ftag-{i}", $"tag{i}", i);
        await _context.SaveChangesAsync();

        var tagIds = Enumerable.Range(0, ForumPostTag.MaxTagsPerPost + 1).Select(i => $"ftag-{i}").ToList();
        var result = await _service.SetPostTagsAsync(PostId, ForumId, tagIds, false, false);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Forbidden, Is.False);
    }

    [Test]
    public async Task SetPostTags_TagFromAnotherForum_ReturnsInvalid()
    {
        AddTag("ftag-other", "other", channelId: "chan-other-forum");
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-other"], false, false);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("ftag-other"));
    }

    [Test]
    public async Task SetPostTags_UnknownTagId_RejectsWholeRequest()
    {
        AddTag("ftag-a", "alpha");
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a", "ftag-nope"], false, false);
        await _context.SaveChangesAsync();

        Assert.That(result.Succeeded, Is.False);
        // The valid half must not have been applied - it's all-or-nothing.
        Assert.That(await _context.ForumPostTags.AnyAsync(pt => pt.ThreadChannelId == PostId), Is.False);
    }

    [Test]
    public async Task SetPostTags_RequireTagWithEmptySet_ReturnsInvalid()
    {
        var result = await _service.SetPostTagsAsync(PostId, ForumId, [], callerIsModerator: false, requireTag: true);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Forbidden, Is.False);
    }

    [Test]
    public async Task SetPostTags_EmptySetWithoutRequireTag_ClearsTags()
    {
        AddTag("ftag-a", "alpha");
        await _context.SaveChangesAsync();
        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-a"], false, false);
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, [], false, requireTag: false);
        await _context.SaveChangesAsync();

        Assert.That(result.Succeeded, Is.True);
        Assert.That(await _context.ForumPostTags.AnyAsync(pt => pt.ThreadChannelId == PostId), Is.False);
    }

    [Test]
    public async Task SetPostTags_NonModeratorApplyingModeratedTag_ReturnsForbidden()
    {
        AddTag("ftag-mod", "confirmed", moderated: true);
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-mod"], callerIsModerator: false, requireTag: false);

        Assert.That(result.Forbidden, Is.True);
    }

    [Test]
    public async Task SetPostTags_ModeratorApplyingModeratedTag_Succeeds()
    {
        AddTag("ftag-mod", "confirmed", moderated: true);
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-mod"], callerIsModerator: true, requireTag: false);

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task SetPostTags_NonModeratorRemovingModeratedTag_ReturnsForbidden()
    {
        AddTag("ftag-mod", "confirmed", moderated: true);
        await _context.SaveChangesAsync();
        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-mod"], callerIsModerator: true, requireTag: false);
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, [], callerIsModerator: false, requireTag: false);

        Assert.That(result.Forbidden, Is.True);
    }

    [Test]
    public async Task SetPostTags_NonModeratorKeepingExistingModeratedTag_Succeeds()
    {
        // The gate is on the difference, not the whole set: an author re-saving their own tags
        // must not trip over a moderated tag someone else already applied.
        AddTag("ftag-mod", "confirmed", moderated: true, position: 0);
        AddTag("ftag-a", "alpha", position: 1);
        await _context.SaveChangesAsync();

        await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-mod"], callerIsModerator: true, requireTag: false);
        await _context.SaveChangesAsync();

        var result = await _service.SetPostTagsAsync(PostId, ForumId, ["ftag-mod", "ftag-a"], callerIsModerator: false, requireTag: false);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.TagIds, Is.EqualTo(new[] { "ftag-mod", "ftag-a" }));
    }

    // ══════════════════════════════════════════════════════════════════════ Lookups
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTagIdsForPosts_GroupsByPostAndOrdersByPosition()
    {
        AddTag("ftag-b", "beta", position: 1);
        AddTag("ftag-a", "alpha", position: 0);
        AddPost("post-1");
        AddPost("post-2");
        await _context.SaveChangesAsync();

        await _service.SetPostTagsAsync("post-1", ForumId, ["ftag-b", "ftag-a"], false, false);
        await _service.SetPostTagsAsync("post-2", ForumId, ["ftag-b"], false, false);
        await _context.SaveChangesAsync();

        var result = await _service.GetTagIdsForPostsAsync(["post-1", "post-2"]);

        Assert.That(result["post-1"], Is.EqualTo(new[] { "ftag-a", "ftag-b" }));
        Assert.That(result["post-2"], Is.EqualTo(new[] { "ftag-b" }));
    }

    [Test]
    public async Task GetTagIdsForPosts_EmptyInput_ReturnsEmptyWithoutQuerying()
    {
        var result = await _service.GetTagIdsForPostsAsync([]);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPostCounts_ExcludesArchivedPosts()
    {
        AddTag("ftag-a", "alpha");
        AddPost("post-live");
        AddPost("post-archived", archived: true);
        await _context.SaveChangesAsync();

        await _service.SetPostTagsAsync("post-live", ForumId, ["ftag-a"], false, false);
        await _service.SetPostTagsAsync("post-archived", ForumId, ["ftag-a"], false, false);
        await _context.SaveChangesAsync();

        var counts = await _service.GetPostCountsAsync(["ftag-a"]);

        Assert.That(counts["ftag-a"], Is.EqualTo(1));
    }

    [Test]
    public async Task GetPostCounts_TagWithNoPosts_IsAbsentRatherThanZero()
    {
        AddTag("ftag-a", "alpha");
        await _context.SaveChangesAsync();

        var counts = await _service.GetPostCountsAsync(["ftag-a"]);

        // Callers use GetValueOrDefault, so an absent key reads as 0 - asserted here so the
        // contract is explicit rather than incidental.
        Assert.That(counts.ContainsKey("ftag-a"), Is.False);
        Assert.That(counts.GetValueOrDefault("ftag-a"), Is.EqualTo(0));
    }

    // ══════════════════════════════════════════════════════════════════════ Config
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConfig_Unconfigured_ReturnsDefaultsWithoutPersisting()
    {
        var config = await _service.GetConfigAsync(ForumId, GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(config.RequireTag, Is.False);
            Assert.That(config.DefaultSortOrder, Is.EqualTo(ForumSortOrder.LatestActivity));
            Assert.That(config.DefaultAutoArchiveMinutes, Is.EqualTo(ForumConfig.DefaultAutoArchiveMinutesFallback));
        });
        Assert.That(await _context.ForumConfigs.AnyAsync(), Is.False, "reads must not insert");
    }

    [Test]
    public async Task GetConfig_Configured_ReturnsStoredRow()
    {
        _context.ForumConfigs.Add(new ForumConfig { ChannelId = ForumId, GuildId = GuildId, RequireTag = true });
        await _context.SaveChangesAsync();

        var config = await _service.GetConfigAsync(ForumId, GuildId);

        Assert.That(config.RequireTag, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ Cursors
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void Cursor_RoundTrips()
    {
        var key = new DateTimeOffset(2026, 7, 30, 12, 34, 56, TimeSpan.Zero);

        var cursor = ForumService.EncodeCursor(isPinned: true, key, "chan-abc");
        var decoded = ForumService.TryDecodeCursor(cursor, out var pinned, out var sortKey, out var id);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(pinned, Is.True);
            Assert.That(sortKey, Is.EqualTo(key));
            Assert.That(id, Is.EqualTo("chan-abc"));
        });
    }

    [Test]
    public void Cursor_IsUrlSafe()
    {
        // Base64url: the raw alphabet's + and / would need escaping in a query string, and a
        // client round-tripping the value verbatim (as the contract tells it to) wouldn't escape.
        var cursor = ForumService.EncodeCursor(false, DateTimeOffset.UtcNow, "chan-" + new string('z', 30));

        Assert.That(cursor, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
    }

    [TestCase("not-base64!!")]
    [TestCase("")]
    [TestCase("YWJj")] // valid base64, wrong field count
    public void Cursor_Malformed_ReturnsFalseRatherThanThrowing(string cursor)
    {
        Assert.That(ForumService.TryDecodeCursor(cursor, out _, out _, out _), Is.False);
    }

    [Test]
    public void SortKey_LatestActivity_FallsBackToCreatedAtWhenNeverPostedIn()
    {
        var post = Channel.Create(new CreateChannelParams { Name = "p", Type = ChannelType.Thread, GuildId = GuildId, Description = "" });
        post.CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        post.LastActivityAt = null;

        Assert.That(ForumService.SortKey(post, ForumSortOrder.LatestActivity), Is.EqualTo(post.CreatedAt));
    }

    [Test]
    public void SortKey_CreationDate_IgnoresLastActivity()
    {
        var post = Channel.Create(new CreateChannelParams { Name = "p", Type = ChannelType.Thread, GuildId = GuildId, Description = "" });
        post.CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        post.LastActivityAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.That(ForumService.SortKey(post, ForumSortOrder.CreationDate), Is.EqualTo(post.CreatedAt));
    }
}
