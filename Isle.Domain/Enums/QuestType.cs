namespace Isle.Domain.Enums;

public enum QuestType
{
    /// <summary>Go somewhere. Used by the director to pull population out of crowded regions.</summary>
    Exploration,

    /// <summary>Hunt something in a region.</summary>
    Hunt,

    /// <summary>Kill a specific marked player. Spawned by the spree detector or by an admin, never by the director.</summary>
    Bounty
}
