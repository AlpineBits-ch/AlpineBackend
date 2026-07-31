using Facet;
using Guild.Domain.Aggregates;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// PublicKeys, ReadStates and WebhookConfigs are excluded rather than nested: none of them is
/// client-facing on a channel object.
/// </summary>
[Facet(typeof(Channel),
    nameof(Channel.PublicKeys), nameof(Channel.ReadStates), nameof(Channel.WebhookConfigs),
    NestedFacets = [typeof(CategoryDto), typeof(ChannelDto), typeof(GuildDto), typeof(ChannelPermissionDto)])]
public partial class ChannelDto
{

}