namespace Guild.Contracts.Bus.Events;

/// <summary>Published by Guild when a guild's auto-moderation config is written, so Messaging can
/// drop the copy it caches per channel instead of enforcing the previous rules until that cache
/// expires on its own.</summary>
public class AutoModConfigChanged
{
    public string GuildId { get; set; }

    /// <summary>Every channel of the guild - Messaging caches the config per channel, since that is
    /// the only id the send path has.</summary>
    public List<string> ChannelIds { get; set; } = [];
}
