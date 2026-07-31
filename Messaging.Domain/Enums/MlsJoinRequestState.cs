namespace Messaging.Domain.Enums;

public enum MlsJoinRequestState
{
    /// <summary>Awaiting review. Still collecting approvals.</summary>
    Pending,

    /// <summary>A member refused it.</summary>
    Denied,

    /// <summary>A commit admitted the device. Terminal.</summary>
    Fulfilled,

    /// <summary>The requester withdrew it.</summary>
    Cancelled,
}
