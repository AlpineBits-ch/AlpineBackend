namespace Messaging.Contracts.Bus.Events;

/// <summary>Encryption was turned on or off for a guild channel.</summary>
public class ChannelMlsStateChanged
{
    public string ChannelId { get; set; } = null!;

    public bool Encrypted { get; set; }

    /// <summary>The generation now active, or the one just terminated.</summary>
    public int Generation { get; set; }

    /// <summary>Who flipped the switch.</summary>
    public string ChangedByUserId { get; set; } = null!;
}
