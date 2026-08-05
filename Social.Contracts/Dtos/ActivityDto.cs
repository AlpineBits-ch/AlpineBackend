namespace Social.Contracts.Dtos;

/// <summary>One thing a user is doing - "Playing Counter-Strike 2", "Listening to …".</summary>
public class ActivityDto
{
    /// <summary>String form of <c>Social.Domain.Enums.ActivityType</c>, following the existing
    /// convention of passing enums as plain strings across the bus/contract boundary (see
    /// <c>UserStatusChanged</c>, <c>ProfileDto.Font</c>).</summary>
    public string Type { get; set; } = null!;

    /// <summary>The display name.</summary>
    public string Name { get; set; } = null!;

    public string? Details { get; set; }
    public string? State { get; set; }

    /// <summary>The application id the activity was reported under, when there is one.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>When the activity started, Unix milliseconds UTC.</summary>
    public long? StartedAt { get; set; }

    public long? EndsAt { get; set; }

    public ActivityAssetsDto? Assets { get; set; }
    public ActivityPartyDto? Party { get; set; }

    /// <summary>String form of <c>Social.Domain.Enums.ActivitySource</c>.</summary>
    public string Source { get; set; } = null!;
}

/// <summary>Artwork slots.</summary>
public class ActivityAssetsDto
{
    public string? LargeImageUrl { get; set; }
    public string? LargeText { get; set; }
    public string? SmallImageUrl { get; set; }
    public string? SmallText { get; set; }
}

public class ActivityPartyDto
{
    public string? Id { get; set; }
    public int? Size { get; set; }
    public int? Max { get; set; }
}

/// <summary>
/// The caps every activity is held to, in one place because the write path, the local IPC reader
/// and the tests all have to agree on them.
/// </summary>
public static class ActivityLimits
{
    public const int MaxActivities = 3;
    public const int MaxNameLength = 128;
    public const int MaxTextLength = 128;
    public const int MaxApplicationIdLength = 20;
    public const int MaxPartySize = 1_000_000;

    /// <summary>How far in the past a client-supplied start time is believed.</summary>
    public static readonly TimeSpan MaxStartAge = TimeSpan.FromDays(1);
}
