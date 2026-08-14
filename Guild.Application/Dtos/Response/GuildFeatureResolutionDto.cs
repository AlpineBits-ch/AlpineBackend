using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>A guild's module state as the four lists a client has to draw from.</summary>
/// <param name="Chosen">What the guild's owner switched on.</param>
/// <param name="IncludedByPlan">What the guild's plan covers.</param>
/// <param name="WithheldByPlan">Chosen and not covered.</param>
/// <param name="Effective">What is actually on, and what every permission gate reads.</param>
public sealed record GuildFeatureResolutionDto(
    IReadOnlyList<string> Chosen,
    IReadOnlyList<string> IncludedByPlan,
    IReadOnlyList<string> WithheldByPlan,
    IReadOnlyList<string> Effective)
{
    public static GuildFeatureResolutionDto From(GuildFeatureResolution resolution) =>
        new(
            GuildFeatureMap.Names(resolution.Chosen),
            GuildFeatureMap.Names(resolution.IncludedByPlan),
            GuildFeatureMap.Names(resolution.WithheldByPlan),
            GuildFeatureMap.Names(resolution.Effective));
}
