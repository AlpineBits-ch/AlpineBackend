using Guild.Domain.Aggregates;
using Persistence;

namespace Guild.Domain.Entity;

public class RoleMember : BaseEntity<RoleMember>, IPrefixedEntity
{
    public static string Prefix { get; } = "rome";
    public virtual Role Role { get; set; } = null!;
    public string RoleId { get; set; }
    public string MemberId { get; set; }
    public virtual GuildMember Member { get; set; } = null!;
}
