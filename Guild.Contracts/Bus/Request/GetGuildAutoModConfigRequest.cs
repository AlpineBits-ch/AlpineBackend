namespace Guild.Contracts.Bus.Request;

/// <summary>Called by Messaging at message-create time (channel messages only) to decide whether
/// to block/rate-limit - resolves GuildId from ChannelId server-side so Messaging never needs to
/// know or cache which guild a channel belongs to for this purpose.</summary>
public class GetGuildAutoModConfigRequest
{
    public string ChannelId { get; set; }
}
