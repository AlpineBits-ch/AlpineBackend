using Import.Domain.Enums;
using Persistence;

namespace Import.Domain.Entity;

/// <summary>
/// One run of the initial "pull a Discord server's structure into a new Echo guild" job.
/// </summary>
public class ImportJob : BaseEntity<ImportJob>, IPrefixedEntity
{
    public static string Prefix { get; } = "imjb";

    public string? EchoGuildId { get; set; }
    public string DiscordGuildId { get; init; }
    public string RequestedByUserId { get; init; }
    public ImportJobStatus Status { get; set; } = ImportJobStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
