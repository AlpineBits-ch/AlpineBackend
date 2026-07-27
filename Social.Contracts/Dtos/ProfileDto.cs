namespace Social.Contracts.Dtos;

public class ProfileDto
{
    public string Id { get; set; }
    public string UserName { get; set; }
    public string? Bio { get; set; }
    public int Hash { get; set; }

    public string UserId { get; init; }

    public string AvatarUrl { get; set; }
    public string BannerUrl { get; set; }
    public string? AccentColor { get; set; }

    // String form of Social.Domain.Enums.ProfileFont, matching the existing convention of
    // passing enums as plain strings across the bus/contract boundary (see UserStatusChanged).
    public string Font { get; set; }

    public ICollection<RelationshipDto> Relationships { get; set; } = [];

    public static string GetCacheIdByUserId(string id)
    {
        return $"profile:user:{id}";
    }
}