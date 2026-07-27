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
    
    public string? FederatedServerId { get; set; }

    // Hex color (e.g. "#5865F2"), used client-side as the banner fallback color and profile
    // accent trim when no BannerUrl image is set. Validated at the API boundary, not here.
    public string? AccentColor { get; set; }

    public ProfileFont Font { get; set; } = ProfileFont.Default;

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