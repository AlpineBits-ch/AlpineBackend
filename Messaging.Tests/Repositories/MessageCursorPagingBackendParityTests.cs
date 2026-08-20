using Cassandra;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Messaging.Tests.Repositories;

/// <summary>
/// The two <see cref="IMessageRepository"/> backends, seeded with byte-identical rows, asked the
/// same cursor-page question, and required to answer it identically - against a real Scylla node
/// and a real Postgres database.
/// </summary>
[TestFixture]
[Category("Scylla")]
[Category("Postgres")]
public class MessageCursorPagingBackendParityTests
{
    private static string? ScyllaContactPoint => Environment.GetEnvironmentVariable("ECHO_TEST_SCYLLA");

    private static string? MaintenanceConnectionString => Environment.GetEnvironmentVariable("ECHO_TEST_POSTGRES");

    private ICluster _cluster = null!;
    private ISession _session = null!;
    private ScyllaContext _scyllaContext = null!;
    private ScyllaMessageRepository _scylla = null!;
    private string _keyspace = null!;

    private string _database = null!;
    private string _testConnectionString = null!;
    private MicroserviceContext _efContext = null!;
    private EfCoreMessageRepository _ef = null!;

    /// <summary>Millisecond-truncated, and UTC: Cassandra timestamps are millisecond-resolution, and
    /// a seed with sub-millisecond ticks reads back from Scylla rounded and from Postgres exact -
    /// which would make every assertion here about storage precision instead of about paging.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (string.IsNullOrWhiteSpace(ScyllaContactPoint) || string.IsNullOrWhiteSpace(MaintenanceConnectionString))
        {
            Assert.Ignore(
                "Set BOTH ECHO_TEST_SCYLLA (host or host:port, e.g. localhost:9042) and ECHO_TEST_POSTGRES "
                + "(e.g. Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres) to run "
                + "the cursor-paging backend-parity tests. Currently missing: "
                + (string.IsNullOrWhiteSpace(ScyllaContactPoint) ? "ECHO_TEST_SCYLLA " : "")
                + (string.IsNullOrWhiteSpace(MaintenanceConnectionString) ? "ECHO_TEST_POSTGRES" : "")
                + ". Neither backend may be substituted: the question is whether a real cluster's clustering "
                + "order and a real database's collation break a same-millisecond tie the same way, and a fake "
                + "mapper and the InMemory provider have neither.");
        }

        await SetUpScyllaAsync();
        await SetUpPostgresAsync();
    }

    private async Task SetUpScyllaAsync()
    {
        var parts = ScyllaContactPoint!.Split(':', 2);
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
        _keyspace = "echo_parity_" + Guid.NewGuid().ToString("N");
        _session.CreateKeyspaceIfNotExists(_keyspace, new Dictionary<string, string>
        {
            { "class", "SimpleStrategy" },
            { "replication_factor", "1" },
        });
        _session.ChangeKeyspace(_keyspace);

        // The real schema and the real column map - see ScyllaContext.CreateForSessionAsync.
        _scyllaContext = await ScyllaContext.CreateForSessionAsync(_session);
        _scylla = new ScyllaMessageRepository(_scyllaContext);
    }

    private async Task SetUpPostgresAsync()
    {
        _database = "echo_parity_" + Guid.NewGuid().ToString("N");
        _testConnectionString = new NpgsqlConnectionStringBuilder(MaintenanceConnectionString)
        {
            Database = _database,
        }.ConnectionString;

        await ExecuteOnMaintenanceAsync($"CREATE DATABASE \"{_database}\";");

        _efContext = NewEfContext();
        await _efContext.Database.EnsureCreatedAsync();
        _ef = new EfCoreMessageRepository(_efContext);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_session is not null)
        {
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

        if (_efContext is null) return;

        await _efContext.DisposeAsync();

        // Npgsql pools connections, so a disposed context can still hold an open backend against the
        // database being dropped; WITH (FORCE) handles the rest. Postgres 13+.
        NpgsqlConnection.ClearAllPools();
        await ExecuteOnMaintenanceAsync($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE);");
    }

    /// <summary>
    /// The production model against a throwaway database - the real column types and the real
    /// collations, not a rebuilt approximation of them.
    /// </summary>
    private MicroserviceContext NewEfContext()
    {
        var builder = new DbContextOptionsBuilder<MicroserviceContext>();
        builder.UseNpgsql(_testConnectionString, options =>
        {
            options.MapEnum<ChannelEncryptionState>();
            options.MapEnum<MessageType>();
            options.MapEnum<AttachmentState>();
            options.MapEnum<AuthorIdType>();
            options.MapEnum<MessagePartType>();
            options.MapEnum<MessageEncryptionState>();
            options.MapEnum<MlsGenerationState>();
            options.MapEnum<MlsJoinRequestState>();
        }).UseSnakeCaseNamingConvention();

        return new ParityMessagingContext(builder.Options);
    }

    private static async Task ExecuteOnMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ParityMessagingContext(DbContextOptions<MicroserviceContext> options)
        : MicroserviceContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Provider already supplied via options; calling base would read the environment's
            // connection string and point this fixture at the real development database.
        }
    }

    private static string NewContextId() => "conv-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Writes the same row to both stores - same id, same timestamp, same partition.
    /// </summary>
    private async Task<string> SeedBothAsync(string contextId, DateTimeOffset at)
    {
        var id = Message.GenerateId();

        await _scylla.CreateMessageAsync(Build(id, contextId, at));
        await _ef.CreateMessageAsync(Build(id, contextId, at));
        await _efContext.SaveChangesAsync();
        _efContext.ChangeTracker.Clear();

        return id;

        static Message Build(string id, string contextId, DateTimeOffset at) => new()
        {
            Id = id,
            ContextId = contextId,
            ConversationId = contextId,
            AuthorId = "user-parity",
            Content = "hello"u8.ToArray(),
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    /// <summary>A same-millisecond group, returned ordinal-ascending by id - the order the Scylla
    /// clustering key puts it in, and the order the assertions are written against.</summary>
    private async Task<List<string>> SeedGroupBothAsync(string contextId, DateTimeOffset at, int count)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++) ids.Add(await SeedBothAsync(contextId, at));

        return ids.Order(StringComparer.Ordinal).ToList();
    }

    private static MessagePageQuery Query(string contextId, string? anchorId, MessageCursorDirection direction, int limit) =>
        new() { ContextId = contextId, AnchorMessageId = anchorId, Direction = direction, Limit = limit };

    private async Task<(List<string> Scylla, List<string> Ef)> BothPagesAsync(
        string contextId, string? anchorId, MessageCursorDirection direction, int limit)
    {
        var query = Query(contextId, anchorId, direction, limit);

        var (scyllaPage, _) = await _scylla.GetMessagePageByCursorAsync(query);
        var (efPage, _) = await _ef.GetMessagePageByCursorAsync(query);

        return (scyllaPage.Select(m => m.Id).ToList(), efPage.Select(m => m.Id).ToList());
    }

    /// <summary>Scrolls one backend to exhaustion the way a client does: each page's far edge becomes
    /// the next page's anchor.</summary>
    private static async Task<List<string>> ScrollAsync(
        IMessageRepository repo, string contextId, string startAnchorId,
        MessageCursorDirection direction, int pageSize, int maxPages = 20)
    {
        var seen = new List<string>();
        var anchorId = startAnchorId;

        for (var page = 0; page < maxPages; page++)
        {
            var (rows, _) = await repo.GetMessagePageByCursorAsync(
                Query(contextId, anchorId, direction, pageSize));

            var ids = rows.Select(m => m.Id).ToList();
            if (ids.Count == 0) break;

            seen.AddRange(ids);
            anchorId = direction == MessageCursorDirection.Before ? ids[0] : ids[^1];
        }

        return seen;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The tie-break, which is the only place the two can disagree
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task BothBackendsBreakASameMillisecondTieTheSameWayScrollingBack()
    {
        // A page cap inside a same-millisecond group: Scylla decides which members are "nearest"
        // with an ordinal comparison in process, Postgres with ORDER BY on a collated text column.
        var contextId = NewContextId();
        var group = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(5), 6);

        var (scylla, ef) = await BothPagesAsync(contextId, group[5], MessageCursorDirection.Before, 3);

        Assert.Multiple(() =>
        {
            Assert.That(scylla, Is.EqualTo(ef), "the two backends returned different pages for one query");
            Assert.That(scylla, Is.EqualTo(new[] { group[2], group[3], group[4] }),
                "and both must return the three immediately before the anchor, oldest-first");
        });
    }

    [Test]
    public async Task BothBackendsStartAtTheSamePlaceWithNoAnchor()
    {
        // Read-from-the-start has no anchor to sit next to, so the two backends have to agree on
        // what "the beginning" is, including inside a same-millisecond group.
        var contextId = NewContextId();
        var oldest = await SeedBothAsync(contextId, Now - TimeSpan.FromMinutes(9));
        var group = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(5), 4);

        var (scylla, ef) = await BothPagesAsync(contextId, null, MessageCursorDirection.After, 3);

        Assert.Multiple(() =>
        {
            Assert.That(scylla, Is.EqualTo(ef), "the two backends disagreed about the beginning");
            Assert.That(scylla, Is.EqualTo(new[] { oldest, group[0], group[1] }));
        });
    }

    [Test]
    public async Task BothBackendsScrollForwardFromNoAnchorIdentically()
    {
        var contextId = NewContextId();
        var oldest = await SeedBothAsync(contextId, Now - TimeSpan.FromMinutes(3));
        var middle = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(2), 4);
        var newest = await SeedBothAsync(contextId, Now);

        var (scyllaFirst, efFirst) = await BothPagesAsync(contextId, null, MessageCursorDirection.After, 2);

        // The client's second page: the first page's far edge becomes the anchor.
        var (scyllaNext, efNext) = await BothPagesAsync(
            contextId, scyllaFirst[^1], MessageCursorDirection.After, 4);

        Assert.Multiple(() =>
        {
            Assert.That(scyllaFirst, Is.EqualTo(efFirst));
            Assert.That(scyllaNext, Is.EqualTo(efNext));
            Assert.That(scyllaFirst.Concat(scyllaNext), Is.Unique);
            Assert.That(scyllaFirst.Concat(scyllaNext),
                Is.EqualTo(new[] { oldest }.Concat(middle).Append(newest)));
        });
    }

    [Test]
    public async Task BothBackendsRefuseANullAnchorRunningBackwards()
    {
        var contextId = NewContextId();
        await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(5), 3);

        var (scylla, ef) = await BothPagesAsync(contextId, null, MessageCursorDirection.Before, 3);

        Assert.Multiple(() =>
        {
            Assert.That(scylla, Is.Empty, "there is nothing before the beginning");
            Assert.That(ef, Is.Empty);
        });
    }

    [Test]
    public async Task BothBackendsBreakASameMillisecondTieTheSameWayScrollingForward()
    {
        var contextId = NewContextId();
        var group = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(5), 6);

        var (scylla, ef) = await BothPagesAsync(contextId, group[0], MessageCursorDirection.After, 3);

        Assert.Multiple(() =>
        {
            Assert.That(scylla, Is.EqualTo(ef));
            Assert.That(scylla, Is.EqualTo(new[] { group[1], group[2], group[3] }));
        });
    }

    [Test]
    public async Task ScrollingEitherBackendToExhaustionVisitsTheSameMessagesInTheSameOrder()
    {
        // The end-to-end form: a client that switched deployments mid-scroll must not see a message
        // twice or miss one.
        var contextId = NewContextId();
        var oldest = await SeedBothAsync(contextId, Now - TimeSpan.FromMinutes(3));
        var lower = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(2), 4);
        var upper = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(1), 4);
        var newest = await SeedBothAsync(contextId, Now);

        var scyllaBack = await ScrollAsync(_scylla, contextId, newest, MessageCursorDirection.Before, 2);
        var efBack = await ScrollAsync(_ef, contextId, newest, MessageCursorDirection.Before, 2);

        var scyllaForward = await ScrollAsync(_scylla, contextId, oldest, MessageCursorDirection.After, 2);
        var efForward = await ScrollAsync(_ef, contextId, oldest, MessageCursorDirection.After, 2);

        var everythingButNewest = lower.Concat(upper).Append(oldest);
        var everythingButOldest = lower.Concat(upper).Append(newest);

        Assert.Multiple(() =>
        {
            Assert.That(scyllaBack, Is.EqualTo(efBack), "the backends diverged while scrolling back");
            Assert.That(scyllaForward, Is.EqualTo(efForward), "the backends diverged while scrolling forward");
            Assert.That(scyllaBack, Is.Unique);
            Assert.That(scyllaForward, Is.Unique);
            Assert.That(scyllaBack, Is.EquivalentTo(everythingButNewest));
            Assert.That(scyllaForward, Is.EquivalentTo(everythingButOldest));
        });
    }

    [Test]
    public async Task BothBackendsReturnTheSameAroundPage()
    {
        var contextId = NewContextId();
        var group = await SeedGroupBothAsync(contextId, Now - TimeSpan.FromMinutes(5), 7);

        var (scylla, ef) = await BothPagesAsync(contextId, group[3], MessageCursorDirection.Around, 5);

        Assert.Multiple(() =>
        {
            Assert.That(scylla, Is.EqualTo(ef));
            Assert.That(scylla, Is.EqualTo(new[] { group[1], group[2], group[3], group[4], group[5] }));
        });
    }

    [Test]
    public async Task BothBackendsAgreeOnAnEmptyRangeAndOnASingleRowRange()
    {
        var contextId = NewContextId();
        var older = await SeedBothAsync(contextId, Now - TimeSpan.FromMinutes(1));
        var newer = await SeedBothAsync(contextId, Now);

        var (emptyScylla, emptyEf) = await BothPagesAsync(contextId, older, MessageCursorDirection.Before, 10);
        var (oneScylla, oneEf) = await BothPagesAsync(contextId, newer, MessageCursorDirection.Before, 10);

        Assert.Multiple(() =>
        {
            Assert.That(emptyScylla, Is.Empty);
            Assert.That(emptyEf, Is.Empty);
            Assert.That(oneScylla, Is.EqualTo(oneEf));
            Assert.That(oneScylla, Is.EqualTo(new[] { older }));
        });
    }

    [Test]
    public async Task BothBackendsReturnTheSameOffsetPages()
    {
        // The deprecated (take, skip) overloads.
        var contextId = NewContextId();
        var group = await SeedGroupBothAsync(contextId, Now, 7);

        var scyllaSeen = new List<string>();
        var efSeen = new List<string>();

        for (var page = 0; page < 4; page++)
        {
            var (scyllaRows, _) = await _scylla.GetMessagesByContextIdAsync(contextId, take: 2, skip: page * 2);
            var (efRows, _) = await _ef.GetMessagesByContextIdAsync(contextId, take: 2, skip: page * 2);

            scyllaSeen.AddRange(scyllaRows.Select(m => m.Id));
            efSeen.AddRange(efRows.Select(m => m.Id));
        }

        Assert.Multiple(() =>
        {
            Assert.That(scyllaSeen, Is.EqualTo(efSeen), "the backends paged the same partition differently");
            Assert.That(scyllaSeen, Is.Unique);
            Assert.That(scyllaSeen, Is.EquivalentTo(group));
        });
    }

    [Test]
    public async Task PostgresOrdersMessageIdsTheSameWayScyllasClusteringKeyDoes()
    {
        // The assumption underneath every assertion above, isolated so that a deployment onto a
        // database with a different LC_COLLATE fails here, with a name that says what is wrong,
        // rather than as a mystery off-by-one in someone's scrollback.
        var contextId = NewContextId();
        var ids = await SeedGroupBothAsync(contextId, Now, 12);

        var fromPostgres = await _efContext.Messages
            .AsNoTracking()
            .Where(m => m.ContextId == contextId)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync();

        Assert.That(fromPostgres, Is.EqualTo(ids),
            "Postgres' collation put message ids in a different order than an ordinal comparison does, "
            + "which is the order the Scylla clustering key uses - cursor paging cannot agree across "
            + "the two backends on this database");
    }
}
