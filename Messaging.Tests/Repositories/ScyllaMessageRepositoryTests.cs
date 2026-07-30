using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Repositories;

/// <summary>
/// Covers ScyllaMessageRepository's list-by-partition reads against a FakeCassandraMapper.
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

    // ══════════════════════════════════════════════════════════════════════════ Paging and
    // ordering ══════════════════════════════════════════════════════════════════════════

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
