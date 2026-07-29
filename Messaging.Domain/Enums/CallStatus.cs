namespace Messaging.Domain.Enums;

public enum CallStatus
{
    Pending,
    Ringing,
    Rejected,
    Connected,
    Completed,

    /// <summary>Participant-only status: they were connected and explicitly left a still-active
    /// call (as opposed to <see cref="Rejected"/>, which is a pre-connect decline). Appended at
    /// the end of the enum to keep already-cached Call blobs' numeric values stable.</summary>
    Left,
}