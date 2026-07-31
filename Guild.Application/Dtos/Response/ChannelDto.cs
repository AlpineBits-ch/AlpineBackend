using Facet;
using Guild.Domain.Aggregates;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// PublicKeys, ReadStates and WebhookConfigs are excluded rather than nested: none of them is
/// client-facing on a channel object. PublicKeys is the E2EE key store, ReadStates is every
/// member's read cursor (a client only ever gets its own, from the member endpoints), and
/// WebhookConfig carries <c>Token</c> - a standing write credential that must never be
/// serialized, which is exactly why <see cref="WebhookConfigDto"/> is hand-written instead of a
/// facet. Left in, each one is copied as the raw entity and dragged into every guild response.
/// </summary>
[Facet(typeof(Channel),
    nameof(Channel.PublicKeys), nameof(Channel.ReadStates), nameof(Channel.WebhookConfigs),
    NestedFacets = [typeof(CategoryDto), typeof(ChannelDto), typeof(GuildDto), typeof(ChannelPermissionDto)])]
public partial class ChannelDto
{

}