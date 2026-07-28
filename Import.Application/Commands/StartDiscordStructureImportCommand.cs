namespace Import.Application.Commands;

/// <summary>Durable Wolverine command - survives restarts via the same PersistMessagesWithPostgresql/
/// durable-inbox convention every other service already uses. Sent from
/// DiscordImportEndpoint.CallbackAsync once the OAuth state is validated and the bot's presence
/// in the target Discord guild is confirmed.</summary>
public class StartDiscordStructureImportCommand
{
    public string ImportJobId { get; set; }
    public string DiscordGuildId { get; set; }
    public string RequestedByUserId { get; set; }
}
