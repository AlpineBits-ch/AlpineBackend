namespace Guild.Contracts.Bus.Response;

public class GetChannelResponse
{
    public ChannelInfo? Channel { get; set; }
}

public class ChannelInfo
{
    public string Id { get; set; }
    public string GuildId { get; set; }
    public string Name { get; set; }

    /// <summary>Guild.Domain.Enums.ChannelType's enum name - see ChannelSnapshot's remarks
    /// (GetGuildSnapshotForBotResponse.cs) for why this stays a plain string.</summary>
    public string Type { get; set; }
    public int Position { get; set; }
    public string? CategoryId { get; set; }
}
