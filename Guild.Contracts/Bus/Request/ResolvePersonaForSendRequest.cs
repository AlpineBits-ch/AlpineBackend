namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Asks Guild which persona, if any, a message is being sent as - Messaging owns the send path but
/// has no access to personas, grants or autoproxy state.
/// </summary>
public class ResolvePersonaForSendRequest
{
    public required string UserId { get; set; }
    public required string ChannelId { get; set; }

    /// <summary>The persona the client named explicitly, which wins over every other path.</summary>
    public string? PersonaId { get; set; }

    /// <summary>
    /// The plaintext body, so a proxy prefix can be matched and stripped. Null for an encrypted
    /// send, where only an explicit persona id can resolve.
    /// </summary>
    public string? Content { get; set; }

    public override string ToString() =>
        $"ResolvePersonaForSendRequest(UserId: {UserId}, ChannelId: {ChannelId}, PersonaId: {PersonaId})";
}
