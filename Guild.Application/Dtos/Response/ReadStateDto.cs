using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(ReadState), nameof(ReadState.Channel), nameof(ReadState.GuildMember))]
public partial class ReadStateDto
{
    /// <summary>Unread mentions in this channel.</summary>
    public int MentionCount { get; set; }
}