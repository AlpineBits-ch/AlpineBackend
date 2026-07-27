namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Published alongside the existing "guild.ChannelCreated"/"guild.ChannelUpdated"/"guild.ChannelDeleted"
/// SignalR broadcasts in ChannelEndpoint so Bots.Application (which cannot join the SignalR/Redis
/// backplane those broadcasts ride on) can translate channel changes into Discord Gateway
/// CHANNEL_CREATE/UPDATE/DELETE dispatches for installed bots.
/// </summary>
public class ChannelCreatedForBots
{
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public int Position { get; set; }
    public string? CategoryId { get; set; }
}

public class ChannelUpdatedForBots
{
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public int Position { get; set; }
    public string? CategoryId { get; set; }
}

public class ChannelDeletedForBots
{
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
}
