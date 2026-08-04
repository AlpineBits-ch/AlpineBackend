namespace Social.Api.Dtos.Response;

/// <summary>One entry of the viewer↔subject mutual friend list, gated by
/// <c>MutualFriendsVisibility</c> (privacy spec T2-17).</summary>
public class MutualFriendDto
{
    public required string ProfileId { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
}

/// <summary>One guild the viewer and the subject share, gated by <c>MutualServersVisibility</c>.</summary>
public class MutualServerDto
{
    public required string GuildId { get; init; }
    public string? Name { get; init; }
}

/// <summary>
/// A linked external account ("connection", in Discord's vocabulary), gated by
/// <c>ConnectionsVisibility</c> (privacy spec T2-17).
/// </summary>
public class ProfileConnectionDto
{
    /// <summary>Provider key, e.g. <c>"steam"</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The provider's own identifier for the account.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Provider-side display name where one is known.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether the link was established through the provider rather than self-asserted.</summary>
    public bool Verified { get; init; }
}

/// <summary>"Playing Isle"-style rich presence, gated by <c>ShareActivity</c> (privacy spec T2-19).</summary>
public class ProfileActivityDto
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string? Details { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}
