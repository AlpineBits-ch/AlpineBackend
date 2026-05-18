using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(GuildInvite), NestedFacets = [typeof(GuildDto), typeof(ChannelDto), typeof(ChannelDto)])]

public partial class InviteDto
{
    
}