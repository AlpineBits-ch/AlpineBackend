using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// Stores the public key of a user for a guild or channel and should be removed once the secrets are exchanged
/// </summary>
public class PublicKeyStore : BaseEntity<PublicKeyStore>, IPrefixedEntity{
    public byte[] Key { get; set; }
    public string UserId { get; set; }
    public static string Prefix { get; } = "pks";
    
    public string? ChannelId { get; init; }
    public string? GuildId { get; init; }
}