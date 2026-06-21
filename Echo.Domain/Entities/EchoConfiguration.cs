using Persistence;

namespace Echo.Domain.Entities;

public class EchoConfiguration : BaseEntity<EchoConfiguration>, IPrefixedEntity
{
    public static string Prefix { get; } = "ecco";
    
    public bool IsRegisterEnabled { get; set; }
    public bool IsLoginEnabled { get; set; }



    public int EnforcedSingleton { get; set; } = 1;
}