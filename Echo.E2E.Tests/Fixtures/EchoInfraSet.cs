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

/// <summary>
/// One independent set of Postgres/RabbitMQ/Redis/MinIO containers. A single-instance test uses one
/// shared <see cref="EchoInfraSet"/> (see <see cref="EchoInfraFixture.Default"/>) for all five
/// services. Two-instance federation tests provision a second, fully independent
/// <see cref="EchoInfraSet"/> instead of trying to namespace a shared broker/database - two real
/// federated Echo deployments would never share infra either, so this keeps the harness honest
/// and makes it trivial to simulate one instance going down without affecting the other.
///
/// <para><b>Why there is object storage here now.</b> Everything in this set used to be something a
/// service could not start without. MinIO is the first piece that exists for a feature instead: the
/// data export (T1-7 of docs/specs/privacy.md) only finishes when Identity's
/// <c>AssembleUserDataExportCommandHandler</c> has actually uploaded the zip - a failed
/// <c>PutAsync</c> marks the request <c>Failed</c> rather than <c>Ready</c>/<c>Partial</c>, so
/// without a real bucket the export cannot reach any of the endings
/// <c>DataExportFlowTests</c> asserts on. Substituting the artifact store was not an option worth
/// taking: every service here runs as its own OS process over unmodified build output (see
/// <see cref="Hosts.SpawnedServiceProcess"/>), so a substitution would have meant a test-only seam
/// in production DI - and the assertion that the archive is downloadable is precisely an assertion
/// about the round trip through the bucket and the signed URL.</para>
/// </summary>
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
    ///
    /// <para>One URL for both is what makes the download assertion work: Identity signs the URL its
    /// <c>SERVICE_URL</c> names, and the test follows that same URL to fetch the archive. Both are
    /// host processes talking to a published container port, so there is no in-Docker/out-of-Docker
    /// split to reconcile here.</para>
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

    /// <summary>
    /// Creates databases that do not already exist.
    ///
    /// <para>Separate from <see cref="CreateDatabasesAsync"/>, which is called once with a known
    /// list and would rather fail loudly on a duplicate. This one is for a database a service
    /// cannot create for itself: every EF-backed service runs migrations at startup, and EF creates
    /// the database when it is missing - but Unfurl owns no domain data and therefore has no
    /// DbContext, so nothing creates the database Wolverine wants for its envelope tables and the
    /// process dies on "Failed to setup resource Envelope Storage".</para>
    ///
    /// <para>Production has the same requirement and meets it elsewhere: <c>db_names</c> in the
    /// infrastructure repo's Terraform, and the <c>postgres-init</c> service in compose.yaml.</para>
    /// </summary>
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
            _minio.DisposeAsync().AsTask());
    }
}
