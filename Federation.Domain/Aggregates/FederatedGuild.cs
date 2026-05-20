using Persistence;

namespace Federation.Domain.Aggregates;

public class FederatedGuild : BaseEntity<FederatedGuild>, IPrefixedEntity
{
    public static string Prefix { get; } = "fegd";
    public string RemoteId { get; set; }
    public string FederatedGuildId { get; set; }
    public virtual FederationInstance Instance { get; set; }
}