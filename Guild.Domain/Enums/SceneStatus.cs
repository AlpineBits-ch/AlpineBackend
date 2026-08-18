namespace Guild.Domain.Enums;

/// <summary>Where a scene is in its life, which is what decides whether the turn moves.</summary>
public enum SceneStatus
{
    /// <summary>Cast is still being assembled. Nobody has a turn yet and nothing is nudged.</summary>
    Open,

    /// <summary>Being played: posting advances the turn and a passed deadline nudges.</summary>
    Active,

    /// <summary>Halted on purpose - a break, a missing player, a plot rewrite. Posting no longer
    /// advances the turn, which is the difference from Active and the reason a GM reaches for
    /// it.</summary>
    Paused,

    /// <summary>Finished. The turn state stops moving for good and the scene is what the chronicle
    /// exports.</summary>
    Concluded,
}
