using System.Text;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Npgsql;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Direct reads against a stack's Guild database, for the two household assertions no HTTP surface
/// in this harness can answer.
/// </summary>
internal static class GuildDatabase
{
    /// <summary>Every label Postgres holds for a mapped enum type, e.g. <c>expense_category</c>.
    /// Empty when the type does not exist at all, which is itself a legitimate answer: an enum
    /// mapped in C# whose type was never created is the same bug one migration earlier.</summary>
    public static async Task<HashSet<string>> EnumLabelsAsync(EchoTestStack stack, string postgresTypeName)
    {
        await using var connection = await OpenAsync(stack);

        await using var command = new NpgsqlCommand(
            """
            SELECT e.enumlabel
            FROM pg_enum e
            JOIN pg_type t ON t.oid = e.enumtypid
            WHERE t.typname = @typeName
            """, connection);
        command.Parameters.AddWithValue("typeName", postgresTypeName);

        var labels = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) labels.Add(reader.GetString(0));

        return labels;
    }

    /// <summary>The distinct audit actions written against one guild, read back as text.</summary>
    public static async Task<HashSet<string>> AuditActionsAsync(EchoTestStack stack, string guildId)
    {
        await using var connection = await OpenAsync(stack);

        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT action_type::text FROM audit_log_entries WHERE guild_id = @guildId", connection);
        command.Parameters.AddWithValue("guildId", guildId);

        var actions = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actions.Add(reader.GetString(0));

        return actions;
    }

    /// <summary>
    /// The Postgres label Npgsql will use for a CLR name, reimplementing
    /// <c>NpgsqlSnakeCaseNameTranslator</c>'s rule - which is what
    /// <c>options.MapEnum&lt;T&gt;()</c> applies when no translator is named, and what every
    /// <c>MapEnum</c> call in <c>MicroserviceContext</c> therefore gets.
    /// </summary>
    public static string ToPostgresLabel(string clrName)
    {
        var builder = new StringBuilder(clrName.Length + 4);

        foreach (var character in clrName)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static async Task<NpgsqlConnection> OpenAsync(EchoTestStack stack)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = EchoInfraFixture.Default.PostgresHost,
            Port = EchoInfraFixture.Default.PostgresPort,
            Database = stack.GuildDatabaseName,
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }
}
