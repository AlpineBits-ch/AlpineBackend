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

/// <summary>One independent set of Postgres/RabbitMQ/Redis/MinIO containers.</summary>
public sealed class EchoInfraSet : IAsyncDisposable
{
    public const string RabbitMqUser = "admin";
    public const string RabbitMqPassword = "admin";
    public const string RedisPassword = "devpassword";

    /// <summary>MinIO's own floor is eight characters for the root password; the access key has no
    /// such rule but is kept symmetric with it.</summary>
    public const string ObjectStorageAccessKey = "minioadmin";
    public const string ObjectStorageSecretKey = "minioadmin";

    /// <summary>Matches <c>Env.StorageConfiguration.BucketName</c>'s default so nothing has to be
    /// overridden twice.</summary>
    public const string ObjectStorageBucket = "echo-chat";

    private const int MinioPort = 9000;

    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitMq;
    private readonly RedisContainer _redis;
    private readonly IContainer _minio;

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
        PostgreSqlContainer postgres, RabbitMqContainer rabbitMq, RedisContainer redis, IContainer minio)
    {
        _postgres = postgres;
        _rabbitMq = rabbitMq;
        _redis = redis;
        _minio = minio;
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

        // Built from the generic ContainerBuilder rather than Testcontainers.Minio: the module
        // would be a new package for a container this harness configures in five lines, and the
        // base package is already here as a dependency of the three modules above.
        var minio = new ContainerBuilder()
            .WithImage("minio/minio:latest")
            .WithEnvironment("MINIO_ROOT_USER", ObjectStorageAccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", ObjectStorageSecretKey)
            .WithCommand("server", "/data")
            .WithPortBinding(MinioPort, assignRandomHostPort: true)
            // MinIO answers the health probe before it will serve the S3 API on a cold start, so
            // this waits on the probe rather than on the port being open.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/minio/health/live").ForPort(MinioPort)))
            .Build();

        var set = new EchoInfraSet(postgres, rabbitMq, redis, minio);

        // A bounded timeout here beats a silent multi-minute hang if a wait strategy ever
        // misbehaves.
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync(), redis.StartAsync(), minio.StartAsync())
            .WaitAsync(TimeSpan.FromMinutes(3));

        set.PostgresHost = postgres.Hostname;
        set.PostgresPort = postgres.GetMappedPublicPort(5432);
        set.RabbitMqHost = rabbitMq.Hostname;
        set.RabbitMqPort = rabbitMq.GetMappedPublicPort(5672);
        set.RedisHost = redis.Hostname;
        set.RedisPort = redis.GetMappedPublicPort(6379);
        set.ObjectStorageUrl = $"http://{minio.Hostname}:{minio.GetMappedPublicPort(MinioPort)}";

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
                // Same two settings AppEnvironment.StorageInstance applies to the real client -
                // MinIO, like GCS's S3-interop API, rejects the SDK's default flexible-checksum
                // trailer.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = ObjectStorageBucket });
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
            _minio.DisposeAsync().AsTask());
    }
}
