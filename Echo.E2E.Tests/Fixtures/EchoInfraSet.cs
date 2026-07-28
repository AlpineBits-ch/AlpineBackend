using Npgsql;
using Testcontainers.Cassandra;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Echo.E2E.Tests.Fixtures;

/// <summary>
/// One independent trio of Postgres/RabbitMQ/Redis containers. A single-instance test uses one
/// shared <see cref="EchoInfraSet"/> (see <see cref="EchoInfraFixture.Default"/>) for all five
/// services. Two-instance federation tests provision a second, fully independent
/// <see cref="EchoInfraSet"/> instead of trying to namespace a shared broker/database - two real
/// federated Echo deployments would never share infra either, so this keeps the harness honest
/// and makes it trivial to simulate one instance going down without affecting the other.
/// </summary>
public sealed class EchoInfraSet : IAsyncDisposable
{
    public const string RabbitMqUser = "admin";
    public const string RabbitMqPassword = "admin";
    public const string RedisPassword = "devpassword";

    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitMq;
    private readonly RedisContainer _redis;
    private readonly CassandraContainer _scylla;

    public string PostgresHost { get; private set; } = null!;
    public int PostgresPort { get; private set; }
    public string RabbitMqHost { get; private set; } = null!;
    public int RabbitMqPort { get; private set; }
    public string RedisHost { get; private set; } = null!;
    public int RedisPort { get; private set; }
    public string ScyllaHost { get; private set; } = null!;
    public int ScyllaPort { get; private set; }

    private EchoInfraSet(
        PostgreSqlContainer postgres, RabbitMqContainer rabbitMq, RedisContainer redis, CassandraContainer scylla)
    {
        _postgres = postgres;
        _rabbitMq = rabbitMq;
        _redis = redis;
        _scylla = scylla;
    }

    public static async Task<EchoInfraSet> StartAsync()
    {
        var postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        var rabbitMq = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithUsername(RabbitMqUser)
            .WithPassword(RabbitMqPassword)
            .Build();

        var redis = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithCommand("redis-server", "--requirepass", RedisPassword)
            .Build();

        // Messaging.Application connects to Scylla unconditionally at startup (there's no
        // in-memory/skip fallback), so this is needed just to get the service to boot, not only
        // for message-store scenarios. Scylla speaks the Cassandra native (CQL) protocol, so the
        // official Cassandra Testcontainers module works against the real scylladb/scylla image
        // - it just needs the image overridden.
        var scylla = new CassandraBuilder()
            .WithImage("scylladb/scylla:5.4")
            .WithCommand("--smp", "1", "--memory", "750M", "--overprovisioned", "1")
            .Build();

        var set = new EchoInfraSet(postgres, rabbitMq, redis, scylla);

        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync(), redis.StartAsync(), scylla.StartAsync());

        set.PostgresHost = postgres.Hostname;
        set.PostgresPort = postgres.GetMappedPublicPort(5432);
        set.RabbitMqHost = rabbitMq.Hostname;
        set.RabbitMqPort = rabbitMq.GetMappedPublicPort(5672);
        set.RedisHost = redis.Hostname;
        set.RedisPort = redis.GetMappedPublicPort(6379);
        set.ScyllaHost = scylla.Hostname;
        set.ScyllaPort = scylla.GetMappedPublicPort(9042);

        return set;
    }

    public async Task CreateDatabasesAsync(params string[] databaseNames)
    {
        var adminConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = PostgresHost,
            Port = PostgresPort,
            Database = "postgres",
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        foreach (var database in databaseNames)
        {
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _rabbitMq.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _scylla.DisposeAsync().AsTask());
    }
}
