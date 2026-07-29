namespace Messaging.Domain.Enums;

public enum CallEndReason
{
    /// <summary>Every invited participant declined (or the ring timeout elapsed with nobody answering).</summary>
    Declined,

    /// <summary>A participant explicitly force-ended the call for everyone.</summary>
    UserEnded,

    /// <summary>The last remaining connected participant was alone for the grace period with nobody rejoining.</summary>
    AloneTimeout,

    /// <summary>The last connected participant called Leave, dropping the call to zero connected participants.</summary>
    AllParticipantsLeft,
}
