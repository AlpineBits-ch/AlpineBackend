using Persistence;

namespace Echo.Entities;

public class EchoConfiguration : BaseEntity<EchoConfiguration>, IPrefixedEntity
{
    public static string Prefix { get; } = "ecco";
    
    public bool IsRegisterEnabled { get; set; }
    public bool IsLoginEnabled { get; set; }
}