namespace Guild.Contracts.Bus.Commands;

public class RemoveBotGuildMemberCommand
{
    public string GuildId { get; set; }
    public string BotUserId { get; set; }
    public string RemovedByUserId { get; set; }
}
