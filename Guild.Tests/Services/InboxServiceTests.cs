using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>Covers <see cref="InboxService"/> - the Unread tab.</summary>
[TestFixture]
public class InboxServiceTests
{
    private const string UserId = "user-1";
    private const string OwnerId = "user-owner";
    private const string GuildId = "gild-1";
    private const string MemberId = "memb-1";
    private const string EveryoneRoleId = "role-everyone";

    private static readonly DateTimeOffset JoinedAt = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;
    private InboxService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();
        _bus.SetResponse<GetChannelMessagePagesRequest>(new GetChannelMessagePagesResponse { Pages = [] });

        var permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _service = new InboxService(
            _context,
            new NotificationResolutionService(_context),
            permissions,
            _bus,
            NullLogger<InboxService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task SeedGuildAsync()
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "Test Guild", OwnerId = OwnerId, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = JoinedAt.UtcDateTime,
            SearchValue = "USER-1", CreatedAt = Now, UpdatedAt = Now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rome-1", RoleId = EveryoneRoleId, MemberId = MemberId, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<Channel> AddChannelAsync(
        string id,
        DateTimeOffset? lastActivityAt,
        int messageCount = 5,
        ChannelType type = ChannelType.Text,
        string? categoryId = null,
        string? lastMessageId = "mesg-head")
    {
        var channel = new Channel
        {
            Id = id, GuildId = GuildId, Name = id, Description = "d", Type = type,
            CategoryId = categoryId,
            LastActivityAt = lastActivityAt,
            LastMessageId = lastActivityAt is null ? null : lastMessageId,
            MessageCount = messageCount,
            CreatedAt = Now, UpdatedAt = Now,
        };
        _context.Channels.Add(channel);
        await _context.SaveChangesAsync();
        return channel;
    }

    private async Task AddReadStateAsync(string channelId, DateTimeOffset? lastReadAt, int countAtRead = 0)
    {
        _context.ReadStates.Add(new ReadState
        {
            Id = $"reta-{channelId}", ChannelId = channelId, MemberId = MemberId,
            LastReadMessageId = "mesg-read", LastReadAt = lastReadAt,
            MessageCountAtRead = countAtRead,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();
    }

    private Task<Guild.Application.Dtos.Response.InboxUnreadPageDto> GetAsync(int limit = 10, string? cursor = null) =>
        _service.GetUnreadAsync(UserId, limit, cursor);

    private async Task<List<string>> UnreadChannelIdsAsync(int limit = 10) =>
        (await GetAsync(limit)).Groups.Select(g => g.Breadcrumb.ChannelId).ToList();

    // ══════════════════════════════════════════════════════════════════════ The predicate
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ChannelWithActivityAfterTheReadCursor_Appears()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(1));

        Assert.That(await UnreadChannelIdsAsync(), Is.EqualTo(new[] { "chan-1" }).AsCollection);
    }

    [Test]
    public async Task ChannelFullyRead_DoesNotAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(5));

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    /// <summary>Most of what belongs in an inbox is channels nobody has opened, and those have no
    /// read state at all - an inner join would silently drop exactly them.</summary>
    [Test]
    public async Task NeverOpenedChannelWithActivityAfterJoining_Appears()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(2));

        Assert.That(await UnreadChannelIdsAsync(), Is.EqualTo(new[] { "chan-1" }).AsCollection);
    }

    /// <summary>The mirror image: joining a guild must not surface its entire history as unread.</summary>
    [Test]
    public async Task NeverOpenedChannelWhoseLastActivityPredatesJoining_DoesNotAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(-3));

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    [Test]
    public async Task ChannelWithNoMessagesAtAll_DoesNotAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", lastActivityAt: null);

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    [Test]
    public async Task GroupsAreOrderedByMostRecentActivityFirst()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-old", JoinedAt.AddHours(1));
        await AddChannelAsync("chan-new", JoinedAt.AddHours(9));
        await AddChannelAsync("chan-mid", JoinedAt.AddHours(4));

        Assert.That(await UnreadChannelIdsAsync(), Is.EqualTo(new[] { "chan-new", "chan-mid", "chan-old" }).AsCollection);
    }

    // ══════════════════════════════════════════════════════════════════════ Counts
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UnreadCount_IsTheDifferenceBetweenTheChannelCountAndTheSnapshot()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), messageCount: 12);
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(1), countAtRead: 4);

        var group = (await GetAsync()).Groups.Single();
        Assert.Multiple(() =>
        {
            Assert.That(group.UnreadCount, Is.EqualTo(8));
            Assert.That(group.MentionCount, Is.Zero, "no mentions were indexed and no broadcasts were sent");
        });
    }

    /// <summary>Both counters are best-effort tallies over bus events, so under message loss the
    /// snapshot can exceed the live count. Clamping beats rendering "-3 unread".</summary>
    [Test]
    public async Task UnreadCount_IsClampedAtZeroWhenTheCountersHaveDrifted()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), messageCount: 2);
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(1), countAtRead: 40);

        Assert.That((await GetAsync()).Groups.Single().UnreadCount, Is.Zero);
    }

    [Test]
    public async Task NeverOpenedChannel_CountsEveryMessageAsUnread()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(2), messageCount: 7);

        Assert.That((await GetAsync()).Groups.Single().UnreadCount, Is.EqualTo(7));
    }

    // ══════════════════════════════════════════════════════════════════════ What must not appear
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The onboarding card promises "unread messages from all your unmuted channels".</summary>
    [Test]
    public async Task MutedChannel_DoesNotAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = MemberId, ChannelId = "chan-1",
            MutedUntil = Now.AddHours(1), CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    [Test]
    public async Task MutedGuild_HidesItsChannels()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = MemberId, MutedUntil = Now.AddHours(1),
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    [Test]
    public async Task ExpiredMute_DoesNotHideTheChannel()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = MemberId, ChannelId = "chan-1",
            MutedUntil = Now.AddHours(-1), CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.That(await UnreadChannelIdsAsync(), Is.EqualTo(new[] { "chan-1" }).AsCollection);
    }

    [Test]
    public async Task ChannelSetToNotifyNothing_DoesNotAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = MemberId, ChannelId = "chan-1",
            Level = NotificationLevel.Nothing, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    /// <summary>A read state outlives the access that produced it.</summary>
    [Test]
    public async Task ChannelTheCallerCanNoLongerSee_DoesNotAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(1));

        // SendMessages goes with it: the permission expansion re-grants ViewChannel from anything
        // that implies SendMessages.
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-1", ChannelId = "chan-1", MemberId = MemberId,
            AllowPermissions = Permissions.None,
            DenyPermissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty);
    }

    [TestCase(ChannelType.List)]
    [TestCase(ChannelType.Chores)]
    [TestCase(ChannelType.Ledger)]
    [TestCase(ChannelType.Pantry)]
    [TestCase(ChannelType.Decisions)]
    public async Task HouseholdModuleChannels_NeverAppear(ChannelType type)
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), type: type);

        Assert.That(await UnreadChannelIdsAsync(), Is.Empty, "household modules keep no message history, so unread is meaningless for them");
    }

    [Test]
    public async Task ThreadsAndForumPosts_DoAppear()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), type: ChannelType.Thread);

        Assert.That(await UnreadChannelIdsAsync(), Is.EqualTo(new[] { "chan-1" }).AsCollection);
    }

    // ══════════════════════════════════════════════════════════════════════ Previews
    // ══════════════════════════════════════════════════════════════════════

    private void RespondWithPreviews(string channelId, int count)
    {
        _bus.SetResponse<GetChannelMessagePagesRequest>(new GetChannelMessagePagesResponse
        {
            Pages =
            [
                new ChannelMessagePage
                {
                    ChannelId = channelId,
                    Messages = Enumerable.Range(0, count).Select(i => new InboxMessagePreview
                    {
                        Id = $"mesg-{i}",
                        CreatedAt = JoinedAt.AddHours(2).AddMinutes(i),
                        AuthorId = "user-2",
                        Content = "hello"u8.ToArray(),
                    }).ToList(),
                },
            ],
        });
    }

    [Test]
    public async Task Previews_AreAttachedToTheirGroup()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), messageCount: 3);
        RespondWithPreviews("chan-1", 3);

        var group = (await GetAsync()).Groups.Single();
        Assert.Multiple(() =>
        {
            Assert.That(group.Previews, Has.Count.EqualTo(3));
            Assert.That(group.PreviewsTruncated, Is.False);
        });
    }

    [Test]
    public async Task Previews_AreAnchoredToTheCallersReadCursor()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(1));

        await GetAsync();

        var request = _bus.Invoked.OfType<GetChannelMessagePagesRequest>().Single();
        Assert.That(request.Items.Single().AfterMessageId, Is.EqualTo("mesg-read"));
    }

    [Test]
    public async Task NeverOpenedChannel_AsksForPreviewsWithNoAnchor()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));

        await GetAsync();

        var request = _bus.Invoked.OfType<GetChannelMessagePagesRequest>().Single();
        Assert.That(request.Items.Single().AfterMessageId, Is.Null);
    }

    [Test]
    public async Task MoreUnreadThanPreviews_SetsTheTruncatedFlag()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), messageCount: 12);
        RespondWithPreviews("chan-1", InboxService.MaxPreviewMessages);

        var group = (await GetAsync()).Groups.Single();
        Assert.Multiple(() =>
        {
            Assert.That(group.Previews, Has.Count.EqualTo(InboxService.MaxPreviewMessages));
            Assert.That(group.PreviewsTruncated, Is.True);
        });
    }

    /// <summary>The unread state is Guild's own data and is still correct without message bodies.
    /// Returning 500 because a preview fetch failed would take out a working feature over a cosmetic
    /// one.</summary>
    [Test]
    public async Task MessagingUnreachable_StillReturnsTheGroupsWithAFlag()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        _bus.ClearResponses();

        var page = await GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(page.Groups, Has.Count.EqualTo(1));
            Assert.That(page.Groups[0].Previews, Is.Empty);
            Assert.That(page.PreviewsUnavailable, Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Paging
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PagingWalksEveryChannelExactlyOnce()
    {
        await SeedGuildAsync();
        for (var i = 0; i < 7; i++) await AddChannelAsync($"chan-{i}", JoinedAt.AddHours(i + 1));

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await GetAsync(limit: 3, cursor: cursor);
            seen.AddRange(page.Groups.Select(g => g.Breadcrumb.ChannelId));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Has.Count.EqualTo(7));
            Assert.That(seen.Distinct().Count(), Is.EqualTo(7));
        });
    }

    [Test]
    public async Task LastPage_ReturnsNoCursor()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(1));

        Assert.That((await GetAsync(limit: 10)).NextCursor, Is.Null);
    }

    /// <summary>A cursor the server did not mint means a stale bookmark, not an attack.</summary>
    [Test]
    public async Task MalformedCursor_IsTreatedAsTheFirstPage()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(1));

        var page = await GetAsync(cursor: "not-base64-at-all!!");

        Assert.That(page.Groups, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LimitIsClampedToTheMaximum()
    {
        await SeedGuildAsync();
        for (var i = 0; i < InboxService.MaxPageSize + 5; i++) await AddChannelAsync($"chan-{i}", JoinedAt.AddHours(i + 1));

        Assert.That((await GetAsync(limit: 1000)).Groups, Has.Count.EqualTo(InboxService.MaxPageSize));
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task NonPositiveLimit_IsClampedToOne(int limit)
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(1));
        await AddChannelAsync("chan-2", JoinedAt.AddHours(2));

        Assert.That((await GetAsync(limit: limit)).Groups, Has.Count.EqualTo(1));
    }

    /// <summary>A page where everything was muted must still hand back a cursor, or paging stops and
    /// hides every unread channel behind it.</summary>
    [Test]
    public async Task PageWhereEverythingWasFiltered_StillAdvancesTheCursor()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-a", JoinedAt.AddHours(3));
        await AddChannelAsync("chan-b", JoinedAt.AddHours(2));
        _context.NotificationOverrides.Add(new NotificationOverride
        {
            Id = "nover-1", MemberId = MemberId, ChannelId = "chan-a",
            MutedUntil = Now.AddHours(1), CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        var first = await GetAsync(limit: 1);
        Assert.That(first.Groups, Is.Empty, "the only row on this page was muted");
        Assert.That(first.NextCursor, Is.Not.Null);

        var second = await GetAsync(limit: 1, cursor: first.NextCursor);
        Assert.That(second.Groups.Select(g => g.Breadcrumb.ChannelId), Is.EqualTo(new[] { "chan-b" }).AsCollection);
    }

    // ══════════════════════════════════════════════════════════════════════ Breadcrumb
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Breadcrumb_CarriesGuildCategoryAndChannel()
    {
        await SeedGuildAsync();
        _context.Categories.Add(new Category { Id = "cate-1", GuildId = GuildId, Name = "General", CreatedAt = Now, UpdatedAt = Now });
        await _context.SaveChangesAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5), categoryId: "cate-1");

        var crumb = (await GetAsync()).Groups.Single().Breadcrumb;
        Assert.Multiple(() =>
        {
            Assert.That(crumb.GuildName, Is.EqualTo("Test Guild"));
            Assert.That(crumb.CategoryName, Is.EqualTo("General"));
            Assert.That(crumb.ChannelName, Is.EqualTo("chan-1"));
            Assert.That(crumb.GuildIconUrl, Is.EqualTo($"/api/v1/guild/guilds/{GuildId}/icon"));
            Assert.That(crumb.GuildIconThumbnailUrl, Is.EqualTo($"/api/v1/guild/guilds/{GuildId}/icon/thumbnail"));
        });
    }

    [Test]
    public async Task Breadcrumb_HasNoCategoryForAnUncategorisedChannel()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));

        var crumb = (await GetAsync()).Groups.Single().Breadcrumb;
        Assert.Multiple(() =>
        {
            Assert.That(crumb.CategoryId, Is.Null);
            Assert.That(crumb.CategoryName, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Failing gracefully
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UserInNoGuilds_ReturnsAnEmptyPage()
    {
        var page = await GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(page.Groups, Is.Empty);
            Assert.That(page.NextCursor, Is.Null);
            Assert.That(page.PreviewsUnavailable, Is.False, "there was nothing to fetch, which is not the same as a failure");
        });
    }

    [Test]
    public async Task NothingUnread_DoesNotAskMessagingForAnything()
    {
        await SeedGuildAsync();
        await AddChannelAsync("chan-1", JoinedAt.AddHours(5));
        await AddReadStateAsync("chan-1", JoinedAt.AddHours(5));

        await GetAsync();

        Assert.That(_bus.Invoked, Is.Empty);
    }
}
