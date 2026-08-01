namespace Messaging.Contracts.Bus.Events;

/// <summary>A commit landed on an encrypted guild channel's MLS group.</summary>
public class ChannelMlsCommitPublished
{
    public string ChannelId { get; set; } = null!;

    public int Generation { get; set; }

    public long Epoch { get; set; }

    /// <summary>Client device id of the publisher; that device already merged locally.</summary>
    public string SenderDeviceId { get; set; } = null!;

    /// <summary>True when the payload was a bare proposal.</summary>
    public bool IsProposal { get; set; }
}
