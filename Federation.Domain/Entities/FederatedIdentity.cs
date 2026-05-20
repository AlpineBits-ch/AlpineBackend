using Persistence;

namespace Federation.Domain.Entities;

public class FederatedIdentity : BaseEntity<FederatedIdentity>, IPrefixedEntity
{
    public static string Prefix { get; } = "feid";
    public string DisplayName { get; set; }
    public int? Hash { get; set; }
    public string? AvatarUrl { get; set; }
}