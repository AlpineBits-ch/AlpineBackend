using Social.Contracts.Dtos;

namespace Social.Contracts.Bus.Integration.Events;

/// <summary>A user's activity list changed.</summary>
public class UserActivityChanged
{
    public string UserId { get; set; } = null!;

    public IReadOnlyList<ActivityDto> Activities { get; set; } = [];
}
