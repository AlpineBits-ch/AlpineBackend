using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(GuildScheduledEvent), nameof(GuildScheduledEvent.Guild), nameof(GuildScheduledEvent.VoiceChannel), nameof(GuildScheduledEvent.Interested))]
public partial class GuildScheduledEventDto
{
    public int InterestedCount { get; set; }
    public bool IsInterested { get; set; }
}
