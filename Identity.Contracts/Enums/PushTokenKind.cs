namespace Identity.Contracts.Enums;

/// <summary>
/// Wire copy of Identity.Domain.Enums.PushTokenKind. Duplicated rather than referenced so the
/// contracts assembly stays dependency-free for the services that consume it - the same split the
/// repo already makes between Messaging.Domain.Enums.MessageType and
/// Guild.Contracts.Bus.Events.MessageType.
/// </summary>
public enum PushTokenKind
{
    Fcm,
    ApnsVoip,
}
