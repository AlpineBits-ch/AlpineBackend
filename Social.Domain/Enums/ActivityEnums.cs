namespace Social.Domain.Enums;

/// <summary>What kind of activity this is.</summary>
public enum ActivityType
{
    Playing,
    Streaming,
    Listening,
    Watching,
    Competing,

    /// <summary>A user-authored status line rather than something detected.</summary>
    Custom,
}

/// <summary>Where an activity came from.</summary>
public enum ActivitySource
{
    /// <summary>Matched against the local process list. Name only - no details, state or party.</summary>
    ProcessScan,

    /// <summary>The game itself asserted it over the local RPC socket.</summary>
    Rpc,

    /// <summary>The OS media session (SMTC on Windows, MPRIS on Linux).</summary>
    Media,

    /// <summary>Typed by the user.</summary>
    Manual,

    /// <summary>Reported by a first-party client through a channel we control.</summary>
    Native,
}
