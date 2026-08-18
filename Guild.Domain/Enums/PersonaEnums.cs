namespace Guild.Domain.Enums;

/// <summary>Who a persona belongs to, which is a different question from who may speak as it.</summary>
public enum PersonaScope
{
    /// <summary>Owned by an account and global: it works in every guild the owner adopts it into,
    /// with per-guild overrides on the profile.</summary>
    User,

    /// <summary>Owned by a guild - a Narrator, a town guard, a recurring antagonist. Speaking
    /// rights come from a <c>PersonaGrant</c> rather than from ownership.</summary>
    Guild,
}

/// <summary>Where a persona's character page has got to in a guild's approval queue.</summary>
public enum PersonaApprovalState
{
    /// <summary>Created and not yet sent for review.</summary>
    Draft,

    /// <summary>In the reviewer's queue.</summary>
    Submitted,

    /// <summary>Signed off. Edits above <c>LastApprovedRevisionNumber</c> are pending but do not
    /// stop the character being played.</summary>
    Approved,

    /// <summary>Sent back with a reason; the owner may resubmit.</summary>
    ChangesRequested,
}

/// <summary>What a channel does with a message the sender did not explicitly attach a persona to.</summary>
public enum AutoproxyMode
{
    /// <summary>Nothing. Only an explicit persona id or a proxy prefix speaks in character.</summary>
    Off,

    /// <summary>Every message goes out as the pinned persona until the sender changes it. Not
    /// called Latch: PluralKit's latch is this enum's <see cref="Sticky"/>, and reusing the word
    /// for the opposite behaviour would mislead exactly the people migrating from it.</summary>
    Pinned,

    /// <summary>Every message goes out as whichever persona the sender last proxied here, which the
    /// send path writes back as it goes.</summary>
    Sticky,
}
