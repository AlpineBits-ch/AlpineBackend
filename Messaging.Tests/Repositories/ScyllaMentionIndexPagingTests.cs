using Cassandra;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;

namespace Messaging.Tests.Repositories;

/// <summary>
/// Mention-tab paging over Scylla, <b>against a real node</b> - the third member of the family that
/// <c>ScyllaDmRetentionRangeDeleteTests</c> and <c>ScyllaCursorPagingTests</c> belong to, and broken
/// the same way for the same reason.
///
/// <para><c>user_mentions</c> is clustered <c>(created_at DESC, message_id ASC)</c>. The page cursor
/// used to be the CQL row tuple <c>(created_at, message_id) &lt; (?, ?)</c>, which is
/// ascending-lexicographic on both components, against a scan whose tie-break runs the other way -
/// so a page cap landing inside a same-millisecond group kept the members furthest from the cursor,
/// and the next page re-served rows the user had already seen while permanently skipping the ones
/// between. Two mentions in one millisecond is all it takes, which one @here fan-out or one bulk
/// import produces routinely.</para>
///
/// <para><b>A fake mapper cannot be asked any of this.</b> It has no clustering order, so a statement
/// that returns the wrong rows and one that returns the right rows look identical to it. Skipped,
/// loudly and by name, when <c>ECHO_TEST_SCYLLA</c> is unset (<c>host:port</c>), so CI without the
/// container still passes.</para>
/// </summary>
[TestFixture]
[Category("Scylla")]
public class ScyllaMentionIndexPagingTests
{
    private static string? ContactPoint => Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA");

    private ICluster _cluster = null!;
    private ISession _session = null!;
    private ScyllaContext _context = null!;
    private ScyllaMentionIndexRepository _repo = null!;
    private string _keyspace = null!;

    /// <summary>Millisecond-truncated: Cassandra timestamps are millisecond-resolution, so a seed
    /// with sub-millisecond ticks does not read back equal to what was written.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (string.IsNullOrWhiteSpace(ContactPoint))
        {
            Assert.Ignore(
                "Set ECHO_TEST_SCYLLA to a reachable node (host or host:port, e.g. localhost:9042) to run the "
                + "mention-index paging tests. They are deliberately not run against FakeCassandraMapper: the "
                + "defect they exist to catch is a disagreement between a WHERE relation and the cluster's scan "
                + "order, and a fake has no scan order to disagree with.");
        }

        var parts = ContactPoint!.Split(':', 2);
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 9042;

        var builder = Cluster.Builder()
            .AddContactPoint(host)
            .WithPort(port)
            .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(20_000))
            .WithQueryTimeout(30_000);

        var user = Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA_USERNAME");
        var password = Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA_PASSWORD");
        if (!string.IsNullOrWhiteSpace(user)) builder = builder.WithCredentials(user, password ?? string.Empty);

        _cluster = builder.Build();
        _session = await _cluster.ConnectAsync();

        // Short prefix on purpose: Cassandra caps keyspace names at 48 characters and the suffix is
        // already 32.
        _keyspace = "echo_ment_" + Guid.NewGuid().ToString("N");
        _session.CreateKeyspaceIfNotExists(_keyspace, new Dictionary<string, string>
        {
            { "class", "SimpleStrategy" },
            { "replication_factor", "1" },
        });
        _session.ChangeKeyspace(_keyspace);

        // The real schema and the real column map - see ScyllaContext.CreateForSessionAsync.
        _context = await ScyllaContext.CreateForSessionAsync(_session);
        _repo = new ScyllaMentionIndexRepository(_context);
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

    /// <summary>One partition per test - every read here is partition-scoped, so tests cannot see
    /// each other's rows.</summary>
    private static string NewUserId() => "user-" + Guid.NewGuid().ToString("N");

    private async Task<string> SeedAsync(string userId, DateTimeOffset at)
    {
        var messageId = Message.GenerateId();
        await _repo.AddAsync([new UserMention
        {
            UserId = userId,
            CreatedAt = at,
            MessageId = messageId,
            ContextId = "conv-mentions",
            AuthorId = "user-author",
        }]);

        return messageId;
    }

    /// <summary>A same-millisecond group, returned ordinal-ascending by message id - the order the
    /// clustering key puts it in, and the order the assertions are written against.</summary>
    private async Task<List<string>> SeedGroupAsync(string userId, DateTimeOffset at, int count)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++) ids.Add(await SeedAsync(userId, at));

        return ids.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Pages the tab to exhaustion the way the client does: the last row of each page
    /// becomes the next page's <c>Before</c> cursor.</summary>
    private async Task<List<string>> PageAllAsync(string userId, int pageSize, int maxPages = 20)
    {
        var seen = new List<string>();
        DateTimeOffset? before = null;
        string? beforeId = null;

        for (var page = 0; page < maxPages; page++)
        {
            var rows = await _repo.GetPageAsync(new MentionPageQuery
            {
                UserId = userId,
                Before = before,
                BeforeMessageId = beforeId,
                Limit = pageSize,
            });

            if (rows.Count == 0) break;

            seen.AddRange(rows.Select(r => r.MessageId));
            before = rows[^1].CreatedAt;
            beforeId = rows[^1].MessageId;
        }

        return seen;
    }

    [Test]
    public async Task TheOriginalRowTupleCursorIsAcceptedByTheClusterAndSilentlyLosesRows()
    {
        // The finding, pinned so that restoring the tuple cursor fails here rather than in a user's
        // mention tab. The natural scan is (created_at DESC, message_id ASC); the relation is
        // ascending-lexicographic; inside one millisecond they point opposite ways.
        var userId = NewUserId();
        var group = await SeedGroupAsync(userId, Now - TimeSpan.FromMinutes(5), 4);

        const string originalCql =
            "WHERE user_id = ? AND (created_at, message_id) < (?, ?) LIMIT ?";

        var firstPage = (await _context.Mapper.FetchAsync<UserMention>(
            originalCql, userId, Now - TimeSpan.FromMinutes(5), group[3], 2)).ToList();

        var resumed = (await _context.Mapper.FetchAsync<UserMention>(
            originalCql, userId, firstPage[^1].CreatedAt, firstPage[^1].MessageId, 10)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Select(m => m.MessageId), Is.EqualTo(new[] { group[0], group[1] }),
                "the scan's tie-break is message_id ASC, so LIMIT keeps the members furthest from the cursor");
            Assert.That(resumed.Select(m => m.MessageId), Does.Not.Contain(group[2]),
                "group[2] sits between the cursor and the page returned, and is now unreachable forever");
            Assert.That(resumed.Select(m => m.MessageId), Is.EqualTo(new[] { group[0] }),
                "while the row it does return was already on the previous page");
        });
    }

    [Test]
    public async Task PagingASameMillisecondGroupReachesEveryMentionExactlyOnce()
    {
        var userId = NewUserId();
        var group = await SeedGroupAsync(userId, Now - TimeSpan.FromMinutes(5), 7);

        var seen = await PageAllAsync(userId, pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique, "a mention was served twice while paging");
            Assert.That(seen, Is.EquivalentTo(group), "a mention past the page boundary was never reached");
        });
    }

    [Test]
    public async Task PagingAcrossAMillisecondBoundaryReachesEveryMention()
    {
        var userId = NewUserId();
        var newest = await SeedAsync(userId, Now);
        var middle = await SeedGroupAsync(userId, Now - TimeSpan.FromMinutes(1), 5);
        var oldest = await SeedAsync(userId, Now - TimeSpan.FromMinutes(2));

        var seen = await PageAllAsync(userId, pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique);
            Assert.That(seen, Is.EquivalentTo(middle.Append(newest).Append(oldest)));
        });
    }

    [Test]
    public async Task APageComesBackNewestFirstWithTheTieBreakDescendingLikeTheRelationalBackend()
    {
        // EfCoreMentionIndexRepository orders (CreatedAt DESC, MessageId DESC). This backend used to
        // hand back raw scan order - (created_at DESC, message_id ASC) - so the two disagreed about
        // every pair of mentions sharing a millisecond, and a self-hosted deployment's mention tab
        // was ordered differently from a hosted one's.
        var userId = NewUserId();
        var older = await SeedGroupAsync(userId, Now - TimeSpan.FromMinutes(1), 3);
        var newer = await SeedGroupAsync(userId, Now, 3);

        var page = await _repo.GetPageAsync(new MentionPageQuery { UserId = userId, Limit = 10 });

        var expected = newer.AsEnumerable().Reverse().Concat(older.AsEnumerable().Reverse());
        Assert.That(page.Select(m => m.MessageId), Is.EqualTo(expected));
    }

    [Test]
    public async Task APageIsNeverLargerThanTheLimitEvenWhenItEndsInsideAGroup()
    {
        var userId = NewUserId();
        await SeedGroupAsync(userId, Now, 9);

        var page = await _repo.GetPageAsync(new MentionPageQuery { UserId = userId, Limit = 3 });

        Assert.That(page, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ASinceBoundExcludesOlderMentionsIncludingOnesSharingTheCursorMillisecond()
    {
        // The cursor's own millisecond is now read by an equality query that the Since bound is not
        // part of, so the bound has to be re-applied to those rows - otherwise a stale cursor drags
        // rows from outside the requested window back into the page.
        var userId = NewUserId();
        var inWindow = await SeedAsync(userId, Now);
        var outOfWindow = await SeedAsync(userId, Now - TimeSpan.FromDays(10));

        var page = await _repo.GetPageAsync(new MentionPageQuery
        {
            UserId = userId,
            Since = Now - TimeSpan.FromDays(1),
            Limit = 10,
        });

        Assert.That(page.Select(m => m.MessageId), Is.EqualTo(new[] { inWindow }),
            $"the mention at {outOfWindow} is older than the requested window");
    }

    [Test]
    public async Task AnEmptyPartitionAndASingleRowPartitionBothBehave()
    {
        var empty = NewUserId();
        var single = NewUserId();
        var only = await SeedAsync(single, Now);

        var emptyPage = await _repo.GetPageAsync(new MentionPageQuery { UserId = empty, Limit = 10 });
        var singlePage = await _repo.GetPageAsync(new MentionPageQuery { UserId = single, Limit = 10 });
        var afterSingle = await _repo.GetPageAsync(new MentionPageQuery
        {
            UserId = single,
            Before = singlePage[0].CreatedAt,
            BeforeMessageId = singlePage[0].MessageId,
            Limit = 10,
        });

        Assert.Multiple(() =>
        {
            Assert.That(emptyPage, Is.Empty);
            Assert.That(singlePage.Select(m => m.MessageId), Is.EqualTo(new[] { only }));
            Assert.That(afterSingle, Is.Empty, "paging past the only row must end rather than repeat it");
        });
    }

    [Test]
    public async Task AZeroLimitIsClampedRatherThanIssuedAsLimitZero()
    {
        // LIMIT 0 is a hard error in CQL, not an empty page.
        var userId = NewUserId();
        await SeedGroupAsync(userId, Now, 3);

        var page = await _repo.GetPageAsync(new MentionPageQuery { UserId = userId, Limit = 0 });

        Assert.That(page, Has.Count.EqualTo(1));
    }
}
