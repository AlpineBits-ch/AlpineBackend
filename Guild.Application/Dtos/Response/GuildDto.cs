using System.Text.Json.Serialization;
using Facet;

namespace Guild.Application.Dtos.Response;

/// <summary>PublicKeys (the E2EE key store) and WebhookConfigs (whose <c>Token</c> is a standing
/// write credential) are excluded for the same reason as on <see cref="ChannelDto"/>: neither is
/// client-facing, and both are copied as raw entities if left in.</summary>
[Facet(typeof(Domain.Aggregates.Guild), nameof(Domain.Aggregates.Guild.Members), nameof(Domain.Aggregates.Guild.Invites), nameof(Domain.Aggregates.Guild.PublicKeys), nameof(Domain.Aggregates.Guild.WebhookConfigs), NestedFacets = [typeof(ChannelDto), typeof(CategoryDto), typeof(RoleDto)], MaxDepth = 1)]
public partial class GuildDto
{
    /// <summary>
    /// The guild's modules split into what the owner chose, what the plan covers, what the plan is
    /// withholding and what is actually on.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GuildFeatureResolutionDto? FeatureResolution { get; set; }
}