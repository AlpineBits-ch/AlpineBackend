namespace Messaging.Contracts.Bus.Events;

/// <summary>Somebody asked to be let into an encrypted channel.</summary>
public class ChannelMlsJoinRequested
{
    public string ChannelId { get; set; } = null!;

    /// <summary>Who is asking.</summary>
    public string RequesterUserId { get; set; } = null!;
}
