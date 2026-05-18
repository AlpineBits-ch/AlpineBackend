using Persistence;
using Social.Domain.Enums;

namespace Social.Domain.Aggregate;

public  class CreateProfileParams
{
    public string UserId { get; init; }
    public string Username { get; init; }
    public string? Bio { get; init; }
}

public class Profile : BaseEntity<Profile>, IPrefixedEntity
{
    public static string Prefix { get; } = "prfl";
    
    public string? Bio { get; set; }
    public string UserName { get; set; }
    public int Hash { get; private set; }
    
    public virtual ICollection<Relationship> Relationships { get; set; } = new List<Relationship>();
    
    public virtual ICollection<Relationship> InvolvedRelationships { get; set; } = new List<Relationship>();
    
    public string UserId { get; init; }
    
    public OnlineStatus OnlineStatus { get; set; } = OnlineStatus.Offline;
    
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public static Profile Create(CreateProfileParams @params)
    {
        var id = GenerateId();

        var profile = new Profile()
        {
            Id = id,
            UserId = @params.UserId,
            UserName = @params.Username,
            Hash = new Random((int)DateTime.UtcNow.Ticks).Next(1111, 9999),
        };
        
        return profile;
    }

    public static string GetCacheKey(string id)
    {
        return $"profile:{id}";
    }
    public string GetCacheKey()
    {
        return $"profile:{Id}";
    }
 
}