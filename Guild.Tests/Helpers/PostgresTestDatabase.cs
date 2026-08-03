using AppEnvironment;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Guild.Tests.Helpers;

/// <summary>
/// A single real Postgres, started once for the whole test run and shared by every test that asks
/// for it.
/// </summary>
internal static class PostgresTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static bool _schemaCreated;

    /// <summary>
    /// Starts the container on first call and points <see cref="Env.Database"/> at it.
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
            // current model, and replaying 90 migrations to arrive at the same schema would cost
            // most of the run time.
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
