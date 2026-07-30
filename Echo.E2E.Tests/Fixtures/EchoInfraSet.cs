using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Echo.E2E.Tests.Fixtures;

/// <summary>One independent trio of Postgres/RabbitMQ/Redis containers.</summary>
public sealed class EchoInfraSet : IAsyncDisposable
{
    public const string RabbitMqUser = "admin";
    public const string RabbitMqPassword = "admin";
    public const string RedisPassword = "devpassword";

    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitMq;
    private readonly RedisContainer _redis;

    public string PostgresHost { get; private set; } = null!;
    public int PostgresPort { get; private set; }
    public string RabbitMqHost { get; private set; } = null!;
    public int RabbitMqPort { get; private set; }
    public string RedisHost { get; private set; } = null!;
    public int RedisPort { get; private set; }

    private EchoInfraSet(
        PostgreSqlContainer postgres, RabbitMqContainer rabbitMq, RedisContainer redis)
    {
        _postgres = postgres;
        _rabbitMq = rabbitMq;
        _redis = redis;
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

        var set = new EchoInfraSet(postgres, rabbitMq, redis);

        // A bounded timeout here beats a silent multi-minute hang if a wait strategy ever
        // misbehaves.
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync(), redis.StartAsync())
            .WaitAsync(TimeSpan.FromMinutes(3));

        set.PostgresHost = postgres.Hostname;
        set.PostgresPort = postgres.GetMappedPublicPort(5432);
        set.RabbitMqHost = rabbitMq.Hostname;
        set.RabbitMqPort = rabbitMq.GetMappedPublicPort(5672);
        set.RedisHost = redis.Hostname;
        set.RedisPort = redis.GetMappedPublicPort(6379);

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
            _redis.DisposeAsync().AsTask());
    }
}
