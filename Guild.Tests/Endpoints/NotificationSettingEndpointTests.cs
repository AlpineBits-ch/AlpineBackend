using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers NotificationSettingEndpoint. Every route acts on the caller's own settings, so there is
/// no permission dimension to test - what matters is the upsert semantics (create-on-first-write,
/// partial update, delete-when-empty) and that a member only ever reaches their own rows.
/// </summary>
[TestFixture]
public class NotificationSettingEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";
    private const string ChannelId = "chan-1";
    private const string CategoryId = "cat-1";

    private TestGuildContext _context = null!;
    private NotificationSettingEndpoint _endpoint = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _endpoint = new NotificationSettingEndpoint();

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Categories.Add(new Category
        {
            Id = CategoryId, GuildId = GuildId, Name = "General",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, CategoryId = CategoryId, Name = "chat", Description = "",
            Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1",
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════
    // Guild level
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Get_NoRowYet_ReturnsTheEffectiveDefaults()
    {
        var result = await _endpoint.GetAsync(GuildId, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<GuildNotificationSettingDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.Level, Is.EqualTo(NotificationLevel.AllMessages));
            Assert.That(ok.Value.MutedUntil, Is.Null);
            Assert.That(ok.Value.MobilePush, Is.True);
        });
    }

    [Test]
    public async Task Get_NotAMember_ReturnsNotFound()
    {
        var result = await _endpoint.GetAsync(GuildId, _context, TestPrincipal.Create("stranger"));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Update_FirstWrite_CreatesTheRow()
    {
        await _endpoint.UpdateAsync(GuildId, new UpdateGuildNotificationSettingDto { Level = NotificationLevel.OnlyMentions },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.GuildNotificationSettings.AsNoTracking().SingleAsync();
        Assert.That(stored.Level, Is.EqualTo(NotificationLevel.OnlyMentions));
    }

    [Test]
    public async Task Update_IsPartial_OmittedFieldsSurvive()
    {
        await _endpoint.UpdateAsync(GuildId,
            new UpdateGuildNotificationSettingDto { Level = NotificationLevel.Nothing, SuppressEveryone = true },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        // Second call touches only MobilePush.
        await _endpoint.UpdateAsync(GuildId, new UpdateGuildNotificationSettingDto { MobilePush = false },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.GuildNotificationSettings.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stored.Level, Is.EqualTo(NotificationLevel.Nothing), "the earlier level must not be reset");
            Assert.That(stored.SuppressEveryone, Is.True);
            Assert.That(stored.MobilePush, Is.False);
        });
    }

    [Test]
    public async Task Update_MuteMinutes_SetsAFutureExpiry()
    {
        await _endpoint.UpdateAsync(GuildId, new UpdateGuildNotificationSettingDto { MuteMinutes = 60 },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.GuildNotificationSettings.AsNoTracking().SingleAsync();
        Assert.That(stored.MutedUntil, Is.Not.Null);
        Assert.That(stored.IsMuted(DateTimeOffset.UtcNow), Is.True);
    }

    [Test]
    public async Task Update_MuteMinutesZero_Unmutes()
    {
        await _endpoint.UpdateAsync(GuildId, new UpdateGuildNotificationSettingDto { MuteForever = true },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        await _endpoint.UpdateAsync(GuildId, new UpdateGuildNotificationSettingDto { MuteMinutes = 0 },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.GuildNotificationSettings.AsNoTracking().SingleAsync();
        Assert.That(stored.MutedUntil, Is.Null);
    }

    [Test]
    public async Task Update_MuteForever_OutranksMuteMinutes()
    {
        await _endpoint.UpdateAsync(GuildId,
            new UpdateGuildNotificationSettingDto { MuteMinutes = 5, MuteForever = true },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.GuildNotificationSettings.AsNoTracking().SingleAsync();
        Assert.That(stored.MutedUntil!.Value.Year, Is.EqualTo(9999));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Channel / category overrides
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpsertChannelOverride_CreatesRow()
    {
        await _endpoint.UpsertChannelOverrideAsync(ChannelId,
            new UpdateNotificationOverrideDto { Level = NotificationLevel.Nothing },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.NotificationOverrides.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stored.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(stored.CategoryId, Is.Null);
            Assert.That(stored.Level, Is.EqualTo(NotificationLevel.Nothing));
        });
    }

    [Test]
    public async Task UpsertChannelOverride_EmptyPayload_StoresNothing()
    {
        var result = await _endpoint.UpsertChannelOverrideAsync(ChannelId,
            new UpdateNotificationOverrideDto(), _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.NotificationOverrides.CountAsync(), Is.Zero,
            "an override that expresses no preference is the same as having none");
    }

    [Test]
    public async Task UpsertChannelOverride_ClearingAnExistingRow_DeletesIt()
    {
        await _endpoint.UpsertChannelOverrideAsync(ChannelId,
            new UpdateNotificationOverrideDto { Level = NotificationLevel.Nothing },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        await _endpoint.UpsertChannelOverrideAsync(ChannelId,
            new UpdateNotificationOverrideDto { Level = null, MuteMinutes = 0 },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(await _context.NotificationOverrides.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task UpsertCategoryOverride_CreatesCategoryScopedRow()
    {
        await _endpoint.UpsertCategoryOverrideAsync(CategoryId,
            new UpdateNotificationOverrideDto { MuteForever = true },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.NotificationOverrides.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stored.CategoryId, Is.EqualTo(CategoryId));
            Assert.That(stored.ChannelId, Is.Null);
        });
    }

    [Test]
    public async Task DeleteChannelOverride_WhenNoneExists_IsIdempotent()
    {
        var result = await _endpoint.DeleteChannelOverrideAsync(ChannelId, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NoContent>());
    }

    [Test]
    public async Task DeleteChannelOverride_OnlyRemovesTheCallersOwnRow()
    {
        // A second member with an override on the same channel.
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-2", GuildId = GuildId, UserId = "user-2", JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-2",
        });
        var theirs = NotificationOverride.ForChannel("member-2", ChannelId);
        theirs.Level = NotificationLevel.Nothing;
        _context.NotificationOverrides.Add(theirs);
        await _context.SaveChangesAsync();

        await _endpoint.UpsertChannelOverrideAsync(ChannelId,
            new UpdateNotificationOverrideDto { Level = NotificationLevel.Nothing },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        await _endpoint.DeleteChannelOverrideAsync(ChannelId, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var remaining = await _context.NotificationOverrides.AsNoTracking().ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].MemberId, Is.EqualTo("member-2"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Bulk hydration
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetAllForUser_ReturnsOneEntryPerGuildIncludingUnconfiguredOnes()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = "guild-2", OwnerId = OwnerId, Name = "g2",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-g2", GuildId = "guild-2", UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1",
        });
        await _context.SaveChangesAsync();

        await _endpoint.UpdateAsync(GuildId, new UpdateGuildNotificationSettingDto { Level = NotificationLevel.Nothing },
            _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetAllForUserAsync(_context, TestPrincipal.Create(UserId));
        var ok = result as Ok<List<GuildNotificationSettingDto>>;

        Assert.That(ok!.Value, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.Single(g => g.GuildId == GuildId).Level, Is.EqualTo(NotificationLevel.Nothing));
            Assert.That(ok.Value.Single(g => g.GuildId == "guild-2").Level, Is.EqualTo(NotificationLevel.AllMessages),
                "a guild the member never configured still reports its effective defaults");
        });
    }
}
