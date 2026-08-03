using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(ReadState), nameof(ReadState.Channel), nameof(ReadState.GuildMember))]
public partial class ReadStateDto
{
    /// <summary>
    /// Unread mentions in this channel.
    ///
    /// <para>Declared here rather than generated from the entity, because it is no longer stored:
    /// it is counted from the mention index and the channel's broadcast pings. Kept on the DTO
    /// deliberately - it is nested inside MemberDto and SelfMemberDto, so removing it would have
    /// silently dropped a field every client's badge reads.</para>
    ///
    /// <para>Zero on projections that do not compute it. Callers that need the real number use the
    /// inbox endpoints, which do.</para>
    /// </summary>
    public int MentionCount { get; set; }
}