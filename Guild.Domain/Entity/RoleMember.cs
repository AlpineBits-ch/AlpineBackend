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

    /// <summary>
    /// Time-boxed role membership (the GuestAccess module): the pet sitter holds "Guest" for five
    /// days and it lapses on its own.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
