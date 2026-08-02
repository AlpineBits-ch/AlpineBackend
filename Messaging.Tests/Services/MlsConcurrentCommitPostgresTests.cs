using Echo.Realtime;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Messaging.Tests.Services;

/// <summary>Two members committing the same epoch at the same instant.</summary>
[TestFixture]
[Category("Postgres")]
public class MlsConcurrentCommitPostgresTests
{
    private const string ChannelId = "chan-race";
    private const string AdminId = "user-admin";

    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An ordinary Postgres connection string to any reachable database, e.g.
    /// <c>Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres</c>.
    /// </summary>
    private static string? MaintenanceConnectionString =>
        Environment.GetEnvironmentVariable("ECHO_TEST_POSTGRES");

    private string _database = null!;
    private string _testConnectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        if (string.IsNullOrWhiteSpace(MaintenanceConnectionString))
        {
            Assert.Ignore(
                "Set ECHO_TEST_POSTGRES to a Postgres connection string to run the concurrent-commit "
                + "tests, e.g. Host=localhost;Port=5433;Database=postgres;Username=postgres;"
                + "Password=postgres. They are deliberately not run against the InMemory provider: "
                + "that provider ignores the filtered unique index they exist to exercise, so they "
                + "would pass without proving anything.");
        }

        // One database per test, not per fixture.
        _database = "echo_mls_race_" + Guid.NewGuid().ToString("N");

        _testConnectionString = new NpgsqlConnectionStringBuilder(MaintenanceConnectionString)
        {
            Database = _database,
        }.ConnectionString;

        await ExecuteOnMaintenanceAsync($"CREATE DATABASE \"{_database}\";");

        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (string.IsNullOrWhiteSpace(MaintenanceConnectionString) || _database is null) return;

        // Npgsql pools connections, so a context that has been disposed can still be holding an
        // open backend against this database.
        NpgsqlConnection.ClearAllPools();

        // WITH (FORCE) terminates anything still attached rather than leaving a stray database
        // behind for the next run to trip over. Postgres 13+.
        await ExecuteOnMaintenanceAsync($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE);");
    }

    /// <summary>Runs a statement against the database named in <see cref="MaintenanceConnectionString"/>,
    /// which is deliberately never the one under test - CREATE and DROP DATABASE cannot run from a
    /// connection to their own target.</summary>
    private static async Task ExecuteOnMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The same Npgsql configuration the real context builds for itself, pointed at this test's
    /// throwaway database.
    /// </summary>
    private MicroserviceContext NewContext()
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

        return new PostgresMessagingContext(builder.Options);
    }

    private sealed class PostgresMessagingContext(DbContextOptions<MicroserviceContext> options)
        : MicroserviceContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Provider already supplied via options; calling base would read the environment's
            // connection string and point these tests at the real development database.
        }
    }

    private static MlsGroupService ServiceFor(MicroserviceContext ctx) =>
        new(ctx, new FakeMessagingHubContext(), new FakeMessageBus(), new MlsJoinRequestService(ctx));

    private async Task SeedGenerationAsync()
    {
        await using var setup = NewContext();

        setup.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = ChannelId,
            ChannelId = ChannelId,
            Generation = 1,
            MlsGroupId = [1, 2, 3],
            Epoch = 0,
            ActivatedByUserId = AdminId,
            ActivatedAt = T0,
        }));
        await setup.SaveChangesAsync();
    }

    /// <summary>The database, not the endpoint, is what resolves a genuine race.</summary>
    [Test]
    public async Task ADuplicateEpoch_RaisesTheUniqueViolationTheServiceTranslatesToA409()
    {
        await SeedGenerationAsync();

        await using var ctx = NewContext();
        var commit = () => MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ChannelId, ChannelId = ChannelId, Generation = 1, Epoch = 1,
            Commit = [1], SenderUserId = AdminId, SenderDeviceId = "device-a",
        });

        ctx.MlsCommits.Add(commit());
        await ctx.SaveChangesAsync();

        ctx.MlsCommits.Add(commit());
        var ex = Assert.ThrowsAsync<DbUpdateException>(async () => await ctx.SaveChangesAsync());

        var sqlState = ex!.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string;
        Assert.That(sqlState, Is.EqualTo("23505"),
            "The service translates exactly this SQLSTATE into a 409; anything else escapes as a 500");
    }

    /// <summary>
    /// The observable contract under real concurrency: exactly one commit survives, and the loser
    /// is told with a 409 it can retry from rather than a 500 it will discard the change over.
    /// </summary>
    [Test]
    public async Task TwoMembersCommittingTheSameEpochConcurrently_ExactlyOneWinsAndNobodyGetsA500()
    {
        await SeedGenerationAsync();

        await using var first = NewContext();
        await using var second = NewContext();

        // Both contexts load the generation at epoch 0 before either writes, so neither pre-check
        // can see the other's commit.
        await ServiceFor(first).GetActiveGenerationAsync(ChannelId);
        await ServiceFor(second).GetActiveGenerationAsync(ChannelId);

        var a = ServiceFor(first).PublishCommitAsync(ChannelId, null, ChannelId, AdminId,
            new PublishMlsCommitDto { Epoch = 1, Commit = MlsWire.Commit(epoch: 1), SenderDeviceId = "device-a" }, [], T0);
        var b = ServiceFor(second).PublishCommitAsync(ChannelId, null, ChannelId, AdminId,
            new PublishMlsCommitDto { Epoch = 1, Commit = MlsWire.Commit(epoch: 2), SenderDeviceId = "device-b" }, [], T0);

        var results = await Task.WhenAll(a, b);

        Assert.Multiple(() =>
        {
            Assert.That(results.Count(r => r.Status == MlsOperationStatus.Ok), Is.EqualTo(1),
                "Exactly one publisher may win the epoch");
            Assert.That(results.Count(r => r.Status == MlsOperationStatus.Conflict), Is.EqualTo(1),
                "The loser must get a conflict, not a 500 - clients only retry on 409");
        });

        var loser = results.Single(r => r.Status == MlsOperationStatus.Conflict);
        Assert.That(loser.Value, Is.InstanceOf<MlsEpochConflictDto>(),
            "The conflict has to carry where the group actually is, so the loser can catch up");

        await using var verify = NewContext();
        Assert.That(await verify.MlsCommits.CountAsync(c => !c.IsProposal), Is.EqualTo(1),
            "Exactly one commit survives the race");
    }

    [Test]
    public async Task AProposalDoesNotCollideWithTheCommitAtTheSameEpoch()
    {
        await SeedGenerationAsync();

        await using var ctx = NewContext();
        var service = ServiceFor(ctx);

        var proposal = await service.PublishCommitAsync(ChannelId, null, ChannelId, AdminId,
            new PublishMlsCommitDto { Epoch = 1, Commit = MlsWire.Proposal(), SenderDeviceId = "d", IsProposal = true }, [], T0);
        var commit = await service.PublishCommitAsync(ChannelId, null, ChannelId, AdminId,
            new PublishMlsCommitDto { Epoch = 1, Commit = MlsWire.Commit(), SenderDeviceId = "d" }, [], T0);

        // The index has to be filtered on is_proposal, or a Remove proposal announced at N+1 blocks
        // the commit that establishes N+1 and the group can never move again.
        Assert.Multiple(() =>
        {
            Assert.That(proposal.Status, Is.EqualTo(MlsOperationStatus.Ok));
            Assert.That(commit.Status, Is.EqualTo(MlsOperationStatus.Ok));
        });
        Assert.That(await ctx.MlsCommits.CountAsync(), Is.EqualTo(2));
    }
}
