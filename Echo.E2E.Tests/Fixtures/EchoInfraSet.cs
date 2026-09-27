using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Echo.E2E.Tests.Fixtures;

/// <summary>One independent set of Postgres/RabbitMQ/Redis/SeaweedFS containers.</summary>
public sealed class EchoInfraSet : IAsyncDisposable
{
    public const string RabbitMqUser = "admin";
    public const string RabbitMqPassword = "admin";
    public const string RedisPassword = "devpassword";

    public const string ObjectStorageAccessKey = "e2eaccess";
    public const string ObjectStorageSecretKey = "e2esecret";

    /// <summary>Matches <c>Env.StorageConfiguration.BucketName</c>'s default so nothing has to be
    /// overridden twice.</summary>
    public const string ObjectStorageBucket = "echo-chat";

    private const int ObjectStoragePort = 8333;

    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitMq;
    private readonly RedisContainer _redis;
    private readonly IContainer _objectStorage;

    public string PostgresHost { get; private set; } = null!;
    public int PostgresPort { get; private set; }
    public string RabbitMqHost { get; private set; } = null!;
    public int RabbitMqPort { get; private set; }
    public string RedisHost { get; private set; } = null!;
    public int RedisPort { get; private set; }

    /// <summary>
    /// The S3-compatible endpoint, as both the spawned services and this test process see it.
    /// </summary>
    public string ObjectStorageUrl { get; private set; } = null!;

    private EchoInfraSet(
        PostgreSqlContainer postgres, RabbitMqContainer rabbitMq, RedisContainer redis, IContainer objectStorage)
    {
        _postgres = postgres;
        _rabbitMq = rabbitMq;
        _redis = redis;
        _objectStorage = objectStorage;
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

        // SeaweedFS turns the AWS_* pair into its admin identity; without it the S3 API is anonymous.
        var objectStorage = new ContainerBuilder()
            .WithImage("chrislusf/seaweedfs:4.47")
            .WithEnvironment("AWS_ACCESS_KEY_ID", ObjectStorageAccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", ObjectStorageSecretKey)
            .WithCommand("mini", "-dir=/data", $"-s3.port={ObjectStoragePort}", "-master.telemetry=false")
            .WithPortBinding(ObjectStoragePort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/healthz").ForPort(ObjectStoragePort)))
            .Build();

        var set = new EchoInfraSet(postgres, rabbitMq, redis, objectStorage);

        // A bounded timeout here beats a silent multi-minute hang if a wait strategy ever
        // misbehaves.
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync(), redis.StartAsync(), objectStorage.StartAsync())
            .WaitAsync(TimeSpan.FromMinutes(3));

        set.PostgresHost = postgres.Hostname;
        set.PostgresPort = postgres.GetMappedPublicPort(5432);
        set.RabbitMqHost = rabbitMq.Hostname;
        set.RabbitMqPort = rabbitMq.GetMappedPublicPort(5672);
        set.RedisHost = redis.Hostname;
        set.RedisPort = redis.GetMappedPublicPort(6379);
        set.ObjectStorageUrl = $"http://{objectStorage.Hostname}:{objectStorage.GetMappedPublicPort(ObjectStoragePort)}";

        // Provisioned here rather than by the service that uses it: nothing in Echo creates its own
        // bucket (compose.yaml and the real deployments both assume one exists), and a harness that
        // created it from application code would be testing something the product does not do.
        await set.CreateObjectStorageBucketAsync();

        return set;
    }

    private async Task CreateObjectStorageBucketAsync()
    {
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(ObjectStorageAccessKey, ObjectStorageSecretKey),
            new AmazonS3Config
            {
                ServiceURL = ObjectStorageUrl,
                ForcePathStyle = true,
                // Same two settings AppEnvironment.StorageInstance applies to the real client, since
                // GCS's S3-interop API rejects the SDK's default flexible-checksum trailer.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = ObjectStorageBucket });
    }

    /// <summary>Creates databases that do not already exist.</summary>
    public async Task EnsureDatabasesAsync(params string[] databaseNames)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString());
        await connection.OpenAsync();

        foreach (var database in databaseNames)
        {
            await using var exists = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @name", connection);
            exists.Parameters.AddWithValue("name", database);
            if (await exists.ExecuteScalarAsync() is not null) continue;

            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }

    private string AdminConnectionString() => new NpgsqlConnectionStringBuilder
    {
        Host = PostgresHost,
        Port = PostgresPort,
        Database = "postgres",
        Username = "postgres",
        Password = "postgres",
    }.ConnectionString;

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
            _objectStorage.DisposeAsync().AsTask());
    }
}
