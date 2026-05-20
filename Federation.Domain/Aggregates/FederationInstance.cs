using Federation.Domain.Events;
using Persistence;

namespace Federation.Domain.Aggregates;

public class FederationInstance : BaseEntity<FederationInstance>, IPrefixedEntity
{
    public static string Prefix { get; } = "fein";
    public required string Host { get; set; }
    public required string Name { get; set; }
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public FederationStatus Status { get; set; } = FederationStatus.Active;
    public string? DefederationReason { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    
    public ICollection<FederatedGuild> FederatedGuilds { get; set; } = new List<FederatedGuild>();
}