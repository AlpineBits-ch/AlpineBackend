using Social.Contracts.Dtos;

namespace Social.Api.Dtos.Response;

/// <summary>What an activity write actually published, after the guard had its say.</summary>
public sealed class SetActivityResultDto
{
    public required IReadOnlyList<ActivityDto> Activities { get; init; }
}
