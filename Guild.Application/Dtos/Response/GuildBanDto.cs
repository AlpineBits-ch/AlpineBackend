using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(GuildBan), nameof(GuildBan.Guild))]
public partial class GuildBanDto
{
}
