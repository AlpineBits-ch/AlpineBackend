namespace Messaging.Application.Dtos.Request;

/// <summary>Same mute vocabulary as the guild-side notification DTOs, deliberately - a client
/// should not have to express "mute for an hour" two different ways depending on whether the
/// target is a channel or a DM.</summary>
public class UpdateConversationNotificationDto
{
    /// <summary>Minutes to mute for. 0 or negative unmutes; null leaves the current state alone.</summary>
    public int? MuteMinutes { get; set; }

    public bool MuteForever { get; set; }
}
