using Persistence;

namespace Discovery.Domain.Entities;

public class InterestVisibility : BaseEntity<InterestVisibility>, IPrefixedEntity
{
    public static string Prefix { get; } = "invs";

    public string UserId { get; set; } = null!;
    public bool Visible { get; set; } = true;
}
