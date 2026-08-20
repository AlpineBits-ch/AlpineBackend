namespace Guild.Domain.Enums;

/// <summary>Where one ask to bring a character into a closed scene has got to.</summary>
public enum SceneJoinRequestStatus
{
    Pending,

    Approved,

    /// <summary>Refused, with the reason kept on the row so the player's inbox can read it.</summary>
    Denied,

    /// <summary>Taken back by the player before anybody decided it.</summary>
    Withdrawn,
}
