using Persistence;

namespace Identity.Domain.Entities;


/// <summary>This backs up all user keys securely (encrypted by the master key)</summary>
public class UserKey : BaseEntity<UserKey>, IPrefixedEntity
{
    public string UserId { get; set; }
    public byte[] Key { get; set; }
    public string KeyId { get; set; }
    public byte[]? PublicKey { get; set; } = Array.Empty<byte>();
    
    public static string Prefix { get; } = "uskr";
    
    public override string ToString()
    {
        return $"{Prefix}:{KeyId}";
    }
}