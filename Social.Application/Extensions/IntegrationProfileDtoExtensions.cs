using AppEnvironment;
using Social.Contracts.Dtos;
using Social.Domain.Aggregate;

namespace Social.Api.Extensions;

public static class IntegrationProfileDtoExtensions
{
    public static ProfileDto ToIntegrationProfile(this Profile profileDto)
    {
        var profile = new ProfileDto()
        {
            Id = profileDto.Id,
            UserName = profileDto.UserName,
            Bio = profileDto.Bio,
            // Blocked rows are deliberately excluded (privacy spec T0-3).
            Relationships = profileDto.Relationships
                .Where(r => r.Status != Domain.Enums.RelationshipStatus.Blocked)
                .Select(r => r.ToIntegrationRelationship())
                .ToList(),
            UserId = profileDto.UserId,
            AvatarUrl = $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/social/profiles/{profileDto.Id}/avatar",
            BannerUrl = $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/social/profiles/{profileDto.Id}/banner",
            AccentColor = profileDto.AccentColor,
            Font = profileDto.Font.ToString(),
        };

        return profile;
    }

    /// <summary>
    /// The minimal public projection a blocked reader gets over the bus (privacy spec T0-3),
    /// mirroring <c>ProfileVisibility.Minimal</c> on the REST side: identity and media URLs, and
    /// nothing the blocker chose to withhold - notably not the bio and not the friend list.
    /// </summary>
    public static ProfileDto ToMinimalIntegrationProfile(this ProfileDto profile) => new()
    {
        Id = profile.Id,
        UserId = profile.UserId,
        UserName = profile.UserName,
        AvatarUrl = profile.AvatarUrl,
        BannerUrl = profile.BannerUrl,
        Bio = null,
        AccentColor = null,
        Font = Domain.Enums.ProfileFont.Default.ToString(),
        Hash = profile.Hash,
        Relationships = [],
    };

    public static RelationshipDto ToIntegrationRelationship(this Relationship relationshipDto)
    {
        return new RelationshipDto()
        {
            Id = relationshipDto.Id,
            ProfileId = relationshipDto.TargetId,
            Status = relationshipDto.Status.ToIntegrationEnum(),
            UserId = relationshipDto.Target.UserId
        };
    }

    public static RelationshipStatus ToIntegrationEnum(this Domain.Enums.RelationshipStatus status)
    {
        switch (status)
        {
            case Domain.Enums.RelationshipStatus.None:
                return RelationshipStatus.None;
            case Domain.Enums.RelationshipStatus.PendingIncoming:
                return RelationshipStatus.Pending;
            case Domain.Enums.RelationshipStatus.PendingOutgoing:
                return RelationshipStatus.Pending;
            case Domain.Enums.RelationshipStatus.Friends:
                return RelationshipStatus.Accepted;
            case Domain.Enums.RelationshipStatus.Blocked:
                return RelationshipStatus.Blocked;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }
}
