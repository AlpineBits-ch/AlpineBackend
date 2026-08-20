using Echo.Realtime;
using Guild.Application.Bus.Events.Realtime;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Guild.Tests.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// The ack against a real Postgres, where the read-state uniqueness index exists.
///
/// A channel opened for the first time has no read state, and the client acks more than once while
/// it settles - the page landing, then each message that arrives. Two of those acks racing each
/// inserted their own row, and a second row keeps the channel unread for good: the unread query
/// left-joins every read state the member has, so the stale one satisfies the predicate no matter
/// how far along the other has moved.
/// </summary>
[TestFixture]
public class GuildReadConcurrencyTests
{
    private const string GuildId = MigrationSqlHarness.GuildId;
    private const string MemberId = "memb-concurrency";
    private const string UserId = "user-" + MemberId;
    private const string ChannelId = "chan-concurrency";

    private static readonly DateTimeOffset HeadCreatedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly GuildReadHandler _handler = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();
        await MigrationSqlHarness.SeedMemberAsync(MemberId);

        await using var context = new PostgresGuildContext();
        context.Channels.Add(new Guild.Domain.Aggregates.Channel
        {
            Id = ChannelId,
            GuildId = GuildId,
            Name = "about that message",
            Description = "d",
            Type = ChannelType.Thread,
            LastMessageId = "mesg-head",
            LastActivityAt = HeadCreatedAt,
            MessageCount = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private Task AckAsync(MicroserviceContext context) =>
        _handler.Handle(
            new UpdateGuildReadCommand(UserId, ChannelId, "mesg-head"),
            context,
            new FakeInvokingMessageBus(),
            NullLogger<GuildReadHandler>.Instance);

    private static async Task<List<ReadState>> StoredAsync()
    {
        await using var context = new PostgresGuildContext();
        return await context.ReadStates.AsNoTracking()
            .Where(r => r.ChannelId == ChannelId && r.MemberId == MemberId)
            .ToListAsync();
    }

    [Test]
    public async Task TwoAcksOnAChannelWithNoReadState_LeaveOneRowAtTheHead()
    {
        await using var first = new PostgresGuildContext();
        await using var second = new PostgresGuildContext();

        await Task.WhenAll(AckAsync(first), AckAsync(second));

        var stored = await StoredAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored, Has.Count.EqualTo(1), "a second row is what leaves the channel unread for good");
            Assert.That(stored[0].LastReadAt, Is.EqualTo(HeadCreatedAt));
            Assert.That(stored[0].LastReadMessageId, Is.EqualTo("mesg-head"));
        });
    }

    /// <summary>The whole point of the index: one read state at the head means nothing left in the
    /// unread page.</summary>
    [Test]
    public async Task AfterTheAck_TheChannelIsOutOfTheUnreadPage()
    {
        await using (var context = new PostgresGuildContext())
        {
            await AckAsync(context);
        }

        await using var reader = new PostgresGuildContext();
        var unread = await InboxService.BuildUnreadQuery(reader, UserId, cursor: null).ToListAsync();

        Assert.That(unread, Is.Empty);
    }

    /// <summary>The failure this fixture exists for, with the index stood down so the second row can
    /// be written: the channel stays unread however current the other row is.</summary>
    [Test]
    public async Task AStaleSecondReadState_KeepsTheChannelUnread()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.ExecuteAsync(connection, "DROP INDEX IF EXISTS ix_read_states_member_id_channel_id;");
        try
        {
            await MigrationSqlHarness.SeedReadStateAsync(
                connection, "reta-stale", MemberId, ChannelId, HeadCreatedAt.AddHours(-3));
            await MigrationSqlHarness.SeedReadStateAsync(
                connection, "reta-current", MemberId, ChannelId, HeadCreatedAt);

            await using var reader = new PostgresGuildContext();
            var unread = await InboxService.BuildUnreadQuery(reader, UserId, cursor: null).ToListAsync();

            Assert.That(unread.Select(r => r.ChannelId), Is.EqualTo(new[] { ChannelId }));
        }
        finally
        {
            await MigrationSqlHarness.ExecuteAsync(connection,
                "DELETE FROM read_states; CREATE UNIQUE INDEX ix_read_states_member_id_channel_id ON read_states (member_id, channel_id);");
        }
    }
}
