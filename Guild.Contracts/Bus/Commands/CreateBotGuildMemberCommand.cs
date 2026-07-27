namespace Guild.Contracts.Bus.Commands;

public class CreateBotGuildMemberCommand
{
    public string GuildId { get; set; }
    public string BotUserId { get; set; }
    public string BotDisplayName { get; set; }
    public string InstalledByUserId { get; set; }
    public ulong GrantedPermissions { get; set; }
}

public class CreateBotGuildMemberResponse
{
    public string GuildMemberId { get; set; }
}
