namespace Messaging.Contracts.Bus.Events;

/// <summary>
/// A commit landed on an encrypted guild channel's MLS group.
///
/// <para>Messaging owns the group but not channel membership, so it cannot address the devices that
/// have to apply this. Guild can, and already does exactly this for channel messages - so the nudge
/// takes the same route rather than duplicating membership resolution.</para>
///
/// <para>Until this existed the channel commit path passed an empty audience: a commit reached the
/// device that published it and nobody else, and every other member sat on the previous epoch until
/// something unrelated made it re-fetch. A member that never re-fetches cannot decrypt anything sent
/// after the commit and has no way to notice.</para>
///
/// <para>As with conversations, this is a <b>nudge, not the payload</b> - it carries only where the
/// group now is. Clients GET the ordered commit list and apply from their own epoch, because
/// applying commits in delivery order is not safe across a reconnect.</para>
/// </summary>
public class ChannelMlsCommitPublished
{
    public string ChannelId { get; set; } = null!;

    public int Generation { get; set; }

    public long Epoch { get; set; }

    /// <summary>Client device id of the publisher; that device already merged locally.</summary>
    public string SenderDeviceId { get; set; } = null!;

    /// <summary>True when the payload was a bare proposal. It does not advance the group's epoch,
    /// and a client must not count it toward "commits applied" when deciding whether to page again.</summary>
    public bool IsProposal { get; set; }
}
