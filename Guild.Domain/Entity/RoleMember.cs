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

    /// <summary>Time-boxed role membership (the GuestAccess module): the pet sitter holds "Guest"
    /// for five days and it lapses on its own. Null - the overwhelmingly common case - means the
    /// role is held indefinitely.
    ///
    /// Enforced on read in GuildPermissionService.GetMembershipAsync rather than by deleting the
    /// row on a timer, so an expiry is never dependent on a sweep having run. The row is tidied up
    /// later by HouseholdReconcileService; until then it is inert.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
