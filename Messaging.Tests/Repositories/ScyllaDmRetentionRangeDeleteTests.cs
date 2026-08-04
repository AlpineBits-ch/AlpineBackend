using Cassandra;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;

namespace Messaging.Tests.Repositories;

/// <summary>
/// T2-22's retention range read and the deletes it feeds, against a real Scylla node.
/// </summary>
[TestFixture]
[Category("Scylla")]
public class ScyllaDmRetentionRangeDeleteTests
{
    private const string Author = "user-retention";
    private const string OtherAuthor = "user-other";

    /// <summary><c>host:port</c>, or just <c>host</c> for the default 9042.</summary>
    private static string? ContactPoint => Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA");

    private ICluster _cluster = null!;
    private ISession _session = null!;
    private ScyllaContext _context = null!;
    private ScyllaMessageRepository _repo = null!;
    private string _keyspace = null!;

    /// <summary>Millisecond-truncated: Cassandra timestamps have millisecond resolution, so a seed
    /// with sub-millisecond ticks does not read back equal to what was written, and every assertion
    /// comparing a written row to a read one would be about the driver's rounding instead of about
    /// retention.</summary>
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (string.IsNullOrWhiteSpace(ContactPoint))
        {
            Assert.Ignore(
                "Set ECHO_TEST_SCYLLA to a reachable node (host or host:port, e.g. localhost:9042) to run the "
                + "DM-retention range-delete tests. They are deliberately not run against FakeCassandraMapper: "
                + "a fake can check the text of a statement but cannot reject one, and the statement under test "
                + "decides which of a user's messages get deleted.");
        }

        var parts = ContactPoint!.Split(':', 2);
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 9042;

        var builder = Cluster.Builder()
            .AddContactPoint(host)
            .WithPort(port)
            // Otherwise a node that is up but still bootstrapping fails the whole fixture on a
            // connect timeout rather than on anything to do with retention.
            .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(20_000))
            .WithQueryTimeout(30_000);

        var user = Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA_USERNAME");
        var password = Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA_PASSWORD");
        if (!string.IsNullOrWhiteSpace(user)) builder = builder.WithCredentials(user, password ?? string.Empty);

        _cluster = builder.Build();
        _session = await _cluster.ConnectAsync();

        // Throwaway keyspace, SimpleStrategy rf=1: the production keyspace is
        // NetworkTopologyStrategy/datacenter1, which a single-node test container may not have a
        // matching DC name for, and this is about the statement rather than about placement.
        _keyspace = "echo_dmret_" + Guid.NewGuid().ToString("N");
        _session.CreateKeyspaceIfNotExists(_keyspace, new Dictionary<string, string>
        {
            { "class", "SimpleStrategy" },
            { "replication_factor", "1" },
        });
        _session.ChangeKeyspace(_keyspace);

        // The real schema and the real column map, not a copy of them - see CreateForSessionAsync.
        _context = await ScyllaContext.CreateForSessionAsync(_session);
        _repo = new ScyllaMessageRepository(_context);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_session is null) return;

        try
        {
            await _session.ExecuteAsync(new SimpleStatement($"DROP KEYSPACE IF EXISTS {_keyspace};"));
        }
        finally
        {
            await _session.ShutdownAsync();
            _cluster?.Dispose();
        }
    }

    /// <summary>One partition per test.</summary>
    private static string NewContextId() => "conv-" + Guid.NewGuid().ToString("N");

    private async Task<Message> SeedAsync(string contextId, string authorId, TimeSpan age)
    {
        var at = Now - age;
        var message = new Message
        {
            Id = Message.GenerateId(),
            ContextId = contextId,
            ConversationId = contextId,
            AuthorId = authorId,
            Content = "hello"u8.ToArray(),
            CreatedAt = at,
            UpdatedAt = at,
        };

        await _repo.CreateMessageAsync(message);
        return message;
    }

    /// <summary>Exactly what DmRetentionSweepService does to one conversation: page the range,
    /// keep only the sweeping user's own rows, delete those. Written out here rather than reused
    /// from the service so this fixture needs no EF context, privacy cache or bus - it is about the
    /// CQL, and everything else is a way for the CQL to be wrong without anyone noticing.</summary>
    private async Task<List<Message>> SweepAsync(
        string contextId, string authorId, DateTimeOffset cutoff, int pageSize = 100, int maxPages = 20)
    {
        var afterCreatedAt = DateTimeOffset.MinValue;
        var afterMessageId = string.Empty;
        var deleted = new List<Message>();

        for (var page = 0; page < maxPages; page++)
        {
            var rows = await _repo.GetContextMessagesOlderThanAsync(
                contextId, cutoff, afterCreatedAt, afterMessageId, pageSize);

            if (rows.Count == 0) break;

            var last = rows[^1];
            afterCreatedAt = last.CreatedAt;
            afterMessageId = last.Id;

            var mine = rows.Where(m => m.AuthorId == authorId).ToList();
            if (mine.Count > 0)
            {
                await _repo.DeleteMessagesAsync(mine);
                deleted.AddRange(mine);
            }

            if (rows.Count < pageSize) break;
        }

        return deleted;
    }

    private async Task<List<string>> SurvivingIdsAsync(string contextId)
    {
        var (messages, _) = await _repo.GetMessagesByContextIdAsync(contextId, take: 500, skip: 0);
        return messages.Select(m => m.Id).ToList();
    }

    // ══════════════════════════════════════════════════════════════════════════ The statement
    // itself ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task TheOriginalRowTupleCursorIsAcceptedByTheClusterAndSilentlySkipsRows()
    {
        // ── The finding this fixture was written to produce. ──
        //
        // The shipped-but-never-executed statement was
        //   WHERE context_id = ? AND (created_at, message_id) > (?, ?) AND (created_at) < (?)
        //   ORDER BY created_at ASC LIMIT ?
        // and the one-component upper-bound tuple was correct reasoning: Cassandra does refuse to mix
        // a multi-column and a single-column relation on the same clustering column. The cluster
        // accepts the whole statement. It is still wrong, and being accepted is what made it
        // dangerous - there was no error to notice.
        //
        // messages is clustered (created_at DESC, message_id ASC). ORDER BY created_at ASC reverses
        // BOTH clustering columns, so rows arrive (created_at ASC, message_id DESC). The row-tuple
        // relation, meanwhile, is plain ascending-lexicographic on both components (verified below).
        // The scan and the cursor therefore disagree about the tie-break column, and resuming after
        // the last row of a page excludes every same-millisecond sibling underneath it. On the
        // retention path that is a permanent, silent under-delete: the user asked for those messages
        // to be gone and they never will be, on any tick, ever.
        //
        // Pinned as a test rather than only as a comment so that "restore the tuple cursor, it reads
        // better" fails here instead of in production.
        var contextId = NewContextId();
        var sameInstant = Now - TimeSpan.FromDays(40);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var message = new Message
            {
                Id = Message.GenerateId(),
                ContextId = contextId,
                ConversationId = contextId,
                AuthorId = Author,
                Content = "hello"u8.ToArray(),
                CreatedAt = sameInstant,
                UpdatedAt = sameInstant,
            };
            await _repo.CreateMessageAsync(message);
            ids.Add(message.Id);
        }

        const string originalCql =
            "SELECT " + Message.SelectColumns + " FROM messages " +
            "WHERE context_id = ? AND (created_at, message_id) > (?, ?) AND (created_at) < (?) " +
            "ORDER BY created_at ASC LIMIT ?";

        var firstPage = (await _context.Mapper.FetchAsync<Message>(
            originalCql, contextId, DateTimeOffset.MinValue, string.Empty, Now.AddDays(-30), 1)).ToList();

        var resumed = (await _context.Mapper.FetchAsync<Message>(
            originalCql, contextId, firstPage[0].CreatedAt, firstPage[0].Id, Now.AddDays(-30), 10)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Single().Id, Is.EqualTo(ids.Max(StringComparer.Ordinal)),
                "ORDER BY created_at ASC returns same-instant rows message_id DESC - the largest id first");
            Assert.That(resumed, Is.Empty,
                "the row-tuple cursor is ascending-lexicographic, so resuming after the largest id in "
                + "the millisecond excludes the two rows the scan had not yet returned");
        });

        // And the replacement reaches all three from the same starting position.
        var fixedRows = await _repo.GetContextMessagesOlderThanAsync(
            contextId, Now.AddDays(-30), DateTimeOffset.MinValue, string.Empty, 10);

        Assert.That(fixedRows.Select(r => r.Id), Is.EquivalentTo(ids));
    }

    [Test]
    public async Task TheRangeReadReturnsOnlyRowsOlderThanTheCutoff()
    {
        var contextId = NewContextId();
        var old = await SeedAsync(contextId, Author, TimeSpan.FromDays(40));
        var recent = await SeedAsync(contextId, Author, TimeSpan.FromDays(5));

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            contextId, Now.AddDays(-30), DateTimeOffset.MinValue, string.Empty, 10);

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => r.Id), Does.Contain(old.Id));
            Assert.That(rows.Select(r => r.Id), Does.Not.Contain(recent.Id),
                "a row inside the retention window was selected for deletion");
        });
    }

    [Test]
    public async Task TheRangeReadComesBackOldestFirst()
    {
        // The clustering order is created_at DESC; the retention scan overrides it to ASC so the
        // resume cursor moves forward through history.
        var contextId = NewContextId();
        var oldest = await SeedAsync(contextId, Author, TimeSpan.FromDays(60));
        var middle = await SeedAsync(contextId, Author, TimeSpan.FromDays(50));
        var newest = await SeedAsync(contextId, Author, TimeSpan.FromDays(40));

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            contextId, Now.AddDays(-30), DateTimeOffset.MinValue, string.Empty, 10);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(new[] { oldest.Id, middle.Id, newest.Id }));
    }

    [Test]
    public async Task AnEmptyWindowSelectsNothingAndDeletesNothing()
    {
        // A user whose messages are all newer than their retention window.
        var contextId = NewContextId();
        var a = await SeedAsync(contextId, Author, TimeSpan.FromDays(3));
        var b = await SeedAsync(contextId, Author, TimeSpan.FromDays(1));

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            contextId, Now.AddDays(-30), DateTimeOffset.MinValue, string.Empty, 10);
        var deleted = await SweepAsync(contextId, Author, Now.AddDays(-30));

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Empty);
            Assert.That(deleted, Is.Empty);
            Assert.That(SurvivingIdsAsync(contextId).Result, Is.EquivalentTo(new[] { a.Id, b.Id }));
        });
    }

    [Test]
    public async Task ARowExactlyOnTheCutoffIsNotDeleted()
    {
        // The bound is strict (<), and it has to be: "older than N days" evaluated at the instant a
        // message turns N days old is a row the user has not yet asked to lose.
        var contextId = NewContextId();
        var cutoff = Now.AddDays(-30);
        var onTheBoundary = await SeedAsync(contextId, Author, TimeSpan.FromDays(30));

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            contextId, cutoff, DateTimeOffset.MinValue, string.Empty, 10);

        Assert.That(rows.Select(r => r.Id), Does.Not.Contain(onTheBoundary.Id));
    }

    // ══════════════════════════════════════════════════════════════════════════ What the delete
    // must NOT touch ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeletesTheOldRowsAndLeavesTheNewerOnes()
    {
        var contextId = NewContextId();
        var old1 = await SeedAsync(contextId, Author, TimeSpan.FromDays(90));
        var old2 = await SeedAsync(contextId, Author, TimeSpan.FromDays(45));
        var recent1 = await SeedAsync(contextId, Author, TimeSpan.FromDays(29));
        var recent2 = await SeedAsync(contextId, Author, TimeSpan.FromHours(1));

        var deleted = await SweepAsync(contextId, Author, Now.AddDays(-30));

        var surviving = await SurvivingIdsAsync(contextId);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.Select(d => d.Id), Is.EquivalentTo(new[] { old1.Id, old2.Id }));
            Assert.That(surviving, Is.EquivalentTo(new[] { recent1.Id, recent2.Id }),
                "the range delete removed rows inside the retention window");
        });
    }

    [Test]
    public async Task NeverDeletesTheOtherPartysMessages()
    {
        // The line between a retention control and a history-rewriting one, asserted against a real
        // node: the other party's rows share the partition and the age, and only the author filter
        // stands between them and deletion.
        var contextId = NewContextId();
        var mineOld = await SeedAsync(contextId, Author, TimeSpan.FromDays(40));
        var theirsOld = await SeedAsync(contextId, OtherAuthor, TimeSpan.FromDays(40));
        var theirsAncient = await SeedAsync(contextId, OtherAuthor, TimeSpan.FromDays(400));

        var deleted = await SweepAsync(contextId, Author, Now.AddDays(-30));

        var surviving = await SurvivingIdsAsync(contextId);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.Select(d => d.Id), Is.EquivalentTo(new[] { mineOld.Id }));
            Assert.That(surviving, Is.EquivalentTo(new[] { theirsOld.Id, theirsAncient.Id }),
                "the other party's copy of a conversation they were also in is not this user's to erase");
        });
    }

    [Test]
    public async Task NeverTouchesAnotherConversation()
    {
        // The partition key is the only thing scoping the delete.
        var mine = NewContextId();
        var elsewhere = NewContextId();

        var doomed = await SeedAsync(mine, Author, TimeSpan.FromDays(40));
        var bystander = await SeedAsync(elsewhere, Author, TimeSpan.FromDays(400));

        await SweepAsync(mine, Author, Now.AddDays(-30));

        Assert.Multiple(() =>
        {
            Assert.That(SurvivingIdsAsync(mine).Result, Is.Empty);
            Assert.That(SurvivingIdsAsync(elsewhere).Result, Is.EquivalentTo(new[] { bystander.Id }),
                "a conversation the sweep was not scanning lost a message");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The resume cursor, which is why the lower bound is a row tuple at all
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PagingTheRangeNeitherSkipsNorRepeatsSameMillisecondSiblings()
    {
        // created_at is not unique - Cassandra timestamps are millisecond resolution, so two
        // messages a few hundred microseconds apart land on the same value and are ordered by
        // message_id.
        var contextId = NewContextId();
        var sameInstant = Now - TimeSpan.FromDays(40);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var message = new Message
            {
                Id = Message.GenerateId(),
                ContextId = contextId,
                ConversationId = contextId,
                AuthorId = Author,
                Content = "hello"u8.ToArray(),
                CreatedAt = sameInstant,
                UpdatedAt = sameInstant,
            };
            await _repo.CreateMessageAsync(message);
            ids.Add(message.Id);
        }

        // One row per page forces the cursor to resume from inside the millisecond twice.
        var seen = new List<string>();
        var afterCreatedAt = DateTimeOffset.MinValue;
        var afterMessageId = string.Empty;

        for (var page = 0; page < 5; page++)
        {
            var rows = await _repo.GetContextMessagesOlderThanAsync(
                contextId, Now.AddDays(-30), afterCreatedAt, afterMessageId, 1);
            if (rows.Count == 0) break;

            seen.AddRange(rows.Select(r => r.Id));
            afterCreatedAt = rows[^1].CreatedAt;
            afterMessageId = rows[^1].Id;
        }

        Assert.That(seen, Is.EquivalentTo(ids), "same-millisecond siblings were skipped or repeated");
    }

    [Test]
    public async Task SweepingWithATinyPageStillReachesEveryOldRow()
    {
        var contextId = NewContextId();
        var doomed = new List<string>();
        for (var i = 0; i < 5; i++)
            doomed.Add((await SeedAsync(contextId, Author, TimeSpan.FromDays(40 + i))).Id);

        var recent = await SeedAsync(contextId, Author, TimeSpan.FromDays(2));

        await SweepAsync(contextId, Author, Now.AddDays(-30), pageSize: 2);

        Assert.That(await SurvivingIdsAsync(contextId), Is.EquivalentTo(new[] { recent.Id }));
    }

    [Test]
    public async Task ATinyPageEndingInsideOneMillisecondStillReachesTheWholeGroup()
    {
        // The regression this whole fixture was written to catch, in its end-to-end form: a page
        // cap that lands in the middle of a same-millisecond group.
        var contextId = NewContextId();
        var sameInstant = Now - TimeSpan.FromDays(40);

        var mine = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var message = new Message
            {
                Id = Message.GenerateId(),
                ContextId = contextId,
                ConversationId = contextId,
                AuthorId = Author,
                Content = "hello"u8.ToArray(),
                CreatedAt = sameInstant,
                UpdatedAt = sameInstant,
            };
            await _repo.CreateMessageAsync(message);
            mine.Add(message.Id);
        }

        var theirs = new Message
        {
            Id = Message.GenerateId(),
            ContextId = contextId,
            ConversationId = contextId,
            AuthorId = OtherAuthor,
            Content = "hello"u8.ToArray(),
            CreatedAt = sameInstant,
            UpdatedAt = sameInstant,
        };
        await _repo.CreateMessageAsync(theirs);

        var deleted = await SweepAsync(contextId, Author, Now.AddDays(-30), pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.Select(d => d.Id), Is.EquivalentTo(mine),
                "same-millisecond siblings past the page boundary were never reached");
            Assert.That(SurvivingIdsAsync(contextId).Result, Is.EquivalentTo(new[] { theirs.Id }),
                "the other party's message shared the millisecond and was deleted with it");
        });
    }

    [Test]
    public async Task AfterTheSweepTheRangeReadsBackEmpty()
    {
        // Proves the rows were really removed rather than merely absent from the page that was read:
        // the mapper builds its DELETE from the full clustering key of each poco, and a poco whose
        // timestamp did not round-trip exactly would delete nothing at all while reporting success.
        var contextId = NewContextId();
        await SeedAsync(contextId, Author, TimeSpan.FromDays(40));
        await SeedAsync(contextId, Author, TimeSpan.FromDays(41));

        await SweepAsync(contextId, Author, Now.AddDays(-30));

        var rows = await _repo.GetContextMessagesOlderThanAsync(
            contextId, Now.AddDays(-30), DateTimeOffset.MinValue, string.Empty, 10);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Empty);
            Assert.That(SurvivingIdsAsync(contextId).Result, Is.Empty);
        });
    }
}
