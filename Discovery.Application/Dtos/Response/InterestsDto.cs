namespace Discovery.Api.Dtos.Response;

/// <summary>A user's own interest set: what ranking reads and what the profile editor writes.</summary>
public class InterestsDto
{
    public required IReadOnlyList<TopicDto> Topics { get; init; }
    public required bool Visible { get; init; }
}
