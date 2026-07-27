namespace Guild.Contracts.Bus.Request;

/// <summary>Guilds a user is a member of AND holds ManageGuild in - i.e. guilds they could
/// actually install a bot into. Backs the bot portal's guild picker so installing a bot doesn't
/// require knowing/typing a raw guild id.</summary>
public class ListManageableGuildsForUserRequest
{
    public string UserId { get; set; }
}
