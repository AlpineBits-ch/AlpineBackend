namespace Messaging.Contracts.Bus.Commands;

/// <summary>Points a message at the thread Guild has just created from it.</summary>
public class AttachThreadToMessageCommand
{
    public string MessageId { get; set; } = null!;

    /// <summary>The channel the caller believes the message lives in, checked rather than trusted -
    /// otherwise a thread could be hung off a message in a channel the caller cannot see.</summary>
    public string ChannelId { get; set; } = null!;

    public string ThreadId { get; set; } = null!;
}

/// <summary>Clears a message's thread pointer once the thread is gone.</summary>
public class DetachThreadFromMessageCommand
{
    public string MessageId { get; set; } = null!;

    /// <summary>Only clears when the message still points here, so a late detach cannot unlink a
    /// thread someone has since created in its place.</summary>
    public string ThreadId { get; set; } = null!;
}

public enum AttachThreadOutcome
{
    Attached,
    MessageNotFound,
    WrongChannel,
    AlreadyHasThread,
}

public class AttachThreadToMessageResponse
{
    public AttachThreadOutcome Outcome { get; set; }

    /// <summary>The thread already on the message when the outcome is AlreadyHasThread.</summary>
    public string? ExistingThreadId { get; set; }
}
