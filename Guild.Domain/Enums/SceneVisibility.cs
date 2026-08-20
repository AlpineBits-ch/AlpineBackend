namespace Guild.Domain.Enums;

/// <summary>Who may see a scene at all.</summary>
public enum SceneVisibility
{
    /// <summary>Whoever can see the channel it hangs under.</summary>
    Everyone,

    /// <summary>The cast and whoever holds ManageScenes, everywhere a scene channel is read.</summary>
    Cast,
}
