namespace Guild.Contracts.Bus.Response;

/// <summary>
/// What the send path should do about a persona. AuthorId is never part of this answer: the real
/// account stays the author whatever comes back, which is what keeps blocking, mutes, bans, the
/// audit log and reply-pings working with no special cases.
/// </summary>
public class ResolvePersonaForSendResponse
{
    /// <summary>False when the caller may not speak as the persona they asked for - answer 403.</summary>
    public bool IsAllowed { get; set; } = true;

    /// <summary>Why not, for the 403 body.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The body to store, with any matched proxy prefix and suffix, or a leading backslash escape,
    /// already stripped. Unchanged when nothing matched.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>Null when the message is being sent as the user themselves.</summary>
    public string? PersonaId { get; set; }

    /// <summary>The name to write into Message.AuthorDisplayName.</summary>
    public string? AuthorDisplayName { get; set; }

    /// <summary>The avatar to write into Message.AuthorAvatarUrl.</summary>
    public string? AuthorAvatarUrl { get; set; }
}
