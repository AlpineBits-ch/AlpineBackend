namespace Isle.Domain.Enums;

/// <summary>How a <c>PlaySession</c> stopped counting.</summary>
public enum PlaySessionEndReason
{
    /// <summary>The game server reported the player leaving. The only reason whose end time is exact.</summary>
    Left,

    /// <summary>
    /// The reconcile pass found the player gone from a fresh roster without ever having seen a
    /// leave event - a crash, a network drop, or a leave that arrived while this service was
    /// restarting.
    /// </summary>
    Disconnected,

    /// <summary>The player swapped to a different dinosaur.</summary>
    SpeciesChange,

    /// <summary>
    /// The hard cap fired: the session was still open long after its last heartbeat, which means
    /// nothing was in a position to close it properly.
    /// </summary>
    Abandoned,
}
