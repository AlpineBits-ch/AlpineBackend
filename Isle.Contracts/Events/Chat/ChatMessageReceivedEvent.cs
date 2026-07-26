namespace Isle.Contracts.Events.Chat;

/// <summary>
/// A single line from the game server's chat feed, republished onto the bus by the chat stream
/// ingestion service.
/// </summary>
public class ChatMessageReceivedEvent
{
    public string SteamId { get; set; } = string.Empty;

    /// <summary>Best-effort display name; the bridge omits it when the plugin couldn't read one.</summary>
    public string? Name { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Raw <c>EChatMode</c> value as reported by the game (0 = spatial, 1 = global, ...).</summary>
    public int Mode { get; set; }

    /// <summary>Unix-ms timestamp the bridge stamped on the line.</summary>
    public long TimestampMs { get; set; }
}
