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
            Hash = profileDto.Hash,
            Relationships = profileDto.Relationships.Select(r => r.ToIntegrationRelationship()).ToList(),
            UserId = profileDto.UserId,
        };

        return profile;
    }
    
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