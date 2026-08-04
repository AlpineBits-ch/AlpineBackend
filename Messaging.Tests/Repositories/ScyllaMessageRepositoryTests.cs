using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Repositories;

/// <summary>
/// Covers ScyllaMessageRepository's list-by-partition reads against a FakeCassandraMapper.
///
/// Regression suite for the production incident where POST /messaging stored a message, the Guild
/// service saw it, message search found it, and GET /messaging/channels/{id}/messages returned
/// 200 [] anyway. The CQL and the data were both fine: the method enumerated the driver's fetch
/// result twice (once to build the page, once for the return value), and the driver's RowSet is a
/// self-consuming queue - the second pass came back empty, so every channel and conversation
/// history endpoint served an empty list on the Scylla backend. Only the EF Core backend was unit
/// tested at the time, and its ToListAsync materialization hid the whole class of bug.
///
/// FakeCassandraMapper deliberately reproduces that single-pass behaviour, so a repository that
/// re-enumerates fails here the same way it failed in production.
/// </summary>
[TestFixture]
public class ScyllaMessageRepositoryTests
{
    private FakeCassandraMapper _mapper = null!;
    private ScyllaMessageRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _mapper = new FakeCassandraMapper();
        _repo = new ScyllaMessageRepository(ScyllaContext.CreateDebug(_mapper));
    }

    private static Message MakeMessage(string contextId, int minutesOld) =>
        new()
        {
            Id = Message.GenerateId(),
            ContextId = contextId,
            ChannelId = contextId,
            AuthorId = "author-1",
            Content = "hello"u8.ToArray(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesOld),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesOld),
        };

    /// <summary>Rows as Scylla hands them over: newest first, per the DESC clustering order.</summary>
    private List<Message> SeedPartition(string contextId, int count)
    {
        var newestFirst = Enumerable.Range(0, count).Select(i => MakeMessage(contextId, i)).ToList();
        _mapper.Messages = newestFirst;
        return newestFirst;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2-22's retention read
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetContextMessagesOlderThanAsync_MaterializesTheSinglePassResult()
    {
        // Same trap as every other read here: the driver's RowSet self-consumes, so a lazy
        // projection handed straight back would delete nothing and report nothing.
        SeedPartition("conv-1", 3);

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            "conv-1", DateTimeOffset.UtcNow, DateTimeOffset.MinValue, string.Empty, 10);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows.Count, Is.EqualTo(3), "a second enumeration must still see the rows");
        });
    }

    [Test]
    public async Task GetContextMessagesOlderThanAsync_WithANonPositiveLimit_ReadsNothing()
    {
        // LIMIT 0 is a hard error in CQL, not an empty page, so the guard has to be in front of the
        // query rather than relying on the cluster to shrug.
        SeedPartition("conv-1", 3);

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            "conv-1", DateTimeOffset.UtcNow, DateTimeOffset.MinValue, string.Empty, 0);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Empty);
            Assert.That(_mapper.Fetches, Is.Empty, "no statement may be sent at all");
        });
    }

    [Test]
    public async Task GetContextMessagesOlderThanAsync_UsesATimestampOnlySlice_NotARowTupleCursor()
    {
        // This assertion used to be the opposite, and it was wrong on a real node.
        //
        // The old query resumed on the row tuple "(created_at, message_id) > (?, ?)" with the upper
        // bound written as the one-component tuple "(created_at) < (?)". It parsed, it ran, and it
        // silently skipped rows: messages is clustered (created_at DESC, message_id ASC), so
        // ORDER BY created_at ASC reverses *both* columns and the page arrives message_id DESC,
        // while the tuple comparison is ascending-lexicographic on both. Resuming after the last row
        // of a page therefore excludes every same-millisecond sibling below it - forever, on every
        // tick. ScyllaDmRetentionRangeDeleteTests catches it against a live node; this fixture
        // cannot, which is exactly why the shape is pinned here as well.
        SeedPartition("conv-1", 1);

        await _repo.GetContextMessagesOlderThanAsync(
            "conv-1", DateTimeOffset.UtcNow, DateTimeOffset.MinValue, string.Empty, 10);

        var cql = _mapper.Fetches[0].Cql;

        Assert.Multiple(() =>
        {
            Assert.That(cql, Does.Contain("created_at > ?"));
            Assert.That(cql, Does.Contain("created_at < ?"));
            Assert.That(cql, Does.Not.Contain("(created_at, message_id)"),
                "a row-tuple cursor cannot be correct against a mixed-order clustering key");
            Assert.That(cql, Does.Contain("ORDER BY created_at ASC"));
        });
    }

    [Test]
    public async Task GetContextMessagesOlderThanAsync_ReturnsRowsOldestFirst()
    {
        // The wire order under ORDER BY created_at ASC is (created_at ASC, message_id DESC) because
        // the clustering key mixes directions; the contract - and the relational backend - is
        // ascending on both, so the page is sorted before it is handed back.
        var newestFirst = SeedPartition("conv-1", 3);

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            "conv-1", DateTimeOffset.UtcNow, DateTimeOffset.MinValue, string.Empty, 10);

        Assert.That(rows.Select(m => m.Id),
            Is.EqualTo(newestFirst.AsEnumerable().Reverse().Select(m => m.Id)));
    }

    [Test]
    public async Task GetContextMessagesOlderThanAsync_WhenThePageIsFull_ReReadsTheBoundaryInstant()
    {
        // A page that fills its limit may have been cut inside one millisecond, and half a
        // same-millisecond group is what makes a timestamp-only resume lossy. The rest of that group
        // is read back with an equality-bounded query so the page always ends on a whole one.
        SeedPartition("conv-1", 2);

        await _repo.GetContextMessagesOlderThanAsync(
            "conv-1", DateTimeOffset.UtcNow, DateTimeOffset.MinValue, string.Empty, 2);

        Assert.Multiple(() =>
        {
            Assert.That(_mapper.Fetches, Has.Count.EqualTo(2));
            Assert.That(_mapper.Fetches[1].Cql, Does.Contain("created_at = ?"));
        });
    }

    [Test]
    public async Task GetContextMessagesOlderThanAsync_WhenThePageIsShort_DoesNotReReadAnything()
    {
        // A short page exhausted the slice, so its last group is already whole and the extra round
        // trip would buy nothing.
        SeedPartition("conv-1", 2);

        await _repo.GetContextMessagesOlderThanAsync(
            "conv-1", DateTimeOffset.UtcNow, DateTimeOffset.MinValue, string.Empty, 50);

        Assert.That(_mapper.Fetches, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The incident: a non-empty partition must not read back as an empty list
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessagesByChannelIdAsync_PartitionHasMessages_ReturnsThemAll()
    {
        SeedPartition("chan-1", 4);

        var (messages, _) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 50, skip: 0);

        Assert.That(messages, Has.Count.EqualTo(4), "channel history read back empty from a non-empty partition");
    }

    [Test]
    public async Task GetMessagesByConversationIdAsync_PartitionHasMessages_ReturnsThemAll()
    {
        SeedPartition("conv-1", 4);

        var (messages, _) = await _repo.GetMessagesByConversationIdAsync("conv-1", take: 50, skip: 0);

        Assert.That(messages, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task GetMessagesByContextIdAsync_PartitionHasMessages_ReturnsThemAll()
    {
        SeedPartition("chan-1", 4);

        var (messages, _) = await _repo.GetMessagesByContextIdAsync("chan-1", take: 50, skip: 0);

        Assert.That(messages, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task GetMessagesByChannelIdAsync_QueriesThePartitionKey()
    {
        SeedPartition("chan-1", 1);

        await _repo.GetMessagesByChannelIdAsync("chan-1", take: 10, skip: 0);

        var (cql, args) = _mapper.Fetches[0];
        Assert.Multiple(() =>
        {
            // channel_id/conversation_id are denormalized columns; filtering on them needs
            // ALLOW FILTERING and Scylla rejects the query outright.
            Assert.That(cql, Does.Contain("WHERE context_id = ?"));
            Assert.That(args[0], Is.EqualTo("chan-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Paging and ordering
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessagesByChannelIdAsync_ReturnsChronologicalOrder()
    {
        var newestFirst = SeedPartition("chan-1", 3);

        var (messages, _) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 50, skip: 0);

        Assert.That(messages.Select(m => m.Id), Is.EqualTo(newestFirst.AsEnumerable().Reverse().Select(m => m.Id)));
    }

    [Test]
    public async Task GetMessagesByChannelIdAsync_SkipsThePrecedingRows()
    {
        var newestFirst = SeedPartition("chan-1", 3);

        var (messages, _) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 2, skip: 1);

        // Wire order is [newest, middle, oldest]; skipping 1 leaves the two older rows, returned
        // oldest-first.
        Assert.That(messages.Select(m => m.Id), Is.EqualTo(new[] { newestFirst[2].Id, newestFirst[1].Id }));
    }

    [Test]
    public async Task GetMessagesByChannelIdAsync_RequestsSkipPlusTakeRows()
    {
        SeedPartition("chan-1", 3);

        await _repo.GetMessagesByChannelIdAsync("chan-1", take: 20, skip: 10);

        Assert.That(_mapper.Fetches[0].Args[1], Is.EqualTo(30));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Degenerate paging must not reach the cluster
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessagesByChannelIdAsync_NonPositiveTake_ReturnsEmptyWithoutQuerying()
    {
        SeedPartition("chan-1", 3);

        var (messages, reactions) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 0, skip: 0);

        Assert.Multiple(() =>
        {
            // "LIMIT ?" bound to 0 makes Scylla throw "LIMIT must be strictly positive" - a 500
            // on what is only an under-specified request.
            Assert.That(_mapper.Fetches, Is.Empty);
            Assert.That(messages, Is.Empty);
            Assert.That(reactions, Is.Empty);
        });
    }

    [Test]
    public async Task GetMessagesByChannelIdAsync_NegativeSkip_IsTreatedAsZero()
    {
        SeedPartition("chan-1", 2);

        var (messages, _) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 10, skip: -5);

        Assert.Multiple(() =>
        {
            Assert.That(_mapper.Fetches[0].Args[1], Is.EqualTo(10));
            Assert.That(messages, Has.Count.EqualTo(2));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Reactions must line up with the returned page
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessagesByChannelIdAsync_ReturnsReactionsKeyedByReturnedMessages()
    {
        var newestFirst = SeedPartition("chan-1", 2);
        _mapper.ReactionsByMessageId[newestFirst[0].Id] = new List<Reaction>
        {
            new() { ContextId = "chan-1", MessageId = newestFirst[0].Id, Emoji = "🔥", UserId = "user-1" },
        };

        var (messages, reactionsByMessage) = await _repo.GetMessagesByChannelIdAsync("chan-1", take: 10, skip: 0);

        Assert.Multiple(() =>
        {
            Assert.That(reactionsByMessage.Keys, Is.EquivalentTo(messages.Select(m => m.Id)));
            Assert.That(reactionsByMessage[newestFirst[0].Id], Has.Count.EqualTo(1));
        });
    }
}
