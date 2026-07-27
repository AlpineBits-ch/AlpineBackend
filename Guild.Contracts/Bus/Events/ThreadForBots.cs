namespace Guild.Contracts.Bus.Events;

public class ThreadCreatedForBots
{
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
    public string ParentChannelId { get; set; }
    public string Name { get; set; }
}

/// <summary>Only ever fired for archive/unarchive today - there's no thread rename/delete
/// endpoint to hook a broader "updated" concept into yet.</summary>
public class ThreadUpdatedForBots
{
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
    public string ParentChannelId { get; set; }
    public string Name { get; set; }
    public bool Archived { get; set; }
}
