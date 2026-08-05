using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Identity.Domain.Entities;

/// <summary>
/// One game a user does not want broadcast, even while <c>ShareActivity</c> is on.
/// </summary>
public class UserHiddenActivity : BaseEntity<UserHiddenActivity>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "uhac";

    public string UserId { get; set; } = null!;

    /// <summary>The application id to suppress. Null when this row keys on <see cref="Name"/>.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>
    /// The activity name to suppress, for sources that cannot produce an application id.
    /// </summary>
    public string? Name { get; set; }
}
