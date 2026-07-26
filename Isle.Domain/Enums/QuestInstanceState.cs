namespace Isle.Domain.Enums;

public enum QuestInstanceState
{
    /// <summary>Broadcast to chat, running, still inside its window.</summary>
    Active,

    /// <summary>Somebody fulfilled it.</summary>
    Completed,

    /// <summary>The window closed with nobody fulfilling it.</summary>
    Expired,

    /// <summary>Ended early — target left, admin closed it, server restarted.</summary>
    Cancelled
}
