namespace Messaging.Contracts.Bus.Events;

/// <summary>
/// Somebody asked to be let into an encrypted channel.
///
/// <para>Messaging owns the request but not channel membership, so it cannot reach the people who
/// would review it. Guild can, and already fans out channel events - so this takes the same route
/// rather than duplicating membership resolution.</para>
/// </summary>
public class ChannelMlsJoinRequested
{
    public string ChannelId { get; set; } = null!;

    /// <summary>Who is asking. Deliberately no key material here: the fingerprint a reviewer
    /// compares is fetched with the request itself, over an authenticated read, rather than
    /// broadcast to everyone in the guild's presence set.</summary>
    public string RequesterUserId { get; set; } = null!;
}
