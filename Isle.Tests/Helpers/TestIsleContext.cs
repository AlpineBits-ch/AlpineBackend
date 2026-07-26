using System.Text.Json;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Isle.Tests.Helpers;

/// <summary>
/// Assigns shadow owned-collection keys purely in memory. Postgres auto-generates these via an
/// identity column regardless of the key being composite (owner FK + this); SQLite's autoincrement
/// only works for a lone INTEGER PRIMARY KEY, so under SQLite the shadow "Id" on
/// <c>GameModeDefinition.Rewards</c>/<c>GameModeRun.Results</c> would otherwise insert as NULL and
/// violate the NOT NULL constraint. Values only need to be unique within one SaveChanges call.
/// </summary>
internal sealed class SequentialIntGenerator : ValueGenerator<int>
{
    private int _next;
    public override int Next(EntityEntry entry) => System.Threading.Interlocked.Increment(ref _next);
    public override bool GeneratesTemporaryValues => false;
}

/// <summary>See its use in <see cref="TestIsleContext.ConfigureConventions"/> for why this exists.</summary>
internal sealed class DateTimeOffsetToTicksConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

/// <summary>
/// SQLite in-memory context for unit tests. EF Core's own "InMemory" provider cannot run
/// <c>ExecuteUpdateAsync</c> (used by <c>QuestInstanceCloser</c>'s atomic close) at all — it throws
/// "not supported by the current database provider" — so this uses a real relational engine instead,
/// which is otherwise just as fast and needs nothing external. Overrides OnConfiguring so the base
/// Postgres setup is never invoked. One open <see cref="SqliteConnection"/> per instance: SQLite's
/// in-memory database only exists while a connection to it is open, so closing that connection is what
/// tears the database down — <see cref="DisposeAsync"/> does both in the right order.
/// </summary>
internal sealed class TestIsleContext : MicroserviceContext
{
    private readonly SqliteConnection _connection;

    private TestIsleContext(SqliteConnection connection)
        : base(new DbContextOptionsBuilder<MicroserviceContext>()
            .UseSqlite(connection)
            .Options)
    {
        _connection = connection;
        Database.EnsureCreated();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Intentionally left empty: the Sqlite provider is already configured via the constructor
        // options; calling base would add a conflicting Postgres provider and throw at runtime.
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // SQLite can't translate ORDER BY or <=/>= comparisons over a raw DateTimeOffset column (its
        // default text representation doesn't sort/compare correctly across offsets, and EF Core would
        // rather throw than silently produce a wrong answer) — Postgres has no such restriction, so
        // every entity here uses DateTimeOffset freely. Every value in this codebase is UTC
        // (DateTimeOffset.UtcNow throughout), so converting to plain UTC ticks is lossless and sorts
        // and compares correctly as an ordinary long.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // StorageSlot.Mutations.Slots is a Dictionary<string,string>, mapped to Postgres' native hstore
        // type by Npgsql's own convention in production — SQLite has no equivalent, so model validation
        // fails for every test touching Player/Storage unless this is spelled out here.
        modelBuilder.Entity<StorageSlot>().OwnsOne(s => s.Mutations, m =>
        {
            m.Property(x => x.Slots).HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null));
        });

        // Player.FriendlyIdSeq/QuestInstance.FriendlyIdSeq default to `nextval('...')` off a Postgres
        // sequence in production — SQLite has no sequence support at all, so both the column default
        // and the sequence itself have to go. Nothing under test relies on friendly ids auto-assigning;
        // tests that need one set FriendlyIdSeq directly.
        // Every row otherwise defaults to FriendlyIdSeq 0 and collides on the unique index the moment
        // a test inserts more than one — the sequence generated a distinct value per row in production,
        // so this needs to keep doing that, just without a real Postgres sequence behind it.
        modelBuilder.Entity<Player>().Property(p => p.FriendlyIdSeq).HasDefaultValueSql(null!).ValueGeneratedOnAdd().HasValueGenerator<SequentialIntGenerator>();
        modelBuilder.Entity<QuestInstance>().Property(i => i.FriendlyIdSeq).HasDefaultValueSql(null!).ValueGeneratedOnAdd().HasValueGenerator<SequentialIntGenerator>();
        modelBuilder.Model.RemoveSequence("player_friendly_id_seq");
        modelBuilder.Model.RemoveSequence("quest_instance_friendly_id_seq");

        // See SequentialIntGenerator's doc comment: these two owned collections' shadow "Id" key
        // needs an in-memory generator under SQLite.
        modelBuilder.Entity<GameModeRun>().OwnsMany(r => r.Results, pr =>
            pr.Property<int>("Id").HasValueGenerator<SequentialIntGenerator>());
        modelBuilder.Entity<GameModeDefinition>().OwnsMany(d => d.Rewards, r =>
            r.Property<int>("Id").HasValueGenerator<SequentialIntGenerator>());
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public static TestIsleContext Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return new TestIsleContext(connection);
    }
}
