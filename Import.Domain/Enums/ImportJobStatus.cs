namespace Import.Domain.Enums;

public enum ImportJobStatus
{
    Pending,
    FetchingFromDiscord,
    CreatingGuild,
    Completed,
    Failed,
}
