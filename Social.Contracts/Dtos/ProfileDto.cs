namespace Social.Contracts.Dtos;

public class ProfileDto
{
    public string Id { get; set; }
    public string UserName { get; set; }
    public string? Bio { get; set; }
    public int Hash { get; set; }
    
    public string UserId { get; init; }

    public ICollection<RelationshipDto> Relationships { get; set; } = [];

    public static string GetCacheIdByUserId(string id)
    {
        return $"profile:user:{id}";
    }
}