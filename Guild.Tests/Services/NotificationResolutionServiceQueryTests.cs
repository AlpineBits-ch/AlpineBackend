using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// Covers ResolveForChannelAsync - the database-shaped half of NotificationResolutionService.
/// NotificationResolutionServiceTests exercises the pure precedence function; this covers what that
/// one cannot: that the three inputs are actually assembled from the right rows, in particular the
/// guild default reached through the channel's Guild navigation. A precedence bug shows up there, a
/// wrong or missing query shows up only here.
/// </summary>
[TestFixture]
public class NotificationResolutionServiceQueryTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string CategoryId = "cate-1";
    private const string MemberId = "memb-1";

    private TestGuildContext _context = null!;
    private NotificationResolutionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = new NotificationResolutionService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync(
        NotificationLevel guildDefault = NotificationLevel.AllMessages,
        bool withCategory = false)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "Test Guild", OwnerId = "user-owner",
            DefaultMessageNotifications = guildDefault,
            CreatedAt = now, UpdatedAt = now,
        });

        if (withCategory)
        {
            _context.Categories.Add(new Category
            {
                Id = CategoryId, GuildId = GuildId, Name = "general",
                CreatedAt = now, UpdatedAt = now,
            });
        }

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "chat", Description = "d",
            Type = ChannelType.Text, CategoryId = withCategory ? CategoryId : null,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = "user-1", JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    private static GuildNotificationSetting Setting(NotificationLevel level) => new()
    {
        Id = "gnot-1", MemberId = MemberId, Level = level,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ══════════════════════════════════════════════════════════════════════
    // The guild default reaches the resolver
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ResolveForChannel_NoMemberRows_UsesTheGuildDefault()
    {
        await SeedAsync(guildDefault: NotificationLevel.OnlyMentions);

        var resolved = await _service.ResolveForChannelAsync(ChannelId, [MemberId]);

        Assert.That(resolved[MemberId].Level, Is.EqualTo(NotificationLevel.OnlyMentions));
    }

    [Test]
    public async Task ResolveForChannel_GuildDefaultAllMessages_MatchesHistoricalBehaviour()
    {
        await SeedAsync(guildDefault: NotificationLevel.AllMessages);

        var resolved = await _service.ResolveForChannelAsync(ChannelId, [MemberId]);

        Assert.That(resolved[MemberId].Level, Is.EqualTo(NotificationLevel.AllMessages));
    }

    [Test]
    public async Task ResolveForChannel_MemberSetting_StillBeatsTheGuildDefault()
    {
        await SeedAsync(guildDefault: NotificationLevel.OnlyMentions);
        _context.GuildNotificationSettings.Add(Setting(NotificationLevel.AllMessages));
        await _context.SaveChangesAsync();

        var resolved = await _service.ResolveForChannelAsync(ChannelId, [MemberId]);

        Assert.That(resolved[MemberId].Level, Is.EqualTo(NotificationLevel.AllMessages));
    }

    [Test]
    public async Task ResolveForChannel_ChannelOverride_StillBeatsEverything()
    {
        await SeedAsync(guildDefault: NotificationLevel.Nothing);
        _context.GuildNotificationSettings.Add(Setting(NotificationLevel.OnlyMentions));
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = MemberId, ChannelId = ChannelId,
            Level = NotificationLevel.AllMessages,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var resolved = await _service.ResolveForChannelAsync(ChannelId, [MemberId]);

        Assert.That(resolved[MemberId].Level, Is.EqualTo(NotificationLevel.AllMessages));
    }

    /// <summary>Category overrides are found via the channel's CategoryId, which is read by the
    /// same projection that now also reads the guild default. Bundling the two lookups must not
    /// have cost the category one.</summary>
    [Test]
    public async Task ResolveForChannel_CategoryOverride_IsStillFound()
    {
        await SeedAsync(guildDefault: NotificationLevel.AllMessages, withCategory: true);
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = MemberId, CategoryId = CategoryId,
            Level = NotificationLevel.OnlyMentions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var resolved = await _service.ResolveForChannelAsync(ChannelId, [MemberId]);

        Assert.That(resolved[MemberId].Level, Is.EqualTo(NotificationLevel.OnlyMentions));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Failing gracefully
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>A channel deleted between the message landing and this resolving. Returning the
    /// safe default beats throwing: the caller is the message-created path, and killing it would
    /// cost the realtime broadcast and the push for a message that was legitimately sent.</summary>
    [Test]
    public async Task ResolveForChannel_UnknownChannel_FallsBackToTheDefaultWithoutThrowing()
    {
        await SeedAsync();

        var resolved = await _service.ResolveForChannelAsync("chan-does-not-exist", [MemberId]);

        Assert.That(resolved[MemberId].Level, Is.EqualTo(NotificationResolutionService.Default.Level));
    }

    [Test]
    public async Task ResolveForChannel_NoMembers_ReturnsEmptyWithoutQuerying()
    {
        await SeedAsync();

        var resolved = await _service.ResolveForChannelAsync(ChannelId, []);

        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public async Task ResolveForChannel_MemberWithNoRowsAtAll_IsStillPresentInTheResult()
    {
        await SeedAsync(guildDefault: NotificationLevel.OnlyMentions);

        var resolved = await _service.ResolveForChannelAsync(ChannelId, [MemberId, "memb-unknown"]);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.ContainsKey("memb-unknown"), Is.True,
                "callers index this dictionary by member id - a missing key is a KeyNotFoundException on the message hot path");
            Assert.That(resolved["memb-unknown"].Level, Is.EqualTo(NotificationLevel.OnlyMentions));
        });
    }
}
