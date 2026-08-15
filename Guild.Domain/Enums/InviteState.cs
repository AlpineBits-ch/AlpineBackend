namespace Guild.Domain.Enums;

/// <summary>The lifecycle of an invite.</summary>
public enum InviteState
{
    Active,
    Expired,

    /// <summary>Withdrawn by a moderator.</summary>
    Revoked,
}
