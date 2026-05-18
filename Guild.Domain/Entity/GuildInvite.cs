using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateGuildInviteParams
{
    public string GuildId { get; set; }
    public InviteType Type { get; set; }
}

public class GuildInvite : BaseEntity<GuildInvite>, IPrefixedEntity
{
    public static string Prefix { get; } = "chiv";
    public string GuildId { get; set; }
    public Aggregates.Guild Guild { get; set; }
    
    public InviteType Type { get; set; }
    public InviteState State { get; set; }
    
    public ICollection<GuildMember> Members { get; set; } = new List<GuildMember>();
    
    public static GuildInvite Create(CreateGuildInviteParams parameters)
    {
        return new GuildInvite
        {
            GuildId = parameters.GuildId,
            Type = parameters.Type,
            State = InviteState.Active
        };
    }
}