using Cassandra;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;

namespace Messaging.Tests.Repositories;

/// <summary>
/// Message-history scroll paging (<see
/// cref="ScyllaMessageRepository.GetMessagePageByCursorAsync"/>) against a real Scylla node - the
/// sibling of <c>ScyllaDmRetentionRangeDeleteTests</c>, and for the same reason.
/// </summary>
[TestFixture]
[Category("Scylla")]
public class ScyllaCursorPagingTests
{
    private const string Author = "user-scroll";

    /// <summary><c>host:port</c>, or just <c>host</c> for the default 9042.</summary>
    private static string? ContactPoint => Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA");

    private ICluster _cluster = null!;
    private ISession _session = null!;
    private ScyllaContext _context = null!;
    private ScyllaMessageRepository _repo = null!;
    private string _keyspace = null!;

    /// <summary>Millisecond-truncated: Cassandra timestamps have millisecond resolution, so a seed
    /// with sub-millisecond ticks does not read back equal to what was written and every assertion
    /// would be about the driver's rounding instead of about paging.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (string.IsNullOrWhiteSpace(ContactPoint))
        {
            Assert.Ignore(
                "Set ECHO_TEST_SCYLLA to a reachable node (host or host:port, e.g. localhost:9042) to run the "
                + "cursor-paging tests. They are deliberately not run against FakeCassandraMapper: the bug they "
                + "exist to catch is a disagreement between a WHERE relation and the cluster's scan order, and a "
                + "fake has no scan order to disagree with.");
        }

        var parts = ContactPoint!.Split(':', 2);
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 9042;

        var builder = Cluster.Builder()
            .AddContactPoint(host)
            .WithPort(port)
            // Otherwise a node that is up but still bootstrapping fails the whole fixture on a
            // connect timeout rather than on anything to do with paging.
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
        _keyspace = "echo_scroll_" + Guid.NewGuid().ToString("N");
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

    /// <summary>One partition per test - cheaper and far less flaky than a keyspace per test, and
    /// every read under test is partition-scoped, so tests cannot see each other's rows.</summary>
    private static string NewContextId() => "conv-" + Guid.NewGuid().ToString("N");

    private async Task<Message> SeedAsync(string contextId, DateTimeOffset at)
    {
        var message = new Message
        {
            Id = Message.GenerateId(),
            ContextId = contextId,
            ConversationId = contextId,
            AuthorId = Author,
            Content = "hello"u8.ToArray(),
            CreatedAt = at,
            UpdatedAt = at,
        };

        await _repo.CreateMessageAsync(message);
        return message;
    }

    /// <summary>A same-millisecond group of <paramref name="count"/> messages, returned in the order
    /// the clustering key puts them in: message_id ordinal-ascending. Ids are ULIDs, whose random
    /// tail makes mint order and sort order differ inside one millisecond - so the sort is what the
    /// assertions have to be written against, not the insertion order.</summary>
    private async Task<List<Message>> SeedInstantGroupAsync(string contextId, DateTimeOffset at, int count)
    {
        var seeded = new List<Message>();
        for (var i = 0; i < count; i++) seeded.Add(await SeedAsync(contextId, at));

        return seeded.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    private MessagePageQuery Query(string contextId, string anchorId, MessageCursorDirection direction, int limit) =>
        new() { ContextId = contextId, AnchorMessageId = anchorId, Direction = direction, Limit = limit };

    private async Task<List<Message>> PageAsync(
        string contextId, string anchorId, MessageCursorDirection direction, int limit)
    {
        var (messages, _) = await _repo.GetMessagePageByCursorAsync(Query(contextId, anchorId, direction, limit));
        return messages.ToList();
    }

    /// <summary>Scrolls until the range is exhausted, exactly as a client does: each page's far edge
    /// becomes the next page's anchor. Backwards that is the oldest row returned (index 0, since
    /// pages come back oldest-first); forwards it is the newest.</summary>
    private async Task<List<string>> ScrollAsync(
        string contextId, string startAnchorId, MessageCursorDirection direction, int pageSize, int maxPages = 20)
    {
        var seen = new List<string>();
        var anchorId = startAnchorId;

        for (var page = 0; page < maxPages; page++)
        {
            var rows = await PageAsync(contextId, anchorId, direction, pageSize);
            if (rows.Count == 0) break;

            seen.AddRange(rows.Select(r => r.Id));
            anchorId = direction == MessageCursorDirection.Before ? rows[0].Id : rows[^1].Id;
        }

        return seen;
    }

    // ══════════════════════════════════════════════════════════════════════════ The statement
    // itself ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task TheOriginalRowTupleCursorIsAcceptedByTheClusterAndSilentlyLosesRows_ScrollingBack()
    {
        // ── The finding, pinned so "the tuple comparison reads better" fails here, not in production. ──
        //
        // The shipped statement for a `before` page was
        //   WHERE context_id = ? AND (created_at, message_id) < (?, ?)
        //   ORDER BY created_at DESC LIMIT ?
        // ORDER BY created_at DESC is the table's own clustering order, so the scan runs
        // (created_at DESC, message_id ASC) - the tie-break ASCENDING while the timestamp descends.
        // The relation is ascending-lexicographic on both. So inside one millisecond the scan hands
        // back the members FURTHEST from the anchor first, and LIMIT keeps those: a "two messages
        // immediately before this one" page returns the two furthest instead, and the next page,
        // anchored on the oldest row it was given, is already past the two it skipped.
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 4);

        const string originalCql =
            "SELECT " + Message.SelectColumns + " FROM messages " +
            "WHERE context_id = ? AND (created_at, message_id) < (?, ?) " +
            "ORDER BY created_at DESC LIMIT ?";

        var anchor = group[3];
        var firstPage = (await _context.Mapper.FetchAsync<Message>(
            originalCql, contextId, anchor.CreatedAt, anchor.Id, 2)).ToList();

        var resumed = (await _context.Mapper.FetchAsync<Message>(
            originalCql, contextId, firstPage[^1].CreatedAt, firstPage[^1].Id, 10)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Select(m => m.Id), Is.EqualTo(new[] { group[0].Id, group[1].Id }),
                "the natural scan order is message_id ASC, so LIMIT keeps the members furthest from the anchor");
            Assert.That(resumed.Select(m => m.Id), Does.Not.Contain(group[2].Id),
                "group[2] sits between the anchor and the page that was returned, and no cursor derived "
                + "from that page can ever reach it again - the message is gone from this client's history");
            Assert.That(resumed.Select(m => m.Id), Is.EqualTo(new[] { group[0].Id }),
                "what it does return is a row the previous page already served: the same disagreement "
                + "duplicates on one edge while it skips on the other");
        });

        // The replacement reaches the nearest two, and then the rest.
        var fixedPage = await PageAsync(contextId, anchor.Id, MessageCursorDirection.Before, 2);
        Assert.That(fixedPage.Select(m => m.Id), Is.EqualTo(new[] { group[1].Id, group[2].Id }),
            "the two immediately before the anchor, oldest-first");
    }

    [Test]
    public async Task TheOriginalRowTupleCursorIsAcceptedByTheClusterAndSilentlyLosesRows_ScrollingForward()
    {
        // The other direction fails the same way for the mirror-image reason: an `after` page asked
        // for ORDER BY created_at ASC, which on a mixed-order table reverses BOTH clustering
        // columns, so the scan ran (created_at ASC, message_id DESC) while the relation stayed
        // ascending-lexicographic.
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 4);

        const string originalCql =
            "SELECT " + Message.SelectColumns + " FROM messages " +
            "WHERE context_id = ? AND (created_at, message_id) > (?, ?) " +
            "ORDER BY created_at ASC LIMIT ?";

        var anchor = group[0];
        var firstPage = (await _context.Mapper.FetchAsync<Message>(
            originalCql, contextId, anchor.CreatedAt, anchor.Id, 2)).ToList();

        var resumed = (await _context.Mapper.FetchAsync<Message>(
            originalCql, contextId, firstPage[^1].CreatedAt, firstPage[^1].Id, 10)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Select(m => m.Id), Is.EqualTo(new[] { group[3].Id, group[2].Id }),
                "ORDER BY created_at ASC reverses the tie-break too, so LIMIT keeps the furthest members");
            Assert.That(resumed.Select(m => m.Id), Does.Not.Contain(group[1].Id),
                "group[1] is adjacent to the anchor and unreachable from the resumed cursor forever");
            Assert.That(resumed.Select(m => m.Id), Is.EqualTo(new[] { group[3].Id }),
                "and the row it does return was already on the previous page");
        });

        var fixedPage = await PageAsync(contextId, anchor.Id, MessageCursorDirection.After, 2);
        Assert.That(fixedPage.Select(m => m.Id), Is.EqualTo(new[] { group[1].Id, group[2].Id }),
            "the two immediately after the anchor, oldest-first");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // No message is skipped when a page boundary lands inside a millisecond
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ScrollingBackThroughASameMillisecondGroupReachesEveryMessageExactlyOnce()
    {
        // The symptom this fixture is named for: a user scrolls up through a burst and one message
        // is simply not there.
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 7);

        var seen = await ScrollAsync(contextId, group[6].Id, MessageCursorDirection.Before, pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique, "no message may be served twice while scrolling");
            Assert.That(seen, Is.EquivalentTo(group.Take(6).Select(m => m.Id)),
                "a same-millisecond sibling past the page boundary was never reached");
        });
    }

    [Test]
    public async Task ScrollingForwardThroughASameMillisecondGroupReachesEveryMessageExactlyOnce()
    {
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 7);

        var seen = await ScrollAsync(contextId, group[0].Id, MessageCursorDirection.After, pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique);
            Assert.That(seen, Is.EquivalentTo(group.Skip(1).Select(m => m.Id)));
        });
    }

    [Test]
    public async Task ScrollingBackAcrossAMillisecondBoundaryReachesEveryMessage()
    {
        // The group is not the whole partition here: the page boundary has to be able to land inside
        // a group that the scan reaches *after* crossing a timestamp, which is the case the boundary
        // re-read exists for and which a single-instant partition never exercises.
        var contextId = NewContextId();
        var newest = await SeedAsync(contextId, Now);
        var middle = await SeedInstantGroupAsync(contextId, Now - TimeSpan.FromMinutes(1), 5);
        var oldest = await SeedAsync(contextId, Now - TimeSpan.FromMinutes(2));

        var seen = await ScrollAsync(contextId, newest.Id, MessageCursorDirection.Before, pageSize: 2);

        var expected = middle.Select(m => m.Id).Append(oldest.Id);
        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique);
            Assert.That(seen, Is.EquivalentTo(expected));
        });
    }

    [Test]
    public async Task ScrollingForwardAcrossAMillisecondBoundaryReachesEveryMessage()
    {
        var contextId = NewContextId();
        var oldest = await SeedAsync(contextId, Now - TimeSpan.FromMinutes(2));
        var middle = await SeedInstantGroupAsync(contextId, Now - TimeSpan.FromMinutes(1), 5);
        var newest = await SeedAsync(contextId, Now);

        var seen = await ScrollAsync(contextId, oldest.Id, MessageCursorDirection.After, pageSize: 2);

        var expected = middle.Select(m => m.Id).Append(newest.Id);
        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique);
            Assert.That(seen, Is.EquivalentTo(expected));
        });
    }

    [Test]
    public async Task APageAnchoredInsideAGroupReturnsTheAdjacentMembersNotTheFurthestOnes()
    {
        // "Adjacent" is the entire contract of a cursor page.
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 6);

        var before = await PageAsync(contextId, group[4].Id, MessageCursorDirection.Before, 2);
        var after = await PageAsync(contextId, group[1].Id, MessageCursorDirection.After, 2);

        Assert.Multiple(() =>
        {
            Assert.That(before.Select(m => m.Id), Is.EqualTo(new[] { group[2].Id, group[3].Id }));
            Assert.That(after.Select(m => m.Id), Is.EqualTo(new[] { group[2].Id, group[3].Id }));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Page size, ordering and the degenerate ranges
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task APageIsNeverLargerThanTheLimitEvenWhenItEndsInsideAGroup()
    {
        // Deliberately unlike the retention scan, which over-returns to keep whole millisecond
        // groups together.
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 9);

        var page = await PageAsync(contextId, group[8].Id, MessageCursorDirection.Before, 3);

        Assert.That(page, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task EveryPageComesBackOrderedByCreatedAtThenIdAscending()
    {
        // The order the relational backend returns (OrderBy CreatedAt, ThenBy Id) - asserted here
        // so a client that pages one backend and then the other cannot see two different orders.
        var contextId = NewContextId();
        await SeedInstantGroupAsync(contextId, Now - TimeSpan.FromMinutes(2), 3);
        await SeedInstantGroupAsync(contextId, Now - TimeSpan.FromMinutes(1), 3);
        var anchor = await SeedAsync(contextId, Now);

        var page = await PageAsync(contextId, anchor.Id, MessageCursorDirection.Before, 10);

        var expected = page
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => m.Id)
            .ToList();

        Assert.That(page.Select(m => m.Id), Is.EqualTo(expected));
    }

    [Test]
    public async Task AnAroundPageSpansBothSidesOfAnAnchorInsideAGroupAndIncludesIt()
    {
        var contextId = NewContextId();
        var instant = Now - TimeSpan.FromMinutes(5);
        var group = await SeedInstantGroupAsync(contextId, instant, 7);

        var page = await PageAsync(contextId, group[3].Id, MessageCursorDirection.Around, 5);

        Assert.Multiple(() =>
        {
            Assert.That(page.Select(m => m.Id), Does.Contain(group[3].Id), "the anchor is the point of `around`");
            Assert.That(page.Select(m => m.Id),
                Is.EqualTo(new[] { group[1].Id, group[2].Id, group[3].Id, group[4].Id, group[5].Id }),
                "two either side of the anchor, in order, all from inside the one millisecond");
        });
    }

    [Test]
    public async Task AnEmptyRangeComesBackEmptyRatherThanWrappingAround()
    {
        // Scrolling up from the oldest message in a partition, and down from the newest.
        var contextId = NewContextId();
        var group = await SeedInstantGroupAsync(contextId, Now - TimeSpan.FromMinutes(5), 3);

        var beforeOldest = await PageAsync(contextId, group[0].Id, MessageCursorDirection.Before, 10);
        var afterNewest = await PageAsync(contextId, group[2].Id, MessageCursorDirection.After, 10);

        Assert.Multiple(() =>
        {
            Assert.That(beforeOldest, Is.Empty);
            Assert.That(afterNewest, Is.Empty);
        });
    }

    [Test]
    public async Task ASingleRowPartitionPagesToEmptyInBothDirections()
    {
        var contextId = NewContextId();
        var only = await SeedAsync(contextId, Now);

        var before = await PageAsync(contextId, only.Id, MessageCursorDirection.Before, 10);
        var after = await PageAsync(contextId, only.Id, MessageCursorDirection.After, 10);
        var around = await PageAsync(contextId, only.Id, MessageCursorDirection.Around, 10);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.Empty);
            Assert.That(after, Is.Empty);
            Assert.That(around.Select(m => m.Id), Is.EqualTo(new[] { only.Id }),
                "`around` still has the anchor to return");
        });
    }

    [Test]
    public async Task ASingleRowRangePagesToExactlyThatRow()
    {
        var contextId = NewContextId();
        var older = await SeedAsync(contextId, Now - TimeSpan.FromMinutes(1));
        var newer = await SeedAsync(contextId, Now);

        var before = await PageAsync(contextId, newer.Id, MessageCursorDirection.Before, 10);
        var after = await PageAsync(contextId, older.Id, MessageCursorDirection.After, 10);

        Assert.Multiple(() =>
        {
            Assert.That(before.Select(m => m.Id), Is.EqualTo(new[] { older.Id }));
            Assert.That(after.Select(m => m.Id), Is.EqualTo(new[] { newer.Id }));
        });
    }

    [Test]
    public async Task AnAnchorFromAnotherPartitionPagesNothing()
    {
        // The partition key is the only thing scoping the read; an anchor resolved through the
        // message_id secondary index could otherwise page a conversation the caller never named.
        var mine = NewContextId();
        var elsewhere = NewContextId();

        await SeedInstantGroupAsync(mine, Now, 3);
        var stranger = await SeedAsync(elsewhere, Now);

        var page = await PageAsync(mine, stranger.Id, MessageCursorDirection.Before, 10);

        Assert.That(page, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The deprecated offset overloads, which had the same boundary defect
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task OffsetPagingASameMillisecondGroupReachesEveryMessageExactlyOnce()
    {
        // GetMessagesByContextIdAsync reads `skip + take` rows and drops `skip`.
        var contextId = NewContextId();
        var group = await SeedInstantGroupAsync(contextId, Now, 7);

        var seen = new List<string>();
        for (var page = 0; page < 4; page++)
        {
            var (rows, _) = await _repo.GetMessagesByContextIdAsync(contextId, take: 2, skip: page * 2);
            seen.AddRange(rows.Select(r => r.Id));
        }

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique);
            Assert.That(seen, Is.EquivalentTo(group.Select(m => m.Id)));
        });
    }

    [Test]
    public async Task AnOffsetPageComesBackOrderedByCreatedAtThenIdAscending()
    {
        var contextId = NewContextId();
        var group = await SeedInstantGroupAsync(contextId, Now, 5);

        var (rows, _) = await _repo.GetMessagesByContextIdAsync(contextId, take: 3, skip: 0);

        // Newest three of the group by the total order, handed back oldest-first.
        Assert.That(rows.Select(m => m.Id), Is.EqualTo(new[] { group[2].Id, group[3].Id, group[4].Id }));
    }

    [Test]
    public async Task AZeroLimitReadsNothingRatherThanIssuingLimitZero()
    {
        // LIMIT 0 is a hard error in CQL, not an empty page - so this has to be caught before any
        // statement is built, not clamped inside one.
        var contextId = NewContextId();
        var group = await SeedInstantGroupAsync(contextId, Now, 3);

        var page = await PageAsync(contextId, group[2].Id, MessageCursorDirection.Before, 0);

        Assert.That(page, Is.Empty);
    }
}
