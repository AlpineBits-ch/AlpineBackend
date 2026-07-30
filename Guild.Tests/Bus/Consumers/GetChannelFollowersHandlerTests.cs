using Guild.Application.Bus.Consumers;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Covers GetChannelFollowersHandler - the bus-side counterpart to ChannelFollowEndpoint used by
/// Messaging when a message is published in a channel, to know whether it must be cross-posted to
/// any following channels. Only Announcement-type channels can have followers.
/// </summary>
[TestFixture]
public class GetChannelFollowersHandlerTests
{
    private TestGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestGuildContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Channel MakeChannel(string id, ChannelType type) => new()
    {
        Id = id, GuildId = "guild-1", Name = "chan", Description = "d", Type = type,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Handle_ChannelDoesNotExist_ReturnsNotAnnouncementChannel()
    {
        var response = await GetChannelFollowersHandler.Handle(new GetChannelFollowersRequest { ChannelId = "nonexistent" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.IsAnnouncementChannel, Is.False);
            Assert.That(response.TargetChannelIds, Is.Null.Or.Empty);
        });
    }

    [Test]
    public async Task Handle_ChannelIsNotAnnouncementType_ReturnsFalse()
    {
        _context.Channels.Add(MakeChannel("chan-1", ChannelType.Text));
        await _context.SaveChangesAsync();

        var response = await GetChannelFollowersHandler.Handle(new GetChannelFollowersRequest { ChannelId = "chan-1" }, _context);

        Assert.That(response.IsAnnouncementChannel, Is.False);
    }

    [Test]
    public async Task Handle_AnnouncementChannelWithNoFollowers_ReturnsTrueWithEmptyList()
    {
        _context.Channels.Add(MakeChannel("chan-1", ChannelType.Announcement));
        await _context.SaveChangesAsync();

        var response = await GetChannelFollowersHandler.Handle(new GetChannelFollowersRequest { ChannelId = "chan-1" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.IsAnnouncementChannel, Is.True);
            Assert.That(response.TargetChannelIds, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_AnnouncementChannelWithFollowers_ReturnsTargetChannelIds()
    {
        _context.Channels.Add(MakeChannel("chan-1", ChannelType.Announcement));
        _context.Set<GuildChannelFollow>().AddRange(
            GuildChannelFollow.Create(new CreateGuildChannelFollowParams
            {
                SourceChannelId = "chan-1", SourceGuildId = "guild-1",
                TargetChannelId = "target-a", TargetGuildId = "guild-2", CreatedByUserId = "user-1",
            }),
            GuildChannelFollow.Create(new CreateGuildChannelFollowParams
            {
                SourceChannelId = "chan-1", SourceGuildId = "guild-1",
                TargetChannelId = "target-b", TargetGuildId = "guild-3", CreatedByUserId = "user-1",
            }));
        await _context.SaveChangesAsync();

        var response = await GetChannelFollowersHandler.Handle(new GetChannelFollowersRequest { ChannelId = "chan-1" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.IsAnnouncementChannel, Is.True);
            Assert.That(response.TargetChannelIds, Is.EquivalentTo(new[] { "target-a", "target-b" }));
        });
    }

    [Test]
    public async Task Handle_DoesNotReturnFollowersOfADifferentSourceChannel()
    {
        _context.Channels.Add(MakeChannel("chan-1", ChannelType.Announcement));
        _context.Channels.Add(MakeChannel("chan-2", ChannelType.Announcement));
        _context.Set<GuildChannelFollow>().Add(GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = "chan-2", SourceGuildId = "guild-1",
            TargetChannelId = "target-a", TargetGuildId = "guild-2", CreatedByUserId = "user-1",
        }));
        await _context.SaveChangesAsync();

        var response = await GetChannelFollowersHandler.Handle(new GetChannelFollowersRequest { ChannelId = "chan-1" }, _context);

        Assert.That(response.TargetChannelIds, Is.Empty);
    }
}
