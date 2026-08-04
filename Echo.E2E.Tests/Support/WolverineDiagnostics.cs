using Echo.E2E.Tests.Fixtures;
using Npgsql;

namespace Echo.E2E.Tests.Support;

/// <summary>Reads Wolverine's own record of what it failed to process.</summary>
internal static class WolverineDiagnostics
{
    public sealed record DeadLetter(string MessageType, string? ExceptionType, string? ExceptionMessage);

    public static async Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(
        EchoInfraSet infra, string databaseName, int limit = 20)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = infra.PostgresHost,
            Port = infra.PostgresPort,
            Database = databaseName,
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                "SELECT message_type, exception_type, exception_message " +
                "FROM wolverine_dead_letters ORDER BY sent_at DESC LIMIT @limit", connection);
            command.Parameters.AddWithValue("limit", limit);

            var results = new List<DeadLetter>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new DeadLetter(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return results;
        }
        catch (Exception e)
        {
            // Diagnostics must never be the thing that fails a test.
            return [new DeadLetter($"<could not read dead letters: {e.Message}>", null, null)];
        }
    }

    /// <summary>
    /// Every message type Wolverine has a record of handling in this database, with counts.
    /// </summary>
    public static async Task<string> IncomingSummaryAsync(EchoInfraSet infra, string databaseName)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = infra.PostgresHost,
            Port = infra.PostgresPort,
            Database = databaseName,
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                "SELECT message_type, status, count(*) FROM wolverine_incoming_envelopes " +
                "GROUP BY message_type, status ORDER BY 3 DESC LIMIT 30", connection);

            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add($"  {reader.GetInt64(2),4}  {reader.GetString(1),-10} {reader.GetString(0)}");

            return lines.Count == 0 ? "  (none)" : string.Join('\n', lines);
        }
        catch (Exception e)
        {
            return $"  <could not read incoming envelopes: {e.Message}>";
        }
    }
}
