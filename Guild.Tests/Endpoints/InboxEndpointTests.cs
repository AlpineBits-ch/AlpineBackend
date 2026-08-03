using Guild.Application.Endpoints;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers the two write endpoints on <see cref="InboxEndpoint"/>: per-channel mark-read (the REST
/// twin of the guild.UpdateLastRead hub method, so the check button works without a live socket)
/// and read-all (the header's clear-everything button).
/// </summary>
[TestFixtureSource(typeof(GuildContextProviders))]
public class InboxEndpointTests(IGuildContextProvider provider)
{
    private const string UserId = "user-1";
    private const string GuildId = "gild-1";
    private const string MemberId = "memb-1";

    private static readonly DateTimeOffset JoinedAt = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HeadAt = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _context = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private InboxEndpoint _endpoint = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = await provider.CreateAsync();
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _endpoint = new InboxEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task SeedAsync(int channelCount = 1, bool withMessages = true)
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = "user-owner", CreatedAt = Now, UpdatedAt = Now,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = JoinedAt.UtcDateTime,
            SearchValue = "USER-1", CreatedAt = Now, UpdatedAt = Now,
        });

        for (var i = 0; i < channelCount; i++)
        {
            _context.Channels.Add(new Channel
            {
                Id = $"chan-{i}", GuildId = GuildId, Name = $"chan-{i}", Description = "d",
                Type = ChannelType.Text,
                LastActivityAt = withMessages ? HeadAt : null,
                LastMessageId = withMessages ? $"mesg-{i}" : null,
                MessageCount = withMessages ? 5 : 0,
                CreatedAt = Now, UpdatedAt = Now,
            });
        }

        await _context.SaveChangesAsync();
    }

    private ReadState? Stored(string channelId) =>
        _context.ReadStates.AsNoTracking().FirstOrDefault(r => r.ChannelId == channelId && r.MemberId == MemberId);

    // ══════════════════════════════════════════════════════════════════════ Mark one channel read
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MarkChannelRead_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedAsync();

        var result = await _endpoint.MarkChannelRead("chan-0", TestPrincipal.CreateAnonymous(), _context, _bus, _hub);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task MarkChannelRead_UnknownChannel_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _endpoint.MarkChannelRead("chan-nope", TestPrincipal.Create(UserId), _context, _bus, _hub);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Channel ids are guessable, so membership has to be checked rather than assumed from
    /// the channel existing.</summary>
    [Test]
    public async Task MarkChannelRead_NonMember_ReturnsForbid()
    {
        await SeedAsync();

        var result = await _endpoint.MarkChannelRead("chan-0", TestPrincipal.Create("user-stranger"), _context, _bus, _hub);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task MarkChannelRead_SendsTheAckCommandForTheChannelHead()
    {
        await SeedAsync();

        var result = await _endpoint.MarkChannelRead("chan-0", TestPrincipal.Create(UserId), _context, _bus, _hub);

        var command = _bus.Invoked.OfType<Echo.Realtime.UpdateGuildReadCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(command.UserId, Is.EqualTo(UserId));
            Assert.That(command.ChannelId, Is.EqualTo("chan-0"));
            Assert.That(command.Id, Is.EqualTo("mesg-0"));
        });
    }

    /// <summary>A client clearing a whole guild does not know which of its channels are empty, so an
    /// empty one is a no-op rather than an error.</summary>
    [Test]
    public async Task MarkChannelRead_ChannelWithNoMessages_IsANoOp()
    {
        await SeedAsync(withMessages: false);

        var result = await _endpoint.MarkChannelRead("chan-0", TestPrincipal.Create(UserId), _context, _bus, _hub);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task MarkChannelRead_NotifiesTheCallersOtherDevices()
    {
        await SeedAsync();

        await _endpoint.MarkChannelRead("chan-0", TestPrincipal.Create(UserId), _context, _bus, _hub);

        var sent = ((FakeHubClients)_hub.Clients).SentMessages;
        Assert.That(sent.Any(m => m.Method == "inbox.ReadStateChanged"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ Read all
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MarkAllRead_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedAsync();

        var result = await _endpoint.MarkAllRead(TestPrincipal.CreateAnonymous(), _context, _hub);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task MarkAllRead_CreatesReadStatesForChannelsNeverOpened()
    {
        await SeedAsync(channelCount: 3);

        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);

        var states = _context.ReadStates.AsNoTracking().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(states, Has.Count.EqualTo(3));
            Assert.That(states.All(s => s.LastReadAt == HeadAt), Is.True);
            Assert.That(states.All(s => s.MessageCountAtRead == 5), Is.True);
        });
    }

    [Test]
    public async Task MarkAllRead_UpdatesExistingReadStatesInPlace()
    {
        await SeedAsync(channelCount: 2);
        _context.ReadStates.Add(new ReadState
        {
            Id = "reta-1", ChannelId = "chan-0", MemberId = MemberId,
            LastReadMessageId = "mesg-old", LastReadAt = JoinedAt, MessageCountAtRead = 1,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);

        var states = _context.ReadStates.AsNoTracking().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(states, Has.Count.EqualTo(2), "the existing row is updated, not duplicated");
            Assert.That(Stored("chan-0")!.LastReadMessageId, Is.EqualTo("mesg-0"));
            Assert.That(Stored("chan-0")!.LastReadAt, Is.EqualTo(HeadAt));
        });
    }

    [Test]
    public async Task MarkAllRead_LeavesEmptyChannelsAlone()
    {
        await SeedAsync(channelCount: 2, withMessages: false);

        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);

        Assert.That(_context.ReadStates.AsNoTracking().ToList(), Is.Empty,
            "a channel with no messages has nothing to mark read, and inserting a row for it is pure write amplification");
    }

    [Test]
    public async Task MarkAllRead_AcrossManyChannels_ClearsEveryOne()
    {
        await SeedAsync(channelCount: 300);

        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);

        var states = _context.ReadStates.AsNoTracking().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(states, Has.Count.EqualTo(300));
            Assert.That(states.All(s => s.LastReadAt == HeadAt), Is.True);
        });
    }

    [Test]
    public async Task MarkAllRead_IsIdempotent()
    {
        await SeedAsync(channelCount: 2);

        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);
        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);

        Assert.That(_context.ReadStates.AsNoTracking().ToList(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task MarkAllRead_UserInNoGuilds_ReturnsNoContentWithoutTouchingAnything()
    {
        var result = await _endpoint.MarkAllRead(TestPrincipal.Create("user-nobody"), _context, _hub);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(_context.ReadStates.AsNoTracking().ToList(), Is.Empty);
        });
    }

    [Test]
    public async Task MarkAllRead_DoesNotTouchAnotherMembersReadStates()
    {
        await SeedAsync(channelCount: 1);
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "memb-2", GuildId = GuildId, UserId = "user-2", JoinedAt = JoinedAt.UtcDateTime,
            SearchValue = "USER-2", CreatedAt = Now, UpdatedAt = Now,
        });
        _context.ReadStates.Add(new ReadState
        {
            Id = "reta-other", ChannelId = "chan-0", MemberId = "memb-2", LastReadAt = JoinedAt,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _endpoint.MarkAllRead(TestPrincipal.Create(UserId), _context, _hub);

        var other = _context.ReadStates.AsNoTracking().Single(r => r.MemberId == "memb-2");
        Assert.That(other.LastReadAt, Is.EqualTo(JoinedAt), "another member's cursor is not this caller's to move");
    }
}
