namespace Messaging.Contracts.Bus.Events;

/// <summary>
/// Encryption was turned on or off for a guild channel.
///
/// <para>Messaging owns the MLS group but not channel membership, so it cannot address the people
/// who need to hear about this. Guild can, and already does exactly this for channel messages - so
/// the notification takes the same route rather than duplicating membership resolution.</para>
///
/// <para>Clients must act on this: a client that keeps encrypting after a disable, or keeps sending
/// plaintext after an enable, has its sends refused until it catches up.</para>
/// </summary>
public class ChannelMlsStateChanged
{
    public string ChannelId { get; set; } = null!;

    public bool Encrypted { get; set; }

    /// <summary>The generation now active, or the one just terminated.</summary>
    public int Generation { get; set; }

    /// <summary>Who flipped the switch.</summary>
    public string ChangedByUserId { get; set; } = null!;
}
