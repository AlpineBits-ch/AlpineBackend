using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Events.Relationship;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Events;

public class FriendshipAcceptedHandler
{
    public static FriendshipAcceptedEvent Handle(FriendRequestAccepted friendRequest, MicroserviceContext ctx)
    {
        var relationship = ctx.Relationships.Include(relationship => relationship.Target)
            .Include(relationship => relationship.Owner).FirstOrDefault(r => r.Id == friendRequest.RelationshipId);
        if(relationship is null) throw new Exception("Relationship not found");
        var friendshipAcceptedEvent = new FriendshipAcceptedEvent
        {
            AcceptantUserId = relationship.Owner.UserId,
            InitiatorUserId = relationship.Target.UserId,
            InitiatorUserName = relationship.Target.UserName,
            AcceptantUserName = relationship.Owner.UserName,
            FriendshipId = relationship.Id,
        };

        return friendshipAcceptedEvent;
    }
}