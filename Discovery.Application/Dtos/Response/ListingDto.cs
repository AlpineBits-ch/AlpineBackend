namespace Discovery.Api.Dtos.Response;

/// <summary>A guild's own listing, as the editor and the owner's preview render it.</summary>
public class ListingDto
{
    public required string Id { get; init; }
    public required string GuildId { get; init; }
    public required string Headline { get; init; }
    public required string Pitch { get; init; }
    public required string Language { get; init; }
    public required string JoinPolicy { get; init; }
    public required IReadOnlyList<string> Links { get; init; }
    public required IReadOnlyList<TopicDto> Topics { get; init; }
    public required string State { get; init; }

    /// <summary>The owner-facing reason when State is Suspended and the cause is a staff ban. Never
    /// the ban's StaffNote - that stays in the console.</summary>
    public string? SuspendedMessage { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? LastBumpedAt { get; init; }
    public DateTimeOffset? BumpAvailableAt { get; init; }
}
