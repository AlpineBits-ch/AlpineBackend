using Persistence;

namespace Identity.Domain.Entities;

public class UserPublicKey : BaseEntity<UserPublicKey>, IPrefixedEntity
{
    public string UserId { get; set; }
    public byte[] PublicKey { get; set; }
    public static string Prefix { get; } = "uspk";
}