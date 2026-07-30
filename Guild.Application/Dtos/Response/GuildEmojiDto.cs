using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(GuildEmoji), nameof(GuildEmoji.Guild))]
public partial class GuildEmojiDto
{
    public string ImageUrl { get; set; } = null!;
}
