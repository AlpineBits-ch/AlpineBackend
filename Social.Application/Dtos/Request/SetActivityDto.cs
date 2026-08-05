using Social.Contracts.Dtos;

namespace Social.Api.Dtos.Request;

/// <summary>The caller's complete activity list.</summary>
public class SetActivityDto
{
    public IReadOnlyList<ActivityDto>? Activities { get; set; }
}
