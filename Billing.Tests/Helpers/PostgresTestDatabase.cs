using AppEnvironment;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Billing.Tests.Helpers;

/// <summary>A single real Postgres, started once for the whole test run.</summary>
internal static class PostgresTestDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static bool _started;

    public static async Task EnsureStartedAsync()
    {
        if (_started) return;

        await Gate.WaitAsync();
        try
        {
            if (_started) return;

            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("billing_db")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync().WaitAsync(TimeSpan.FromMinutes(3));

            Env.Database.DatabaseHostname = _container.Hostname;
            Env.Database.DatabasePort = _container.GetMappedPublicPort(5432).ToString();
            Env.Database.DatabaseName = "billing_db";
            Env.Database.DatabaseUsername = "postgres";
            Env.Database.DatabasePassword = "postgres";

            _started = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static MicroserviceContext CreateContext() =>
        new(new DbContextOptionsBuilder<MicroserviceContext>().Options);

    /// <summary>Drops everything the migrations created, so a test can watch them apply to a
    /// genuinely empty database rather than to whatever the previous test left.</summary>
    public static async Task ResetToEmptyAsync()
    {
        await using var connection = new NpgsqlConnection(Env.Database.ConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Env.Database.ConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
