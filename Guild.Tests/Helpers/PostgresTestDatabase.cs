using AppEnvironment;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Guild.Tests.Helpers;

/// <summary>
/// A single real Postgres, started once for the whole test run and shared by every test that asks
/// for it.
///
/// <para><b>Why the tests need one at all.</b> The InMemory provider cannot translate LINQ to SQL -
/// it evaluates the expression tree in-process. So it cannot fail the way a real database fails, and
/// an untranslatable query passes every InMemory test while 500ing in production. It also cannot
/// tell you anything about Postgres semantics: collation and ordering, NULL handling in an outer
/// join, <c>timestamptz</c> coercion, or the snake-case column mapping. Running the existing
/// behavioural suite a second time against this fixture is what closes both gaps.</para>
///
/// <para><b>Why the container is shared and the tables are truncated.</b> Starting a container costs
/// seconds and creating 90-odd tables costs more, so doing either per test would make the suite
/// unusable. Truncating every table between tests gives the same isolation for the price of one
/// statement. NUnit runs this assembly sequentially (nothing is marked
/// <c>[Parallelizable]</c>), so a shared database is safe; if that ever changes, this needs a
/// schema or database per worker.</para>
/// </summary>
internal static class PostgresTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static bool _schemaCreated;

    /// <summary>
    /// Starts the container on first call and points <see cref="Env.Database"/> at it.
    ///
    /// Mutating the shared config rather than hand-building <c>DbContextOptions</c> is deliberate:
    /// it means <see cref="MicroserviceContext.OnConfiguring"/> - with the real enum mappings and
    /// the real snake-case convention - is the thing under test. A context configured separately
    /// here would be testing a model that is not the one deployed, which is how this class of bug
    /// hides in the first place.
    /// </summary>
    public static async Task EnsureStartedAsync()
    {
        if (_schemaCreated) return;

        await Gate.WaitAsync();
        try
        {
            if (_schemaCreated) return;

            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("postgres")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync().WaitAsync(TimeSpan.FromMinutes(3));

            Env.Database.DatabaseHostname = _container.Hostname;
            Env.Database.DatabasePort = _container.GetMappedPublicPort(5432).ToString();
            Env.Database.DatabaseName = "postgres";
            Env.Database.DatabaseUsername = "postgres";
            Env.Database.DatabasePassword = "postgres";

            // EnsureCreated, not Migrate: this fixture exists to test the queries against the
            // *current model*, and replaying 90 migrations to arrive at the same schema would cost
            // most of the run time. Migration correctness is a separate concern with a separate
            // owner (the E2E harness applies them for real).
            await using var context = new PostgresGuildContext();
            await context.Database.EnsureCreatedAsync();

            _schemaCreated = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Empties every table so the next test starts from nothing.</summary>
    public static async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(Env.Database.ConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public') LOOP
                    EXECUTE 'TRUNCATE TABLE public.' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
