namespace Guild.Domain.Enums;

/// <summary>Who may bring a character into a scene.</summary>
public enum SceneJoinPolicy
{
    /// <summary>Anyone who can see the scene brings a character in themselves.</summary>
    Open,

    /// <summary>Only the cast plays, and getting in needs a GM's yes.</summary>
    Ask,
}
